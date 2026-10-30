using System.Reflection;
using Audio.AmbientSubsystem;
using HarmonyLib;
using UnityEngine;

namespace SenzeExtremeWeather;

[HarmonyPatch(typeof(WindAmbientBlender), nameof(WindAmbientBlender.GetWindVolume))]
internal static class WindVolumePatch
{
    [HarmonyPostfix]
    private static void Postfix(ref float __result)
    {
        var settings = Plugin.Settings;
        if (settings != null)
        {
            __result = Mathf.Clamp01(__result * settings.WindVolume.Value / 100f);
        }
    }
}

[HarmonyPatch(typeof(PrecipitationAmbientBlender), nameof(PrecipitationAmbientBlender.UpdateGlobalsVolumeMult))]
internal static class RainVolumePatch
{
    private static readonly FieldInfo VolumeMultiplierField = AccessTools.Field(
        typeof(PrecipitationAmbientBlender),
        "_globalsVolumeMult");

    [HarmonyPostfix]
    private static void Postfix(PrecipitationAmbientBlender __instance)
    {
        var settings = Plugin.Settings;
        if (settings == null || VolumeMultiplierField == null)
        {
            return;
        }

        var currentValue = (float)VolumeMultiplierField.GetValue(__instance);
        var configuredVolume = WeatherSeason.IsWinter
            ? settings.SnowVolume.Value
            : settings.RainVolume.Value;
        VolumeMultiplierField.SetValue(__instance, currentValue * configuredVolume / 100f);
    }
}
