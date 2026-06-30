using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>
/// Reads ambient fill configurations from a CSV and spawns background aerosol
/// particles distributed across room volumes at simulation start.
/// Each spawned particle system gets its own AirflowParticleDriver so it
/// is driven through the velocity field just like agent emission particles.
/// </summary>
public class AmbientFillEmitter : MonoBehaviour
{
    [Header("Configuration")]
    [Tooltip("Path to CSV file. Can be absolute or relative to project root.")]
    [SerializeField] private string csvPath = "Assets/Data/emitter_config.csv";

    [Header("Visualization")]
    [Tooltip("Visual diameter of particles in world units (meters).")]
    [SerializeField] private float visualParticleSize = 0.03f;

    [Tooltip("Color of ambient fill particles.")]
    [SerializeField] private Color ambientColor = new Color(0.2f, 0.6f, 1f, 1f);

    [Tooltip("URP-compatible particle material. Assign a Particles/Unlit Transparent material.")]
    [SerializeField] private Material particleMaterial;

    [Header("Airflow Driver Settings")]
    [Tooltip("Match the Airflow Strength value on your other AirflowParticleDriver instances.")]
    [Range(0f, 10f)]
    [SerializeField] private float airflowStrength = 1.0f;

    [Tooltip("Match the Advection Mode on your other AirflowParticleDriver instances.")]

    [Range(0f, 0.5f)]
    [SerializeField] private float diffusionStrength = 0.02f;

    // -------------------------------------------------------------------------
    // Internal

    private readonly List<AmbientFillConfig> _configs = new();

    private class AmbientFillConfig
    {
        public string  EmitterId;
        public Vector3 Position;
        public Vector3 Extents;        // half-extents (meters)
        public int     Count;
        public float   Tau;
        public float   ParticleSizeUm;
    }

    // -------------------------------------------------------------------------
    // Lifecycle — Awake so particle systems exist before AirflowParticleDriver.Start()

    void Awake()
    {
        LoadConfigs();
        SpawnAll();
    }

    // -------------------------------------------------------------------------
    // CSV loading

    void LoadConfigs()
    {
        string resolvedPath = Path.IsPathRooted(csvPath)
            ? csvPath
            : Path.Combine(Application.dataPath, "..", csvPath);

        if (!File.Exists(resolvedPath))
        {
            Debug.LogError($"[AmbientFillEmitter] CSV not found: {resolvedPath}");
            return;
        }

        _configs.Clear();
        string[] lines = File.ReadAllLines(resolvedPath);

        for (int i = 1; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (string.IsNullOrEmpty(line) || line.StartsWith("#")) continue;

            string[] p = line.Split(',');
            if (p.Length < 11) continue;
            if (p[1].Trim().ToLower() != "ambient") continue;

            if (!TryParseRow(p, i + 1, out AmbientFillConfig cfg)) continue;
            _configs.Add(cfg);
        }

        Debug.Log($"[AmbientFillEmitter] Loaded {_configs.Count} ambient fill config(s).");
    }

    bool TryParseRow(string[] p, int lineNum, out AmbientFillConfig cfg)
    {
        cfg = null;
        try
        {
            cfg = new AmbientFillConfig
            {
                EmitterId      = p[0].Trim(),
                Position       = new Vector3(float.Parse(p[2]), float.Parse(p[3]), float.Parse(p[4])),
                Extents        = new Vector3(float.Parse(p[5]), float.Parse(p[6]), float.Parse(p[7])),
                Count          = int.Parse(p[8]),
                Tau            = float.Parse(p[9]),
                ParticleSizeUm = float.Parse(p[10])
            };
            return true;
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"[AmbientFillEmitter] Skipping malformed row {lineNum}: {e.Message}");
            return false;
        }
    }

    // -------------------------------------------------------------------------
    // Spawning

    void SpawnAll()
    {
        foreach (var cfg in _configs)
            SpawnForConfig(cfg);
    }

    void SpawnForConfig(AmbientFillConfig cfg)
    {
        var obj = new GameObject($"AmbientFill_{cfg.EmitterId}");
        obj.transform.SetParent(transform, worldPositionStays: false);
        obj.transform.position = cfg.Position;

        // --- Particle System ---
        var ps = obj.AddComponent<ParticleSystem>();
        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

        var main             = ps.main;
        main.loop            = false;
        main.startSpeed      = 0f;
        main.startLifetime   = 9999f;
        main.startSize       = visualParticleSize;
        main.startColor      = ambientColor;
        main.maxParticles    = cfg.Count + 64;
        main.simulationSpace = ParticleSystemSimulationSpace.World; // required by AirflowParticleDriver

        var shape       = ps.shape;
        shape.enabled   = true;
        shape.shapeType = ParticleSystemShapeType.Box;
        shape.scale     = cfg.Extents * 2f;

        var emission = ps.emission;
        emission.rateOverTime = 0f;
        emission.SetBursts(new[] { new ParticleSystem.Burst(0f, cfg.Count) });

        var vol     = ps.velocityOverLifetime;
        vol.enabled = false;

        var externalForces    = ps.externalForces;
        externalForces.enabled = true;

        var rend = ps.GetComponent<ParticleSystemRenderer>();
        rend.renderMode = ParticleSystemRenderMode.Billboard;
        if (particleMaterial != null) rend.material = particleMaterial;

        ps.Play();

        // --- AirflowParticleDriver: must be added AFTER PS is configured ---
        // (Awake fires immediately on AddComponent; PS is already in World space by then)
        var driver                = obj.AddComponent<AirflowParticleDriver>();
        driver.airflowStrength    = airflowStrength;
        driver.diffusionStrength  = diffusionStrength;
        // Settling velocity from Stokes drag — scales with d_p²
        // v_s = (ρ_p · g · d_p²) / (18 · μ)
        const float rhoP = 1100f;    // kg/m³
        const float grav = 9.81f;    // m/s²
        const float mu   = 1.81e-5f; // Pa·s
        float dp = cfg.ParticleSizeUm * 1e-6f;
        driver.settlingSpeed = (rhoP * grav * dp * dp) / (18f * mu);

        // --- Marker: physics params for downstream consumers (Wells-Riley etc.) ---
        var marker           = obj.AddComponent<AmbientParticleMarker>();
        marker.EmitterId     = cfg.EmitterId;
        marker.Tau           = cfg.Tau;
        marker.ParticleSizeUm = cfg.ParticleSizeUm;

        Debug.Log($"[AmbientFillEmitter] '{cfg.EmitterId}' — {cfg.Count} particles " +
                  $"in volume {cfg.Extents * 2f} centred at {cfg.Position}");
    }
}