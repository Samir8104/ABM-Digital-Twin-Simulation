using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

// Editor-only alignment guide. Uses the actual data bounds and prefab transform.
[CustomEditor(typeof(VelocityFieldLoader))]
public class AirflowBoundsEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();
        var loader = (VelocityFieldLoader)target;
        if (loader.recordedField == null) return;
        EditorGUILayout.Space();
        EditorGUILayout.HelpBox("Scene bounds: cyan = floor, yellow = ceiling. Enable Scene Gizmos. Move, rotate or scale this object to align the volume with the first floor.", MessageType.Info);
        if (GUILayout.Button("Frame Airflow Bounds in Scene View"))
        {
            loader.showAlignmentBounds = true;
            EditorUtility.SetDirty(loader);
            var view = SceneView.lastActiveSceneView;
            if (view == null) view = EditorWindow.GetWindow<SceneView>();
            view.Frame(WorldBounds(loader), false);
            SceneView.RepaintAll();
        }
    }

    static Bounds WorldBounds(VelocityFieldLoader loader)
    {
        var b = loader.recordedField.bounds;
        var result = new Bounds(loader.transform.TransformPoint(b.center), Vector3.zero);
        for (int i = 0; i < 8; i++) result.Encapsulate(loader.transform.TransformPoint(Corner(b, i)));
        return result;
    }
    static Vector3 Corner(Bounds b, int i) => new Vector3(
        (i & 1) == 0 ? b.min.x : b.max.x,
        (i & 4) == 0 ? b.min.y : b.max.y,
        (i & 2) == 0 ? b.min.z : b.max.z);

    [DrawGizmo(GizmoType.Selected | GizmoType.NonSelected)]
    static void DrawBounds(VelocityFieldLoader loader, GizmoType type)
    {
        if (!loader.showAlignmentBounds || loader.recordedField == null || Camera.current == null || Camera.current.cameraType != CameraType.SceneView) return;
        var bounds = loader.recordedField.bounds;
        var p = new Vector3[8];
        for (int i = 0; i < p.Length; i++) p[i] = loader.transform.TransformPoint(Corner(bounds, i));
        Color oldColor = Handles.color;
        CompareFunction oldDepth = Handles.zTest;
        try
        {
            // Keep the outline visible through the building while aligning it.
            Handles.zTest = CompareFunction.Always;
            Handles.color = new Color(0f, 1f, 1f, 0.055f);
            Handles.DrawAAConvexPolygon(p[0], p[1], p[3], p[2]);
            Handles.color = Color.cyan;
            Handles.DrawAAPolyLine(4f, p[0], p[1], p[3], p[2], p[0]);
            Handles.color = Color.yellow;
            Handles.DrawAAPolyLine(4f, p[4], p[5], p[7], p[6], p[4]);
            Handles.color = new Color(1f, 1f, 1f, 0.85f);
            for (int i = 0; i < 4; i++) Handles.DrawAAPolyLine(3f, p[i], p[i + 4]);
            for (int i = 0; i < 8; i++)
            {
                Handles.color = i < 4 ? Color.cyan : Color.yellow;
                Handles.DotHandleCap(0, p[i], Quaternion.identity, HandleUtility.GetHandleSize(p[i]) * 0.055f, EventType.Repaint);
            }
            var label = new GUIStyle(EditorStyles.helpBox) { fontSize = 13, alignment = TextAnchor.MiddleCenter };
            label.normal.textColor = Color.white;
            Vector3 size = new Vector3(Vector3.Distance(p[0], p[1]), Vector3.Distance(p[0], p[4]), Vector3.Distance(p[0], p[2]));
            Handles.Label((p[0] + p[3]) * 0.5f, "AIRFLOW FLOOR\n" + size.x.ToString("F1") + " × " + size.z.ToString("F1") + " m", label);
            Handles.Label((p[4] + p[7]) * 0.5f, "AIRFLOW CEILING\nHeight " + size.y.ToString("F2") + " m", label);
        }
        finally { Handles.color = oldColor; Handles.zTest = oldDepth; }
    }
}
