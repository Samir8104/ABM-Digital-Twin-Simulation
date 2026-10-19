using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using ABMU.Core;
using UnityEditor;
using UnityEngine;

public static class SimulationPerformanceValidation
{
    private const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    private sealed class Probe : Stepper
    {
        private readonly Action callback;
        public Probe(int step, int priority, Action action) : base(step, () => { }, priority, null) { callback = action; }
        public override void Step() => callback();
    }

    public static void Run()
    {
        if (!Application.isBatchMode) throw new InvalidOperationException("Use an isolated batch editor for these fixtures.");
        ValidateScheduler();
        ValidateNavigationCancellation();
        ValidateCaptureGrid();
        ValidateCsv();
        AgentInteractionValidation.Run();
        Debug.Log("SIMULATION PERFORMANCE VALIDATION PASSED: scheduler cadence/order/removal, exact capture grid, split CSVs and pooled IDs, airflow and particle regressions.");
    }

    private static int navigationCalls;
    private static void Move() => navigationCalls++;
    private static void ValidateNavigationCancellation()
    {
        var controllerObject = new GameObject("Cancellation controller");
        var agentObject = new GameObject("Cancellation agent");
        try
        {
            var controller = controllerObject.AddComponent<AbstractController>(); controller.Init();
            var agent = agentObject.AddComponent<NavigationAgent>(); agent.Init();
            typeof(Steppable).GetField("_controller", Hidden).SetValue(agent, controller);
            var create = typeof(NavigationAgent).GetMethod("SafeCreateStepper", Hidden);
            var remove = typeof(NavigationAgent).GetMethod("SafeDestroyStepper", Hidden);
            for (int i = 0; i < 100; i++)
            {
                create.Invoke(agent, new object[] { "Move", (ABMU.Utilities.Del)Move, 1, 105 });
                remove.Invoke(agent, new object[] { "Move" });
            }
            Require(agent.steppers.Count == 0, "Cancelled navigation steppers accumulated on the agent.");
            navigationCalls = 0;
            create.Invoke(agent, new object[] { "Move", (ABMU.Utilities.Del)Move, 1, 105 });
            controller.scheduler.Tick();
            Require(navigationCalls == 1, "Recreating a cancelled navigation stepper revived duplicate callbacks.");
            remove.Invoke(agent, new object[] { "Move" }); controller.scheduler.Tick();
        }
        finally { UnityEngine.Object.DestroyImmediate(agentObject); UnityEngine.Object.DestroyImmediate(controllerObject); }
    }

    private static void ValidateScheduler()
    {
        var scheduler = new Scheduler();
        var observed = new List<int>();
        int tick = 0;
        var every = new Probe(1, 100, () => observed.Add(tick * 1000 + 100));
        var two = new Probe(2, 105, () => observed.Add(tick * 1000 + 105));
        var three = new Probe(3, 110, () => observed.Add(tick * 1000 + 110));
        var late = new Probe(1, 800, () => observed.Add(tick * 1000 + 800));
        scheduler.RegisterStepper(late); scheduler.RegisterStepper(two);
        scheduler.RegisterStepper(three); scheduler.RegisterStepper(every);
        for (tick = 0; tick < 12; tick++) scheduler.Tick();
        var expected = new List<int>();
        for (int t = 0; t < 12; t++)
        {
            expected.Add(t * 1000 + 100);
            if (t % 2 == 1) expected.Add(t * 1000 + 105);
            if (t % 3 == 2) expected.Add(t * 1000 + 110);
            expected.Add(t * 1000 + 800);
        }
        Require(string.Join(",", observed) == string.Join(",", expected), "Stepper cadence or priority changed: " + string.Join(",", observed));
        var mixed = new Scheduler();
        int countTwo = 0, countThree = 0;
        mixed.RegisterStepper(new Probe(2, 7, () => countTwo++));
        mixed.RegisterStepper(new Probe(3, 7, () => countThree++));
        for (int i = 0; i < 60; i++) mixed.Tick();
        Require(countTwo == 30 && countThree == 20, "Mixed intervals at the same priority lost ticks.");
        scheduler.DeregisterStepper(two); scheduler.DeregisterStepper(three);
        observed.Clear();
        for (tick = 12; tick < 18; tick++) scheduler.Tick();
        Require(observed.Count == 12, "Removed steppers still run.");
        int removedRuns = 0;
        var victim = new Probe(1, 200, () => removedRuns++);
        var remover = new Probe(1, 150, () => scheduler.DeregisterStepper(victim));
        scheduler.RegisterStepper(victim); scheduler.RegisterStepper(remover); scheduler.Tick();
        Require(removedRuns == 0, "Removal during a tick did not prevent later execution.");
        scheduler.DeregisterStepper(remover); scheduler.Tick();
        var cancelled = new Probe(2, 110, () => throw new Exception("Cancelled stepper ran."));
        scheduler.RegisterStepper(cancelled); scheduler.DeregisterStepper(cancelled);
        scheduler.Tick(); scheduler.Tick(); scheduler.Tick();
    }

