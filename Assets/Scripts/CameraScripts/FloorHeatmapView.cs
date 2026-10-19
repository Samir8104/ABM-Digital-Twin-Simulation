using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

[DefaultExecutionOrder(300)]
public sealed class FloorHeatmapView : MonoBehaviour
{
    public bool Active { get; private set; }
    public int SelectedFloor { get; private set; }
    private SimulationHeatmap heatmap;
    private Camera view;
    private Vector3 savedPosition;
    private Quaternion savedRotation;
    private bool savedOrthographic, savedOcclusion;
    private float savedSize, savedNear, savedFar;
    private readonly List<Behaviour> suspendedControllers = new();
    private Bounds buildingBounds;
    private bool hasBounds;
    private Bounds[] floorBounds;
    private bool[] hasFloorBounds;

    public void Initialize(SimulationHeatmap owner) => heatmap = owner;

    private void LateUpdate()
    {
        if (SimulationFlowController.RenderingDisabled || heatmap == null || !heatmap.IsRunning || heatmap.Grid == null || SimulationHeatmap.TypingInField()) return;
#if ENABLE_INPUT_SYSTEM
        var keyboard = Keyboard.current;
        if (keyboard != null)
        {
            if (keyboard.tKey.wasPressedThisFrame) SetActive(!Active);
            if (Active && (keyboard.pageUpKey.wasPressedThisFrame || keyboard.rightBracketKey.wasPressedThisFrame)) SelectFloor(SelectedFloor + 1);
            if (Active && (keyboard.pageDownKey.wasPressedThisFrame || keyboard.leftBracketKey.wasPressedThisFrame)) SelectFloor(SelectedFloor - 1);
        }
#elif ENABLE_LEGACY_INPUT_MANAGER
        if (Input.GetKeyDown(KeyCode.T)) SetActive(!Active);
        if (Active && (Input.GetKeyDown(KeyCode.PageUp) || Input.GetKeyDown(KeyCode.RightBracket))) SelectFloor(SelectedFloor + 1);
        if (Active && (Input.GetKeyDown(KeyCode.PageDown) || Input.GetKeyDown(KeyCode.LeftBracket))) SelectFloor(SelectedFloor - 1);
#endif
        if (Active) PositionCamera();
    }

    public void SetActive(bool active)
    {
        if (active == Active) return;
        if (active)
        {
            view = Camera.main;
            if (view == null || heatmap == null || heatmap.Grid == null) return;
            var triangles = NavMesh.CalculateTriangulation();
            if (triangles.vertices.Length == 0) { Debug.LogWarning("[FloorHeatmapView] No navigation bounds available."); return; }
            buildingBounds = new Bounds(triangles.vertices[0], Vector3.zero);
            foreach (var v in triangles.vertices) buildingBounds.Encapsulate(v);
            floorBounds = new Bounds[heatmap.Grid.FloorHeights.Length];
            hasFloorBounds = new bool[floorBounds.Length];
            foreach (var v in triangles.vertices)
            {
                int floor = heatmap.Grid.FloorAt(v.y);
                float baseY = heatmap.Grid.FloorHeights[floor];
                if (v.y < baseY - 1f || v.y > SliceTop(floor)) continue;
                if (!hasFloorBounds[floor]) { floorBounds[floor] = new Bounds(v, Vector3.zero); hasFloorBounds[floor] = true; }
                else floorBounds[floor].Encapsulate(v);
            }
            hasBounds = true;
            savedPosition = view.transform.position; savedRotation = view.transform.rotation;
            savedOrthographic = view.orthographic; savedSize = view.orthographicSize;
            savedNear = view.nearClipPlane; savedFar = view.farClipPlane; savedOcclusion = view.useOcclusionCulling;
            foreach (var behavior in view.GetComponents<Behaviour>())
                if (behavior.enabled && (behavior is CameraOrbitFollow || behavior is TMPro.Examples.CameraController ||
                    behavior.GetType().FullName == "UnityEngine.Rendering.FreeCamera" || behavior.GetType().FullName == "ClickObject"))
                { suspendedControllers.Add(behavior); behavior.enabled = false; }
            Active = true;
            heatmap.SetFloorSlice(true, SelectedFloor);
            view.orthographic = true; view.useOcclusionCulling = false;
            PositionCamera();
        }
        else
        {
            Active = false;
            heatmap.SetFloorSlice(false, SelectedFloor);
            if (view != null)
            {
                view.transform.SetPositionAndRotation(savedPosition, savedRotation);
                view.orthographic = savedOrthographic; view.orthographicSize = savedSize;
                view.nearClipPlane = savedNear; view.farClipPlane = savedFar; view.useOcclusionCulling = savedOcclusion;
            }
            foreach (var behavior in suspendedControllers) if (behavior != null) behavior.enabled = true;
            suspendedControllers.Clear();
        }
    }

    public void SelectFloor(int floor)
    {
        SelectedFloor = Mathf.Clamp(floor, 0, heatmap.Grid.FloorHeights.Length - 1);
        heatmap.SetFloorSlice(Active, SelectedFloor);
        if (Active) PositionCamera();
    }

    private void PositionCamera()
    {
        if (view == null || !hasBounds) return;
        var floors = heatmap.Grid.FloorHeights;
        float baseY = floors[SelectedFloor];
        // Near/far clipping slices geometry, including meshes spanning multiple
        // floors, without disabling colliders or changing the running model.
        float top = SliceTop(SelectedFloor);
        float bottom = baseY - 1f;
        float cameraY = Mathf.Max(buildingBounds.max.y, floors[floors.Length - 1] + 4f) + 20f;
        var bounds = hasFloorBounds[SelectedFloor] ? floorBounds[SelectedFloor] : buildingBounds;
        view.transform.SetPositionAndRotation(new Vector3(bounds.center.x, cameraY, bounds.center.z), Quaternion.Euler(90, 0, 0));
        view.orthographicSize = Mathf.Max(1, Mathf.Max(bounds.extents.z, bounds.extents.x / Mathf.Max(0.1f, view.aspect))) * 1.08f;
        view.nearClipPlane = cameraY - top;
        view.farClipPlane = cameraY - bottom;
    }

    private float SliceTop(int floor)
    {
        var floors = heatmap.Grid.FloorHeights;
        return floor + 1 < floors.Length ? Mathf.Min(floors[floor] + 2.2f, floors[floor + 1] - 1f) : floors[floor] + 2.2f;
    }

    private void OnDisable() { if (Active) SetActive(false); }
}
