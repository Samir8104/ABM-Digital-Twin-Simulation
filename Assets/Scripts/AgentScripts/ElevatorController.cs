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
    public float[] floorYPositions = new float[0];

    [Tooltip("Call stations, one per floor (assign in order: floor 0, 1, 2 …).")]
    public ElevatorCallStation[] callStations = new ElevatorCallStation[0];

    [Header("Door GameObjects")]
    [Tooltip("Left door Transform for each floor (same order as floorYPositions).")]
    public Transform[] leftDoors = new Transform[0];

    [Tooltip("Right door Transform for each floor (same order as floorYPositions).")]
    public Transform[] rightDoors = new Transform[0];

    [Tooltip("How far each door slides along its local axis when fully open.")]
    public float doorOpenOffset = 0.9f;

    [Tooltip("Seconds to fully open or close a door.")]
    public float doorAnimDuration = 1.2f;

    [Header("Elevator Movement")]
    [Tooltip("Cage travel speed in Unity units per second.")]
    public float travelSpeed = 3f;

    [Tooltip("Seconds the doors stay open while agents board/exit.")]
    public float doorDwellTime = 2f;

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

    // ── Private state ─────────────────────────────────────────────────────────

    private readonly List<NavigationAgent> _riders = new();
    private readonly List<int> _stopQueue = new();

    private int _currentFloor = 0;
    private int _direction = 0;
    private bool _busy = false;
    private bool _doorsOpen = false;
    private bool _acceptingPassengers;
    private int _servicingFloor = -1;

    private Vector3[] _leftClosedPos;
    private Vector3[] _rightClosedPos;

    // ── Public API ────────────────────────────────────────────────────────────

    public bool IsFull => _riders.Count >= maxCapacity;
    public bool IsBusy => _busy;
    public int CurrentFloor => _currentFloor;
    public int RiderCount => _riders.Count;
    public bool CanBoardAt(int floor) => _acceptingPassengers && floor == _currentFloor;
    public bool IsValidFloor(int floor) => floorYPositions != null && floor >= 0 && floor < floorYPositions.Length;

    private ElevatorCallStation StationAt(int floor)
    {
        if (callStations != null)
            foreach (var station in callStations)
                if (station != null && station.floorIndex == floor && station.elevatorController == this) return station;
        return null;
    }

    /// <summary>
    /// Returns the world-space position of this elevator's call station on
    /// the given floor — used by agents to warp to a safe exit point.
    /// </summary>
    public Vector3 GetCallStationPosition(int floor)
    {
        var station = StationAt(floor);
        if (station != null) return station.transform.position;
        return transform.position;
    }

    public void RequestFloor(int targetFloor)
    {
        if (!IsValidFloor(targetFloor)) return;
        // Calls during opening/dwell belong to this stop, not a second door cycle.
        if (_servicingFloor == targetFloor && _doorsOpen) return;
        if (!_stopQueue.Contains(targetFloor))
            _stopQueue.Add(targetFloor);

        if (!_busy && !_doorsOpen)
            StartCoroutine(RunElevator());
    }

    public void BoardAgent(NavigationAgent agent)
    {
        if (agent == null || IsFull || _riders.Contains(agent)) return;
        _riders.Add(agent);
        agent.TeleportWithElevator(GetRiderSlot(_riders.Count - 1));
        RequestFloor(agent.TargetFloor); // A destination is a stop only once its passenger boards.
    }

    public void ExitAgent(NavigationAgent agent) => _riders.Remove(agent);

    // ── Init ──────────────────────────────────────────────────────────────────

    private void Awake()
    {
        _leftClosedPos = new Vector3[leftDoors.Length];
        _rightClosedPos = new Vector3[rightDoors.Length];

        for (int i = 0; i < leftDoors.Length; i++) if (leftDoors[i] != null) _leftClosedPos[i] = leftDoors[i].localPosition;
        for (int i = 0; i < rightDoors.Length; i++) if (rightDoors[i] != null) _rightClosedPos[i] = rightDoors[i].localPosition;
        if (floorYPositions != null && floorYPositions.Length > 0)
            for (int i = 1; i < floorYPositions.Length; i++)
                if (Mathf.Abs(transform.position.y - floorYPositions[i]) < Mathf.Abs(transform.position.y - floorYPositions[_currentFloor])) _currentFloor = i;
    }

    // ── Main coroutine ────────────────────────────────────────────────────────

    private IEnumerator RunElevator()
    {
        _busy = true;

        while (_stopQueue.Count > 0)
        {
            _riders.RemoveAll(rider => rider == null);
            int nextFloor = PickNextStop();
            _stopQueue.Remove(nextFloor);

            _direction = nextFloor > _currentFloor ? 1 :
                         nextFloor < _currentFloor ? -1 : 0;

            if (_direction != 0)
                yield return StartCoroutine(MoveCageToFloor(nextFloor));

            _currentFloor = nextFloor;
            _direction = 0;

            _servicingFloor = nextFloor;
            _doorsOpen = true;
            yield return StartCoroutine(AnimateDoors(nextFloor, open: true));

            // Free seats before boarding. New arrivals may join this same dwell window.
            NotifyRidersAtFloor(nextFloor);
            _acceptingPassengers = true;
            var station = StationAt(nextFloor);
            if (station != null) station.OnElevatorArrived();

            yield return new WaitForSeconds(Mathf.Max(1f, doorDwellTime));

            _acceptingPassengers = false;
            _doorsOpen = false; // Calls during closing are serviced on a later visit.
            yield return StartCoroutine(AnimateDoors(nextFloor, open: false));
            _servicingFloor = -1;
            if (station != null && station.WaitingCount > 0) RequestFloor(nextFloor);
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
            // Deliver riders before reopening a full car for the same waiting queue.
            if (f == _currentFloor && _riders.Count > 0 && _stopQueue.Count > 1) continue;
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
            t += Time.deltaTime * Mathf.Max(0.1f, travelSpeed) / Mathf.Max(dist, 0.01f);
            transform.position = Vector3.Lerp(start, end, Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t)));

            for (int i = 0; i < _riders.Count; i++)
                if (_riders[i] != null) _riders[i].TeleportWithElevator(GetRiderSlot(i));

            yield return null;
        }

        transform.position = end;
    }

    // ── Door animation ────────────────────────────────────────────────────────

    private IEnumerator AnimateDoors(int floor, bool open)
    {
        if (floor < 0 || floor >= leftDoors.Length || floor >= rightDoors.Length ||
            leftDoors[floor] == null || rightDoors[floor] == null) yield break;

        Transform ld = leftDoors[floor];
        Transform rd = rightDoors[floor];

        // Always lerp from the current actual position so a mid-animation
        // interrupt (which shouldn't happen now) never causes a jump
        Vector3 lStart = ld.localPosition;
        Vector3 rStart = rd.localPosition;

        Vector3 lEnd = open ? _leftClosedPos[floor] + Vector3.forward * doorOpenOffset : _leftClosedPos[floor];
        Vector3 rEnd = open ? _rightClosedPos[floor] + Vector3.back * doorOpenOffset : _rightClosedPos[floor];

        // If already at the target position, skip the animation entirely
        if (Vector3.Distance(lStart, lEnd) < 0.001f && Vector3.Distance(rStart, rEnd) < 0.001f)
            yield break;

        float t = 0f;
        while (t < 1f)
        {
            t += Time.deltaTime / Mathf.Max(0.01f, doorAnimDuration);
            float smooth = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t));
            ld.localPosition = Vector3.Lerp(lStart, lEnd, smooth);
            rd.localPosition = Vector3.Lerp(rStart, rEnd, smooth);
            yield return null;
        }

        ld.localPosition = lEnd;
        rd.localPosition = rEnd;
    }

    // ── Rider slot layout ─────────────────────────────────────────────────────

    private Vector3 GetRiderSlot(int slot)
    {
        int cols = Mathf.Max(1, Mathf.CeilToInt(Mathf.Sqrt(maxCapacity)));
        int totalRows = Mathf.CeilToInt((float)maxCapacity / cols);
        int col = slot % cols;
        int row = slot / cols;

        float localX = cols > 1 ? Mathf.Lerp(-cageHalfWidth, cageHalfWidth, (float)col / (cols - 1)) : 0f;
        float localZ = totalRows > 1 ? Mathf.Lerp(-cageHalfDepth, cageHalfDepth, (float)row / (totalRows - 1)) : 0f;

        return transform.position + transform.right * localX + transform.forward * localZ + riderOffset;
    }

    // ── Exit notifications ────────────────────────────────────────────────────

    private void NotifyRidersAtFloor(int floor)
    {
        foreach (var rider in new List<NavigationAgent>(_riders))
            if (rider != null && rider.TargetFloor == floor)
                rider.ExitElevator(_currentFloor);
    }
}
