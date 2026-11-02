using Comfort.Common;
using BepInEx.Logging;
using EFT;
using UnityEngine;

namespace SenzeExtremeWeather;

internal sealed class StormMovementController
{
    private const float UpdateInterval = 0.25f;

    private readonly ManualLogSource _logger;
    private Player _player;
    private float _nextUpdateTime;
    private float _speedMultiplier = 1f;
    private bool _active;

    internal StormMovementController(ManualLogSource logger)
    {
        _logger = logger;
    }

    internal void Update(float stormStrength, int maximumResistancePercent)
    {
        if (Time.unscaledTime < _nextUpdateTime)
        {
            return;
        }
        _nextUpdateTime = Time.unscaledTime + UpdateInterval;

        var player = Singleton<GameWorld>.Instance?.MainPlayer;
        if (player != _player)
        {
            SetInactive();
            _player = player;
        }

        var shouldApply = _player != null
            && _player.MovementContext != null
            && RainController.IsCameraUnderRain
            && stormStrength > 0.001f
            && maximumResistancePercent > 0;
        if (!shouldApply)
        {
            SetInactive();
            return;
        }

        var maximumResistance = Mathf.Clamp(maximumResistancePercent / 100f, 0f, 0.5f);
        _speedMultiplier = Mathf.Lerp(1f, 1f - maximumResistance, Mathf.Clamp01(stormStrength));
        if (!_active)
        {
            _active = true;
            _logger.LogInfo(
                $"Outdoor storm movement resistance activated at {(1f - _speedMultiplier) * 100f:0}%.");
        }
    }

    internal bool TryGetSpeedMultiplier(MovementContext movementContext, out float multiplier)
    {
        multiplier = _speedMultiplier;
        return _active
            && _player != null
            && ReferenceEquals(_player.MovementContext, movementContext)
            && multiplier < 0.999f;
    }

    internal void Reset()
    {
        SetInactive();
        _player = null;
        _nextUpdateTime = 0f;
    }

    private void SetInactive()
    {
        if (_active)
        {
            _logger.LogInfo("Outdoor storm movement resistance released.");
        }

        _speedMultiplier = 1f;
        _active = false;
    }
}
