using System;
using BepInEx.Logging;
using Comfort.Common;
using EFT;
using EFT.Weather;
using UnityEngine;

namespace SenzeExtremeWeather;

internal struct ControlledWeather
{
    internal bool DebugEnabled;
    internal float CloudDensity;
    internal float Fog;
    internal float Rain;
    internal float ThunderProbability;
    internal float Temperature;
    internal float WindMagnitude;
    internal WeatherDebug.Direction WindDirection;
    internal Vector2 TopWindDirection;

    internal static ControlledWeather Clear()
    {
        return Create(-0.65f, 0f, 0f, 0f, 16f, 0.12f);
    }

    internal static ControlledWeather Overcast()
    {
        return Create(1f, 0.08f, 0f, 0f, 9f, 0.42f);
    }

    internal static ControlledWeather Rainy()
    {
        return Create(1f, 0.12f, 0.7f, 0.18f, 7f, 0.68f);
    }

    internal static ControlledWeather Foggy()
    {
        return Create(0.7f, 0.35f, 0.08f, 0f, 8f, 0.12f);
    }

    internal static ControlledWeather Snowy()
    {
        return Create(1f, 0.16f, 0.82f, 0.12f, -14f, 0.58f);
    }

    internal static ControlledWeather ExtremeStorm()
    {
        return Create(1f, 0.35f, 1f, 1f, 2f, 1f);
    }

    internal static ControlledWeather FromCurve(IWeatherCurve curve)
    {
        return new ControlledWeather
        {
            DebugEnabled = true,
            CloudDensity = curve.Cloudiness,
            Fog = curve.Fog,
            Rain = curve.Rain,
            ThunderProbability = 0f,
            Temperature = curve.Temperature,
            WindMagnitude = Mathf.Clamp01(curve.Wind.magnitude),
            WindDirection = FindClosestDirection(curve.Wind),
            TopWindDirection = curve.TopWind,
        };
    }

    internal static ControlledWeather Lerp(ControlledWeather start, ControlledWeather end, float amount)
    {
        amount = Mathf.Clamp01(amount);
        return new ControlledWeather
        {
            DebugEnabled = true,
            CloudDensity = Mathf.Lerp(start.CloudDensity, end.CloudDensity, amount),
            Fog = Mathf.Lerp(start.Fog, end.Fog, amount),
            Rain = Mathf.Lerp(start.Rain, end.Rain, amount),
            ThunderProbability = Mathf.Lerp(start.ThunderProbability, end.ThunderProbability, amount),
            Temperature = Mathf.Lerp(start.Temperature, end.Temperature, amount),
            WindMagnitude = Mathf.Lerp(start.WindMagnitude, end.WindMagnitude, amount),
            WindDirection = amount < 0.35f ? start.WindDirection : end.WindDirection,
            TopWindDirection = Vector2.Lerp(start.TopWindDirection, end.TopWindDirection, amount),
        };
    }

    private static ControlledWeather Create(float cloudDensity, float fog, float rain,
        float thunderProbability, float temperature, float windMagnitude)
    {
        return new ControlledWeather
        {
            DebugEnabled = true,
            CloudDensity = cloudDensity,
            Fog = fog,
            Rain = rain,
            ThunderProbability = thunderProbability,
            Temperature = temperature,
            WindMagnitude = windMagnitude,
            WindDirection = WeatherDebug.Direction.North,
            TopWindDirection = Vector2.down,
        };
    }

    private static WeatherDebug.Direction FindClosestDirection(Vector2 wind)
    {
        if (wind.sqrMagnitude < 0.0001f)
        {
            return WeatherDebug.Direction.North;
        }

        var normalizedWind = wind.normalized;
        var closestDirection = WeatherDebug.Direction.North;
        var closestDot = float.MinValue;
        for (var index = 1; index < WeatherNode.WindDirections.Length; index++)
        {
            var dot = Vector2.Dot(normalizedWind, WeatherNode.WindDirections[index]);
            if (dot > closestDot)
            {
                closestDot = dot;
                closestDirection = (WeatherDebug.Direction)index;
            }
        }

        return closestDirection;
    }
}

