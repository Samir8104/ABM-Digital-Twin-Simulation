using System.Collections.Generic;
using UnityEngine;
using ABMU.Core;
using UnityEngine.AI;

public class NavigationAgent : AbstractAgent
{
    // ── Scene references ──────────────────────────────────────────────────────
    NavigationController nCont;
    NavMeshAgent nmAgent;
    TimeManager _time;
    Renderer[] _renderers;

    // ── Navigation state ──────────────────────────────────────────────────────
    Vector3 target;
    public GameObject targetRoom;
    public bool isNearTarget = false;
    private GameObject _classroomNode;

    // ── Exit nodes ────────────────────────────────────────────────────────────
    private List<GameObject> _exitNodes = new();

    // ── Deferred navigation ───────────────────────────────────────────────────
    // Instead of calling SetTarget (which destroys/creates steppers) directly
    // inside CheckDistToTarget (which runs inside ABMU's LateUpdate stepper loop),
    // we store the destination and act on it from ScheduleTick on the next tick.
    // This prevents modifying the stepper list while it is being iterated.
    private GameObject _pendingNavTarget = null;
    private bool _arrived = false;

    // ── Elevator state ────────────────────────────────────────────────────────
    public int TargetFloor { get; private set; } = 0;
    private bool _isRiding = false;
    private bool _isWaitingForElevator = false;
    private ElevatorController _elevator = null;
    private ElevatorCallStation _pendingCallStation = null;
    private int _pendingDestFloor = 0;
    private ElevatorCallStation[] _callStations;

    // ── Schedule ──────────────────────────────────────────────────────────────
    private AgentSchedule _schedule;
    private bool _scheduleInitialized = false;

    private const string BathroomNodeName = "Bathroom";
    private const string OfficeHoursNodeName = "OfficeHours";

    // ── Post-class thresholds ─────────────────────────────────────────────────
    private const int StayIfNextClassWithin = 90;
    private const int LeaveIfNextClassAfter = 150;

    // ── Floor cache ───────────────────────────────────────────────────────────
    private readonly Dictionary<GameObject, int> _floorCache = new();

    // ── Stepper liveness ──────────────────────────────────────────────────────
    private bool _stepperAlive_DeferredInit = false;
    private bool _stepperAlive_ScheduleTick = false;
    private bool _stepperAlive_CheckDist = false;
    private bool _stepperAlive_Move = false;
    private bool _stepperAlive_StayInPlace = false;

    // ── Setup ─────────────────────────────────────────────────────────────────

    public void SetSchedule(AgentSchedule schedule)
    {
        _schedule = schedule;
        _classroomNode = schedule.ClassroomNode;
    }

    public void Init(GameObject initialTargetRoom, List<GameObject> exitNodes)
    {
        base.Init();
        nCont = FindObjectOfType<NavigationController>();
        nmAgent = GetComponent<NavMeshAgent>();
        _time = FindObjectOfType<TimeManager>();
        _callStations = FindObjectsOfType<ElevatorCallStation>();
        _renderers = GetComponentsInChildren<Renderer>();
        _exitNodes = exitNodes ?? new List<GameObject>();

        SetNMAgentProperties();
        targetRoom = initialTargetRoom;

        SetVisible(false);
        SafeCreateStepper("DeferredInit", DeferredInit, 1, 1);
    }

    // ── Visibility ────────────────────────────────────────────────────────────

    private void SetVisible(bool visible)
    {
        if (_renderers == null) return;
        foreach (var r in _renderers) r.enabled = visible;
    }

    // ── Stepper helpers ───────────────────────────────────────────────────────

    void SafeCreateStepper(string n, ABMU.Utilities.Del method, int step, int priority)
    {
        switch (n)
        {
            case "DeferredInit": if (_stepperAlive_DeferredInit) return; _stepperAlive_DeferredInit = true; break;
            case "ScheduleTick": if (_stepperAlive_ScheduleTick) return; _stepperAlive_ScheduleTick = true; break;
            case "CheckDistToTarget": if (_stepperAlive_CheckDist) return; _stepperAlive_CheckDist = true; break;
            case "Move": if (_stepperAlive_Move) return; _stepperAlive_Move = true; break;
            case "StayInPlace": if (_stepperAlive_StayInPlace) return; _stepperAlive_StayInPlace = true; break;
        }
        CreateStepper(method, step, priority);
    }

