using UnityEngine;
using TMPro;
using System.Text;

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



    private NavigationAgent _currentAgent;
    private float _refreshTimer;

    void Awake()
    {
        if (panelRoot != null) panelRoot.SetActive(false);
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

        var sb = new StringBuilder();
        sb.AppendLine("Schedule:");

        var active = schedule.ActiveClass;

        for (int i = 0; i < schedule.ClassCount; i++)
        {
            var entry = schedule.GetClassAt(i);
            bool isActive = active.HasValue && active.Value.Section == entry.Section;

            string startStr = MinutesToTimeString(entry.Section.startMinute);
            string endStr = MinutesToTimeString(entry.Section.endMinute);
            string room = entry.Section.roomNumber;

            string line = $"  {startStr} - {endStr}  Room {room}";
            if (isActive) line = $"<b><color=#FFD24A>{line}  ? ACTIVE</color></b>";

            sb.AppendLine(line);
        }


        scheduleText.text = sb.ToString();
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