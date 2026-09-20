using System.Collections.Generic;
using UnityEngine;

// Local, approximate air entrainment. Added to the room velocity before droplet drag.
[DefaultExecutionOrder(-20)]
public class AgentBodyWake : MonoBehaviour
{
    [Header("Walking wake (world metres)")]
    [Range(0f, 10f)] public float strengthMultiplier = 6f;
    [Range(0.1f, 2f)] public float wakeRadius = 1.2f;
    [Tooltip("Velocity smoothing time in seconds; the wake fades when the agent stops.")]
    [Range(0.01f, 1f)] public float smoothing = 0.2f;
    public float bodyHeight = 0.9f;
    [Tooltip("Larger position jumps are treated as teleports, not gusts.")]
    public float maximumWalkingSpeed = 40f;
    [Tooltip("Caps entrained air speed so accelerated simulation walking does not create extreme wind.")]
    public float maximumWakeSpeed = 3f;

    [Header("Breathing (world metres)")]
    public Transform mouth;
    [Range(0.1f, 2f)] public float exhaleReach = 1.2f;
    [Range(0.1f, 1f)] public float inhaleReach = 0.55f;
    public Vector3 WalkingVelocity { get; private set; }
    public float BreathVelocity { get; private set; }
    public bool IsInhaling => isActiveAndEnabled && BreathVelocity < -0.001f;
    Vector3 lastPosition;
    Vector3 bodyPosition;
    Vector3 mouthPosition;
    Vector3 mouthForward;

    const float CellSize = 4f;
    static readonly List<AgentBodyWake> sources = new List<AgentBodyWake>();
    static readonly Dictionary<Vector3Int, List<AgentBodyWake>> grid = new Dictionary<Vector3Int, List<AgentBodyWake>>();
    static readonly Stack<List<AgentBodyWake>> pool = new Stack<List<AgentBodyWake>>();
    static int gridFrame = -1;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { sources.Clear(); grid.Clear(); pool.Clear(); gridFrame = -1; }
    void OnEnable()
    {
        lastPosition = transform.position; WalkingVelocity = Vector3.zero; BreathVelocity = 0;
        if (!sources.Contains(this)) sources.Add(this);
        CachePose(); gridFrame = -1;
    }
    void OnDisable() { sources.Remove(this); gridFrame = -1; BreathVelocity = 0; }
    void LateUpdate() { AdvanceWake(Time.deltaTime); }
    void AdvanceWake(float dt)
    {
        Vector3 delta = transform.position - lastPosition;
        lastPosition = transform.position;
        if (dt > 0)
        {
            Vector3 measured = delta / dt;
            // NavMesh warps/elevators should not fling the room's particles.
            if (measured.magnitude > Mathf.Max(0.1f, maximumWalkingSpeed)) WalkingVelocity = Vector3.zero;
            else
            {
                measured.y = 0;
                measured = Vector3.ClampMagnitude(measured, Mathf.Max(0, maximumWakeSpeed));
                WalkingVelocity = Vector3.Lerp(WalkingVelocity, measured, 1f - Mathf.Exp(-dt / Mathf.Max(0.01f, smoothing)));
            }
        }
        CachePose(); gridFrame = -1;
    }
    void CachePose()
    {
        bodyPosition = transform.position + Vector3.up * bodyHeight;
        mouthPosition = mouth != null ? mouth.position : transform.position + Vector3.up * 1.6f;
        mouthForward = mouth != null ? mouth.forward : transform.forward;
    }
    public void SetBreath(float signedSpeed)
    {
        BreathVelocity = Mathf.Clamp(signedSpeed, -4f, 30f);
        CachePose(); gridFrame = -1;
    }
    public Vector3 SampleLocalVelocity(Vector3 worldPosition)
    {
        Vector3 offset = worldPosition - bodyPosition;
        // Centre the moving air slightly behind the agent; preserve travel direction.
        offset += WalkingVelocity.normalized * (wakeRadius * 0.35f);
        float radius = Mathf.Max(0.1f, wakeRadius);
        float d2 = (offset.x * offset.x + offset.z * offset.z) / (radius * radius) + offset.y * offset.y / (0.9f * 0.9f);
        float falloff = Mathf.Max(0f, 1f - d2);
        Vector3 velocity = WalkingVelocity * (Mathf.Clamp01(strengthMultiplier * 0.12f) * falloff * falloff);
        offset = worldPosition - mouthPosition;
        float distance = offset.magnitude;
        float forward = Vector3.Dot(offset, mouthForward);
        if (BreathVelocity > 0 && forward >= -0.025f && forward < exhaleReach)
        {
            float axial = Mathf.Max(0, forward);
            float spread = 0.06f + axial * 0.3f;
            float lateral2 = Mathf.Max(0, offset.sqrMagnitude - forward * forward);
            float radial = Mathf.Max(0, 1f - lateral2 / (spread * spread));
            float along = Mathf.Clamp01(1f - axial / Mathf.Max(0.1f, exhaleReach));
            Vector3 direction = (mouthForward + Vector3.ProjectOnPlane(offset, mouthForward) * 0.4f).normalized;
            velocity += direction * (BreathVelocity * radial * radial * along * along / (1f + 2f * axial));
        }
        else if (BreathVelocity < 0 && distance < inhaleReach && forward >= -0.04f)
        {
            float inward = Mathf.Clamp01(1f - distance / Mathf.Max(0.1f, inhaleReach));
            velocity -= offset / Mathf.Max(distance, 0.015f) * (-BreathVelocity * inward * inward);
        }
        return velocity;
    }
    static Vector3Int Cell(Vector3 p) => new Vector3Int(Mathf.FloorToInt(p.x / CellSize), Mathf.FloorToInt(p.y / CellSize), Mathf.FloorToInt(p.z / CellSize));
    static void BuildGrid()
    {
        if (gridFrame == Time.frameCount) return;
        gridFrame = Time.frameCount;
        foreach (var bucket in grid.Values) { bucket.Clear(); pool.Push(bucket); }
        grid.Clear();
        foreach (var source in sources)
        {
            if (source == null || !source.isActiveAndEnabled) continue;
            float radius = Mathf.Max(source.wakeRadius * 1.35f, 0.9f);
            var bounds = new Bounds(source.bodyPosition, Vector3.one * radius * 2);
            float breathRadius = Mathf.Max(source.exhaleReach, source.inhaleReach);
            bounds.Encapsulate(source.mouthPosition + Vector3.one * breathRadius);
            bounds.Encapsulate(source.mouthPosition - Vector3.one * breathRadius);
            Vector3Int min = Cell(bounds.min), max = Cell(bounds.max);
            for (int x = min.x; x <= max.x; x++)
            for (int y = min.y; y <= max.y; y++)
            for (int z = min.z; z <= max.z; z++)
            {
                var key = new Vector3Int(x,y,z);
                if (!grid.TryGetValue(key, out var bucket)) { bucket = pool.Count > 0 ? pool.Pop() : new List<AgentBodyWake>(); grid.Add(key, bucket); }
                bucket.Add(source);
            }
        }
    }
    public static Vector3 SampleVelocity(Vector3 position)
    {
        BuildGrid();
        Vector3 velocity = Vector3.zero;
        if (grid.TryGetValue(Cell(position), out var bucket))
            foreach (var source in bucket) velocity += source.SampleLocalVelocity(position);
        return Vector3.ClampMagnitude(velocity, 30f);
    }
}
