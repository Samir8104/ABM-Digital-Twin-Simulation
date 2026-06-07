using UnityEngine;
using ABMU.Core;
using UnityEngine.AI;

/// <summary>
/// Handles agent navigation, room selection, and schedule-driven behaviour.
///
/// Changes from the original:
///   • Accepts an <see cref="AgentSchedule"/> injected by <see cref="ScheduleManager"/>.
///   • Every tick the schedule is consulted; when it issues a command the agent
///     transitions to the appropriate room or behaviour.
///   • Bathroom → node named "Bathroom" (or "officeHours" for the original naming).
///   • Office hours → node tagged/named "OfficeHours".
///   • Chatting / Wandering → random room on the same floor.
///   • Core movement helpers (Move, CheckDistToTarget) are unchanged.
/// </summary>
public class NavigationAgent : AbstractAgent
{
    // ── Scene references ──────────────────────────────────────────────────────

    NavigationController nCont;
    NavMeshAgent nmAgent;
    TimeManager _time;

    // ── Navigation state ──────────────────────────────────────────────────────

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
    private ElevatorCallStation[] _callStations;

    // ── Schedule ──────────────────────────────────────────────────────────────

    private AgentSchedule _schedule;

    /// <summary>
    /// How often (in ABMU steps) to poll the schedule. At 1 step/frame this is
    /// roughly once per second at 60 fps. Increase for cheaper polling.
    /// </summary>
    private const int SchedulePollInterval = 60;

    // Node-name constants — change here if your scene uses different names.
    private const string BathroomNodeName = "Bathroom";      // or "officeHours"
    private const string OfficeHoursNodeName = "OfficeHours";

    // ── Init ──────────────────────────────────────────────────────────────────

    /// <summary>Called by ScheduleManager before Init() to inject the schedule.</summary>
    public void SetSchedule(AgentSchedule schedule) => _schedule = schedule;

    public void Init(GameObject _targetRoom)
    {
        base.Init();
        nCont = FindObjectOfType<NavigationController>();
        nmAgent = GetComponent<NavMeshAgent>();
        _time = FindObjectOfType<TimeManager>();
        _callStations = FindObjectsOfType<ElevatorCallStation>();

        SetNMAgentProperties();

        // Start idle — the schedule stepper will issue the first move command.
        targetRoom = _targetRoom;
        CreateStepper(TickSchedule, SchedulePollInterval, 1);   // low priority, infrequent
    }

    // ── Schedule stepper ──────────────────────────────────────────────────────

    /// <summary>Polls the schedule and reacts to any issued command.</summary>
    void TickSchedule()
    {
        if (_schedule == null || _time == null) return;

        ScheduleCommand cmd = _schedule.Tick(_time);
        if (cmd.Changed)
            ApplyCommand(cmd);
    }

    /// <summary>Applies a ScheduleCommand: start moving, stay, or leave.</summary>
    private void ApplyCommand(ScheduleCommand cmd)
    {
        if (cmd.ActivityHint == AgentActivity.Done)
        {
            // Agent is done for the day — stop all activity and disable.
            StopMoving();
            DestroyStepper("TickSchedule");
            gameObject.SetActive(false);
            return;
        }

        if (cmd.Target != null)
        {
            // A specific destination was given (e.g. classroom).
            NavigateTo(cmd.Target);
            return;
        }

        // No explicit target: resolve based on activity hint.
        switch (cmd.ActivityHint)
        {
            case AgentActivity.GoingToBathroom:
                NavigateToNamedNode(BathroomNodeName);
                break;

            case AgentActivity.GoingToOfficeHours:
                NavigateToNamedNode(OfficeHoursNodeName);
                break;

            case AgentActivity.Chatting:
            case AgentActivity.Wandering:
                // Pick a random room on the same floor and wander there.
                NavigateTo(nCont.GetRandomRoom());
                break;

            case AgentActivity.InClass:
            case AgentActivity.InBathroom:
            case AgentActivity.InOfficeHours:
                // Arrived/in-place: the schedule drives the stay timer via NotifyActivityTimer.
                SetupStationary();
                break;
        }
    }

