using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class FillerAgentManager : MonoBehaviour
{
    [Header("References")]
    public ScheduleManager scheduleManager;
    public NavigationController nCont;
    public TimeManager timeManager;
    public GameObject fillerAgentPrefab;
    public Transform spawnRoot;

    [Header("Timing")]
    [Tooltip("How many sim-minutes before a section's start a filler may be activated.")]
    public int activationLeadMinutes = 15;
    public float pollInterval = 1f;

    private readonly Dictionary<CourseSection, int> _deficits = new();
    private readonly HashSet<(CourseSection section, int day)> _spawnedToday = new();

    private void Start()
    {
        if (scheduleManager == null) scheduleManager = FindObjectOfType<ScheduleManager>();
        if (nCont == null) nCont = FindObjectOfType<NavigationController>();
        if (timeManager == null) timeManager = FindObjectOfType<TimeManager>();
        StartCoroutine(WaitForRosterThenRun());
    }

    private IEnumerator WaitForRosterThenRun()
    {
        while (scheduleManager == null || !scheduleManager.RosterReady)
            yield return null;

        ComputeDeficits();
        StartCoroutine(PollLoop());
    }

    // For every section, real attendance = how many virtual students actually
    // have it on their schedule. Deficit = enrolled - real attendance.
    private void ComputeDeficits()
    {
        var attendance = new Dictionary<CourseSection, int>();
        foreach (var sched in scheduleManager.PendingStudents)
        {
            for (int i = 0; i < sched.ClassCount; i++)
            {
                var sec = sched.GetClassAt(i).Section;
                attendance.TryGetValue(sec, out int c);
                attendance[sec] = c + 1;
            }
        }

        foreach (var section in scheduleManager.courseData.sections)
        {
            attendance.TryGetValue(section, out int attended);
            int deficit = section.totalEnrolled - attended;
            if (deficit > 0) _deficits[section] = deficit;
        }

        Debug.Log($"[FillerAgentManager] {_deficits.Count} section(s) need filler agents " +
                  $"(total real students: {scheduleManager.PendingStudents.Count}).");
    }

    private IEnumerator PollLoop()
    {
        var wait = new WaitForSeconds(pollInterval);
        while (true)
        {
            int simMinute = timeManager.CurrentHour * 60 + timeManager.CurrentMinute;
            var today = timeManager.GetCurrentDayOfWeek();
            int dayKey = timeManager.CurrentDay;

            foreach (var kvp in _deficits)
            {
                var section = kvp.Key;
                if (!section.MeetsOnDay(today)) continue;

                var key = (section, dayKey);
                if (_spawnedToday.Contains(key)) continue;

                if (simMinute >= section.endMinute) { _spawnedToday.Add(key); continue; } // missed today's window
                if (simMinute < section.startMinute - activationLeadMinutes) continue;

                SpawnFillersForSection(section, kvp.Value);
                _spawnedToday.Add(key);
            }

            yield return wait;
        }
    }

    private void SpawnFillersForSection(CourseSection section, int count)
    {
        GameObject room = nCont.GetRoomByNumber(section.roomNumber);
        if (room == null)
        {
            Debug.LogWarning($"[FillerAgentManager] No room node for '{section.roomNumber}' — skipping fillers.");
            return;
        }

        var exitNodes = scheduleManager.exitNodes;
        for (int i = 0; i < count; i++)
        {
            Transform origin = (exitNodes != null && exitNodes.Count > 0)
                ? exitNodes[Random.Range(0, exitNodes.Count)].transform
                : spawnRoot;
            Vector3 pos = GetNavMeshSpawnPoint(origin.position);

            GameObject go = Instantiate(fillerAgentPrefab, pos, Quaternion.identity);
            go.name = $"Filler_{section.roomNumber}_{section.startMinute}_{i}";

            var filler = go.GetComponent<FillerAgent>();
            if (filler == null) { Debug.LogError("[FillerAgentManager] fillerAgentPrefab missing FillerAgent!"); Destroy(go); continue; }
            filler.Setup(section, room, pos);
        }
    }

    private Vector3 GetNavMeshSpawnPoint(Vector3 origin)
    {
        foreach (float r in new float[] { 3f, 6f, 10f })
            if (UnityEngine.AI.NavMesh.SamplePosition(origin, out var hit, r, UnityEngine.AI.NavMesh.AllAreas))
                return hit.position;
        return origin;
    }
}