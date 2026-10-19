using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.AI;

// Run in an isolated batch editor: -executeMethod HeatmapValidation.Run.
public static class HeatmapValidation
{
    private const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Instance;
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static object Call(object target, string name, params object[] args) => target.GetType().GetMethod(name, Hidden).Invoke(target, args);

    // Uses the actual model in a temporary project copy with graphics enabled.
    // Produces floor PNGs for visual inspection; never saves scene changes.
    public static void RenderModelFloors()
    {
        if (!Application.isBatchMode) throw new InvalidOperationException("Use an isolated batch editor for rendering validation.");
        UnityEditor.SceneManagement.EditorSceneManager.OpenScene("Assets/McEinryScene.unity");
        var flow = UnityEngine.Object.FindFirstObjectByType<SimulationFlowController>();
        Require(flow != null, "The model flow controller is missing.");
        flow.runWithoutRendering = false; // Rendered validation, regardless of saved background-run preference.
        var navigation = UnityEngine.Object.FindFirstObjectByType<NavigationController>(); navigation.Init();
        Call(flow, "Awake");
        Require(flow.particleToggle != null && flow.particleToggle.gameObject.activeInHierarchy, "Particle toggle is missing from the actual menu.");
        var output = Path.GetFullPath(Path.Combine(Application.dataPath, "../ModelHeatmapValidation")); Directory.CreateDirectory(output);
        ValidateAndRenderMenu(flow, output);
        if (flow.menuPanel != null) flow.menuPanel.SetActive(false);
        if (flow.loadingPanel != null) flow.loadingPanel.SetActive(false);
        var map = flow.GetComponent<SimulationHeatmap>();
        map.SetMeasure(HeatmapMeasure.WalkingEntriesPerHour); map.SetScale(20);
        map.Grid.AdvanceTime(60, 3600);
        var navmesh = NavMesh.CalculateTriangulation();
        Require(navmesh.vertices.Length > 0 && map.Grid.FloorHeights.Length == 4, "Model floor heights or NavMesh are missing.");
        var counts = new int[4];
        map.Grid.BeginSample();
        for (int i = 0; i < navmesh.indices.Length; i += 3)
        {
            var a = navmesh.vertices[navmesh.indices[i]]; var b = navmesh.vertices[navmesh.indices[i + 1]]; var c = navmesh.vertices[navmesh.indices[i + 2]];
            var center = (a + b + c) / 3;
            counts[map.Grid.FloorAt(center.y)]++;
        }
        Require(counts.All(n => n > 0), "One of the configured floors has no heatmap navigation coverage: " + string.Join(",", counts));
        // Follow actual model NavMesh routes, rather than filling arbitrary cells.
        for (int floor = 0; floor < 4; floor++)
        {
            var candidates = navigation.rooms.Where(r => r != null && map.Grid.FloorAt(r.transform.position.y) == floor)
                .Select(r => r.transform.position).ToArray();
            Vector3[] route = null; float longest = 0;
            for (int a = 0; a < candidates.Length; a++) for (int b = a + 1; b < candidates.Length; b++)
            {
                if (!NavMesh.SamplePosition(candidates[a], out var from, 3, NavMesh.AllAreas) ||
                    !NavMesh.SamplePosition(candidates[b], out var to, 3, NavMesh.AllAreas)) continue;
                var path = new NavMeshPath();
                if (!NavMesh.CalculatePath(from.position, to.position, NavMesh.AllAreas, path) || path.status != NavMeshPathStatus.PathComplete ||
                    path.corners.Any(p => map.Grid.FloorAt(p.y) != floor)) continue;
                float length = 0; for (int c = 1; c < path.corners.Length; c++) length += Vector3.Distance(path.corners[c - 1], path.corners[c]);
                if (length > longest) { longest = length; route = path.corners; }
            }
            Require(route != null && longest > 10, "No hallway route was available on model floor " + (floor + 1));
            for (int visit = 0; visit < 20; visit++)
            {
                map.Grid.RecordArrival(route[0], floor * 100 + visit);
                for (int c = 1; c < route.Length; c++) map.Grid.RecordMovement(route[c - 1], route[c], floor * 100 + visit);
            }
            Debug.Log("MODEL HALLWAY ROUTE: floor " + (floor + 1) + ", " + longest.ToString("F1", CultureInfo.InvariantCulture) + " metres, " + route.Length + " corners.");
        }
        map.SetVisible(true);
        var saved = HeatmapSnapshot.Capture(map.Grid, HeatmapCoverage.Capture(map.Grid));
        saved.building = "McEniry (synthetic route validation)";
        saved.measure = map.measure.ToString(); saved.scaleMaximum = map.scaleMaximum;
        HeatmapExport.Save(Path.Combine(output, "SavedHeatmap"), saved, true);
        var overlays = UnityEngine.Object.FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None)
            .Where(r => r.gameObject.name.StartsWith("Occupancy heatmap floor")).ToArray();
        Require(overlays.Length == 4 && overlays.All(r => r.GetComponent<MeshFilter>().sharedMesh.vertexCount > 0), "Actual model overlay meshes are empty.");
        var shader = Resources.Load<Shader>("OccupancyHeatmap"); Require(shader != null && !ShaderUtil.ShaderHasError(shader), "Model heatmap shader errors.");
        var camera = Camera.main; camera.aspect = 16f / 9f;
        var legend = UnityEngine.Object.FindObjectsByType<Canvas>(FindObjectsSortMode.None)
            .First(c => c.transform.Find("Heatmap legend") != null);
        var oldMode = legend.renderMode; var oldCamera = legend.worldCamera; float oldDistance = legend.planeDistance;
        var hiddenUI = legend.transform.Cast<Transform>().Where(t => t.name != "Heatmap legend" && t.gameObject.activeSelf).ToArray();
        foreach (var item in hiddenUI) item.gameObject.SetActive(false);
        var uiObject = new GameObject("Legend preview camera"); var uiCamera = uiObject.AddComponent<Camera>();
        uiCamera.cullingMask = 1 << legend.gameObject.layer; uiCamera.clearFlags = CameraClearFlags.Depth;
        uiCamera.transform.position = new Vector3(0, 0, -100); uiCamera.orthographic = true;
        int oldMask = camera.cullingMask; camera.cullingMask &= ~uiCamera.cullingMask;
        legend.renderMode = RenderMode.ScreenSpaceCamera; legend.worldCamera = uiCamera; legend.planeDistance = 10;
        map.FloorView.SetActive(true);
        for (int floor = 0; floor < 4; floor++)
        {
            map.FloorView.SelectFloor(floor);
            Require(overlays.Count(r => r.gameObject.activeInHierarchy) == 1 &&
                overlays.Single(r => r.gameObject.activeInHierarchy).sharedMaterial.GetFloat("_ZTest") == (float)UnityEngine.Rendering.CompareFunction.Always,
                "Top-down must display only the selected overlay above decorative slabs.");
            var target = new RenderTexture(1600, 900, 24); target.Create();
            uiCamera.targetTexture = target;
            Call(map.GetComponent<HeatmapLegendUI>(), "Refresh");
            Canvas.ForceUpdateCanvases();
            foreach (var text in legend.GetComponentsInChildren<TMPro.TMP_Text>()) text.ForceMeshUpdate();
            UnityEngine.Rendering.RenderPipeline.SubmitRenderRequest(camera,
                new UnityEngine.Rendering.Universal.UniversalRenderPipeline.SingleCameraRequest { destination = target });
            var previous = RenderTexture.active; RenderTexture.active = target;
            var pixels = new Texture2D(1600, 900, TextureFormat.RGB24, false);
            pixels.ReadPixels(new Rect(0, 0, 1600, 900), 0, 0); pixels.Apply();
            File.WriteAllBytes(Path.Combine(output, "floor-" + (floor + 1) + ".png"), pixels.EncodeToPNG());
            // URP camera render requests clear colour independently. Capture the
            // overlay separately instead of presenting a misleading composite.
            UnityEngine.Rendering.RenderPipeline.SubmitRenderRequest(uiCamera,
                new UnityEngine.Rendering.Universal.UniversalRenderPipeline.SingleCameraRequest { destination = target });
            RenderTexture.active = target; pixels.ReadPixels(new Rect(0, 0, 1600, 900), 0, 0); pixels.Apply();
            File.WriteAllBytes(Path.Combine(output, "legend-floor-" + (floor + 1) + ".png"), pixels.EncodeToPNG());
            RenderTexture.active = previous; UnityEngine.Object.DestroyImmediate(pixels); uiCamera.targetTexture = null; target.Release(); UnityEngine.Object.DestroyImmediate(target);
        }
        legend.renderMode = oldMode; legend.worldCamera = oldCamera; legend.planeDistance = oldDistance;
        camera.cullingMask = oldMask; UnityEngine.Object.DestroyImmediate(uiObject);
        foreach (var item in hiddenUI) item.gameObject.SetActive(true);
        map.FloorView.SetActive(false);
        Require(overlays.All(r => r.gameObject.activeInHierarchy &&
            r.sharedMaterial.GetFloat("_ZTest") == (float)UnityEngine.Rendering.CompareFunction.LessEqual), "Perspective overlay depth was not restored.");
        Require(!ShaderUtil.ShaderHasError(shader), "Heatmap shader failed when actually rendered.");
        Debug.Log("MODEL HEATMAP RENDER VALIDATION PASSED: four floor meshes, actual menu toggle, model NavMesh coverage (" +
            string.Join(",", counts) + " triangles), shader and camera restoration. Floor PNGs: " + output);
    }

    private static void ValidateAndRenderMenu(SimulationFlowController flow, string output)
    {
        var filler = flow.menuPanel.GetComponentInChildren<FillerToggleUI>(true).toggle;
        var particle = flow.particleToggle;
        Require(particle.GetComponent<FillerToggleUI>() == null, "Particle checkbox retained the filler setting binding.");
        Require(particle.transform.localScale == filler.transform.localScale && particle.colors == filler.colors &&
            ((UnityEngine.UI.Image)particle.targetGraphic).sprite == ((UnityEngine.UI.Image)filler.targetGraphic).sprite &&
            ((UnityEngine.UI.Image)particle.graphic).sprite == ((UnityEngine.UI.Image)filler.graphic).sprite,
            "Particle checkbox does not match the existing menu style.");
        var label = flow.menuPanel.GetComponentsInChildren<TMPro.TextMeshProUGUI>(true).Single(t => t.text == "Enable particles?");
        var fillerLabel = flow.menuPanel.GetComponentsInChildren<TMPro.TextMeshProUGUI>(true).Single(t => t.text.Contains("Enable filler"));
        Require(label.font == fillerLabel.font && label.fontSize == fillerLabel.fontSize && label.color == fillerLabel.color &&
            label.fontStyle == fillerLabel.fontStyle, "Particle label does not match the existing menu font.");
        bool previous = ParticleSimulationSettings.Enabled;
        particle.isOn = !previous; Require(ParticleSimulationSettings.Enabled == !previous, "Menu checkbox did not change particle setting.");
        particle.isOn = previous;
        var canvas = flow.menuPanel.GetComponentInParent<Canvas>();
        var oldMode = canvas.renderMode; var oldCamera = canvas.worldCamera; float oldDistance = canvas.planeDistance;
        var cameraObject = new GameObject("Menu preview camera"); var camera = cameraObject.AddComponent<Camera>();
        camera.cullingMask = 1 << canvas.gameObject.layer; camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = Color.gray; camera.transform.position = new Vector3(0, 0, -100); camera.orthographic = true;
        var target = new RenderTexture(1200, 1000, 24); target.Create(); camera.targetTexture = target;
        canvas.renderMode = RenderMode.ScreenSpaceCamera; canvas.worldCamera = camera; canvas.planeDistance = 10;
        // Batch edit-mode scene loads do not run the normal first-frame UI lifecycle.
        foreach (var text in canvas.GetComponentsInChildren<TMPro.TextMeshProUGUI>(true))
        { Call(text, "Awake"); if (text.isActiveAndEnabled) { Call(text, "OnEnable"); text.ForceMeshUpdate(); } }
        Canvas.ForceUpdateCanvases();
        UnityEngine.Rendering.RenderPipeline.SubmitRenderRequest(camera,
            new UnityEngine.Rendering.Universal.UniversalRenderPipeline.SingleCameraRequest { destination = target });
        var previousTarget = RenderTexture.active; RenderTexture.active = target;
        var pixels = new Texture2D(1200, 1000, TextureFormat.RGB24, false);
        pixels.ReadPixels(new Rect(0, 0, 1200, 1000), 0, 0); pixels.Apply();
        File.WriteAllBytes(Path.Combine(output, "menu.png"), pixels.EncodeToPNG());
        RenderTexture.active = previousTarget; canvas.renderMode = oldMode; canvas.worldCamera = oldCamera; canvas.planeDistance = oldDistance;
        UnityEngine.Object.DestroyImmediate(pixels); camera.targetTexture = null; target.Release();
        UnityEngine.Object.DestroyImmediate(target); UnityEngine.Object.DestroyImmediate(cameraObject);
    }

    public static void Run()
    {
        if (!Application.isBatchMode) throw new InvalidOperationException("Use an isolated batch editor for these fixtures.");
        UnityEditor.SceneManagement.EditorSceneManager.NewScene(UnityEditor.SceneManagement.NewSceneSetup.EmptyScene,
            UnityEditor.SceneManagement.NewSceneMode.Single);
        ValidateGrid();
        ValidateMovementTraffic();
        ValidatePopulationAndPersistence();
        ValidateParticleSwitch();
        ValidateCameraSlice();
        SimulationPerformanceValidation.Run();
        Debug.Log("HEATMAP VALIDATION PASSED: between-snapshot hallway crossings, return visits, stationary stays, teleport/absence exclusions, distinct visitors, negative grid coordinates, floor separation, population conservation, pooled identities, CSV persistence/culture, HUD removal, particle gating/new spawns, and camera slice/restoration. Existing particle/airflow/scheduler regressions also passed.");
    }

    private static void ValidateGrid()
    {
        var grid = new HeatmapGrid(new[] { 8f, 0f, 4f }, 1);
        var lower = new Vector3(-0.1f, 0.6f, -0.1f);
        var upper = new Vector3(-0.1f, 4.6f, -0.1f);
        Require(grid.Key(lower) == new Vector3Int(-1, 0, -1), "Negative coordinates used truncation rather than floor.");
        grid.RecordArrival(lower, 1); grid.RecordArrival(lower, 2); grid.RecordArrival(upper, 3);
        grid.BeginSample(); grid.Observe(lower, 1, 1); grid.Observe(lower, 2, 1); grid.Observe(upper, 3, 1);
        Require(grid.Cells.Values.Sum(c => c.Occupants) == 3, "People were lost/double-counted across floors.");
        grid.BeginSample(); grid.Observe(lower, 1, 2);
        var cell = grid.Cells[grid.Key(lower)];
        Require(cell.Occupants == 1 && cell.PersonSamples == 3 && cell.Visitors.Count == 2 && cell.PersonSeconds == 4,
            "Instant count, frequency, visitors and duration were conflated.");
        Require(grid.Cells[grid.Key(upper)].Occupants == 0 && grid.Cells[grid.Key(upper)].PersonSamples == 1,
            "Empty cells retained occupants or lost history.");
        var gradient = new Gradient();
        gradient.SetKeys(new[] { new GradientColorKey(Color.blue, 0), new GradientColorKey(Color.red, 1) },
            new[] { new GradientAlphaKey(1, 0), new GradientAlphaKey(1, 1) });
        Require(grid.ColorAt(new Vector3Int(500, 0, 500), gradient, grid.MaximumEntries) == Color.blue, "Low frequency is not blue.");
        Require(grid.ColorAt(grid.Key(lower), gradient, grid.MaximumEntries) == Color.red, "High frequency is not red.");
    }

    private static void ValidateMovementTraffic()
    {
        var grid = new HeatmapGrid(new[] { 0f, 4f }, 1);
        var start = new Vector3(-2.5f, 0, 0.5f); var end = new Vector3(3.5f, 0, 0.5f);
        grid.RecordArrival(start, 1); grid.RecordMovement(start, end, 1);
        Require(grid.Cells.Count == 7 && grid.Cells.Values.All(c => c.Entries == 1), "Fast walk skipped hallway cells.");
        grid.RecordMovement(end, start, 1);
        Require(grid.Cells[new Vector3Int(0, 0, 0)].Entries == 2, "Return walk did not count another visit.");
        long before = grid.Revision;
        for (int i = 0; i < 3000; i++)
        { grid.RecordMovement(start, start, 1); grid.BeginSample(); grid.Observe(start, 1, 1); }
        Require(grid.Revision == before && grid.MaximumEntries == 2, "Stationary occupancy dominated traffic colors.");
        grid.RecordMovement(start, new Vector3(20.5f, 4, 0.5f), 1);
        Require(grid.Cells.ContainsKey(new Vector3Int(20, 1, 0)) && !grid.Cells.ContainsKey(new Vector3Int(10, 0, 0)), "Floor change invented a hallway trail.");
        grid.RecordMovement(end, new Vector3(20.5f, 0, 0.5f), 1, false);
        Require(!grid.Cells.ContainsKey(new Vector3Int(10, 0, 0)), "Teleport invented a hallway trail.");
        var diagonal = new HeatmapGrid(new[] { 0f }, 1);
        diagonal.RecordArrival(new Vector3(-2.5f, 0, -2.5f), 1);
        diagonal.RecordMovement(new Vector3(-2.5f, 0, -2.5f), new Vector3(2.5f, 0, 2.5f), 1);
        Require(diagonal.Cells.Count == 6 && diagonal.Cells.Keys.All(k => k.x == k.z), "Diagonal corner traversal counted untouched cells.");

        var root = new GameObject("Between-snapshots movement fixture");
        var walker = MakePerson("Hallway walker", new Vector3(0.5f, 0, 0.5f));
        try
        {
            var clock = root.AddComponent<TimeManager>(); clock.BeginSimulation();
            var map = root.AddComponent<SimulationHeatmap>(); map.floorHeights = new[] { 0f, 4f }; map.Initialize(clock);
            map.Begin(Path.GetFullPath(Path.Combine(Application.dataPath, "../HeatmapMovementValidationOutput")));
            Call(map, "TrackMovement", 0.1f); Call(map, "Capture", 0f, 0f);
            walker.transform.position = new Vector3(3.5f, 0, 0.5f); Call(map, "TrackMovement", 0.1f);
            // Whole walk happens between CSV snapshots, with heatmap hidden.
            Require(map.Grid.Cells[new Vector3Int(1, 0, 0)].Entries == 1 && map.Grid.Cells[new Vector3Int(2, 0, 0)].Entries == 1,
                "Runtime tracker lost a short hallway visit between snapshots.");
            Call(map, "Capture", 1f, 1f);
            Require(map.Grid.Cells.Values.Sum(c => c.Occupants) == 1 && map.Grid.Cells[new Vector3Int(1, 0, 0)].Occupants == 0,
                "Historic hallway visitors became extra current people.");
            walker.transform.position = new Vector3(20.5f, 0, 0.5f); Call(map, "TrackMovement", 0.1f);
            Require(!map.Grid.Cells.ContainsKey(new Vector3Int(10, 0, 0)), "Runtime teleport created a false traffic trail.");
            walker.GetComponent<Collider>().enabled = false; Call(map, "TrackMovement", 0.1f);
            walker.transform.position = new Vector3(23.5f, 0, 0.5f); walker.GetComponent<Collider>().enabled = true;
            Call(map, "TrackMovement", 0.1f);
            Require(!map.Grid.Cells.ContainsKey(new Vector3Int(22, 0, 0)), "Absent walker left a phantom trail on return.");
            long stationaryEntries = map.Grid.Revision;
            for (int i = 0; i < 20; i++) Call(map, "TrackMovement", 0.1f);
            Require(map.Grid.Revision == stationaryEntries, "Runtime tracker repeatedly counted a stationary stay.");
            Call(walker, "OnDisable"); Call(map, "TrackMovement", 0.1f);
            walker.transform.position = new Vector3(26.5f, 0, 0.5f); Call(walker, "OnEnable");
            Call(map, "TrackMovement", 0.1f);
            Require(!map.Grid.Cells.ContainsKey(new Vector3Int(25, 0, 0)), "Unregistered walker left a phantom trail on return.");
            Call(map, "Capture", 2f, 1f); Call(map, "Close");
            var rows = File.ReadAllLines(Path.Combine(map.OutputDirectory, "heatmap_cells.csv")).Skip(1).Select(r => r.Split(',')).ToArray();
            Require(rows.Any(r => r[0] == "2" && r[5] == "1" && r[10] == "0" && r[14] == "1"), "Hallway visits were not persisted independently of occupancy.");
            Require(typeof(SimulationHeatmap).GetMethod("OnGUI", Hidden) == null, "Added runtime HUD is still present.");
        }
        finally
        { Call(walker, "OnDisable"); UnityEngine.Object.DestroyImmediate(walker.gameObject); UnityEngine.Object.DestroyImmediate(root); }
    }

    private static AgentHealth MakePerson(string name, Vector3 position, AgentActivity? activity = null, bool filler = false)
    {
        var go = new GameObject(name); go.transform.position = position; go.AddComponent<BoxCollider>();
        if (activity.HasValue)
        {
            var nav = go.AddComponent<NavigationAgent>(); var schedule = new AgentSchedule(); schedule.SetActivity(activity.Value);
            typeof(NavigationAgent).GetField("_schedule", Hidden).SetValue(nav, schedule);
        }
        if (filler) go.AddComponent<FillerAgent>();
        var person = go.AddComponent<AgentHealth>(); Call(person, "Awake"); Call(person, "OnEnable"); person.ResetForSpawn(false);
        return person;
    }

    private static void ValidatePopulationAndPersistence()
    {
        var root = new GameObject("Heatmap CSV fixture");
        var people = new System.Collections.Generic.List<AgentHealth>();
        var oldCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var main = MakePerson("Main", new Vector3(-0.2f, 0.6f, 0), AgentActivity.InClass); people.Add(main);
            main.gameObject.AddComponent<SphereCollider>(); // A second collider is still one person.
            people.Add(MakePerson("Filler", new Vector3(-0.2f, 4.6f, 0), filler: true));
            var absent = MakePerson("Hidden", Vector3.zero); people.Add(absent); absent.GetComponent<Collider>().enabled = false;
            people.Add(MakePerson("Not arrived", Vector3.zero, AgentActivity.Idle));
            people.Add(MakePerson("Off campus", Vector3.zero, AgentActivity.OffCampus));
            people.Add(MakePerson("Finished", Vector3.zero, AgentActivity.Done));
            var clock = root.AddComponent<TimeManager>(); clock.BeginSimulation();
            var map = root.AddComponent<SimulationHeatmap>(); map.floorHeights = new[] { 0f, 4f }; map.Initialize(clock);
            map.Begin(Path.GetFullPath(Path.Combine(Application.dataPath, "../HeatmapValidationOutput")));
            Call(map, "TrackMovement", 0.25f); Call(map, "Capture", 0.25f, 0.25f);
            Require(map.PresentCount == 2 && map.FillerCount == 1 && map.RegisteredCount == 6, "Population includes invisible/absent students or counts colliders.");
            map.SetVisible(false); Call(map, "TrackMovement", 1f); Call(map, "Capture", 1.25f, 1f);
            Require(map.Grid.Cells.Values.Sum(c => c.PersonSamples) == 4, "Hiding the map interrupted sampling.");
            main.ResetForSpawn(false); Call(map, "TrackMovement", 1f); Call(map, "Capture", 2.25f, 1f);
            Require(map.Grid.Cells[map.Grid.Key(main.transform.position)].Visitors.Count == 2, "Pooled students reused a visitor identity.");
            Call(map, "Close");
            var census = File.ReadAllLines(Path.Combine(map.OutputDirectory, "heatmap_population.csv"));
            var cells = File.ReadAllLines(Path.Combine(map.OutputDirectory, "heatmap_cells.csv"));
            Require(census.Length == 7 && cells.Length == 7, "Unexpected persisted snapshot count.");
            foreach (var row in cells.Skip(1)) Require(row.Split(',').Length == 20, "Cell CSV is not culture invariant.");
            foreach (var row in census.Skip(1)) Require(row.Split(',').Length == 14, "Population CSV schema changed.");
            Require(census[1].Split(',')[1] == "0.250", "Locale changed the decimal separator.");
            for (int sample = 0; sample < 3; sample++)
            {
                int sumCells = cells.Skip(1).Select(r => r.Split(',')).Where(r => int.Parse(r[0]) == sample).Sum(r => int.Parse(r[10]));
                var floorRows = census.Skip(1).Select(r => r.Split(',')).Where(r => int.Parse(r[0]) == sample).ToArray();
                Require(sumCells == 2 && floorRows.Sum(r => int.Parse(r[12])) == sumCells, "Cell, floor and total population disagree.");
                Require(floorRows.All(r => int.Parse(r[6]) == sumCells && int.Parse(r[7]) == 4), "Census does not conserve present/absent people.");
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = oldCulture;
            foreach (var person in people) { Call(person, "OnDisable"); UnityEngine.Object.DestroyImmediate(person.gameObject); }
            UnityEngine.Object.DestroyImmediate(root);
        }
    }

    private static void ValidateParticleSwitch()
    {
        var first = new GameObject("Existing particles"); var spawned = new GameObject("Particles spawned while off");
        try
        {
            ParticleSimulationSettings.SetEnabled(true, false);
            var system = first.AddComponent<ParticleSystem>(); system.Play(); system.Emit(2);
            ParticleSimulationSettings.Register(system);
            var gate = first.GetComponent<ParticleSimulationGate>(); Call(gate, "Awake");
            ParticleSimulationSettings.SetEnabled(false, false);
            Require(system.particleCount == 0 && !system.isPlaying && !first.GetComponent<ParticleSystemRenderer>().enabled,
                "Off only hid the renderer instead of stopping particles.");
            var later = spawned.AddComponent<ParticleSystem>(); later.Play(); later.Emit(2);
            ParticleSimulationSettings.Register(later); Call(spawned.GetComponent<ParticleSimulationGate>(), "Awake");
            Require(later.particleCount == 0 && !later.isPlaying, "New particles bypassed the disabled setting.");
            // Mouth emission must not bypass the switch via explicit Emit/Play.
            first.AddComponent<AgentBodyWake>();
            var behavior = first.AddComponent<AgentBehaviorController>(); behavior.mouthParticles = system; Call(behavior, "Awake");
            Call(behavior, "OnEnable"); behavior.Cough(); Call(behavior, "Update");
            Require(system.particleCount == 0 && !system.isPlaying, "Breathing/cough restarted a disabled system.");
            ParticleSimulationSettings.SetEnabled(true, false);
            Require(system.isPlaying && later.isPlaying && first.GetComponent<ParticleSystemRenderer>().enabled, "Toggle did not resume particles.");
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(first); UnityEngine.Object.DestroyImmediate(spawned);
            ParticleSimulationSettings.SetEnabled(true, false);
        }
    }

    private static void ValidateCameraSlice()
    {
        var root = new GameObject("Floor view fixture"); var cameraObject = new GameObject("Fixture camera");
        NavMeshDataInstance instance = default;
        try
        {
            var sources = new System.Collections.Generic.List<NavMeshBuildSource>();
            for (int i = 0; i < 3; i++) sources.Add(new NavMeshBuildSource { shape = NavMeshBuildSourceShape.Box,
                size = new Vector3(12, 0.2f, 12), transform = Matrix4x4.TRS(new Vector3(0, i * 4 - 0.1f, 0), Quaternion.identity, Vector3.one), area = 0 });
            var data = NavMeshBuilder.BuildNavMeshData(NavMesh.GetSettingsByIndex(0), sources,
                new Bounds(new Vector3(0, 4, 0), new Vector3(20, 16, 20)), Vector3.zero, Quaternion.identity);
            Require(data != null, "Synthetic floor bake failed."); instance = NavMesh.AddNavMeshData(data);
            cameraObject.tag = "MainCamera"; var camera = cameraObject.AddComponent<Camera>();
            camera.transform.position = new Vector3(1, 2, 3); camera.transform.rotation = Quaternion.Euler(20, 30, 0);
            camera.nearClipPlane = 0.3f; camera.farClipPlane = 100; camera.orthographic = false;
            var position = camera.transform.position; var rotation = camera.transform.rotation;
            var orbit = cameraObject.AddComponent<CameraOrbitFollow>();
            var clock = root.AddComponent<TimeManager>(); clock.BeginSimulation();
            var map = root.AddComponent<SimulationHeatmap>(); map.floorHeights = new[] { 0f, 4f, 8f }; map.Initialize(clock);
            var view = map.FloorView; view.SetActive(true); view.SelectFloor(1);
            Require(view.Active && camera.orthographic && !orbit.enabled, "Top-down did not suspend the existing camera.");
            float y = camera.transform.position.y;
            Require(y - 4 > camera.nearClipPlane && y - 4 < camera.farClipPlane, "Selected floor was clipped.");
            Require(y - 8 < camera.nearClipPlane && y > camera.farClipPlane, "Other floors are still inside the view slice.");
            view.SelectFloor(99); Require(view.SelectedFloor == 2, "Floor selection is not clamped.");
            view.SetActive(false);
            Require(camera.transform.position == position && Quaternion.Angle(camera.transform.rotation, rotation) < 0.001f &&
                !camera.orthographic && Mathf.Approximately(camera.nearClipPlane, 0.3f) && Mathf.Approximately(camera.farClipPlane, 100) && orbit.enabled,
                "Leaving top-down did not restore the camera.");
            var shader = Resources.Load<Shader>("OccupancyHeatmap");
            Require(shader != null && !ShaderUtil.ShaderHasError(shader), "Heatmap shader is missing or has compiler errors.");
        }
        finally
        {
            if (instance.valid) instance.Remove();
            UnityEngine.Object.DestroyImmediate(root); UnityEngine.Object.DestroyImmediate(cameraObject);
        }
    }
}
