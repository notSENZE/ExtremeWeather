using EFT;
using HarmonyLib;

namespace SenzeExtremeWeather;

[HarmonyPatch(typeof(MovementContext), nameof(MovementContext.ClampSpeed))]
internal static class WalkingSpeedPatch
{
    private static readonly AccessTools.FieldRef<MovementContext, Player> PlayerField =
        AccessTools.FieldRefAccess<MovementContext, Player>("_player");

    [HarmonyPostfix]
    private static void Postfix(MovementContext __instance, ref float __result)
    {
        if (Plugin.TryGetMovementSpeedMultiplier(PlayerField(__instance), __instance, out var multiplier))
        {
            __result *= multiplier;
        }
    }
}

[HarmonyPatch(typeof(MovementContext), nameof(MovementContext.StateSprintSpeedLimit), MethodType.Getter)]
internal static class SprintSpeedPatch
{
    private static readonly AccessTools.FieldRef<MovementContext, Player> PlayerField =
        AccessTools.FieldRefAccess<MovementContext, Player>("_player");

    [HarmonyPostfix]
    private static void Postfix(MovementContext __instance, ref float __result)
    {
        if (Plugin.TryGetMovementSpeedMultiplier(PlayerField(__instance), __instance, out var multiplier))
        {
            __result *= multiplier;
        }
    }
}
