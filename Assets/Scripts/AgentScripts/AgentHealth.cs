using System.Collections.Generic;
using UnityEngine;

// Runs after AirflowParticleDriver so it cannot restore consumed particles.
[DefaultExecutionOrder(100)]
public class AgentHealth : MonoBehaviour
{
    [SerializeField] private bool infected;
    [Tooltip("Visual capture radius in metres, not a calibrated lung dose model.")]
    [Range(0.01f, 0.5f)] public float inhalationRadius = 0.2f;
    public bool IsInfected => infected;
    public int InhaledParticles { get; private set; }
    public int InhaledInfectedParticles { get; private set; }
    public int SpawnGeneration { get; private set; }
    public bool IsPresent => isActiveAndEnabled && (bodyCollider == null || bodyCollider.enabled) &&
        (navigation == null || navigation.Schedule == null ||
        (navigation.Schedule.CurrentActivity != AgentActivity.Idle &&
         navigation.Schedule.CurrentActivity != AgentActivity.OffCampus && navigation.Schedule.CurrentActivity != AgentActivity.Done));
    public static IReadOnlyList<AgentHealth> RegisteredAgents => agents;

    private ParticleSystem mouth;
    private AgentBehaviorController behavior;
    private ParticleSystem.Particle[] particles;
    private Renderer[] bodies;
    private Collider bodyCollider;
    private TimeManager clock;
    private NavigationAgent navigation;
    private Vector3 mouthPosition;
    private readonly List<MaterialPropertyBlock> originals = new();
    private MaterialPropertyBlock tint;
    private static readonly List<AgentHealth> agents = new();
    private static readonly Dictionary<Vector3Int, List<AgentHealth>> grid = new();
    private static readonly Stack<List<AgentHealth>> buckets = new();
    private static int gridFrame = -1;
    private const float CellSize = 0.5f;
    private static readonly Unity.Profiling.ProfilerMarker CaptureMarker = new("Simulation.Inhalation");

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        agents.Clear(); grid.Clear(); buckets.Clear(); gridFrame = -1;
    }

    private void Awake()
    {
        tint = new MaterialPropertyBlock();
        behavior = GetComponentInChildren<AgentBehaviorController>(true);
        mouth = behavior != null ? behavior.mouthParticles : null;
        if (mouth == null && behavior != null) mouth = behavior.GetComponent<ParticleSystem>();
        if (mouth == null)
        {
            foreach (var ps in GetComponentsInChildren<ParticleSystem>(true))
                if (ps.GetComponent<AmbientParticleMarker>() == null) { mouth = ps; break; }
        }
        bodyCollider = GetComponent<Collider>();
        navigation = GetComponent<NavigationAgent>();
        clock = navigation != null ? navigation.SimulationTime : FindFirstObjectByType<TimeManager>();
        var renderers = new List<Renderer>();
        foreach (var r in GetComponentsInChildren<Renderer>(true))
        {
            if (r is ParticleSystemRenderer) continue;
            renderers.Add(r);
            var block = new MaterialPropertyBlock();
            r.GetPropertyBlock(block);
            originals.Add(block);
        }
        bodies = renderers.ToArray();
        ApplyColors();
    }

    private void OnEnable() { if (!agents.Contains(this)) agents.Add(this); gridFrame = -1; }
    private void OnDisable() { agents.Remove(this); gridFrame = -1; }

    public void ResetForSpawn(bool initiallyInfected)
    {
        SpawnGeneration++;
        infected = initiallyInfected;
        InhaledParticles = InhaledInfectedParticles = 0;
        if (mouth != null) mouth.Clear();
        ApplyColors();
    }

    private void ApplyColors()
    {
        for (int i = 0; i < bodies.Length; i++)
        {
            if (infected)
            {
                bodies[i].GetPropertyBlock(tint);
                tint.SetColor("_BaseColor", Color.red);
                tint.SetColor("_Color", Color.red);
                bodies[i].SetPropertyBlock(tint);
            }
            else bodies[i].SetPropertyBlock(originals[i]);
        }
        if (mouth == null) return;
        var main = mouth.main;
        main.startColor = infected ? Color.red : Color.white;
        var lifetime = mouth.colorOverLifetime;
        lifetime.enabled = false;
        var speed = mouth.colorBySpeed;
        speed.enabled = false;
        var trails = mouth.trails;
        trails.inheritParticleColor = true;
    }

    private bool CanInhale => ParticleSimulationSettings.Enabled && IsPresent && mouth != null &&
        (clock == null || clock.IsRunning) &&
        (behavior == null || behavior.IsInhaling);

    private static Vector3Int Cell(Vector3 p) => new Vector3Int(
        Mathf.FloorToInt(p.x / CellSize), Mathf.FloorToInt(p.y / CellSize), Mathf.FloorToInt(p.z / CellSize));

    private static void BuildGrid()
    {
        if (gridFrame == Time.frameCount) return;
        gridFrame = Time.frameCount;
        foreach (var bucket in grid.Values) { bucket.Clear(); buckets.Push(bucket); }
        grid.Clear();
        foreach (var agent in agents)
        {
            if (!agent.CanInhale) continue;
            agent.mouthPosition = agent.mouth.transform.position;
            // Insert each receiver in every cell its capture sphere overlaps.
            // Particle queries then need one lookup instead of 27; the exact
            // distance test below still determines capture and nearest receiver.
            float radius = Mathf.Clamp(agent.inhalationRadius, 0.01f, CellSize);
            Vector3Int min = Cell(agent.mouthPosition - Vector3.one * radius);
            Vector3Int max = Cell(agent.mouthPosition + Vector3.one * radius);
            for (int x = min.x; x <= max.x; x++)
            for (int y = min.y; y <= max.y; y++)
            for (int z = min.z; z <= max.z; z++)
            {
                var key = new Vector3Int(x, y, z);
                if (!grid.TryGetValue(key, out var bucket))
                {
                    bucket = buckets.Count > 0 ? buckets.Pop() : new List<AgentHealth>();
                    grid.Add(key, bucket);
                }
                bucket.Add(agent);
            }
        }
    }

    private void LateUpdate()
    {
        if (!ParticleSimulationSettings.Enabled) return;
        using var profile = CaptureMarker.Auto();
        if (mouth == null || (clock != null && !clock.IsRunning) || Time.deltaTime <= 0f) return;
        BuildGrid();
        if (grid.Count == 0 || mouth.particleCount == 0) return;
        if (particles == null || particles.Length < mouth.main.maxParticles)
            particles = new ParticleSystem.Particle[mouth.main.maxParticles];
        int count = mouth.GetParticles(particles);
        bool changed = false;
        var main = mouth.main;
        Transform space = main.simulationSpace == ParticleSystemSimulationSpace.Custom
            ? main.customSimulationSpace : mouth.transform;
        for (int i = 0; i < count; i++)
        {
            if (particles[i].remainingLifetime <= 0f) continue;
            Vector3 pos = particles[i].position;
            if (main.simulationSpace != ParticleSystemSimulationSpace.World && space != null)
                pos = space.TransformPoint(pos);
            Vector3Int key = Cell(pos);
            AgentHealth nearest = FindReceiver(pos, key, this);
            if (nearest == null) continue;
            nearest.InhaledParticles++;
            if (infected) nearest.InhaledInfectedParticles++;
            particles[i].remainingLifetime = -1f;
            changed = true;
        }
        if (changed) mouth.SetParticles(particles, count);
    }

    private static AgentHealth FindReceiver(Vector3 pos, Vector3Int key, AgentHealth source)
    {
        AgentHealth nearest = null;
        float nearestDistance = float.PositiveInfinity;
        if (grid.TryGetValue(key, out var bucket))
        foreach (var receiver in bucket)
        {
            if (receiver == source) continue;
            float d = (pos - receiver.mouthPosition).sqrMagnitude;
            float radius = Mathf.Clamp(receiver.inhalationRadius, 0.01f, CellSize);
            if (d > radius * radius || d >= nearestDistance) continue;
            nearest = receiver; nearestDistance = d;
        }
        return nearest;
    }
}
