using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

// Short moving pathlines sample the same time-dependent field as aerosols.
public class VelocityFieldVisualizer : MonoBehaviour
{
    public bool showArrows = false; // retained serialized toggle name
    public KeyCode toggleKey = KeyCode.V;
    [Range(16, 2000)] public int wispCount = 450;
    [Range(3, 32)] public int trailPoints = 12;
    public float trailSampleSeconds = 0.08f;
    public float lifetime = 8f;
    public float lineWidth = 0.025f;
    public Material lineMaterial;
    class Wisp { public LineRenderer line; public Vector3 pos; public Vector3[] path; public float age, clock; public int count; }
    Wisp[] wisps;
    Material ownedMaterial;
    GameObject container;

    void Update()
    {
        bool toggle = false;
#if ENABLE_INPUT_SYSTEM
        toggle = Keyboard.current != null && Keyboard.current.vKey.wasPressedThisFrame;
#elif ENABLE_LEGACY_INPUT_MANAGER
        toggle = Input.GetKeyDown(toggleKey);
#endif
        if (toggle) showArrows = !showArrows;
        var field = VelocityFieldLoader.Instance;
        if (field == null || !field.IsLoaded) { if (container != null) container.SetActive(false); return; }
        if (showArrows && wisps == null) Build(field);
        if (container == null) return;
        container.SetActive(showArrows);
        if (!showArrows) return;
        float dt = Time.deltaTime;
        foreach (var w in wisps)
        {
            w.age += dt;
            Vector3 velocity = field.SampleVelocity(w.pos);
            if (w.age > lifetime || !field.IsInBounds(w.pos) || velocity.sqrMagnitude < 1e-8f)
            { Seed(w, field); continue; }
            // Midpoint integration with small steps keeps lines on curved flow paths.
            float remaining = dt;
            while (remaining > 0)
            {
                float step = Mathf.Min(remaining, 0.02f);
                velocity = field.SampleVelocity(w.pos);
                w.pos += field.SampleVelocity(w.pos + velocity * (step * 0.5f)) * step;
                remaining -= step;
            }
            if (!field.IsInBounds(w.pos)) { Seed(w, field); continue; }
            w.clock += dt;
            if (w.clock >= Mathf.Max(0.01f, trailSampleSeconds))
            {
                w.clock = 0;
                for (int j = w.path.Length - 1; j > 0; j--) w.path[j] = w.path[j-1];
                w.count = Mathf.Min(w.count + 1, w.path.Length);
            }
            w.path[0] = w.pos;
            w.line.positionCount = w.count;
            for (int j = 0; j < w.count; j++) w.line.SetPosition(j, w.path[j]);
            float speed = field.recordedField == null ? velocity.magnitude : field.transform.InverseTransformVector(velocity).magnitude;
            float strength = Mathf.Clamp01(speed / Mathf.Max(field.MaximumSpeed, 0.0001f));
            Color color = strength < 0.5f ? Color.Lerp(Color.blue, Color.green, strength * 2f) : Color.Lerp(Color.green, Color.red, (strength - 0.5f) * 2f);
            w.line.startColor = color;
            color.a = 0; w.line.endColor = color;
        }
    }
    void Build(VelocityFieldLoader field)
    {
        container = new GameObject("Airflow wisps (V)");
        container.transform.SetParent(transform, false);
        Material material = lineMaterial;
        if (material == null)
        {
            var shader = Shader.Find("Airflow/Wisp");
            if (shader == null) { Debug.LogError("Airflow wisp shader missing."); return; }
            ownedMaterial = material = new Material(shader);
        }
        wisps = new Wisp[Mathf.Clamp(wispCount, 16, 2000)];
        for (int i = 0; i < wisps.Length; i++)
        {
            var obj = new GameObject("Wisp"); obj.transform.SetParent(container.transform, false);
            var line = obj.AddComponent<LineRenderer>();
            line.sharedMaterial = material; line.useWorldSpace = true;
            line.startWidth = lineWidth; line.endWidth = 0;
            line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            line.receiveShadows = false;
            var w = new Wisp { line = line, path = new Vector3[Mathf.Clamp(trailPoints, 3, 32)] };
            wisps[i] = w; Seed(w, field); w.age = Random.value * lifetime;
        }
    }
    void Seed(Wisp w, VelocityFieldLoader field)
    {
        w.pos = field.recordedField != null
            ? field.transform.TransformPoint(field.recordedField.positions[Random.Range(0, field.recordedField.positions.Length)])
            : new Vector3(Random.Range(field.xMin, field.xMax), Random.Range(field.yMin, field.yMax), Random.Range(field.zMin, field.zMax));
        w.age = 0; w.clock = 0; w.count = 1;
        for (int i = 0; i < w.path.Length; i++) w.path[i] = w.pos;
        w.line.positionCount = 1; w.line.SetPosition(0, w.pos);
    }
    void OnDisable() { if (container != null) container.SetActive(false); }
    void OnDestroy() { if (ownedMaterial != null) Destroy(ownedMaterial); if (container != null) Destroy(container); }
}
