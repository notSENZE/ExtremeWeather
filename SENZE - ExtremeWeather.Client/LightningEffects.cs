using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using Comfort.Common;
using EFT;
using EFT.HealthSystem;
using EFT.Weather;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Audio;

namespace SenzeExtremeWeather;

internal sealed class LightningEffects
{
    private static readonly FieldInfo TinnitusClipField = AccessTools.Field(typeof(Player), "_tinnitus");

    private sealed class PendingThunder
    {
        internal float PlayAt;
        internal float Delay;
        internal bool Strong;
        internal ThunderStyle Style;
        internal bool HasOrigin;
        internal Vector3 Origin;
        internal float Distance;
    }

    private readonly ExtremeWeatherConfig _settings;
    private readonly ThunderAudio _thunderAudio;
    private readonly SevereWeatherOverlay _overlay;
    private readonly BodycamInterferenceBridge _bodycamInterference;
    private readonly ManualLogSource _logger;
    private readonly List<PendingThunder> _pendingThunder = new List<PendingThunder>();
    private float _nextLightningAt;
    private bool _wasActive;

    internal LightningEffects(ExtremeWeatherConfig settings, ThunderAudio thunderAudio,
        SevereWeatherOverlay overlay, BodycamInterferenceBridge bodycamInterference,
        ManualLogSource logger)
    {
        _settings = settings;
        _thunderAudio = thunderAudio;
        _overlay = overlay;
        _bodycamInterference = bodycamInterference;
        _logger = logger;
    }

    internal void Update(float stormStrength, bool active)
    {
        UpdateThunder();

        if (!active)
        {
            _wasActive = false;
            return;
        }

        if (!_wasActive)
        {
            _wasActive = true;
            TriggerLightning(Mathf.Clamp01(stormStrength));
            ScheduleNextLightning();
            return;
        }

        if (Time.unscaledTime >= _nextLightningAt)
        {
            TriggerLightning(Mathf.Clamp01(stormStrength));
            ScheduleNextLightning();
        }
    }

    internal void TriggerNow(float stormStrength, bool scheduleThunder = true)
    {
        TriggerLightning(Mathf.Clamp01(stormStrength), scheduleThunder);
        _wasActive = true;
        ScheduleNextLightning();
    }

    internal void TriggerDistantNow(float stormStrength, bool scheduleThunder)
    {
        TriggerDistantLightning(Mathf.Clamp01(stormStrength), scheduleThunder);
    }

    internal bool TriggerNearStrikeNow(float stormStrength, bool scheduleThunder)
    {
        return TriggerNearStrikeNow(
            stormStrength,
            scheduleThunder,
            _settings.NearStrikeImpactEffects.Value,
            false);
    }

    private bool TriggerNearStrikeNow(float stormStrength, bool scheduleThunder,
        bool createImpactEffect, bool forceVisible)
    {
        var brightness = _settings.StrongFlashBrightness.Value * Mathf.Clamp01(stormStrength) * 1.35f;
        if (!_overlay.TriggerNearStrike(brightness, createImpactEffect, forceVisible))
        {
            return false;
        }

        if (_settings.BodycamInterferenceEnabled.Value)
        {
            _bodycamInterference.TryTrigger(
                _settings.NearStrikeInterferenceIntensity.Value,
                _settings.NearStrikeInterferenceDuration.Value);
        }

        var hasOrigin = _overlay.TryGetThunderOrigin(out var origin, out var distance);
        if (_settings.NearStrikeVisionEnabled.Value && hasOrigin)
        {
            TriggerNativeVisionStun(distance);
        }
        if (_settings.NearStrikeTinnitusEnabled.Value
            && hasOrigin
            && distance <= _settings.NearStrikeTinnitusDistance.Value)
        {
            TriggerNativeTinnitus(distance);
        }

        if (scheduleThunder)
        {
            var delay = Random.Range(0.05f, 0.18f);
            ScheduleThunder(delay, true, ThunderStyle.ClapAndRumble, hasOrigin, origin, distance);
            _logger.LogInfo($"Near lightning strike triggered; loud thunder scheduled in {delay:0.00}s.");
        }
        else
        {
            _logger.LogInfo("Near lightning strike triggered without thunder.");
        }
        return true;
    }

    internal void TriggerThunderNow(ThunderStyle style)
    {
        _thunderAudio.Play(0f, true, style);
    }

