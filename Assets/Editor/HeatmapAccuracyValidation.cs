using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

// Isolated editor only: -batchmode -executeMethod HeatmapAccuracyValidation.Run -quit.
public static class HeatmapAccuracyValidation
{
    private const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Instance;
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Near(double actual, double expected, string message) => Require(Math.Abs(actual - expected) < 0.000001, message + ": " + actual + " vs " + expected);
    private static object Call(object target, string method, params object[] args) => target.GetType().GetMethod(method, Hidden).Invoke(target, args);

    public static void Run()
    {
        if (!Application.isBatchMode) throw new InvalidOperationException("Run these fixtures in an isolated batch editor.");
        ValidateIntegrals(); ValidateFootTraffic(); ValidateRuntimeClock(); WriteAnalyticExport();
        HeatmapValidation.Run();
        Debug.Log("HEATMAP ACCURACY VALIDATION PASSED: conserved segment person-time, negative/diagonal cells, simulated-hour normalization, speed changes, pauses, relocation exclusions, fixed shared scales, TIFF/world-file orientation, zero versus NoData, saved viewer/JSON and CSV persistence. Physical sensor calibration remains external to these counting tests.");
    }

    private static void ValidateIntegrals()
    {
        var grid = new HeatmapGrid(new[] { 0f, 4f }, 1);
        var a = new Vector3(0.25f, 0.6f, 0.5f); var b = new Vector3(2.75f, 0.6f, 0.5f);
        grid.AdvanceTime(1, 10); grid.RecordArrival(a, 1); grid.RecordDuration(a, b, 10); grid.RecordMovement(a, b, 1);
        Near(grid.Cells[new Vector3Int(0, 0, 0)].SimulatedPersonSeconds, 3, "Starting-cell person-time");
        Near(grid.Cells[new Vector3Int(1, 0, 0)].SimulatedPersonSeconds, 4, "Intermediate-cell person-time");
        Near(grid.Cells[new Vector3Int(2, 0, 0)].SimulatedPersonSeconds, 3, "Final-cell person-time");
        Near(grid.Cells.Values.Sum(c => c.SimulatedPersonSeconds), 10, "Person-time conservation");
        Require(grid.Cells[new Vector3Int(0,0,0)].WalkingEntries == 0 && grid.Cells[new Vector3Int(1,0,0)].WalkingEntries == 1,
            "Initial presence inflated walking traffic.");
        Near(HeatmapMeasures.Value(grid.Cells[new Vector3Int(1,0,0)], HeatmapMeasure.WalkingEntriesPerHour, grid.SimulatedSeconds, 0), 360, "Simulated hour rate");
        grid.RecordDuration(a, new Vector3(0.25f, 4.6f, 0.5f), 5);
        grid.RecordDuration(a, b, 7, false);
        Near(grid.ExcludedTransitionPersonSeconds, 12, "Uncertain transition time was hidden");
        Near(grid.Cells.Values.Sum(c => c.SimulatedPersonSeconds), 10, "Relocation assigned phantom person-time");
        var negative = new HeatmapGrid(new[] { 0f }, 1);
        negative.RecordDuration(new Vector3(-2.75f,0,-2.75f), new Vector3(-0.25f,0,-0.25f), 10);
        Near(negative.Cells.Values.Sum(c => c.SimulatedPersonSeconds), 10, "Negative diagonal conservation");
        Require(negative.Cells.Count == 3 && negative.Cells.Keys.All(k => k.x == k.z), "Corner crossing allocated time to untouched cells.");
        var normal = new HeatmapGrid(new[] { 0f }, 1); var faster = new HeatmapGrid(new[] { 0f }, 1);
        normal.AdvanceTime(60, 3600); faster.AdvanceTime(30, 1800); faster.AdvanceTime(15, 1800);
        normal.RecordDuration(a, a, 3600); faster.RecordDuration(a, a, 1800); faster.RecordDuration(a, a, 1800);
        normal.AdvanceTime(0, 0); normal.RecordDuration(a, a, 0);
        Near(normal.Cells[normal.Key(a)].SimulatedPersonSeconds, faster.Cells[faster.Key(a)].SimulatedPersonSeconds, "Speed-dependent person-time");
        Near(HeatmapMeasures.Value(normal.Cells[normal.Key(a)], HeatmapMeasure.MeanOccupancy, normal.SimulatedSeconds, 1), 1, "Stationary mean occupancy");
        Require(double.IsNaN(HeatmapMeasures.Value(null, HeatmapMeasure.WalkingEntriesPerHour, 0, 0)), "Undefined duration became a measured zero.");
        Require(HeatmapCoverage.Intersects(new Vector3(0,0,0.1f),new Vector3(2,0,0.1f),new Vector3(2,0,0.2f),0.5f,0.5f,0.5f), "Narrow corridor lost because cell centre was outside.");
        Require(!HeatmapCoverage.Intersects(Vector3.zero,Vector3.right,Vector3.forward,2.5f,2.5f,0.5f), "Outside floor cell became valid coverage.");
    }

