using System;
using System.Reflection;
using UnityEditor;
using UnityEngine;

// Run in an isolated batch editor: -executeMethod AgentInteractionValidation.Run -quit
public static class AgentInteractionValidation
{
    static void Call(object target, string method, params object[] args) =>
        target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Invoke(target, args);
    static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    public static void Run()
    {
        if (!Application.isBatchMode) throw new InvalidOperationException("Run these simulation fixtures in an isolated batch editor.");
        var root = new GameObject("Agent airflow fixture");
        AgentBodyWake wake = null;
        try
        {
            wake = root.AddComponent<AgentBodyWake>();
            Call(wake, "OnEnable"); wake.smoothing = 0.01f;
            root.transform.position += Vector3.right * 0.1f;
            Call(wake, "AdvanceWake", 0.1f);
            Vector3 body = root.transform.position + Vector3.up * wake.bodyHeight;
            var moving = AgentBodyWake.SampleVelocity(body);
            Require(moving.x > 0.5f && Mathf.Abs(moving.z) < 0.0001f, "Walking wake must follow movement, not point radially.");
            Require(AgentBodyWake.SampleVelocity(body + Vector3.up * 4) == Vector3.zero, "Wake crossed floors.");
            root.transform.position += Vector3.right * 1.6f;
            Call(wake, "AdvanceWake", 0.1f);
            Require(wake.WalkingVelocity.x > 2.9f && wake.WalkingVelocity.x <= wake.maximumWakeSpeed, "Accelerated walking lost its wake or exceeded the speed cap.");
            body = root.transform.position + Vector3.up * wake.bodyHeight;
            for (int i = 0; i < 60; i++) Call(wake, "AdvanceWake", 1f / 60);
            Require(AgentBodyWake.SampleVelocity(body).magnitude < 0.001f, "Stopped wake did not decay.");
            root.transform.position += Vector3.right * 20;
            Call(wake, "AdvanceWake", 0.1f);
            Require(wake.WalkingVelocity == Vector3.zero, "Teleport produced a gust.");
            Call(wake, "AdvanceWake", 0f);

            var mouth = new GameObject("Mouth"); mouth.transform.SetParent(root.transform, false); mouth.transform.localPosition = Vector3.up * 1.6f;
            wake.mouth = mouth.transform;
            Vector3 front = mouth.transform.position + Vector3.forward * 0.2f;
            wake.SetBreath(1.5f);
            Require(AgentBodyWake.SampleVelocity(front).z > 0, "Exhale jet points inward.");
            Require(AgentBodyWake.SampleVelocity(mouth.transform.position - Vector3.forward * 0.3f) == Vector3.zero, "Exhale jet exits back of head.");
            wake.SetBreath(-1.2f);
            Require(AgentBodyWake.SampleVelocity(front).z < 0, "Inhale points outward.");
            Require(AgentBodyWake.SampleVelocity(front + Vector3.forward * 2) == Vector3.zero, "Breath escaped local range.");
            for (int space = 0; space < 3; space++)
            {
                VerifyParticle(root, wake, space, false, 1.5f);
                VerifyParticle(root, wake, space, false, -1.2f);
            }
            VerifyParticle(root, wake, 1, true, 1.5f);
            VerifyParticle(root, wake, 1, true, -1.2f);

            var ps = mouth.AddComponent<ParticleSystem>(); ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var behavior = mouth.AddComponent<AgentBehaviorController>(); behavior.mouthParticles = ps;
            Call(behavior, "Awake");
            var phase = typeof(AgentBehaviorController).GetField("phase", BindingFlags.Instance | BindingFlags.NonPublic);
            phase.SetValue(behavior, behavior.exhaleFraction * 0.5f);
            Call(behavior, "AdvanceRespiration", 0f);
            Require(ps.emission.enabled && !behavior.IsInhaling && wake.BreathVelocity > 0, "Exhalation and emission are out of sync.");
            var health = root.AddComponent<AgentHealth>(); Call(health, "Awake");
            var canInhale = typeof(AgentHealth).GetProperty("CanInhale", BindingFlags.Instance | BindingFlags.NonPublic);
            Require(!(bool)canInhale.GetValue(health), "Health captures during exhalation.");
            phase.SetValue(behavior, behavior.exhaleFraction + (1 - behavior.exhaleFraction) * 0.5f);
            Call(behavior, "AdvanceRespiration", 0f);
            Require(!ps.emission.enabled && behavior.IsInhaling && wake.BreathVelocity < 0, "Inhalation must pull inward without emitting.");
            Require((bool)canInhale.GetValue(health), "Health cannot inhale during the inward phase.");
            behavior.Cough();
            Require(!behavior.IsInhaling && wake.BreathVelocity > 1.5f, "Cough did not override inhalation with an outward jet.");
            Call(behavior, "AdvanceRespiration", 1.1f);
            Require(!ps.emission.enabled && behavior.IsInhaling, "Breathing did not resume after cough.");
            Call(behavior, "OnDisable");
            Require(wake.BreathVelocity == 0 && !ps.emission.enabled, "Disabled behavior still emits.");
            Call(wake, "OnDisable");
            Require(AgentBodyWake.SampleVelocity(front) == Vector3.zero, "Disabled source remains in the spatial grid.");
            Debug.Log("AGENT INTERACTION VALIDATION PASSED: directional/fading wake, floors, teleport/pause, forward exhale, inward inhale, local range, physical particle movement in three spaces and ambient mode, emission phases, health capture gating, cough recovery and cleanup.");
        }
        finally
        {
            if (wake != null) Call(wake, "OnDisable");
            UnityEngine.Object.DestroyImmediate(root);
        }
        AirflowValidation.ValidateParticleMotion();
    }
    static void VerifyParticle(GameObject root, AgentBodyWake wake, int space, bool ambient, float breath)
    {
        var go = new GameObject("Particle fixture"); go.transform.SetParent(root.transform, false);
        go.transform.rotation = Quaternion.Euler(0, 53, 0); go.transform.localScale = Vector3.one * 2;
        var ps = go.AddComponent<ParticleSystem>(); ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        var main = ps.main; main.startSpeed = 0; main.startLifetime = 10; main.maxParticles = 1;
        main.simulationSpace = (ParticleSystemSimulationSpace)space;
        if (space == 2) main.customSimulationSpace = go.transform;
        var emission = ps.emission; emission.enabled = false;
        var shape = ps.shape; shape.enabled = false;
        if (ambient) { var marker = go.AddComponent<AmbientParticleMarker>(); marker.Tau = 0.05f; }
        var driver = go.AddComponent<AirflowParticleDriver>(); Call(driver, "Awake");
        driver.diffusionStrength = 0; driver.settlingSpeed = 0;
        Vector3 start = wake.mouth.position + Vector3.forward * 0.2f;
        Vector3 local = space == 1 ? start : go.transform.InverseTransformPoint(start);
        ps.Play(); ps.Emit(new ParticleSystem.EmitParams { position = local, velocity = Vector3.zero, startLifetime = 10 }, 1);
        wake.SetBreath(breath);
        for (int i = 0; i < 12; i++) { ps.Play(); Call(driver, "AdvanceParticles", 1f / 60); ps.Simulate(1f / 60, false, false, false); }
        var particles = new ParticleSystem.Particle[1]; Require(ps.GetParticles(particles) == 1, "Particle disappeared.");
        Vector3 end = space == 1 ? particles[0].position : go.transform.TransformPoint(particles[0].position);
        Require(breath > 0 ? end.z > start.z + 0.02f : end.z < start.z - 0.02f, "Incorrect breathing displacement in space " + space + ", ambient=" + ambient + ": " + (end - start));
        UnityEngine.Object.DestroyImmediate(go);
    }
}
