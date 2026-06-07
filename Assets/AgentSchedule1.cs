using System.Collections.Generic;
using UnityEngine;

// ?????????????????????????????????????????????????????????????????????????????
// What an agent can be doing at any given moment.
// ?????????????????????????????????????????????????????????????????????????????
public enum AgentActivity
{
    Idle,           // Waiting for the first class of the day
    GoingToClass,   // Actively walking to the classroom
    InClass,        // Sitting in class until endMinute
    Wandering,      // Free time between activities (random: bathroom, lounge…)
    GoingToBathroom,
    InBathroom,
    GoingToOfficeHours,
    InOfficeHours,
    Chatting,       // Temporarily stopped to chat (stationary wander)
    Done            // No more activities today — agent may leave the building
}

/// <summary>
/// Encapsulates one agent's daily schedule built from a single <see cref="CourseSection"/>.
/// The owning <see cref="NavigationAgent"/> ticks <see cref="Tick"/> every frame / stepper
/// cycle and reacts to the returned <see cref="ScheduleCommand"/>.
///
/// Design philosophy:
///   • All randomness lives here, not in NavigationAgent, keeping navigation logic clean.
///   • Easy to extend: add a new <see cref="AgentActivity"/> value and handle it in
///     <see cref="Tick"/>.
/// </summary>
public class AgentSchedule
{
    // ?? Configuration (tweak here for tuning) ?????????????????????????????????

    // Probability weights for picking a random between-class activity.
    // Must sum to 1.0 (or just be relative — they are normalised below).
    private static readonly (AgentActivity activity, float weight)[] RandomActivities =
    {
        (AgentActivity.GoingToBathroom,     0.35f),
        (AgentActivity.GoingToOfficeHours,  0.20f),
        (AgentActivity.Chatting,            0.30f),
        (AgentActivity.Wandering,           0.15f),
    };

    // How long (sim minutes) each random activity lasts before picking the next.
    private static readonly (int min, int max) BathroomDuration = (2, 6);
    private static readonly (int min, int max) OfficeHoursDuration = (10, 30);
    private static readonly (int min, int max) ChattingDuration = (2, 8);
    private static readonly (int min, int max) WanderDuration = (3, 12);

    // How many minutes before class starts an agent begins walking toward the room.
    private const int WalkEarlyMinutes = 5;

    // ?? State ?????????????????????????????????????????????????????????????????

    public AgentActivity CurrentActivity { get; private set; } = AgentActivity.Idle;

    private readonly CourseSection _section;
    private readonly GameObject _classroomNode;

    // Minutes (sim time) at which the current random activity should end.
    private int _activityEndMinute = -1;

    // Whether the agent has attended class today.
    private bool _attendedToday = false;

    // ?? Constructor ???????????????????????????????????????????????????????????

    public AgentSchedule(CourseSection section, GameObject classroomNode)
    {
        _section = section;
        _classroomNode = classroomNode;
    }

    // ?? Public API ????????????????????????????????????????????????????????????

    /// <summary>
    /// Called every tick (or every few ticks) by NavigationAgent.
    /// Returns a <see cref="ScheduleCommand"/> describing what the agent should do next.
    /// The command is only meaningful when <see cref="ScheduleCommand.Changed"/> is true.
    /// </summary>
    public ScheduleCommand Tick(TimeManager time)
    {
        int currentMinute = time.CurrentHour * 60 + time.CurrentMinute;
        bool classToday = _section.MeetsOnDay(time.GetCurrentDayOfWeek());

        switch (CurrentActivity)
        {
            // ?? Idle ??????????????????????????????????????????????????????????
            case AgentActivity.Idle:
                if (classToday && currentMinute >= _section.startMinute - WalkEarlyMinutes)
                {
                    return Transition(AgentActivity.GoingToClass,
                        new ScheduleCommand { Target = _classroomNode });
                }
                // No class today ? go straight to random activities then done
                if (!classToday)
                {
                    return TransitionToRandomActivity(currentMinute);
                }
                break;

            // ?? Going to class ????????????????????????????????????????????????
            case AgentActivity.GoingToClass:
                // Already heading there; NavigationAgent calls OnArrivedAtDestination
                // which will call NotifyArrived(), switching us to InClass.
                break;

            // ?? In class ?????????????????????????????????????????????????????
            case AgentActivity.InClass:
                if (currentMinute >= _section.endMinute)
                {
                    _attendedToday = true;
                    return TransitionToRandomActivity(currentMinute);
                }
                break;

            // ?? Random activities (bathroom, office hours, chatting, wander) ??
            case AgentActivity.GoingToBathroom:
            case AgentActivity.GoingToOfficeHours:
            case AgentActivity.Chatting:
            case AgentActivity.Wandering:
                // NavigationAgent notifies via NotifyArrived(); handled below.
                break;

            case AgentActivity.InBathroom:
            case AgentActivity.InOfficeHours:
                if (currentMinute >= _activityEndMinute)
                    return TransitionToRandomActivity(currentMinute);
                break;

            // ?? Done ??????????????????????????????????????????????????????????
            case AgentActivity.Done:
                // Nothing further to do today.
                break;
        }

        return ScheduleCommand.NoChange;
    }

