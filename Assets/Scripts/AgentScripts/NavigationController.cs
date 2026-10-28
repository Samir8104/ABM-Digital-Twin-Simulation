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
    [Min(0.1f)] public float classroomArrivalDistance = 0.45f;
    [Tooltip("How far agents spread around the classroom node, while staying on connected walkable space.")]
    [Min(0f)] public float classroomScatterRadius = 2f;
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
    public float exitNodeScatterRadius = 5f;

    // Resolved once at Init.
    private GameObject _bathroomNode;
    private GameObject _studyingNode;
    private GameObject[] _exitNodes;
    private GameObject[] _officeHoursNodes;
    public TimeManager SimulationTime { get; private set; }
    public ElevatorCallStation[] CallStations { get; private set; }
    private readonly Dictionary<NavigationAgent, Vector3> _roomTargets = new();

    public void ReleaseRoomTarget(NavigationAgent agent) => _roomTargets.Remove(agent);

    public Vector3 ReserveRoomTarget(NavigationAgent agent, GameObject room, bool classroom)
    {
        _roomTargets.Remove(agent);
        Vector3 best = classroom ? GetClassroomTarget(room) : GetRandomPointInRoom(room);
        float bestClearance = -1f;
        var nav = agent.GetComponent<NavMeshAgent>();
        var path = new NavMeshPath();
        float spacing = nav.radius * 2f + 0.25f;
        for (int i = 0; i < 32; i++)
        {
            Vector3 point = classroom ? GetClassroomTarget(room) : GetRandomPointInRoom(room);
            if (!nav.CalculatePath(point, path) || path.status != NavMeshPathStatus.PathComplete) continue;
            float clearance = float.MaxValue;
            foreach (var other in _roomTargets.Values)
                clearance = Mathf.Min(clearance, (point - other).sqrMagnitude);
            if (clearance > bestClearance) { best = point; bestClearance = clearance; }
            if (clearance >= spacing * spacing) break;
        }
        _roomTargets[agent] = best;
        return best;
    }




    public override void Init()
    {
        base.Init();
        rooms = GetAllRooms();
        SimulationTime = FindFirstObjectByType<TimeManager>();
        CallStations = FindObjectsByType<ElevatorCallStation>(FindObjectsSortMode.None);
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
        foreach(float radius in new float[] {exitNodeScatterRadius, exitNodeScatterRadius * 4f, 5f })
        {
            Vector2 circle = Random.insideUnitCircle * radius;
            Vector3 candidate = origin + new Vector3(circle.x, 0f, circle.y);
            if (NavMesh.SamplePosition(candidate, out NavMeshHit hit, radius, NavMesh.AllAreas))
                return hit.position;
        }
        return origin;
    }


    public Vector3 GetClassroomTarget(GameObject room)
    {
        // Classroom objects are point markers, often with an empty MeshCollider.
        // Broad exit-node sampling can select the corridor across a wall.
        Vector3 origin = room.transform.position;
        if (!NavMesh.SamplePosition(origin, out var center, 0.75f, NavMesh.AllAreas))
        {
            Debug.LogWarning($"[NavigationController] Classroom {room.name} needs a node on its interior NavMesh.", room);
            return origin;
        }
        for (int i = 0; i < 12; i++)
        {
            Vector2 offset = Random.insideUnitCircle * classroomScatterRadius;
            Vector3 candidate = center.position + new Vector3(offset.x, 0, offset.y);
            if (NavMesh.SamplePosition(candidate, out var hit, 0.25f, NavMesh.AllAreas) &&
                Mathf.Abs(hit.position.y - center.position.y) < 0.3f &&
                !NavMesh.Raycast(center.position, hit.position, out _, NavMesh.AllAreas))
                return hit.position;
        }
        return center.position;
    }

    public bool HasArrivedInClassroom(NavMeshAgent agent, Vector3 position, Vector3 destination)
    {
        Vector3 delta = position - destination;
        return Mathf.Abs(delta.y) < 1.2f &&
            new Vector2(delta.x, delta.z).sqrMagnitude <= classroomArrivalDistance * classroomArrivalDistance &&
            !agent.pathPending && agent.pathStatus == NavMeshPathStatus.PathComplete &&
            agent.remainingDistance <= classroomArrivalDistance;
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
        // Many activity nodes are point markers or have empty mesh colliders.
        // Never feed arbitrary bounds points (walls/voids) to SetDestination.
        var collider = room.GetComponent<Collider>();
        if (collider == null || collider.bounds.size.sqrMagnitude < 0.01f)
            return GetClassroomTarget(room);
        Bounds bounds = collider.bounds;
        Vector3 origin = bounds.center;
        origin.y = bounds.min.y;
        if (!NavMesh.SamplePosition(origin, out var center, 1f, NavMesh.AllAreas))
            return GetClassroomTarget(room);
        for (int i = 0; i < 16; i++)
        {
            Vector3 candidate = new Vector3(Random.Range(bounds.min.x, bounds.max.x), center.position.y,
                Random.Range(bounds.min.z, bounds.max.z));
            if (NavMesh.SamplePosition(candidate, out var hit, 0.25f, NavMesh.AllAreas) &&
                Mathf.Abs(hit.position.y - center.position.y) < 0.3f &&
                !NavMesh.Raycast(center.position, hit.position, out _, NavMesh.AllAreas))
                return hit.position;
        }
        return center.position;
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
