using UnityEngine;
using TMPro;
using System;
public class TimeManager : MonoBehaviour
{
    [Header("Time Settings")]
    public float timeMultipler = 60f;
    [Tooltip("The timeMultipler value that represents '1x' sim speed for UI/agent-speed purposes.")]
    public float baselineTimeMultiplier = 60f;
    [Header("Agent Speed Scaling")]
    [Tooltip("Agent walk speed multiplier = sqrt(simSpeedMultiplier), clamped to this range.")]
    public float minAgentSpeedMultiplier = 0.5f;
    public float maxAgentSpeedMultiplier = 4f;
    [Header("UI")]
    public TextMeshProUGUI timeText;
    private float _currentTime;
    public int startHour;
    public int endHour;
    public int simMinute;
    public int CurrentDay { get; private set; } = 1;
    public int CurrentHour { get; private set; } = 8;
    public int CurrentMinute { get; private set; }
    public enum DayOfWeek { Monday, Tuesday, Wednesday, Thursday, Friday }
    private bool isAm = true;
    public event Action<float> OnSimSpeedChanged;

    // ?? Sim lifecycle ?????????????????????????????????????????????????????????
    // The clock doesn't move — and therefore nothing that depends on simMinute
    // fires — until BeginSimulation() is called (once the loading screen
    // finishes spawning every requested agent).
    public bool IsRunning { get; private set; } = false;

    public void BeginSimulation()
    {
        if (IsRunning) return;
        IsRunning = true;
    }

    public DayOfWeek GetCurrentDayOfWeek()
    {
        int dayIndex = (CurrentDay - 1) % 7;
        return (DayOfWeek)dayIndex;
    }
    public float CurrentSimSpeedMultiplier => timeMultipler / baselineTimeMultiplier;
    public float GetAgentSpeedMultiplier()
    {
        float raw = Mathf.Sqrt(Mathf.Max(0.01f, CurrentSimSpeedMultiplier));
        return Mathf.Clamp(raw, minAgentSpeedMultiplier, maxAgentSpeedMultiplier);
    }
    /// <summary>Sets sim speed as a multiple of baselineTimeMultiplier (e.g. 2f = "2x").</summary>
    public void SetSimSpeed(float multiplier)
    {
        timeMultipler = baselineTimeMultiplier * multiplier;
        OnSimSpeedChanged?.Invoke(GetAgentSpeedMultiplier());
    }
    private void Start()
    {
        OnSimSpeedChanged?.Invoke(GetAgentSpeedMultiplier());
        UpdateUI(); // show the (frozen) start time on the loading/menu screen
    }
    private void Update()
    {
        if (!IsRunning) return; // clock frozen pre-start; nothing to update

        _currentTime += Time.deltaTime * timeMultipler;
        CurrentHour = (Mathf.FloorToInt(_currentTime / 3600f) % 24) + startHour;
        CurrentMinute = Mathf.FloorToInt((_currentTime % 3600f) / 60f);
        simMinute = CurrentHour * 60 + CurrentMinute;
        if (CurrentHour >= 12) isAm = false;
        if (CurrentHour >= endHour)
        {
            CurrentMinute = 0;
            CurrentHour = 0;
            CurrentDay += 1;
            _currentTime = 0;
            isAm = true;
        }
        UpdateUI();
    }
    private void UpdateUI()
    {
        if (timeText == null) return;
        int displayHour = CurrentHour;
        string suffix = isAm ? "AM" : "PM";
        if (!isAm && displayHour != 12) displayHour -= 12;
        else if (isAm && displayHour == 0) displayHour = 12;
        timeText.text = $"{GetCurrentDayOfWeek()}, {displayHour:00}:{CurrentMinute:00} {suffix}";
    }
}