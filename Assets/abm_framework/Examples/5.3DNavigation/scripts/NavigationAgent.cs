using UnityEngine;
using ABMU.Core;
using UnityEngine.AI;
using System.Collections;

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
    private bool usedBathroomRecently = false;

    int _stayEndMinute = -1;
    // ── Stepper liveness tracking ─────────────────────────────────────────────
    private bool _stepperAlive_DeferredInit = false;
    private bool _stepperAlive_ScheduleTick = false;
    private bool _stepperAlive_CheckDist = false;
    private bool _stepperAlive_Move = false;
    private bool _stepperAlive_StayInPlace = false;
    private bool _stepperAlive_BathroomUrge = false;



    private readonly System.Collections.Generic.HashSet<string> _pendingRegistration = new();

    // ── Animation ─────────────────────────────────────────────────────────────
    private bool _isMovingAnim = false;
    private Coroutine _animTransitionCoroutine;
    public Animator animator;

    private int _respawnAtMinute = -1;
    private Renderer[] _renderers;
    private Collider[] _colliders;


    void SetMovingAnimState(bool isMoving)
    {
        if (animator == null || isMoving == _isMovingAnim) return;
        _isMovingAnim = isMoving;

        if (_animTransitionCoroutine != null) StopCoroutine(_animTransitionCoroutine);
        _animTransitionCoroutine = StartCoroutine(isMoving ? BeginWalkingRoutine() : BeginIdleRoutine());
    }

    IEnumerator BeginWalkingRoutine()
    {
        animator.SetBool("isIdle", false);
        animator.SetBool("stopWalking", false);
        animator.SetBool("startWalking", true);

        yield return new WaitForSeconds(1); 

        animator.SetBool("startWalking", false);
        animator.SetBool("Walking", true);
    }

    IEnumerator BeginIdleRoutine()
    {
        animator.SetBool("startWalking", false);
        animator.SetBool("Walking", false);
        animator.SetBool("stopWalking", true);

        yield return null;

        animator.SetBool("stopWalking", false);
        animator.SetBool("isIdle", true);
    }

    //Sets the schedule for the agent
    public void SetSchedule(AgentSchedule schedule)
    {
        _schedule = schedule;
        DepartureWindowMinutes = Random.Range(10, 21);
    }

    // Creates steppers for the agent, also assigns scene scripts
    public void Init(GameObject startRoom)
    {
        base.Init();
        nCont = FindObjectOfType<NavigationController>();
        nmAgent = GetComponent<NavMeshAgent>();
        _time = FindObjectOfType<TimeManager>();
        _callStations = FindObjectsOfType<ElevatorCallStation>();
        if(animator != null)
        {
            animator.SetBool("isIdle", true);
            animator.SetBool("Walking", false);
        }

        SetNMAgentProperties();
        targetRoom = startRoom;
        SafeCreateStepper("DeferredInit", DeferredInit, 1, 1);
    }



    // This is SUPPOSED to wake the agent up for class, but its never called
    // I'll make sure to use the function later for when we are simulating multiple days
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

    // Creates stepper functions for the ABMU script. Steppers run every frame on a step interval.
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
        
        _pendingRegistration.Remove("ScheduleTick");
        if (_schedule == null || _time == null || nCont == null) return;
        if (_isRiding) return;

        int simMinute = _time.CurrentHour * 60 + _time.CurrentMinute;

        switch (_schedule.CurrentActivity)
        {
            case AgentActivity.OffCampus:
                if (_respawnAtMinute >= 0 && simMinute >= _respawnAtMinute)
                    RespawnAgent();
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
                    StartTimedStay(Random.Range(nCont.classExitLingerMin, nCont.classExitLingerMax + 1));
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


    IEnumerator ResetBathroomBool()
    {
        yield return new WaitForSeconds(60);
        usedBathroomRecently = false;
    }

    void AfterActivityDecision()
    {
        int simMinute = _time.CurrentHour * 60 + _time.CurrentMinute;

        var nextClass = _schedule.NextClass;
        if (nextClass.HasValue && nextClass.Value.Section.MeetsOnDay(_time.GetCurrentDayOfWeek()))
        {
            int headOutAt = nextClass.Value.Section.startMinute - DepartureWindowMinutes;

            if (simMinute >= nextClass.Value.Section.endMinute)
            {
                // Mark it attended anyway so scheduling logic doesn't loop on it,
                // then re-run the decision against the class after this one.
                _schedule.SetActiveClass(nextClass.Value);
                AfterActivityDecision();
                return;
            }

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
        ResetBathroomUrge();  
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
    private bool _pendingLeaveIsExit = false;
    void LeaveBuilding()
    {
        int simMinute = _time.CurrentHour * 60 + _time.CurrentMinute;
        var nextClass = _schedule.NextClass;

        GameObject exitNode = nCont.GetRandomExitNode();
        if (exitNode == null) { DeactivateAgent(hasMoreClasses: false); return; }

        // Decide respawn timing before leaving. If there's another class today,
        // come back 15 min before it starts; otherwise this agent is done for the day.
        if (nextClass.HasValue && nextClass.Value.Section.MeetsOnDay(_time.GetCurrentDayOfWeek()))
            _respawnAtMinute = nextClass.Value.Section.startMinute - 15;
        else
            _respawnAtMinute = -1;

        _schedule.SetActivity(AgentActivity.GoingToExit);
        _pendingLeaveIsExit = true;
        NavigateDirect(exitNode);
    }

    // ── Agent deactivation ────────────────────────────────────────────────────

    void DeactivateAgent(bool hasMoreClasses)
    {
        SafeDestroyStepper("CheckDistToTarget");
        SafeDestroyStepper("Move");
        SafeDestroyStepper("StayInPlace");
        SafeDestroyStepper("BathroomUrge");

        _schedule.SetActivity(AgentActivity.OffCampus);

        if (_renderers == null) _renderers = GetComponentsInChildren<Renderer>();
        if (_colliders == null) _colliders = GetComponentsInChildren<Collider>();
        foreach (var r in _renderers) r.enabled = false;
        foreach (var c in _colliders) c.enabled = false;

        // Stop the agent instead of disabling the component — avoids
        // triggering NavMesh crowd/avoidance recomputation.
        nmAgent.isStopped = true;
        nmAgent.velocity = Vector3.zero;

        if (!hasMoreClasses)
            _schedule.SetActivity(AgentActivity.Done);
    }

    // ── Arrival handling ──────────────────────────────────────────────────────

    void CheckDistToTarget()
    {
        _pendingRegistration.Remove("CheckDistToTarget");
        float d = Vector3.Distance(transform.position, target);
        if (d >= nCont.distToTargetThreshold) { isNearTarget = false; return; }

        isNearTarget = true;
        nmAgent.isStopped = true;
        SetMovingAnimState(false);
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
                if(usedBathroomRecently == false) // prevents the agent from using the bathroom too often, causing unrealistic behaviour. 
                {
                    usedBathroomRecently = true;
                    ResetBathroomBool();
                    StartTimedStay(Random.Range(2, 6));

                }
                break;

            case AgentActivity.GoingToStudying:
                _schedule.SetActivity(AgentActivity.InStudying);
                // Short, per-agent-jittered check-in instead of a blind 60-120 min roll.
                // AfterActivityDecision (called when this elapses) already knows how to
                // send the agent to a class, leave the building, or wait again — we just
                // need to actually reach it soon instead of locking in for hours up front.
                StartTimedStay(Random.Range(10, 21));
                break;

            case AgentActivity.GoingToExit:
                DeactivateAgent(false);
                break;
            case AgentActivity.GoingToOfficeHours:
                bool hasMore = _schedule.NextClass.HasValue &&
               _schedule.NextClass.Value.Section.MeetsOnDay(_time.GetCurrentDayOfWeek());
                DeactivateAgent(hasMoreClasses: hasMore);
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
        int nowMinute = _time.CurrentHour * 60 + _time.CurrentMinute;
        _stayEndMinute = nowMinute + simMinutes;

        Debug.Log($"[{name}] StartTimedStay called: nowMinute={nowMinute}, " +
                  $"duration={simMinutes}, newEnd={_stayEndMinute}, " +
                  $"activity={_schedule.CurrentActivity}\n{System.Environment.StackTrace}");

        SafeCreateStepper("StayInPlace", StayInPlace, 2, 1);
    }

    void StayInPlace()
    {
        _pendingRegistration.Remove("StayInPlace");
        Debug.Log($"[{name}] should start staying in place.");
        int nowMinute = _time.CurrentHour * 60 +_time.CurrentMinute;
        if (nowMinute <= _stayEndMinute) return;
        SafeDestroyStepper("StayInPlace");
        Debug.Log($"[{name}] should stop staying in place.");
        switch (_schedule.CurrentActivity)
        {
            case AgentActivity.Wandering:
                PostClassDecision();
                break;
            case AgentActivity.InBathroom:
                AfterActivityDecision();
                break;
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

        // Already there or already heading there — nothing to flag.
        if (act == AgentActivity.InBathroom || act == AgentActivity.GoingToBathroom)
        {
            ResetBathroomUrge();
            return;
        }

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
        int thisFloor = GetFloorFromPosition(transform.position);  

        if (newFloor != thisFloor && newFloor >= 0 && thisFloor >= 0)
        {
            ElevatorCallStation station = GetBestStationForFloor(thisFloor);
            if (station != null)
            {
                StartElevatorJourney(room, newFloor, station);
                return;
            }
        }

        SetTarget(room);
    }
    private int GetFloorFromPosition(Vector3 pos)
    {
        int best = -1;
        float bestDist = float.MaxValue;
        foreach (var s in _callStations)
        {
            float dist = Mathf.Abs(s.transform.position.y - pos.y);
            if (dist < bestDist) { bestDist = dist; best = s.floorIndex; }
        }
        return best;
    }


    void RespawnAgent()
    {
        _respawnAtMinute = -1;

        foreach (var r in _renderers) r.enabled = true;
        foreach (var c in _colliders) c.enabled = true;

        GameObject spawnNode = nCont.GetRandomExitNode();
        if (spawnNode != null)
        {
            Vector3 pos = spawnNode.transform.position;
            transform.position = pos;
            nmAgent.Warp(pos);
        }

        nmAgent.isStopped = false;
        _schedule.SetActivity(AgentActivity.Idle);
        ResetBathroomUrge();
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
        SetMovingAnimState(true);   
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
        SetMovingAnimState(true);


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
        SetMovingAnimState(false);
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
        ElevatorCallStation bestStation = null;
        foreach (var s in _callStations)
        {
            float dist = Mathf.Abs(s.transform.position.y - roomY);
            if (dist < bestDist) { bestDist = dist; best = s.floorIndex; bestStation = s; }
        }
        Debug.Log($"[GetFloorOfRoom] room={room.name} roomY={roomY:F2} → matched station={bestStation?.name}, " +
                  $"stationY={bestStation?.transform.position.y:F2}, floorIndex={best}, dist={bestDist:F2}");
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