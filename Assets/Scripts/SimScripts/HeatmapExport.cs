using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.AI;

public enum HeatmapMeasure { MeanOccupancy, WalkingEntriesPerHour, CurrentOccupancy, TotalEntries, PersonHours }

public static class HeatmapMeasures
{
    public static string Label(HeatmapMeasure measure) => measure switch {
        HeatmapMeasure.MeanOccupancy => "Mean people per cell",
        HeatmapMeasure.WalkingEntriesPerHour => "Walking entries / simulated hour / cell",
        HeatmapMeasure.CurrentOccupancy => "People per cell (last snapshot)",
        HeatmapMeasure.TotalEntries => "Total cell entries (includes arrivals)",
        _ => "Person-hours per cell"
    };
    public static double DefaultMaximum(HeatmapMeasure measure) => measure switch {
        HeatmapMeasure.MeanOccupancy => 1, HeatmapMeasure.WalkingEntriesPerHour => 60,
        HeatmapMeasure.CurrentOccupancy => 5, HeatmapMeasure.TotalEntries => 100, _ => 1
    };
    public static double Value(HeatmapGrid.Cell cell, HeatmapMeasure measure, double seconds, int sampleCount)
    {
        if (measure == HeatmapMeasure.MeanOccupancy) return seconds > 0 ? (cell?.SimulatedPersonSeconds ?? 0) / seconds : double.NaN;
        if (measure == HeatmapMeasure.WalkingEntriesPerHour) return seconds > 0 ? (cell?.WalkingEntries ?? 0) * 3600d / seconds : double.NaN;
        if (measure == HeatmapMeasure.CurrentOccupancy) return sampleCount > 0 ? cell?.Occupants ?? 0 : double.NaN;
        if (measure == HeatmapMeasure.TotalEntries) return cell?.Entries ?? 0;
        return (cell?.SimulatedPersonSeconds ?? 0) / 3600d;
    }
}

[Serializable]
public sealed class HeatmapSnapshot
{
    public int schemaVersion = 2;
    public string building, recordedUtc, exportedUtc, coordinateSystem = "Local Unity X/Z metres; no surveyed map CRS";
    public string timeBasis = "Active simulated seconds; paused time and skipped overnight gaps excluded";
    public string coverageSource, measure;
    public int minX, minZ, width, height, sampleCount, startDay, startMinute, endDay, endMinute;
    public float cellSize;
    public double simulatedSeconds, observedRealSeconds, excludedTransitionPersonSeconds, scaleMaximum;
    public double lastSnapshotSimulatedSeconds;
    public int registeredCount, presentCount, fillerCount;
    public Floor[] floors;
    public Cell[] cells;
    [Serializable] public sealed class Floor { public int number; public float height; public int[] coverage; }
    [Serializable] public sealed class Cell
    {
        public int floor, x, z, occupants, uniqueVisitors;
        public long entries, walkingEntries;
        public double simulatedPersonSeconds;
        public bool onWalkableSurface;
    }

    public static HeatmapSnapshot Capture(HeatmapGrid grid, HeatmapCoverage coverage)
    {
        var data = new HeatmapSnapshot { cellSize = grid.CellSize, sampleCount = grid.SampleCount,
            simulatedSeconds = grid.SimulatedSeconds, observedRealSeconds = grid.ObservedRealSeconds,
            excludedTransitionPersonSeconds = grid.ExcludedTransitionPersonSeconds,
            lastSnapshotSimulatedSeconds = grid.LastSnapshotSimulatedSeconds,
            exportedUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            coverageSource = coverage.FromNavMesh ? "NavMesh cell intersections plus observed cells" : "Observed cells only; walkable coverage unavailable" };
        int maxX = 0, maxZ = 0; bool first = true;
        foreach (var key in coverage.Cells) Extend(key);
        foreach (var key in grid.Cells.Keys) Extend(key);
        if (first) { data.minX = data.minZ = maxX = maxZ = 0; }
        data.width = checked(maxX - data.minX + 1); data.height = checked(maxZ - data.minZ + 1);
        if ((long)data.width * data.height > 8000000) throw new InvalidOperationException("Heatmap export is too large; increase the cell size.");
        data.floors = new Floor[grid.FloorHeights.Length];
        var masks = new List<int>[data.floors.Length];
        for (int floor = 0; floor < masks.Length; floor++) masks[floor] = new List<int>();
        foreach (var key in coverage.Cells)
            masks[key.y].Add((maxZ - key.z) * data.width + key.x - data.minX);
        for (int floor = 0; floor < masks.Length; floor++)
        {
            masks[floor].Sort();
            data.floors[floor] = new Floor { number = floor + 1, height = grid.FloorHeights[floor], coverage = masks[floor].ToArray() };
        }
        var cells = new List<Cell>();
        foreach (var pair in grid.Cells)
            cells.Add(new Cell { floor = pair.Key.y + 1, x = pair.Key.x, z = pair.Key.z, occupants = pair.Value.Occupants,
                uniqueVisitors = pair.Value.Visitors.Count, entries = pair.Value.Entries, walkingEntries = pair.Value.WalkingEntries,
                simulatedPersonSeconds = pair.Value.SimulatedPersonSeconds, onWalkableSurface = coverage.Cells.Contains(pair.Key) });
        cells.Sort((a,b) => a.floor != b.floor ? a.floor.CompareTo(b.floor) : a.z != b.z ? a.z.CompareTo(b.z) : a.x.CompareTo(b.x));
        data.cells = cells.ToArray();
        return data;

        void Extend(Vector3Int key)
        {
            if (first) { data.minX = maxX = key.x; data.minZ = maxZ = key.z; first = false; }
            else { data.minX = Math.Min(data.minX, key.x); maxX = Math.Max(maxX, key.x); data.minZ = Math.Min(data.minZ, key.z); maxZ = Math.Max(maxZ, key.z); }
        }
    }

