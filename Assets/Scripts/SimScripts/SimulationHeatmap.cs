using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using TMPro;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

[DefaultExecutionOrder(250)]
public sealed class SimulationHeatmap : MonoBehaviour
{
    [Min(0.25f)] public float cellSize = 1f;
    [Min(0.1f)] public float sampleIntervalSeconds = 1f;
    [Tooltip("Position jumps faster than this are teleports, not hallway passages.")]
    [Min(1f)] public float maximumTrackedSpeed = 40f;
    [Tooltip("Optional override. Otherwise use the scene's elevator floor heights.")]
    public float[] floorHeights;
    [Header("Readable scale and saved exports")]
    public string buildingName = "McEniry";
    public HeatmapMeasure measure = HeatmapMeasure.WalkingEntriesPerHour;
    [Min(0.001f)] public float scaleMaximum = 60f;
    [Min(5f)] public float checkpointIntervalSeconds = 30f;
    public bool exportTiffsOnStop = true;
    public HeatmapGrid Grid { get; private set; }
    public bool Visible { get; private set; }
    public string OutputDirectory { get; private set; }
    public int PresentCount { get; private set; }
    public int RegisteredCount { get; private set; }
    public int FillerCount { get; private set; }
    public FloorHeatmapView FloorView { get; private set; }
    public bool IsRunning => clock != null && clock.IsRunning;

    private TimeManager clock;
    private NavigationController navigation;
    private float startTime, nextSample, nextFlush, activeSampleSeconds, nextTextureRefresh;
    private bool recording;
    private StreamWriter cellsWriter, censusWriter;
    private sealed class PersonTrace
    {
        public int generation, id, lastFrame = -1;
        public bool filler, present;
        public Vector3 position;
    }
    private readonly Dictionary<AgentHealth, PersonTrace> identities = new();
    private int nextPersonId, trackingFrame;
    private int[] floorCounts;
    private readonly List<GameObject> surfaces = new();
    private readonly List<Texture2D> textures = new();
    private readonly List<Material> materials = new();
    private readonly List<Mesh> meshes = new();
    private readonly List<Color[]> pixelBuffers = new();
    private int minX, minZ, width, depth;
    private Gradient gradient;
    private bool floorSlice;
    private int visibleFloor;
    private long displayedRevision = -1;
    private long displayedStatisticsRevision = -1;
    private HeatmapCoverage coverage;
    private HeatmapLegendUI legend;
    private double lastSimulationSeconds;
    private float nextCheckpoint;
    private string recordedUtc;
    private int startDay, startMinute;
    private int trackedRegisteredCount, lastObservedDay, lastObservedMinute;
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;
    private static readonly Unity.Profiling.ProfilerMarker SampleMarker = new("Simulation.HeatmapSample");

    public void Initialize(TimeManager timeManager)
    {
        clock = timeManager;
        navigation = FindFirstObjectByType<NavigationController>();
        // The example room prefabs in this model have no volume meshes. The
        // navigation surface supplies actual geometry instead of those bounds.
        foreach (var room in FindObjectsByType<RoomScript>(FindObjectsSortMode.None)) room.enabled = false;
        gradient = new Gradient();
        gradient.SetKeys(new[] { new GradientColorKey(Color.blue, 0), new GradientColorKey(Color.cyan, 0.33f),
            new GradientColorKey(Color.yellow, 0.66f), new GradientColorKey(Color.red, 1) },
            new[] { new GradientAlphaKey(0.65f, 0), new GradientAlphaKey(0.65f, 1) });
        if (navigation != null) navigation.heatmapGradient = gradient;
        if (floorHeights == null || floorHeights.Length == 0)
        {
            var elevator = FindFirstObjectByType<ElevatorController>();
            floorHeights = elevator != null && elevator.floorYPositions != null && elevator.floorYPositions.Length > 0
                ? elevator.floorYPositions : DetectFloorHeights();
        }
        Grid = new HeatmapGrid(floorHeights, cellSize);
        floorCounts = new int[Grid.FloorHeights.Length];
        FloorView = GetComponent<FloorHeatmapView>();
        if (FloorView == null) FloorView = gameObject.AddComponent<FloorHeatmapView>();
        FloorView.Initialize(this);
    }

