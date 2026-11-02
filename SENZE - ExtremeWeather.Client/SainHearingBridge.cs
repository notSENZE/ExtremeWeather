using System;
using System.Reflection;
using BepInEx.Bootstrap;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace SenzeExtremeWeather;

internal static class SainHearingBridge
{
    private const string SainPluginGuid = "me.sol.sain";
    private const string WeatherTypeName = "SAIN.Components.BotController.SAINWeatherClass";

    private static FieldInfo _outdoorModifierField;
    private static FieldInfo _indoorModifierField;
    private static bool _sainInstalled;
    private static bool _patchApplied;
    private static bool _patchFailed;

    internal static void Initialize(Harmony harmony, ManualLogSource logger)
    {
        _sainInstalled = Chainloader.PluginInfos.ContainsKey(SainPluginGuid);
        TryPatch(harmony, logger);
    }

    internal static void Update(Harmony harmony, ManualLogSource logger)
    {
        if (_sainInstalled && !_patchApplied && !_patchFailed)
        {
            TryPatch(harmony, logger);
        }
    }

    private static void TryPatch(Harmony harmony, ManualLogSource logger)
    {
        if (!_sainInstalled
            || !Chainloader.PluginInfos.TryGetValue(SainPluginGuid, out var pluginInfo)
            || pluginInfo.Instance == null)
        {
            return;
        }

        var sainAssembly = pluginInfo.Instance.GetType().Assembly;
        var weatherType = sainAssembly.GetType(WeatherTypeName, false);
        var updateMethod = weatherType == null
            ? null
            : AccessTools.Method(weatherType, "Update", new[] { typeof(float), typeof(float) });
        _outdoorModifierField = weatherType == null
            ? null
            : AccessTools.Field(weatherType, "<RainSoundModifierOutdoor>k__BackingField");
        _indoorModifierField = weatherType == null
            ? null
            : AccessTools.Field(weatherType, "<RainSoundModifierIndoor>k__BackingField");

        if (updateMethod == null || _outdoorModifierField == null || _indoorModifierField == null)
        {
            _patchFailed = true;
            logger.LogWarning("SAIN was detected, but its weather-hearing API did not match SAIN 4.5.1. Native bot hearing remains supported.");
            return;
        }

        var postfix = AccessTools.Method(typeof(SainHearingBridge), nameof(UpdatePostfix));
        harmony.Patch(updateMethod, postfix: new HarmonyMethod(postfix));
        _patchApplied = true;
        logger.LogInfo("SAIN 4.5.1 weather-hearing integration enabled.");
    }

    private static void UpdatePostfix(object __instance)
    {
        if (!Plugin.TryGetSainHearingModifiers(out var outdoor, out var indoor))
        {
            return;
        }

        var currentOutdoor = (float)_outdoorModifierField.GetValue(__instance);
        var currentIndoor = (float)_indoorModifierField.GetValue(__instance);
        _outdoorModifierField.SetValue(__instance, Mathf.Min(currentOutdoor, outdoor));
        _indoorModifierField.SetValue(__instance, Mathf.Min(currentIndoor, indoor));
    }
}
