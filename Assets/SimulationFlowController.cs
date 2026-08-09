using System;
using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class SimulationFlowController : MonoBehaviour
{
    [Header("Scene References")]
    public ScheduleManager scheduleManager;
    public TimeManager timeManager;

    [Header("Menu Panel")]
    public GameObject menuPanel;
    public TMP_InputField agentCountField;
    public TMP_Dropdown diseaseDropdown;
    public Button startButton;
    public int defaultAgentCount = 100;
    public int maxAgentCountAllowed = 1000;

    [Header("Loading Panel")]
    public GameObject loadingPanel;
    [Tooltip("The green fill Image. RectTransform: Anchor Min (0,0), Anchor Max (0,1), Pivot (0, 0.5).")]
    public RectTransform loadingBarFill;
    [Tooltip("The gray track behind it — used to read the full width to fill toward.")]
    public RectTransform loadingBarBackground;
    public TextMeshProUGUI loadingLabel;
    [Tooltip("How quickly the bar visually catches up to the real load progress. Higher = snappier.")]
    public float barLerpSpeed = 6f;

    [Header("Sim HUD (optional — shown once running)")]
    public GameObject simHudPanel;

    public static DiseaseProfile ActiveDisease { get; private set; }
    public static int ActiveAgentCount { get; private set; }

    // 0..1 — where loading actually is vs. where the bar is currently drawn.
    private float _targetProgress = 0f;
    private float _displayedProgress = 0f;
    private bool _barActive = false;

    private void Awake()
    {
        if (menuPanel != null) menuPanel.SetActive(true);
        if (loadingPanel != null) loadingPanel.SetActive(false);
        if (simHudPanel != null) simHudPanel.SetActive(false);

        if (agentCountField != null) agentCountField.text = defaultAgentCount.ToString();

        if (diseaseDropdown != null)
        {
            diseaseDropdown.ClearOptions();
            var options = new List<string>();
            foreach (var d in DiseaseProfile.Presets) options.Add(d.diseaseName);
            diseaseDropdown.AddOptions(options);
        }

        if (startButton != null) startButton.onClick.AddListener(OnStartPressed);
    }

    private void Update()
    {
        // Smoothly chase the real progress value rather than snapping the bar
        // width every time an agent finishes loading.
        if (!_barActive || loadingBarFill == null) return;

        _displayedProgress = Mathf.MoveTowards(_displayedProgress, _targetProgress, barLerpSpeed * Time.deltaTime);
        ApplyBarWidth(_displayedProgress);
    }

    private void ApplyBarWidth(float progress01)
    {
        float fullWidth = loadingBarBackground != null ? loadingBarBackground.rect.width : 0f;
        var size = loadingBarFill.sizeDelta;
        size.x = fullWidth * Mathf.Clamp01(progress01);
        loadingBarFill.sizeDelta = size;
    }

    private void OnStartPressed()
    {
        int selectedCount = ParseAgentCount();
        int diseaseIndex = Mathf.Clamp(diseaseDropdown != null ? diseaseDropdown.value : 0,
                                        0, DiseaseProfile.Presets.Length - 1);

        ActiveAgentCount = selectedCount;
        ActiveDisease = DiseaseProfile.Presets[diseaseIndex];

        if (menuPanel != null) menuPanel.SetActive(false);
        if (loadingPanel != null) loadingPanel.SetActive(true);

        StartCoroutine(RunLoadSequence(selectedCount));
    }

    private int ParseAgentCount()
    {
        if (agentCountField == null) return defaultAgentCount;
        if (!int.TryParse(agentCountField.text, out int count)) return defaultAgentCount;
        return Mathf.Clamp(count, 1, maxAgentCountAllowed);
    }

    private IEnumerator RunLoadSequence(int selectedCount)
    {
        while (!scheduleManager.IsScheduleBuilt)
            yield return null;

        int requested = Mathf.Min(selectedCount, scheduleManager.AvailableStudentCount);

        _targetProgress = 0f;
        _displayedProgress = 0f;
        _barActive = true;
        ApplyBarWidth(0f);

        if (loadingLabel != null) loadingLabel.text = $"Loading agents… 0 / {requested}";

        bool done = false;
        yield return StartCoroutine(scheduleManager.LoadAgents(
            selectedCount,
            onProgress: (loaded, total) =>
            {
                if (loadingLabel != null) loadingLabel.text = $"Loading agents… {loaded} / {total}";
                _targetProgress = total > 0 ? (float)loaded / total : 1f;
            },
            onComplete: () => done = true));

        while (!done) yield return null;

        // Let the bar visually finish catching up to 100% before switching
        // panels, so it doesn't look like it cuts off mid-fill.
        while (_displayedProgress < 0.999f)
        {
            _displayedProgress = Mathf.MoveTowards(_displayedProgress, 1f, barLerpSpeed * Time.deltaTime);
            ApplyBarWidth(_displayedProgress);
            yield return null;
        }
        _barActive = false;

        if (loadingPanel != null) loadingPanel.SetActive(false);
        if (simHudPanel != null) simHudPanel.SetActive(true);

        timeManager.BeginSimulation();
    }
}