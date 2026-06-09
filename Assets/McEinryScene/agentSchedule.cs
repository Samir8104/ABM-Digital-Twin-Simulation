using System.Collections.Generic;
using UnityEngine;

// ── Activity enum ─────────────────────────────────────────────────────────────
// Wandering removed — agents now Leave or Chat instead.
public enum AgentActivity
{
    Idle,               // Waiting, invisible, outside building
    GoingToClass,
    InClass,
    GoingToBathroom,
    InBathroom,
    GoingToOfficeHours,
    InOfficeHours,
    Chatting,
    Leaving,            // Walking to an exit node
    Done                // Deactivated for the day
}

// ── AgentSchedule ─────────────────────────────────────────────────────────────

/// <summary>
/// Holds all of an agent's class sections for the day and tracks which one is
/// currently active. Supports multiple classes per agent per day.
/// </summary>
public class AgentSchedule
{
    // All sections assigned to this agent (may be more than one).
    private readonly List<(CourseSection section, GameObject classroomNode)> _allSections;

    // Index into _allSections for the class currently in progress (or about to start).
    private int _activeIndex = 0;

    public AgentActivity CurrentActivity { get; private set; } = AgentActivity.Idle;

    // ── Convenience accessors for the CURRENT (next upcoming) class ──────────

    public int StartMinute
    {
        get
        {
            var s = ActiveSection;
            return s.HasValue ? s.Value.section.startMinute : int.MaxValue;
        }
    }

    public int EndMinute
    {
        get
        {
            var s = ActiveSection;
            return s.HasValue ? s.Value.section.endMinute : int.MaxValue;
        }
    }

    public GameObject ClassroomNode
    {
        get
        {
            var s = ActiveSection;
            return s.HasValue ? s.Value.classroomNode : null;
        }
    }

    /// <summary>True once the agent has attended at least one class today.</summary>
    public bool AttendedToday { get; private set; } = false;

    /// <summary>True when there are no more classes left in the schedule today.</summary>
    public bool NoMoreClassesToday => _activeIndex >= _allSections.Count;

    // ── Constructor ──────────────────────────────────────────────────────────

    /// <summary>
    /// Pass all sections assigned to this agent, sorted earliest-first.
    /// ScheduleManager is responsible for sorting before construction.
    /// </summary>
    public AgentSchedule(List<(CourseSection, GameObject)> sections)
    {
        // Defensive copy; sort by start time so index 0 is always earliest.
        _allSections = new List<(CourseSection, GameObject)>(sections);
        _allSections.Sort((a, b) => a.Item1.startMinute.CompareTo(b.Item1.startMinute));
    }

    // ── Public API ───────────────────────────────────────────────────────────

    /// <summary>
    /// Returns true if ANY of the agent's sections meet today.
    /// </summary>
    public bool HasClassToday(TimeManager time)
    {
        foreach (var (section, _) in _allSections)
            if (section.MeetsOnDay(time.GetCurrentDayOfWeek())) return true;
        return false;
    }

    /// <summary>
    /// Returns true if the CURRENT active section meets today.
    /// </summary>
    public bool SectionMeetsToday(TimeManager time)
    {
        var s = ActiveSection;
        return s.HasValue && s.Value.section.MeetsOnDay(time.GetCurrentDayOfWeek());
    }

    /// <summary>
    /// Returns how many minutes until the next class starts, or int.MaxValue if
    /// there is no next class today.  Used by the post-class decision logic.
    /// </summary>
    public int MinutesUntilNextClass(int simMinute)
    {
        // Look one past the active index for a future class.
        int nextIndex = _activeIndex + 1;
        if (nextIndex >= _allSections.Count) return int.MaxValue;

        int gap = _allSections[nextIndex].section.startMinute - simMinute;
        return gap > 0 ? gap : int.MaxValue;
    }

    /// <summary>
    /// Advance the active index after a class ends so the next section becomes current.
    /// Call this when the agent finishes InClass and you want it to know about
    /// the next class.
    /// </summary>
    public void AdvanceToNextClass()
    {
        _activeIndex++;
    }

    public void SetActivity(AgentActivity activity)
    {
        if (activity == AgentActivity.InClass)
            AttendedToday = true;
        CurrentActivity = activity;
    }

    /// <summary>
    /// Reset per-day state when a new simulation day starts.
    /// </summary>
    public void ResetForNewDay()
    {
        _activeIndex = 0;
        AttendedToday = false;
        CurrentActivity = AgentActivity.Idle;
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    private (CourseSection section, GameObject classroomNode)? ActiveSection =>
        (_activeIndex < _allSections.Count) ? _allSections[_activeIndex] : null;
}