    private float[] DetectFloorHeights()
    {
        var heights = new List<float>();
        if (navigation != null && navigation.rooms != null)
            foreach (var room in navigation.rooms)
            {
                if (room == null) continue;
                float y = room.transform.position.y;
                if (!heights.Exists(h => Mathf.Abs(h - y) < 2f)) heights.Add(y);
            }
        if (heights.Count == 0) heights.Add(0);
        heights.Sort();
        return heights.ToArray();
    }

    public void Begin(string runDirectory = null)
    {
        if (Grid == null) Initialize(clock != null ? clock : FindFirstObjectByType<TimeManager>());
        Close();
        Grid = new HeatmapGrid(Grid.FloorHeights, cellSize);
        coverage = HeatmapCoverage.Capture(Grid);
        identities.Clear(); nextPersonId = trackingFrame = 0;
        lastSimulationSeconds = clock != null ? clock.ElapsedSimulationSeconds : 0;
        recordedUtc = DateTime.UtcNow.ToString("O", Culture);
        startDay = clock != null ? clock.CurrentDay : 1;
        startMinute = clock != null ? clock.CurrentHour * 60 + clock.CurrentMinute : 0;
        startTime = nextSample = Time.unscaledTime;
        activeSampleSeconds = 0;
        nextTextureRefresh = startTime; displayedRevision = -1;
        nextFlush = startTime + 5f;
        nextCheckpoint = startTime + Mathf.Max(5f, checkpointIntervalSeconds);
        recording = true;
        TrackMovement(0); // Establish a baseline; do not backfill time before first observation.
        try
        {
            OutputDirectory = runDirectory ?? Path.Combine(Application.persistentDataPath, "SimulationRuns",
                DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff", Culture) + "_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(OutputDirectory);
            cellsWriter = Open("heatmap_cells.csv", "sample_id,elapsed_seconds,sim_day,sim_minute,floor,cell_x,cell_z,x_min,z_min,cell_size,occupants,person_samples,unique_visitors,person_seconds,cell_entries,walking_entries,simulated_seconds,simulated_person_seconds,mean_people,walking_entries_per_sim_hour");
            censusWriter = Open("heatmap_population.csv", "sample_id,elapsed_seconds,sim_day,sim_minute,expected_roster,registered,present,off_campus,main_present,filler_present,particles_enabled,floor,present_on_floor,simulated_seconds");
            Debug.Log("[SimulationHeatmap] Persistent heatmap data: " + OutputDirectory);
            ExportSnapshot(false);
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { FailRecording(e); }
    }

    private StreamWriter Open(string name, string header)
    {
        var writer = new StreamWriter(Path.Combine(OutputDirectory, name), false, new UTF8Encoding(false), 65536);
        writer.WriteLine(header); writer.Flush(); return writer;
    }

    private void LateUpdate()
    {
        if (!SimulationFlowController.RenderingDisabled && IsRunning && Grid != null && !TypingInField())
        {
#if ENABLE_INPUT_SYSTEM
            if (Keyboard.current != null && Keyboard.current.hKey.wasPressedThisFrame) SetVisible(!Visible);
#elif ENABLE_LEGACY_INPUT_MANAGER
            if (Input.GetKeyDown(KeyCode.H)) SetVisible(!Visible);
#endif
        }
        float now = Time.unscaledTime;
        if (recording && now >= nextFlush) { Flush(); nextFlush = now + 5f; }
        if (!recording || clock == null || !clock.IsRunning || clock.timeMultipler <= 0 || Time.timeScale <= 0) return;
        TrackMovement(Time.unscaledDeltaTime);
        activeSampleSeconds += Time.unscaledDeltaTime;
        if (now >= nextSample)
        {
            nextSample = now + Mathf.Max(0.1f, sampleIntervalSeconds);
            Capture(now - startTime, activeSampleSeconds);
            activeSampleSeconds = 0;
        }
        if (now >= nextCheckpoint)
        {
            nextCheckpoint = now + Mathf.Max(5f, checkpointIntervalSeconds);
            TryExport(false);
        }
        if (Visible && (Grid.Revision != displayedRevision || Grid.StatisticsRevision != displayedStatisticsRevision) && now >= nextTextureRefresh)
        { RefreshTextures(); nextTextureRefresh = now + 0.1f; }
    }

    private PersonTrace Identity(AgentHealth agent)
    {
        if (!identities.TryGetValue(agent, out var person) || person.generation != agent.SpawnGeneration)
        {
            person = new PersonTrace { generation = agent.SpawnGeneration, id = ++nextPersonId,
                filler = agent.GetComponent<FillerAgent>() != null };
            identities[agent] = person;
        }
        return person;
    }

    private void TrackMovement(float elapsedSeconds)
    {
        double simulated = clock != null ? clock.ElapsedSimulationSeconds : lastSimulationSeconds;
        double duration = Math.Max(0, simulated - lastSimulationSeconds);
        lastSimulationSeconds = simulated;
        Grid.AdvanceTime(elapsedSeconds, duration);
        // Count active tracking frames, so pausing does not turn a continuous
        // stay into a new arrival. Unregistered agents still break continuity.
        int frame = ++trackingFrame;
        trackedRegisteredCount = 0;
        if (clock != null) { lastObservedDay = clock.CurrentDay; lastObservedMinute = clock.CurrentHour * 60 + clock.CurrentMinute; }
        foreach (var agent in AgentHealth.RegisteredAgents)
        {
            if (agent == null) continue;
            trackedRegisteredCount++;
            if (!agent.IsPresent)
            {
                if (identities.TryGetValue(agent, out var absent)) absent.present = false;
                continue;
            }
            var person = Identity(agent);
            Vector3 position = agent.transform.position;
            bool continuous = person.present && frame == person.lastFrame + 1 &&
                Vector3.Distance(person.position, position) <= Mathf.Max(Grid.CellSize, maximumTrackedSpeed * Mathf.Max(0, elapsedSeconds));
            if (person.present) Grid.RecordDuration(person.position, position, duration, continuous);
            if (person.lastFrame < 0) Grid.RecordArrival(position, person.id);
            else Grid.RecordMovement(person.position, position, person.id, continuous);
            person.present = true; person.position = position; person.lastFrame = frame;
        }
    }

    public static bool TypingInField()
    {
        var selected = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
        return selected != null && (selected.GetComponent<TMP_InputField>() != null || selected.GetComponent<InputField>() != null);
    }

    private void Capture(float elapsed, float duration)
    {
        using var profile = SampleMarker.Auto();
        Grid.BeginSample(); Array.Clear(floorCounts, 0, floorCounts.Length);
        PresentCount = FillerCount = 0; RegisteredCount = trackedRegisteredCount;
        // Use the latest observed frame, including on stop when Unity may already
        // have disabled the agents. Teardown must not invent an empty building.
        foreach (var identity in identities.Values)
        {
            if (!identity.present || identity.lastFrame != trackingFrame) continue;
            PresentCount++;
            if (identity.filler) FillerCount++;
            var position = identity.position;
            floorCounts[Grid.FloorAt(position.y)]++;
            Grid.Observe(position, identity.id, duration);
        }
        if (cellsWriter == null || censusWriter == null) return;
        try
        {
            string common = (Grid.SampleCount - 1).ToString(Culture) + "," + elapsed.ToString("F3", Culture) + "," +
                lastObservedDay.ToString(Culture) + "," + lastObservedMinute.ToString(Culture) + ",";
            var rows = new StringBuilder();
            foreach (var pair in Grid.Cells)
            {
                var key = pair.Key; var cell = pair.Value;
                rows.Append(common).Append(key.y + 1).Append(',').Append(key.x).Append(',').Append(key.z).Append(',')
                    .Append((key.x * Grid.CellSize).ToString("R", Culture)).Append(',')
                    .Append((key.z * Grid.CellSize).ToString("R", Culture)).Append(',')
                    .Append(Grid.CellSize.ToString("R", Culture)).Append(',').Append(cell.Occupants).Append(',')
                    .Append(cell.PersonSamples).Append(',').Append(cell.Visitors.Count).Append(',')
                    .Append(cell.PersonSeconds.ToString("F3", Culture)).Append(',').Append(cell.Entries).Append(',')
                    .Append(cell.WalkingEntries).Append(',').Append(Grid.SimulatedSeconds.ToString("R", Culture)).Append(',')
                    .Append(cell.SimulatedPersonSeconds.ToString("R", Culture)).Append(',')
                    .Append(HeatmapMeasures.Value(cell, HeatmapMeasure.MeanOccupancy, Grid.SimulatedSeconds, Grid.SampleCount).ToString("R", Culture)).Append(',')
                    .Append(HeatmapMeasures.Value(cell, HeatmapMeasure.WalkingEntriesPerHour, Grid.SimulatedSeconds, Grid.SampleCount).ToString("R", Culture)).AppendLine();
            }
            cellsWriter.Write(rows.ToString());
            for (int floor = 0; floor < floorCounts.Length; floor++)
                censusWriter.WriteLine(common + SimulationFlowController.ActiveAgentCount + "," + RegisteredCount + "," +
                    PresentCount + "," + (RegisteredCount - PresentCount) + "," + (PresentCount - FillerCount) + "," + FillerCount + "," +
                    (ParticleSimulationSettings.Enabled ? "1," : "0,") + (floor + 1) + "," + floorCounts[floor] + "," + Grid.SimulatedSeconds.ToString("R", Culture));
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { FailRecording(e); }
    }

    public void SetVisible(bool visible)
    {
        Visible = visible && !SimulationFlowController.RenderingDisabled;
        if (Visible && surfaces.Count == 0) BuildSurfaces();
        ApplyFloorVisibility();
        if (Visible) RefreshTextures();
        if (Visible && legend == null)
        {
            legend = gameObject.AddComponent<HeatmapLegendUI>();
            legend.Initialize(this, clock);
        }
        if (legend != null) legend.SetVisible(Visible);
    }

    public void SetMeasure(HeatmapMeasure value)
    {
        measure = value; scaleMaximum = (float)HeatmapMeasures.DefaultMaximum(value);
        displayedStatisticsRevision = -1;
        if (Visible) RefreshTextures();
    }

    public void SetScale(float maximum)
    {
        if (float.IsNaN(maximum) || float.IsInfinity(maximum) || maximum <= 0) return;
        scaleMaximum = maximum; displayedStatisticsRevision = -1;
        if (Visible) RefreshTextures();
    }

    public Color CellColor(Vector3Int key)
    {
        Grid.Cells.TryGetValue(key, out var cell);
        double value = HeatmapMeasures.Value(cell, measure, Grid.SimulatedSeconds, Grid.SampleCount);
        return double.IsNaN(value) ? new Color(0.5f, 0.5f, 0.5f, 0.4f) : gradient.Evaluate(Mathf.Clamp01((float)(value / Math.Max(0.001f, scaleMaximum))));
    }

    public Color ScaleColor(float fraction) => gradient.Evaluate(Mathf.Clamp01(fraction));

    public string ExportSnapshot(bool tiffs = true)
    {
        if (Grid == null || string.IsNullOrEmpty(OutputDirectory)) throw new InvalidOperationException("Start a simulation before exporting its heatmap.");
        var data = HeatmapSnapshot.Capture(Grid, coverage ??= HeatmapCoverage.Capture(Grid));
        data.building = buildingName; data.recordedUtc = recordedUtc; data.startDay = startDay; data.startMinute = startMinute;
        data.endDay = lastObservedDay;
        data.endMinute = lastObservedMinute;
        data.measure = measure.ToString(); data.scaleMaximum = scaleMaximum;
        data.registeredCount = RegisteredCount; data.presentCount = PresentCount; data.fillerCount = FillerCount;
        HeatmapExport.Save(OutputDirectory, data, tiffs);
        return OutputDirectory;
    }

    private void TryExport(bool tiffs)
    {
        if (string.IsNullOrEmpty(OutputDirectory)) return;
        try { ExportSnapshot(tiffs); }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is InvalidOperationException || e is OverflowException)
        { Debug.LogError("[SimulationHeatmap] Export failed: " + e.Message); }
    }

    public void SetFloorSlice(bool active, int floor)
    {
        floorSlice = active; visibleFloor = floor;
        ApplyFloorVisibility();
        if (Visible) RefreshTextures();
    }

    private void ApplyFloorVisibility()
    {
        for (int floor = 0; floor < surfaces.Count; floor++)
        {
            surfaces[floor].SetActive(Visible && (!floorSlice || floor == visibleFloor));
            // Decorative slabs can sit above the model's baked NavMesh.
            materials[floor].SetFloat("_ZTest", floorSlice ? (float)UnityEngine.Rendering.CompareFunction.Always :
                (float)UnityEngine.Rendering.CompareFunction.LessEqual);
        }
    }

    private void BuildSurfaces()
    {
        var triangulation = NavMesh.CalculateTriangulation();
        if (triangulation.vertices.Length == 0) { Debug.LogWarning("[SimulationHeatmap] No baked navigation surface to display."); return; }
        var bounds = new Bounds(triangulation.vertices[0], Vector3.zero);
        foreach (var v in triangulation.vertices) bounds.Encapsulate(v);
        minX = Mathf.FloorToInt(bounds.min.x / Grid.CellSize); minZ = Mathf.FloorToInt(bounds.min.z / Grid.CellSize);
        width = Mathf.CeilToInt(bounds.max.x / Grid.CellSize) - minX + 1;
        depth = Mathf.CeilToInt(bounds.max.z / Grid.CellSize) - minZ + 1;
        if (width > SystemInfo.maxTextureSize || depth > SystemInfo.maxTextureSize)
        { Debug.LogError("[SimulationHeatmap] Grid exceeds texture size; increase cell size."); return; }
        var shader = Resources.Load<Shader>("OccupancyHeatmap");
        if (shader == null) { Debug.LogError("[SimulationHeatmap] Occupancy shader missing."); return; }
        for (int floor = 0; floor < Grid.FloorHeights.Length; floor++)
        {
            var vertices = new List<Vector3>(); var uv = new List<Vector2>(); var triangles = new List<int>();
            for (int i = 0; i < triangulation.indices.Length; i += 3)
            {
                var a = triangulation.vertices[triangulation.indices[i]];
                var b = triangulation.vertices[triangulation.indices[i + 1]];
                var c = triangulation.vertices[triangulation.indices[i + 2]];
                if (Grid.FloorAt((a.y + b.y + c.y) / 3f) != floor) continue;
                foreach (var v in new[] { a, b, c })
                {
                    triangles.Add(vertices.Count); vertices.Add(v + Vector3.up * 0.06f);
                    uv.Add(new Vector2((v.x / Grid.CellSize - minX) / width, (v.z / Grid.CellSize - minZ) / depth));
                }
            }
            var mesh = new Mesh { name = "Heatmap floor " + (floor + 1), indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            mesh.SetVertices(vertices); mesh.SetUVs(0, uv); mesh.SetTriangles(triangles, 0); mesh.RecalculateBounds();
            var texture = new Texture2D(width, depth, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            var material = new Material(shader); material.mainTexture = texture;
            var surface = new GameObject("Occupancy heatmap floor " + (floor + 1)); surface.transform.SetParent(transform, false);
            surface.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = surface.AddComponent<MeshRenderer>(); renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; renderer.receiveShadows = false;
            surfaces.Add(surface); meshes.Add(mesh); textures.Add(texture); materials.Add(material);
            pixelBuffers.Add(new Color[width * depth]);
        }
    }

    private void RefreshTextures()
    {
        for (int floor = 0; floor < textures.Count; floor++)
        {
            if (floorSlice && floor != visibleFloor) continue;
            var pixels = pixelBuffers[floor];
            for (int z = 0; z < depth; z++) for (int x = 0; x < width; x++)
                pixels[z * width + x] = CellColor(new Vector3Int(minX + x, floor, minZ + z));
            textures[floor].SetPixels(pixels); textures[floor].Apply(false);
        }
        displayedRevision = Grid.Revision;
        displayedStatisticsRevision = Grid.StatisticsRevision;
    }

    private void FailRecording(Exception e)
    {
        CloseWriters();
        Debug.LogError("[SimulationHeatmap] Disk recording unavailable: " + e.Message);
    }
    private void Flush()
    {
        try { cellsWriter?.Flush(); censusWriter?.Flush(); }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { FailRecording(e); }
    }
    private void CloseWriters() { Dispose(ref cellsWriter); Dispose(ref censusWriter); }
    private static void Dispose(ref StreamWriter writer)
    {
        var closing = writer; writer = null;
        try { closing?.Dispose(); }
        catch (IOException e) { Debug.LogError("[SimulationHeatmap] File close failed: " + e.Message); }
    }
    private void Close()
    {
        if (recording)
        {
            // Persist the final partial snapshot as well as the continuous time integral.
            if (Grid != null && (activeSampleSeconds > 0 || Grid.SampleCount == 0))
                Capture(Time.unscaledTime - startTime, activeSampleSeconds);
            TryExport(exportTiffsOnStop);
        }
        recording = false; CloseWriters();
    }
    private void OnApplicationPause(bool paused) { if (paused) Flush(); }
    private void OnDisable() => Close();
    private void OnApplicationQuit() => Close();
    private void OnDestroy()
    {
        Close();
        foreach (var surface in surfaces) if (surface != null) Destroy(surface);
        foreach (var mesh in meshes) Destroy(mesh);
        foreach (var material in materials) Destroy(material);
        foreach (var texture in textures) Destroy(texture);
    }
}
