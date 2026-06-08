using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class ScheduleManager : MonoBehaviour
{
    // ?? Inspector ??????????????????????????????????????????????????????????????

    [Header("References")]
    public GameObject agentPrefab;
    public CourseData courseData;
    public Transform  spawnRoot;
    public float      spawnRadius = 3f;

    [Header("Cap (0 = unlimited)")]
    [Tooltip("Hard cap on total agents spawned. Set to 0 to spawn every enrolled student.")]
    public int maxTotalAgents = 0;

    // ?? Internal ???????????????????????????????????????????????????????????????

    private TimeManager _time;
    private readonly Dictionary<string, GameObject> _roomByNumber = new();
    private readonly List<NavigationAgent>           _allAgents   = new();

    // ?? Unity ??????????????????????????????????????????????????????????????????

    private void Awake()
    {
        if (courseData == null)
            courseData = Resources.Load<CourseData>("CourseData");

        if (courseData == null)
        {
            Debug.LogError("[ScheduleManager] CourseData asset not found! " +
                           "Run Assets ? Simulation ? Import Course CSV first.");
            enabled = false;
        }
    }

    private void Start()
    {
        _time = FindObjectOfType<TimeManager>();
        IndexSceneRooms();

        // ?? Fix: use a coroutine so agents are spawned one per frame instead of
        // all in a single Update() call.  This prevents the instantiation burst
        // that hit NavMesh baking and ABMU stepper registration simultaneously.
        StartCoroutine(SpawnAllAgentsCoroutine());
    }

    // ?? Room indexing ??????????????????????????????????????????????????????????

    private void IndexSceneRooms()
    {
        foreach (var go in FindObjectsOfType<GameObject>())
        {
            string n = go.name.Trim();
            if (System.Text.RegularExpressions.Regex.IsMatch(n, @"^\d+$"))
            {
                if (!_roomByNumber.ContainsKey(n))
                    _roomByNumber[n] = go;
            }
        }
        Debug.Log($"[ScheduleManager] Indexed {_roomByNumber.Count} room nodes.");
    }

    // ?? Spawning ???????????????????????????????????????????????????????????????

    // ?? Fix: yield return null after each agent so Unity gets a frame to
    // process the new NavMeshAgent and ABMU stepper before the next one arrives.
    // On a 200-agent scene this costs ~200 frames (< 0.5 s at 60 fps) but
    // completely eliminates the spawn-time freeze.
    private IEnumerator SpawnAllAgentsCoroutine()
    {
        // Wait one frame for ABMU's AbstractController.Init() to finish.
        yield return null;

        if (agentPrefab == null) { Debug.LogError("[ScheduleManager] agentPrefab is null!");  yield break; }
        if (spawnRoot   == null) { Debug.LogError("[ScheduleManager] spawnRoot is null!");    yield break; }

        Debug.Log($"[ScheduleManager] CourseData has {courseData.sections.Count} sections.");
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
                    yield break;
                }

                Vector3 pos  = spawnRoot.position + Random.insideUnitSphere * spawnRadius;
                pos.y        = spawnRoot.position.y;

                GameObject go = Instantiate(agentPrefab, pos, Quaternion.identity);
                go.name       = $"Agent_{total:0000}";

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

                // ?? Fix: yield after every agent so NavMesh and ABMU can
                // process the registration before the next one is created.
                yield return null;
            }
        }

        Debug.Log($"[ScheduleManager] Spawned {total} agents across {courseData.sections.Count} sections.");
    }

    public IReadOnlyList<NavigationAgent> AllAgents => _allAgents;
}