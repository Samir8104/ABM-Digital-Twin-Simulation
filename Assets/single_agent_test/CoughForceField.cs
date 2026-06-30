using UnityEngine;

/// <summary>
/// Spawns a temporary repulsive force field at the agent's mouth on cough or sneeze,
/// pushing nearby ambient particles outward to simulate jet displacement.
///
/// USAGE — add one line to AgentBehaviorController:
///   In Cough():  CoughForceField.Spawn(mouthParticles.transform.position, CoughForceField.Preset.Cough);
///   In Sneeze(): CoughForceField.Spawn(mouthParticles.transform.position, CoughForceField.Preset.Sneeze);
///
/// SETUP:
///   Ambient particle systems must have External Forces module enabled to respond.
///   AmbientFillEmitter enables this automatically on every system it spawns.
/// </summary>
public class CoughForceField : MonoBehaviour
{
    public enum Preset { Cough, Sneeze }

    // -----------------------------------------------------------------------
    // Presets
    // -----------------------------------------------------------------------

    private static readonly float[] PeakStrength = { 2f,  3f  }; // Cough, Sneeze
    private static readonly float[] Radius       = { 3.0f, 4.5f };
    private static readonly float[] Lifetime     = { 2.0f, 3.0f };

    private static int _counter = 0;

    // -----------------------------------------------------------------------
    // Spawn
    // -----------------------------------------------------------------------

    public static void Spawn(UnityEngine.Vector3 worldPosition, Preset preset = Preset.Cough)
    {
        int i = (int)preset;

        var obj = new UnityEngine.GameObject($"CoughFF_{_counter++}");
        obj.transform.position = worldPosition;

        var ff        = obj.AddComponent<UnityEngine.ParticleSystemForceField>();
        ff.shape      = UnityEngine.ParticleSystemForceFieldShape.Sphere;
        ff.endRange   = Radius[i];
        ff.startRange = 0f;
        ff.gravity    = new UnityEngine.ParticleSystem.MinMaxCurve(-PeakStrength[i]);
        ff.gravityFocus = 0f;

        var c              = obj.AddComponent<CoughForceField>();
        c._ff              = ff;
        c._lifetime        = Lifetime[i];
        c._peakStrength    = PeakStrength[i];
    }

    // -----------------------------------------------------------------------
    // Decay + self-destruct
    // -----------------------------------------------------------------------

    private UnityEngine.ParticleSystemForceField _ff;
    private float _lifetime;
    private float _elapsed;
    private float _peakStrength;

    void Update()
    {
        _elapsed += UnityEngine.Time.deltaTime;

        float t = UnityEngine.Mathf.Clamp01(_elapsed / _lifetime);
        _ff.gravity = new UnityEngine.ParticleSystem.MinMaxCurve(
            -UnityEngine.Mathf.Lerp(_peakStrength, 0f, t)
        );

        if (_elapsed >= _lifetime)
            Destroy(gameObject);
    }
}