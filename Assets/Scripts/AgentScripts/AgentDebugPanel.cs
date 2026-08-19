using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;

public class AgentDebugPanel : MonoBehaviour
{
    [Header("UI References")]
    public GameObject panelRoot;      // The panel GameObject to show/hide
    public TMP_Text nameText;
    public TMP_Text activityText;
    public TMP_Text scheduleText;

    [Header("Refresh")]
    [Tooltip("How often (seconds) to refresh the panel while an agent is selected.")]
    public float refreshInterval = 0.25f;

    private TimeManager _time;



    private NavigationAgent _currentAgent;
    private float _refreshTimer;

    void Awake()
    {
        if (panelRoot != null) panelRoot.SetActive(false);
        _time = FindObjectOfType<TimeManager>();
    }

    void Update()
    {
        if (_currentAgent == null) return;

        _refreshTimer -= Time.deltaTime;
        if (_refreshTimer <= 0f)
        {
            _refreshTimer = refreshInterval;
            Refresh();
        }
    }

    public void Show(NavigationAgent agent)
    {
        if (agent == null) return;
        _currentAgent = agent;
        if (panelRoot != null) panelRoot.SetActive(true);
        Refresh();
    }

    public void Hide()
    {
        _currentAgent = null;
        if (panelRoot != null) panelRoot.SetActive(false);
    }

    private void Refresh()
    {
        if (_currentAgent == null) return;

        nameText.text = _currentAgent.name;

        var schedule = _currentAgent.Schedule;
        if (schedule == null)
        {
            activityText.text = "Activity: (no schedule assigned yet)";
            scheduleText.text = "";
            return;
        }

        activityText.text = $"Activity: {schedule.CurrentActivity}";

        var active = schedule.ActiveClass;
        var today = _time != null ? _time.GetCurrentDayOfWeek() : TimeManager.DayOfWeek.Monday;

        var entries = new List<(AgentSchedule.ClassEntry entry, int rank)>();
        for (int i = 0; i < schedule.ClassCount; i++)
        {
            var entry = schedule.GetClassAt(i);
            entries.Add((entry, MinDayRank(entry.Section, today)));
        }

        // Today's classes first (by time), then next day's classes, etc.
        entries.Sort((a, b) =>
        {
            int rankCompare = a.rank.CompareTo(b.rank);
            return rankCompare != 0 ? rankCompare : a.entry.Section.startMinute.CompareTo(b.entry.Section.startMinute);
        });

        var sb = new StringBuilder();
        sb.AppendLine("Schedule:");

        foreach (var (entry, _) in entries)
        {
            bool isActive = active.HasValue && active.Value.Section == entry.Section;
            string startStr = MinutesToTimeString(entry.Section.startMinute);
            string endStr = MinutesToTimeString(entry.Section.endMinute);
            string days = GetMeetingDaysString(entry.Section);

            string line = $"  {startStr} - {endStr}  Room {entry.Section.roomNumber}  [{days}]";
            if (isActive) line = $"<b><color=#FFD24A>{line}  ? ACTIVE</color></b>";

            sb.AppendLine(line);
        }

        scheduleText.text = sb.ToString();
    }

    // 0 = meets today, 1 = next day this week the class occurs, etc.
    private static int MinDayRank(CourseSection section, TimeManager.DayOfWeek today)
    {
        int best = int.MaxValue;
        foreach (TimeManager.DayOfWeek day in System.Enum.GetValues(typeof(TimeManager.DayOfWeek)))
        {
            if (!section.MeetsOnDay(day)) continue;
            int rank = ((int)day - (int)today + 5) % 5;
            if (rank < best) best = rank;
        }
        return best == int.MaxValue ? 5 : best;
    }
    private static string GetMeetingDaysString(CourseSection section)
    {
        var sb = new StringBuilder();
        foreach (TimeManager.DayOfWeek day in System.Enum.GetValues(typeof(TimeManager.DayOfWeek)))
        {
            if (section.MeetsOnDay(day))
            {
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(day.ToString().Substring(0, 3)); // Monday ? Mon, etc.
            }
        }
        return sb.ToString();
    }
    private static string MinutesToTimeString(int totalMinutes)
    {
        int h = (totalMinutes / 60) % 24;
        int m = totalMinutes % 60;
        string suffix = h >= 12 ? "PM" : "AM";
        int displayH = h % 12;
        if (displayH == 0) displayH = 12;
        return $"{displayH:00}:{m:00} {suffix}";
    }
}