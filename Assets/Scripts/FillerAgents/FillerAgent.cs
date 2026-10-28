using UnityEngine;
using ABMU.Core;
using UnityEngine.AI;

/// <summary>
/// One-shot, single-purpose agent used only to pad class attendance up to a
/// section's enrolled count. No bathroom, studying, office hours, or elevator
/// use — spawns, walks to exactly one class, waits it out, leaves, and
/// destroys itself. Never reused/pooled.
/// </summary>
public class FillerAgent : AbstractAgent
{
    private enum State { WaitingToHead, GoingToClass, InClass, Leaving }

    NavMeshAgent nmAgent;
    NavigationController nCont;
    TimeManager _time;
    ElevatorCallStation[] _callStations;
    public Animator animator;

    private CourseSection _section;
    private GameObject _classroom;
    private Vector3 _target;
    private State _state;
    private int _stayEndMinute;
    private int _headOutMinute;
    private float _baseSpeed = -1f;

    private bool _tickAlive, _tickPending;
    private bool _moveAlive, _movePending;

    public event System.Action<FillerAgent> OnDespawned;

    public void Setup(CourseSection section, GameObject classroom, Vector3 spawnPos,
                       NavigationController controller, TimeManager time, ElevatorCallStation[] callStations)
    {
        _section = section;
        _classroom = classroom;

        base.Init(); // must run before any CreateStepper call
        nmAgent = GetComponent<NavMeshAgent>();
        nCont = controller;
        _time = time;
        _callStations = callStations;

        nmAgent.updatePosition = true;
        nmAgent.updateRotation = false;
        nmAgent.acceleration = 999f;

        // Real deceleration zone so the agent slows smoothly on approach
        // instead of steering at full speed almost to the exact point —
        // without this, direction flips erratically at short range and
        // the agent visibly spins in place.
        nmAgent.stoppingDistance = Mathf.Max(0.5f, nCont.distToTargetThreshold * 0.6f);

        transform.position = spawnPos;
        nmAgent.Warp(spawnPos);
        nmAgent.enabled = false;

        _baseSpeed = nmAgent.speed;
        if (_time != null)
        {
            _time.OnSimSpeedChanged += OnSimSpeedChanged;
            OnSimSpeedChanged(_time.GetAgentSpeedMultiplier());
        }

        _headOutMinute = section.startMinute - Random.Range(5, 16);
        _state = State.WaitingToHead;
        if (animator != null) animator.SetBool("isIdle", true);

        CreateTick();
    }

    void OnSimSpeedChanged(float mult)
    {
        if (nmAgent != null && _baseSpeed > 0f) nmAgent.speed = _baseSpeed * mult;
    }

    // ?? Stepper lifecycle (same same-tick-create/destroy guard used
    //    elsewhere in this project, to avoid the ABMU NullReferenceException
    //    when a stepper is created and destroyed within the same tick) ??????
    void CreateTick() { if (_tickAlive) return; _tickAlive = true; _tickPending = true; CreateStepper(Tick, 20, 1); }
    void DestroyTick()
    {
        if (_tickPending) { _tickPending = false; _tickAlive = false; return; }
        if (!_tickAlive) return;
        _tickAlive = false; DestroyStepper("Tick");
    }
    void CreateMove() { if (_moveAlive) return; _moveAlive = true; _movePending = true; CreateStepper(Move, 1, 105); }
    void DestroyMove()
    {
        if (_movePending) { _movePending = false; _moveAlive = false; return; }
        if (!_moveAlive) return;
        _moveAlive = false; DestroyStepper("Move");
    }

    // ?? State machine ?????????????????????????????????????????????????????
    void Tick()
    {
        if (!_tickAlive) return;
        _tickPending = false;
        if (_time == null || !_time.IsRunning) return;

        int simMinute = _time.CurrentHour * 60 + _time.CurrentMinute;

        switch (_state)
        {
            case State.WaitingToHead:
                if (simMinute >= _headOutMinute) HeadToClass();
                break;

            case State.GoingToClass:
                if (Arrived())
                {
                    _state = State.InClass;
                    StopMoving();
                    _stayEndMinute = _section.endMinute;
                }
                break;

            case State.InClass:
                if (simMinute >= _stayEndMinute) HeadToExit();
                break;

            case State.Leaving:
                if (Arrived())
                {
                    OnDespawned?.Invoke(this);
                    DestroyTick();
                    DestroyMove();
                    Destroy(gameObject);
                }
                break;
        }
    }

    bool Arrived() => _state == State.GoingToClass
        ? nCont.HasArrivedInClassroom(nmAgent, transform.position, _target)
        : Vector3.Distance(transform.position, _target) < nCont.distToTargetThreshold;

