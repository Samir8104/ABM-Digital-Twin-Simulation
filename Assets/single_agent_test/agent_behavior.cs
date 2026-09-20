using UnityEngine;
using UnityEngine.AI;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

public class AgentBehaviorController : MonoBehaviour
{
    [Header("References")]
    public ParticleSystem mouthParticles;
    [Header("Behavior Rates")]
    [SerializeField] private float sneezesPerHour = 2f;
    [SerializeField] private float coughsPerHour = 10f;
    [Header("Speed-Breathing Coupling")]
    [SerializeField] private float maxWalkSpeed = 1.4f;
    [SerializeField] private float maxVentMultiplier = 2.8f;
    [Header("Breathing cycle")]
    [Min(1f)] public float breathPeriod = 4f;
    [Range(0.3f, 0.7f)] public float exhaleFraction = 0.55f;
    [Range(0.1f, 4f)] public float inhaleSpeed = 1.2f;
    public bool IsInhaling => isActiveAndEnabled && wake != null && wake.IsInhaling;
    [Header("Debug")]
    [SerializeField] private bool debugControls = false;

    bool isTalking;
    bool isBursting;
    float nextSneezeTime, nextCoughTime, phase, burstRemaining, burstSpeed;
    NavMeshAgent navAgent;
    AgentBodyWake wake;

    void Awake()
    {
        if (mouthParticles == null) mouthParticles = GetComponentInChildren<ParticleSystem>();
        if (mouthParticles == null) { Debug.LogWarning("Breathing requires a mouth ParticleSystem.", this); enabled = false; return; }
        navAgent = GetComponentInParent<NavMeshAgent>();
        wake = GetComponentInParent<AgentBodyWake>();
        if (wake == null) wake = (navAgent != null ? navAgent.gameObject : gameObject).AddComponent<AgentBodyWake>();
        wake.mouth = mouthParticles.transform;
        if (mouthParticles.GetComponent<AirflowParticleDriver>() == null) mouthParticles.gameObject.AddComponent<AirflowParticleDriver>();
        var main = mouthParticles.main;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.emitterVelocityMode = ParticleSystemEmitterVelocityMode.Transform;
        var inherit = mouthParticles.inheritVelocity;
        inherit.enabled = true; inherit.mode = ParticleSystemInheritVelocityMode.Initial; inherit.curveMultiplier = 1;
    }
    void OnEnable()
    {
        if (mouthParticles == null || wake == null) return;
        phase = Random.value; isTalking = false; isBursting = false;
        ResumeBreathing(); AdvanceRespiration(0);
        nextSneezeTime = Time.time + SampleExponential(sneezesPerHour);
        nextCoughTime = Time.time + SampleExponential(coughsPerHour);
    }
    void OnDisable()
    {
        if (wake != null) wake.SetBreath(0);
        if (mouthParticles != null) { var emission = mouthParticles.emission; emission.enabled = false; }
    }
    void Update()
    {
        if (Time.time >= nextSneezeTime) { Sneeze(); nextSneezeTime = Time.time + SampleExponential(sneezesPerHour); }
        if (Time.time >= nextCoughTime) { Cough(); nextCoughTime = Time.time + SampleExponential(coughsPerHour); }
        AdvanceRespiration(Time.deltaTime);
        if (!debugControls) return;
#if ENABLE_INPUT_SYSTEM
        var keyboard = Keyboard.current;
        if (keyboard == null) return;
        if (keyboard.spaceKey.wasPressedThisFrame) Sneeze();
        if (keyboard.cKey.wasPressedThisFrame) Cough();
        if (keyboard.tKey.wasPressedThisFrame) { if (isTalking) StopTalking(); else StartTalking(); }
#elif ENABLE_LEGACY_INPUT_MANAGER
        if (Input.GetKeyDown(KeyCode.Space)) Sneeze();
        if (Input.GetKeyDown(KeyCode.C)) Cough();
        if (Input.GetKeyDown(KeyCode.T)) { if (isTalking) StopTalking(); else StartTalking(); }
#endif
    }
    void ApplyProfile(BehaviorEmissionProfile profile)
    {
        var main = mouthParticles.main; var emission = mouthParticles.emission; var shape = mouthParticles.shape;
        main.startSpeed = profile.initialSpeed; main.startLifetime = profile.startLifetime;
        emission.rateOverTime = profile.emissionRate;
        shape.enabled = true; shape.shapeType = ParticleSystemShapeType.Cone;
        shape.angle = profile.emissionAngle; shape.radius = profile.mouthRadius;
    }
    void AdvanceRespiration(float dt)
    {
        if (mouthParticles == null || wake == null) return;
        if (isBursting)
        {
            float consumed = Mathf.Min(Mathf.Max(0, dt), burstRemaining);
            burstRemaining -= consumed;
            dt -= consumed;
            wake.SetBreath(burstSpeed * burstRemaining * burstRemaining);
            if (burstRemaining > 0) return;
            ResumeBreathing();
        }
        float speed = wake.WalkingVelocity.magnitude;
        float vent = Mathf.Lerp(1f, Mathf.Max(1, maxVentMultiplier), Mathf.Pow(Mathf.Clamp01(speed / Mathf.Max(0.1f, maxWalkSpeed)), 1.2f));
        phase = Mathf.Repeat(phase + dt * Mathf.Sqrt(vent) / Mathf.Max(1f, breathPeriod), 1f);
        float fraction = Mathf.Clamp(exhaleFraction, 0.3f, 0.7f);
        bool exhaling = phase < fraction;
        float pulse = Mathf.Sin(Mathf.PI * (exhaling ? phase / fraction : (phase - fraction) / (1f - fraction)));
        var profile = isTalking ? EmissionProfiles.Talking : EmissionProfiles.Breathing;
        var emission = mouthParticles.emission; var main = mouthParticles.main;
        emission.enabled = exhaling;
        // Maintain the profile's approximate cycle-averaged emission count.
        emission.rateOverTime = profile.emissionRate * vent * pulse * Mathf.PI / (2f * fraction);
        main.startSpeed = exhaling ? profile.initialSpeed * Mathf.Sqrt(vent) * pulse : 0;
        wake.SetBreath((exhaling ? profile.initialSpeed : -inhaleSpeed) * Mathf.Sqrt(vent) * pulse);
    }
    public void Sneeze() { Burst(EmissionProfiles.Sneezing); }
    public void Cough() { Burst(EmissionProfiles.Coughing); }
    void Burst(BehaviorEmissionProfile profile)
    {
        if (mouthParticles == null || wake == null) return;
        isBursting = true; burstRemaining = 1; burstSpeed = profile.initialSpeed;
        ApplyProfile(profile);
        var emission = mouthParticles.emission; emission.enabled = false;
        mouthParticles.Emit(profile.burstCount);
        wake.SetBreath(burstSpeed);
    }
    public void StartTalking() { isTalking = true; if (!isBursting) { ApplyProfile(EmissionProfiles.Talking); AdvanceRespiration(0); } }
    public void StopTalking() { isTalking = false; if (!isBursting) { ApplyProfile(EmissionProfiles.Breathing); AdvanceRespiration(0); } }
    void ResumeBreathing()
    {
        isBursting = false;
        ApplyProfile(isTalking ? EmissionProfiles.Talking : EmissionProfiles.Breathing);
        mouthParticles.Play();
    }
    float SampleExponential(float eventsPerHour) => eventsPerHour <= 0 ? float.PositiveInfinity :
        -Mathf.Log(Mathf.Max(Random.value, 1e-7f)) / (eventsPerHour / 3600f);
}
