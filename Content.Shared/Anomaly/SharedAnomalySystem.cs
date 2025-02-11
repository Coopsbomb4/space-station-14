﻿using Content.Shared.Administration.Logs;
using Content.Shared.Anomaly.Components;
using Content.Shared.Database;
using Robust.Shared.GameStates;
using Robust.Shared.Network;
using Robust.Shared.Random;
using Robust.Shared.Timing;
using Robust.Shared.Utility;

namespace Content.Shared.Anomaly;

public abstract class SharedAnomalySystem : EntitySystem
{
    [Dependency] protected readonly IGameTiming Timing = default!;
    [Dependency] private readonly INetManager _net = default!;
    [Dependency] protected readonly IRobustRandom Random = default!;
    [Dependency] protected readonly ISharedAdminLogManager Log = default!;
    [Dependency] protected readonly SharedAudioSystem Audio = default!;
    [Dependency] protected readonly SharedAppearanceSystem Appearance = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<AnomalyComponent, ComponentGetState>(OnAnomalyGetState);
        SubscribeLocalEvent<AnomalyComponent, ComponentHandleState>(OnAnomalyHandleState);
        SubscribeLocalEvent<AnomalySupercriticalComponent, ComponentGetState>(OnSupercriticalGetState);
        SubscribeLocalEvent<AnomalySupercriticalComponent, ComponentHandleState>(OnSupercriticalHandleState);

