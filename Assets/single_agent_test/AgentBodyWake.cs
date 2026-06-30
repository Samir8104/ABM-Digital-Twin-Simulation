using UnityEngine;

/// <summary>
/// Attaches a persistent radial force field to the agent that pushes nearby
/// ambient particles outward as the agent moves through them.
/// Force strength scales with movement speed — zero when still, stronger when walking.
///
/// SETUP:
///   1. Add this script to the agent prefab.
///   2. No other setup needed — the force field child object is created at runtime.
/// </summary>
public class AgentBodyWake : MonoBehaviour
{
    [Header("Wake Settings")]
    [Tooltip("Force multiplier. Increase if particles aren't displaced enough.")]
    [Range(0f, 10f)]
    public float strengthMultiplier = 6f;

    [Tooltip("Radius of the displacement field around the agent body (meters).")]
    [Range(0.1f, 2f)]
    public float wakeRadius = 2f;

    [Tooltip("Smoothing for force changes — prevents snapping when agent starts/stops.")]
    [Range(0.01f, 1f)]
    public float smoothing = 0.1f;

    // -----------------------------------------------------------------------
    // Internal
    // -----------------------------------------------------------------------

    private ParticleSystemForceField _ff;
    private float                    _currentStrength;
    private Vector3                  _lastPosition;

    // -----------------------------------------------------------------------
    // Lifecycle
    // -----------------------------------------------------------------------

    void Awake()
    {
        _lastPosition = transform.position;

        var ffObj = new GameObject("BodyWakeForceField");
        ffObj.transform.SetParent(transform, worldPositionStays: false);
        ffObj.transform.localPosition = new Vector3(0f, 0.9f, 0f);

        _ff              = ffObj.AddComponent<ParticleSystemForceField>();
        _ff.shape        = ParticleSystemForceFieldShape.Sphere;
        _ff.endRange     = wakeRadius;
        _ff.startRange   = 0f;
        _ff.gravityFocus = 0f;
        _ff.gravity      = new ParticleSystem.MinMaxCurve(0f);
    }

    void Update()
    {
      float speed          = (transform.position - _lastPosition).magnitude / Time.deltaTime;
     _lastPosition        = transform.position;

      float targetStrength = speed * strengthMultiplier;
      _currentStrength     = Mathf.Lerp(_currentStrength, targetStrength, smoothing);

      Debug.Log($"[BodyWake] {GetInstanceID()} pos: {transform.position}, speed: {speed:F2}, strength: {_currentStrength:F2}");

     _ff.gravity  = new ParticleSystem.MinMaxCurve(-_currentStrength);
      _ff.endRange = wakeRadius;
    }
}