    void SafeDestroyStepper(string n)
    {
        switch (n)
        {
            case "DeferredInit": if (!_stepperAlive_DeferredInit) return; _stepperAlive_DeferredInit = false; break;
            case "ScheduleTick": if (!_stepperAlive_ScheduleTick) return; _stepperAlive_ScheduleTick = false; break;
            case "CheckDistToTarget": if (!_stepperAlive_CheckDist) return; _stepperAlive_CheckDist = false; break;
            case "Move": if (!_stepperAlive_Move) return; _stepperAlive_Move = false; break;
            case "StayInPlace": if (!_stepperAlive_StayInPlace) return; _stepperAlive_StayInPlace = false; break;
            default: return;
        }
        DestroyStepper(n);
    }

    // ── DeferredInit ──────────────────────────────────────────────────────────

    void DeferredInit()
    {
        if (nCont == null || _time == null || _schedule == null) return;
        if (_scheduleInitialized) { SafeDestroyStepper("DeferredInit"); return; }

        _scheduleInitialized = true;
        SafeDestroyStepper("DeferredInit");

        int jitter = Random.Range(0, 30);
        SafeCreateStepper("ScheduleTick", ScheduleTick, 30 + jitter, 1);
    }

    // ── ScheduleTick ─────────────────────────────────────────────────────────
    // Runs every 30 ticks. Handles both schedule logic AND deferred navigation
    // so that stepper create/destroy never happens inside CheckDistToTarget.

    void ScheduleTick()
    {
        if (_schedule == null || _time == null || nCont == null) return;

        // ── Process any deferred arrival first ────────────────────────────────
        if (_arrived)
        {
            _arrived = false;
            ProcessArrival();
            return; // Let the schedule logic run next tick, not same tick.
        }

        // ── Deferred navigation redirect ──────────────────────────────────────
        if (_pendingNavTarget != null)
        {
            GameObject dest = _pendingNavTarget;
            _pendingNavTarget = null;
            SetTarget(dest);
            return;
        }

        if (_isRiding || _isWaitingForElevator) return;

        int simMinute = _time.CurrentHour * 60 + _time.CurrentMinute;
        bool classToday = _schedule.SectionMeetsToday(_time);

        switch (_schedule.CurrentActivity)
        {
            case AgentActivity.Idle:
                if (classToday && simMinute >= _schedule.StartMinute - 10)
                {
                    SetVisible(true);
                    _schedule.SetActivity(AgentActivity.GoingToClass);
                    NavigateTo(_schedule.ClassroomNode);
                }
                else if (!classToday && _schedule.NoMoreClassesToday)
                {
                    _schedule.SetActivity(AgentActivity.Done);
                    SafeDestroyStepper("ScheduleTick");
                    gameObject.SetActive(false);
                }
                break;

            case AgentActivity.InClass:
                if (simMinute >= _schedule.EndMinute)
                {
                    _schedule.AdvanceToNextClass();
                    _schedule.SetActivity(AgentActivity.Idle);
                    PostClassDecision(simMinute);
                }
                break;

            case AgentActivity.Done:
                SafeDestroyStepper("ScheduleTick");
                gameObject.SetActive(false);
                break;
        }
    }

    // ── Arrival processing ────────────────────────────────────────────────────
    // Called from ScheduleTick the tick AFTER CheckDistToTarget sets _arrived.
    // This is the only place that calls SetTarget or mutates steppers on arrival,
    // and it runs outside the CheckDistToTarget stepper's own execution.