    /// <summary>
    /// Called by NavigationAgent when the agent physically arrives at its destination.
    /// Returns the next command so NavigationAgent knows what to do immediately.
    /// </summary>
    public ScheduleCommand NotifyArrived(TimeManager time)
    {
        int currentMinute = time.CurrentHour * 60 + time.CurrentMinute;

        switch (CurrentActivity)
        {
            case AgentActivity.GoingToClass:
                return Transition(AgentActivity.InClass, ScheduleCommand.Stay);

            case AgentActivity.GoingToBathroom:
                _activityEndMinute = currentMinute + Random.Range(BathroomDuration.min, BathroomDuration.max);
                return Transition(AgentActivity.InBathroom, ScheduleCommand.Stay);

            case AgentActivity.GoingToOfficeHours:
                _activityEndMinute = currentMinute + Random.Range(OfficeHoursDuration.min, OfficeHoursDuration.max);
                return Transition(AgentActivity.InOfficeHours, ScheduleCommand.Stay);

            case AgentActivity.Chatting:
                _activityEndMinute = currentMinute + Random.Range(ChattingDuration.min, ChattingDuration.max);
                // Stay put to "chat"; reuse Stay so the agent idles in place
                return ScheduleCommand.Stay;

            case AgentActivity.Wandering:
                _activityEndMinute = currentMinute + Random.Range(WanderDuration.min, WanderDuration.max);
                return ScheduleCommand.Stay;
        }

        return ScheduleCommand.NoChange;
    }

    /// <summary>
    /// Called when a stationary activity timer expires, or directly from Tick.
    /// Picks the next random activity or marks the agent as Done.
    /// </summary>
    public ScheduleCommand NotifyActivityTimer(TimeManager time, NavigationController navController)
    {
        int currentMinute = time.CurrentHour * 60 + time.CurrentMinute;
        bool classToday = _section.MeetsOnDay(time.GetCurrentDayOfWeek());

        // If class hasn't happened yet and it's almost time, head to class
        if (classToday && !_attendedToday &&
            currentMinute >= _section.startMinute - WalkEarlyMinutes)
        {
            return Transition(AgentActivity.GoingToClass,
                new ScheduleCommand { Target = _classroomNode });
        }

        return TransitionToRandomActivity(currentMinute);
    }

    // ?? Private helpers ???????????????????????????????????????????????????????

    private ScheduleCommand TransitionToRandomActivity(int currentMinute)
    {
        // If it's late in the day and class is over (or no class), leave.
        bool classToday = _section.MeetsOnDay(TimeManager.DayOfWeek.Monday); // checked via section
        if (_attendedToday || currentMinute > _section.endMinute + 60)
        {
            // After class + 60-minute buffer, 30 % chance each tick the agent decides to leave.
            if (Random.value < 0.30f)
                return Transition(AgentActivity.Done, ScheduleCommand.Leave);
        }

        AgentActivity chosen = PickRandomActivity();
        return Transition(chosen, new ScheduleCommand { ActivityHint = chosen });
    }

    private static AgentActivity PickRandomActivity()
    {
        float total = 0f;
        foreach (var (_, w) in RandomActivities) total += w;

        float roll = Random.Range(0f, total);
        float cumulative = 0f;
        foreach (var (activity, weight) in RandomActivities)
        {
            cumulative += weight;
            if (roll <= cumulative) return activity;
        }
        return AgentActivity.Wandering;
    }

    private ScheduleCommand Transition(AgentActivity next, ScheduleCommand cmd)
    {
        CurrentActivity = next;
        cmd.Changed = true;
        return cmd;
    }
}

// ?????????????????????????????????????????????????????????????????????????????
// Lightweight value type returned by AgentSchedule to tell NavigationAgent
// what to do next.  No allocations — just set fields.
// ?????????????????????????????????????????????????????????????????????????????
public struct ScheduleCommand
{
    /// <summary>True when NavigationAgent should act on this command.</summary>
    public bool Changed;

    /// <summary>Move to this GameObject (null = stay put or not applicable).</summary>
    public GameObject Target;

    /// <summary>Hint about which activity is starting (used to pick the right node).</summary>
    public AgentActivity ActivityHint;

    // ?? Static singletons for common cases ???????????????????????????????????
    public static readonly ScheduleCommand NoChange = new() { Changed = false };
    public static readonly ScheduleCommand Stay = new() { Changed = true, Target = null };
    public static readonly ScheduleCommand Leave = new() { Changed = true, Target = null, ActivityHint = AgentActivity.Done };
}