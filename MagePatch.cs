using System.Collections.Generic;
using HarmonyLib;
using ProjectMage;
using ProjectMage.character;
using ProjectMage.gamestate.mage;

namespace SaS2SkipHuntChases;

[HarmonyPatch]
public static class MagePatch
{
    // Tracks charIdx values we have already promoted.
    // A mage dying and its slot being reused will go through Activate() -> AddMage/SetMissionTarget again,
    // so the spawn patches will catch it cleanly the second time around.
    private static readonly HashSet<int> Handled = [];

    // Safety net: catches mages that were already alive when the mod was first
    // loaded (e.g. pre-existing wandering mages from an unmodded save), plus
    // anything that slipped through the spawn patches via network events.
    [HarmonyPatch(typeof(Mage), "Update")]
    [HarmonyPrefix]
    // ReSharper disable once InconsistentNaming
    public static void MageUpdatePatch(Mage __instance)
    {
        if (!NetworkMgr.Instance.IsHost()) return;
        if (__instance.charIdx < 0 || !__instance.exists) return;

        var shouldSkip = MageSkipHelper.ShouldSkipMissionMage(__instance)
                         || MageSkipHelper.ShouldSkipWanderingMage();

        if (!shouldSkip) return;

        // Already handled this mage
        if (Handled.Contains(__instance.charIdx)) return;

        // Still has hunt cycles left: this is a pre-existing mage from before the mod was loaded. Runs the full promotion path
        if (__instance.cycle < __instance.totalCycles)
        {
            MageSkipHelper.MarkCyclesComplete(__instance);
            var character = CharMgr.character[__instance.charIdx];
            MageSkipHelper.TryPromoteToBoss(character, __instance);
            Plugin.Instance.Log.LogDebug($"Update: late-promoted mage {__instance.charIdx}.");
        }

        // Mark handled so we skip it next check
        Handled.Add(__instance.charIdx);
    }
}