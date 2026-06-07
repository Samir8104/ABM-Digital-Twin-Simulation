using System;
using System.Collections.Generic;
using UnityEngine;

// ─────────────────────────────────────────────────────────────────────────────
// Data structures shared across the scheduling system.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Days a course section meets. Flags so a section can meet on multiple days.
/// </summary>
[Flags]
public enum CourseDays
{
    None = 0,
    Monday = 1 << 0,
    Tuesday = 1 << 1,
    Wednesday = 1 << 2,
    Thursday = 1 << 3,
    Friday = 1 << 4
}

/// <summary>
/// A single course section parsed from the CSV.
/// Only the fields the simulation actually uses are stored.
/// </summary>
[Serializable]
public class CourseSection
{
    [Tooltip("Room number string, e.g. '116'. Used to find the matching NavNode.")]
    public string roomNumber;

    [Tooltip("Start time in minutes since midnight, e.g. 10*60+45 = 645.")]
    public int startMinute;

    [Tooltip("End time in minutes since midnight.")]
    public int endMinute;

    [Tooltip("Total enrolled students — the simulation spawns exactly this many agents for this section.")]
    public int totalEnrolled;

    [Tooltip("Which days of the week this section meets.")]
    public CourseDays meetingDays;

    // ── Convenience ──────────────────────────────────────────────────────────

    /// <summary>Returns true when the supplied TimeManager day matches this section's meeting days.</summary>
    public bool MeetsOnDay(TimeManager.DayOfWeek day)
    {
        CourseDays flag = DayToFlag(day);
        return (meetingDays & flag) != 0;
    }

    private static CourseDays DayToFlag(TimeManager.DayOfWeek d)
    {
        return d switch
        {
            TimeManager.DayOfWeek.Monday => CourseDays.Monday,
            TimeManager.DayOfWeek.Tuesday => CourseDays.Tuesday,
            TimeManager.DayOfWeek.Wednesday => CourseDays.Wednesday,
            TimeManager.DayOfWeek.Thursday => CourseDays.Thursday,
            TimeManager.DayOfWeek.Friday => CourseDays.Friday,
            _ => CourseDays.None
        };
    }
}

/// <summary>
/// ScriptableObject that holds every course section for the building.
/// Populate via <see cref="CourseDataImporter"/> (Editor menu) or by hand.
/// </summary>
[CreateAssetMenu(fileName = "CourseData", menuName = "Simulation/Course Data")]
public class CourseData : ScriptableObject
{
    public List<CourseSection> sections = new();
}
