using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;

// Run in an isolated batch project, without -quit:
// -executeMethod NavigationBehaviorValidation.Run
[InitializeOnLoad]
public static class NavigationBehaviorValidation
{
    const string Pending = "NavigationBehaviorValidation.Pending";
    const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    static IEnumerator scenario;
    static int lastFrame = -1;
    static double deadline;
    static int errors;
    static NavigationBehaviorValidation()
    {
        if (SessionState.GetBool(Pending, false))
        {
            EditorApplication.update += Tick;
            Application.logMessageReceived += Log;
        }
    }
    static void Log(string message, string trace, LogType type)
    {
        if (trace != null && trace.Contains("UnityEditor.Search.SearchDatabase")) return;
        if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert) errors++;
    }
    static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    static object Call(object target, string method, params object[] args) => target.GetType().GetMethod(method, Hidden).Invoke(target, args);
    static void Set(object target, string field, object value) => target.GetType().GetField(field, Hidden).SetValue(target, value);
    static void SetTime(TimeManager time, double elapsed) => Set(time, "<ElapsedSimulationSeconds>k__BackingField", elapsed);

    public static void Run()
    {
        Require(Application.isBatchMode, "Use an isolated batch editor.");
        try
        {
            EditorSceneManager.OpenScene("Assets/McEinryScene.unity");
            var navigation = UnityEngine.Object.FindFirstObjectByType<NavigationController>(); navigation.Init();
            var flow = UnityEngine.Object.FindFirstObjectByType<SimulationFlowController>();
            flow.runWithoutRendering = false;
            Call(flow, "Awake");
            Require(flow.bodyWakeSlider != null && flow.bodyWakeSlider.minValue == 0f && flow.bodyWakeSlider.maxValue == 1f,
                "Main menu is missing the body wake slider.");
            float saved = ParticleSimulationSettings.BodyWakeStrength;
            try
            {
                flow.bodyWakeSlider.value = 0f;
                Require(ParticleSimulationSettings.BodyWakeStrength == 0f, "Left slider endpoint did not disable body wake.");
                flow.bodyWakeSlider.value = 1f;
                Require(ParticleSimulationSettings.BodyWakeStrength == 1f, "Right slider endpoint did not restore authored strength.");
            }
            finally { flow.bodyWakeSlider.value = saved; }
            string output = Path.GetFullPath(".utmp/navigation-validation"); Directory.CreateDirectory(output);
            typeof(HeatmapValidation).GetMethod("ValidateAndRenderMenu", BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, new object[] { flow, output });
            int samples = 0;
            foreach (var tag in new[] { "Bathroom", "Studying", "OfficeHours" })
                foreach (var node in GameObject.FindGameObjectsWithTag(tag))
                    for (int i = 0; i < 32; i++)
                    {
                        Vector3 point = navigation.GetRandomPointInRoom(node);
                        Require(NavMesh.SamplePosition(point, out var hit, 0.05f, NavMesh.AllAreas) &&
                            Vector3.Distance(point, hit.position) < 0.05f, "Activity target is off the NavMesh: " + node.name);
                        samples++;
                    }
            Require(samples > 0, "No actual building activity nodes were checked.");
            Debug.Log("NAVIGATION MENU AND MODEL TARGETS PASSED: " + samples + " activity samples; slider endpoints and menu render: " + output);
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            SessionState.SetBool(Pending, true);
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
            EditorApplication.isPlaying = true;
        }
        catch (Exception error) { Debug.LogException(error); EditorApplication.Exit(1); }
    }

    static void Tick()
    {
        if (!EditorApplication.isPlaying || !SessionState.GetBool(Pending, false)) return;
        if (Time.frameCount == lastFrame) return;
        lastFrame = Time.frameCount;
        try
        {
            if (scenario == null) { scenario = ExerciseMovement(); deadline = EditorApplication.timeSinceStartup + 450; }
            Require(EditorApplication.timeSinceStartup < deadline, "Movement validation timed out.");
            if (scenario.MoveNext()) return;
            Require(errors == 0, "Navigation run logged " + errors + " simulation errors.");
            SessionState.SetBool(Pending, false);
            Debug.Log("NAVIGATION BEHAVIOR VALIDATION PASSED: immediate departures, actual movement animation, room stays, rollover, spaced and bounded reservations, boarding, timeout, pooled reuse, and wake scaling.");
            EditorApplication.Exit(0);
        }
        catch (Exception error)
        {
            SessionState.SetBool(Pending, false); Debug.LogException(error); EditorApplication.Exit(1);
        }
    }

    static NavigationAgent Person(NavigationController controller, GameObject room, Vector3 position)
    {
        var go = new GameObject("Movement fixture person"); go.transform.position = position;
        var nav = go.AddComponent<NavMeshAgent>(); nav.radius = 0.25f; nav.speed = 4f; nav.baseOffset = 0.5f;
        var person = go.AddComponent<NavigationAgent>();
        var model = new GameObject("Model"); model.transform.SetParent(go.transform);
        Set(person, "meshRenderer", model.AddComponent<SkinnedMeshRenderer>());
        var mouth = new GameObject("Mouth"); mouth.transform.SetParent(go.transform);
        var particles = mouth.AddComponent<ParticleSystem>(); particles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        Set(person, "mouth", particles);
        person.AssignNewSchedule(new AgentSchedule(), room, position);
        return person;
    }

    static IEnumerator ExerciseMovement()
    {
        var sources = new List<NavMeshBuildSource> { new NavMeshBuildSource {
            shape = NavMeshBuildSourceShape.Box, size = new Vector3(50, 0.2f, 50),
            transform = Matrix4x4.TRS(new Vector3(0, -0.1f, 0), Quaternion.identity, Vector3.one), area = 0 } };
        var data = NavMeshBuilder.BuildNavMeshData(NavMesh.GetSettingsByIndex(0), sources,
            new Bounds(Vector3.zero, new Vector3(60, 5, 60)), Vector3.zero, Quaternion.identity);
        Require(data != null, "Fixture NavMesh build failed."); var meshInstance = NavMesh.AddNavMeshData(data);
        var time = new GameObject("Clock").AddComponent<TimeManager>(); time.enabled = false; time.BeginSimulation();
        ParticleSimulationSettings.SetSimulationRunning(true);
        var controller = new GameObject("Navigation").AddComponent<NavigationController>();
        // Awake has initialized the scheduler, but station references need refreshing below.
        var elevator = new GameObject("Elevator").AddComponent<ElevatorController>();
        elevator.floorYPositions = new[] { 0f, 4f }; elevator.doorDwellTime = 1f; elevator.travelSpeed = 20f;
        var station = new GameObject("Landing").AddComponent<ElevatorCallStation>();
        station.transform.position = new Vector3(5, 0, 5); station.elevatorController = elevator; station.maxWaitingAgents = 3;
        elevator.callStations = new[] { station };
        controller.Init();
        var room = new GameObject("Study marker"); room.transform.position = new Vector3(8, 0, 0);
        var person = Person(controller, room, Vector3.zero);
        var nav = person.GetComponent<NavMeshAgent>();
        Require(!nav.enabled, "Idle pool member still blocks spawn avoidance.");
        person.Schedule.SetActivity(AgentActivity.GoingToStudying); person.SetTarget(room);
        Vector3 start = person.transform.position;
        float until = Time.time + 0.35f;
        while (Time.time < until) yield return null;
        Require(Vector3.Distance(start, person.transform.position) > 0.25f, "New path stalled at spawn.");
        until = Time.time + 8f;
        while (person.Schedule.CurrentActivity != AgentActivity.InStudying && Time.time < until) yield return null;
        Require(person.Schedule.CurrentActivity == AgentActivity.InStudying, "Studying target never arrived.");
        var rotation = person.transform.rotation; var position = person.transform.position;
        until = Time.time + 0.4f; while (Time.time < until) yield return null;
        Require(Quaternion.Angle(rotation, person.transform.rotation) < 0.1f && Vector3.Distance(position, person.transform.position) < 0.05f,
            "Stopped activity drifted or spun.");

        // A stay starting before the clock wraps must still end afterwards.
        SetTime(time, 100); Call(person, "StartTimedStay", 2);
        Set(time, "<CurrentHour>k__BackingField", 0); SetTime(time, 221);
        person.Schedule.SetActivity(AgentActivity.InBathroom);
        Call(person, "StayInPlace");
        Require(person.Schedule.CurrentActivity != AgentActivity.InBathroom, "Bathroom stay survived its deadline across day rollover.");

        var people = new List<NavigationAgent>(); var slots = new List<Vector3>();
        for (int i = 0; i < 3; i++)
        {
            var other = Person(controller, room, new Vector3(-6, 0, -4 + i * 2));
            other.Schedule.SetActivity(AgentActivity.GoingToClass);
            Call(other, "StartElevatorJourney", room, 1, station);
            Require(other.HasElevatorReservation(station), "Could not reserve reachable landing slot.");
            slots.Add((Vector3)typeof(NavigationAgent).GetField("target", Hidden).GetValue(other));
            people.Add(other);
        }
        Require(station.ReservedCount == 3 && !station.IsElevatorViable(), "Approaching agents were not included in queue capacity.");
        for (int i = 0; i < slots.Count; i++) for (int j = i + 1; j < slots.Count; j++)
            Require(Vector3.Distance(slots[i], slots[j]) >= 1f, "Landing slots overlap.");
        var overflow = Person(controller, room, new Vector3(-6, 0, 4));
        Call(overflow, "StartElevatorJourney", room, 1, station);
        Require(!overflow.HasElevatorReservation(station) && station.ReservedCount == 3, "Overflow did not take stairs.");

        // Force one arrived ticket to board, without waiting for fixture travel.
        Set(people[0], "_pendingCallStation", null); Set(people[0], "_waitingCallStation", station);
        Set(elevator, "_acceptingPassengers", true);
        Require(station.TryRegisterWaitingAgent(people[0], 1), "Reserved passenger rejected at arrival.");
        Require(elevator.RiderCount == 1 && station.ReservedCount == 2 && !people[0].GetComponent<NavMeshAgent>().enabled,
            "Boarded rider retained a queue ticket or landing avoidance body.");
        Set(elevator, "_acceptingPassengers", false);
        people[0].AssignNewSchedule(new AgentSchedule(), room, new Vector3(-10, 0, 0));
        Require(elevator.RiderCount == 0, "Pool reset left a rider in the cage.");
        people[1].AssignNewSchedule(new AgentSchedule(), room, new Vector3(-10, 0, 2));
        Require(station.ReservedCount == 1, "Pool reset left a stale elevator ticket.");
        Call(station, "OnDisable");
        Require(station.ReservedCount == 0 && !people[2].HasElevatorReservation(station), "Unavailable elevator left agents waiting.");
        station.maximumWaitSeconds = 0.2f;
        Call(people[2], "StartElevatorJourney", room, 1, station);
        Require(station.ReservedCount == 1, "Timeout fixture did not reserve a place.");
        until = Time.time + 0.9f; while (Time.time < until) yield return null;
        Require(station.ReservedCount == 0 && !people[2].HasElevatorReservation(station), "Expired elevator ticket did not reroute to stairs.");

        // Reuse the same body repeatedly, covering the state that used to leak
        // over long runs. Each departure must move on its first navigation frames.
        for (int day = 1; day <= 4; day++)
        {
            foreach (var activity in new[] { AgentActivity.GoingToBathroom, AgentActivity.GoingToOfficeHours, AgentActivity.GoingToStudying })
            {
                person.AssignNewSchedule(new AgentSchedule(), room, new Vector3(-8, 0, -8));
                Set(time, "<CurrentDay>k__BackingField", day);
                person.Schedule.SetActivity(activity); person.SetTarget(room);
                start = person.transform.position;
                until = Time.time + 0.25f; while (Time.time < until) yield return null;
                Require(Vector3.Distance(start, person.transform.position) > 0.15f, "Reused body stalled on day " + day + ": " + activity);
            }
        }

        var wakeObject = new GameObject("Wake scaling fixture"); var wake = wakeObject.AddComponent<AgentBodyWake>();
        wake.smoothing = 0.01f; wakeObject.transform.position += Vector3.right * 0.1f; Call(wake, "AdvanceWake", 0.1f);
        Vector3 body = wakeObject.transform.position + Vector3.up * wake.bodyHeight;
        ParticleSimulationSettings.SetBodyWakeStrength(1f, false); Vector3 full = wake.SampleLocalVelocity(body);
        ParticleSimulationSettings.SetBodyWakeStrength(0.5f, false); Vector3 half = wake.SampleLocalVelocity(body);
        ParticleSimulationSettings.SetBodyWakeStrength(0f, false);
        Require(full.sqrMagnitude > 0.01f && Vector3.Distance(half, full * 0.5f) < 0.00001f && wake.SampleLocalVelocity(body) == Vector3.zero,
            "Body wake strength is not linear or does not fully turn off.");
        wake.SetBreath(1.5f);
        Require(wake.SampleLocalVelocity(wakeObject.transform.position + Vector3.up * 1.6f + Vector3.forward * 0.2f).z > 0,
            "Disabling body wake also disabled respiration.");
        ParticleSimulationSettings.SetBodyWakeStrength(1f, false);
        meshInstance.Remove(); UnityEngine.Object.Destroy(data);
        Debug.Log("FOCUSED NAVIGATION CHECKS PASSED; beginning three-day, 200-agent building soak at 12x simulation speed.");
        var soak = ExerciseBuilding();
        while (soak.MoveNext()) yield return null;
    }

    static IEnumerator ExerciseBuilding()
    {
        EditorSceneManager.LoadSceneInPlayMode("Assets/McEinryScene.unity", new UnityEngine.SceneManagement.LoadSceneParameters(UnityEngine.SceneManagement.LoadSceneMode.Single));
        SimulationFlowController flow;
        while ((flow = UnityEngine.Object.FindFirstObjectByType<SimulationFlowController>()) == null) yield return null;
        flow.recordCsv = false;
        flow.agentCountField.text = "200";
        flow.agentToggle.isOn = true;
        foreach (var fillers in UnityEngine.Object.FindObjectsByType<FillerAgentManager>(FindObjectsSortMode.None)) fillers.fillersEnabled = false;
        foreach (var camera in UnityEngine.Object.FindObjectsByType<Camera>(FindObjectsSortMode.None)) camera.enabled = false;
        foreach (var canvas in UnityEngine.Object.FindObjectsByType<Canvas>(FindObjectsSortMode.None)) canvas.enabled = false;
        ParticleSimulationSettings.SetEnabled(false, false);
        yield return null;
        flow.startButton.onClick.Invoke();
        while (!flow.timeManager.IsRunning) yield return null;
        flow.timeManager.SetSimSpeed(12f);
        int firstDay = flow.timeManager.CurrentDay;
        int reportedDay = firstDay - 1;
        var previous = new Dictionary<NavigationAgent, (Vector3 position, Quaternion rotation, AgentActivity activity)>();
        var activities = new HashSet<AgentActivity>();
        int observations = 0, peakQueue = 0;
        float nextSample = Time.time;
        while (flow.timeManager.CurrentDay < firstDay + 3)
        {
            yield return null;
            if (Time.time < nextSample) continue;
            nextSample = Time.time + 0.25f;
            if (reportedDay != flow.timeManager.CurrentDay)
            {
                reportedDay = flow.timeManager.CurrentDay;
                Debug.Log("BUILDING NAVIGATION SOAK: day " + reportedDay + ", bodies=" + flow.scheduleManager.AllAgents.Count + ", peak reserved queue=" + peakQueue);
            }
            foreach (var station in UnityEngine.Object.FindObjectsByType<ElevatorCallStation>(FindObjectsSortMode.None))
            {
                Require(station.ReservedCount <= station.maxWaitingAgents && station.WaitingCount <= station.ReservedCount,
                    "Building elevator queue exceeded its reservations/capacity.");
                peakQueue = Mathf.Max(peakQueue, station.ReservedCount);
            }
            foreach (var agent in flow.scheduleManager.AllAgents)
            {
                if (agent == null || agent.Schedule == null) continue;
                var activity = agent.Schedule.CurrentActivity; activities.Add(activity);
                bool staying = activity == AgentActivity.InBathroom || activity == AgentActivity.InOfficeHours || activity == AgentActivity.InStudying;
                if (staying && previous.TryGetValue(agent, out var old) && old.activity == activity &&
                    Vector3.Distance(old.position, agent.transform.position) < 0.01f)
                    Require(Quaternion.Angle(old.rotation, agent.transform.rotation) < 0.5f, "Stationary building activity spun: " + activity);
                previous[agent] = (agent.transform.position, agent.transform.rotation, activity);
                observations++;
            }
            Require(errors == 0, "Building soak logged " + errors + " simulation errors.");
        }
        Require(observations > 1000 && activities.Contains(AgentActivity.InClass) && activities.Contains(AgentActivity.InBathroom) &&
            activities.Contains(AgentActivity.InOfficeHours), "Building soak did not exercise the requested activities: " + string.Join(",", activities));
        Debug.Log("THREE-DAY BUILDING SOAK PASSED: 200 pooled bodies, " + observations + " observations; peak reserved landing queue=" + peakQueue +
            "; activities=" + string.Join(",", activities) + ". Particles/rendering disabled for the movement soak; wake and UI checked separately.");
    }
}
