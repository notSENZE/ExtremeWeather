using System;
using EFT.Weather;
using EFT.UI;
using UnityEngine;

namespace SenzeExtremeWeather;

internal sealed class WeatherControlMenu
{
    private const int WindowId = 0x53455A45;

    private readonly WeatherControlService _weatherControl;
    private readonly LightningEffects _lightningEffects;
    private readonly GUI.WindowFunction _drawWindow;
    private Rect _windowRect = new Rect(45f, 45f, 500f, 710f);
    private ControlledWeather _pendingWeather;
    private int _targetHour = 12;
    private int _targetMinute;
    private bool _visible;
    private bool _loadedCurrentValues;
    private CursorLockMode _previousCursorLock;
    private bool _previousCursorVisible;
    private GameObject _raidInput;
    private bool _previousRaidInputState;
    private string _debugStatus = "Vision test works anywhere; full strike requires open sky.";

    internal WeatherControlMenu(WeatherControlService weatherControl, LightningEffects lightningEffects)
    {
        _weatherControl = weatherControl;
        _lightningEffects = lightningEffects;
        _drawWindow = DrawWindow;
    }

    internal bool IsVisible => _visible;

    internal void SetVisible(bool visible)
    {
        if (_visible == visible)
        {
            return;
        }

        _visible = visible;
        if (visible)
        {
            _previousCursorLock = Cursor.lockState;
            _previousCursorVisible = Cursor.visible;
            _raidInput = GameObject.Find("___Input");
            if (_raidInput)
            {
                _previousRaidInputState = _raidInput.activeSelf;
                _raidInput.SetActive(false);
            }

            CursorSwitcher.SetCursor(ECursorType.Idle);
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            LoadCurrentValues();
            return;
        }

        if (_raidInput)
        {
            _raidInput.SetActive(_previousRaidInputState);
        }
        _raidInput = null;

        CursorSwitcher.SetCursor(_previousCursorVisible
            ? ECursorType.Idle
            : ECursorType.Invisible);
        Cursor.lockState = _previousCursorLock;
        Cursor.visible = _previousCursorVisible;
    }

    internal void Draw()
    {
        if (!_visible)
        {
            return;
        }

        _windowRect = GUI.Window(WindowId, _windowRect, _drawWindow, "Senze - Extreme Weather");
    }

    internal void Reset()
    {
        SetVisible(false);
        _loadedCurrentValues = false;
        _raidInput = null;
        _debugStatus = "Vision test works anywhere; full strike requires open sky.";
    }

    private void LoadCurrentValues()
    {
        if (_weatherControl.TryReadVisibleWeather(out var weather))
        {
            _pendingWeather = weather;
        }
        else
        {
            _pendingWeather = ControlledWeather.Clear();
        }

        if (_weatherControl.TryGetCurrentTime(out var current))
        {
            _targetHour = current.Hour;
            _targetMinute = current.Minute;
        }
        _loadedCurrentValues = true;
    }

