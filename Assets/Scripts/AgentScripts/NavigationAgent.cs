using UnityEngine;
using ABMU.Core;
using UnityEngine.AI;


// NavigationAgent is a state machine connected to every NavMesh Agent.
// Rather than using Unity's update function, it uses the ABMU's scheduler AKA steppers
// Agent basically loops like this: Read currentActivity - > Decide what to do - > Change Activity - > Move - > Arrive at place and decide again

// A lot of methods in this script work like this : (Given current activity, decide next activity and where to move the agent.)
// PostClassDecision() and AfterActivityDecision() work like this.
public class NavigationAgent : AbstractAgent
{
    [Header("Diagnostics")]
    [SerializeField] private bool verboseLogging = false;
    #region References
    // ── Scene references ──────────────────────────────────────────────────────
    NavigationController nCont;
    NavMeshAgent nmAgent;
    TimeManager _time;
    public TimeManager SimulationTime => _time;
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
    private ElevatorCallStation _waitingCallStation;
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
    // ── Stepper liveness tracking ─────────────────────────────────────────────
    private bool _stepperAlive_DeferredInit = false;
    private bool _stepperAlive_ScheduleTick = false;
    private bool _stepperAlive_CheckDist = false;
    private bool _stepperAlive_Move = false;
    private bool _stepperAlive_StayInPlace = false;


    public event System.Action<NavigationAgent> OnFinishedForDay;


    // ── Animation ─────────────────────────────────────────────────────────────
    private bool _isMovingAnim = false;
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

