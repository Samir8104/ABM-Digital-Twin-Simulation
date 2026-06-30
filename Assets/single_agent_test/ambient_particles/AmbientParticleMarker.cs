using UnityEngine;

/// <summary>
/// Attached alongside a ParticleSystem spawned by AmbientFillEmitter.
/// Stores the physical parameters for that emitter so AirflowParticleDriver
/// (or any other consumer) can read them without parsing the CSV again.
/// </summary>
public class AmbientParticleMarker : MonoBehaviour
{
    [Tooltip("Matches the emitter_id column in the CSV.")]
    public string EmitterId;

    [Tooltip("Velocity relaxation time constant (seconds). " +
             "Smaller = snaps to field faster. " +
             "Physically: τ = (ρ_p · d_p²) / (18 · μ_air)")]
    public float Tau = 0.1f;

    [Tooltip("Physical particle diameter in micrometers (µm). " +
             "Used for Stokes settling velocity and Wells-Riley quanta calculations.")]
    public float ParticleSizeUm = 1f;

    /// <summary>
    /// Stokes settling velocity (m/s, positive = downward) derived from particle size.
    /// Uses standard air properties at 20°C: ρ_air = 1.204 kg/m³, μ = 1.81e-5 Pa·s,
    /// ρ_particle ≈ 1100 kg/m³ (respiratory aerosol, ~water density).
    /// v_s = (ρ_p · g · d_p²) / (18 · μ)
    /// </summary>
    public float SettlingVelocity
    {
        get
        {
            const float rhoParticle = 1100f;    // kg/m³
            const float g           = 9.81f;    // m/s²
            const float mu          = 1.81e-5f; // Pa·s, dynamic viscosity of air at 20°C
            float dp = ParticleSizeUm * 1e-6f;  // µm → m
            return (rhoParticle * g * dp * dp) / (18f * mu);
        }
    }
}