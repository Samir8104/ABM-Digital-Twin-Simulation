# Background runs and recording

## Particle loading and 200-agent runs

Regular and filler agents now have a maximum of **31 live mouth particles each**
(halved from 62). Particles stay stopped on the menu and during agent loading,
then resume when the simulation day begins. Students who are idle/off campus do
not emit or contribute body wakes; previously emitted world-space aerosols can
continue to move. Idle/off-campus students also stop animating.

Agents are instantiated at the sampled NavMesh spawn position, rather than at
an off-mesh staging root before being warped. The latter produced thousands of
navigation errors across recent editor runs. Shared clock/elevator references
are cached, and new particle systems register their airflow driver on creation
instead of requiring a whole-scene scan every half second.

Recorded airflow uses a bounded spatial tree and a Burst-compiled, batched search.
Each particle still gets its own exact four nearest neighbours and the existing
spatial/temporal weights. A managed fallback remains available when Burst is
disabled. Persistent native search buffers are disposed when the asset unloads.
Decorative airflow wisps have a bounded number of integration steps per frame,
so a slow frame cannot trigger unlimited catch-up work.

`ParticlePerformanceValidation.Run` runs the existing regressions, then enters
Play mode in an isolated project copy with 200 agents, no fillers, enabled
particles, and cameras/canvases disabled. Run without `-quit`; the fixture exits
the editor itself. It checks valid spawn positions, zero particles during
loading, clock advancement, and fills all systems to 6,200 live particles.
The final check started the day in about 5.7 seconds, continued for eight seconds
at the full cap, and logged no simulation errors. The particle-physics marker
averaged about 28 ms per observed editor update, compared with about 106 ms in
the earlier stress check. These are isolated CPU/startup measurements, not
rendered FPS or a guarantee of performance on another machine. The airflow
regressions also compare 64 single and batched queries with a brute-force
four-neighbour reference, in addition to the existing particle/airflow, capture,
scheduler, CSV, and heatmap checks.

## Background mode

On the scene's `SimulationFlowController`, enable **Run Without Rendering**
before entering Play mode. It loads the default agent/infection/disease settings
automatically, disables cameras and canvases, and skips decorative airflow wisps.
The simulation continues when the application loses focus. Unity's Scene view
can still draw in the editor; use a standalone build to remove graphics entirely.

Build the simulation scene (`Assets/McEinryScene.unity`) as a Windows standalone
player. The global build scene list now selects McEinryScene. If your active Build
Profile overrides the global scene list, select McEinryScene in that profile too.
Launch the resulting executable from PowerShell:

```powershell
& 'C:\path\to\Simulation.exe' -batchmode -nographics -logFile 'C:\path\to\simulation.log'
```

The player automatically starts with the flow controller's defaults. These flags
skip Unity's graphics device. `--sim-no-render` can also auto-start a normal
player and disable its cameras/UI, but does not remove its graphics device.
The background mode targets 30 frames/second to limit CPU use; it still needs
enough CPU time to advance the simulation. Stop an editor run with Play, or stop
the standalone process when finished. There is no automatic end-of-run cutoff.
Animation remains enabled because mouth transforms may depend on animated bones.
When **Particle simulation** is enabled, particle systems remain active and use AlwaysSimulate culling: particle motion,
emissions, settling, evaporation, wakes, and inhalation still run without cameras.
The main-menu particle switch can stop aerosol processing for faster occupancy
runs. Its saved setting also applies in background mode; the heatmap population
CSV records whether particles were enabled. See [HEATMAP.md](HEATMAP.md) for the
H/T controls, floor views and persistent occupancy files.

## CSV output

**Record Csv** is enabled by default on `SimulationFlowController`, for both
rendered and background runs. **Recording Interval Seconds** defaults to 1 real
second, independent of the simulation clock multiplier. Increase it to reduce
file size and I/O. Each start creates its own UTC-stamped, uniquely named folder:

```text
%USERPROFILE%\AppData\LocalLow\DefaultCompany\ABM Digital Twin Simulation\SimulationRuns\<run>
```