    // ── Named-node lookup ─────────────────────────────────────────────────────

    private void NavigateToNamedNode(string nodeName)
    {
        GameObject node = GameObject.Find(nodeName);
        if (node != null)
            NavigateTo(node);
        else
        {
            Debug.LogWarning($"[NavigationAgent] Node '{nodeName}' not found — wandering instead.");
            NavigateTo(nCont.GetRandomRoom());
        }
    }

    // ── Target setting ────────────────────────────────────────────────────────

    /// <summary>Begins navigating to a room GameObject.</summary>
    public void NavigateTo(GameObject room)
    {
        targetRoom = room;
        SetTarget(room);
    }

    public void SetTarget(GameObject room)
    {
        targetRoom = room;
        target = nCont.GetRandomPointInRoom(targetRoom);

        nmAgent.SetDestination(target);
        nmAgent.isStopped = false;

        CreateStepper(CheckDistToTarget, 1, 100);
        CreateStepper(Move, 1, 105);
    }

    // ── Arrival ───────────────────────────────────────────────────────────────

    void CheckDistToTarget()
    {
        float d = Vector3.Distance(transform.position, target);
        if (d < nCont.distToTargetThreshold)
        {
            isNearTarget = true;
            nmAgent.isStopped = true;
            DestroyStepper("CheckDistToTarget");
            DestroyStepper("Move");

            // Elevator-specific arrival handling (unchanged)
            if (_pendingCallStation != null)
            {
                ElevatorCallStation station = _pendingCallStation;
                int destFloor = _pendingDestFloor;
                _pendingCallStation = null;

                bool accepted = station.TryRegisterWaitingAgent(this, destFloor);
                if (!accepted) TakeStairs(targetRoom);
                return;
            }

            // Notify schedule that we arrived, then apply the returned command.
            if (_schedule != null)
            {
                ScheduleCommand arrivalCmd = _schedule.NotifyArrived(_time);
                if (arrivalCmd.Changed)
                {
                    ApplyCommand(arrivalCmd);
                    return;
                }
            }

            // Default: set up a stationary wait (legacy behaviour).
            SetupStationary();
        }
        else
        {
            isNearTarget = false;
        }
    }

    // ── Core movement (unchanged) ─────────────────────────────────────────────

    void Move()
    {
        if (_isRiding) return;

        nmAgent.velocity = Vector3.zero;
        nmAgent.nextPosition = transform.position + nmAgent.desiredVelocity * 0.03f;
        transform.LookAt(nmAgent.nextPosition, Vector3.up);
        transform.position = nmAgent.nextPosition;
    }

    void StopMoving()
    {
        nmAgent.isStopped = true;
        DestroyStepper("CheckDistToTarget");
        DestroyStepper("Move");
        DestroyStepper("Stay");
    }

    // ── Stationary wait ───────────────────────────────────────────────────────

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
            DestroyStepper("Stay");

            // Let the schedule decide what to do after the wait.
            if (_schedule != null)
            {
                ScheduleCommand cmd = _schedule.NotifyActivityTimer(_time, nCont);
                if (cmd.Changed) { ApplyCommand(cmd); return; }
            }

            // Fallback: legacy random room navigation.
            SetNewTarget();
        }
    }

    // ── Legacy floor-aware target selection (unchanged, kept as fallback) ──────

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
        if (station != null) StartElevatorJourney(newRoom, newFloor, station);
        else TakeStairs(newRoom);
    }

    // ── Elevator journey (unchanged) ──────────────────────────────────────────

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

    public void BoardElevator(ElevatorController elevator)
    {
        _elevator = elevator;
        _isRiding = true;
        nmAgent.isStopped = true;
        elevator.BoardAgent(this);
    }

    public void ExitElevator(int floorIndex)
    {
        Vector3 exitPosition = _elevator.GetCallStationPosition(floorIndex);
        _elevator.ExitAgent(this);
        _elevator = null;
        _isRiding = false;

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