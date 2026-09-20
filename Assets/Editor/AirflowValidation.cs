using System;
using UnityEditor;
using UnityEngine;

public static class AirflowValidation
{
    [MenuItem("Tools/Airflow/Validate recorded field")]
    public static void Validate()
    {
        var data = AssetDatabase.LoadAssetAtPath<RecordedAirflow>("Assets/SimData/flow_field.npz");
        Require(data != null && data.positions.Length == 42981 && data.times.Length == 101, "NPZ dimensions");
        Require(Mathf.Abs(data.bounds.size.y - 3.048f) < 0.001f, "Z-up conversion");
        Require(data.times[100] == 50f && data.maxSpeed > 3.43f && data.maxSpeed < 3.45f, "Time and velocity units");
        data.Initialize();
        for (int i = 0; i < data.positions.Length; i += 317)
        {
            var p = data.positions[i];
            Require((data.Sample(p, 25, 0.75f) - data.velocities[50 * data.positions.Length + i]).magnitude < 0.001f, "Exact node");
            var expected = Vector3.Lerp(data.velocities[50 * data.positions.Length + i], data.velocities[51 * data.positions.Length + i], 0.5f);
            Require((data.Sample(p, 25.25f, 0.75f) - expected).magnitude < 0.001f, "Temporal interpolation");
        }
        Require(data.Sample(data.bounds.max + Vector3.one, 25, 0.75f) == Vector3.zero, "Outside volume");
        // Analytic field: spatial and temporal interpolation plus unsupported holes.
        var simple = ScriptableObject.CreateInstance<RecordedAirflow>();
        simple.positions = new[] { Vector3.zero, Vector3.right, Vector3.up, Vector3.forward };
        simple.times = new[] { 0f, 1f };
        simple.bounds = new Bounds(Vector3.one * 0.5f, Vector3.one);
        simple.velocities = new[] { Vector3.back, Vector3.back, Vector3.back, Vector3.back,
            Vector3.back * 3, Vector3.back * 3, Vector3.back * 3, Vector3.back * 3 };
        Require((simple.Sample(Vector3.one * 0.2f, 0.5f, 1) - Vector3.back * 2).magnitude < 0.00001f, "Uniform south flow");
        Require(simple.Sample(Vector3.one * 0.5f, 0.5f, 0.1f) == Vector3.zero, "Unsupported mesh gap");
        UnityEngine.Object.DestroyImmediate(simple);
        Debug.Log("AIRFLOW VALIDATION PASSED: import, axes, units, 272 recorded-node/time checks, southward interpolation, bounds and support cutoff.");
    }
    public static void ValidateParticleMotion()
    {
        Validate();
        var root = new GameObject("Airflow test fixture");
        var data = ScriptableObject.CreateInstance<RecordedAirflow>();
        try
        {
            data.positions = new[] { new Vector3(-10,-10,-10), new Vector3(10,-10,10), new Vector3(-10,10,10), new Vector3(10,10,-10) };
            data.velocities = new[] { Vector3.back, Vector3.back, Vector3.back, Vector3.back };
            data.times = new[] { 0f }; data.bounds = new Bounds(Vector3.zero, Vector3.one * 20);
            var loader = root.AddComponent<VelocityFieldLoader>(); loader.recordedField = data; loader.supportRadius = 30;
            loader.SendMessage("Awake"); loader.SendMessage("Start");
            for (int space = 0; space < 3; space++)
            {
                var go = new GameObject("Particle fixture"); go.transform.SetParent(root.transform);
                go.transform.rotation = Quaternion.Euler(0, 53, 0); go.transform.localScale = Vector3.one * 2;
                var ps = go.AddComponent<ParticleSystem>(); ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                var main = ps.main; main.startSpeed = 0; main.startLifetime = 10; main.maxParticles = 1;
                main.simulationSpace = (ParticleSystemSimulationSpace)space;
                if (space == 2) main.customSimulationSpace = go.transform;
                var emission = ps.emission; emission.enabled = false;
                loader.SendMessage("AttachParticleDrivers");
                var driver = ps.GetComponent<AirflowParticleDriver>();
                Require(driver != null, "New particle system registration"); driver.SendMessage("Awake");
                main.maxParticles = 8; // Verify resizing after the driver was created.
                ps.Play(); ps.Emit(new ParticleSystem.EmitParams { position = Vector3.zero, velocity = Vector3.zero, startLifetime = 10 }, 1);
                for (int frame = 0; frame < 60; frame++)
                {
                    ps.Play(); driver.SendMessage("AdvanceParticles", 1f / 60f);
                    ps.Simulate(1f / 60f, false, false, false);
                }
                var particles = new ParticleSystem.Particle[8];
                Require(ps.GetParticles(particles) == 1, "Particle retained");
                Vector3 world = space == 1 ? particles[0].position : go.transform.TransformPoint(particles[0].position);
                Require(world.z < -0.9f, "Southward particle displacement in space " + space + ": " + world);
            }
            var viz = root.AddComponent<VelocityFieldVisualizer>();
            Require(!viz.showArrows, "Visualization hidden by default");
            Debug.Log("AIRFLOW MOTION PASSED: 60 physical simulation steps in local, world and custom space; late registration; buffer resizing; hidden wisps.");
        }
        finally { UnityEngine.Object.DestroyImmediate(root); UnityEngine.Object.DestroyImmediate(data); }
    }

    static void Require(bool condition, string label)
    { if (!condition) throw new Exception("Airflow validation failed: " + label); }

    // Also used to generate a portable prefab in an isolated verification project.
    public static void GeneratePrefabAndValidate()
    {
        Validate();
        var data = AssetDatabase.LoadAssetAtPath<RecordedAirflow>("Assets/SimData/flow_field.npz");
        var material = new Material(Shader.Find("Airflow/Wisp"));
        AssetDatabase.CreateAsset(material, "Assets/AirflowWisp.mat");
        var root = new GameObject("First Floor Recorded Airflow");
        var loader = root.AddComponent<VelocityFieldLoader>(); loader.recordedField = data;
        var viz = root.AddComponent<VelocityFieldVisualizer>(); viz.lineMaterial = material; viz.showArrows = false;
        PrefabUtility.SaveAsPrefabAsset(root, "Assets/FirstFloorAirflow.prefab");
        UnityEngine.Object.DestroyImmediate(root);
        AssetDatabase.SaveAssets();
    }
}
