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

    public int DepartureWindowMinutes { get; private set; }
    public AgentSchedule Schedule => _schedule;

    // ── Class-end guard ───────────────────────────────────────────────────────
    private bool _classEndHandled = false;

    // ── Bathroom urge ─────────────────────────────────────────────────────────
    private int _bathroomUrgeSteps = 0;
    private bool _bathroomUrgeActive = false;
    private bool _bathroomNeedPending = false;

    // ── Stepper liveness tracking ─────────────────────────────────────────────
    private bool _stepperAlive_DeferredInit = false;
    private bool _stepperAlive_ScheduleTick = false;
    private bool _stepperAlive_CheckDist = false;
    private bool _stepperAlive_Move = false;
    private bool _stepperAlive_StayInPlace = false;
    private bool _stepperAlive_BathroomUrge = false;

    // Tracks steppers created this ABMU tick that haven't been flushed into the
    // scheduler dictionary yet. SafeDestroyStepper checks this before calling
    // DestroyStepper — destroying a stepper before RegisterSteppersCreated runs
    // causes the NullReferenceException in Scheduler.DeregisterDestroyedStepper.
    private readonly System.Collections.Generic.HashSet<string> _pendingRegistration = new();

    // ── Setup ─────────────────────────────────────────────────────────────────

    public void SetSchedule(AgentSchedule schedule)
    {
        _schedule = schedule;
        DepartureWindowMinutes = Random.Range(10, 21);
    }

    public void Init(GameObject startRoom)
    {
        base.Init();
        nCont = FindObjectOfType<NavigationController>();
        nmAgent = GetComponent<NavMeshAgent>();
        _time = FindObjectOfType<TimeManager>();
        _callStations = FindObjectsOfType<ElevatorCallStation>();

        SetNMAgentProperties();
        targetRoom = startRoom;
        Debug.Log(name + " just used it's init function.");
        SafeCreateStepper("DeferredInit", DeferredInit, 1, 1);
    }




    public void WakeUpForClass(AgentSchedule.ClassEntry classEntry, Vector3 spawnPosition)
    {
        transform.position = spawnPosition;
        nmAgent.nextPosition = spawnPosition;

        _schedule.SetActiveClass(classEntry);
        _schedule.SetActivity(AgentActivity.GoingToClass);
        _classEndHandled = false;
        _bathroomNeedPending = false;

        NavigateTo(classEntry.ClassroomNode);
        ResetBathroomUrge();
    }

    // ── Stepper lifecycle helpers ─────────────────────────────────────────────

    void SafeCreateStepper(string stepperName, ABMU.Utilities.Del method, int step, int priority)
    {
        switch (stepperName)
        {
            case "DeferredInit":
                if (_stepperAlive_DeferredInit) return;
                _stepperAlive_DeferredInit = true; break;
            case "ScheduleTick":
                if (_stepperAlive_ScheduleTick) return;
                _stepperAlive_ScheduleTick = true; break;
            case "CheckDistToTarget":
                if (_stepperAlive_CheckDist) return;
                _stepperAlive_CheckDist = true; break;
            case "Move":
                if (_stepperAlive_Move) return;
                _stepperAlive_Move = true; break;
            case "StayInPlace":
                if (_stepperAlive_StayInPlace) return;
                _stepperAlive_StayInPlace = true; break;
            case "BathroomUrge":
                if (_stepperAlive_BathroomUrge) return;
                _stepperAlive_BathroomUrge = true; break;
        }
        _pendingRegistration.Add(stepperName);
        CreateStepper(method, step, priority);
    }

    void SafeDestroyStepper(string stepperName)
    {
        // If this stepper was created this tick but ABMU hasn't run
        // RegisterSteppersCreated yet, it isn't in the scheduler dict.
        // Calling DestroyStepper now causes NullReferenceException in
        // DeregisterDestroyedStepper. Just clear the flags instead —
        // the stepper will be registered next tick but the liveness flag
        // being false means it will do nothing and never be re-destroyed.
        if (_pendingRegistration.Contains(stepperName))
        {
            _pendingRegistration.Remove(stepperName);
            switch (stepperName)
            {
                case "DeferredInit": _stepperAlive_DeferredInit = false; break;
                case "ScheduleTick": _stepperAlive_ScheduleTick = false; break;
                case "CheckDistToTarget": _stepperAlive_CheckDist = false; break;
                case "Move": _stepperAlive_Move = false; break;
                case "StayInPlace": _stepperAlive_StayInPlace = false; break;
                case "BathroomUrge": _stepperAlive_BathroomUrge = false; break;
            }
            return;
        }

        switch (stepperName)
        {
            case "DeferredInit":
                if (!_stepperAlive_DeferredInit) return;
                _stepperAlive_DeferredInit = false; break;
            case "ScheduleTick":
                if (!_stepperAlive_ScheduleTick) return;
                _stepperAlive_ScheduleTick = false; break;
            case "CheckDistToTarget":
                if (!_stepperAlive_CheckDist) return;
                _stepperAlive_CheckDist = false; break;
            case "Move":
                if (!_stepperAlive_Move) return;
                _stepperAlive_Move = false; break;
            case "StayInPlace":
                if (!_stepperAlive_StayInPlace) return;
                _stepperAlive_StayInPlace = false; break;
            case "BathroomUrge":
                if (!_stepperAlive_BathroomUrge) return;
                _stepperAlive_BathroomUrge = false; break;
            default: return;
        }
        DestroyStepper(stepperName);
    }

    // ── DeferredInit ──────────────────────────────────────────────────────────

    void DeferredInit()
    {
        _pendingRegistration.Remove("DeferredInit");
        if (nCont == null || _time == null || _schedule == null) return;
        if (_scheduleInitialized) { SafeDestroyStepper("DeferredInit"); return; }

        _scheduleInitialized = true;
        SafeDestroyStepper("DeferredInit");
        SafeCreateStepper("ScheduleTick", ScheduleTick, 30, 1);
    }

    // ── ScheduleTick ──────────────────────────────────────────────────────────

    void ScheduleTick()
    {
        Debug.Log($"[{name}] total steppers on controller: {controller.scheduler.steppersEveryTick.Count}");
        _pendingRegistration.Remove("ScheduleTick");
        if (_schedule == null || _time == null || nCont == null) return;
        if (_isRiding) return;

        int simMinute = _time.CurrentHour * 60 + _time.CurrentMinute;

        switch (_schedule.CurrentActivity)
        {
            case AgentActivity.OffCampus:
                break;

            case AgentActivity.Idle:
                var toHead = _schedule.FindClassToHeadTo(simMinute, DepartureWindowMinutes);
                if (toHead.HasValue)
                {
                    _schedule.SetActiveClass(toHead.Value);
                    _schedule.SetActivity(AgentActivity.GoingToClass);
                    _classEndHandled = false;
                    NavigateTo(toHead.Value.ClassroomNode);
                    ResetBathroomUrge();
                }
                break;

            case AgentActivity.InClass:
                if (simMinute >= _schedule.EndMinute && !_classEndHandled)
                {
                    _classEndHandled = true;
                    _schedule.SetActivity(AgentActivity.Wandering);
                    StartTimedStay(Random.Range(1, 30));
                }
                break;

            case AgentActivity.Done:
                SafeDestroyStepper("ScheduleTick");
                break;
        }
    }

    // ── Post-class decision ───────────────────────────────────────────────────

    void PostClassDecision()
    {
        int simMinute = _time.CurrentHour * 60 + _time.CurrentMinute;
        int gapMinutes = _schedule.MinutesUntilNextClass(simMinute);

        float stayChance;
        if (gapMinutes <= 60) stayChance = 0.65f;
        else if (gapMinutes <= 120) stayChance = 0.25f;
        else stayChance = 0.10f;

        if (Random.value < stayChance)
        {
            float roll = Random.value;

            if (_bathroomNeedPending || roll < 0.30f)
            {
                _bathroomNeedPending = false;
                GoToBathroom();
            }
            else if (roll < 0.40f) 
            {
                Debug.Log("Going to office hours");
                GoToOfficeHours();
            }
            else
            {
                GoStudy();
            }
        }
        else
        {
            LeaveBuilding();
        }
    }

    // ── After bathroom / study finishes ──────────────────────────────────────

    void AfterActivityDecision()
    {
        int simMinute = _time.CurrentHour * 60 + _time.CurrentMinute;

        // Check if next class is approaching
        var nextClass = _schedule.NextClass;
        if (nextClass.HasValue && nextClass.Value.Section.MeetsOnDay(_time.GetCurrentDayOfWeek()))
        {
            int headOutAt = nextClass.Value.Section.startMinute - DepartureWindowMinutes;
            if (simMinute >= headOutAt)
            {
                _schedule.SetActiveClass(nextClass.Value);
                _schedule.SetActivity(AgentActivity.GoingToClass);
                _classEndHandled = false;
                NavigateTo(nextClass.Value.ClassroomNode);
                return;
            }
        }

        int gapMinutes = _schedule.MinutesUntilNextClass(simMinute);

        if (gapMinutes > 90)
        {
            // Too long until next class — leave the building
            LeaveBuilding();
        }
        else if (_bathroomNeedPending)
        {
            _bathroomNeedPending = false;
            GoToBathroom();
        }
        else
        {
            // Gap is short enough — stay put until class time
            // Don't GoStudy() again — just wait in place
            StartTimedStay(Mathf.Clamp(gapMinutes - DepartureWindowMinutes, 5, 60));
        }
    }

    // ── Activity helpers ──────────────────────────────────────────────────────

    void GoToBathroom()
    {
        GameObject node = nCont.GetClosestBathroomNode(transform.position);
        if (node == null) { AfterActivityDecision(); return; }
        _schedule.SetActivity(AgentActivity.GoingToBathroom);
        NavigateDirect(node);
    }

    void GoToOfficeHours()
    {
        GameObject node = nCont.GetRandomOfficeHoursNode();
        if (node == null) { AfterActivityDecision(); return; }
        _schedule.SetActivity(AgentActivity.GoingToOfficeHours);
        NavigateDirect(node);
    }


    void GoStudy()
    {
        GameObject node = nCont.GetRandomStudyingNode();
        if (node == null) { LeaveBuilding(); return; }
        _schedule.SetActivity(AgentActivity.GoingToStudying);
        NavigateDirect(node);
    }

    void LeaveBuilding()
    {
        GameObject exitNode = nCont.GetRandomExitNode();
        if (exitNode == null) { DeactivateAgent(); return; }
        _schedule.SetActivity(AgentActivity.GoingToExit);
        NavigateDirect(exitNode);
    }

    // ── Agent deactivation ────────────────────────────────────────────────────

    void DeactivateAgent()
    {
        SafeDestroyStepper("CheckDistToTarget");
        SafeDestroyStepper("Move");
        SafeDestroyStepper("StayInPlace");
        SafeDestroyStepper("BathroomUrge");
        _schedule.SetActivity(AgentActivity.OffCampus);
        gameObject.SetActive(false);
    }

    // ── Arrival handling ──────────────────────────────────────────────────────

    void CheckDistToTarget()
    {
        _pendingRegistration.Remove("CheckDistToTarget");
        float d = Vector3.Distance(transform.position, target);
        if (d >= nCont.distToTargetThreshold) { isNearTarget = false; return; }

        isNearTarget = true;
        nmAgent.isStopped = true;
        SafeDestroyStepper("CheckDistToTarget");
        SafeDestroyStepper("Move");

        if (_pendingCallStation != null)
        {
            ElevatorCallStation station = _pendingCallStation;
            int destFloor = _pendingDestFloor;
            _pendingCallStation = null;
            if (!station.TryRegisterWaitingAgent(this, destFloor))
                TakeStairs(targetRoom);
            return;
        }

        switch (_schedule.CurrentActivity)
        {
            case AgentActivity.GoingToClass:
                _schedule.SetActivity(AgentActivity.InClass);
                break;

            case AgentActivity.GoingToBathroom:
                _schedule.SetActivity(AgentActivity.InBathroom);
                StartTimedStay(Random.Range(2, 6));
                break;

            case AgentActivity.GoingToStudying:
                _schedule.SetActivity(AgentActivity.InStudying);
                // Study until close to next class, or for a long time if done for the day
                int simMinute = _time.CurrentHour * 60 + _time.CurrentMinute;
                int gap = _schedule.MinutesUntilNextClass(simMinute);
                int studyDuration;
                if (gap <= 0 || gap > 200)
                    studyDuration = Random.Range(60, 120); 
                else
                    studyDuration = Mathf.Clamp(gap - 15, 10, 90); // Study until ~15 min before next class
                StartTimedStay(studyDuration);
                break;

            case AgentActivity.GoingToExit:
                DeactivateAgent();
                break;
            case AgentActivity.GoingToOfficeHours:
                _schedule.SetActivity(AgentActivity.InOfficeHours);
                StartTimedStay(Random.Range(20, 60));
                break;

            default:
                break;
        }
    }

    // ── Timed stationary stay ─────────────────────────────────────────────────

    int _stayMinutesRemaining = 0;

    void StartTimedStay(int simMinutes)
    {
        SafeDestroyStepper("StayInPlace");
        _stayMinutesRemaining = simMinutes;
        SafeCreateStepper("StayInPlace", StayInPlace, 2, 1);
    }

    void StayInPlace()
    {
        _pendingRegistration.Remove("StayInPlace");
        _stayMinutesRemaining--;
        if (_stayMinutesRemaining > 0) return;

        SafeDestroyStepper("StayInPlace");

        switch (_schedule.CurrentActivity)
        {
            case AgentActivity.Wandering:
                PostClassDecision();
                break;
            case AgentActivity.InBathroom:
            case AgentActivity.InStudying:
                AfterActivityDecision();
                break;
            case AgentActivity.InOfficeHours:
                AfterActivityDecision();
                break;
            default:
                AfterActivityDecision();
                break;
        }
    }

    // ── Bathroom urge system ──────────────────────────────────────────────────

    private const int BATHROOM_URGE_MIN = 180;
    private const int BATHROOM_URGE_MAX = 360;

    void ResetBathroomUrge()
    {
        SafeDestroyStepper("BathroomUrge");
        _bathroomUrgeSteps = Random.Range(BATHROOM_URGE_MIN, BATHROOM_URGE_MAX + 1);
        _bathroomUrgeActive = true;
        SafeCreateStepper("BathroomUrge", BathroomUrgeTick, 60, 50);
    }

    void BathroomUrgeTick()
    {
        _pendingRegistration.Remove("BathroomUrge");
        if (!_bathroomUrgeActive) return;
        _bathroomUrgeSteps--;
        if (_bathroomUrgeSteps > 0) return;

        _bathroomUrgeActive = false;
        SafeDestroyStepper("BathroomUrge");

        var act = _schedule.CurrentActivity;
        bool canGoNow = act == AgentActivity.InStudying ||
                        act == AgentActivity.Wandering ||
                        act == AgentActivity.Idle;

        if (canGoNow)
        {
            SafeDestroyStepper("StayInPlace");
            GoToBathroom();
        }
        else
        {
            _bathroomNeedPending = true;
        }

        ResetBathroomUrge();
    }

    // ── Navigation ────────────────────────────────────────────────────────────

    void NavigateTo(GameObject room)
    {
        if (room == null) return;

        int newFloor = GetFloorOfRoom(room);
        int thisFloor = GetFloorOfRoom(targetRoom);

        if (newFloor != thisFloor && newFloor >= 0 && thisFloor >= 0)
        {
            ElevatorCallStation station = GetBestStationForFloor(thisFloor);
            if (station != null && Random.value < 0.80f)
            {
                StartElevatorJourney(room, newFloor, station);
                return;
            }
        }

        SetTarget(room);
    }

    void NavigateDirect(GameObject room)
    {
        if (room == null) return;
        SetTarget(room);
    }

    public void SetTarget(GameObject room)
    {
        SnapToNavMesh();

        targetRoom = room;
        target = nCont.GetRandomPointInRoom(room);

        SafeDestroyStepper("CheckDistToTarget");
        SafeDestroyStepper("Move");

        nmAgent.isStopped = false;
        nmAgent.SetDestination(target);

        SafeCreateStepper("CheckDistToTarget", CheckDistToTarget, 2, 100);
        SafeCreateStepper("Move", Move, 1, 105);
    }

    // ── Movement ──────────────────────────────────────────────────────────────

    void Move()
    {
        _pendingRegistration.Remove("Move");
        if (_isRiding) return;
        nmAgent.velocity = Vector3.zero;
        nmAgent.nextPosition = transform.position + nmAgent.desiredVelocity * 0.03f;
        transform.LookAt(nmAgent.nextPosition, Vector3.up);
        transform.position = nmAgent.nextPosition;
    }

    // ── NavMesh safety ────────────────────────────────────────────────────────

    private void SnapToNavMesh()
    {
        if (nmAgent.isOnNavMesh) return;
        foreach (float r in new float[] { 2f, 5f, 10f })
        {
            if (NavMesh.SamplePosition(transform.position, out NavMeshHit hit, r, NavMesh.AllAreas))
            {
                nmAgent.Warp(hit.position);
                transform.position = hit.position;
                return;
            }
        }
        Debug.LogWarning($"[{name}] SnapToNavMesh: no surface within 10 units.");
    }

    // ── Elevator ──────────────────────────────────────────────────────────────

    private void StartElevatorJourney(GameObject destinationRoom, int destFloor,
                                       ElevatorCallStation station)
    {
        SnapToNavMesh();
        targetRoom = destinationRoom;
        TargetFloor = destFloor;
        _pendingCallStation = station;
        _pendingDestFloor = destFloor;
        target = station.transform.position;
        nmAgent.isStopped = false;
        nmAgent.SetDestination(target);

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
        Vector3 exitPos = _elevator.GetCallStationPosition(floorIndex);
        _elevator.ExitAgent(this);
        _elevator = null;
        _isRiding = false;
        transform.position = exitPos;
        nmAgent.Warp(exitPos);
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
        nmAgent.acceleration = 999f;
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
        if (best != null && bestLoad >= nCont.maxElevatorLoadBeforeStairs) return null;
        return best;
    }
}