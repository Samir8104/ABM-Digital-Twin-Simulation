using System.Collections.Generic;
using UnityEngine;

// Moves each aerosol particle every frame. The ParticleSystem only draws them.
//
// Two kinds of particle share this script:
//   * Ambient room-air tracers  -> just follow the airflow (old behaviour, unchanged).
//   * Respiratory droplets       -> full droplet physics copied from AerosolSim:
//        size-dependent drag, gravity settling, Brownian jitter, evaporation.
//
// Which path a particle system uses is decided automatically: if the object has an
// AmbientParticleMarker it is treated as an ambient tracer; otherwise it gets the
// droplet physics.
[RequireComponent(typeof(ParticleSystem))]
public class AirflowParticleDriver : MonoBehaviour
{
    // ----------------------------------------------------------------------
    //  Airflow
    // ----------------------------------------------------------------------
    [Header("Airflow")]
    [Tooltip("Scales the sampled field velocity. 1 = physically accurate m/s.")]
    [Range(0f, 5f)]
    public float airflowStrength = 1f;

    // ----------------------------------------------------------------------
    //  Ambient-tracer settings (only used when this is an ambient particle system)
    // ----------------------------------------------------------------------
    [Header("Ambient tracer (old behaviour)")]
    [Tooltip("How fast ambient particles snap to the airflow (seconds).")]
    [Range(0.05f, 10f)]
    public float relaxationTime = 2f;
    [Tooltip("Random jitter for ambient particles. 0 = none.")]
    [Range(0f, 0.5f)]
    public float diffusionStrength = 0.02f;
    [Tooltip("Constant downward drift for ambient particles (m/s).")]
    [Range(0f, 0.2f)]
    public float settlingSpeed = 0.005f;

    // ----------------------------------------------------------------------
    //  Droplet physics constants (SI units, air ~20 C, water droplets).
    //  These are standard literature defaults copied from AerosolSim.
    //  Adjust + cite them for your pathogen / conditions before trusting output.
    // ----------------------------------------------------------------------
    [Header("Air / droplet properties (SI)")]
    public float airDensity = 1.204f;    // kg/m^3
    public float dropletDensity = 1000f;     // kg/m^3 (water)
    public float airViscosity = 1.81e-5f;  // Pa*s
    public float meanFreePath = 6.8e-8f;   // m  (air mean free path, for slip)
    public float airTemperature = 293f;      // K

    [Header("Evaporation (d^2 law)")]
    [Tooltip("Evaporation rate (m^2/s). ~8e-10 at 50% RH. 0 disables shrinking.")]
    public float evaporationRate = 8e-10f;
    [Tooltip("A droplet dries down to this fraction of its start diameter, then stops.")]
    [Range(0.1f, 1f)]
    public float residualFraction = 0.5f;

    [Header("Initial droplet size (lognormal, metres)")]
    [Tooltip("ln of the median diameter. -11.51 = ln(10 micrometres).")]
    public float logMeanDiameter = -11.51f;
    [Tooltip("ln of the geometric standard deviation. 0.7 ~ GSD 2.")]
    public float logStdDiameter = 0.7f;

    // ----------------------------------------------------------------------
    //  Optional: make the drawn dot shrink as the droplet evaporates.
    //  Real diameters are microscopic (~10 um), so we scale them up to be visible.
    //  Off by default so it won't fight your ParticleSystem's own size settings.
    // ----------------------------------------------------------------------
    [Header("Visuals (optional)")]
    public bool matchDotSizeToDroplet = false;
    [Tooltip("Drawn size = droplet diameter (m) * this. 1000 => a 10 um droplet draws as ~1 cm.")]
    public float dotSizeScale = 1000f;

    [Header("Debug")]
    [Tooltip("Ambient path only: move by the raw field, skip the relaxation blend.")]
    public bool debugPositionMode = false;

    // Fixed physical constants
    const float kB = 1.380649e-23f;   // Boltzmann constant
    const float PI = 3.14159265f;
    static readonly Vector3 gravity = new Vector3(0f, -9.81f, 0f);

    // Set true automatically for ambient particle systems (see EnsureClassified()).
    bool _ambientMode = false;
    bool _classified = false;

    // Unity plumbing
    ParticleSystem _ps;
    ParticleSystem.Particle[] _particles;

    // Per-particle memory. Each live droplet remembers its CURRENT and ORIGINAL
    // diameter, keyed by the particle's randomSeed (stable for the particle's life).
    // We need the original because evaporation stops at a fraction of where it started.
    struct DropletState { public float diameter; public float diameter0; }
    Dictionary<uint, DropletState> _state = new Dictionary<uint, DropletState>();
    HashSet<uint> _seenThisFrame = new HashSet<uint>();
    List<uint> _toRemove = new List<uint>();

    void Awake()
    {
        _ps = GetComponent<ParticleSystem>();
        _particles = new ParticleSystem.Particle[_ps.main.maxParticles];

        var main = _ps.main;
        if (main.simulationSpace != ParticleSystemSimulationSpace.World)
            Debug.LogWarning("[AirflowParticleDriver] Set the Particle System Simulation Space to 'World'.");
        if (main.gravityModifier.constant != 0f)
            Debug.LogWarning("[AirflowParticleDriver] Set the Particle System Gravity Modifier to 0 — " +
                             "this script applies gravity itself, or it will be double-counted.");
    }

    // Decide ambient-vs-droplet on the first movement frame, NOT in Start().
    // AmbientFillEmitter adds the AmbientParticleMarker AFTER it adds this driver,
    // so the marker isn't reliably present when Start() runs. Checking here
    // guarantees it's there, whatever order the components were added in.
    void EnsureClassified()
    {
        if (_classified) return;
        _classified = true;

        var marker = GetComponent<AmbientParticleMarker>();
        if (marker != null)
        {
            _ambientMode = true;
            relaxationTime = marker.Tau;
        }
        Debug.Log($"[AirflowParticleDriver] '{name}' mode = {(_ambientMode ? "ambient tracer" : "droplet physics")}");
    }

