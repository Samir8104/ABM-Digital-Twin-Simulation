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
    public TMP_InputField infectedCountField;
    public int defaultInfectedCount = 1;
    private bool _starting;
    public TMP_Dropdown diseaseDropdown;
    public Button startButton;
    public int defaultAgentCount = 100;
    public int maxAgentCountAllowed = 1000;
    [Header("Agent simulation")]
    public Toggle agentToggle;
    public bool defaultAgentsEnabled = true;
    private bool _agentsEnabled;
    private FillerToggleUI _fillerUI;
    [Header("Particle simulation")]
    public Toggle particleToggle;
    public bool defaultParticlesEnabled = true;
    private SimulationHeatmap heatmap;
    [Header("Background and recording")]
    [Tooltip("Automatically load the defaults and disable cameras/UI when entering Play mode.")]
    public bool runWithoutRendering;
    public bool recordCsv = true;
    [Min(0.1f)] public float recordingIntervalSeconds = 1f;
    public static bool RenderingDisabled { get; private set; }

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
    public static int ActiveInfectedCount { get; private set; }

    // 0..1 — where loading actually is vs. where the bar is currently drawn.
    private float _targetProgress = 0f;
    private float _displayedProgress = 0f;
    private bool _barActive = false;

    private void Awake()
    {
        // Batch editor validation must not start a simulation accidentally.
        RenderingDisabled = runWithoutRendering || (!Application.isEditor &&
            (Application.isBatchMode || Array.IndexOf(Environment.GetCommandLineArgs(), "--sim-no-render") >= 0));
        Application.runInBackground = true;
        ParticleSimulationSettings.SetSimulationRunning(false);
        ParticleSimulationSettings.SetEnabled(PlayerPrefs.GetInt(ParticleSimulationSettings.PreferenceKey,
            defaultParticlesEnabled ? 1 : 0) != 0, false);
        foreach (var particles in FindObjectsByType<ParticleSystem>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            ParticleSimulationSettings.Register(particles);
        heatmap = GetComponent<SimulationHeatmap>();
        if (heatmap == null) heatmap = gameObject.AddComponent<SimulationHeatmap>();
        heatmap.Initialize(timeManager);
        if (RenderingDisabled)
        {
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = 30;
            foreach (var camera in FindObjectsByType<Camera>(FindObjectsInactive.Include, FindObjectsSortMode.None)) camera.enabled = false;
            foreach (var canvas in FindObjectsByType<Canvas>(FindObjectsInactive.Include, FindObjectsSortMode.None)) canvas.enabled = false;
        }
        if (menuPanel != null) menuPanel.SetActive(true);
        if (loadingPanel != null) loadingPanel.SetActive(false);
        if (simHudPanel != null) simHudPanel.SetActive(false);

        if (agentCountField != null) agentCountField.text = defaultAgentCount.ToString();
        if (infectedCountField != null) infectedCountField.text = defaultInfectedCount.ToString();

        if (diseaseDropdown != null)
        {
            diseaseDropdown.ClearOptions();
            var options = new List<string>();
            foreach (var d in DiseaseProfile.Presets) options.Add(d.diseaseName);
            diseaseDropdown.AddOptions(options);
        }

        if (startButton != null) startButton.onClick.AddListener(OnStartPressed);
        if (particleToggle == null && menuPanel != null) particleToggle = CreateParticleToggle();
        if (particleToggle != null)
        {
            particleToggle.SetIsOnWithoutNotify(ParticleSimulationSettings.Enabled);
            particleToggle.onValueChanged.AddListener(value => ParticleSimulationSettings.SetEnabled(value));
        }
        _fillerUI = menuPanel != null ? menuPanel.GetComponentInChildren<FillerToggleUI>(true) : null;
        if (agentToggle == null && menuPanel != null) agentToggle = CreateAgentToggle();
        if (agentToggle != null)
        {
            agentToggle.SetIsOnWithoutNotify(defaultAgentsEnabled);
            agentToggle.onValueChanged.AddListener(SetAgentsEnabled);
        }
        SetAgentsEnabled(defaultAgentsEnabled);
    }

    private void SetAgentsEnabled(bool value)
    {
        _agentsEnabled = value;
        if (agentCountField != null) agentCountField.interactable = value;
        if (infectedCountField != null) infectedCountField.interactable = value;
        if (_fillerUI != null && _fillerUI.toggle != null) _fillerUI.toggle.interactable = value;
    }

    private Toggle CreateAgentToggle()
    {
        var source = _fillerUI != null ? _fillerUI.toggle : null;
        TextMeshProUGUI sourceLabel = null;
        foreach (var label in menuPanel.GetComponentsInChildren<TextMeshProUGUI>(true))
            if (label.text.IndexOf("Enable filler", StringComparison.OrdinalIgnoreCase) >= 0) { sourceLabel = label; break; }
        if (source == null || sourceLabel == null)
        {
            Debug.LogWarning("[SimulationFlowController] Assign Agent Toggle or provide the existing filler menu controls to match their style.");
            return null;
        }

        var toggle = Instantiate(source, source.transform.parent);
        toggle.gameObject.name = "AgentToggle";
        toggle.onValueChanged = new Toggle.ToggleEvent();
        foreach (var copiedBinding in toggle.GetComponents<FillerToggleUI>())
        {
            copiedBinding.enabled = false;
            if (Application.isPlaying) Destroy(copiedBinding);
            else DestroyImmediate(copiedBinding);
        }
        var agentLabel = Instantiate(sourceLabel, sourceLabel.transform.parent);
        agentLabel.gameObject.name = "AgentToggleText";
        agentLabel.text = "Enable agents?";

        // Keep the existing row and START position, with matching 420-wide columns.
        var togglePosition = ((RectTransform)source.transform).anchoredPosition;
        var labelPosition = sourceLabel.rectTransform.anchoredPosition;
        // The particle control has already split the original row into two columns.
        if (particleToggle != null)
        {
            togglePosition = (togglePosition + ((RectTransform)particleToggle.transform).anchoredPosition) * 0.5f;
            foreach (var label in menuPanel.GetComponentsInChildren<TextMeshProUGUI>(true))
                if (label.gameObject.name == "ParticleText")
                {
                    labelPosition = (labelPosition + label.rectTransform.anchoredPosition) * 0.5f;
                    label.rectTransform.anchoredPosition = labelPosition + Vector2.right * 420f;
                    break;
                }
            ((RectTransform)particleToggle.transform).anchoredPosition = togglePosition + Vector2.right * 420f;
        }
        ((RectTransform)toggle.transform).anchoredPosition = togglePosition + Vector2.left * 420f;
        agentLabel.rectTransform.anchoredPosition = labelPosition + Vector2.left * 420f;
        ((RectTransform)source.transform).anchoredPosition = togglePosition;
        sourceLabel.rectTransform.anchoredPosition = labelPosition;
        var labelSize = sourceLabel.rectTransform.sizeDelta; labelSize.x = 420f;
        sourceLabel.rectTransform.sizeDelta = agentLabel.rectTransform.sizeDelta = labelSize;
        return toggle;
    }

    private Toggle CreateParticleToggle()
    {
        var fillerUI = menuPanel.GetComponentInChildren<FillerToggleUI>(true);
        var source = fillerUI != null ? fillerUI.toggle : null;
        TextMeshProUGUI sourceLabel = null;
        foreach (var label in menuPanel.GetComponentsInChildren<TextMeshProUGUI>(true))
            if (label.text.IndexOf("Enable filler", StringComparison.OrdinalIgnoreCase) >= 0) { sourceLabel = label; break; }
        if (source == null || sourceLabel == null)
        {
            Debug.LogWarning("[SimulationFlowController] Assign Particle Toggle or provide the existing filler menu controls to match their style.");
            return null;
        }

        // Copy the existing font, rounded white box, checkmark and scale exactly.
        // Both options share the existing row; START keeps its original position.
        var togglePosition = ((RectTransform)source.transform).anchoredPosition;
        var labelPosition = sourceLabel.rectTransform.anchoredPosition;
        var toggle = Instantiate(source, source.transform.parent);
        toggle.gameObject.name = "ParticleToggle";
        toggle.onValueChanged = new Toggle.ToggleEvent();
        foreach (var copiedBinding in toggle.GetComponents<FillerToggleUI>())
        {
            copiedBinding.enabled = false;
            if (Application.isPlaying) Destroy(copiedBinding);
            else DestroyImmediate(copiedBinding);
        }
        var particleLabel = Instantiate(sourceLabel, sourceLabel.transform.parent);
        particleLabel.gameObject.name = "ParticleText";
        particleLabel.text = "Enable particles?";
        const float offset = 210f;
        ((RectTransform)source.transform).anchoredPosition = togglePosition + Vector2.left * offset;
        ((RectTransform)toggle.transform).anchoredPosition = togglePosition + Vector2.right * offset;
        sourceLabel.rectTransform.anchoredPosition = labelPosition + Vector2.left * offset;
        particleLabel.rectTransform.anchoredPosition = labelPosition + Vector2.right * offset;
        var labelSize = sourceLabel.rectTransform.sizeDelta; labelSize.x = 420;
        sourceLabel.rectTransform.sizeDelta = particleLabel.rectTransform.sizeDelta = labelSize;
        return toggle;
    }

    private void Start()
    {
        if (RenderingDisabled) OnStartPressed();
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
        if (_starting) return;
        _starting = true;
        int selectedCount = _agentsEnabled ? ParseAgentCount() : 0;
        if (!_agentsEnabled)
            foreach (var fillers in FindObjectsByType<FillerAgentManager>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                fillers.SetFillersEnabled(false);
        int infected = defaultInfectedCount;
        if (infectedCountField != null && int.TryParse(infectedCountField.text, out int parsed)) infected = parsed;
        ActiveInfectedCount = Mathf.Clamp(infected, 0, selectedCount);
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
        ActiveAgentCount = requested;
        ActiveInfectedCount = Mathf.Min(ActiveInfectedCount, requested);
        scheduleManager.initialInfectedCount = ActiveInfectedCount;

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
        while (!RenderingDisabled && _displayedProgress < 0.999f)
        {
            _displayedProgress = Mathf.MoveTowards(_displayedProgress, 1f, barLerpSpeed * Time.deltaTime);
            ApplyBarWidth(_displayedProgress);
            yield return null;
        }
        _barActive = false;

        if (loadingPanel != null) loadingPanel.SetActive(false);
        if (simHudPanel != null) simHudPanel.SetActive(true);

        timeManager.BeginSimulation();
        ParticleSimulationSettings.SetSimulationRunning(true);
        string runDirectory = null;
        if (recordCsv)
        {
            var recorder = GetComponent<SimulationCsvRecorder>();
            if (recorder == null) recorder = gameObject.AddComponent<SimulationCsvRecorder>();
            recorder.Begin(timeManager, recordingIntervalSeconds);
            runDirectory = recorder.OutputDirectory;
        }
        // Heatmap evidence is always saved, even when position/health CSVs are off.
        heatmap.sampleIntervalSeconds = recordingIntervalSeconds;
        heatmap.Begin(runDirectory);
    }
}