    public double Value(Cell cell, HeatmapMeasure metric)
    {
        if (metric == HeatmapMeasure.MeanOccupancy) return simulatedSeconds > 0 ? (cell?.simulatedPersonSeconds ?? 0) / simulatedSeconds : double.NaN;
        if (metric == HeatmapMeasure.WalkingEntriesPerHour) return simulatedSeconds > 0 ? (cell?.walkingEntries ?? 0) * 3600d / simulatedSeconds : double.NaN;
        if (metric == HeatmapMeasure.CurrentOccupancy) return sampleCount > 0 ? cell?.occupants ?? 0 : double.NaN;
        if (metric == HeatmapMeasure.TotalEntries) return cell?.entries ?? 0;
        return (cell?.simulatedPersonSeconds ?? 0) / 3600d;
    }

    public float[] Raster(int floor, HeatmapMeasure metric)
    {
        var result = new float[checked(width * height)];
        for (int i = 0; i < result.Length; i++) result[i] = HeatmapTiff.NoData;
        double zero = Value(null, metric);
        foreach (int index in floors[floor - 1].coverage) result[index] = double.IsNaN(zero) ? HeatmapTiff.NoData : (float)zero;
        foreach (var cell in cells)
            if (cell.floor == floor)
            {
                double value = Value(cell, metric);
                result[(minZ + height - 1 - cell.z) * width + cell.x - minX] = double.IsNaN(value) ? HeatmapTiff.NoData : (float)value;
            }
        return result;
    }
}

public sealed class HeatmapCoverage
{
    public readonly HashSet<Vector3Int> Cells = new();
    public bool FromNavMesh { get; private set; }
    public static HeatmapCoverage Capture(HeatmapGrid grid)
    {
        var result = new HeatmapCoverage();
        var mesh = NavMesh.CalculateTriangulation(); result.FromNavMesh = mesh.indices.Length > 0;
        for (int i = 0; i < mesh.indices.Length; i += 3)
        {
            Vector3 a = mesh.vertices[mesh.indices[i]], b = mesh.vertices[mesh.indices[i + 1]], c = mesh.vertices[mesh.indices[i + 2]];
            if (Mathf.Abs((b.x - a.x) * (c.z - a.z) - (b.z - a.z) * (c.x - a.x)) < 1e-7f) continue;
            int floor = grid.FloorAt((a.y + b.y + c.y) / 3);
            int minX = Mathf.FloorToInt(Mathf.Min(a.x, Mathf.Min(b.x, c.x)) / grid.CellSize);
            int minZ = Mathf.FloorToInt(Mathf.Min(a.z, Mathf.Min(b.z, c.z)) / grid.CellSize);
            int maxX = Mathf.CeilToInt(Mathf.Max(a.x, Mathf.Max(b.x, c.x)) / grid.CellSize) - 1;
            int maxZ = Mathf.CeilToInt(Mathf.Max(a.z, Mathf.Max(b.z, c.z)) / grid.CellSize) - 1;
            for (int z = minZ; z <= maxZ; z++) for (int x = minX; x <= maxX; x++)
                if (Intersects(a, b, c, (x + 0.5f) * grid.CellSize, (z + 0.5f) * grid.CellSize, grid.CellSize * 0.5f))
                    result.Cells.Add(new Vector3Int(x, floor, z));
        }
        return result;
    }