internal sealed class WeatherControlService
{
    private enum AutomaticStormPhase
    {
        None,
        WaitingForWeather,
        DevelopmentDelay,
        Building,
        ThunderDelay,
        Peak,
        Complete,
    }

    private const float WeatherCheckInterval = 2f;
    private const float ManualWeatherRollDelay = 2f;
    private const float DevelopmentDelayMinimumSeconds = 180f;
    private const float DevelopmentDelayMaximumSeconds = 240f;
    private const float BuildUpMinimumSeconds = 180f;
    private const float BuildUpMaximumSeconds = 300f;
    private const float ThunderDelaySeconds = 120f;

    private readonly ExtremeWeatherConfig _settings;
    private readonly ManualLogSource _logger;
    private readonly Action _activateExtremeEffects;
    private ControlledWeather _restoreWeather;
    private ControlledWeather _phaseStartWeather;
    private ControlledWeather _phaseTargetWeather;
    private AutomaticStormPhase _automaticPhase;
    private bool _automaticRollResolved;
    private bool _manualPrecipitationPending;
    private bool _hasRestoreWeather;
    private bool _automaticMonitorLogged;
    private float _phaseStartedAt;
    private float _phaseEndsAt;

    internal WeatherControlService(
        ExtremeWeatherConfig settings,
        ManualLogSource logger,
        Action activateExtremeEffects)
    {
        _settings = settings;
        _logger = logger;
        _activateExtremeEffects = activateExtremeEffects;
    }

    internal string AutomaticStormStatus { get; private set; } = "Waiting for raid";
    internal float AutomaticEffectStrength { get; private set; }
    internal bool AutomaticSequenceControlsEffects =>
        (_automaticPhase == AutomaticStormPhase.WaitingForWeather && _manualPrecipitationPending)
        || _automaticPhase == AutomaticStormPhase.DevelopmentDelay
        || _automaticPhase == AutomaticStormPhase.Building
        || _automaticPhase == AutomaticStormPhase.ThunderDelay
        || _automaticPhase == AutomaticStormPhase.Peak;
    internal bool AutomaticThunderActive => _automaticPhase == AutomaticStormPhase.Peak;
    internal bool MaximumStormIntensityActive =>
        _automaticPhase == AutomaticStormPhase.ThunderDelay
        || _automaticPhase == AutomaticStormPhase.Peak;

    internal bool TryReadCurrent(out ControlledWeather state)
    {
        state = default;
        var controller = WeatherController.Instance;
        var debug = controller ? controller.WeatherDebug : null;
        if (debug == null)
        {
            return false;
        }

        state.DebugEnabled = debug.Enabled;
        state.CloudDensity = debug.CloudDensity;
        state.Fog = debug.Fog;
        state.Rain = debug.Rain;
        state.ThunderProbability = debug.LightningThunderProbability;
        state.Temperature = debug.Temperature;
        state.WindMagnitude = debug.WindMagnitude;
        state.WindDirection = debug.WindDirection;
        state.TopWindDirection = debug.TopWindDirection;
        return true;
    }

    internal bool TryReadVisibleWeather(out ControlledWeather state)
    {
        if (TryReadCurrent(out state) && state.DebugEnabled)
        {
            OverrideWithRenderedPrecipitation(ref state);
            return true;
        }

        return TryReadNativeWeather(out state);
    }

    internal bool Apply(ControlledWeather state, bool rememberCurrent = true,
        bool preserveAutomaticStorm = false)
    {
        var automaticStormWasRunning = IsAutomaticStormRunning();
        if (automaticStormWasRunning && !preserveAutomaticStorm)
        {
            _automaticPhase = AutomaticStormPhase.Complete;
            _manualPrecipitationPending = false;
            AutomaticEffectStrength = 0f;
            AutomaticStormStatus = "Automatic storm stopped by manual weather";
            _logger.LogInfo("Automatic storm stopped by a manual weather preset.");
        }

        var applied = ApplyState(state, rememberCurrent);
        if (applied && !automaticStormWasRunning)
        {
            ScheduleManualPrecipitationRoll(state);
        }

        return applied;
    }