    void HeadToClass()
    {
        nmAgent.enabled = true;
        _state = State.GoingToClass;
        _target = nCont.GetClassroomTarget(_classroom);
        nmAgent.stoppingDistance = nCont.classroomArrivalDistance * 0.5f;

        int destFloor = GetFloorOfRoom(_classroom);
        int curFloor = GetFloorFromPosition(transform.position);

        if (destFloor >= 0 && curFloor >= 0 && destFloor != curFloor)
        {
            // No elevator/stairs simulation for fillers — relocate them to
            // the destination floor directly instead of letting them walk
            // straight across a floor gap with no collision to stop them.
            var station = System.Array.Find(_callStations, s => s.floorIndex == destFloor);
            Vector3 warpNear = station != null ? station.transform.position : _target;
            if (NavMesh.SamplePosition(warpNear, out NavMeshHit hit, 5f, NavMesh.AllAreas))
            {
                transform.position = hit.position;
                nmAgent.Warp(hit.position);
            }
        }

        StartMoving();
    }

    void HeadToExit()
    {
        GameObject exit = nCont.GetRandomExitNode();
        _target = exit != null ? nCont.GetScatteredPointNearNode(exit) : transform.position;

        int destFloor = exit != null ? GetFloorOfRoom(exit) : -1;
        int curFloor = GetFloorFromPosition(transform.position);

        if (destFloor >= 0 && curFloor >= 0 && destFloor != curFloor)
        {
            var station = System.Array.Find(_callStations, s => s.floorIndex == destFloor);
            Vector3 warpNear = station != null ? station.transform.position : _target;
            if (NavMesh.SamplePosition(warpNear, out NavMeshHit hit, 5f, NavMesh.AllAreas))
            {
                transform.position = hit.position;
                nmAgent.Warp(hit.position);
            }
        }

        _state = State.Leaving;
        nmAgent.stoppingDistance = Mathf.Max(0.5f, nCont.distToTargetThreshold * 0.6f);
        StartMoving();
    }

    void StartMoving()
    {
        SnapToNavMesh();
        nmAgent.isStopped = false;
        var path = new NavMeshPath();
        if (nmAgent.CalculatePath(_target, path) && path.status == NavMeshPathStatus.PathComplete)
            nmAgent.SetPath(path);
        else nmAgent.SetDestination(_target);
        _lastMovementPosition = transform.position;
        CreateMove();
    }

    void StopMoving()
    {
        nmAgent.isStopped = true;
        DestroyMove();
        if (animator != null) { animator.SetBool("Walking", false); animator.SetBool("isIdle", true); }
    }

    private Vector3 _lastMovementPosition;

    void Move()
    {
        if (!_moveAlive) return;
        _movePending = false;
        Vector3 delta = transform.position - _lastMovementPosition;
        _lastMovementPosition = transform.position;
        delta.y = 0f;
        bool moving = delta.sqrMagnitude > 0.000001f;
        if (animator != null) { animator.SetBool("Walking", moving); animator.SetBool("isIdle", !moving); }
        if (moving)
            transform.rotation = Quaternion.RotateTowards(transform.rotation, Quaternion.LookRotation(delta), 540f * Time.deltaTime);
    }
    void SnapToNavMesh()
    {
        nmAgent.enabled = true;
        if (nmAgent.isOnNavMesh) return;
        foreach (float r in new float[] { 2f, 5f, 10f })
            if (NavMesh.SamplePosition(transform.position, out NavMeshHit hit, r, NavMesh.AllAreas))
            { nmAgent.Warp(hit.position); transform.position = hit.position; return; }
    }

    private int GetFloorOfRoom(GameObject room)
    {
        if (room == null || _callStations == null) return -1;
        float roomY = room.transform.position.y;
        int best = -1; float bestDist = float.MaxValue;
        foreach (var s in _callStations)
        {
            float d = Mathf.Abs(s.transform.position.y - roomY);
            if (d < bestDist) { bestDist = d; best = s.floorIndex; }
        }
        return best;
    }

    private int GetFloorFromPosition(Vector3 pos)
    {
        if (_callStations == null) return -1;
        int best = -1; float bestDist = float.MaxValue;
        foreach (var s in _callStations)
        {
            float d = Mathf.Abs(s.transform.position.y - pos.y);
            if (d < bestDist) { bestDist = d; best = s.floorIndex; }
        }
        return best;
    }

    private void OnDestroy()
    {
        if (_time != null) _time.OnSimSpeedChanged -= OnSimSpeedChanged;
    }
}