    // Separating-axis test: preserve narrow corridors even if no cell centre is inside a triangle.
    public static bool Intersects(Vector3 a, Vector3 b, Vector3 c, float x, float z, float half)
    {
        return Axis(a, b, c, x, z, half, 1, 0) && Axis(a, b, c, x, z, half, 0, 1) &&
            Axis(a, b, c, x, z, half, b.z - a.z, a.x - b.x) &&
            Axis(a, b, c, x, z, half, c.z - b.z, b.x - c.x) &&
            Axis(a, b, c, x, z, half, a.z - c.z, c.x - a.x);
    }
    private static bool Axis(Vector3 a, Vector3 b, Vector3 c, float x, float z, float half, float ax, float az)
    {
        if (ax == 0 && az == 0) return true;
        float pa = (a.x - x) * ax + (a.z - z) * az, pb = (b.x - x) * ax + (b.z - z) * az, pc = (c.x - x) * ax + (c.z - z) * az;
        float radius = half * (Mathf.Abs(ax) + Mathf.Abs(az));
        return Mathf.Min(pa, Mathf.Min(pb, pc)) < radius && Mathf.Max(pa, Mathf.Max(pb, pc)) > -radius;
    }
}

// Baseline little-endian, single-band Float32 TIFF. Values remain numerical,
// rather than baking a colour image; TFW supplies local metre coordinates.
public static class HeatmapTiff
{
    public const float NoData = -9999f;
    public static void Write(string path, int width, int height, float[] values, string description)
    {
        if (width <= 0 || height <= 0 || (long)width * height != values.Length) throw new ArgumentException("Invalid TIFF dimensions.");
        byte[] text = Encoding.ASCII.GetBytes(description + "\0"), nodata = Encoding.ASCII.GetBytes("-9999\0");
        const ushort tags = 14;
        uint textOffset = 8 + 2 + tags * 12 + 4, nodataOffset = textOffset + (uint)text.Length;
        uint pixelsOffset = (nodataOffset + (uint)nodata.Length + 3) & ~3u;
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write((byte)'I'); writer.Write((byte)'I'); writer.Write((ushort)42); writer.Write(8u); writer.Write(tags);
        Tag(256, 4, 1, (uint)width); Tag(257, 4, 1, (uint)height); Tag(258, 3, 1, 32); Tag(259, 3, 1, 1);
        Tag(262, 3, 1, 1); Tag(270, 2, (uint)text.Length, text.Length <= 4 ? Pack(text) : textOffset);
        Tag(273, 4, 1, pixelsOffset); Tag(274, 3, 1, 1); Tag(277, 3, 1, 1); Tag(278, 4, 1, (uint)height);
        Tag(279, 4, 1, checked((uint)values.Length * 4)); Tag(284, 3, 1, 1); Tag(339, 3, 1, 3); Tag(42113, 2, (uint)nodata.Length, nodataOffset);
        writer.Write(0u); writer.Write(text); writer.Write(nodata);
        while (writer.BaseStream.Position < pixelsOffset) writer.Write((byte)0);
        foreach (float value in values) writer.Write(value);
        void Tag(ushort id, ushort type, uint count, uint value) { writer.Write(id); writer.Write(type); writer.Write(count); writer.Write(value); }
    }
    private static uint Pack(byte[] value) { uint result = 0; for (int i = 0; i < value.Length; i++) result |= (uint)value[i] << (8 * i); return result; }
    public static string WorldFile(HeatmapSnapshot data) => string.Join("\n", new[] {
        data.cellSize.ToString("R", CultureInfo.InvariantCulture), "0", "0", (-data.cellSize).ToString("R", CultureInfo.InvariantCulture),
        ((data.minX + 0.5d) * data.cellSize).ToString("R", CultureInfo.InvariantCulture),
        ((data.minZ + data.height - 0.5d) * data.cellSize).ToString("R", CultureInfo.InvariantCulture) }) + "\n";
}

public static class HeatmapExport
{
    public static void Save(string directory, HeatmapSnapshot data, bool tiffs)
    {
        Directory.CreateDirectory(directory);
        string json = JsonUtility.ToJson(data);
        AtomicText(Path.Combine(directory, "heatmap_run.json"), json);
        var template = Resources.Load<TextAsset>("HeatmapViewer");
        if (template != null) AtomicText(Path.Combine(directory, "heatmap-viewer.html"), template.text.Replace("/*HEATMAP_DATA*/null", json.Replace("</", "<\\/")));
        if (!tiffs) return;
        foreach (var floor in data.floors)
            foreach (HeatmapMeasure metric in Enum.GetValues(typeof(HeatmapMeasure)))
            {
                string path = Path.Combine(directory, "floor-" + floor.number + "-" + metric + ".tif");
                HeatmapTiff.Write(path + ".tmp", data.width, data.height, data.Raster(floor.number, metric),
                    "McEniry heatmap");
                Replace(path + ".tmp", path);
                AtomicText(Path.ChangeExtension(path, ".tfw"), HeatmapTiff.WorldFile(data));
            }
    }
    private static void AtomicText(string path, string text) { File.WriteAllText(path + ".tmp", text, new UTF8Encoding(false)); Replace(path + ".tmp", path); }
    private static void Replace(string source, string path)
    {
        // Mono's Windows File.Replace can fail even for writable existing files.
        // Finish writing the temporary file before overwriting the destination.
        File.Copy(source, path, true);
        File.Delete(source);
    }
}
