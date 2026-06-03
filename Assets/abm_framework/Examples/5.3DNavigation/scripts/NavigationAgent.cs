using UnityEngine;
using ABMU.Core;
using UnityEngine.AI;

/// <summary>
/// Handles agent navigation, room selection, and elevator/stair decision-making.
/// Core movement logic (Move, CheckDistToTarget, Stay) is unchanged.
/// </summary>
public class NavigationAgent : AbstractAgent
{
    NavigationController nCont;
    NavMeshAgent nmAgent;
    Vector3 target;
    public GameObject targetRoom;
    public bool isNearTarget = false;
    int timeSpentSitting = 0;
    int stationaryDuration = -1;

    static readonly (int min, int max) LowWait = (30, 200);
    static readonly (int min, int max) MidWait = (50, 600);
    static readonly (int min, int max) HighWait = (200, 1500);

    // ── Elevator state ────────────────────────────────────────────────────────

    public int TargetFloor { get; private set; } = 0;

    private bool _isRiding = false;
    private ElevatorController _elevator = null;
    private ElevatorCallStation _pendingCallStation = null;
    private int _pendingDestFloor = 0;

    // All call stations cached once at Init — includes every elevator in the scene
    private ElevatorCallStation[] _callStations;

    // ── Init ──────────────────────────────────────────────────────────────────

    public void Init(GameObject _targetRoom)
    {
        base.Init();
        nCont = GameObject.FindObjectOfType<NavigationController>();
        nmAgent = GetComponent<NavMeshAgent>();
        _callStations = GameObject.FindObjectsOfType<ElevatorCallStation>();
        SetNMAgentProperties();
        SetupStationary();
    }

    // ── Target setting ────────────────────────────────────────────────────────

    public void SetTarget(GameObject room)
    {
        targetRoom = room;
        target = nCont.GetRandomPointInRoom(targetRoom);
        nmAgent.SetDestination(target);
        nmAgent.isStopped = false;
        CreateStepper(CheckDistToTarget, 1, 100);
        CreateStepper(Move, 1, 105);
    }

    // ── Core movement (unchanged) ─────────────────────────────────────────────

    void CheckDistToTarget()
    {
        float d = Vector3.Distance(this.transform.position, target);
        if (d < nCont.distToTargetThreshold)
        {
            isNearTarget = true;
            nmAgent.isStopped = true;
            DestroyStepper("CheckDistToTarget");
            DestroyStepper("Move");

            // Arrived at a call station — attempt to register, fall back to stairs if full
            if (_pendingCallStation != null)
            {
                ElevatorCallStation station = _pendingCallStation;
                int destFloor = _pendingDestFloor;
                _pendingCallStation = null;

                bool accepted = station.TryRegisterWaitingAgent(this, destFloor);
                if (!accepted)
                    TakeStairs(targetRoom);
                return;
            }

            SetupStationary();
        }
        else
        {
            isNearTarget = false;
        }
    }

    void Move()
    {
        if (_isRiding) return;

        nmAgent.velocity = Vector3.zero;
        nmAgent.nextPosition = this.transform.position + nmAgent.desiredVelocity * 0.03f;
        transform.LookAt(nmAgent.nextPosition, Vector3.up);
        transform.position = nmAgent.nextPosition;
    }

    void SetupStationary()
    {
        stationaryDuration = GetWaitDuration();
        timeSpentSitting = 0;
        CreateStepper(Stay);
    }

    int GetWaitDuration()
    {
        if (targetRoom == null) return Random.Range(MidWait.min, MidWait.max);
        var rp = targetRoom.GetComponent<RoomPriority>();
        if (rp == null) return Random.Range(MidWait.min, MidWait.max);
        return rp.priority switch
        {
            RoomPriorityLevel.Low => Random.Range(LowWait.min, LowWait.max),
            RoomPriorityLevel.Mid => Random.Range(MidWait.min, MidWait.max),
            RoomPriorityLevel.High => Random.Range(HighWait.min, HighWait.max),
            _ => Random.Range(MidWait.min, MidWait.max)
        };
    }

    void Stay()
    {
        timeSpentSitting++;
        if (timeSpentSitting > stationaryDuration)
        {
            SetNewTarget();
            DestroyStepper("Stay");
        }
    }

    // ── Floor-aware target selection ──────────────────────────────────────────

    void SetNewTarget()
    {
        GameObject newRoom = nCont.GetRandomRoom();
        int newFloor = GetFloorOfRoom(newRoom);
        int thisFloor = GetFloorOfRoom(targetRoom);

        if (newFloor == thisFloor || newFloor < 0 || thisFloor < 0)
        {
            SetTarget(newRoom);
            return;
        }

        ElevatorCallStation station = GetBestStationForFloor(thisFloor);
        if (station != null)
            StartElevatorJourney(newRoom, newFloor, station);
        else
            TakeStairs(newRoom);
    }

    // ── Elevator journey ──────────────────────────────────────────────────────

    private void StartElevatorJourney(GameObject destinationRoom, int destFloor,
                                       ElevatorCallStation station)
    {
        targetRoom = destinationRoom;
        TargetFloor = destFloor;
        _pendingCallStation = station;
        _pendingDestFloor = destFloor;

        target = station.transform.position;
        nmAgent.SetDestination(target);
        nmAgent.isStopped = false;
        CreateStepper(CheckDistToTarget, 1, 100);
        CreateStepper(Move, 1, 105);
    }

    private void TakeStairs(GameObject room) => SetTarget(room);

    // ── Called by ElevatorCallStation / ElevatorController ───────────────────

    public void BoardElevator(ElevatorController elevator)
    {
        _elevator = elevator;
        _isRiding = true;
        nmAgent.isStopped = true;
        elevator.BoardAgent(this);
    }

    public void ExitElevator(int floorIndex)
    {
        // Get the call station position on this floor before releasing the elevator ref
        Vector3 exitPosition = _elevator.GetCallStationPosition(floorIndex);

        _elevator.ExitAgent(this);
        _elevator = null;
        _isRiding = false;

        // Warp to the call station so the agent is guaranteed on the NavMesh,
        // in open space, not behind the elevator cage
        transform.position = exitPosition;
        nmAgent.Warp(exitPosition);
        nmAgent.isStopped = false;

        SetTarget(targetRoom);
    }

    public void TeleportWithElevator(Vector3 slotPosition)
    {
        transform.position = slotPosition;
        nmAgent.nextPosition = slotPosition;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    void SetNMAgentProperties()
    {
        nmAgent.updatePosition = false;
        nmAgent.velocity = Vector3.zero;
        nmAgent.acceleration = 0f;
    }

    private int GetFloorOfRoom(GameObject room)
    {
        if (room == null) return -1;
        float roomY = room.transform.position.y;
        int best = -1;
        float bestDist = float.MaxValue;

        foreach (var s in _callStations)
        {
            float dist = Mathf.Abs(s.transform.position.y - roomY);
            if (dist < bestDist) { bestDist = dist; best = s.floorIndex; }
        }
        return best;
    }

    /// <summary>
    /// Loops all stations on this floor (one per elevator) and returns the
    /// viable one with the fewest riders + waiters. Returns null if none are viable.
    /// </summary>
    private ElevatorCallStation GetBestStationForFloor(int floor)
    {
        ElevatorCallStation best = null;
        int bestLoad = int.MaxValue;

        foreach (var s in _callStations)
        {
            if (s.floorIndex != floor) continue;
            if (!s.IsElevatorViable()) continue;

            int load = s.WaitingCount + s.elevatorController.RiderCount;
            if (load < bestLoad) { bestLoad = load; best = s; }
        }

        return best;
    }
}