    internal bool ApplyExtremeStorm()
    {
        _automaticRollResolved = true;
        _manualPrecipitationPending = false;
        _automaticPhase = AutomaticStormPhase.Complete;
        AutomaticEffectStrength = 0f;
        if (!ApplyState(ControlledWeather.ExtremeStorm(), true))
        {
            return false;
        }

        AutomaticStormStatus = "Manual extreme storm";
        _activateExtremeEffects();
        return true;
    }

    internal bool RestoreDynamicWeather()
    {
        _manualPrecipitationPending = false;
        AutomaticEffectStrength = 0f;
        var restored = RestoreDynamicWeatherState();
        if (_settings.Enabled.Value
            && _settings.AutomaticStormsEnabled.Value
            && !_automaticRollResolved)
        {
            _automaticPhase = AutomaticStormPhase.WaitingForWeather;
            _phaseEndsAt = Time.unscaledTime + WeatherCheckInterval;
            AutomaticStormStatus = "Waiting for rain or snow";
        }
        else
        {
            _automaticPhase = AutomaticStormPhase.Complete;
            AutomaticStormStatus = "Dynamic weather";
        }

        return restored;
    }

    internal void BeginRaid()
    {
        _hasRestoreWeather = false;
        _automaticRollResolved = false;
        _manualPrecipitationPending = false;
        _automaticMonitorLogged = false;
        AutomaticEffectStrength = 0f;
        if (!_settings.Enabled.Value || !_settings.AutomaticStormsEnabled.Value)
        {
            _automaticPhase = AutomaticStormPhase.Complete;
            AutomaticStormStatus = "Automatic storms disabled";
            return;
        }

        _automaticPhase = AutomaticStormPhase.WaitingForWeather;
        _phaseEndsAt = Time.unscaledTime + WeatherCheckInterval;
        AutomaticStormStatus = "Waiting for rain or snow";
    }

    internal void UpdateAutomaticStorm()
    {
        if (_automaticPhase == AutomaticStormPhase.None
            || _automaticPhase == AutomaticStormPhase.Complete)
        {
            return;
        }

        if (!_settings.Enabled.Value || !_settings.AutomaticStormsEnabled.Value)
        {
            if (_automaticPhase == AutomaticStormPhase.Building
                || _automaticPhase == AutomaticStormPhase.ThunderDelay
                || _automaticPhase == AutomaticStormPhase.Peak)
            {
                RestoreDynamicWeatherState();
            }

            _automaticPhase = AutomaticStormPhase.Complete;
            AutomaticEffectStrength = 0f;
            AutomaticStormStatus = "Automatic storms disabled";
            return;
        }

        switch (_automaticPhase)
        {
            case AutomaticStormPhase.WaitingForWeather:
                EvaluateInitialWeather();
                break;
            case AutomaticStormPhase.DevelopmentDelay:
                UpdateDevelopmentDelay();
                break;
            case AutomaticStormPhase.Building:
                UpdateBuildUp();
                break;
            case AutomaticStormPhase.ThunderDelay:
                UpdateThunderDelay();
                break;
            case AutomaticStormPhase.Peak:
                UpdatePeak();
                break;
        }
    }

    internal bool SetTime(int hour, int minute)
    {
        if (!Singleton<GameWorld>.Instantiated)
        {
            return false;
        }

        var gameWorld = Singleton<GameWorld>.Instance;
        var gameDateTime = gameWorld?.GameDateTime;
        if (gameDateTime == null)
        {
            return false;
        }

        var current = gameDateTime.Calculate();
        var target = new DateTime(
            current.Year,
            current.Month,
            current.Day,
            Mathf.Clamp(hour, 0, 23),
            Mathf.Clamp(minute, 0, 59),
            0,
            current.Kind);

        var controller = WeatherController.Instance;
        if (controller && controller.WeatherDebug != null)
        {
            if (controller.WeatherDebug.Date == null)
            {
                controller.WeatherDebug.Date = new TOD_CycleParameters();
            }
            controller.WeatherDebug.Date.DateTime = target;
            controller.SetDebugDate();
        }

        gameDateTime.ResetForce(target);
        if (TOD_Sky.Instantiated && TOD_Sky.Instance?.Cycle != null)
        {
            TOD_Sky.Instance.Cycle.DateTime = target;
        }
        return true;
    }

