using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

// Buffered snapshots, independent of rendering. Late order includes inhalation
// performed this frame. A pooled agent gets a new ID when ResetForSpawn runs.
[DefaultExecutionOrder(200)]
public class SimulationCsvRecorder : MonoBehaviour
{
    public string OutputDirectory { get; private set; }
    private TimeManager clock;
    private StreamWriter positions, health, activity;
    private float interval, nextSample, nextFlush, startTime;
    private int sampleIndex, nextAgentId;
    private readonly Dictionary<AgentHealth, (int generation, int id, NavigationAgent navigation)> identities = new();
    private readonly StringBuilder positionRows = new();
    private readonly StringBuilder healthRows = new();
    private readonly StringBuilder activityRows = new();
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    public void Begin(TimeManager timeManager, float sampleInterval, string outputRoot = null)
    {
        Close();
        clock = timeManager;
        interval = Mathf.Max(0.1f, sampleInterval);
        startTime = nextSample = Time.unscaledTime;
        nextFlush = startTime + 5f;
        sampleIndex = nextAgentId = 0;
        identities.Clear();
        try
        {
            OutputDirectory = Path.Combine(outputRoot ?? Application.persistentDataPath, "SimulationRuns",
                DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff", Culture) + "_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(OutputDirectory);
            positions = Open("positions.csv", "sample_id,elapsed_seconds,sim_day,sim_minute,agent_id,present,x,y,z");
            health = Open("health.csv", "sample_id,elapsed_seconds,sim_day,sim_minute,agent_id,present,infected,inhaled_particles,inhaled_infected_particles");
            activity = Open("activity.csv", "sample_id,elapsed_seconds,sim_day,sim_minute,agent_id,present,activity");
            Debug.Log("[SimulationCsvRecorder] Recording to " + OutputDirectory);
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
        {
            Close();
            Debug.LogError("[SimulationCsvRecorder] Could not open output files: " + e.Message);
        }
    }

    private StreamWriter Open(string file, string header)
    {
        var writer = new StreamWriter(Path.Combine(OutputDirectory, file), false, new UTF8Encoding(false), 65536);
        writer.WriteLine(header);
        writer.Flush();
        return writer;
    }

    private void LateUpdate()
    {
        if (positions == null || clock == null || !clock.IsRunning) return;
        float now = Time.unscaledTime;
        if (now < nextSample) return;
        // Record actual snapshots once; avoid a disk catch-up burst after a stall.
        nextSample = now + interval;
        try
        {
            Capture(now - startTime);
            if (now >= nextFlush) { Flush(); nextFlush = now + 5f; }
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
        {
            Close();
            Debug.LogError("[SimulationCsvRecorder] Recording stopped: " + e.Message);
        }
    }

    private void Capture(float elapsed)
    {
        positionRows.Clear(); healthRows.Clear(); activityRows.Clear();
        foreach (var agent in AgentHealth.RegisteredAgents)
        {
            if (agent == null) continue;
            if (!identities.TryGetValue(agent, out var identity) || identity.generation != agent.SpawnGeneration)
            {
                identity = (agent.SpawnGeneration, ++nextAgentId, agent.GetComponent<NavigationAgent>());
                identities[agent] = identity;
            }
            string common = sampleIndex.ToString(Culture) + "," + elapsed.ToString("F3", Culture) + "," +
                clock.CurrentDay.ToString(Culture) + "," + (clock.CurrentHour * 60 + clock.CurrentMinute).ToString(Culture) + "," +
                identity.id.ToString(Culture) + "," + (agent.IsPresent ? "1," : "0,");
            Vector3 p = agent.transform.position;
            positionRows.Append(common).Append(p.x.ToString("R", Culture)).Append(',')
                .Append(p.y.ToString("R", Culture)).Append(',').Append(p.z.ToString("R", Culture)).AppendLine();
            healthRows.Append(common).Append(agent.IsInfected ? "1," : "0,")
                .Append(agent.InhaledParticles.ToString(Culture)).Append(',')
                .Append(agent.InhaledInfectedParticles.ToString(Culture)).AppendLine();
            activityRows.Append(common).Append(identity.navigation?.Schedule?.CurrentActivity.ToString() ?? "Unknown").AppendLine();
        }
        positions.Write(positionRows.ToString());
        health.Write(healthRows.ToString());
        activity.Write(activityRows.ToString());
        sampleIndex++;
    }

    private void Flush() { positions?.Flush(); health?.Flush(); activity?.Flush(); }
    private void OnApplicationPause(bool paused) { if (paused) Flush(); }
    private void OnDisable() => Close();
    private void OnApplicationQuit() => Close();
    private void Close()
    {
        // Attempt all three closes even if a disk error affects one of them.
        Dispose(ref positions); Dispose(ref health); Dispose(ref activity);
    }
    private static void Dispose(ref StreamWriter writer)
    {
        var closing = writer; writer = null;
        try { closing?.Dispose(); }
        catch (IOException e) { Debug.LogError("[SimulationCsvRecorder] File close failed: " + e.Message); }
    }
}
