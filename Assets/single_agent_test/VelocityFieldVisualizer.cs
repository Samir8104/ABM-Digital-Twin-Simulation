using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Draws a 3D grid of arrows showing the velocity field's direction and magnitude
/// using real LineRenderer GameObjects — visible in both Scene AND Game view
/// (and therefore in screen recordings) with no Editor toggles required.
///
/// SETUP:
///   1. Add this script to any GameObject in the scene (e.g. same one as VelocityFieldLoader).
///   2. Assign a simple unlit material in "Line Material" (optional — a default
///      one is created automatically if left blank).
///   3. Press the toggle key (default 'V') during Play mode, or check
///      "Show Arrows" in the Inspector.
///
/// PERFORMANCE NOTE:
///   This builds a full 3D grid (X x Y x Z). With wide bounds, a small
///   gridSpacing can produce tens of thousands of objects and freeze the
///   editor. Start with gridSpacing = 5 and decrease only if performance
///   allows.
/// </summary>
public class VelocityFieldVisualizer : MonoBehaviour
{
    [Header("Toggle")]
    public bool showArrows = false;
    public KeyCode toggleKey = KeyCode.V;

    [Header("Grid")]
    [Tooltip("Spacing between sample points in X, Y, and Z (meters). " +
             "Start at 5 — lower values can create tens of thousands of objects.")]
    public float gridSpacing = 5f;

    [Tooltip("Skip drawing an arrow if field speed is below this (m/s).")]
    public float minSpeedToShow = 0.02f;

    [Header("Appearance")]
    public float arrowScale = 2f;
    public Color arrowColor = Color.cyan;
    public float lineWidth = 0.03f;
    [Tooltip("Size of the arrowhead chevron relative to arrow length.")]
    public float arrowHeadSize = 0.25f;

    [Header("Material (optional)")]
    [Tooltip("Leave blank to auto-create a simple unlit colored material.")]
    public Material lineMaterial;

    // -----------------------------------------------------------------------

    private GameObject _container;
    private readonly List<LineRenderer> _pool = new();
    private bool _built = false;

    void Update()
    {
        if (Input.GetKeyDown(toggleKey))
            showArrows = !showArrows;

        if (showArrows && !_built)
            BuildArrows();

        if (_container != null)
            _container.SetActive(showArrows);
    }

    void BuildArrows()
    {
        var loader = VelocityFieldLoader.Instance;
        if (loader == null || !loader.IsLoaded) return;

        _container = new GameObject("VelocityFieldArrows");
        _container.transform.SetParent(transform, false);

        Material mat = lineMaterial;
        if (mat == null)
        {
            mat = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
            if (mat.shader == null) mat = new Material(Shader.Find("Unlit/Color"));
            mat.color = arrowColor;
        }

        int count = 0;

        for (float y = loader.yMin; y <= loader.yMax; y += gridSpacing)
        {
            for (float x = loader.xMin; x <= loader.xMax; x += gridSpacing)
            {
                for (float z = loader.zMin; z <= loader.zMax; z += gridSpacing)
                {
                    Vector3 pos = new Vector3(x, y, z);
                    if (!loader.IsInBounds(pos)) continue;

                    Vector3 v = loader.SampleVelocity(pos);
                    if (v.magnitude < minSpeedToShow) continue;

                    CreateArrow(pos, v * arrowScale, mat);
                    count++;
                }
            }
        }

        _built = true;
        Debug.Log($"[VelocityFieldVisualizer] Built {count} arrows ({_pool.Count} line objects). " +
                  $"If this is too slow, increase gridSpacing.");
    }

    void CreateArrow(Vector3 origin, Vector3 vec, Material mat)
    {
        Vector3 tip   = origin + vec;
        Vector3 dir   = vec.normalized;
        Vector3 right = Vector3.Cross(dir, Vector3.up).normalized;
        if (right == Vector3.zero) right = Vector3.right;

        float headLen = vec.magnitude * arrowHeadSize;
        Vector3 head1 = tip - dir * headLen + right * headLen * 0.6f;
        Vector3 head2 = tip - dir * headLen - right * headLen * 0.6f;

        // Shaft
        MakeLine(new[] { origin, tip }, mat);
        // Arrowhead (two chevron lines)
        MakeLine(new[] { tip, head1 }, mat);
        MakeLine(new[] { tip, head2 }, mat);
    }

    void MakeLine(Vector3[] points, Material mat)
    {
        var obj = new GameObject("ArrowLine");
        obj.transform.SetParent(_container.transform, false);

        var lr = obj.AddComponent<LineRenderer>();
        lr.material          = mat;
        lr.startColor        = arrowColor;
        lr.endColor          = arrowColor;
        lr.startWidth        = lineWidth;
        lr.endWidth          = lineWidth;
        lr.positionCount     = points.Length;
        lr.useWorldSpace     = true;
        lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        lr.receiveShadows    = false;
        lr.SetPositions(points);

        _pool.Add(lr);
    }
}