    internal bool TryGetCurrentTime(out DateTime current)
    {
        current = default;
        if (!Singleton<GameWorld>.Instantiated)
        {
            return false;
        }

        var gameDateTime = Singleton<GameWorld>.Instance?.GameDateTime;
        if (gameDateTime == null)
        {
            return false;
        }

        current = gameDateTime.Calculate();
        return true;
    }

    internal void ResetRaidState()
    {
        _hasRestoreWeather = false;
        _automaticRollResolved = false;
        _manualPrecipitationPending = false;
        _automaticMonitorLogged = false;
        AutomaticEffectStrength = 0f;
        _automaticPhase = AutomaticStormPhase.None;
        AutomaticStormStatus = "Waiting for raid";
    }

    private void EvaluateInitialWeather()
    {
        if (Time.unscaledTime < _phaseEndsAt)
        {
            return;
        }

        _phaseEndsAt = Time.unscaledTime + WeatherCheckInterval;

        if (_manualPrecipitationPending)
        {
            _manualPrecipitationPending = false;
            if (TryReadCurrent(out var manualWeather)
                && manualWeather.DebugEnabled
                && manualWeather.Rain > 0f)
            {
                RollAutomaticStorm(manualWeather, "manual precipitation");
            }
            return;
        }

        var controller = WeatherController.Instance;
        if (!controller || controller.WeatherDebug == null || controller.WeatherCurve == null)
        {
            AutomaticStormStatus = "Waiting for weather controller";
            return;
        }

        if (!TryReadVisibleWeather(out var currentWeather))
        {
            AutomaticStormStatus = "Waiting for visible weather";
            return;
        }

        if (!_automaticMonitorLogged)
        {
            var source = controller.WeatherDebug.Enabled ? "controlled" : "dynamic";
            _logger.LogInfo(
                $"Automatic storm monitor active: source={source}, rendered precipitation={currentWeather.Rain:0.000}, curve precipitation={controller.WeatherCurve.Rain:0.000}.");
            _automaticMonitorLogged = true;
        }

        if (currentWeather.Rain <= 0f)
        {
            AutomaticStormStatus = "Waiting for rain or snow";
            return;
        }

        var precipitationSource = controller.WeatherDebug.Enabled
            ? "controlled precipitation"
            : "natural precipitation";
        RollAutomaticStorm(currentWeather, precipitationSource);
    }

    private void UpdateDevelopmentDelay()
    {
        AutomaticEffectStrength = 0f;
        AutomaticStormStatus = $"Storm selected - intensification in {FormatRemainingTime()}";
        if (Time.unscaledTime < _phaseEndsAt)
        {
            return;
        }

        if (TryReadVisibleWeather(out var visibleWeather))
        {
            _phaseStartWeather = visibleWeather;
        }

        _phaseStartWeather.DebugEnabled = true;
        _phaseTargetWeather = CreateAutomaticStormTarget(_phaseStartWeather);
        _phaseStartedAt = Time.unscaledTime;
        _phaseEndsAt = _phaseStartedAt
            + UnityEngine.Random.Range(BuildUpMinimumSeconds, BuildUpMaximumSeconds);
        _automaticPhase = AutomaticStormPhase.Building;
        ApplyState(_phaseStartWeather, false);
        AutomaticStormStatus = $"{Capitalize(WeatherSeason.PrecipitationName)} and wind intensifying";
        _logger.LogInfo(
            $"Automatic {(WeatherSeason.IsWinter ? "snowstorm" : "rainstorm")} entered the build-up phase; "
            + $"duration={(_phaseEndsAt - _phaseStartedAt):0}s.");
    }

