using EFT.Weather;
using UnityEngine;

namespace SenzeExtremeWeather;

internal sealed class SnowEnhancer
{
    private SnowFlakes _snowFlakes;
    private float _originalStormFactor;
    private float _originalStormSideSpeed;
    private Vector2 _originalSideSpeed;
    private Vector2 _originalMinimumSize;
    private Vector2 _originalMaximumSize;

    internal void Apply(WeatherController weatherController, float stormStrength,
        float extraSnowPercent, int windVisualBoostPercent)
    {
        var snowFlakes = weatherController?.RainController?._snowFlakes;
        if (snowFlakes == null)
        {
            Restore();
            return;
        }

        if (_snowFlakes != snowFlakes)
        {
            Restore();
            _snowFlakes = snowFlakes;
            _originalStormFactor = snowFlakes.StormFactor;
            _originalStormSideSpeed = snowFlakes.StormSideSpeed;
            _originalSideSpeed = snowFlakes.MinMaxSideSpeed;
            _originalMinimumSize = snowFlakes.FlakesSizeMin;
            _originalMaximumSize = snowFlakes.FlakesSizeMax;
        }

        var strength = Mathf.Clamp01(stormStrength);
        var snowAmount = Mathf.Clamp(extraSnowPercent, 0f, 200f) / 100f * strength;
        var gust = Mathf.Lerp(0.82f, 1f, Mathf.PerlinNoise(Time.unscaledTime * 0.18f, 0.37f));
        var windMultiplier = Mathf.Lerp(
            1f,
            Mathf.Clamp(windVisualBoostPercent / 100f, 1f, 2f),
            strength * gust);

        var enhancedStormFactor = snowAmount < 0.15f
            ? 0f
            : Mathf.InverseLerp(0.15f, 1f, snowAmount);
        if (snowAmount > 1f)
        {
            enhancedStormFactor += snowAmount - 1f;
        }

        snowFlakes.StormFactor = Mathf.Max(_originalStormFactor, enhancedStormFactor);
        snowFlakes.StormSideSpeed = _originalStormSideSpeed * windMultiplier;
        snowFlakes.MinMaxSideSpeed = _originalSideSpeed * windMultiplier;

        var sizeMultiplier = 1f + snowAmount * 0.18f;
        snowFlakes.FlakesSizeMin = _originalMinimumSize * sizeMultiplier;
        snowFlakes.FlakesSizeMax = _originalMaximumSize * sizeMultiplier;
    }

    internal void Restore()
    {
        if (_snowFlakes == null)
        {
            return;
        }

        _snowFlakes.StormFactor = _originalStormFactor;
        _snowFlakes.StormSideSpeed = _originalStormSideSpeed;
        _snowFlakes.MinMaxSideSpeed = _originalSideSpeed;
        _snowFlakes.FlakesSizeMin = _originalMinimumSize;
        _snowFlakes.FlakesSizeMax = _originalMaximumSize;
        _snowFlakes = null;
    }
}
