using Comfort.Common;
using EFT;
using UnityEngine;

namespace SenzeExtremeWeather;

internal sealed class StormMovementController
{
    private const float UpdateInterval = 0.25f;
    private const Player.ESpeedLimit StormSpeedLimit = (Player.ESpeedLimit)100;

    private Player _player;
    private float _nextUpdateTime;
    private float _lastSpeedLimit = 1f;
    private bool _speedLimitApplied;

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
            RemoveSpeedLimit();
            _player = player;
        }

        var shouldApply = _player != null
            && _player.MovementContext != null
            && RainController.IsCameraUnderRain
            && stormStrength > 0.001f
            && maximumResistancePercent > 0;
        if (!shouldApply)
        {
            RemoveSpeedLimit();
            return;
        }

        var maximumResistance = Mathf.Clamp(maximumResistancePercent / 100f, 0f, 0.5f);
        var speedLimit = Mathf.Lerp(1f, 1f - maximumResistance, Mathf.Clamp01(stormStrength));
        if (_speedLimitApplied && Mathf.Abs(speedLimit - _lastSpeedLimit) < 0.005f)
        {
            return;
        }

        _player.MovementContext.RemoveStateSpeedLimit(StormSpeedLimit);
        _player.MovementContext.AddStateSpeedLimit(speedLimit, StormSpeedLimit);
        _lastSpeedLimit = speedLimit;
        _speedLimitApplied = true;
    }

    internal void Reset()
    {
        RemoveSpeedLimit();
        _player = null;
        _nextUpdateTime = 0f;
    }

    private void RemoveSpeedLimit()
    {
        if (_speedLimitApplied && _player?.MovementContext != null)
        {
            _player.MovementContext.RemoveStateSpeedLimit(StormSpeedLimit);
        }

        _lastSpeedLimit = 1f;
        _speedLimitApplied = false;
    }
}