    private void UpdateBuildUp()
    {
        var progress = PhaseProgress();
        var smoothed = Mathf.SmoothStep(0f, 1f, progress);
        var buildingWeather = ControlledWeather.Lerp(_phaseStartWeather, _phaseTargetWeather, smoothed);
        buildingWeather.ThunderProbability = 0f;
        ApplyState(buildingWeather, false);
        AutomaticEffectStrength = smoothed;
        AutomaticStormStatus =
            $"{Capitalize(WeatherSeason.PrecipitationName)} and wind intensifying {Mathf.RoundToInt(progress * 100f)}%";

        if (progress < 1f)
        {
            return;
        }

        _phaseStartedAt = Time.unscaledTime;
        _phaseEndsAt = _phaseStartedAt + ThunderDelaySeconds;
        _automaticPhase = AutomaticStormPhase.ThunderDelay;
        AutomaticEffectStrength = 1f;
        var stormWithoutThunder = _phaseTargetWeather;
        stormWithoutThunder.ThunderProbability = 0f;
        ApplyState(stormWithoutThunder, false);
        AutomaticStormStatus = "Severe storm - thunder approaching";
        _logger.LogInfo("Automatic storm reached full precipitation and wind; thunder delay started for 120s.");
    }

    private void UpdateThunderDelay()
    {
        var stormWithoutThunder = _phaseTargetWeather;
        stormWithoutThunder.ThunderProbability = 0f;
        ApplyState(stormWithoutThunder, false);
        AutomaticEffectStrength = 1f;
        AutomaticStormStatus = $"Severe storm - thunder in {FormatRemainingTime()}";
        if (Time.unscaledTime < _phaseEndsAt)
        {
            return;
        }

        _automaticPhase = AutomaticStormPhase.Peak;
        ApplyState(_phaseTargetWeather, false);
        AutomaticStormStatus = "Full extreme storm until raid end";
        _activateExtremeEffects();
        _logger.LogInfo(
            $"Automatic {(WeatherSeason.IsWinter ? "thundersnow" : "thunderstorm")} entered the peak phase and will remain active until raid end.");
    }

    private void UpdatePeak()
    {
        ApplyState(_phaseTargetWeather, false);
        AutomaticEffectStrength = 1f;
        AutomaticStormStatus = "Full extreme storm until raid end";
    }

    private bool TryReadNativeWeather(out ControlledWeather state)
    {
        state = default;
        var controller = WeatherController.Instance;
        if (!controller || controller.WeatherCurve == null)
        {
            return false;
        }

        state = ControlledWeather.FromCurve(controller.WeatherCurve);
        OverrideWithRenderedPrecipitation(ref state);
        return true;
    }

    private static void OverrideWithRenderedPrecipitation(ref ControlledWeather state)
    {
        var controller = WeatherController.Instance;
        if (controller && controller.RainController != null)
        {
            state.Rain = Mathf.Clamp01(RainController.Intensity);
        }
    }

    private bool ApplyState(ControlledWeather state, bool rememberCurrent)
    {
        var controller = WeatherController.Instance;
        var debug = controller ? controller.WeatherDebug : null;
        if (debug == null)
        {
            return false;
        }

        if (rememberCurrent)
        {
            CaptureRestoreWeather();
        }

        debug.CloudDensity = Mathf.Clamp(state.CloudDensity, -1f, 1f);
        debug.Fog = Mathf.Clamp(state.Fog, 0f, 0.35f);
        debug.Rain = Mathf.Clamp01(state.Rain);
        debug.LightningThunderProbability = Mathf.Clamp01(state.ThunderProbability);
        debug.Temperature = Mathf.Clamp(state.Temperature, -50f, 50f);
        debug.WindMagnitude = Mathf.Clamp01(state.WindMagnitude);
        debug.WindDirection = state.WindDirection;
        debug.TopWindDirection = state.TopWindDirection;
        debug.Enabled = state.DebugEnabled;
        return true;
    }

    private void CaptureRestoreWeather()
    {
        if (!_hasRestoreWeather && TryReadCurrent(out var current))
        {
            _restoreWeather = current;
            _hasRestoreWeather = true;
        }
    }

    private bool RestoreDynamicWeatherState()
    {
        if (_hasRestoreWeather)
        {
            var restored = ApplyState(_restoreWeather, false);
            _hasRestoreWeather = false;
            return restored;
        }

        var controller = WeatherController.Instance;
        var debug = controller ? controller.WeatherDebug : null;
        if (debug == null)
        {
            return false;
        }

        debug.Enabled = false;
        return true;
    }