        SubscribeLocalEvent<AnomalyComponent, EntityUnpausedEvent>(OnAnomalyUnpause);
        SubscribeLocalEvent<AnomalyPulsingComponent, EntityUnpausedEvent>(OnPulsingUnpause);
        SubscribeLocalEvent<AnomalySupercriticalComponent, EntityUnpausedEvent>(OnSupercriticalUnpause);
    }

    private void OnAnomalyGetState(EntityUid uid, AnomalyComponent component, ref ComponentGetState args)
    {
        args.State = new AnomalyComponentState(
            component.Severity,
            component.Stability,
            component.Health,
            component.NextPulseTime);
    }

    private void OnAnomalyHandleState(EntityUid uid, AnomalyComponent component, ref ComponentHandleState args)
    {
        if (args.Current is not AnomalyComponentState state)
            return;
        component.Severity = state.Severity;
        component.Stability = state.Stability;
        component.Health = state.Health;
        component.NextPulseTime = state.NextPulseTime;
    }

    private void OnSupercriticalGetState(EntityUid uid, AnomalySupercriticalComponent component, ref ComponentGetState args)
    {
        args.State = new AnomalySupercriticalComponentState
        {
            EndTime = component.EndTime,
            Duration = component.SupercriticalDuration
        };
    }

    private void OnSupercriticalHandleState(EntityUid uid, AnomalySupercriticalComponent component, ref ComponentHandleState args)
    {
        if (args.Current is not AnomalySupercriticalComponentState state)
            return;

        component.EndTime = state.EndTime;
        component.SupercriticalDuration = state.Duration;
    }

    private void OnAnomalyUnpause(EntityUid uid, AnomalyComponent component, ref EntityUnpausedEvent args)
    {
        component.NextPulseTime += args.PausedTime;
        Dirty(component);
    }

    private void OnPulsingUnpause(EntityUid uid, AnomalyPulsingComponent component, ref EntityUnpausedEvent args)
    {
        component.EndTime += args.PausedTime;
    }

    private void OnSupercriticalUnpause(EntityUid uid, AnomalySupercriticalComponent component, ref EntityUnpausedEvent args)
    {
        component.EndTime += args.PausedTime;
        Dirty(component);
    }

    public void DoAnomalyPulse(EntityUid uid, AnomalyComponent? component = null)
    {
        if (!Resolve(uid, ref component))
            return;

        var variation = Random.NextFloat(-component.PulseVariation, component.PulseVariation) + 1;
        component.NextPulseTime = Timing.CurTime + GetPulseLength(component) * variation;

        // if we are above the growth threshold, then grow before the pulse
        if (component.Stability > component.GrowthThreshold)
        {
            ChangeAnomalySeverity(uid, GetSeverityIncreaseFromGrowth(component), component);
        }
        else
        {
            // just doing this to update the scanner ui
            // as they hook into these events
            ChangeAnomalySeverity(uid, 0);
        }

        Log.Add(LogType.Anomaly, LogImpact.Medium, $"Anomaly {ToPrettyString(uid)} pulsed with severity {component.Severity}.");
        Audio.PlayPvs(component.PulseSound, uid);

        var pulse = EnsureComp<AnomalyPulsingComponent>(uid);
        pulse.EndTime  = Timing.CurTime + pulse.PulseDuration;
        Appearance.SetData(uid, AnomalyVisuals.IsPulsing, true);

        var ev = new AnomalyPulseEvent(component.Stability, component.Severity);
        RaiseLocalEvent(uid, ref ev);
    }

    /// <summary>
    /// Begins the animation for going supercritical
    /// </summary>
    /// <param name="uid"></param>
    public void StartSupercriticalEvent(EntityUid uid)
    {
        // don't restart it if it's already begun
        if (HasComp<AnomalySupercriticalComponent>(uid))
            return;

        Log.Add(LogType.Anomaly, LogImpact.High, $"Anomaly {ToPrettyString(uid)} began to go supercritical.");

        var super = EnsureComp<AnomalySupercriticalComponent>(uid);
        super.EndTime = Timing.CurTime + super.SupercriticalDuration;
        Appearance.SetData(uid, AnomalyVisuals.Supercritical, true);
        Dirty(super);
    }

    /// <summary>
    /// Does the supercritical event for the anomaly.
    /// This isn't called once the anomaly reaches the point, but
    /// after the animation for it going supercritical
    /// </summary>
    /// <param name="uid"></param>
    /// <param name="component"></param>
    public void DoAnomalySupercriticalEvent(EntityUid uid, AnomalyComponent? component = null)
    {
        if (!Resolve(uid, ref component))
            return;
        Audio.PlayPvs(component.SupercriticalSound, uid);

        var ev = new AnomalySupercriticalEvent();
        RaiseLocalEvent(uid, ref ev);

        EndAnomaly(uid, component, true);
    }

    /// <summary>
    /// Ends an anomaly, cleaning up all entities that may be associated with it.
    /// </summary>
    /// <param name="uid">The anomaly being shut down</param>
    /// <param name="component"></param>
    /// <param name="supercritical">Whether or not the anomaly ended via supercritical event</param>
    public void EndAnomaly(EntityUid uid, AnomalyComponent? component = null, bool supercritical = false)
    {
        if (!Resolve(uid, ref component))
            return;

        var ev = new AnomalyShutdownEvent(uid, supercritical);
        RaiseLocalEvent(uid, ref ev, true);

        Log.Add(LogType.Anomaly, LogImpact.Extreme, $"Anomaly {ToPrettyString(uid)} went supercritical.");

        if (Terminating(uid) || _net.IsClient)
            return;
        Del(uid);
    }

    /// <summary>
    /// Changes the stability of the anomaly.
    /// </summary>
    /// <param name="uid"></param>
    /// <param name="change"></param>
    /// <param name="component"></param>
    public void ChangeAnomalyStability(EntityUid uid, float change, AnomalyComponent? component = null)
    {
        if (!Resolve(uid, ref component))
            return;

        var newVal = component.Stability + change;

        component.Stability = Math.Clamp(newVal, 0, 1);
        Dirty(component);

        var ev = new AnomalyStabilityChangedEvent(uid, component.Stability);
        RaiseLocalEvent(uid, ref ev, true);
    }

    /// <summary>
    /// Changes the severity of an anomaly, going supercritical if it exceeds 1.
    /// </summary>
    /// <param name="uid"></param>
    /// <param name="change"></param>
    /// <param name="component"></param>
    public void ChangeAnomalySeverity(EntityUid uid, float change, AnomalyComponent? component = null)
    {
        if (!Resolve(uid, ref component))
            return;

        var newVal = component.Severity + change;

        if (newVal >= 1)
            StartSupercriticalEvent(uid);

        component.Severity = Math.Clamp(newVal, 0, 1);
        Dirty(component);

        var ev = new AnomalySeverityChangedEvent(uid, component.Severity);
        RaiseLocalEvent(uid, ref ev, true);
    }

    /// <summary>
    /// Changes the health of an anomaly, ending it if it's less than 0.
    /// </summary>
    /// <param name="uid"></param>
    /// <param name="change"></param>
    /// <param name="component"></param>
    public void ChangeAnomalyHealth(EntityUid uid, float change, AnomalyComponent? component = null)
    {
        if (!Resolve(uid, ref component))
            return;

        var newVal = component.Health + change;

        if (newVal < 0)
        {
            EndAnomaly(uid, component);
            return;
        }

        component.Health = Math.Clamp(newVal, 0, 1);
        Dirty(component);

        var ev = new AnomalyHealthChangedEvent(uid, component.Health);
        RaiseLocalEvent(uid, ref ev, true);
    }

    /// <summary>
    /// Gets the length of time between each pulse
    /// for an anomaly based on its current stability.
    /// </summary>
    /// <remarks>
    /// For anomalies under the instability theshold, this will return the maximum length.
    /// For those over the theshold, they will return an amount between the maximum and
    /// minium value based on a linear relationship with the stability.
    /// </remarks>
    /// <param name="component"></param>
    /// <returns>The length of time as a TimeSpan, not including random variation.</returns>
    public TimeSpan GetPulseLength(AnomalyComponent component)
    {
        DebugTools.Assert(component.MaxPulseLength > component.MinPulseLength);
        var modifier = Math.Clamp((component.Stability - component.GrowthThreshold) /  component.GrowthThreshold, 0, 1);
        return (component.MaxPulseLength - component.MinPulseLength) * modifier + component.MinPulseLength;
    }

    /// <summary>
    /// Gets the increase in an anomaly's severity due
    /// to being above its growth threshold
    /// </summary>
    /// <param name="component"></param>
    /// <returns>The increase in severity for this anomaly</returns>
    private float GetSeverityIncreaseFromGrowth(AnomalyComponent component)
    {
        var score = 1 + Math.Max(component.Stability - component.GrowthThreshold, 0) * 10;
        return score * component.SeverityGrowthCoefficient;
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        foreach (var anomaly in EntityQuery<AnomalyComponent>())
        {
            var ent = anomaly.Owner;

            // if the stability is under the death threshold,
            // update it every second to start killing it slowly.
            if (anomaly.Stability < anomaly.DecayThreshold)
            {
                ChangeAnomalyHealth(ent, anomaly.HealthChangePerSecond * frameTime, anomaly);
            }

            if (Timing.CurTime > anomaly.NextPulseTime)
            {
                DoAnomalyPulse(ent, anomaly);
            }
        }

        foreach (var pulse in EntityQuery<AnomalyPulsingComponent>())
        {
            var ent = pulse.Owner;

            if (Timing.CurTime > pulse.EndTime)
            {
                Appearance.SetData(ent, AnomalyVisuals.IsPulsing, false);
                RemComp(ent, pulse);
            }
        }

        foreach (var (super, anom) in EntityQuery<AnomalySupercriticalComponent, AnomalyComponent>())
        {
            var ent = anom.Owner;

            if (Timing.CurTime <= super.EndTime)
                continue;
            DoAnomalySupercriticalEvent(ent, anom);
            RemComp(ent, super);
        }
    }
}
