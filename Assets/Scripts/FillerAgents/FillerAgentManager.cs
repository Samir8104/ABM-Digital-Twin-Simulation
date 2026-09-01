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


    [Header("Spawn Throttling")]
    [SerializeField] int maxSpawnsPerFrame = 5;
    private readonly Queue<(CourseSection section, GameObject room)> _spawnQueue = new();
    private bool _spawnCoroutineRunning = false;

    [Header("Priority")]
    public float startupDelaySeconds = 5f;

    [Header("Enable/Disable")]
    public bool fillersEnabled = true;
    public bool despawnExistingOnDisable = true;


    private readonly Dictionary<CourseSection, int> _deficits = new();
    private readonly HashSet<(CourseSection section, int day)> _spawnedToday = new();
    private ElevatorCallStation[] _callStations;



    public void SetFillersEnabled(bool enabled)
    {
        fillersEnabled = enabled;
        if (!enabled)
        {
            _spawnQueue.Clear();
        }
    }

    private void Start()
    {
        if (scheduleManager == null) scheduleManager = FindObjectOfType<ScheduleManager>();
        if (nCont == null) nCont = FindObjectOfType<NavigationController>();
        if (timeManager == null) timeManager = FindObjectOfType<TimeManager>();

        _callStations = FindObjectsOfType<ElevatorCallStation>();
        StartCoroutine(WaitForRosterThenRun());
    }

    private IEnumerator WaitForRosterThenRun()
    {
        while (scheduleManager == null || !scheduleManager.RosterReady)
            yield return null;

        // Prevent the filler agents from running during the start screen
        while (timeManager == null || !timeManager.IsRunning)
            yield return null;

        ComputeDeficits();
        yield return new WaitForSeconds(startupDelaySeconds);
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
            yield return wait;

            // If the sim gets paused/returned to a menu mid-run, stop spawning
            // until it resumes, never spawn fillers while paused.
            if (timeManager == null || !timeManager.IsRunning)
                continue;

            int simMinute = timeManager.CurrentHour * 60 + timeManager.CurrentMinute;
            var today = timeManager.GetCurrentDayOfWeek();
            int dayKey = timeManager.CurrentDay;

            foreach (var kvp in _deficits)
            {
                var section = kvp.Key;
                if (!section.MeetsOnDay(today)) continue;

                var key = (section, dayKey);
                if (_spawnedToday.Contains(key)) continue;

                if (simMinute >= section.endMinute) { _spawnedToday.Add(key); continue; }
                if (simMinute < section.startMinute - activationLeadMinutes) continue;

                SpawnFillersForSection(section, kvp.Value);
                _spawnedToday.Add(key);
            }
        }
    }
    private void SpawnFillersForSection(CourseSection section, int count)
    {
        if (!fillersEnabled) return;
        GameObject room = nCont.GetRoomByNumber(section.roomNumber);
        if (room == null)
        {
            Debug.LogWarning($"[FillerAgentManager] No room node for '{section.roomNumber}' — skipping fillers.");
            return;
        }
        for(int i = 0; i < count; i++)
        {
            _spawnQueue.Enqueue((section, room));
        }
        if (!_spawnCoroutineRunning)
        {
            StartCoroutine(ProcessSpawnQueue());
        }
    }

    private IEnumerator ProcessSpawnQueue()
    {
        _spawnCoroutineRunning = true;
        int spawnedThisFrame = 0;

        while (_spawnQueue.Count > 0)
        {
            if (timeManager != null && !timeManager.IsRunning)
            {
                yield return null;
                continue;
            }

            var (section, room) = _spawnQueue.Dequeue();
            SpawnOneFiller(section, room);

            spawnedThisFrame++;
            if (spawnedThisFrame >= maxSpawnsPerFrame)
            {
                spawnedThisFrame = 0;
                yield return null;
            }
        }

        _spawnCoroutineRunning = false;
    }
    private void SpawnOneFiller(CourseSection section, GameObject room)
    {
        if (!fillersEnabled) return;

        // basically this line says, if theere are exit nodes, use a random one, otherwise just use the spawnroot as a fallback. 
        Transform origin = (scheduleManager.exitNodes != null && scheduleManager.exitNodes.Count > 0)
        ? scheduleManager.exitNodes[Random.Range(0, scheduleManager.exitNodes.Count)].transform : spawnRoot;
        Vector3 pos = GetNavMeshSpawnPoint(origin.position);

        GameObject go = Instantiate(fillerAgentPrefab, pos, Quaternion.identity); // Spawn the agent
        go.name = $"Filler_{section.roomNumber}_{section.startMinute}";
        var filler = go.GetComponent<FillerAgent>();
        if(filler == null) 
        { 
          Debug.LogError("Filler agent prefab missing"); 
          Destroy(go); 
          return;
        }
        filler.Setup(section, room, pos, nCont, timeManager, _callStations);

    }


    private Vector3 GetNavMeshSpawnPoint(Vector3 origin)
    {
        foreach (float r in new float[] { 3f, 6f, 10f })
            if (UnityEngine.AI.NavMesh.SamplePosition(origin, out var hit, r, UnityEngine.AI.NavMesh.AllAreas))
                return hit.position;
        return origin;
    }
}