    void ProcessArrival()
    {
        // ── Elevator call-station arrival ─────────────────────────────────────
        if (_pendingCallStation != null)
        {
            ElevatorCallStation station = _pendingCallStation;
            int destFloor = _pendingDestFloor;
            _pendingCallStation = null;

            bool accepted = station.TryRegisterWaitingAgent(this, destFloor);
            if (accepted)
                _isWaitingForElevator = true;
            else
                _pendingNavTarget = targetRoom; // Take stairs next tick.
            return;
        }

        int simMinute = _time.CurrentHour * 60 + _time.CurrentMinute;

        switch (_schedule.CurrentActivity)
        {
            case AgentActivity.GoingToClass:
                _schedule.SetActivity(AgentActivity.InClass);
                break;

            case AgentActivity.GoingToBathroom:
                _schedule.SetActivity(AgentActivity.InBathroom);
                StartTimedStay(Random.Range(2, 6));
                break;

            case AgentActivity.GoingToOfficeHours:
                _schedule.SetActivity(AgentActivity.InOfficeHours);
                StartTimedStay(Random.Range(10, 30));
                break;

            case AgentActivity.Chatting:
                StartTimedStay(Random.Range(2, 8));
                break;

            case AgentActivity.Leaving:
                SetVisible(false);
                if (_schedule.NoMoreClassesToday)
                {
                    _schedule.SetActivity(AgentActivity.Done);
                    SafeDestroyStepper("ScheduleTick");
                    gameObject.SetActive(false);
                }
                else
                {
                    _schedule.SetActivity(AgentActivity.Idle);
                }
                break;

            default:
                break;
        }
    }

    // ── Post-class decision ───────────────────────────────────────────────────

    void PostClassDecision(int simMinute)
    {
        if (_schedule.NoMoreClassesToday)
        {
            StartLeaving();
            return;
        }

        int gap = _schedule.MinutesUntilNextClass(simMinute);

        if (gap > LeaveIfNextClassAfter && Random.value < 0.75f)
        {
            StartLeaving();
            return;
        }

        if (gap > StayIfNextClassWithin && Random.value < 0.40f)
        {
            StartLeaving();
            return;
        }

        PickIndoorActivity(simMinute);
    }

    // ── Indoor activity picker ────────────────────────────────────────────────

    void PickIndoorActivity(int simMinute)
    {
        float roll = Random.value;
        if (roll < 0.35f)
        {
            _schedule.SetActivity(AgentActivity.GoingToBathroom);
            NavigateToNamedNode(BathroomNodeName);
        }
        else if (roll < 0.55f)
        {
            _schedule.SetActivity(AgentActivity.GoingToOfficeHours);
            NavigateToNamedNode(OfficeHoursNodeName);
        }
        else
        {
            _schedule.SetActivity(AgentActivity.Chatting);
            NavigateTo(nCont.GetRandomRoom());
        }
    }

    // ── Leaving ───────────────────────────────────────────────────────────────

    void StartLeaving()
    {
        if (_exitNodes == null || _exitNodes.Count == 0)
        {
            _schedule.SetActivity(AgentActivity.Done);
            SafeDestroyStepper("ScheduleTick");
            gameObject.SetActive(false);
            return;
        }

        _schedule.SetActivity(AgentActivity.Leaving);
        NavigateTo(_exitNodes[Random.Range(0, _exitNodes.Count)]);
    }

    // ── CheckDistToTarget ─────────────────────────────────────────────────────
    // IMPORTANT: This method must NOT call SetTarget, SafeCreateStepper, or
    // SafeDestroyStepper. Doing so modifies ABMU's stepper list while LateUpdate
    // is iterating it, causing the 98ms spike seen in the profiler.
    // Instead, set _arrived = true and let ScheduleTick handle it next tick.

    void CheckDistToTarget()
    {
        if (_isRiding) return;

        float d = Vector3.Distance(transform.position, target);
        if (d >= nCont.distToTargetThreshold)
        {
            isNearTarget = false;
            return;
        }

        // Arrived — stop moving and flag for deferred processing.
        isNearTarget = true;
        nmAgent.isStopped = true;
        _arrived = true;
        // Do NOT touch steppers here. ScheduleTick will call ProcessArrival
        // on the next tick outside this stepper's own execution context.
    }

    // ── Timed stationary stay ─────────────────────────────────────────────────

    int _stayMinutesRemaining = 0;

    void StartTimedStay(int simMinutes)
    {
        _stayMinutesRemaining = simMinutes;
        SafeCreateStepper("StayInPlace", StayInPlace, 60, 2);
    }

