using EFT;
using HarmonyLib;

namespace SenzeExtremeWeather;

[HarmonyPatch(typeof(MovementContext), nameof(MovementContext.ClampSpeed))]
internal static class WalkingSpeedPatch
{
    [HarmonyPostfix]
    private static void Postfix(MovementContext __instance, ref float __result)
    {
        if (Plugin.TryGetMovementSpeedMultiplier(__instance, out var multiplier))
        {
            __result *= multiplier;
        }
    }
}

[HarmonyPatch(typeof(MovementContext), nameof(MovementContext.StateSprintSpeedLimit), MethodType.Getter)]
internal static class SprintSpeedPatch
{
    [HarmonyPostfix]
    private static void Postfix(MovementContext __instance, ref float __result)
    {
        if (Plugin.TryGetMovementSpeedMultiplier(__instance, out var multiplier))
        {
            __result *= multiplier;
        }
    }
}
