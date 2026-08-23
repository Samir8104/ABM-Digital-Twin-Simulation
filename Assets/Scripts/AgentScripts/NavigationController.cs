using ABMU;
using ABMU.Core;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

// NavigationController finds rooms by numbers and ensures agents dont all go to the same point in a room.
// Basically it always finds the nearest point an agent is looking for. So if an agent is trying to exit, then this script returns the closest exit.
public class NavigationController : AbstractController
{
    [Header("Simulation Parameters")]
    public List<GameObject> rooms;
    public float heatmapUpperBound = 0.15f;
    public Gradient heatmapGradient;


    [Header("Agent Parameters")]
    public float distToTargetThreshold = 2f;
    public LayerMask agentLm;
    public GameObject agentPrefab;
    public int classExitLingerMin = 1;
    public int classExitLingerMax = 5;

    [Header("Room Selection Weights")]
    public float lowWeight = 0.1f;
    public float midWeight = 0.35f;
    public float highWeight = 0.55f;

    [Header("Elevator Parameters")]
    [Tooltip("If the best available elevator at a floor already has this many people waiting/riding, agents treat it as busy and take the stairs instead of queuing.")]
    public int maxElevatorLoadBeforeStairs = 4;

    [Header("Named Nodes")]
    public string bathroomNodeName = "Bathroom";
    public string studyingNodeName = "Studying";


    [Header("Exit Nodes")]
    [Tooltip("Tag all exit GameObjects with this tag. Agents heading to Done will pick one at random.")]
    public string exitNodeTag = "ExitNode";
    public float exitNodeScatterRadius = 2.5f;

    // Resolved once at Init.
    private GameObject _bathroomNode;
    private GameObject _studyingNode;
    private GameObject[] _exitNodes;
    private GameObject[] _officeHoursNodes;




    public override void Init()
    {
        base.Init();
        rooms = GetAllRooms();
        _bathroomNode = GameObject.Find(bathroomNodeName);
        _studyingNode = GameObject.Find(studyingNodeName);
        _exitNodes = GameObject.FindGameObjectsWithTag(exitNodeTag);
        _officeHoursNodes = GameObject.FindGameObjectsWithTag("OfficeHours");


        if (_exitNodes == null || _exitNodes.Length == 0)
            Debug.LogWarning("[NavigationController] No exit nodes found. " +
                             $"Make sure exit GameObjects are tagged '{exitNodeTag}'.");
    }

    public GameObject GetClosestBathroomNode(Vector3 fromPosition)
    {
        return GetClosestNodeWithTag("Bathroom", fromPosition);
    }


    public GameObject GetRandomStudyingNode()
    {
        GameObject[] nodes = GameObject.FindGameObjectsWithTag("Studying");
        if (nodes == null || nodes.Length == 0) return null;
        return nodes[Random.Range(0, nodes.Length)];
    }

    public GameObject GetRandomOfficeHoursNode()
    {
        if (_officeHoursNodes == null || _officeHoursNodes.Length == 0) return null;
        return _officeHoursNodes[Random.Range(0, _officeHoursNodes.Length)];
    }
    // Checks a radius around the node to see if there are any valid points. Returns a random valid point, fallsback on the nodes exact position :0
    public Vector3 GetScatteredPointNearNode(GameObject node)
    {
        if (node == null) return Vector3.zero;

        Vector3 origin = node.transform.position;
        foreach(float radius in new float[] {exitNodeScatterRadius, exitNodeScatterRadius * 2f, 5f })
        {
            Vector2 circle = Random.insideUnitCircle * radius;
            Vector3 candidate = origin + new Vector3(circle.x, 0f, circle.y);
            if (NavMesh.SamplePosition(candidate, out NavMeshHit hit, radius, NavMesh.AllAreas))
                return hit.position;
        }
        return origin;
    }


    private GameObject GetClosestNodeWithTag(string tag, Vector3 fromPosition)
    {
        GameObject[] nodes = GameObject.FindGameObjectsWithTag(tag);
        if (nodes == null || nodes.Length == 0) return null;

        GameObject closest = null;
        float bestDist = float.MaxValue;

        foreach (var node in nodes)
        {
            float dist = Vector3.Distance(fromPosition, node.transform.position);
            if (dist < bestDist) { bestDist = dist; closest = node; }
        }

        return closest;
    }

    public override void Step()
    {
        PauseAtFrame();
        base.Step();
    }

    // ── Exit node access ──────────────────────────────────────────────────────

    /// <summary>
    /// Returns a random exit node. Agents that need to leave spread across all
    /// available exits, reducing NavMesh congestion at a single point.
    /// Returns null if no exit nodes were found at Init (logs a warning once above).
    /// </summary>
    public GameObject GetRandomExitNode()
    {
        if (_exitNodes == null || _exitNodes.Length == 0) return null;
        return _exitNodes[Random.Range(0, _exitNodes.Length)];
    }

    // ── Room selection ────────────────────────────────────────────────────────

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