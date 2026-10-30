using System.Reflection;
using EFT.Weather;
using HarmonyLib;
using UnityEngine;

namespace SenzeExtremeWeather;

internal sealed class WindEnhancer
{
    private static readonly int BillboardTreesWindPower = Shader.PropertyToID("_BilboardTreesWindPower");
    private static readonly int WindVector = Shader.PropertyToID("_WindVector");
    private static readonly int SpeedTreeWindVector = Shader.PropertyToID("_ST_WindVector");
    private static readonly FieldInfo WindCalculatorField =
        AccessTools.Field(typeof(WindController), "_windCalculator");

    private WeatherController _weatherController;
    private WindController _controller;
    private float _originalRainWindMultiplier;
    private float _originalCloudWindMultiplier;
    private float _originalBillboardTreesWindMultiplier;
    private float _currentMultiplier = 1f;

    internal void Apply(WeatherController weatherController, float stormStrength,
        int visualBoostPercent, bool winter)
    {
        var controller = weatherController ? weatherController.WindController : null;
        if (controller == null)
        {
            Restore();
            return;
        }

        if (_controller != controller)
        {
            Restore();
            _weatherController = weatherController;
            _controller = controller;
            _originalRainWindMultiplier = controller.RainWindMultiplier;
            _originalCloudWindMultiplier = controller.CloudWindMultiplier;
            _originalBillboardTreesWindMultiplier = controller.BilboardTreesWindMultiplier;
        }

        var maximumMultiplier = Mathf.Clamp(visualBoostPercent / 100f, 1f, 2f);
        var gust = Mathf.Lerp(0.84f, 1f, Mathf.PerlinNoise(Time.unscaledTime * 0.14f, 0.73f));
        var multiplier = Mathf.Lerp(
            1f,
            maximumMultiplier,
            Mathf.Clamp01(stormStrength) * gust);
        _currentMultiplier = multiplier;
        controller.RainWindMultiplier = winter
            ? _originalRainWindMultiplier
            : _originalRainWindMultiplier * multiplier;
        controller.CloudWindMultiplier = _originalCloudWindMultiplier * multiplier;
        controller.BilboardTreesWindMultiplier = _originalBillboardTreesWindMultiplier * multiplier;
        Shader.SetGlobalFloat(BillboardTreesWindPower, controller.BilboardTreesWindMultiplier);
        ApplyShaderWind(weatherController, multiplier, false);
    }

    internal void Restore()
    {
        if (_controller == null)
        {
            return;
        }

        var weatherController = _weatherController;
        _controller.RainWindMultiplier = _originalRainWindMultiplier;
        _controller.CloudWindMultiplier = _originalCloudWindMultiplier;
        _controller.BilboardTreesWindMultiplier = _originalBillboardTreesWindMultiplier;
        Shader.SetGlobalFloat(BillboardTreesWindPower, _originalBillboardTreesWindMultiplier);
        ApplyShaderWind(weatherController, 1f, true);
        _weatherController = null;
        _controller = null;
        _currentMultiplier = 1f;
    }

    internal void ReapplyShaderWind()
    {
        if (_controller != null && _weatherController && _currentMultiplier > 1f)
        {
            ApplyShaderWind(_weatherController, _currentMultiplier, false);
        }
    }

    private static void ApplyShaderWind(WeatherController weatherController, float multiplier,
        bool clampToNativeMaximum)
    {
        var weatherCurve = weatherController ? weatherController.WeatherCurve : null;
        if (weatherCurve == null)
        {
            return;
        }

        var wind = weatherCurve.Wind;
        var magnitude = wind.magnitude;
        var direction = magnitude < 0.0001f ? Vector2.one : wind.normalized;
        var windPower = magnitude * multiplier;
        if (clampToNativeMaximum)
        {
            windPower = Mathf.Clamp(windPower, 0.0001f, 0.99999f);
        }

        var enhancedWind = direction * windPower;
        var windController = weatherController.WindController;
        var windCalculator = WindCalculatorField?.GetValue(windController) as WindCalculator;
        windCalculator?.SetWind(enhancedWind);
        Shader.SetGlobalVector(WindVector, enhancedWind);
        Shader.SetGlobalVector(
            SpeedTreeWindVector,
            new Vector4(direction.x, 0f, direction.y, windPower));
    }
}

[HarmonyPatch(typeof(WindController), nameof(WindController.ApplyWindToPlants))]
internal static class WindPlantShaderPatch
{
    [HarmonyPostfix]
    private static void Postfix()
    {
        Plugin.Instance?.ReapplyEnhancedWind();
    }
}
