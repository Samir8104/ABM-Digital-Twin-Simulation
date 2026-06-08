using System.Collections.Generic;
using UnityEngine;

public enum AgentActivity
{
    Idle,
    GoingToClass,
    InClass,
    Wandering,
    GoingToBathroom,
    InBathroom,
    GoingToOfficeHours,
    InOfficeHours,
    Chatting,
    Done
}

public class AgentSchedule
{
    private readonly CourseSection _section;
    private readonly GameObject _classroomNode;

    public AgentActivity CurrentActivity { get; private set; } = AgentActivity.Idle;
    public bool AttendedToday { get; private set; } = false;

    public int StartMinute => _section.startMinute;
    public int EndMinute => _section.endMinute;
    public GameObject ClassroomNode => _classroomNode;

    public AgentSchedule(CourseSection section, GameObject classroomNode)
    {
        _section = section;
        _classroomNode = classroomNode;
    }

    public bool SectionMeetsToday(TimeManager time)
        => _section.MeetsOnDay(time.GetCurrentDayOfWeek());

    /// <summary>Called by NavigationAgent to drive state transitions.</summary>
    public void SetActivity(AgentActivity activity)
    {
        if (activity == AgentActivity.InClass)
            AttendedToday = true;
        CurrentActivity = activity;
    }
}