using ABMU.Core;
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

public class ScheduleManager : MonoBehaviour
{
    [Header("References")]
    public GameObject agentPrefab;
    public CourseData courseData;
    public Transform spawnRoot;
    public float spawnRadius = 3f;

    [Header("Exits / Entrances")]
    public List<GameObject> exitNodes = new();

    [Header("Cap (set automatically from the menu's agent-count selection)")]
    public int maxTotalAgents = 0;

    [Header("Realism")]
    public int minClassesPerAgent = 1;
    public int maxClassesPerAgent = 4;

    [Header("Rolling Spawn (used for refill AFTER the initial load)")]
    [Tooltip("How many sim-minutes before a virtual student's first class they may be activated.")]
    public int activationLeadMinutes = 20;
    [Tooltip("Real seconds between pool-refill checks.")]
    public float poolCheckInterval = 1f;

    public TimeManager _time;
    private readonly Dictionary<string, GameObject> _roomByNumber = new();
    private readonly List<NavigationAgent> _allAgents = new();

    private List<AgentSchedule> _pendingStudents = new();
    private int _pendingCursor = 0;

    private readonly List<NavigationAgent> _agentPool = new();
    private readonly HashSet<NavigationAgent> _activeAgents = new();

    /// <summary>True once virtual student schedules have finished building.</summary>
    public bool IsScheduleBuilt { get; private set; } = false;
    public int AvailableStudentCount => _pendingStudents.Count;

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
        StartCoroutine(BuildScheduleCoroutine());
    }

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

    /// <summary>
    /// Builds the virtual-student roster from CourseData. Runs automatically
    /// on scene start — this is cheap/instant and doesn't spawn anything, so
    /// it doesn't need to wait for the menu.
    /// </summary>
    private IEnumerator BuildScheduleCoroutine()
    {
        yield return null;

        if (agentPrefab == null) { Debug.LogError("[ScheduleManager] agentPrefab is null!"); yield break; }
        if (spawnRoot == null) { Debug.LogError("[ScheduleManager] spawnRoot is null!"); yield break; }

        var slots = new List<(CourseSection section, GameObject classroomNode)>();
        foreach (var section in courseData.sections)
        {
            if (!_roomByNumber.TryGetValue(section.roomNumber, out GameObject node))
            {
                Debug.LogWarning($"[ScheduleManager] No room node for '{section.roomNumber}' — skipping.");
                continue;
            }

            int before = slots.Count;
            for (int i = 0; i < section.totalEnrolled; i++)
                slots.Add((section, node));
            int added = slots.Count - before;

            Debug.Log($"[ScheduleManager] Section {section.roomNumber} {section.startMinute}-{section.endMinute} " +
                      $"totalEnrolled={section.totalEnrolled} → added {added} slots.");
        }
        Debug.Log($"[ScheduleManager] TOTAL slots built: {slots.Count}");

        for (int i = slots.Count - 1; i > 0; i--)
        {
            int j = UnityEngine.Random.Range(0, i + 1);
            (slots[i], slots[j]) = (slots[j], slots[i]);
        }

        var students = new List<List<(CourseSection, GameObject)>>();

        foreach (var slot in slots)
        {
            bool assigned = false;

            for (int i = 0; i < students.Count; i++)
            {
                var existing = students[i];
                if (existing.Count >= maxClassesPerAgent) continue;

                bool conflict = false;
                foreach (var (existingSection, _) in existing)
                {
                    if (SectionsConflict(existingSection, slot.section))
                    {
                        conflict = true;
                        break;
                    }
                }
                if (!conflict)
                {
                    existing.Add(slot);
                    assigned = true;
                    break;
                }
            }

            if (!assigned)
                students.Add(new List<(CourseSection, GameObject)> { slot });
        }

        foreach (var student in students)
        {
            int target = UnityEngine.Random.Range(minClassesPerAgent, maxClassesPerAgent + 1);
            if (student.Count > target)
            {
                for (int i = student.Count - 1; i >= target; i--)
                    student.RemoveAt(UnityEngine.Random.Range(0, student.Count));
            }
        }
        students.RemoveAll(s => s.Count == 0);

        _pendingStudents = new List<AgentSchedule>(students.Count);
        foreach (var student in students)
        {
            student.Sort((a, b) => a.Item1.startMinute.CompareTo(b.Item1.startMinute));
            var schedule = new AgentSchedule();
            foreach (var (section, node) in student)
                schedule.AddClass(section, node);
            _pendingStudents.Add(schedule);
        }

        _pendingStudents.Sort((a, b) => a.GetClassAt(0).Section.startMinute
                                  .CompareTo(b.GetClassAt(0).Section.startMinute));

        Debug.Log($"[ScheduleManager] Built {_pendingStudents.Count} virtual student schedules " +
                  $"from {slots.Count} slots.");

        IsScheduleBuilt = true;
    }

    /// <summary>
    /// Called by the flow controller once the player has chosen an agent
    /// count and pressed Start. Spawns exactly `requestedCount` agents right
    /// away (bypassing the normal activation-lead-time gating), reporting
    /// progress for a loading bar. Agents are fully positioned and
    /// initialized but sit idle. TimeManager.IsRunning is still false at
    /// this point, so NavigationAgent.ScheduleTick won't issue any movement.
    /// Once loading completes, the normal rolling refill (PoolMaintenanceLoop)
    /// takes over for backfilling as agents finish their day.
    /// </summary>
    public IEnumerator LoadAgents(int requestedCount, Action<int, int> onProgress, Action onComplete)
    {
        while (!IsScheduleBuilt) yield return null;

        int count = Mathf.Clamp(requestedCount, 0, _pendingStudents.Count);
        maxTotalAgents = count;

        for (int i = 0; i < count; i++)
        {
            var next = _pendingStudents[_pendingCursor];
            _pendingCursor++;
            SpawnOrReuseAgent(next);
            onProgress?.Invoke(i + 1, count);
            yield return null; // spread instantiation across frames
        }

        onComplete?.Invoke();
        StartCoroutine(PoolMaintenanceLoop());
    }

    private IEnumerator PoolMaintenanceLoop()
    {
        var wait = new WaitForSeconds(poolCheckInterval);
        while (true)
        {
            yield return wait;
            yield return StartCoroutine(FillPoolCoroutine());
        }
    }

    private IEnumerator FillPoolCoroutine()
    {
        int simMinute = _time.CurrentHour * 60 + _time.CurrentMinute;
        int capacity = maxTotalAgents > 0 ? maxTotalAgents : int.MaxValue;

        while (_activeAgents.Count < capacity && _pendingCursor < _pendingStudents.Count)
        {
            var next = _pendingStudents[_pendingCursor];
            int firstClassStart = next.GetClassAt(0).Section.startMinute;

            if (simMinute < firstClassStart - activationLeadMinutes)
                break;

            _pendingCursor++;
            SpawnOrReuseAgent(next);
            yield return null;
        }
    }

    private void SpawnOrReuseAgent(AgentSchedule schedule)
    {
        NavigationAgent agent = GetPooledAgent();

        Transform spawnOrigin = (exitNodes != null && exitNodes.Count > 0)
            ? exitNodes[UnityEngine.Random.Range(0, exitNodes.Count)].transform
            : spawnRoot;
        Vector3 pos = GetNavMeshSpawnPoint(spawnOrigin.position);

        agent.transform.position = pos;
        agent.AssignNewSchedule(schedule, schedule.GetClassAt(0).ClassroomNode, pos);

        _activeAgents.Add(agent);
    }

    private NavigationAgent GetPooledAgent()
    {
        for (int i = 0; i < _agentPool.Count; i++)
        {
            var a = _agentPool[i];
            if (!_activeAgents.Contains(a))
                return a;
        }

        GameObject go = Instantiate(agentPrefab, spawnRoot.position, Quaternion.identity);
        go.name = $"Agent_{_agentPool.Count:0000}";
        var agent = go.GetComponent<NavigationAgent>();
        if (agent == null)
        {
            Debug.LogError("[ScheduleManager] agentPrefab missing NavigationAgent!");
            Destroy(go);
            return null;
        }
        agent.OnFinishedForDay += HandleAgentFinished;
        _agentPool.Add(agent);
        _allAgents.Add(agent);
        return agent;
    }

    private void HandleAgentFinished(NavigationAgent agent)
    {
        _activeAgents.Remove(agent);
    }

    private static bool SectionsConflict(CourseSection a, CourseSection b)
    {
        bool timeOverlap = a.startMinute < b.endMinute && b.startMinute < a.endMinute;
        if (!timeOverlap) return false;

        foreach (TimeManager.DayOfWeek day in Enum.GetValues(typeof(TimeManager.DayOfWeek)))
        {
            if (a.MeetsOnDay(day) && b.MeetsOnDay(day))
                return true;
        }
        return false;
    }

    private Vector3 GetNavMeshSpawnPoint(Vector3 origin)
    {
        foreach (float radius in new float[] { spawnRadius, spawnRadius * 2f, 5f, 10f })
        {
            Vector2 circle = UnityEngine.Random.insideUnitCircle * radius;
            Vector3 candidate = origin + new Vector3(circle.x, 0f, circle.y);
            if (NavMesh.SamplePosition(candidate, out NavMeshHit hit, radius, NavMesh.AllAreas))
                return hit.position;
        }
        Debug.LogWarning($"[ScheduleManager] Could not find NavMesh point near {origin} — using raw position.");
        return origin;
    }

    public IReadOnlyList<NavigationAgent> AllAgents => _allAgents;
}