        animator.SetBool("isIdle", !isMoving);
        animator.SetBool("stopWalking", false);
        animator.SetBool("startWalking", false);
        animator.SetBool("Walking", isMoving);
    }

    //Sets the schedule for the agent
    public void SetSchedule(AgentSchedule schedule)
    {
        _schedule = schedule;
        DepartureWindowMinutes = Random.Range(10, 21);
    }
    void SetNMAgentProperties()
    {
        // Let the NavMesh simulation own movement and avoidance. Writing velocity
        // every tick overrides that simulation and stalls newly assigned paths.
        nmAgent.updatePosition = true;
        nmAgent.updateRotation = false;
        nmAgent.acceleration = 999f;

        nmAgent.stoppingDistance = Mathf.Max(0.5f, nCont.distToTargetThreshold * 0.6f);
    }

    private bool _fullyInitialized = false;

    public void Init(GameObject startRoom)
    {
        if (_fullyInitialized) return;
        _fullyInitialized = true;

        base.Init();
        nCont = controller as NavigationController;
        if (nCont == null) nCont = FindFirstObjectByType<NavigationController>();
        nmAgent = GetComponent<NavMeshAgent>();
        _time = nCont.SimulationTime;
        _callStations = nCont.CallStations;
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
        if (animator != null) animator.enabled = true;
        transform.position = spawnPosition;
        nmAgent.nextPosition = spawnPosition;

        _schedule.SetActiveClass(classEntry);
        _schedule.SetActivity(AgentActivity.GoingToClass);
        _classEndHandled = false;


        NavigateTo(classEntry.ClassroomNode);
    }

    // ABMU gives every Agent access to a method called CreateStepper()
    // A stepper basically calls a method every N ticks, at X priority related to other methods
    // By using a stepper instead of Unity's update function, we can opt in/opt out of methods. This means that it is more cost effective. 
    // The reason why I'm using this 'SafeCreateStepper' function is because creating/destroying steppers isn't instant. 
  
    void SafeCreateStepper(string stepperName, ABMU.Utilities.Del method, int step, int priority)
    {
        switch (stepperName)
        {
            // The stepperAlive boolean is there to ensure that the script does not accidentally create 2 stepper functions
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
        CreateStepper(method, step, priority);
    }

    void SafeDestroyStepper(string stepperName)
    {
        // The scheduler queues removals, including steppers awaiting registration.
        // Always cancel the actual stepper so a subsequent create cannot revive it.

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

    // DeferredInit is an initilization function that runs once and waits until all variables are non-null
    // The reason why DeferredInit is waiting is because it needs to wait until the schedule is initialized.
    void DeferredInit()
    {
        if (!_stepperAlive_DeferredInit) return;
        if (nCont == null || _time == null || _schedule == null) return;
        if (_scheduleInitialized) { SafeDestroyStepper("DeferredInit"); return; }

        _scheduleInitialized = true;
        SafeDestroyStepper("DeferredInit");
        SafeCreateStepper("ScheduleTick", ScheduleTick, 30, 1);
    }
    #endregion

    #region AgentDecisions

    // ScheduleTick is the main brain of the agent. It runs much slower than other scripts (every 30 ticks) 
    // The function looks at the currentActivity state and decides what the agent should do next.
    void ScheduleTick()
    {
        if (!_stepperAlive_ScheduleTick) return;
        if (_schedule == null || _time == null || nCont == null || !_time.IsRunning) return;
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
                    if (animator != null) animator.enabled = true;
                    emission.enabled = true;
                    _schedule.SetActiveClass(toHead.Value);
                    _schedule.SetActivity(AgentActivity.GoingToClass);
                    _classEndHandled = false;
                    NavigateTo(toHead.Value.ClassroomNode);

                }
                break;
            case AgentActivity.InClass:
                if (simMinute >= _schedule.EndMinute && !_classEndHandled)
                {
                    _classEndHandled = true;
                    _schedule.SetActivity(AgentActivity.Wandering);
                    PostClassDecision();
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
        if (verboseLogging) Debug.Log($"[{name}] [TIMING] PostClassDecision start at real-t={Time.realtimeSinceStartup:F2}");
        int simMinute = _time.CurrentHour * 60 + _time.CurrentMinute;
        int gapMinutes = _schedule.MinutesUntilNextClassOnDay(simMinute, _time.GetCurrentDayOfWeek());

        if (Random.value < _bathroomChance)
        {
            GoToBathroom();
            return;
        }
        IncrementBathroomChance();
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
        if (verboseLogging) Debug.Log($"[{name}] [TIMING] GoStudy → {node.name} at real-t={Time.realtimeSinceStartup:F2}");
        _schedule.SetActivity(AgentActivity.GoingToStudying);
        NavigateDirect(node);
    }
    void LeaveBuilding()
    {
        var today = _time.GetCurrentDayOfWeek();
        var nextClass = _schedule.NextClassOnDay(today);

        GameObject exitNode = nCont.GetRandomExitNode();
        if (exitNode == null) { DeactivateAgent(hasMoreClasses: false); return; }

        _respawnAtMinute = nextClass.HasValue ? nextClass.Value.Section.startMinute - 15 : -1;

        if (verboseLogging) Debug.Log($"[{name}] [TIMING] LeaveBuilding → {exitNode.name} at real-t={Time.realtimeSinceStartup:F2}");
        _schedule.SetActivity(AgentActivity.GoingToExit);
        Vector3 scatterTarget = nCont.GetScatteredPointNearNode(exitNode);
        SetScatteredTarget(exitNode, scatterTarget);
    }
    #endregion

    #region DeactivateAgents

    void DeactivateAgent(bool hasMoreClasses)
    {
        ResetNavigation();

        _schedule.SetActivity(AgentActivity.OffCampus);


        foreach (var r in _renderers) r.enabled = false;
        foreach (var c in _colliders) c.enabled = false;
        if (animator != null) animator.enabled = false;

        // Remove invisible off-campus bodies from the avoidance simulation.
        nmAgent.isStopped = true;
        nmAgent.velocity = Vector3.zero;
        nmAgent.enabled = false; // Invisible pooled bodies must not block the entrance.

        if (!hasMoreClasses)
        {
            _schedule.SetActivity(AgentActivity.Done);
            OnFinishedForDay?.Invoke(this);
        }
    }

    void RespawnAgent()
    {
        _respawnAtMinute = -1;
        if (animator != null) animator.enabled = true;

        foreach (var r in _renderers) r.enabled = true;
        foreach (var c in _colliders) c.enabled = true;

        if (!ParticleSimulationSettings.Enabled && mouth != null)
            mouth.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);

        GameObject spawnNode = nCont.GetRandomExitNode();
        nmAgent.enabled = true;
        if (spawnNode != null)
        {
            Vector3 pos = nCont.GetScatteredPointNearNode(spawnNode);
            transform.position = pos;
            nmAgent.Warp(pos);
        }

        nmAgent.isStopped = false;
        _schedule.SetActivity(AgentActivity.Idle);

    }

    private void OnDestroy()
    {
        ResetNavigation();
        if (_fullyInitialized)
        {
            SafeDestroyStepper("ScheduleTick");
            SafeDestroyStepper("DeferredInit");
        }
        if (_time != null) _time.OnSimSpeedChanged -= HandleSimSpeedChanged;
    }

    private void OnDisable()
    {
        if (_fullyInitialized) ResetNavigation();
    }

    private void ResetNavigation()
    {
        if (!_fullyInitialized) return;
        SafeDestroyStepper("CheckDistToTarget");
        SafeDestroyStepper("Move");
        SafeDestroyStepper("StayInPlace");
        ReleaseElevatorReservation();
        if (_elevator != null) _elevator.ExitAgent(this);
        _elevator = null;
        _isRiding = false;
        isNearTarget = false;
        if (nCont != null) nCont.ReleaseRoomTarget(this);
        if (nmAgent != null && nmAgent.isOnNavMesh)
        {
            nmAgent.isStopped = true;
            nmAgent.ResetPath();
        }
        if (nmAgent != null) nmAgent.updatePosition = true;
        SetMovingAnimState(false);
    }
    #endregion

    #region Schedule
    public void AssignNewSchedule(AgentSchedule schedule, GameObject startRoom, Vector3 spawnPos)
    {
        // First time this GameObject is used from the pool — run full one-time setup.
        if (!_fullyInitialized)
            Init(startRoom);

        ResetNavigation();

        _schedule = schedule;
        DepartureWindowMinutes = Random.Range(10, 21);

        transform.position = spawnPos;
        if (nmAgent != null)
        {
            nmAgent.enabled = true;
            nmAgent.Warp(spawnPos);
            nmAgent.isStopped = true; // No path/avoidance work until this student's departure time.
            nmAgent.enabled = false;
        }

        targetRoom = startRoom;
        _classEndHandled = false;
        _respawnAtMinute = -1;
        _bathroomChance = bathroomChanceDefault;

        if (_renderers == null) _renderers = GetComponentsInChildren<Renderer>();
        if (_colliders == null) _colliders = GetComponentsInChildren<Collider>();
        foreach (var r in _renderers) r.enabled = true;
        foreach (var c in _colliders) c.enabled = true;
        if (meshRenderer != null) meshRenderer.enabled = false;
        if (animator != null) animator.enabled = false;

        if (!ParticleSimulationSettings.Enabled && mouth != null)
            mouth.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);

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
    private double _stayEndSimulationSeconds;

    void StartTimedStay(int simMinutes)
    {
        SafeDestroyStepper("StayInPlace");
        _stayEndSimulationSeconds = _time.ElapsedSimulationSeconds + simMinutes * 60.0;
        SafeCreateStepper("StayInPlace", StayInPlace, 2, 1);
    }

    // Triggers any time the agent needs to wait X amount of time. Does exactly what it says then triggers another decision. 
    void StayInPlace()
    {
        if (!_stepperAlive_StayInPlace) return;
        if (_time == null || !_time.IsRunning || _time.ElapsedSimulationSeconds < _stayEndSimulationSeconds) return;
        SafeDestroyStepper("StayInPlace");
        switch (_schedule.CurrentActivity)
        {
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
        if (verboseLogging) Debug.Log($"[{name}] GoToBathroom() called.\n{System.Environment.StackTrace}");
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
        if (verboseLogging) Debug.Log($"[{name}] bathroom chance incremented to {_bathroomChance:F2}");
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
    // CheckDistToTarget checks the vector3 distance to the current target every two ticks. 
    // Once close enough, the function checks the state machine to see what it should do next. For example, if the agent is studying then this function ensures the agent studys for x amount of time.
    void CheckDistToTarget()
    {
        if (!_stepperAlive_CheckDist) return;

        // This is where the function checks how close it is to a target. If the agent is still far away then it returns and the agent keeps walking.
        if (nmAgent == null || !nmAgent.isOnNavMesh || nmAgent.pathPending) return;
        Vector3 offset = transform.position - target;
        float d = new Vector2(offset.x, offset.z).magnitude;
        bool headingToClass = _pendingCallStation == null && _schedule.CurrentActivity == AgentActivity.GoingToClass;
        float arrivalDistance = headingToClass ? nCont.classroomArrivalDistance :
            (_pendingCallStation != null ? 0.4f : _schedule.CurrentActivity == AgentActivity.GoingToExit ? nCont.distToTargetThreshold : 0.5f);
        if (Mathf.Abs(offset.y) > 1.2f || d > arrivalDistance ||
            nmAgent.pathStatus != NavMeshPathStatus.PathComplete || nmAgent.remainingDistance > arrivalDistance)
        { isNearTarget = false; return; }
        if (verboseLogging) Debug.Log($"[{name}] [TIMING] CheckDistToTarget arrived at real-t={Time.realtimeSinceStartup:F2}, dist={d:F2}");

        isNearTarget = true;
        nmAgent.isStopped = true;
        nmAgent.ResetPath();
        SetMovingAnimState(false);
        SafeDestroyStepper("CheckDistToTarget");
        SafeDestroyStepper("Move");

        if (_pendingCallStation != null)
        {
            ElevatorCallStation station = _pendingCallStation;
            int destFloor = _pendingDestFloor;
            _pendingCallStation = null;
            _waitingCallStation = station;
            if (!station.TryRegisterWaitingAgent(this, destFloor))
            {
                ReleaseElevatorReservation();
                TakeStairs(targetRoom);
            }
            return;
        }

        switch (_schedule.CurrentActivity)
        {
            case AgentActivity.GoingToClass:
                _schedule.SetActivity(AgentActivity.InClass);
                break;

            case AgentActivity.GoingToBathroom:
                _schedule.SetActivity(AgentActivity.InBathroom);
                int randomTime = Random.Range(2, 6);
                if (verboseLogging) Debug.Log($"[{name}] is staying in the bathroom for " + randomTime + " minutes");
                StartTimedStay(randomTime);

                
                break;

            case AgentActivity.GoingToStudying:
                _schedule.SetActivity(AgentActivity.InStudying);

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

    public void SetScatteredTarget(GameObject node, Vector3 scatteredPoint)
    {
        PrepareToNavigate();
        SnapToNavMesh();
        targetRoom = node;
        target = scatteredPoint;
        nmAgent.stoppingDistance = Mathf.Max(0.5f, nCont.distToTargetThreshold * 0.6f);

        SafeDestroyStepper("CheckDistToTarget");
        SafeDestroyStepper("Move");

        StartNavigationPath();
        SetMovingAnimState(false); // Walking starts when the body actually moves.
        SafeCreateStepper("CheckDistToTarget", CheckDistToTarget, 2, 100);
        SafeCreateStepper("Move", Move, 1, 105);
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
        PrepareToNavigate();
        SnapToNavMesh();
        targetRoom = room;
        bool headingToClass = _schedule != null && _schedule.CurrentActivity == AgentActivity.GoingToClass;
        target = nCont.ReserveRoomTarget(this, room, headingToClass);
        nmAgent.stoppingDistance = headingToClass ? nCont.classroomArrivalDistance * 0.5f : 0.25f;

        if (verboseLogging)
        {
            int nearbyAgents = 0;
            foreach (var other in FindObjectsByType<NavigationAgent>(FindObjectsSortMode.None))
                if (other != this && (other.transform.position - transform.position).sqrMagnitude < 25f)
                    nearbyAgents++;
            Debug.Log($"[{name}] SetTarget -> {room.name}, nearbyAgents(<5m)={nearbyAgents}, target={target}");
        }


        SafeDestroyStepper("CheckDistToTarget");
        SafeDestroyStepper("Move");

        StartNavigationPath();
        SetMovingAnimState(false);
        SafeCreateStepper("CheckDistToTarget", CheckDistToTarget, 2, 100);
        SafeCreateStepper("Move", Move, 1, 105);
   
    }
    #endregion

    #region Movement

    private Vector3 _lastMovementPosition;

    private void StartNavigationPath()
    {
        // Resolve departures now instead of placing a whole class into Unity's
        // asynchronous path queue, which can leave a visible multi-second wait.
        var path = new NavMeshPath();
        nmAgent.isStopped = false;
        if (nmAgent.CalculatePath(target, path) && path.status == NavMeshPathStatus.PathComplete)
            nmAgent.SetPath(path);
        else
            nmAgent.SetDestination(target);
    }

    private void PrepareToNavigate()
    {
        SafeDestroyStepper("StayInPlace");
        ReleaseElevatorReservation();
        nCont.ReleaseRoomTarget(this);
        isNearTarget = false;
        _lastMovementPosition = transform.position;
    }

    // Navigation owns position; this step only turns/animates actual movement.
    void Move()
    {
        if (!_stepperAlive_Move) return;
        if (_isRiding) return;

        Vector3 delta = transform.position - _lastMovementPosition;
        _lastMovementPosition = transform.position;
        delta.y = 0;
        bool moving = delta.sqrMagnitude > 0.000001f;
        SetMovingAnimState(moving);
        if (moving)
            transform.rotation = Quaternion.RotateTowards(transform.rotation, Quaternion.LookRotation(delta), 540f * Time.deltaTime);
    }


    private void SnapToNavMesh()
    {
        nmAgent.enabled = true;
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
        PrepareToNavigate();
        SnapToNavMesh();
        targetRoom = destinationRoom;
        if (!station.TryReserveAgent(this, destFloor, out Vector3 queuePosition))
        {
            TakeStairs(destinationRoom);
            return;
        }
        TargetFloor = destFloor;
        _pendingCallStation = station;
        _pendingDestFloor = destFloor;
        target = queuePosition;
        nmAgent.stoppingDistance = 0.2f;
        StartNavigationPath();
        SetMovingAnimState(false);


        SafeDestroyStepper("CheckDistToTarget");
        SafeDestroyStepper("Move");
        SafeCreateStepper("CheckDistToTarget", CheckDistToTarget, 1, 100);
        SafeCreateStepper("Move", Move, 1, 105);
    }

    private void TakeStairs(GameObject room) => SetTarget(room);

    public bool HasElevatorReservation(ElevatorCallStation station) =>
        isActiveAndEnabled && (_pendingCallStation == station || _waitingCallStation == station);

    private void ReleaseElevatorReservation()
    {
        if (_pendingCallStation != null) _pendingCallStation.RemoveAgent(this);
        if (_waitingCallStation != null) _waitingCallStation.RemoveAgent(this);
        _pendingCallStation = null;
        _waitingCallStation = null;
    }

    public void CancelElevatorWait(ElevatorCallStation station)
    {
        if (!HasElevatorReservation(station)) return;
        ReleaseElevatorReservation();
        TakeStairs(targetRoom);
    }

    public void BoardElevator(ElevatorController elevator)
    {
        ReleaseElevatorReservation();
        _elevator = elevator;
        _isRiding = true;
        nmAgent.isStopped = true;
        nmAgent.ResetPath();
        SetMovingAnimState(false);
        elevator.BoardAgent(this);
        nmAgent.enabled = false;
    }

    public void ExitElevator(int floorIndex)
    {
        Vector3 exitPos = _elevator.GetCallStationPosition(floorIndex);
        _elevator.ExitAgent(this);
        _elevator = null;
        _isRiding = false;
        transform.position = exitPos;
        nmAgent.enabled = true;
        nmAgent.Warp(exitPos);
        nmAgent.updatePosition = true;
        nmAgent.isStopped = false;
        SetTarget(targetRoom);
    }

    public void TeleportWithElevator(Vector3 slotPosition)
    {
        // Riders are carried outside the NavMesh; do not let native navigation
        // pull them back to the landing while the cage travels.
        nmAgent.updatePosition = false;
        transform.position = slotPosition;
    }
    private ElevatorCallStation GetBestStationForFloor(int floor)
    {
        ElevatorCallStation best = null;
        int bestLoad = int.MaxValue;
        foreach (var s in _callStations)
        {
            if (s.floorIndex != floor) continue;
            if (!s.IsElevatorViable()) continue;
            int load = s.ReservedCount + s.elevatorController.RiderCount;
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
        if (verboseLogging) Debug.Log($"[GetFloorOfRoom] room={room.name} roomY={roomY:F2} → matched station={bestStation?.name}, " +
                  $"stationY={bestStation?.transform.position.y:F2}, floorIndex={best}, dist={bestDist:F2}");
        return best;
    }
    #endregion







}