    private void DrawWindow(int windowId)
    {
        if (!_loadedCurrentValues)
        {
            LoadCurrentValues();
        }

        GUI.DragWindow(new Rect(0f, 0f, 440f, 24f));
        if (GUI.Button(new Rect(462f, 3f, 30f, 22f), "X"))
        {
            SetVisible(false);
            return;
        }

        GUI.Label(new Rect(18f, 32f, 180f, 22f), "Raid time");
        GUI.Label(new Rect(198f, 32f, 70f, 22f), $"{_targetHour:00}:{_targetMinute:00}");
        GUI.Label(new Rect(18f, 50f, 205f, 20f), "Hours");
        GUI.Label(new Rect(245f, 50f, 155f, 20f), "Minutes");
        _targetHour = Mathf.RoundToInt(GUI.HorizontalSlider(
            new Rect(18f, 72f, 205f, 18f), _targetHour, 0f, 23f));
        _targetMinute = Mathf.RoundToInt(GUI.HorizontalSlider(
            new Rect(245f, 72f, 155f, 18f), _targetMinute, 0f, 59f));
        if (GUI.Button(new Rect(408f, 48f, 72f, 30f), "SET"))
        {
            _weatherControl.SetTime(_targetHour, _targetMinute);
        }

        GUI.Box(new Rect(12f, 100f, 476f, 278f), "Manual weather - changes apply immediately");
        GUI.changed = false;
        _pendingWeather.CloudDensity = DrawSlider(128f, "Cloud density", _pendingWeather.CloudDensity, -1f, 1f);
        _pendingWeather.Fog = DrawSlider(168f, "Fog", _pendingWeather.Fog, 0f, 0.35f);
        var precipitationLabel = WeatherSeason.IsWinter ? "Snow intensity" : "Rain intensity";
        _pendingWeather.Rain = DrawSlider(208f, precipitationLabel, _pendingWeather.Rain, 0f, 1f);
        _pendingWeather.ThunderProbability = DrawSlider(248f, "Thunder", _pendingWeather.ThunderProbability, 0f, 1f);
        _pendingWeather.WindMagnitude = DrawSlider(288f, "Wind", _pendingWeather.WindMagnitude, 0f, 1f);
        _pendingWeather.Temperature = DrawSlider(328f, "Temperature", _pendingWeather.Temperature, -30f, 30f, " °C");

        var direction = Mathf.Clamp((int)_pendingWeather.WindDirection, 1, 8);
        GUI.Label(new Rect(26f, 352f, 120f, 20f), $"Direction: {(WeatherDebug.Direction)direction}");
        direction = Mathf.RoundToInt(GUI.HorizontalSlider(new Rect(150f, 357f, 320f, 16f), direction, 1f, 8f));
        _pendingWeather.WindDirection = (WeatherDebug.Direction)direction;
        _pendingWeather.TopWindDirection = DirectionToVector(direction);
        _pendingWeather.DebugEnabled = true;
        var manualWeatherChanged = GUI.changed;
        if (manualWeatherChanged)
        {
            _weatherControl.Apply(_pendingWeather, preserveAutomaticStorm: true);
        }

        GUI.Label(new Rect(18f, 388f, 120f, 22f), "Presets");
        if (GUI.Button(new Rect(18f, 414f, 88f, 30f), "CLEAR"))
        {
            ApplyPreset(ControlledWeather.Clear());
        }
        if (GUI.Button(new Rect(112f, 414f, 88f, 30f), "OVERCAST"))
        {
            ApplyPreset(ControlledWeather.Overcast());
        }
        if (GUI.Button(new Rect(206f, 414f, 88f, 30f), "RAIN"))
        {
            ApplyPreset(ControlledWeather.Rainy());
        }
        if (GUI.Button(new Rect(300f, 414f, 88f, 30f), "FOG"))
        {
            ApplyPreset(ControlledWeather.Foggy());
        }
        if (GUI.Button(new Rect(394f, 414f, 88f, 30f), "SNOW"))
        {
            ApplyPreset(ControlledWeather.Snowy());
        }

        if (GUI.Button(new Rect(18f, 460f, 464f, 38f), "RESTORE DYNAMIC WEATHER"))
        {
            _weatherControl.RestoreDynamicWeather();
            LoadCurrentValues();
        }
        if (GUI.Button(new Rect(18f, 510f, 464f, 48f), "EXTREME STORM - APPLY NOW"))
        {
            _pendingWeather = ControlledWeather.ExtremeStorm();
            _weatherControl.ApplyExtremeStorm();
        }

        GUI.Label(
            new Rect(18f, 564f, 464f, 22f),
            $"Automatic weather: {_weatherControl.AutomaticStormStatus}");

        GUI.Box(new Rect(12f, 592f, 476f, 104f), "Debug tests");
        if (GUI.Button(new Rect(18f, 620f, 148f, 34f), "TEST VISION"))
        {
            _lightningEffects.TriggerVisionEffectNow();
            _debugStatus = "Near-strike vision effect triggered.";
        }
        if (GUI.Button(new Rect(174f, 620f, 148f, 34f), "TEST TINNITUS"))
        {
            _debugStatus = _lightningEffects.TriggerTinnitusNow()
                ? "Native tinnitus requested; an earpiece may suppress it."
                : "No local player was available for tinnitus.";
        }
        if (GUI.Button(new Rect(330f, 620f, 152f, 34f), "TEST FULL STRIKE"))
        {
            _debugStatus = _lightningEffects.TriggerImpactNow()
                ? "Complete near strike triggered."
                : "No valid open-sky strike point was found.";
        }
        GUI.Label(new Rect(18f, 662f, 464f, 22f), _debugStatus);
    }

    private void ApplyPreset(ControlledWeather weather)
    {
        _pendingWeather = weather;
        _weatherControl.Apply(_pendingWeather);
    }

    private static float DrawSlider(float y, string label, float value,
        float minimum, float maximum, string suffix = "")
    {
        GUI.Label(new Rect(26f, y, 150f, 22f), $"{label}: {value:0.00}{suffix}");
        return GUI.HorizontalSlider(new Rect(180f, y + 5f, 290f, 16f), value, minimum, maximum);
    }

    private static Vector2 DirectionToVector(int direction)
    {
        var angle = (direction - 1) * 45f * Mathf.Deg2Rad;
        return new Vector2(Mathf.Sin(angle), Mathf.Cos(angle));
    }
}
