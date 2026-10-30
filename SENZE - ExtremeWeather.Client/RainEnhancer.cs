using EFT.Weather;
using UnityEngine;

namespace SenzeExtremeWeather;

internal sealed class RainEnhancer
{
    private RainController _rainController;
    private Vector2 _originalDensity;
    private float _originalDropAlpha;
    private float _originalSplashCount;
    private float _lastExtra = -1f;
    private float _lastMaximumDensityMultiplier = -1f;
    private bool _captured;

    internal void Apply(WeatherController weatherController, float stormStrength, float extraPercent,
        float maximumDensityMultiplier)
    {
        var rainController = weatherController?.RainController;
        if (rainController == null || rainController._rainFallDrops == null)
        {
            Restore();
            return;
        }

        if (_rainController != rainController)
        {
            Restore();
            _rainController = rainController;
            _originalDensity = rainController._rainFallDrops.MinMaxDensity;
            _originalDropAlpha = rainController._rainFallDrops.DropsAlphaClose;
            _originalSplashCount = rainController._rainSplashController != null
                ? rainController._rainSplashController._maxGeneratedParticlesInFrame
                : 0f;
            _captured = true;
            _lastExtra = -1f;
            _lastMaximumDensityMultiplier = -1f;
        }

        var extra = Mathf.Clamp(extraPercent, 0f, 200f) / 100f * Mathf.Clamp01(stormStrength);
        var densityLimit = Mathf.Clamp(maximumDensityMultiplier, 1f, 15f);
        if (Mathf.Approximately(extra, _lastExtra)
            && Mathf.Approximately(densityLimit, _lastMaximumDensityMultiplier))
        {
            return;
        }

        var density = _originalDensity;
        var densityProgress = Mathf.Clamp01(extra / 2f);
        density.y = _originalDensity.y * Mathf.Lerp(1f, densityLimit, densityProgress);
        rainController._rainFallDrops.MinMaxDensity = density;
        rainController._rainFallDrops.DropsAlphaClose = _originalDropAlpha * (1f + extra * 0.75f);
        rainController._rainFallDrops.OnScreenChange();
        if (rainController._rainSplashController != null)
        {
            rainController._rainSplashController._maxGeneratedParticlesInFrame =
                _originalSplashCount * (1f + extra * 0.35f);
        }
        _lastExtra = extra;
        _lastMaximumDensityMultiplier = densityLimit;
    }

    internal void Restore()
    {
        if (_captured && _rainController != null)
        {
            if (_rainController._rainFallDrops != null)
            {
                _rainController._rainFallDrops.MinMaxDensity = _originalDensity;
                _rainController._rainFallDrops.DropsAlphaClose = _originalDropAlpha;
                _rainController._rainFallDrops.OnScreenChange();
            }

            if (_rainController._rainSplashController != null)
            {
                _rainController._rainSplashController._maxGeneratedParticlesInFrame = _originalSplashCount;
            }
        }

        _rainController = null;
        _lastExtra = -1f;
        _lastMaximumDensityMultiplier = -1f;
        _captured = false;
    }
}
