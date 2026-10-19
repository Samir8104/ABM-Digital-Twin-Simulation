# Quantitative heatmap and saved viewer

Run `Assets/McEinryScene.unity` with **Run Without Rendering** off to use the visual controls. Background runs still collect and export heatmap data.

| Control | Action |
| --- | --- |
| H | Show/hide the heatmap and its numerical legend. Recording continues while hidden. |
| T | Enter/leave the top-down floor view. The original camera is restored on exit. |
| Page Up / Page Down, or ] / [ | Select a floor in top-down view. |
| Change measure | Cycle through the five measurements below. |
| Red from | Set the numerical red threshold. All floors use the same linear scale. |
| Export files | Save run data, the offline viewer, and numerical TIFFs for every floor and measurement. |
| Open saved view | Save current data and open its standalone HTML viewer in a browser. |

Keyboard shortcuts are ignored while typing in an input field. The legend uses the simulation's existing font, button sprites and canvas scaling. It is anchored at the top right so the time, speed and agent information remain on the left.

## Measurements and colours

The default grid is one metre by one metre, separated by floor. Blue means zero, cyan/yellow intermediate values, and red means at least the displayed threshold. Values above the threshold remain unchanged in the exported data. The scale does not automatically change as more people move through the building. Compare runs using the same measurement, cell size, threshold and time window.

| Measurement | Definition | Default red threshold |
| --- | --- | --- |
| Mean people per cell | Integrated simulated person-seconds divided by active simulated seconds | 1 mean person/cell |
| Walking entries / simulated hour / cell | Continuous walking cell entries divided by active simulated hours | 60 entries/hour/cell |
| People per cell (last snapshot) | People present in the latest occupancy snapshot | 5 people/cell |
| Total cell entries | All entries, including initial arrivals and relocations | 100 entries/cell |
| Person-hours per cell | Integrated simulated person-seconds divided by 3600 | 1 person-hour/cell |

Mean occupancy is useful for areas where people remain, such as classrooms. Walking entries highlight routes. Walking entries include repeat visits and are **not unique people or directional doorway counts**. Person-hours describe cumulative presence. The current-occupancy measurement is a snapshot, not a continuous live census; snapshots default to once per real second.

Before any simulated time has been observed, mean occupancy and hourly rates are unknown (gray in the sim and NoData in exports), rather than a fabricated zero. In the saved viewer, checkerboard indicates unmapped locations or unavailable observations. NavMesh-covered cells with no visits are zero; areas outside coverage remain unknown. Recorded off-NavMesh cells are exported with a flag but have no walkable mesh to colour in the sim.

## Counting and time basis

Each present `AgentHealth` root is counted once, independently of collider count. Main and filler agents are included. The model's hidden Idle, OffCampus and Done students are absent. Pooled replacement students receive fresh visitor IDs. Floor numbers are one-based, sorted by height; floor heights normally come from the elevator configuration.

Every active frame, the tracker partitions each observed straight movement segment across the X/Z grid and apportions its simulated duration to the crossed cells. Standing still adds person-time without repeated walking entries. This retains short hallway passages between CSV snapshots. It cannot reconstruct turns made entirely within a stalled frame.

The denominator is **active simulated time**, using the clock's actual applied time step. Pauses and skipped overnight gaps are excluded. Changing simulation speed therefore changes the time actually observed instead of mixing real seconds with simulated hours. This is a run-to-date rate over the active observation window, not a rolling clock-hour rate.

Floor changes and discontinuous jumps do not create trails through unrelated rooms. Their person-time is excluded and reported as `excludedTransitionPersonSeconds`, rather than allocated to a guessed path. First appearances and disappearances between frames cannot be reconstructed. Floor selection uses the nearest configured floor height, so elevator/stair transition occupancy is an approximation. `maximumTrackedSpeed` defaults to 40 metres per real second, with a minimum one-cell displacement allowance.

The conserved path integrals validate accounting within the simulation. They do not establish real-world predictive accuracy. Once the people sensors are chosen, match their floor, covered area, time window, units and counting convention before comparison. A doorway counter and an occupancy sensor need different comparisons. Sensor coordinates must be registered to the model, and observed data are needed to calibrate schedules, population and movement. Do not sum cell entries or cell visitor counts to estimate unique building attendance.

## Saved data and TIFFs

Recording starts after agents finish loading, even if **Record Csv** is disabled. Files share the position/health run directory when enabled; otherwise a fresh directory is created under `Application.persistentDataPath/SimulationRuns`. The Console prints the exact path. On the default Windows project settings this is beneath:

```text
%USERPROFILE%\AppData\LocalLow\DefaultCompany\ABM Digital Twin Simulation\SimulationRuns\<run ID>\
```

| File | Contents |
| --- | --- |
| `heatmap_run.json` | Latest saved numerical grid, NavMesh coverage, floor heights, time basis, census, chosen measure and threshold. |
| `heatmap-viewer.html` | Self-contained viewer with embedded run data. Open by double-clicking; no Unity, server or network dependency is required. |
| `floor-N-Measure.tif` | Single-band, uncompressed Float32 numerical raster, one per floor and measurement. |
| Matching `.tfw` | Local-coordinate pixel size and origin for the TIFF. Keep beside its TIFF. |
| `heatmap_cells.csv` | Snapshot history of cell occupancy, entries, visitors and person-time. |
| `heatmap_population.csv` | Snapshot census, floor totals, main/filler split and particle setting. |

JSON and HTML checkpoint every 30 real seconds by default. Explicit export and normal stop also write all five TIFF measurements for every floor. CSV buffers flush every five real seconds and on normal pause/stop. A forced process termination can lose the latest buffered rows/checkpoint interval. Repeated export updates the existing files in the run directory. Copy an export elsewhere to preserve an intermediate view. Exports may briefly pause large runs because serialization and TIFF writing run synchronously.

The offline viewer provides floor/measurement selection, zoom, fixed numerical scale, cell values on hover, and TIFF/world-file/JSON downloads. It can open another run JSON or `heatmap_cells.csv`, including older 15-column CSVs. Legacy CSVs retain recorded values but cannot recover missing simulated person-time or NavMesh coverage. Unrecorded cells remain unknown. Importing a CSV uses its latest saved snapshot; the CSV history remains available for other analysis tools. Viewing saved data does not resume a simulation.

TIFF pixels store the actual selected values, independent of colour thresholds. `-9999` is NoData. Increasing world Z points toward the top row. X/Z coordinates and cell sizes are local Unity metres. TIFF plus world file supports raster placement in those local coordinates; these files do **not** claim a surveyed map CRS or EPSG code. Real-world GIS alignment needs surveyed registration. See [GDAL TIFF documentation](https://gdal.org/en/stable/drivers/raster/gtiff.html) and [world-file conventions](https://gdal.org/en/stable/drivers/raster/wld.html).

## CSV compatibility and population checks

The first 15 cell columns and 13 population columns retain their original order. Cell rows append `walking_entries,simulated_seconds,simulated_person_seconds,mean_people,walking_entries_per_sim_hour`; population rows append `simulated_seconds`. `person_seconds` remains the legacy real-time snapshot estimate; quantitative occupancy uses `simulated_person_seconds` instead. Counters are cumulative; `occupants` is the current snapshot. Grid indices use floor(x/cell_size) and floor(z/cell_size), including negative coordinates.

Within one snapshot, summed cell occupants and summed `present_on_floor` must each equal the global `present` count. `main_present + filler_present = present` and `registered = present + off_campus`. Global population totals repeat on each floor row; do not sum those repeated totals. Expected roster means the configured main roster, not simultaneous attendance. The final snapshot preserves the last observed state even if Unity disables agent objects before the heatmap during shutdown.

## Validation and performance

`HeatmapAccuracyValidation.Run` checks conserved segment person-time, negative/diagonal cells, speed changes, pauses, hourly normalization, discontinuity exclusions, narrow-corridor coverage, shared scales, TIFF/world-file orientation, zero versus NoData, JSON/viewer exports, CSV persistence and stop-time census. It also invokes the existing heatmap, airflow, particle and scheduler regression suites. Run in an isolated batch-editor project copy.

`HeatmapValidation.RenderModelFloors` renders the actual model's four floors, legend and menu using synthetic walks along actual NavMesh routes. These images are visual fixtures, not real sensor measurements. Editor and standalone compilation, numerical/regression checks and model renders were verified on Unity 6000.3.9f1. TIFF numerical values were independently opened with Pillow, and browser checks cover saved viewing, fixed scales and legacy CSV import.

Movement tracking uses active agent roots and grid traversal; overlays are constructed only when shown. Texture updates are capped at ten per real second and reuse pixel buffers. Snapshot writing and export cadence are separate from movement accounting. Hidden and background runs continue recording without building overlay meshes.
