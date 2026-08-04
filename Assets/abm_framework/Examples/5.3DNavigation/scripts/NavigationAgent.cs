using UnityEngine;
using ABMU.Core;
using UnityEngine.AI;
using System.Collections;

public class NavigationAgent : AbstractAgent
{
    #region References
    // ── Scene references ──────────────────────────────────────────────────────
    NavigationController nCont;
    NavMeshAgent nmAgent;
    TimeManager _time;
    [SerializeField] private SkinnedMeshRenderer meshRenderer;
    [SerializeField] private ParticleSystem mouth;

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
    [Header("Bathroom Behavior")]
    [SerializeField] private float bathroomChanceDefault = 0.10f;
    [SerializeField] private float bathroomChanceIncrement = 0.08f;
    private float _bathroomChance;
    int _stayEndMinute = -1;
    // ── Stepper liveness tracking ─────────────────────────────────────────────
    private bool _stepperAlive_DeferredInit = false;
    private bool _stepperAlive_ScheduleTick = false;
    private bool _stepperAlive_CheckDist = false;
    private bool _stepperAlive_Move = false;
    private bool _stepperAlive_StayInPlace = false;


    public event System.Action<NavigationAgent> OnFinishedForDay;

    private readonly System.Collections.Generic.HashSet<string> _pendingRegistration = new();

    // ── Animation ─────────────────────────────────────────────────────────────
    private bool _isMovingAnim = false;
    private Coroutine _animTransitionCoroutine;
    public Animator animator;

    private int _respawnAtMinute = -1;
    private Renderer[] _renderers;
    private Collider[] _colliders;

    // ── Sim-speed-scaled movement ─────────────────────────────────────────────
    private float _baseSpeed = -1f;
    private bool _subscribedToSimSpeed = false;

    #endregion

    #region Initilizations
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
    void SetNMAgentProperties()
    {
        nmAgent.updatePosition = false;
        nmAgent.velocity = Vector3.zero;
        nmAgent.acceleration = 999f;
    }

    private bool _fullyInitialized = false;

    public void Init(GameObject startRoom)
    {
        if (_fullyInitialized) return;
        _fullyInitialized = true;

        base.Init();
        nCont = FindObjectOfType<NavigationController>();
        nmAgent = GetComponent<NavMeshAgent>();
        _time = FindObjectOfType<TimeManager>();
        _callStations = FindObjectsOfType<ElevatorCallStation>();
        if (animator != null)
        {
            animator.SetBool("isIdle", true);
            animator.SetBool("Walking", false);
        }

        SetNMAgentProperties();
        targetRoom = startRoom;

        if (_baseSpeed < 0f) _baseSpeed = nmAgent.speed; // capture prefab's authored speed once

        if (!_subscribedToSimSpeed && _time != null)
        {
            _subscribedToSimSpeed = true;
            _time.OnSimSpeedChanged += HandleSimSpeedChanged;
            HandleSimSpeedChanged(_time.GetAgentSpeedMultiplier()); // apply current speed immediately
        }

        SafeCreateStepper("DeferredInit", DeferredInit, 1, 1);
    }