    private static void ValidateFootTraffic()
    {
        var grid = new HeatmapGrid(new[] { 0f }, 1);
        var classroom = new Vector3(0.5f, 0, 0.5f);
        grid.AdvanceTime(60, 3600);
        for (int person = 0; person < 20; person++)
        {
            grid.RecordArrival(classroom, person);
            grid.RecordDuration(classroom, classroom, 3600);
            grid.RecordMovement(classroom, classroom, person);
        }
        Near(HeatmapMeasures.Value(grid.Cells[grid.Key(classroom)], HeatmapMeasure.WalkingEntriesPerHour, 3600, 0), 0,
            "Stationary classroom occupants inflated foot traffic");
        var start = new Vector3(2.5f, 0, 0.5f); var end = new Vector3(5.5f, 0, 0.5f);
        for (int person = 0; person < 60; person++) grid.RecordMovement(start, end, person);
        Near(HeatmapMeasures.Value(grid.Cells[new Vector3Int(4, 0, 0)], HeatmapMeasure.WalkingEntriesPerHour, 3600, 0), 60,
            "Busy hallway did not retain walking traffic");
    }

    private static void ValidateRuntimeClock()
    {
        var root = new GameObject("Heatmap simulated-time fixture");
        var body = new GameObject("Observed person"); body.AddComponent<BoxCollider>();
        var person = body.AddComponent<AgentHealth>(); Call(person,"Awake"); Call(person,"OnEnable");
        body.transform.position = new Vector3(0.25f,0.6f,0.5f);
        try
        {
            var clock = root.AddComponent<TimeManager>(); clock.BeginSimulation();
            var map = root.AddComponent<SimulationHeatmap>(); map.floorHeights = new[] { 0f, 4f }; map.Initialize(clock);
            map.Begin(Path.GetFullPath(Path.Combine(Application.dataPath,"../HeatmapRuntimeAccuracyOutput")));
            typeof(TimeManager).GetField("<ElapsedSimulationSeconds>k__BackingField",Hidden).SetValue(clock,1800d);
            body.transform.position = new Vector3(2.75f,0.6f,0.5f); Call(map,"TrackMovement",30f); Call(map,"Capture",30f,30f);
            typeof(TimeManager).GetField("<ElapsedSimulationSeconds>k__BackingField",Hidden).SetValue(clock,3600d);
            Call(map,"TrackMovement",30f); Call(map,"Capture",60f,30f);
            Near(map.Grid.SimulatedSeconds,3600,"Runtime clock denominator");
            Near(map.Grid.Cells.Values.Sum(c=>c.SimulatedPersonSeconds),3600,"Runtime continuous person-time");
            Near(HeatmapMeasures.Value(map.Grid.Cells[new Vector3Int(2,0,0)],HeatmapMeasure.MeanOccupancy,3600,2),0.65,"Runtime fractional dwell");
            map.ExportSnapshot();
            Call(person,"OnDisable"); // Simulate Unity disabling agents before the map on Play-mode stop.
            typeof(SimulationHeatmap).GetField("activeSampleSeconds",Hidden).SetValue(map,0.1f);
            Call(map,"Close");
            var data = JsonUtility.FromJson<HeatmapSnapshot>(File.ReadAllText(Path.Combine(map.OutputDirectory,"heatmap_run.json")));
            Near(data.simulatedSeconds,3600,"Saved simulation duration");
            Near(data.cells.Sum(c=>c.simulatedPersonSeconds),3600,"Saved person-time");
            Require(data.presentCount==1&&data.cells.Sum(c=>c.occupants)==1,"Stop teardown invented an empty final snapshot.");
            var rows = File.ReadAllLines(Path.Combine(map.OutputDirectory,"heatmap_cells.csv"));
            Require(rows[0].Contains("simulated_person_seconds")&&rows.Skip(1).All(r=>r.Split(',').Length==20),"New CSV units/schema not persisted.");
        }
        finally { Call(person,"OnDisable"); UnityEngine.Object.DestroyImmediate(body); UnityEngine.Object.DestroyImmediate(root); }
    }