    void StayInPlace()
    {
        _stayMinutesRemaining--;
        if (_stayMinutesRemaining > 0) return;

        SafeDestroyStepper("StayInPlace");
        int simMinute = _time.CurrentHour * 60 + _time.CurrentMinute;
        PostClassDecision(simMinute);
    }

    // ── Navigation ────────────────────────────────────────────────────────────

    void NavigateTo(GameObject room)
    {
        if (room == null) { Debug.LogWarning($"[{name}] NavigateTo: null room"); return; }

        int newFloor = GetFloorOfRoom(room);
        int thisFloor = GetFloorOfRoom(targetRoom);

        if (newFloor != thisFloor && newFloor >= 0 && thisFloor >= 0)
        {
            ElevatorCallStation station = GetBestStationForFloor(thisFloor);
            if (station != null) { StartElevatorJourney(room, newFloor, station); return; }
        }

        SetTarget(room);
    }

    void NavigateToNamedNode(string nodeName)
    {
        GameObject node = GameObject.Find(nodeName);
        NavigateTo(node != null ? node : nCont.GetRandomRoom());
    }

    public void SetTarget(GameObject room)
    {
        targetRoom = room;
        target = nCont.GetRandomPointInRoom(room);
        _arrived = false;

        SafeDestroyStepper("CheckDistToTarget");
        SafeDestroyStepper("Move");

        nmAgent.SetDestination(target);
        nmAgent.isStopped = false;

        SafeCreateStepper("CheckDistToTarget", CheckDistToTarget, 2, 100);
        SafeCreateStepper("Move", Move, 2, 105);
    }

    // ── Movement ──────────────────────────────────────────────────────────────

    void Move()
    {
        if (_isRiding) return;
        nmAgent.velocity = Vector3.zero;
        nmAgent.nextPosition = transform.position + nmAgent.desiredVelocity * 0.03f;
        transform.LookAt(nmAgent.nextPosition, Vector3.up);
        transform.position = nmAgent.nextPosition;
    }

    // ── Elevator ──────────────────────────────────────────────────────────────

    private void StartElevatorJourney(GameObject dest, int destFloor, ElevatorCallStation station)
    {
        targetRoom = dest;
        TargetFloor = destFloor;
        _pendingCallStation = station;
        _pendingDestFloor = destFloor;
        target = station.transform.position;
        _arrived = false;

        nmAgent.SetDestination(target);
        nmAgent.isStopped = false;

        SafeDestroyStepper("CheckDistToTarget");
        SafeDestroyStepper("Move");
        SafeCreateStepper("CheckDistToTarget", CheckDistToTarget, 2, 100);
        SafeCreateStepper("Move", Move, 2, 105);
    }

    private void TakeStairs(GameObject room) => SetTarget(room);

    public void BoardElevator(ElevatorController elevator)
    {
        _isWaitingForElevator = false;
        _elevator = elevator;
        _isRiding = true;
        nmAgent.isStopped = true;
        elevator.BoardAgent(this);
    }

    public void ExitElevator(int floorIndex)
    {
        Vector3 exit = _elevator.GetCallStationPosition(floorIndex);
        _elevator.ExitAgent(this);
        _elevator = null;
        _isRiding = false;
        transform.position = exit;
        nmAgent.Warp(exit);
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
        if (_floorCache.TryGetValue(room, out int cached)) return cached;

        float roomY = room.transform.position.y;
        int best = -1;
        float bestDist = float.MaxValue;
        foreach (var s in _callStations)
        {
            float d = Mathf.Abs(s.transform.position.y - roomY);
            if (d < bestDist) { bestDist = d; best = s.floorIndex; }
        }

        _floorCache[room] = best;
        return best;
    }

    private ElevatorCallStation GetBestStationForFloor(int floor)
    {
        ElevatorCallStation best = null;
        int bestLoad = int.MaxValue;
        foreach (var s in _callStations)
        {
            if (s.floorIndex != floor || !s.IsElevatorViable()) continue;
            int load = s.WaitingCount + s.elevatorController.RiderCount;
            if (load < bestLoad) { bestLoad = load; best = s; }
        }
        return best;
    }
}