using System;
using System.Collections.Generic;
using UnityEngine;

// Counts people, never colliders. Samples are observations, not distinct people.
public sealed class HeatmapGrid
{
    public sealed class Cell
    {
        public int Occupants;
        public long PersonSamples;
        public long Entries;
        public long WalkingEntries;
        public double PersonSeconds;
        public double SimulatedPersonSeconds;
        public readonly HashSet<int> Visitors = new();
    }

    public readonly Dictionary<Vector3Int, Cell> Cells = new();
    public readonly float[] FloorHeights;
    public readonly float CellSize;
    public int SampleCount { get; private set; }
    public long MaximumFrequency { get; private set; }
    public long MaximumEntries { get; private set; }
    public long Revision { get; private set; }
    public long StatisticsRevision { get; private set; }
    public double SimulatedSeconds { get; private set; }
    public double ObservedRealSeconds { get; private set; }
    public double ExcludedTransitionPersonSeconds { get; private set; }
    public double LastSnapshotSimulatedSeconds { get; private set; }

    public HeatmapGrid(float[] floors, float cellSize)
    {
        if (floors == null || floors.Length == 0) throw new ArgumentException("At least one floor is required.");
        FloorHeights = (float[])floors.Clone();
        Array.Sort(FloorHeights);
        CellSize = Mathf.Max(0.25f, cellSize);
    }

    public int FloorAt(float y)
    {
        int best = 0;
        for (int i = 1; i < FloorHeights.Length; i++)
            if (Mathf.Abs(y - FloorHeights[i]) < Mathf.Abs(y - FloorHeights[best])) best = i;
        return best;
    }

    public Vector3Int Key(Vector3 p) => new(Mathf.FloorToInt(p.x / CellSize), FloorAt(p.y), Mathf.FloorToInt(p.z / CellSize));

    public void BeginSample()
    {
        foreach (var cell in Cells.Values) cell.Occupants = 0;
        SampleCount++;
        LastSnapshotSimulatedSeconds = SimulatedSeconds;
        StatisticsRevision++;
    }

    public void Observe(Vector3 position, int personId, double elapsedSeconds)
    {
        var key = Key(position);
        if (!Cells.TryGetValue(key, out var cell)) Cells.Add(key, cell = new Cell());
        cell.Occupants++;
        cell.PersonSamples++;
        cell.PersonSeconds += Math.Max(0, elapsedSeconds);
        cell.Visitors.Add(personId);
        MaximumFrequency = Math.Max(MaximumFrequency, cell.PersonSamples);
    }

    public void RecordArrival(Vector3 position, int personId) => Enter(Key(position), personId);

    public void AdvanceTime(double realSeconds, double simulatedSeconds)
    {
        ObservedRealSeconds += Math.Max(0, realSeconds);
        SimulatedSeconds += Math.Max(0, simulatedSeconds);
        StatisticsRevision++;
    }

    // Integrate each frame's person-time along its observed straight segment.
    // Every crossed cell receives only its fraction of the simulated interval.
    public void RecordDuration(Vector3 from, Vector3 to, double seconds, bool continuous = true)
    {
        if (seconds <= 0) return;
        var current = Key(from); var end = Key(to);
        if (!continuous || current.y != end.y) { ExcludedTransitionPersonSeconds += seconds; return; }
        double dx = to.x - from.x, dz = to.z - from.z;
        int stepX = Math.Sign(dx), stepZ = Math.Sign(dz);
        double deltaX = stepX == 0 ? double.PositiveInfinity : CellSize / Math.Abs(dx);
        double deltaZ = stepZ == 0 ? double.PositiveInfinity : CellSize / Math.Abs(dz);
        double nextX = stepX == 0 ? double.PositiveInfinity : ((current.x + (stepX > 0 ? 1 : 0)) * (double)CellSize - from.x) / dx;
        double nextZ = stepZ == 0 ? double.PositiveInfinity : ((current.z + (stepZ > 0 ? 1 : 0)) * (double)CellSize - from.z) / dz;
        double previous = 0;
        int remaining = Math.Abs(end.x - current.x) + Math.Abs(end.z - current.z) + 2;
        while (remaining-- > 0)
        {
            double next = current == end ? 1 : Math.Min(1, Math.Min(nextX, nextZ));
            if (!Cells.TryGetValue(current, out var cell)) Cells.Add(current, cell = new Cell());
            cell.SimulatedPersonSeconds += seconds * Math.Max(0, next - previous);
            if (next >= 1) break;
            previous = next;
            if (Math.Abs(nextX - nextZ) < 1e-9)
            { current.x += stepX; current.z += stepZ; nextX += deltaX; nextZ += deltaZ; }
            else if (nextX < nextZ) { current.x += stepX; nextX += deltaX; }
            else { current.z += stepZ; nextZ += deltaZ; }
        }
        StatisticsRevision++;
    }

    // Traverse every crossed grid cell, including negative coordinates. This
    // records short hallway passages between disk snapshots. A continuous stay
    // counts once; leaving and returning counts another entry.
    public void RecordMovement(Vector3 from, Vector3 to, int personId, bool continuous = true)
    {
        var current = Key(from);
        var end = Key(to);
        if (!continuous || current.y != end.y) { Enter(end, personId); return; }
        if (current == end) return;
        double dx = to.x - from.x, dz = to.z - from.z;
        int stepX = Math.Sign(dx), stepZ = Math.Sign(dz);
        double deltaX = stepX == 0 ? double.PositiveInfinity : CellSize / Math.Abs(dx);
        double deltaZ = stepZ == 0 ? double.PositiveInfinity : CellSize / Math.Abs(dz);
        double nextX = stepX == 0 ? double.PositiveInfinity :
            ((current.x + (stepX > 0 ? 1 : 0)) * (double)CellSize - from.x) / dx;
        double nextZ = stepZ == 0 ? double.PositiveInfinity :
            ((current.z + (stepZ > 0 ? 1 : 0)) * (double)CellSize - from.z) / dz;
        int remaining = Math.Abs(end.x - current.x) + Math.Abs(end.z - current.z);
        while (current != end && remaining-- > 0)
        {
            if (Math.Abs(nextX - nextZ) < 1e-9)
            { current.x += stepX; current.z += stepZ; nextX += deltaX; nextZ += deltaZ; }
            else if (nextX < nextZ) { current.x += stepX; nextX += deltaX; }
            else { current.z += stepZ; nextZ += deltaZ; }
            Enter(current, personId, true);
        }
    }

    private void Enter(Vector3Int key, int personId, bool walking = false)
    {
        if (!Cells.TryGetValue(key, out var cell)) Cells.Add(key, cell = new Cell());
        cell.Entries++;
        if (walking) cell.WalkingEntries++;
        cell.Visitors.Add(personId);
        MaximumEntries = Math.Max(MaximumEntries, cell.Entries);
        Revision++;
    }

    public Color ColorAt(Vector3Int key, Gradient gradient, long scale)
    {
        long frequency = Cells.TryGetValue(key, out var cell) ? cell.Entries : 0;
        // A logarithmic scale keeps lower-traffic passages visible without
        // altering the raw entry counts written to disk.
        float value = ABMU.Utilities.Map((float)Math.Log(1d + frequency), 0,
            (float)Math.Log(1d + Math.Max(1, scale)), 0, 1);
        return gradient.Evaluate(Mathf.Clamp01(value));
    }
}
