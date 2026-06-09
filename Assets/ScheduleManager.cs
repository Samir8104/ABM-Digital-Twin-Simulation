using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Spawns agents, assigns their schedules, and wires up scene references.
///
/// Key changes from the previous version:
///   • Enrollment is randomised each run — a shuffled slot list means no two
///     runs produce the same agent-to-section mapping.
///   • Agents can hold multiple sections (multi-class days).  The manager
///     groups sections by distributing shuffled slots round-robin across agents.
/// </summary>
public class ScheduleManager : MonoBehaviour
{
    // ── Inspector ─────────────────────────────────────────────────────────────

    [Header("References")]
    public GameObject agentPrefab;
    public CourseData courseData;
    public Transform spawnRoot;
    public float spawnRadius = 3f;

    [Header("Exits / Entrances")]
    [Tooltip("Assign every door / exit node in the scene here. Agents heading " +
             "to Leaving will pick one at random.")]
    public List<GameObject> exitNodes = new();

    [Header("Cap (0 = unlimited)")]
    [Tooltip("Hard cap on total unique agents spawned. 0 = no cap.")]
    public int maxTotalAgents = 0;

    // ── Internal ──────────────────────────────────────────────────────────────

    private TimeManager _time;
    private readonly Dictionary<string, GameObject> _roomByNumber = new();
    private readonly List<NavigationAgent> _allAgents = new();

    // ── Unity ─────────────────────────────────────────────────────────────────

    private void Awake()
    {
        if (courseData == null)
            courseData = Resources.Load<CourseData>("CourseData");

        if (courseData == null)
        {
            Debug.LogError("[ScheduleManager] CourseData not found.");
            enabled = false;
        }
    }

    private void Start()
    {
        _time = FindObjectOfType<TimeManager>();
        IndexSceneRooms();
        StartCoroutine(SpawnAllAgentsCoroutine());
    }

    // ── Room indexing ─────────────────────────────────────────────────────────

    private void IndexSceneRooms()
    {
        foreach (var go in FindObjectsOfType<GameObject>())
        {
            string n = go.name.Trim();
            if (System.Text.RegularExpressions.Regex.IsMatch(n, @"^\d+$") &&
                !_roomByNumber.ContainsKey(n))
                _roomByNumber[n] = go;
        }
        Debug.Log($"[ScheduleManager] Indexed {_roomByNumber.Count} room nodes.");
    }

    // ── Spawning ──────────────────────────────────────────────────────────────

    private IEnumerator SpawnAllAgentsCoroutine()
    {
        yield return null; // Let ABMU finish its first Init pass.

        if (agentPrefab == null) { Debug.LogError("[ScheduleManager] agentPrefab is null!"); yield break; }
        if (spawnRoot == null) { Debug.LogError("[ScheduleManager] spawnRoot is null!"); yield break; }

        // ── Step 1: build a flat list of (section, classroomNode) slots ──────
        // Each slot represents one "student seat". We'll shuffle this list so
        // no two runs assign the same agents to the same sections.
        var slots = new List<(CourseSection section, GameObject classroomNode)>();

        foreach (var section in courseData.sections)
        {
            if (!_roomByNumber.TryGetValue(section.roomNumber, out GameObject node))
            {
                Debug.LogWarning($"[ScheduleManager] No room node for '{section.roomNumber}' — skipping.");
                continue;
            }
            for (int i = 0; i < section.totalEnrolled; i++)
                slots.Add((section, node));
        }

        // ── Step 2: Fisher-Yates shuffle ─────────────────────────────────────
        // This is the core fix for "same agents every run". By shuffling before
        // assignment, every run produces a different agent-to-section mapping.
        for (int i = slots.Count - 1; i > 0; i--)
        {
            int j = Random.Range(0, i + 1);
            (slots[i], slots[j]) = (slots[j], slots[i]);
        }

        // ── Step 3: determine how many unique agents to spawn ─────────────────
        int totalSlots = slots.Count;
        int agentCount = maxTotalAgents > 0
                         ? Mathf.Min(maxTotalAgents, totalSlots)
                         : totalSlots;

        // ── Step 4: distribute slots round-robin across agents ────────────────
        // Agent 0 gets slot 0, slot agentCount, slot 2*agentCount, etc.
        // Because the slot list is shuffled, each agent's classes come from
        // random positions in the original course list — different every run.
        // This naturally gives agents with lower indices more classes when
        // totalSlots > agentCount (realistic: some students have more classes).
        var agentSections = new List<List<(CourseSection, GameObject)>>(agentCount);
        for (int i = 0; i < agentCount; i++)
            agentSections.Add(new List<(CourseSection, GameObject)>());

        for (int slotIdx = 0; slotIdx < totalSlots; slotIdx++)
        {
            int agentIdx = slotIdx % agentCount;
            agentSections[agentIdx].Add(slots[slotIdx]);
        }

        // ── Step 5: spawn one agent per schedule ──────────────────────────────
        int total = 0;
        for (int i = 0; i < agentCount; i++)
        {
            var sections = agentSections[i];
            if (sections.Count == 0) continue;

            // Pick a random exit node as the spawn origin so agents enter from
            // different doors. Falls back to spawnRoot if no exits are assigned.
            Transform spawnOrigin = (exitNodes != null && exitNodes.Count > 0)
                ? exitNodes[Random.Range(0, exitNodes.Count)].transform
                : spawnRoot;

            Vector3 pos = spawnOrigin.position + Random.insideUnitSphere * spawnRadius;
            pos.y = spawnOrigin.position.y;

            GameObject go = Instantiate(agentPrefab, pos, Quaternion.identity);
            go.name = $"Agent_{total:0000}";

            var agent = go.GetComponent<NavigationAgent>();
            if (agent == null)
            {
                Debug.LogError("[ScheduleManager] agentPrefab missing NavigationAgent!");
                Destroy(go);
                continue;
            }

            // Sort sections by start time before handing to the schedule.
            sections.Sort((a, b) => a.Item1.startMinute.CompareTo(b.Item1.startMinute));

            var schedule = new AgentSchedule(sections);
            agent.SetSchedule(schedule);
            // Pass the first classroom as the initial targetRoom, plus the exit list.
            agent.Init(sections[0].Item2, exitNodes);

            _allAgents.Add(agent);
            total++;

            yield return null; // One agent per frame — avoids NavMesh/ABMU burst.
        }

        Debug.Log($"[ScheduleManager] Spawned {total} agents.");
    }

    public IReadOnlyList<NavigationAgent> AllAgents => _allAgents;
}