    private void ScheduleManualPrecipitationRoll(ControlledWeather state)
    {
        if (!_settings.Enabled.Value || !_settings.AutomaticStormsEnabled.Value)
        {
            _automaticPhase = AutomaticStormPhase.Complete;
            AutomaticEffectStrength = 0f;
            AutomaticStormStatus = "Manual weather active";
            return;
        }

        if (_automaticRollResolved)
        {
            _automaticPhase = AutomaticStormPhase.Complete;
            AutomaticEffectStrength = 0f;
            AutomaticStormStatus = "Manual weather active";
            return;
        }

        if (state.Rain <= 0f)
        {
            _manualPrecipitationPending = false;
            _automaticPhase = AutomaticStormPhase.Complete;
            AutomaticEffectStrength = 0f;
            AutomaticStormStatus = "Manual weather active";
            return;
        }

        _manualPrecipitationPending = true;
        _automaticPhase = AutomaticStormPhase.WaitingForWeather;
        _phaseEndsAt = Time.unscaledTime + ManualWeatherRollDelay;
        AutomaticEffectStrength = 0f;
        AutomaticStormStatus = "Manual rain detected - storm roll pending";
    }

    private void RollAutomaticStorm(ControlledWeather currentWeather, string source)
    {
        _automaticRollResolved = true;
        var roll = UnityEngine.Random.Range(0f, 100f);
        var chance = Mathf.Clamp(_settings.AutomaticStormChance.Value, 0f, 100f);
        if (chance < 100f && roll >= chance)
        {
            _automaticPhase = AutomaticStormPhase.Complete;
            AutomaticEffectStrength = 0f;
            AutomaticStormStatus = "Rain detected, storm not selected";
            _logger.LogInfo(
                $"Automatic storm skipped after {source}: roll {roll:0.0}, chance {chance:0.0}%.");
            return;
        }

        CaptureRestoreWeather();
        _phaseStartWeather = currentWeather;
        _phaseTargetWeather = CreateAutomaticStormTarget(currentWeather);
        _phaseStartedAt = Time.unscaledTime;
        _phaseEndsAt = _phaseStartedAt
            + UnityEngine.Random.Range(DevelopmentDelayMinimumSeconds, DevelopmentDelayMaximumSeconds);
        _automaticPhase = AutomaticStormPhase.DevelopmentDelay;
        AutomaticEffectStrength = 0f;
        AutomaticStormStatus = "Storm selected - rain remains steady";
        _logger.LogInfo(
            $"Automatic {(WeatherSeason.IsWinter ? "snowstorm" : "rainstorm")} selected after {source}: "
            + $"roll {roll:0.0}, chance {chance:0.0}%. Development delay={(_phaseEndsAt - _phaseStartedAt):0}s.");
    }

    private bool IsAutomaticStormRunning()
    {
        return _automaticPhase == AutomaticStormPhase.DevelopmentDelay
            || _automaticPhase == AutomaticStormPhase.Building
            || _automaticPhase == AutomaticStormPhase.ThunderDelay
            || _automaticPhase == AutomaticStormPhase.Peak;
    }

    private static ControlledWeather CreateAutomaticStormTarget(ControlledWeather initialWeather)
    {
        var target = ControlledWeather.ExtremeStorm();
        target.Temperature = initialWeather.Temperature;
        var direction = UnityEngine.Random.Range(1, WeatherNode.WindDirections.Length);
        target.WindDirection = (WeatherDebug.Direction)direction;
        target.TopWindDirection = WeatherNode.WindDirections[direction];
        return target;
    }

    private float PhaseProgress()
    {
        return Mathf.InverseLerp(_phaseStartedAt, _phaseEndsAt, Time.unscaledTime);
    }

    private string FormatRemainingTime()
    {
        var totalSeconds = Mathf.Max(0, Mathf.CeilToInt(_phaseEndsAt - Time.unscaledTime));
        return $"{totalSeconds / 60}:{totalSeconds % 60:00}";
    }

    private static string Capitalize(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        return char.ToUpperInvariant(value[0]) + value.Substring(1);
    }
}
