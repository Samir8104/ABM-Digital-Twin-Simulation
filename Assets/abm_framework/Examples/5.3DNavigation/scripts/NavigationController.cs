using ABMU;
using ABMU.Core;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

public class NavigationController : AbstractController
{
    [Header("Simulation Parameters")]
    public List<GameObject> rooms;
    public float heatmapUpperBound = 0.15f;
    public Gradient heatmapGradient;

    [Header("Agent Parameters")]
    public float distToTargetThreshold = 2f;
    public LayerMask agentLm;
    public GameObject agentPrefab; // Still needed for GetRandomPointInRoom's baseOffset lookup

    [Header("Room Selection Weights")]
    public float lowWeight = 0.1f;
    public float midWeight = 0.35f;
    public float highWeight = 0.55f;

    public override void Init()
    {
        base.Init();
        rooms = GetAllRooms();
        // No agent spawning here anymore — ScheduleManager.Start() handles it.
    }

    public override void Step()
    {
        PauseAtFrame();
        base.Step();
    }



    // ── Room selection (unchanged) ────────────────────────────────────────────

    public GameObject GetRandomRoom()
    {
        float totalWeight = 0f;
        foreach (var room in rooms)
            totalWeight += GetWeightForRoom(room);

        float roll = Random.Range(0f, totalWeight);
        float cumulative = 0f;

        foreach (var room in rooms)
        {
            cumulative += GetWeightForRoom(room);
            if (roll <= cumulative) return room;
        }
        return rooms[rooms.Count - 1];
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


    public GameObject GetRoomByNumber(string roomNumber)
    {
        foreach (var room in rooms)
            if (room.name.Trim() == roomNumber) return room;
        return null;
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