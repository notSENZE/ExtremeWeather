using EFT.Weather;
using HarmonyLib;
using UnityEngine;

namespace SenzeExtremeWeather;

[HarmonyPatch(typeof(WeatherController), nameof(WeatherController.ControlScattering))]
internal static class ScatteringFogDensityPatch
{
    [HarmonyPrefix]
    private static void Prefix(ref float __0)
    {
        if (Plugin.TryGetFogDensityMultiplier(out var multiplier))
        {
            __0 *= multiplier;
        }
    }
}

[HarmonyPatch(typeof(WeatherController), nameof(WeatherController.ControlFogginess))]
internal static class AtmosphereFogDensityPatch
{
    [HarmonyPrefix]
    private static void Prefix(ref float __0)
    {
        if (Plugin.TryGetFogDensityMultiplier(out var multiplier))
        {
            __0 = Mathf.Clamp01(__0 * multiplier);
        }
    }
}
