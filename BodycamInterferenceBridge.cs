using System;
using System.Reflection;
using BepInEx.Bootstrap;
using BepInEx.Logging;

namespace SenzeExtremeWeather;

internal sealed class BodycamInterferenceBridge
{
    private const string BodycamPluginGuid = "com.senze.bodycam";

    private readonly ManualLogSource _logger;
    private MethodInfo _triggerMethod;
    private bool _lookupCompleted;
    private bool _missingApiLogged;

    internal BodycamInterferenceBridge(ManualLogSource logger)
    {
        _logger = logger;
        FindApi();
    }

    internal bool TryTrigger(float intensity, float duration)
    {
        if (!_lookupCompleted)
        {
            FindApi();
        }
        if (_triggerMethod == null)
        {
            return false;
        }

        try
        {
            var result = _triggerMethod.Invoke(
                null,
                new object[] { intensity, duration });
            return result is bool triggered && triggered;
        }
        catch (TargetInvocationException exception)
        {
            _logger.LogWarning(
                $"Bodycam interference failed: {exception.InnerException?.Message ?? exception.Message}");
            return false;
        }
    }

    private void FindApi()
    {
        _lookupCompleted = true;
        if (!Chainloader.PluginInfos.TryGetValue(BodycamPluginGuid, out var pluginInfo)
            || pluginInfo?.Instance == null)
        {
            return;
        }

        var apiType = pluginInfo.Instance.GetType().Assembly.GetType(
            "SenzeBodycam.BodycamInterferenceApi",
            false);
        _triggerMethod = apiType?.GetMethod(
            "Trigger",
            BindingFlags.Public | BindingFlags.Static,
            null,
            new[] { typeof(float), typeof(float) },
            null);

        if (_triggerMethod != null)
        {
            _logger.LogInfo(
                $"Bodycam interference API detected in Bodycam {pluginInfo.Metadata.Version}.");
            return;
        }

        if (!_missingApiLogged)
        {
            _logger.LogWarning(
                "Bodycam is installed but does not expose the interference API. Update Bodycam to 1.1.0 or newer.");
            _missingApiLogged = true;
        }
    }
}
