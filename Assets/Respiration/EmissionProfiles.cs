// PLACEHOLDER VALUES!!!
[System.Serializable]
public struct BehaviorEmissionProfile {
    public float emissionRate;      // particles per second, continuous emission
    public int   burstCount;        // particles per cough/sneeze
    public float initialSpeed;      // m/s, exit velocity at mouth (relative to agent; walking velocity is added)
    public float particleSizeMin;   // m, does not currently apply
    public float particleSizeMax;   // m, does not currently apply
    public float quantaPerParticle; // infectious quanta per particle (disease-specific placeholder)
    public float emissionAngle;     // degrees, cone half-angle
    public float mouthRadius;       // meters
    public float startLifetime;     // seconds
}

// NOTE — quantaPerParticle:
// All quanta values are disease-agnostic placeholders scaled by relative transmission risk
// (sneezing > coughing > talking > breathing). A validated model requires a pathogen-specific
// quanta generation rate q (quanta/hr), typically from Wells-Riley back-calculation studies.
// These values will need to be replaced once a target pathogen is identified.

// NOTE — startLifetime:
// Physical airborne time follows Stokes' law: v_settle = (ρ d² g) / (18 μ).
// A 5 µm particle settles at ~0.0008 m/s, remaining airborne for ~30 min from 1.5 m height.
// Values here are conservative simplifications for simulation performance.
// In a ventilated room (6 ACH), effective removal time is ~10 min regardless of size.
// Lifetime should be treated as a visualization parameter; the Wells-Riley kernel
// handles true quanta decay analytically via ventilation rate.

public static class EmissionProfiles {

    public static BehaviorEmissionProfile Breathing = new BehaviorEmissionProfile {
        emissionRate    = 3.0f,
        burstCount      = 0, // Breathing is continuous; no burst event.
        initialSpeed    = 1.5f,
        particleSizeMin = 3e-7f,
        particleSizeMax = 5e-6f,
        quantaPerParticle = 0.01f,  // Placeholder — see NOTE above.
        emissionAngle   = 10f,
        mouthRadius     = 0.005f,
        startLifetime   = 60f
    };

    public static BehaviorEmissionProfile Talking = new BehaviorEmissionProfile {
        emissionRate    = 6.0f,
        burstCount      = 0, // Talking is continuous; no burst event.
        initialSpeed    = 2.0f,
        particleSizeMin = 3e-7f,
        particleSizeMax = 1e-4f,
        quantaPerParticle = 0.05f,  // Placeholder — see NOTE above. Higher than breathing reflecting greater particle count and vocal fold involvement.
        emissionAngle   = 15f, // Slightly wider than breathing due to mouth shape during speech.
        mouthRadius     = 0.015f,
        startLifetime   = 45f
    };

    public static BehaviorEmissionProfile Coughing = new BehaviorEmissionProfile {
        emissionRate    = 0f, // Coughing is episodic; continuous emission rate is zero.
        burstCount      = 3000,
        initialSpeed    = 11.0f,
        particleSizeMin = 1e-6f,
        particleSizeMax = 1e-3f,
        quantaPerParticle = 1.0f,
        emissionAngle   = 20f,
        mouthRadius     = 0.015f,
        startLifetime   = 15f
    };

    public static BehaviorEmissionProfile Sneezing = new BehaviorEmissionProfile {
        emissionRate    = 0f, // Sneezing is episodic; continuous emission rate is zero.
        burstCount      = 40000,
        initialSpeed    = 8.0f,
        particleSizeMin = 1e-6f,
        particleSizeMax = 1e-3f,
        quantaPerParticle = 2.0f,  // Placeholder — see NOTE above. Highest value reflecting the largest particle count and deepest respiratory involvement of any behavior.
        emissionAngle   = 30f,
        mouthRadius     = 0.020f,
        startLifetime   = 8f
    };
}