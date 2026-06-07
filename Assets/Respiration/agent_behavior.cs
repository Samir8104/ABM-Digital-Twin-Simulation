using UnityEngine;
using UnityEngine.AI;

public class AgentBehaviorController : MonoBehaviour {

    [Header("References")]
    public ParticleSystem mouthParticles;

    [Header("Behavior Rates")]
    [SerializeField] private float sneezesPerHour = 2f;
    [SerializeField] private float coughsPerHour  = 10f;

    [Header("Speed-Breathing Coupling")]
    [SerializeField] private float maxWalkSpeed      = 1.4f;
    [SerializeField] private float maxVentMultiplier = 2.8f; 
    [Header("Debug")]
    [SerializeField] private bool debugControls = false;

    private bool         isTalking;
    private bool         isBursting;
    private float        nextSneezeTime;
    private float        nextCoughTime;
    private NavMeshAgent navAgent;

    void Awake() {
        if (mouthParticles == null)
            mouthParticles = GetComponentInChildren<ParticleSystem>();

        navAgent = GetComponent<NavMeshAgent>();

        var main = mouthParticles.main;
        main.simulationSpace     = ParticleSystemSimulationSpace.World;
        main.emitterVelocityMode = ParticleSystemEmitterVelocityMode.Transform;

        var inheritVelocity            = mouthParticles.inheritVelocity;
        inheritVelocity.enabled        = true;
        inheritVelocity.mode           = ParticleSystemInheritVelocityMode.Initial;
        inheritVelocity.curveMultiplier = 1.0f;
    }

    void Start() {
        ResumeBreathing();
        nextSneezeTime = Time.time + SampleExponential(sneezesPerHour);
        nextCoughTime  = Time.time + SampleExponential(coughsPerHour);
    }

    void Update() {
        if (Time.time >= nextSneezeTime) {
            Sneeze();
            nextSneezeTime = Time.time + SampleExponential(sneezesPerHour);
        }
        if (Time.time >= nextCoughTime) {
            Cough();
            nextCoughTime = Time.time + SampleExponential(coughsPerHour);
        }

        if (!isTalking && !isBursting)
            UpdateBreathingToSpeed();

        if (debugControls) {
            if (Input.GetKeyDown(KeyCode.Space)) Sneeze();
            if (Input.GetKeyDown(KeyCode.C))     Cough();
            if (Input.GetKeyDown(KeyCode.T)) {
                if (isTalking) StopTalking();
                else StartTalking();
            }
        }
    }

    void ApplyProfile(BehaviorEmissionProfile profile) {
        var main     = mouthParticles.main;
        var emission = mouthParticles.emission;
        var shape    = mouthParticles.shape;

        main.startSpeed       = profile.initialSpeed;
        main.startLifetime    = new ParticleSystem.MinMaxCurve(profile.startLifetime);
        emission.rateOverTime = profile.emissionRate;
        shape.angle           = profile.emissionAngle;
        shape.radius          = profile.mouthRadius;
    }

    void UpdateBreathingToSpeed() {
        float speed = navAgent != null ? navAgent.velocity.magnitude : 0f;
        float t = Mathf.Clamp01(speed / maxWalkSpeed);

        // Power law exponent ~1.2 for walking — ventilation rises faster than linearly
        // with speed but slower than VO2 (Adams 1993, EPA Exposure Factors Handbook)
        float ventMultiplier = Mathf.Lerp(1f, maxVentMultiplier, Mathf.Pow(t, 1.2f));

        var emission = mouthParticles.emission;
        var main     = mouthParticles.main;

        // Emission rate scales linearly with minute ventilation
        emission.rateOverTime = EmissionProfiles.Breathing.emissionRate * ventMultiplier;

        // Exhalation speed scales with sqrt of ventilation — higher flow rate
        // through the same mouth cross-section, not proportionally higher velocity
        main.startSpeed = EmissionProfiles.Breathing.initialSpeed * Mathf.Sqrt(ventMultiplier);
    }

    public void Sneeze() {
        CancelInvoke(nameof(ResumeBreathing));
        isBursting = true;
        ApplyProfile(EmissionProfiles.Sneezing);
        var emission = mouthParticles.emission;
        emission.enabled = false;
        mouthParticles.Emit(EmissionProfiles.Sneezing.burstCount);
        Invoke(nameof(ResumeBreathing), 1f);
    }

    public void Cough() {
        CancelInvoke(nameof(ResumeBreathing));
        isBursting = true;
        ApplyProfile(EmissionProfiles.Coughing);
        var emission = mouthParticles.emission;
        emission.enabled = false;
        mouthParticles.Emit(EmissionProfiles.Coughing.burstCount);
        Invoke(nameof(ResumeBreathing), 1f);
    }

    public void StartTalking() {
        isTalking = true;
        ApplyProfile(EmissionProfiles.Talking);
        var emission = mouthParticles.emission;
        emission.enabled = true;
    }

    public void StopTalking() {
        isTalking = false;
        ResumeBreathing();
    }

    void ResumeBreathing() {
        isBursting = false;
        ApplyProfile(EmissionProfiles.Breathing);
        var emission = mouthParticles.emission;
        emission.enabled = true;
        mouthParticles.Play();
    }

    float SampleExponential(float eventsPerHour) {
        float lambda = eventsPerHour / 3600f;
        return -Mathf.Log(Random.value) / lambda;
    }
}