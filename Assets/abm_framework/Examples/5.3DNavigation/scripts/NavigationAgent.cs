using UnityEngine;
using ABMU.Core;
using UnityEngine.AI;

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
    private GameObject _classroomNode;

    // ── Elevator state ────────────────────────────────────────────────────────
    public int TargetFloor { get; private set; } = 0;
    private bool _isRiding = false;
    private ElevatorController _elevator = null;
    private ElevatorCallStation _pendingCallStation = null;
    private int _pendingDestFloor = 0;
    private ElevatorCallStation[] _callStations;

    // ── Schedule ──────────────────────────────────────────────────────────────
    private AgentSchedule _schedule;
    private bool _scheduleInitialized = false;

    private const string BathroomNodeName = "Bathroom";
    private const string OfficeHoursNodeName = "OfficeHours";

    // ── Stepper liveness tracking ─────────────────────────────────────────────
    // Rule: NEVER call DestroyStepper(name) unless the matching flag is true.
    // Always set the flag to false immediately after destroying.
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

    public void Init(GameObject _targetRoom)
    {
        base.Init();
        nCont = FindObjectOfType<NavigationController>();
        nmAgent = GetComponent<NavMeshAgent>();
        _time = FindObjectOfType<TimeManager>();
        _callStations = FindObjectsOfType<ElevatorCallStation>();

        SetNMAgentProperties();
        targetRoom = _targetRoom;

        SafeCreateStepper("DeferredInit", DeferredInit, 1, 1);
    }

    // ── Stepper lifecycle helpers ─────────────────────────────────────────────

    /// <summary>Creates a named stepper only if it isn't already alive.</summary>
    void SafeCreateStepper(string stepperName, ABMU.Utilities.Del method, int step, int priority)
    {
        switch (stepperName)
        {
            case "DeferredInit":
                if (_stepperAlive_DeferredInit) return;
                _stepperAlive_DeferredInit = true;
                break;
            case "ScheduleTick":
                if (_stepperAlive_ScheduleTick) return;
                _stepperAlive_ScheduleTick = true;
                break;
            case "CheckDistToTarget":
                if (_stepperAlive_CheckDist) return;
                _stepperAlive_CheckDist = true;
                break;
            case "Move":
                if (_stepperAlive_Move) return;
                _stepperAlive_Move = true;
                break;
            case "StayInPlace":
                if (_stepperAlive_StayInPlace) return;
                _stepperAlive_StayInPlace = true;
                break;
        }
        CreateStepper(method, step, priority);
    }

    /// <summary>Destroys a named stepper only if it is currently alive.</summary>
    void SafeDestroyStepper(string stepperName)
    {
        switch (stepperName)
        {
            case "DeferredInit":
                if (!_stepperAlive_DeferredInit) return;
                _stepperAlive_DeferredInit = false;
                break;
            case "ScheduleTick":
                if (!_stepperAlive_ScheduleTick) return;
                _stepperAlive_ScheduleTick = false;
                break;
            case "CheckDistToTarget":
                if (!_stepperAlive_CheckDist) return;
                _stepperAlive_CheckDist = false;
                break;
            case "Move":
                if (!_stepperAlive_Move) return;
                _stepperAlive_Move = false;
                break;
            case "StayInPlace":
                if (!_stepperAlive_StayInPlace) return;
                _stepperAlive_StayInPlace = false;
                break;
            default:
                return;
        }
        DestroyStepper(stepperName);
    }

    // ── DeferredInit ──────────────────────────────────────────────────────────

    void DeferredInit()
    {

        if (nCont == null || _time == null || _schedule == null) return;
        if (_scheduleInitialized)
        {
            SafeDestroyStepper("DeferredInit");
            return;
        }

        _scheduleInitialized = true;
        SafeDestroyStepper("DeferredInit");

        SafeCreateStepper("ScheduleTick", ScheduleTick, 30, 1);
    }

    // ── ScheduleTick ──────────────────────────────────────────────────────────

    void ScheduleTick()
    {
        if (_schedule == null || _time == null || nCont == null)
        {
            return;
        }

        int simMinute = _time.CurrentHour * 60 + _time.CurrentMinute;
        bool classToday = _schedule.SectionMeetsToday(_time);

       
        // if the silly agent is on an elevator
        if (_isRiding) return;

        switch (_schedule.CurrentActivity)
        {
            case AgentActivity.Idle:
                if (classToday && simMinute >= _schedule.StartMinute - 10)
                {
                    _schedule.SetActivity(AgentActivity.GoingToClass);
                    NavigateTo(_classroomNode);
                }
                else if (!classToday)
                {
                    _schedule.SetActivity(AgentActivity.Wandering);
                    NavigateTo(nCont.GetRandomRoom());
                }
                else
                {
                }
                break;

            case AgentActivity.InClass:
                if (simMinute >= _schedule.EndMinute)
                {
                    Debug.Log($"[{name}] → Class over at {simMinute}, leaving");

                    _schedule.SetActivity(AgentActivity.Wandering);
                    PickAndDoRandomActivity(simMinute);
                }
                break;

            case AgentActivity.Done:
                SafeDestroyStepper("ScheduleTick");
                gameObject.SetActive(false);
                break;
        }
    }

    // ── Random activity picker ────────────────────────────────────────────────

    void PickAndDoRandomActivity(int simMinute)
    {
        if (_schedule.AttendedToday && simMinute > _schedule.EndMinute + 60)
        {
            if (Random.value < 0.30f)
            {
                _schedule.SetActivity(AgentActivity.Done);
                return;
            }
        }

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
        else if (roll < 0.85f)
        {
            _schedule.SetActivity(AgentActivity.Chatting); // TODO: Make it so the agent heads to a node called 'chattingarea' instead of a random classroom
            NavigateTo(nCont.GetRandomRoom());
        }
        else
        {
            _schedule.SetActivity(AgentActivity.Wandering); // Wandering and chatting are the same thing, I feel like it would be better if the agent just left the building atp. 
            // TODO: Make the agent leave the building instead of wander.
            NavigateTo(nCont.GetRandomRoom());
        }
    }

    // ── Arrival handling ──────────────────────────────────────────────────────

    void CheckDistToTarget()
    {
        float d = Vector3.Distance(transform.position, target);

        if (d < nCont.distToTargetThreshold) // if the agent is next to the target
        {

            isNearTarget = true;
            nmAgent.isStopped = true;

            // Always kill movement steppers first — safe because we track liveness
            SafeDestroyStepper("CheckDistToTarget");
            SafeDestroyStepper("Move");

            if (_pendingCallStation != null)
            {
                ElevatorCallStation station = _pendingCallStation;
                int destFloor = _pendingDestFloor;
                _pendingCallStation = null;

                bool accepted = station.TryRegisterWaitingAgent(this, destFloor);
                if (!accepted) TakeStairs(targetRoom);
                return;
            }

            int simMinute = _time.CurrentHour * 60 + _time.CurrentMinute;

            switch (_schedule.CurrentActivity)
                //Once the agent has arrived at its location, it waits a certain amount of time depending on the state of the agent
                // The performance hit has to happen somewhere here, just dont know why yet. 
            {
                case AgentActivity.GoingToClass:
                    _schedule.SetActivity(AgentActivity.InClass);
                    // ScheduleTick will detect InClass → past EndMinute and call PickAndDoRandomActivity
                    break;

                case AgentActivity.GoingToBathroom:
                    int bathroomTime = Random.Range(2, 6);
                    _schedule.SetActivity(AgentActivity.InBathroom);
                    StartTimedStay(bathroomTime);
                    break;

                case AgentActivity.GoingToOfficeHours:
                    int ohTime = Random.Range(10, 30);
                    _schedule.SetActivity(AgentActivity.InOfficeHours);
                    StartTimedStay(ohTime);
                    break;

                case AgentActivity.Chatting:
                    int chatTime = Random.Range(2, 8);
                    StartTimedStay(chatTime);
                    break;

                case AgentActivity.Wandering:
                    int wanderTime = Random.Range(3, 12);
                    StartTimedStay(wanderTime);
                    break;

                default:
                    break;
            }
        }
        else
        {
            isNearTarget = false;
        }
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

        if (_stayMinutesRemaining <= 0)
        {
            SafeDestroyStepper("StayInPlace");
            int simMinute = _time.CurrentHour * 60 + _time.CurrentMinute;
            PickAndDoRandomActivity(simMinute);
        }
    }

    // ── Navigation ────────────────────────────────────────────────────────────

    void NavigateTo(GameObject room)
    {
        if (room == null)
        {
            return;
        }


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
        Debug.Log($"[{name}] SetTarget: {room.name} → position {target}");

        // Kill any in-flight movement steppers before starting new ones
        SafeDestroyStepper("CheckDistToTarget");
        SafeDestroyStepper("Move");

        nmAgent.SetDestination(target);
        nmAgent.isStopped = false;

        SafeCreateStepper("CheckDistToTarget", CheckDistToTarget, 1, 100);
        SafeCreateStepper("Move", Move, 1, 105);
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

        SafeDestroyStepper("CheckDistToTarget");
        SafeDestroyStepper("Move");
        SafeCreateStepper("CheckDistToTarget", CheckDistToTarget, 1, 100);
        SafeCreateStepper("Move", Move, 1, 105);
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