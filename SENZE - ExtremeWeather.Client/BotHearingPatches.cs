using EFT;
using HarmonyLib;

namespace SenzeExtremeWeather;

[HarmonyPatch(typeof(BotHearingSensor), "OnSoundPlayedInner")]
internal static class BotHearingSensorPatch
{
    [HarmonyPrefix]
    private static void Prefix(IPlayer __0, ref float __2, AISoundType __3)
    {
        if (Plugin.TryGetBotHearingModifier(__0, __3, out var modifier))
        {
            __2 *= modifier;
        }
    }
}
