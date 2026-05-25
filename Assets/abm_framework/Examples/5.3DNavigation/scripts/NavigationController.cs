using ABMU;
using ABMU.Core;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

public class NavigationController : AbstractController
{
    [Header("Simulation Parameters")]
    public GameObject agentPrefab;
    public List<GameObject> rooms;
    public float heatmapUpperBound = 0.15f;
    public Gradient heatmapGradient;
    public int numAgents = 100;

    [Header("Agent Parameters")]
    public float distToTargetThreshold = 2f;
    public LayerMask agentLm;

    [Header("Room Selection Weights")]
    public float lowWeight = 0.1f;
    public float midWeight = 0.35f;
    public float highWeight = 0.55f;

    public override void Init()
    {
        base.Init();
        rooms = GetAllRooms();
        for (int i = 0; i < numAgents; i++)
        {
            GameObject agent = Instantiate(agentPrefab);
            NavMeshAgent nmAgent = agent.GetComponent<NavMeshAgent>();
            nmAgent.Warp(GetRandomPointInRoom(GetRandomRoom()));
            agent.transform.position = nmAgent.nextPosition;
            agent.GetComponent<NavigationAgent>().Init(GetRandomRoom());
        }
    }

    public override void Step()
    {
        PauseAtFrame();
        base.Step();
    }

    public GameObject GetRandomRoom()
    {
        float totalWeight = 0f;
        foreach (var room in rooms)
        {
            totalWeight += GetWeightForRoom(room);
        }

        float roll = Random.Range(0f, totalWeight);
        float cumulative = 0f;

        foreach (var room in rooms)
        {
            cumulative += GetWeightForRoom(room);
            if (roll <= cumulative)
                return room;
        }

        return rooms[rooms.Count - 1]; // fallback
    }

    float GetWeightForRoom(GameObject room)
    {
        var rp = room.GetComponent<RoomPriority>();
        if (rp == null) return midWeight; 
        return rp.priority switch
        {
            RoomPriorityLevel.Low => lowWeight,
            RoomPriorityLevel.Mid => midWeight,
            RoomPriorityLevel.High => highWeight,
            _ => midWeight
        };
    }

    public Vector3 GetRandomPointInRoom(GameObject room)
    {
        Bounds rb = room.GetComponent<Collider>().bounds;
        Vector3 cr = Utilities.RandomPointInBounds(rb);
        cr.y = rb.center.y;
        cr.y -= rb.extents.y;
        cr.y += agentPrefab.GetComponent<NavMeshAgent>().baseOffset;
        return cr;
    }

    public Vector3 GetCenterOfRoom(GameObject room)
    {
        Bounds rb = room.GetComponent<Collider>().bounds;
        Vector3 cr = rb.center;
        cr.y -= rb.extents.y;
        cr.y += agentPrefab.transform.localScale.y / 2f;
        return cr;
    }

    List<GameObject> GetAllRooms()
    {
        return new List<GameObject>(GameObject.FindGameObjectsWithTag("room"));
    }
}