The recorder logs the actual output directory at startup. Unity resolves it via
`Application.persistentDataPath`, so the location changes if company/product
names or platform change.

| File | Recorded values |
| --- | --- |
| `positions.csv` | World-space x, y, z |
| `health.csv` | Infection flag, cumulative inhaled count, cumulative infected-particle inhaled count |
| `activity.csv` | Schedule activity, or Unknown for agents without a NavigationAgent schedule |

All files share `sample_id,elapsed_seconds,sim_day,sim_minute,agent_id,present`.
Join using `(sample_id,agent_id)` within a run. `elapsed_seconds` is real time
since recording began; `sim_minute` is the current minute of the simulated day.
Each pooled spawn gets a new run-local ID, so a replacement student's reset
counts cannot be confused with the previous student's data. `present=0` marks
a registered agent whose body collider is disabled (for example, off-campus).
Disabled/destroyed agents no longer produce rows; inhalation counts are snapshots,
not individual event records. Filler agents with AgentHealth are included.

Writes are buffered, flushed every five seconds and closed on a normal stop/quit.
Force termination can lose the last buffered rows. Sampling records actual
snapshots and does not invent missed samples after a slow frame. Separate files
make individual properties easier to inspect; they do not reduce the total data
volume. The infection flag records the current AgentHealth state; this change
does not introduce a new disease-transmission model.

## Optimizations and verification

- Capture spheres are indexed in their overlapping grid cells, reducing each
  particle's receiver lookup from 27 dictionary queries to one. Exact radius and
  nearest-receiver checks still decide capture.
- Recorded airflow keeps its exact four-neighbour spatial interpolation and
  temporal interpolation. Shared time-frame lookup is cached and duplicate
  world-to-field position conversion is removed.
- Particle transforms, ambient jitter scale and buoyancy are calculated once per
  system update. Walking-wake coefficients are calculated once per source pose.
- The scheduler reuses countdown buckets and scratch collections instead of
  copying dictionaries and filtering lists every tick. Frequencies remain the
  same: movement every tick, arrival checks every two ticks, schedule checks every
  30 ticks. They remain frame-based, so reducing tick frequency can alter timing.
- Cancelling a navigation stepper before its registration now removes the actual
  callback instead of only clearing its liveness flag. Recreating it cannot
  revive old callbacks. Scheduler removal checks use a hash set when many agents
  change state together.
- Verbose navigation logs, global nearby-agent diagnostic scans and bathroom
  stack traces are disabled by default. Enable Verbose Logging when debugging.
- Clock text is updated when the displayed time changes.

Profiler markers `Simulation.Scheduler`, `Simulation.ParticlePhysics`, and
`Simulation.Inhalation` expose the major CPU paths. Compare equivalent runs
(same scene, counts, speed, and wisp visibility) in a development player. A 30 FPS
target leaves about 33 ms per frame; full-scene FPS must be measured on the target
machine, separately from isolated regression checks or scheduler benchmarks.

`Assets/Editor/SimulationPerformanceValidation.cs` provides isolated batch-editor
checks for stepper cadence/priority/removal, capture-grid equivalence against a
brute-force search, CSV schemas and pooled IDs. It also runs the existing recorded
airflow and particle-motion fixtures. Run in a separate project copy, with the
custom scripts, framework dependencies, importer and flow_field.npz, because the
fixtures create temporary agents and particle systems.

The isolated Unity 6000.3.9f1 validation passed the scheduler tests (including
100 cancelled/recreated navigation steppers), 2,000 capture-grid comparisons,
CSV output checks, and the existing airflow/breathing/particle-motion fixtures.
The final isolated benchmark with 3,000 steppers over 1,000 ticks measured about
92 ms before and 74 ms after the scheduler changes (about 20% less scheduler time).
This benchmark excludes real agent/particle callbacks and rendering; it is not
a full-scene FPS measurement. Script compilation also passed with editor defines
and references removed after guarding existing example/debug editor APIs, which
previously prevented standalone compilation. A full player build was not made.
