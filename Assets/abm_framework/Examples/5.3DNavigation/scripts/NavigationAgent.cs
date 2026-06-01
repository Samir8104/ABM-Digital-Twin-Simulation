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

    // The floor this agent needs to reach when riding the elevator
    public int TargetFloor { get; private set; } = 0;

    // True while this agent is inside the cage
    private bool _isRiding = false;

    // Active elevator reference while riding
    private ElevatorController _elevator = null;

    // The call station the agent is currently walking toward (cleared on arrival)
    private ElevatorCallStation _pendingCallStation = null;

    // The final destination floor stored so we can pass it to the station on arrival
    private int _pendingDestFloor = 0;

    // All call stations cached once at Init
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

                // TryRegister is the second gate: queue may have filled during the walk
                bool accepted = station.TryRegisterWaitingAgent(this, destFloor);
                if (!accepted)
                {
                    // Queue filled by the time we arrived — take stairs to the stored room
                    TakeStairs(targetRoom);
                }
                // If accepted, agent stands idle here waiting for BoardElevator() callback
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
        // Elevator moves the agent via TeleportWithElevator while riding
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

        // Same floor — walk directly, no elevator needed
        if (newFloor == thisFloor || newFloor < 0 || thisFloor < 0)
        {
            SetTarget(newRoom);
            return;
        }

        // Different floor — first gate: check before even starting the walk
        ElevatorCallStation station = GetCallStationForFloor(thisFloor);
        if (station != null && station.IsElevatorViable())
            StartElevatorJourney(newRoom, newFloor, station);
        else
            TakeStairs(newRoom);
    }

    // ── Elevator journey ──────────────────────────────────────────────────────

    /// <summary>
    /// Walks the agent to the call station. CheckDistToTarget will attempt
    /// registration on arrival via the second gate.
    /// </summary>
    private void StartElevatorJourney(GameObject destinationRoom, int destFloor,
                                       ElevatorCallStation station)
    {
        targetRoom = destinationRoom; // remember final destination
        TargetFloor = destFloor;
        _pendingCallStation = station;
        _pendingDestFloor = destFloor;

        target = station.transform.position;
        nmAgent.SetDestination(target);
        nmAgent.isStopped = false;
        CreateStepper(CheckDistToTarget, 1, 100);
        CreateStepper(Move, 1, 105);
    }

    /// <summary>
    /// Navigates directly via stairs/ramp NavMesh links.
    /// </summary>
    private void TakeStairs(GameObject room) => SetTarget(room);

    // ── Called by ElevatorCallStation / ElevatorController ───────────────────

    /// <summary>
    /// Called when the elevator arrives and this agent may enter the cage.
    /// </summary>
    public void BoardElevator(ElevatorController elevator)
    {
        _elevator = elevator;
        _isRiding = true;
        nmAgent.isStopped = true;
        elevator.BoardAgent(this);
    }

    /// <summary>
    /// Called by ElevatorController when the cage reaches TargetFloor.
    /// Agent exits and navigates to the final room.
    /// </summary>
    public void ExitElevator(int floorIndex)
    {
        _elevator.ExitAgent(this);
        _elevator = null;
        _isRiding = false;

        // Warp so NavMesh pathfinding resumes from the correct position
        nmAgent.Warp(transform.position);
        nmAgent.isStopped = false;

        SetTarget(targetRoom);
    }

    /// <summary>
    /// Called by ElevatorController every frame while the cage is moving,
    /// and once on board to place the agent at their assigned grid slot.
    /// </summary>
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

    /// <summary>
    /// Returns the floor index of a room by proximity to the nearest call station.
    /// </summary>
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
    /// Finds the ElevatorCallStation assigned to a specific floor index.
    /// </summary>
    private ElevatorCallStation GetCallStationForFloor(int floor)
    {
        foreach (var s in _callStations)
            if (s.floorIndex == floor) return s;
        return null;
    }
}