    private static void ValidateCaptureGrid()
    {
        var roots = new List<GameObject>();
        var receivers = new List<AgentHealth>();
        var random = new System.Random(321);
        try
        {
            for (int i = 0; i < 40; i++)
            {
                var go = new GameObject("Capture fixture"); roots.Add(go);
                go.transform.position = new Vector3((float)random.NextDouble() * 4 - 2,
                    (float)random.NextDouble() * 2 - 1, (float)random.NextDouble() * 4 - 2);
                go.AddComponent<ParticleSystem>();
                var receiver = go.AddComponent<AgentHealth>();
                typeof(AgentHealth).GetMethod("Awake", Hidden).Invoke(receiver, null);
                receiver.inhalationRadius = 0.01f + (float)random.NextDouble() * 0.49f;
                typeof(AgentHealth).GetMethod("OnEnable", Hidden).Invoke(receiver, null);
                receivers.Add(receiver);
            }
            typeof(AgentHealth).GetMethod("BuildGrid", Hidden).Invoke(null, null);
            var find = typeof(AgentHealth).GetMethod("FindReceiver", Hidden);
            var cell = typeof(AgentHealth).GetMethod("Cell", Hidden);
            for (int i = 0; i < 2000; i++)
            {
                var p = receivers[i % receivers.Count].transform.position + new Vector3(
                    (float)random.NextDouble() - 0.5f, (float)random.NextDouble() - 0.5f, (float)random.NextDouble() - 0.5f);
                var source = receivers[(i + 1) % receivers.Count];
                AgentHealth expected = null; float nearest = float.PositiveInfinity;
                foreach (var receiver in receivers)
                {
                    float d = (p - receiver.transform.position).sqrMagnitude;
                    if (receiver != source && d <= receiver.inhalationRadius * receiver.inhalationRadius && d < nearest)
                    { expected = receiver; nearest = d; }
                }
                var actual = find.Invoke(null, new[] { (object)p, cell.Invoke(null, new[] { (object)p }), source });
                Require(ReferenceEquals(actual, expected), "Capture grid missed a receiver or chose the wrong agent.");
            }
        }
        finally { foreach (var go in roots) UnityEngine.Object.DestroyImmediate(go); }
    }

    private static void ValidateCsv()
    {
        var go = new GameObject("CSV fixture");
        var agentObject = new GameObject("CSV agent");
        try
        {
            agentObject.AddComponent<ParticleSystem>();
            var agent = agentObject.AddComponent<AgentHealth>();
            typeof(AgentHealth).GetMethod("Awake", Hidden).Invoke(agent, null);
            typeof(AgentHealth).GetMethod("OnEnable", Hidden).Invoke(agent, null);
            agent.ResetForSpawn(true);
            var clock = go.AddComponent<TimeManager>(); clock.BeginSimulation();
            var recorder = go.AddComponent<SimulationCsvRecorder>();
            recorder.Begin(clock, 1, Path.GetFullPath(Path.Combine(Application.dataPath, "../CsvValidation")));
            var capture = typeof(SimulationCsvRecorder).GetMethod("Capture", Hidden);
            capture.Invoke(recorder, new object[] { 0.25f });
            agent.ResetForSpawn(false);
            capture.Invoke(recorder, new object[] { 1.25f });
            typeof(SimulationCsvRecorder).GetMethod("Close", Hidden).Invoke(recorder, null);
            foreach (string file in new[] { "positions.csv", "health.csv", "activity.csv" })
            {
                var lines = File.ReadAllLines(Path.Combine(recorder.OutputDirectory, file));
                Require(lines.Length == 3, "Unexpected CSV row count: " + file);
                Require(lines[1].Split(',').Length == lines[0].Split(',').Length, "Malformed CSV row: " + file);
                Require(lines[1].Split(',')[4] != lines[2].Split(',')[4], "Pooled agent ID was reused.");
                Require(lines[1].Split(',')[1] == "0.250", "CSV decimal format is locale-dependent.");
            }
            var rows = File.ReadAllLines(Path.Combine(recorder.OutputDirectory, "health.csv"));
            Require(rows[1].Split(',')[6] == "1" && rows[2].Split(',')[6] == "0", "Infection snapshots incorrect.");
        }
        finally { UnityEngine.Object.DestroyImmediate(go); UnityEngine.Object.DestroyImmediate(agentObject); }
    }
}
