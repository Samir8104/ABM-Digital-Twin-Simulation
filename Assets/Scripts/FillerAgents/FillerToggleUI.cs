using UnityEngine;
using UnityEngine.UI;

public class FillerToggleUI : MonoBehaviour
{
    public FillerAgentManager fillerManager;
    public Toggle toggle;

    private void Awake()
    {
        if (fillerManager == null) fillerManager = FindObjectOfType<FillerAgentManager>();
    }

    private void Start()
    {
        toggle.isOn = fillerManager.fillersEnabled;
        toggle.onValueChanged.AddListener(OnToggleChanged);
    }

    void OnToggleChanged(bool isOn)
    {
        fillerManager.SetFillersEnabled(isOn);
    }
        
}
