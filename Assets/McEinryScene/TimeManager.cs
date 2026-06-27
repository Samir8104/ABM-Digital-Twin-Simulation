using UnityEngine;
using TMPro;
using System;
public class TimeManager : MonoBehaviour
{
    [Header("Time Settings")]
    // Multilier for simulation time vs real time. 
    public float timeMultipler = 60f;

    [Header("UI")]
    public TextMeshProUGUI timeText;

    private float _currentTime;
    public int startHour;
    public int endHour;
    public int simMinute;

    public int CurrentDay { get; private set; } = 1;
    public int CurrentHour { get; private set; } = 8; // What time does it start at? For the best results, I'm gonna start it at 8am
    public int CurrentMinute { get; private set; }


    public enum DayOfWeek { Monday, Tuesday, Wednesday, Thursday, Friday}
    private bool isAm = true;




    public DayOfWeek GetCurrentDayOfWeek()
    {
        int dayIndex = (CurrentDay - 1) % 7; 
        return (DayOfWeek)dayIndex;
    }

    private void Update()
    {
        _currentTime += Time.deltaTime * timeMultipler;

        CurrentHour = (Mathf.FloorToInt(_currentTime / 3600f) % 24) + startHour;
        CurrentMinute = Mathf.FloorToInt((_currentTime % 3600f) / 60f);
        simMinute = CurrentHour * 60 + CurrentMinute;
        if (CurrentHour >= 12 )
        {
            isAm = false;
        }
        if(CurrentHour >= endHour)
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
        if(timeText != null)
        {
            // format
            if (isAm)
            {
                timeText.text = $"{GetCurrentDayOfWeek()}, {CurrentHour:00}:{CurrentMinute:00} AM";

            } else
            {
                if(CurrentHour != 12)
                {
                    CurrentHour = CurrentHour - 12;
                }
                timeText.text = $"{GetCurrentDayOfWeek()}, {CurrentHour:00}:{CurrentMinute:00} PM";

            }
        }
    }
}