    private static void WriteAnalyticExport()
    {
        var grid = new HeatmapGrid(new[] { 0f,4f },1);
        Vector3 bottom = new Vector3(0.5f,0,0.5f), top = new Vector3(0.5f,0,1.5f), upper = new Vector3(3.5f,4,0.5f);
        grid.AdvanceTime(60,3600);
        grid.RecordArrival(bottom,1);grid.RecordArrival(bottom,2);grid.RecordArrival(top,3);grid.RecordArrival(upper,4);grid.RecordArrival(upper,5);
        grid.RecordDuration(bottom,bottom,7200);grid.RecordDuration(top,top,1800);grid.RecordDuration(upper,upper,7200);
        grid.BeginSample();grid.Observe(bottom,1,60);grid.Observe(bottom,2,60);grid.Observe(top,3,60);grid.Observe(upper,4,60);grid.Observe(upper,5,60);
        var coverage = new HeatmapCoverage();
        coverage.Cells.Add(new Vector3Int(0,0,0));coverage.Cells.Add(new Vector3Int(1,0,0));coverage.Cells.Add(new Vector3Int(0,0,1));coverage.Cells.Add(new Vector3Int(1,0,1));coverage.Cells.Add(new Vector3Int(3,1,0));
        var snapshot=HeatmapSnapshot.Capture(grid,coverage);snapshot.building="Analytic validation fixture";snapshot.measure=HeatmapMeasure.MeanOccupancy.ToString();snapshot.scaleMaximum=2;
        snapshot.recordedUtc="2026-10-19T12:00:00Z";snapshot.coverageSource="Known synthetic test cells; not real sensor observations";
        snapshot.startDay=snapshot.endDay=1;snapshot.startMinute=480;snapshot.endMinute=540;
        var values=snapshot.Raster(1,HeatmapMeasure.MeanOccupancy);
        Require(snapshot.width==4&&snapshot.height==2,"Raster bounds");
        Near(values[0],0.5,"TIFF top row is increasing Z");Near(values[4],2,"TIFF bottom row");Near(values[1],0,"Measured zero");Near(values[3],HeatmapTiff.NoData,"Unknown area became blue zero");
        string output=Path.GetFullPath(Path.Combine(Application.dataPath,"../HeatmapReadableValidationOutput"));
        HeatmapExport.Save(output,snapshot,true);
        string tiff=Path.Combine(output,"floor-1-MeanOccupancy.tif");
        using(var reader=new BinaryReader(File.OpenRead(tiff))){Require(reader.ReadUInt16()==0x4949&&reader.ReadUInt16()==42,"TIFF byte order/header");}
        var world=File.ReadAllLines(Path.ChangeExtension(tiff,".tfw"));Near(double.Parse(world[3],CultureInfo.InvariantCulture),-1,"North-up pixel height");Near(double.Parse(world[5],CultureInfo.InvariantCulture),1.5,"World-file pixel-centre Z");
        Require(File.ReadAllText(Path.Combine(output,"heatmap-viewer.html")).Contains("Analytic validation fixture"),"Offline viewer missing embedded data.");
        var root=new GameObject("Fixed scale fixture");
        try
        {
            var map=root.AddComponent<SimulationHeatmap>();map.floorHeights=new[]{0f,4f};map.Initialize(null);
            Require(map.measure == HeatmapMeasure.WalkingEntriesPerHour && map.scaleMaximum == 60,
                "Default heatmap is not foot traffic");
            map.SetMeasure(HeatmapMeasure.MeanOccupancy);
            typeof(SimulationHeatmap).GetField("<Grid>k__BackingField",Hidden).SetValue(map,grid);
            map.SetScale(2);
            Require(map.CellColor(grid.Key(bottom))==map.CellColor(grid.Key(upper)),"Different scale on different floors.");
            Require(map.CellColor(grid.Key(bottom)).r>0.99f&&map.CellColor(grid.Key(bottom)).g<0.01f,"Numerical red threshold");
        }
        finally { UnityEngine.Object.DestroyImmediate(root); }
        Debug.Log("READABLE HEATMAP FIXTURE: "+output);
    }
}
