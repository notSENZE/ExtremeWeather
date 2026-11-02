using Comfort.Common;
using BepInEx.Logging;
using EFT;
using UnityEngine;

namespace SenzeExtremeWeather;

internal sealed class StormMovementController
{
    private const float UpdateInterval = 0.25f;

    private readonly ManualLogSource _logger;
    private Player _localPlayer;
    private float _nextUpdateTime;
    private float _speedMultiplier = 1f;
    private bool _stormActive;
    private bool _localPlayerOutside;

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

        _localPlayer = Singleton<GameWorld>.Instance?.MainPlayer;
        _localPlayerOutside = _localPlayer != null && RainController.IsCameraUnderRain;

        var shouldApply = _localPlayer != null
            && stormStrength > 0.001f
            && maximumResistancePercent > 0;
        if (!shouldApply)
        {
            SetInactive();
            return;
        }

        var maximumResistance = Mathf.Clamp(maximumResistancePercent / 100f, 0f, 0.5f);
        _speedMultiplier = Mathf.Lerp(1f, 1f - maximumResistance, Mathf.Clamp01(stormStrength));
        if (!_stormActive)
        {
            _stormActive = true;
            _logger.LogInfo(
                $"Outdoor storm movement resistance activated for the player and bots at {(1f - _speedMultiplier) * 100f:0}%.");
        }
    }

    internal bool TryGetSpeedMultiplier(Player player, MovementContext movementContext, out float multiplier)
    {
        multiplier = _speedMultiplier;
        if (!_stormActive || player == null || multiplier >= 0.999f)
        {
            return false;
        }

        if (ReferenceEquals(player, _localPlayer))
        {
            return _localPlayerOutside && ReferenceEquals(player.MovementContext, movementContext);
        }

        return player.IsAI
            && player.AIData != null
            && player.AIData.EnvironmentId == 0
            && ReferenceEquals(player.MovementContext, movementContext);
    }

    internal void Reset()
    {
        SetInactive();
        _localPlayer = null;
        _localPlayerOutside = false;
        _nextUpdateTime = 0f;
    }

    private void SetInactive()
    {
        if (_stormActive)
        {
            _logger.LogInfo("Outdoor storm movement resistance released.");
        }

        _speedMultiplier = 1f;
        _stormActive = false;
    }
}
