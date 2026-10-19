using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Unity.Profiling;

// Run in an isolated project copy, without -quit:
// -batchmode -executeMethod ParticlePerformanceValidation.Run
[InitializeOnLoad]
public static class ParticlePerformanceValidation
{
    private const string Active = "ParticlePerformanceValidation.Active";
    private const string Started = "ParticlePerformanceValidation.Started";
    private static SimulationFlowController flow;
    private static int phase, frames, errorCount;
    private static double runningAt;
    private static int startingMinute;
    private static readonly string[] MarkerNames = { "Simulation.ParticlePhysics", "Simulation.BodyWake", "Simulation.RecordedAirflow", "Simulation.Inhalation", "Simulation.Scheduler" };
    private static ProfilerRecorder[] recorders;
    private static double[] totals;

    static ParticlePerformanceValidation()
    {
        if (!SessionState.GetBool(Active, false)) return;
        EditorApplication.update += Tick;
        Application.logMessageReceived += Log;
    }

    public static void Run()
    {
        if (!Application.isBatchMode) throw new InvalidOperationException("Use an isolated batch editor for this play-mode fixture.");
        HeatmapValidation.Run();
        EditorSceneManager.OpenScene("Assets/McEinryScene.unity");
        var setup = UnityEngine.Object.FindFirstObjectByType<SimulationFlowController>();
        setup.defaultAgentCount = 200;
        setup.defaultAgentsEnabled = setup.defaultParticlesEnabled = true;
        setup.runWithoutRendering = false;
        setup.recordCsv = false;
        foreach (var fillers in UnityEngine.Object.FindObjectsByType<FillerAgentManager>(FindObjectsSortMode.None))
            fillers.fillersEnabled = false;
        SessionState.SetBool(Active, true);
        SessionState.SetFloat(Started, (float)EditorApplication.timeSinceStartup);
        EditorApplication.EnterPlaymode();
    }

    private static void Log(string message, string trace, LogType type)
    {
        // Unity's editor search index is unrelated to the simulation under test.
        if (trace != null && trace.Contains("UnityEditor.Search.SearchDatabase")) return;
        if (type == LogType.Exception || type == LogType.Error || type == LogType.Assert) errorCount++;
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }

    private static void Tick()
    {
        try
        {
            Require(EditorApplication.timeSinceStartup - SessionState.GetFloat(Started, 0) < 120, "200-agent startup timed out.");
            if (!EditorApplication.isPlaying || EditorApplication.isCompiling) return;
            if (flow == null) flow = UnityEngine.Object.FindFirstObjectByType<SimulationFlowController>();
            if (flow == null) return;
            if (phase == 0)
            {
                // Exercise the user's enabled option while the menu/loading gate is closed.
                ParticleSimulationSettings.SetEnabled(true, false);
                foreach (var camera in UnityEngine.Object.FindObjectsByType<Camera>(FindObjectsSortMode.None)) camera.enabled = false;
                foreach (var canvas in UnityEngine.Object.FindObjectsByType<Canvas>(FindObjectsSortMode.None)) canvas.enabled = false;
                flow.startButton.onClick.Invoke();
                phase = 1;
            }
            if (!flow.timeManager.IsRunning)
            {
                Require(!ParticleSimulationSettings.IsSimulating, "Particle physics ran during loading.");
                foreach (var agent in flow.scheduleManager.AllAgents)
                {
                    var mouth = agent.GetComponentInChildren<ParticleSystem>();
                    Require(mouth == null || mouth.particleCount == 0, "Loading agents emitted particles.");
                    var navigation = agent.GetComponent<UnityEngine.AI.NavMeshAgent>();
                    Require(navigation.isOnNavMesh, "An agent was instantiated away from the NavMesh.");
                }
                return;
            }
            if (phase == 1)
            {
                Require(flow.scheduleManager.AllAgents.Count == 200, "Did not load exactly 200 agents.");
                Require(ParticleSimulationSettings.IsSimulating, "Enabled particles did not resume after loading.");
                Require(UnityEngine.Object.FindObjectsByType<FillerAgent>(FindObjectsSortMode.None).Length == 0, "Filler agents spawned.");
                int loadedParticles = 0;
                foreach (var agent in flow.scheduleManager.AllAgents)
                {
                    var mouth = agent.GetComponentInChildren<ParticleSystem>();
                    Require(mouth != null && mouth.main.maxParticles == 31, "An agent bypassed the particle cap.");
                    mouth.Emit(mouth.main.maxParticles);
                    loadedParticles += mouth.particleCount;
                }
                Require(loadedParticles == 6200, "Could not fill all 200 systems to the 31-particle cap.");
                Debug.Log("200-AGENT PARTICLE STRESS: filled all systems to 6,200 simultaneous particles.");
                runningAt = EditorApplication.timeSinceStartup;
                startingMinute = flow.timeManager.simMinute;
                Debug.Log("200-AGENT STARTUP: day began after " + (runningAt - SessionState.GetFloat(Started, 0)).ToString("F2") + " seconds (includes play-mode reload).");
                phase = 2;
                totals = new double[MarkerNames.Length];
                recorders = new ProfilerRecorder[MarkerNames.Length];
                for (int i = 0; i < recorders.Length; i++)
                    recorders[i] = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, MarkerNames[i], 1,
                        ProfilerRecorderOptions.StartImmediately | ProfilerRecorderOptions.SumAllSamplesInFrame | ProfilerRecorderOptions.WrapAroundWhenCapacityReached);
            }
            frames++;
            for (int i = 0; i < recorders.Length; i++) totals[i] += recorders[i].LastValue / 1000000.0;
            if (EditorApplication.timeSinceStartup - runningAt < 8) return;
            Require(flow.timeManager.simMinute > startingMinute, "Simulation clock stalled after loading.");
            Require(errorCount == 0, "Play-mode run logged " + errorCount + " errors.");
            int particles = 0;
            foreach (var agent in flow.scheduleManager.AllAgents)
            {
                var mouth = agent.GetComponentInChildren<ParticleSystem>();
                Require(mouth != null && mouth.main.maxParticles == 31, "An agent bypassed the 31-particle cap.");
                particles += mouth.particleCount;
            }
            Require(particles > 0, "Active agents did not emit after the day began.");
            for (int i = 0; i < recorders.Length; i++)
                Debug.Log("PARTICLE CPU: " + MarkerNames[i] + " average " + (totals[i] / frames).ToString("F2") + " ms per observed update.");
            Debug.Log("PARTICLE STARTUP VALIDATION PASSED: 200 agents, no fillers, zero loading particles, valid spawn positions, day started and clock advanced, " +
                frames + " observed updates over eight running seconds after starting at the full 6,200-particle cap, " + particles + " live particles, no simulation errors. Cameras disabled; this is a startup/CPU smoke test, not a rendered FPS benchmark.");
            Finish(0);
        }
        catch (Exception e) { Debug.LogException(e); Finish(1); }
    }

    private static void Finish(int code)
    {
        if (recorders != null) foreach (var recorder in recorders) recorder.Dispose();
        SessionState.SetBool(Active, false);
        EditorApplication.update -= Tick;
        Application.logMessageReceived -= Log;
        EditorApplication.Exit(code);
    }
}