    internal bool TriggerImpactNow()
    {
        return TriggerNearStrikeNow(1f, true, true, true);
    }

    internal void TriggerVisionEffectNow()
    {
        _overlay.TriggerNearStrikeVisionEffectNow();
        var nativeStun = TriggerNativeVisionStun(_settings.NearStrikeDistanceMinimum.Value);
        _logger.LogInfo(
            $"Near-strike vision effect triggered from the debug menu; nativeStun={nativeStun}.");
    }

    internal bool TriggerTinnitusNow()
    {
        return TriggerNativeTinnitus(_settings.NearStrikeDistanceMinimum.Value);
    }

    internal void Reset()
    {
        _pendingThunder.Clear();
        _thunderAudio.Reset();
        _overlay.ResetEffects();
        _wasActive = false;
    }

    internal void Dispose()
    {
    }

    private void TriggerLightning(float stormStrength, bool scheduleThunder = true)
    {
        var useNearStrike = _settings.NearStrikeEnabled.Value
            && Random.Range(0f, 100f) < _settings.NearStrikeChance.Value;
        if (useNearStrike && TriggerNearStrikeNow(stormStrength, scheduleThunder))
        {
            return;
        }

        TriggerDistantLightning(stormStrength, scheduleThunder);
    }

    private void TriggerDistantLightning(float stormStrength, bool scheduleThunder)
    {
        var strong = Random.Range(0f, 100f) < _settings.StrongFlashChance.Value;
        var flashBrightness = (strong ? _settings.StrongFlashBrightness.Value : _settings.NormalFlashBrightness.Value) * stormStrength;
        _overlay.TriggerFlash(flashBrightness);
        if (strong
            && _settings.BodycamInterferenceEnabled.Value
            && Random.Range(0f, 100f) < _settings.StrongLightningInterferenceChance.Value)
        {
            _bodycamInterference.TryTrigger(
                _settings.StrongLightningInterferenceIntensity.Value,
                _settings.StrongLightningInterferenceDuration.Value);
        }
        var hasOrigin = _overlay.TryGetThunderOrigin(out var origin, out var distance);

        var minimumDelay = _settings.ThunderDelayMinimum.Value;
        var maximumDelay = _settings.ThunderDelayMaximum.Value;
        var delay = strong
            ? Random.Range(minimumDelay, Mathf.Lerp(minimumDelay, maximumDelay, 0.35f))
            : Random.Range(Mathf.Min(minimumDelay, maximumDelay), Mathf.Max(minimumDelay, maximumDelay));

        if (scheduleThunder)
        {
            ScheduleThunder(delay, strong, ThunderStyle.Random, hasOrigin, origin, distance);
            _logger.LogInfo($"Lightning triggered; thunder scheduled in {delay:0.00}s.");
        }
        else
        {
            _logger.LogInfo("Distant lightning triggered without scheduled thunder.");
        }
    }

    private void ScheduleThunder(float delay, bool strong, ThunderStyle style,
        bool hasOrigin, Vector3 origin, float distance)
    {
        _pendingThunder.Add(new PendingThunder
        {
            PlayAt = Time.unscaledTime + delay,
            Delay = delay,
            Strong = strong,
            Style = style,
            HasOrigin = hasOrigin,
            Origin = origin,
            Distance = distance,
        });
    }

    private void UpdateThunder()
    {
        for (var index = _pendingThunder.Count - 1; index >= 0; index--)
        {
            var pending = _pendingThunder[index];
            if (Time.unscaledTime < pending.PlayAt)
            {
                continue;
            }

            _thunderAudio.Play(pending.Delay, pending.Strong, pending.Style,
                pending.HasOrigin ? pending.Origin : (Vector3?)null,
                pending.Distance);
            _pendingThunder.RemoveAt(index);
        }
    }

    private void ScheduleNextLightning()
    {
        var minimum = Mathf.Min(_settings.LightningIntervalMinimum.Value, _settings.LightningIntervalMaximum.Value);
        var maximum = Mathf.Max(_settings.LightningIntervalMinimum.Value, _settings.LightningIntervalMaximum.Value);
        if (WeatherSeason.IsWinter)
        {
            var frequency = Mathf.Clamp(_settings.ThundersnowFrequency.Value / 100f, 0.05f, 1f);
            minimum /= frequency;
            maximum /= frequency;
        }
        _nextLightningAt = Time.unscaledTime + Random.Range(minimum, maximum);
    }

