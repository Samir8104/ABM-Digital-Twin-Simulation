# Recorded first-floor airflow

`flow_field.npz` is imported directly by Unity into a binary `RecordedAirflow` asset. No Python or external file access is required in a player build. Replacing the NPZ triggers a reimport.

## Use

- `Assets/McEinryScene.unity`: the existing `airflowManager` now uses the recording. Its transform starts at the source origin, with unit scale.
- Alternatively drag `Assets/Prefabs/FirstFloorAirflow.prefab` into your scene. Remove the old airflow manager first; use one loader per scene.
- Enable Scene gizmos to see the volume even when it is not selected: cyan floor, yellow ceiling, white vertical edges and corner markers, visible through the building. Select the manager/prefab and click **Frame Airflow Bounds in Scene View** in its Inspector to locate it. Toggle **Show Alignment Bounds** to hide the guide. Move, rotate, and scale it to align with the first floor. The data alone does not establish registration against the building model, so visually confirm alignment. Its local bounds are X 1.358–70.658, Y approximately 0–3.048, Z 7.986–76.266 metres. The root is the solver origin, not the bounds centre.
- Enter Play Mode. Airflow applies to all scene ParticleSystems, including ambient and respiratory systems and systems created later. Press **V** to show/hide flowing wisps. They start hidden; toggling does not turn off particle advection.
- Blue = zero/low speed, green = half the dataset's maximum, red = maximum. The maximum is measured from this recording (about 3.438 m/s), not copied from the reference image's numbers.

## Data and motion

The file has 42,981 scattered spatial nodes and 101 frames from 0 to 50 seconds, at 0.5-second intervals. Positions and velocities are remapped from solver `(x,y,z-up)` to Unity `(x,y-up,z)`. Spatial samples use inverse-distance weighting of four nearest nodes, found with a k-d tree. Time is interpolated linearly. The last frame is held after 50 seconds by default; optional looping restarts the recording.

Outside the first-floor bounds, or farther than `supportRadius` (default 0.75 source metres) from a sample, the field is zero. Particles retain their droplet settling/evaporation behavior. No upper-floor airflow is fabricated. The NPZ contains no cell connectivity or wall mesh: interpolation approximates the flow between measured nodes and does not itself implement wall collisions. Existing ParticleSystem collisions remain responsible for geometry interaction.

Transform scaling maps both distances and velocities, keeping travel times consistent. For physical metre-per-second values, leave scale at one. `playbackSpeed` changes the timing of recorded frames, not air velocity. Wisps use midpoint integration of the same field and the same scaled game clock as normal particles.

## Validation

`Tools > Airflow > Validate recorded field` checks dimensions, axes, units, exact node values, interpolation in time, southward flow, bounds, and unsupported mesh gaps. Unity 6000.3.9f1 batch validation passed. A deterministic isolated Unity ParticleSystem simulation test passed 60 motion steps in world/local/custom coordinate spaces, late particle-system registration, and buffer resizing. Full scene visual alignment and appearance still need an in-editor check.

The prefab and scene explicitly reference the wisp material so its shader is included in builds. The wisp color shader uses vertex colors, transparency, and scene depth testing.

## Agent wakes and breathing

Agent prefabs now add their local airflow to the recorded room flow before aerosol drag is evaluated. This makes both ambient particles and exhaled droplets respond. The V-key wisps sample the combined flow as well.

- Walking carries nearby air in the agent's actual travel direction, with a stronger region just behind the body and smooth decay when stopping. Warps do not produce a gust. Influence is local and limited vertically.
- Breathing alternates between exhaling and inhaling (four-second cycle at rest, with randomized starting phases). Exhalation creates a spreading forward jet and emits particles only during that phase. Inhalation draws nearby particles toward the mouth and emits none. Walking increases breathing frequency and strength.
- Talking uses the same inhale/exhale cycle with its stronger emission profile. Coughs and sneezes briefly override the cycle with a stronger outward jet, then resume breathing.
- Existing health/infection particle counters now capture only during inhalation. Particle ownership and infection colors are preserved. The existing health system still determines which particles are consumed; the new local flow does not independently remove/count them.

Tune `AgentBodyWake` on an agent's root: **Strength Multiplier**, **Wake Radius**, **Smoothing** (seconds), **Exhale Reach** (default 1.2 m), and **Inhale Reach** (default 0.55 m). Tune **Breath Period**, **Exhale Fraction**, and **Inhale Speed** on its mouth's `AgentBehaviorController`. Distances are world metres and do not shrink with the mouth object's visual scale. Local agent effects also work outside the recorded first-floor volume; only the recorded building airflow remains confined to that volume.

This is an approximate visual interaction model, not a calibrated respiratory fluid solver. Local wakes/jets have distance falloff but do not raycast through the building's wall geometry. ParticleSystem collision settings continue to handle solid surfaces.

`AgentInteractionValidation.Run` provides isolated batch-editor checks for direction, decay, teleport/pause handling, breathing movement of real ParticleSystems in local/world/custom spaces, ambient tracers, breath/emission phase synchronization, health capture gating, and cough recovery. It then reruns the recorded-field motion checks.
