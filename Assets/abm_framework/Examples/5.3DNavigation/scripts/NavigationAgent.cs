using UnityEngine;
using ABMU.Core;
using UnityEngine.AI;

public class NavigationAgent : AbstractAgent
{
    NavigationController nCont;
    NavMeshAgent nmAgent;
    Vector3 target;
    public GameObject targetRoom;
    public bool isNearTarget = false;
    int timeSpentSitting = 0;
    int stationaryDuration = -1;

    // Tweak these ranges in the inspector via NavigationController,
    // or just hardcode them here as constants.
    static readonly (int min, int max) LowWait = (30, 200);
    static readonly (int min, int max) MidWait = (50, 600);
    static readonly (int min, int max) HighWait = (200, 1500);

    public void Init(GameObject _targetRoom)
    {
        base.Init();
        nCont = GameObject.FindObjectOfType<NavigationController>();
        nmAgent = GetComponent<NavMeshAgent>();
        SetNMAgentProperties();
        SetupStationary();
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

    void CheckDistToTarget()
    {
        float d = Vector3.Distance(this.transform.position, target);
        if (d < nCont.distToTargetThreshold)
        {
            isNearTarget = true;
            nmAgent.isStopped = true;
            DestroyStepper("CheckDistToTarget");
            DestroyStepper("Move");
            SetupStationary();
        }
        else
        {
            isNearTarget = false;
        }
    }

    void Move()
    {
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

    void SetNewTarget()
    {
        SetTarget(nCont.GetRandomRoom());
    }

    void SetNMAgentProperties()
    {
        nmAgent.updatePosition = false;
        nmAgent.velocity = Vector3.zero;
        nmAgent.acceleration = 0f;
    }
}