    private bool TriggerNativeTinnitus(float distance)
    {
        if (!Singleton<GameWorld>.Instantiated)
        {
            return false;
        }

        var player = Singleton<GameWorld>.Instance?.MainPlayer;
        if (player == null)
        {
            return false;
        }

        var healthController = player.ActiveHealthController;
        if (healthController == null)
        {
            _logger.LogWarning("Native tinnitus was not started because the local health controller is unavailable.");
            return false;
        }

        var tinnitusClip = TinnitusClipField?.GetValue(player) as AudioClip;
        if (tinnitusClip == null)
        {
            _logger.LogWarning("Native tinnitus was not started because Tarkov's player tinnitus clip is unavailable.");
            return false;
        }

        if (healthController.FindActiveEffect<ActiveHealthController.Stun>(EBodyPart.Common) == null
            && !EnsureTinnitusStun(player))
        {
            _logger.LogWarning("Native tinnitus was not started because SPT's required common-body stun could not be created.");
            return false;
        }

        if (!Singleton<BetterAudio>.Instantiated)
        {
            _logger.LogWarning("Native tinnitus was not started because BetterAudio is unavailable.");
            return false;
        }

        var duration = _settings.NearStrikeTinnitusDuration.Value;
        Singleton<BetterAudio>.Instance.StartTinnitusEffect(duration * 0.5f, tinnitusClip);
        var commonStunActive = healthController
            .FindActiveEffect<ActiveHealthController.Stun>(EBodyPart.Common) != null;
        _logger.LogInfo(
            $"Native near-strike tinnitus started: clip={tinnitusClip.name}, duration={duration:0.0}s, "
            + $"distance={distance:0}m, commonStun={commonStunActive}.");
        return true;
    }

    private bool TriggerNativeVisionStun(float distance)
    {
        if (!Singleton<GameWorld>.Instantiated)
        {
            return false;
        }

        var player = Singleton<GameWorld>.Instance?.MainPlayer;
        if (player == null)
        {
            return false;
        }

        var healthController = player.ActiveHealthController;
        if (healthController == null)
        {
            return false;
        }

        var existingStun = healthController
            .FindActiveEffect<ActiveHealthController.Stun>(EBodyPart.Head);
        if (existingStun != null)
        {
            return true;
        }

        var closeness = Mathf.InverseLerp(
            _settings.NearStrikeDistanceMaximum.Value,
            _settings.NearStrikeDistanceMinimum.Value,
            distance);
        var visionScale = Mathf.Clamp(_settings.NearStrikeVisionStrength.Value / 100f, 0.25f, 2f);
        var effectTime = Mathf.Lerp(0.55f, 1.1f, closeness)
            * Mathf.Clamp(_settings.NearStrikeVisionDuration.Value / 2.2f, 0.5f, 2f);
        var strength = Mathf.Clamp(
            Mathf.Lerp(0.12f, 0.24f, closeness) * visionScale,
            0.05f,
            0.4f);

        healthController.DoStun(effectTime, strength);

        var created = healthController
            .FindActiveEffect<ActiveHealthController.Stun>(EBodyPart.Head) != null;
        _logger.LogInfo(
            $"Native near-strike vision stun created: strength={strength:0.000}, "
            + $"effectTime={effectTime:0.00}s, headActive={created}.");
        return created;
    }

    private bool EnsureTinnitusStun(Player player)
    {
        var healthController = player.ActiveHealthController;
        if (healthController == null)
        {
            return false;
        }

        var existingStun = healthController
            .FindActiveEffect<ActiveHealthController.Stun>(EBodyPart.Common);
        if (existingStun != null)
        {
            return true;
        }

        const float strength = 0.001f;
        const float effectTime = 0.05f;
        var workTime = ActiveHealthController.Stun.StunBuildUpTime
            + effectTime * strength * player.BlindnessDuration;
        healthController.AddEffect<ActiveHealthController.Stun>(
            EBodyPart.Common,
            0f,
            workTime,
            null,
            strength,
            null);

        var created = healthController
            .FindActiveEffect<ActiveHealthController.Stun>(EBodyPart.Common) != null;
        _logger.LogInfo(
            $"SPT tinnitus prerequisite created: strength={strength:0.000}, "
            + $"workTime={workTime:0.00}s, commonBodyActive={created}.");
        return created;
    }
}