    private void HandleSimSpeedChanged(float multiplier)
    {
        if (nmAgent != null && _baseSpeed > 0f)
            nmAgent.speed = _baseSpeed * multiplier;
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


        NavigateTo(classEntry.ClassroomNode);
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
            default: return;
        }
        DestroyStepper(stepperName);
    }


    void DeferredInit()
    {
        if (!_stepperAlive_DeferredInit) return;
        _pendingRegistration.Remove("DeferredInit");
        if (nCont == null || _time == null || _schedule == null) return;
        if (_scheduleInitialized) { SafeDestroyStepper("DeferredInit"); return; }

        _scheduleInitialized = true;
        SafeDestroyStepper("DeferredInit");
        SafeCreateStepper("ScheduleTick", ScheduleTick, 30, 1);
    }
    #endregion

    #region AgentDecisions
    void ScheduleTick()
    {
        if (!_stepperAlive_ScheduleTick) return;
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
                var toHead = _schedule.FindClassToHeadTo(simMinute, DepartureWindowMinutes, _time.GetCurrentDayOfWeek());
                meshRenderer.enabled = false;
                var emission = mouth.emission;
                emission.enabled = false;
                if (toHead.HasValue)
                {
                    meshRenderer.enabled = true;
                    emission.enabled = true;
                    _schedule.SetActiveClass(toHead.Value);
                    _schedule.SetActivity(AgentActivity.GoingToClass);
                    _classEndHandled = false;
                    NavigateTo(toHead.Value.ClassroomNode);

                }
                break;

            case AgentActivity.Done:
                SafeDestroyStepper("ScheduleTick");
                break;
        }
    }

    // ── Post-class decision ───────────────────────────────────────────────────

    void AfterActivityDecision()
    {
        int simMinute = _time.CurrentHour * 60 + _time.CurrentMinute;
        var today = _time.GetCurrentDayOfWeek();

        var nextClass = _schedule.NextClassOnDay(today);
        if (nextClass.HasValue)
        {
            int headOutAt = nextClass.Value.Section.startMinute - DepartureWindowMinutes;

            if (simMinute >= nextClass.Value.Section.endMinute)
            {
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

        if (Random.value < _bathroomChance)
        {
            GoToBathroom();
            return;
        }
        IncrementBathroomChance();

        int gapMinutes = _schedule.MinutesUntilNextClassOnDay(simMinute, today);
        RouteByGap(gapMinutes);
    }

    void PostClassDecision()
    {
        int simMinute = _time.CurrentHour * 60 + _time.CurrentMinute;
        int gapMinutes = _schedule.MinutesUntilNextClassOnDay(simMinute, _time.GetCurrentDayOfWeek());

        if (Random.value < _bathroomChance)
        {
            GoToBathroom();
            return;
        }
        IncrementBathroomChance();
       // this function runs once, so def not the issue :(
        RouteByGap(gapMinutes);
    }
    void RouteByGap(int gapMinutes)
    {
        if (gapMinutes > 90)
        {
            // Plenty of time — mostly leave, sometimes study a bit first.
            if (Random.value < 0.70f) LeaveBuilding();
            else GoStudy();
        }
        else if (gapMinutes > 60)
        {
            // Roughly even split between studying, office hours, or leaving.
            float roll = Random.value;
            if (roll < 0.34f) GoStudy();
            else if (roll < 0.67f) GoToOfficeHours();
            else LeaveBuilding();
        }
        else if (gapMinutes > 30)
        {
            // Getting close — usually study, occasionally leave.
            if (Random.value < 0.75f) GoStudy();
            else LeaveBuilding();
        }
        else
        {
      
            GoStudy();
        }
    }
    #endregion

    #region ActivityHelpers


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
        var today = _time.GetCurrentDayOfWeek();
        var nextClass = _schedule.NextClassOnDay(today);

        GameObject exitNode = nCont.GetRandomExitNode();
        if (exitNode == null) { DeactivateAgent(hasMoreClasses: false); return; }

        _respawnAtMinute = nextClass.HasValue ? nextClass.Value.Section.startMinute - 15 : -1;

        _schedule.SetActivity(AgentActivity.GoingToExit);
        _pendingLeaveIsExit = true;
        NavigateDirect(exitNode);
    }
    #endregion

    #region DeactivateAgents

    void DeactivateAgent(bool hasMoreClasses)
    {
        SafeDestroyStepper("CheckDistToTarget");
        SafeDestroyStepper("Move");
        SafeDestroyStepper("StayInPlace");
        SafeDestroyStepper("BathroomUrge");

        _schedule.SetActivity(AgentActivity.OffCampus);


        foreach (var r in _renderers) r.enabled = false;
        foreach (var c in _colliders) c.enabled = false;

        // Stop the agent instead of disabling the component — avoids
        // triggering NavMesh crowd/avoidance recomputation.
        nmAgent.isStopped = true;
        nmAgent.velocity = Vector3.zero;

        if (!hasMoreClasses)
            _schedule.SetActivity(AgentActivity.Done);
            OnFinishedForDay?.Invoke(this);
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

    }

    private void OnDestroy()
    {
        if (_time != null) _time.OnSimSpeedChanged -= HandleSimSpeedChanged;
    }
    #endregion

    #region Schedule
    public void AssignNewSchedule(AgentSchedule schedule, GameObject startRoom, Vector3 spawnPos)
    {
        // First time this GameObject is used from the pool — run full one-time setup.
        if (!_fullyInitialized)
            Init(startRoom);

        _schedule = schedule;
        DepartureWindowMinutes = Random.Range(10, 21);

        transform.position = spawnPos;
        if (nmAgent != null)
        {
            nmAgent.Warp(spawnPos);
            nmAgent.isStopped = false;
        }

        targetRoom = startRoom;
        _classEndHandled = false;
        _respawnAtMinute = -1;

        if (_renderers == null) _renderers = GetComponentsInChildren<Renderer>();
        if (_colliders == null) _colliders = GetComponentsInChildren<Collider>();
        foreach (var r in _renderers) r.enabled = true;
        foreach (var c in _colliders) c.enabled = true;

        _schedule.SetActivity(AgentActivity.Idle);

        // Important for REUSED pooled agents: ScheduleTick destroys itself once an
        // agent's day is Done (see ScheduleTick's Done case). Init()'s DeferredInit
        // only creates it once per GameObject lifetime, so a recycled agent on its
        // 2nd/3rd use would otherwise never tick again. SafeCreateStepper is a
        // no-op if it's already alive, so this is safe to call every time.
        SafeCreateStepper("ScheduleTick", ScheduleTick, 30, 1);

    }
    #endregion

    #region TimedStay
    int _stayMinutesRemaining = 0;

    void StartTimedStay(int simMinutes)
    {
        SafeDestroyStepper("StayInPlace");
        int nowMinute = _time.CurrentHour * 60 + _time.CurrentMinute;
        _stayEndMinute = nowMinute + simMinutes;
        SafeCreateStepper("StayInPlace", StayInPlace, 2, 1);
    }

    void StayInPlace()
    {
        if (!_stepperAlive_StayInPlace) return;
        _pendingRegistration.Remove("StayInPlace");
        int nowMinute = _time.CurrentHour * 60 + _time.CurrentMinute;
        if (nowMinute <= _stayEndMinute) return;
        SafeDestroyStepper("StayInPlace");
        switch (_schedule.CurrentActivity)
        {
            case AgentActivity.InClass:
            case AgentActivity.Wandering:
                PostClassDecision();
                break;
            case AgentActivity.InBathroom:
            case AgentActivity.InStudying:
            case AgentActivity.InOfficeHours:
            case AgentActivity.WaitingForClass:
                AfterActivityDecision();
                break;
            default:
                AfterActivityDecision();
                break;
        }

    }
        #endregion

    #region Bathroom
        void GoToBathroom()
    {
        Debug.Log($"[{name}] GoToBathroom() called.\n{System.Environment.StackTrace}");
        GameObject node = nCont.GetClosestBathroomNode(transform.position);
        if (node == null) { AfterActivityDecision(); return; }
        _schedule.SetActivity(AgentActivity.GoingToBathroom);
        _bathroomChance = bathroomChanceDefault; // reset now that they're going
        NavigateDirect(node);
    }

    // Called at every decision point the agent DIDN'T go to the bathroom,
    // so the chance climbs the longer they hold off.
    void IncrementBathroomChance()
    {
        _bathroomChance = Mathf.Clamp01(_bathroomChance + bathroomChanceIncrement);
        Debug.Log($"[{name}] bathroom chance incremented to {_bathroomChance:F2}");
    }
    #endregion Bathroom

    #region Navigation
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
    void CheckDistToTarget()
    {
        if (!_stepperAlive_CheckDist) return;
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
                int minutesLeftInClass = Mathf.Max(1, _schedule.EndMinute - (_time.CurrentHour * 60 + _time.CurrentMinute));
                StartTimedStay(minutesLeftInClass);
                break;

            case AgentActivity.GoingToBathroom:
                _schedule.SetActivity(AgentActivity.InBathroom);
                int randomTime = Random.Range(2, 6);
                Debug.Log($"[{name}] is staying in the bathroom for " + randomTime + " minutes"); // this only fires once, so that means the problem is in startTimedstay
                StartTimedStay(randomTime);

                
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
                bool hasMore = _schedule.NextClassOnDay(_time.GetCurrentDayOfWeek()).HasValue;
                DeactivateAgent(hasMoreClasses: hasMore);
                break;
            case AgentActivity.GoingToOfficeHours:
                _schedule.SetActivity(AgentActivity.InOfficeHours);
                StartTimedStay(Random.Range(10, 30));
                break;

            default:
                break;
        }
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
    #endregion

    #region Movement
    void Move()
    {
        _pendingRegistration.Remove("Move");
        if (_isRiding) return;
        nmAgent.velocity = Vector3.zero;
        nmAgent.nextPosition = transform.position + nmAgent.desiredVelocity * 0.03f;
        transform.LookAt(nmAgent.nextPosition, Vector3.up);
        transform.position = nmAgent.nextPosition;
    }


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
    #endregion

    #region Elevator
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
    #endregion







}