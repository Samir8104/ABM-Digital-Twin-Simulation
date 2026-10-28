using System.Collections.Generic;
using UnityEngine;

public static class ParticleSimulationSettings
{
    public const string PreferenceKey = "Simulation.ParticlesEnabled";
    public const string BodyWakePreferenceKey = "Simulation.BodyWakeStrength";
    public static float BodyWakeStrength { get; private set; } = 1f;
    public static bool Enabled { get; private set; } = true;
    public static bool SimulationRunning { get; private set; } = true;
    public static bool IsSimulating => Enabled && SimulationRunning;
    private static readonly List<ParticleSimulationGate> gates = new();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void Reset() { gates.Clear(); Enabled = true; SimulationRunning = true; BodyWakeStrength = 1f; }

    public static void SetBodyWakeStrength(float value, bool savePreference = true)
    {
        BodyWakeStrength = Mathf.Clamp01(value);
        if (savePreference) { PlayerPrefs.SetFloat(BodyWakePreferenceKey, BodyWakeStrength); PlayerPrefs.Save(); }
    }

    public static void SetSimulationRunning(bool value)
    {
        SimulationRunning = value;
        foreach (var gate in gates) if (gate != null) gate.Apply(Enabled);
    }

    public static void SetEnabled(bool value, bool savePreference = true)
    {
        Enabled = value;
        if (savePreference) { PlayerPrefs.SetInt(PreferenceKey, value ? 1 : 0); PlayerPrefs.Save(); }
        foreach (var gate in gates) if (gate != null) gate.Apply(value);
    }

    public static void Register(ParticleSystem system)
    {
        if (system != null && system.GetComponent<ParticleSimulationGate>() == null)
            system.gameObject.AddComponent<ParticleSimulationGate>();
        // Register on creation, rather than repeatedly searching the whole scene.
        if (system != null && system.GetComponent<AirflowParticleDriver>() == null)
            system.gameObject.AddComponent<AirflowParticleDriver>();
    }

    internal static void Add(ParticleSimulationGate gate) { if (!gates.Contains(gate)) gates.Add(gate); gate.Apply(Enabled); }
    internal static void Remove(ParticleSimulationGate gate) => gates.Remove(gate);
}
