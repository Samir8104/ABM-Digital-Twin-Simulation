using UnityEngine;

[RequireComponent(typeof(ParticleSystem))]
public sealed class ParticleSimulationGate : MonoBehaviour
{
    private ParticleSystem system;
    private ParticleSystemRenderer visual;
    private bool suspended, resumePlaying, resumeRenderer;

    private void Awake()
    {
        system = GetComponent<ParticleSystem>();
        visual = GetComponent<ParticleSystemRenderer>();
        ParticleSimulationSettings.Add(this);
    }

    private void OnEnable() { if (system != null) Apply(ParticleSimulationSettings.Enabled); }

    public void Apply(bool enabled)
    {
        if (!enabled || !ParticleSimulationSettings.SimulationRunning)
        {
            if (!suspended)
            {
                resumePlaying = system.isPlaying || system.main.playOnAwake;
                resumeRenderer = visual != null && visual.enabled;
                suspended = true;
            }
            system.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);
            if (visual != null) visual.enabled = false;
        }
        else if (suspended)
        {
            suspended = false;
            if (visual != null) visual.enabled = resumeRenderer;
            if (resumePlaying && gameObject.activeInHierarchy) system.Play(false);
        }
    }

    private void OnDestroy() => ParticleSimulationSettings.Remove(this);
}