    void LateUpdate()
    {
        EnsureClassified();
        if (VelocityFieldLoader.Instance == null) return;

        int count = _ps.GetParticles(_particles);
        if (count == 0) return;

        float dt = Time.deltaTime;
        float blend = Mathf.Clamp01(dt / relaxationTime);   // ambient path only
        _seenThisFrame.Clear();

        for (int i = 0; i < count; i++)
        {
            Vector3 pos = _particles[i].position;
            Vector3 uFluid = VelocityFieldLoader.Instance.SampleVelocity(pos) * airflowStrength;

            // ============================================================
            //  AMBIENT TRACERS — unchanged old behaviour
            // ============================================================
            if (_ambientMode)
            {
                if (debugPositionMode)
                {
                    _particles[i].position = pos + uFluid * dt;
                }
                else
                {
                    _particles[i].velocity = Vector3.Lerp(_particles[i].velocity, uFluid, blend);
                    if (diffusionStrength > 0f)
                        _particles[i].velocity += new Vector3(Gauss(), Gauss(), Gauss()) * diffusionStrength;
                    _particles[i].velocity += Vector3.down * settlingSpeed * dt;
                }
                continue;
            }

            // ============================================================
            //  RESPIRATORY DROPLETS — real physics (copied from AerosolSim)
            // ============================================================

            // 1) Remember this droplet's size. First time we see it, roll a
            //    starting diameter from the lognormal distribution (~10 um median).
            uint seed = _particles[i].randomSeed;
            _seenThisFrame.Add(seed);
            DropletState s;
            if (!_state.TryGetValue(seed, out s))
            {
                float d0 = SampleLognormalDiameter();
                s = new DropletState { diameter = d0, diameter0 = d0 };
                _state[seed] = s;
            }
            float d = Mathf.Max(s.diameter, 1e-9f);

            // 2) How quickly this droplet follows the air (its "drag response time").
            //    Tiny droplets -> tiny time -> track the air almost perfectly.
            //    Big droplets  -> long time -> lag behind and fall.
            float Cc = CunninghamSlip(d);                                       // slip: matters sub-micron
            float tau = Mathf.Max(dropletDensity * d * d * Cc / (18f * airViscosity), 1e-7f);

            // Extra drag for bigger/faster droplets (Schiller-Naumann).
            Vector3 uRel = uFluid - _particles[i].velocity;
            float Re = airDensity * uRel.magnitude * d / airViscosity;
            float f = 1f + 0.15f * Mathf.Pow(Mathf.Max(Re, 1e-6f), 0.687f);
            float tauEff = tau / f;

            // 3) Gravity, slightly reduced by the buoyancy of the air.
            Vector3 gEff = gravity * (1f - airDensity / dropletDensity);

            // 4) New velocity. This is the physically-correct version of the old
            //    "blend toward the airflow" — the blend rate now comes from the
            //    droplet's own size instead of a slider, and gravity is folded in.
            float a = dt / tauEff;
            _particles[i].velocity = (_particles[i].velocity + a * uFluid + dt * gEff) / (1f + a);

            // 5) Brownian motion: tiny random nudge to the position. Only meaningful
            //    for very small droplets; negligible for big ones.
            float D = kB * airTemperature * Cc / (3f * PI * airViscosity * d);
            float sig = Mathf.Sqrt(2f * D * dt);
            _particles[i].position += new Vector3(Gauss(), Gauss(), Gauss()) * sig;

            // 6) Evaporation: the droplet shrinks toward a dry residue and then stops.
            //    Smaller diameter next frame -> lighter -> follows the air more -> the
            //    classic "small droplets float away, big ones fall" behaviour emerges.
            if (evaporationRate > 0f)
            {
                float d2 = d * d - evaporationRate * dt;
                float dmin = s.diameter0 * residualFraction;
                s.diameter = Mathf.Sqrt(Mathf.Max(d2, dmin * dmin));
                _state[seed] = s;
            }

            // 7) Optional: shrink the drawn dot to match (scaled up so it's visible).
            if (matchDotSizeToDroplet)
                _particles[i].startSize = s.diameter * dotSizeScale;
        }

        _ps.SetParticles(_particles, count);

        if (!_ambientMode) PruneDeadDroplets();
    }

    // Cunningham slip correction — lets sub-micron droplets slip through the air.
    float CunninghamSlip(float d)
    {
        float Kn = 2f * meanFreePath / Mathf.Max(d, 1e-9f);
        return 1f + Kn * (1.257f + 0.4f * Mathf.Exp(-1.1f / Mathf.Max(Kn, 1e-9f)));
    }

    float SampleLognormalDiameter()
    {
        return Mathf.Exp(logMeanDiameter + logStdDiameter * Gauss());
    }

    // Standard normal random number (Box-Muller).
    static float Gauss()
    {
        float u1 = Mathf.Max(Random.value, 1e-7f);
        float u2 = Random.value;
        return Mathf.Sqrt(-2f * Mathf.Log(u1)) * Mathf.Cos(2f * PI * u2);
    }

    // Forget droplets whose particles have died so the memory table can't grow forever.
    void PruneDeadDroplets()
    {
        if (_state.Count <= _seenThisFrame.Count) return;
        _toRemove.Clear();
        foreach (var kv in _state)
            if (!_seenThisFrame.Contains(kv.Key)) _toRemove.Add(kv.Key);
        for (int i = 0; i < _toRemove.Count; i++) _state.Remove(_toRemove[i]);
    }
}