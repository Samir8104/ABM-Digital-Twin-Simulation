using System.Collections.Generic;
using UnityEngine;

public enum AgentActivity
{
    Idle,
    OffCampus,          // Agent is inactive/invisible between classes
    GoingToClass,
    InClass,
    Wandering,
    GoingToBathroom,
    InBathroom,
    GoingToOfficeHours,
    InOfficeHours,
    Chatting,
    GoingToStudying,
    InStudying,
    GoingToExit,
    Done
}

/// <summary>
/// Holds a full day's worth of classes for one agent.
/// Sorted by start time at construction so NextClass / CurrentClass lookups are O(n) 
/// over a tiny list (max 4 sections per agent).
/// </summary>
public class AgentSchedule
{

    // Each entry pairs a CourseSection with its resolved classroom GameObject.
    public readonly struct ClassEntry
    {
        public readonly CourseSection Section;
        public readonly GameObject ClassroomNode;
        public ClassEntry(CourseSection s, GameObject node) { Section = s; ClassroomNode = node; }
    }

    private readonly List<ClassEntry> _classes = new(); // sorted ascending by StartMinute

    // ?? Per-session state (resets each sim-day if you add day-rollover later) ?

    public AgentActivity CurrentActivity { get; private set; } = AgentActivity.Idle;

    // Index of the class the agent is currently attending or heading to.
    // -1 means none yet today.
    private int _activeClassIndex = -1;


    public AgentSchedule() { }

    /// <summary>Add a class to this agent's schedule. Call before the sim starts.</summary>
    public void AddClass(CourseSection section, GameObject classroomNode)
    {
        _classes.Add(new ClassEntry(section, classroomNode));
        // Keep sorted so NextClass queries are always correct.
        _classes.Sort((a, b) => a.Section.startMinute.CompareTo(b.Section.startMinute));
    }

    // ?? Public accessors ??????????????????????????????????????????????????????

    public int ClassCount => _classes.Count;

    /// <summary>
    /// The class the agent is currently in or heading to (set when GoingToClass is triggered).
    /// Null if no active class.
    /// </summary>
    public ClassEntry? ActiveClass =>
        _activeClassIndex >= 0 && _activeClassIndex < _classes.Count
            ? _classes[_activeClassIndex]
            : (ClassEntry?)null;

    /// <summary>Convenience: classroom node for the current active class.</summary>
    public GameObject ClassroomNode => ActiveClass?.ClassroomNode;

    /// <summary>Convenience: start minute for the current active class.</summary>
    public int StartMinute => ActiveClass?.Section.startMinute ?? 0;

    /// <summary>Convenience: end minute for the current active class.</summary>
    public int EndMinute => ActiveClass?.Section.endMinute ?? 0;

    /// <summary>
    /// Returns the next upcoming class after the currently active one,
    /// or null if there are no more classes today.
    /// </summary>
    public ClassEntry? NextClass =>
        _activeClassIndex + 1 < _classes.Count
            ? _classes[_activeClassIndex + 1]
            : (ClassEntry?)null;

    /// <summary>
    /// Returns the first class whose start time is still in the future relative
    /// to simMinute, regardless of _activeClassIndex. Used at day-start to find
    /// which class to head to first.
    /// </summary>
    public ClassEntry? NextUpcomingClass(int simMinute)
    {
        foreach (var c in _classes)
            if (c.Section.startMinute > simMinute)
                return c;
        return null;
    }

    /// <summary>
    /// Returns the first class that is about to start (within the departure
    /// window) and that the agent hasn't already been assigned as active.
    /// </summary>
    public ClassEntry? FindClassToHeadTo(int simMinute, int departureWindowMinutes)
    {
        for (int i = 0; i < _classes.Count; i++)
        {
            var c = _classes[i];
            // Skip classes the agent has already attended or is currently attending.
            if (i <= _activeClassIndex) continue;
            if (!ClassMeetsToday(c)) continue;
            int headOutAt = c.Section.startMinute - departureWindowMinutes;
            if (simMinute >= headOutAt && simMinute < c.Section.endMinute)
                return c;
        }
        return null;
    }

    /// <summary>
    /// Marks a specific class as the active one the agent is heading to / in.
    /// </summary>
    public void SetActiveClass(ClassEntry entry)
    {
        for (int i = 0; i < _classes.Count; i++)
        {
            if (_classes[i].Section == entry.Section)
            {
                _activeClassIndex = i;
                return;
            }
        }
    }

    public bool SectionMeetsToday(TimeManager time) =>
        ActiveClass.HasValue && ClassMeetsToday(ActiveClass.Value, time);

    private static bool ClassMeetsToday(ClassEntry c, TimeManager time) =>
        c.Section.MeetsOnDay(time.GetCurrentDayOfWeek());

    // Overload that skips the TimeManager for internal use where we don't have it.
    // Called from FindClassToHeadTo which is itself called with a day-check upstream.
    private static bool ClassMeetsToday(ClassEntry c) => true; // Caller filters by day

    // ?? Activity state ????????????????????????????????????????????????????????

    public void SetActivity(AgentActivity activity)
    {
        CurrentActivity = activity;
    }

    /// <summary>
    /// Minutes until the next class starts. Returns int.MaxValue if no next class.
    /// </summary>
    public int MinutesUntilNextClass(int simMinute)
    {
        ClassEntry? next = NextClass;
        if (!next.HasValue) return int.MaxValue;
        return Mathf.Max(0, next.Value.Section.startMinute - simMinute);
    }

    /// <summary>
    /// True if the agent has another class today and it starts within thresholdMinutes.
    /// </summary>
    public bool HasClassSoon(int simMinute, int thresholdMinutes)
    {
        return MinutesUntilNextClass(simMinute) <= thresholdMinutes;
    }
    /// <summary>Returns the class at index i. Only for use during schedule-building.</summary>
    public ClassEntry GetClassAt(int i) => _classes[i];

}