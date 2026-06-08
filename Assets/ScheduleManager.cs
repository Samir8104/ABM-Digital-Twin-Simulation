using System.Collections.Generic;
using UnityEngine;


/// Wire up in the Inspector:
///   • agentPrefab     – your NavigationAgent prefab
///   • courseData      – the CourseData ScriptableObject (in Resources/CourseData)
///   • spawnRoot       – an empty Transform used as the spawn origin
///   • spawnRadius     – radius around spawnRoot to scatter new agents
/// </summary>
public class ScheduleManager : MonoBehaviour
{
    // ?? Inspector ?????????????????????????????????????????????????????????????

    [Header("References")]
    public GameObject agentPrefab;
    public CourseData courseData;          // Assign via Inspector or auto-loaded from Resources
    public Transform spawnRoot;           // Where agents materialise before walking to class
    public float spawnRadius = 3f;
    private bool _spawned = false;


    [Header("Cap (0 = unlimited)")]
    [Tooltip("Hard cap on total agents spawned. Set to 0 to spawn every enrolled student.")]
    public int maxTotalAgents = 0;

    // ?? Internal ??????????????????????????????????????????????????????????????

    private TimeManager _time;

    // All room GameObjects in the scene, keyed by their name (or a child name) that
    // contains the room number string.  Populated once in Start().
    private readonly Dictionary<string, GameObject> _roomByNumber = new();

    // Tracks spawned agents so we can cap and inspect them.
    private readonly List<NavigationAgent> _allAgents = new();

    // ?? Unity ?????????????????????????????????????????????????????????????????

    private void Awake()
    {
        if (courseData == null)
            courseData = Resources.Load<CourseData>("CourseData");

        if (courseData == null)
        {
            Debug.LogError("[ScheduleManager] CourseData asset not found! " +
                           "Run Assets ? Simulation ? Import Course CSV first.");
            enabled = false;
            return;
        }
    }

    private void Update()
    {
        // Wait one frame for ABMU's AbstractController.Init() to fully complete.
        if (!_spawned)
        {
            _spawned = true;
            IndexSceneRooms();
            SpawnAllAgents();
        }
    }


    private void Start()
    {
        _time = FindObjectOfType<TimeManager>();
    }
    // ?? Room indexing ?????????????????????????????????????????????????????????

    /// <summary>
    /// Walks every GameObject in the scene and records those whose name
    /// contains only digits (or ends with digits) as room-number nodes.
    /// Adjust the matching logic here if your naming convention differs.
    /// </summary>
    private void IndexSceneRooms()
    {
        // FindObjectsOfType<GameObject>() is slow at runtime — acceptable for
        // one-time setup.  For large scenes replace with a tagged query.
        foreach (var go in FindObjectsOfType<GameObject>())
        {
            string n = go.name.Trim();

            // Accept names that are purely numeric: "116", "401", etc.
            if (System.Text.RegularExpressions.Regex.IsMatch(n, @"^\d+$"))
            {
                if (!_roomByNumber.ContainsKey(n))
                    _roomByNumber[n] = go;
            }
        }

        Debug.Log($"[ScheduleManager] Indexed {_roomByNumber.Count} room nodes.");
    }

    // ?? Spawning ??????????????????????????????????????????????????????????????

    // In ScheduleManager.cs, replace SpawnAllAgents() with this version:

    private void SpawnAllAgents()
    {
        if (agentPrefab == null) { Debug.LogError("[ScheduleManager] agentPrefab is null!"); return; }
        if (spawnRoot == null) { Debug.LogError("[ScheduleManager] spawnRoot is null!"); return; }

        // Print every section loaded from the asset
        Debug.Log($"[ScheduleManager] CourseData has {courseData.sections.Count} sections.");

        // Print every room node that was indexed
        foreach (var kvp in _roomByNumber)
            Debug.Log($"[ScheduleManager] Indexed room: '{kvp.Key}' -> {kvp.Value.name}");

        int total = 0;

        foreach (CourseSection section in courseData.sections)
        {
            if (!_roomByNumber.TryGetValue(section.roomNumber, out GameObject classroomNode))
            {
                Debug.LogWarning($"[ScheduleManager] No node found for room '{section.roomNumber}' — skipping.");
                continue;
            }

            for (int i = 0; i < section.totalEnrolled; i++)
            {
                if (maxTotalAgents > 0 && total >= maxTotalAgents)
                {
                    Debug.Log($"[ScheduleManager] Hit maxTotalAgents cap of {maxTotalAgents}.");
                    return;
                }

                Vector3 pos = spawnRoot.position + Random.insideUnitSphere * spawnRadius;
                pos.y = spawnRoot.position.y;

                GameObject go = Instantiate(agentPrefab, pos, Quaternion.identity);
                go.name = $"Agent_{total:0000}";

                var agent = go.GetComponent<NavigationAgent>();
                if (agent == null)
                {
                    Debug.LogError("[ScheduleManager] agentPrefab has no NavigationAgent component!");
                    Destroy(go);
                    continue;
                }

                var schedule = new AgentSchedule(section, classroomNode);
                agent.SetSchedule(schedule);
                agent.Init(classroomNode);

                _allAgents.Add(agent);
                total++;
            }
        }

        Debug.Log($"[ScheduleManager] Spawned {total} agents across {courseData.sections.Count} sections.");
    }

    public IReadOnlyList<NavigationAgent> AllAgents => _allAgents;
}