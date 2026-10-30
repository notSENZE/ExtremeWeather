using HarmonyLib;
using UnityEngine;

namespace SenzeExtremeWeather;

[HarmonyPatch(typeof(LookSensor), nameof(LookSensor.VisibleDist), MethodType.Getter)]
internal static class VisibleDistancePatch
{
    [HarmonyPostfix]
    private static void Postfix(ref float __result)
    {
        if (Plugin.TryGetMaximumStormBotVision(out var maximumDistance))
        {
            __result = Mathf.Min(__result, maximumDistance);
        }
    }
}

[HarmonyPatch(typeof(LookSensor), nameof(LookSensor.ClearVisibleDist), MethodType.Getter)]
internal static class ClearVisibleDistancePatch
{
    [HarmonyPostfix]
    private static void Postfix(ref float __result)
    {
        if (Plugin.TryGetMaximumStormBotVision(out var maximumDistance))
        {
            __result = Mathf.Min(__result, maximumDistance);
        }
    }
}
