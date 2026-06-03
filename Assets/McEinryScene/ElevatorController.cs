using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Manages the elevator cage: floor queueing, cage movement, door lerp animation,
/// and agent boarding/unloading. Attach to the ElevatorCage GameObject.
/// </summary>
public class ElevatorController : MonoBehaviour
{
    // ── Inspector ─────────────────────────────────────────────────────────────

    [Header("Floor Setup")]
    [Tooltip("World-space Y positions for each floor, index 0 = ground floor.")]
    public float[] floorYPositions;

    [Tooltip("Call stations, one per floor (assign in order: floor 0, 1, 2 …).")]
    public ElevatorCallStation[] callStations;

    [Header("Door GameObjects")]
    [Tooltip("Left door Transform for each floor (same order as floorYPositions).")]
    public Transform[] leftDoors;

    [Tooltip("Right door Transform for each floor (same order as floorYPositions).")]
    public Transform[] rightDoors;

    [Tooltip("How far each door slides along its local axis when fully open.")]
    public float doorOpenOffset = 0.9f;

    [Tooltip("Seconds to fully open or close a door.")]
    public float doorAnimDuration = 1.2f;

    [Header("Elevator Movement")]
    [Tooltip("Cage travel speed in Unity units per second.")]
    public float travelSpeed = 3f;

    [Tooltip("Seconds the doors stay open while agents board/exit.")]
    public float doorDwellTime = 2.5f;

    [Header("Capacity")]
    [Tooltip("Maximum agents allowed inside the cage at once.")]
    public int maxCapacity = 6;

    [Header("Rider Slot Layout")]
    [Tooltip("World-space offset applied to every rider slot. Use Z to push agents back into the elevator.")]
    public Vector3 riderOffset = new Vector3(0f, 0f, 0.5f);

    [Tooltip("Half-width of the cage interior along the cage's LOCAL X axis (side to side).")]
    public float cageHalfWidth = 0.4f;

    [Tooltip("Half-depth of the cage interior along the cage's LOCAL Z axis (front to back, away from doors).")]
    public float cageHalfDepth = 0.5f;

    public int RiderCount => _riders.Count;
    // ── Private state ─────────────────────────────────────────────────────────

    private readonly List<NavigationAgent> _riders = new();
    private readonly List<int> _stopQueue = new();

    private int _currentFloor = 0;
    private int _direction = 0;
    private bool _busy = false;
    private bool _doorsOpen = false;

    private Vector3[] _leftClosedPos;
    private Vector3[] _rightClosedPos;

   

    // ── Public API ────────────────────────────────────────────────────────────

    public bool IsFull => _riders.Count >= maxCapacity;
    public bool IsBusy => _busy;
    public int CurrentFloor => _currentFloor;

    public void RequestFloor(int targetFloor)
    {
        Debug.Log($"[{gameObject.name}] RequestFloor({targetFloor}) | busy={_busy} | doorsOpen={_doorsOpen} | queue={_stopQueue.Count}");

        if (!_stopQueue.Contains(targetFloor))
            _stopQueue.Add(targetFloor);

        if (!_busy && !_doorsOpen)
            StartCoroutine(RunElevator());
    }

    /// <summary>Registers a boarding agent and immediately places them at their slot.</summary>
    public void BoardAgent(NavigationAgent agent)
    {
        _riders.Add(agent);
        // Place the agent at their assigned slot straight away
        agent.TeleportWithElevator(GetRiderSlot(_riders.Count - 1));
    }

    public void ExitAgent(NavigationAgent agent) => _riders.Remove(agent);

    // ── Init ──────────────────────────────────────────────────────────────────

    private void Start()
    {
        _leftClosedPos = new Vector3[leftDoors.Length];
        _rightClosedPos = new Vector3[rightDoors.Length];

        for (int i = 0; i < leftDoors.Length; i++) _leftClosedPos[i] = leftDoors[i].localPosition;
        for (int i = 0; i < rightDoors.Length; i++) _rightClosedPos[i] = rightDoors[i].localPosition;
    }

    // ── Main coroutine ────────────────────────────────────────────────────────

