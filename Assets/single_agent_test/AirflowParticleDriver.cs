using UnityEngine;

[RequireComponent(typeof(ParticleSystem))]
public class AirflowParticleDriver : MonoBehaviour
{
    [Header("Velocity Relaxation")]
    [Tooltip("Jet dissipation time constant (seconds). ~0.5s = fast snap, ~3s = slow blend.")]
    [Range(0.05f, 10f)]
    public float relaxationTime = 2f;

    [Header("Airflow")]
    [Tooltip("Scales the sampled field velocity. 1 = physically accurate m/s.")]
    [Range(0f, 5f)]
    public float airflowStrength = 1f;

    [Header("Diffusion")]
    [Tooltip("Brownian / turbulent diffusion. 0 = none.")]
    [Range(0f, 0.5f)]
    public float diffusionStrength = 0.02f;

    [Header("Gravity Settling")]
    [Tooltip("Downward drift (m/s). Fine aerosols ~0.001, larger droplets ~0.01-0.1.")]
    [Range(0f, 0.2f)]
    public float settlingSpeed = 0.005f;

    [Header("Debug")]
    public bool debugPositionMode = false;

    private ParticleSystem            _ps;
    private ParticleSystem.Particle[] _particles;

  void Awake()
{
    _ps = GetComponent<ParticleSystem>();
    _particles = new ParticleSystem.Particle[_ps.main.maxParticles]; // moved here

    if (_ps.main.simulationSpace != ParticleSystemSimulationSpace.World)
        Debug.LogWarning("[AirflowParticleDriver] Simulation Space must be 'World'.");
}

void Start()
{
    var marker = GetComponent<AmbientParticleMarker>();
    if (marker != null) relaxationTime = marker.Tau;

    Debug.Log($"[AirflowParticleDriver] '{name}' τ = {relaxationTime:F2}s");
}

    void LateUpdate()
    {
        if (VelocityFieldLoader.Instance == null) return;

        int count = _ps.GetParticles(_particles);
        if (count == 0) return;

        float dt    = Time.deltaTime;
        float blend = Mathf.Clamp01(dt / relaxationTime);

        for (int i = 0; i < count; i++)
        {
            Vector3 rawSample = VelocityFieldLoader.Instance.SampleVelocity(_particles[i].position);
            Vector3 vField    = rawSample * airflowStrength;

            if (i == 0) Debug.Log($"raw: {rawSample}, strength: {airflowStrength}, vField: {vField}");

            if (debugPositionMode)
            {
                _particles[i].position += vField * dt;
            }
            else
            {
                _particles[i].velocity = Vector3.Lerp(_particles[i].velocity, vField, blend);

                if (diffusionStrength > 0f)
                {
                    _particles[i].velocity += new Vector3(
                        GaussianRandom() * diffusionStrength,
                        GaussianRandom() * diffusionStrength,
                        GaussianRandom() * diffusionStrength
                    );
                }

                _particles[i].velocity += Vector3.down * settlingSpeed * dt;
            }
        }

        _ps.SetParticles(_particles, count);
    }

    static float GaussianRandom()
    {
        float u1 = Mathf.Max(Random.value, 1e-6f);
        float u2 = Random.value;
        return Mathf.Sqrt(-2f * Mathf.Log(u1)) * Mathf.Cos(2f * Mathf.PI * u2);
    }
}