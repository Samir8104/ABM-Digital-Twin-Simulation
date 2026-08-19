using UnityEngine;
using UnityEngine.UI;
using TMPro;
using System.Collections.Generic;

public class SimSpeedUI : MonoBehaviour
{
    [System.Serializable]
    public class SpeedButtonBinding
    {
        public Button button;
        public float speedMultiplier;
    }

    public TimeManager timeManager;
    public TMP_Text currentSpeedText;

    public List<SpeedButtonBinding> speedButtons = new(); // Init in inspector

    private void Awake()
    {
        if (timeManager == null) timeManager = FindFirstObjectByType<TimeManager>();

        foreach (var binding in speedButtons)
        {
            float speed = binding.speedMultiplier;
            binding.button.onClick.AddListener(() => SetSpeed(speed));
        }
    }

    private void Start()
    {
        UpdateLabel();
    }

    void SetSpeed(float multiplier)
    {
        timeManager.SetSimSpeed(multiplier);
        UpdateLabel();
    }

    void UpdateLabel()
    {
        if(currentSpeedText != null)
        {
            currentSpeedText.text = $"{timeManager.CurrentSimSpeedMultiplier:0.#}x";
        }
    }
}