    private IEnumerator RunElevator()
    {
        _busy = true;

        while (_stopQueue.Count > 0)
        {
            int nextFloor = PickNextStop();
            _stopQueue.Remove(nextFloor);

            _direction = nextFloor > _currentFloor ? 1 :
                         nextFloor < _currentFloor ? -1 : 0;

            if (_direction != 0)
                yield return StartCoroutine(MoveCageToFloor(nextFloor));

            _currentFloor = nextFloor;
            _direction = 0;

            _doorsOpen = true;
            yield return StartCoroutine(AnimateDoors(nextFloor, open: true));

            if (nextFloor < callStations.Length && callStations[nextFloor] != null)
                callStations[nextFloor].OnElevatorArrived();

            NotifyRidersAtFloor(nextFloor);

            yield return new WaitForSeconds(doorDwellTime);

            yield return StartCoroutine(AnimateDoors(nextFloor, open: false));
            _doorsOpen = false;
        }

        _busy = false;
        _direction = 0;
    }

    // ── SCAN floor selection ──────────────────────────────────────────────────

    private int PickNextStop()
    {
        if (_stopQueue.Count == 0) return _currentFloor;

        List<int> sameDir = new();
        foreach (int f in _stopQueue)
        {
            if (_direction == 0) sameDir.Add(f);
            else if (_direction > 0 && f > _currentFloor) sameDir.Add(f);
            else if (_direction < 0 && f < _currentFloor) sameDir.Add(f);
        }

        List<int> candidates = sameDir.Count > 0 ? sameDir : _stopQueue;
        int best = candidates[0];
        foreach (int f in candidates)
            if (Mathf.Abs(f - _currentFloor) < Mathf.Abs(best - _currentFloor))
                best = f;

        return best;
    }

    // ── Cage movement ─────────────────────────────────────────────────────────

    private IEnumerator MoveCageToFloor(int floor)
    {
        Vector3 start = transform.position;
        Vector3 end = new Vector3(start.x, floorYPositions[floor], start.z);
        float dist = Mathf.Abs(end.y - start.y);
        float t = 0f;

        while (t < 1f)
        {
            t += Time.deltaTime * travelSpeed / Mathf.Max(dist, 0.01f);
            transform.position = Vector3.Lerp(start, end, Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t)));

            // Keep each rider at their own spread slot as the cage moves
            for (int i = 0; i < _riders.Count; i++)
                _riders[i].TeleportWithElevator(GetRiderSlot(i));

            yield return null;
        }

        transform.position = end;
    }

    // ── Door animation ────────────────────────────────────────────────────────

    private IEnumerator AnimateDoors(int floor, bool open)
    {
        if (floor >= leftDoors.Length) yield break;

        Transform ld = leftDoors[floor];
        Transform rd = rightDoors[floor];

        Vector3 lStart = ld.localPosition;
        Vector3 rStart = rd.localPosition;

        // Axis might change — Z used here; swap to X if your doors slide side-to-side
        Vector3 lEnd = open ? _leftClosedPos[floor] + Vector3.forward * doorOpenOffset : _leftClosedPos[floor];
        Vector3 rEnd = open ? _rightClosedPos[floor] + Vector3.back * doorOpenOffset : _rightClosedPos[floor];

        float t = 0f;
        while (t < 1f)
        {
            t += Time.deltaTime / doorAnimDuration;
            float smooth = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t));
            ld.localPosition = Vector3.Lerp(lStart, lEnd, smooth);
            rd.localPosition = Vector3.Lerp(rStart, rEnd, smooth);
            yield return null;
        }

        ld.localPosition = lEnd;
        rd.localPosition = rEnd;
    }

    // ── Rider slot layout ─────────────────────────────────────────────────────

    /// <summary>
    /// Returns a world-space position for the given rider slot, spread in a grid
    /// and shifted by the flat <see cref="riderOffset"/>.
    /// </summary>
    private Vector3 GetRiderSlot(int slot)
    {
        int cols = Mathf.Max(1, Mathf.CeilToInt(Mathf.Sqrt(maxCapacity)));
        int totalRows = Mathf.CeilToInt((float)maxCapacity / cols);
        int col = slot % cols;
        int row = slot / cols;

        float localX = cols > 1 ? Mathf.Lerp(-cageHalfWidth, cageHalfWidth, (float)col / (cols - 1)) : 0f;
        float localZ = totalRows > 1 ? Mathf.Lerp(-cageHalfDepth, cageHalfDepth, (float)row / (totalRows - 1)) : 0f;

        // Spread across the grid then apply the flat offset to push agents into the elevator
        return transform.position + transform.right * localX + transform.forward * localZ + riderOffset;
    }

    // ── Exit notifications ────────────────────────────────────────────────────

    private void NotifyRidersAtFloor(int floor)
    {
        foreach (var rider in new List<NavigationAgent>(_riders))
            if (rider.TargetFloor == floor)
                rider.ExitElevator(_currentFloor);
    }


}