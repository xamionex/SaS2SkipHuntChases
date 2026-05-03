using System.Collections.Generic;
using HarmonyLib;
using ProjectMage;
using ProjectMage.character;
using ProjectMage.gamestate;
using ProjectMage.gamestate.arenastate;
using ProjectMage.gamestate.mage;

namespace SaS2SkipHuntChases;

[HarmonyPatch]
internal static class MagePatch
{
    // Track mages that have already been retroactively skipped to avoid double processing.
    private static readonly HashSet<int> SkippedMages = [];
    // Track mages whose HP has already been reduced to avoid double reduction.
    private static readonly HashSet<int> HpReducedMages = [];

    [HarmonyPatch(typeof(Mage), "NextCycle")]
    [HarmonyPrefix]
    // ReSharper disable once InconsistentNaming
    public static bool NextCyclePatch(Mage __instance)
    {
        // Determine if this mage should be skipped based on current config
        var shouldSkip = false;
        if (GameSessionMgr.gameSession.activeMission < 0)
            shouldSkip = MageSkipHelper.ShouldSkipWanderingMage();
        if (!shouldSkip && GauntletMgr.IsActive)
            shouldSkip = MageSkipHelper.ShouldSkipGauntletMage();
        if (!shouldSkip && GameSessionMgr.gameSession.activeMission > 0)
            shouldSkip = MageSkipHelper.ShouldSkipMissionMage(__instance);

        if (!shouldSkip) return true;          // No skipping → run original NextCycle
        if (!NetworkMgr.Instance.IsHost()) return true;

        if (__instance.charIdx < 0 || __instance.charIdx >= CharMgr.character.Length)
            return true;
        var character = CharMgr.character[__instance.charIdx];
        if (character is not { exists: true }) return true;

        // Already a boss → nothing more to do, skip original
        if (character.boss) return false;

        // If we already skipped this mage before, don't do it again
        if (SkippedMages.Contains(__instance.charIdx))
            return false;

        // If cycles are not yet complete, finish them now (retroactive skip)
        if (__instance.cycle < __instance.totalCycles)
        {
            Plugin.Instance.Log.LogInfo($"Retroactive skip: completing {__instance.totalCycles - __instance.cycle} remaining cycles for mage {__instance.charIdx}");
            MageSkipHelper.MarkCyclesComplete(__instance);
            SkippedMages.Add(__instance.charIdx);
        }

        // Promote to boss
        MageSkipHelper.TryPromoteToBoss(character, __instance);

        // Reduce HP only once per mage
        if (!character.boss || HpReducedMages.Contains(__instance.charIdx)) return false;
        MageSkipHelper.ReduceBossHp(character, __instance);
        HpReducedMages.Add(__instance.charIdx);

        // Prevent original NextCycle from running
        return false;
    }

    // Clear tracking caches on map load to avoid stale IDs
    [HarmonyPatch(typeof(NetworkEvents), "OnMapLoading")]
    [HarmonyPostfix]
    public static void OnMapLoadingPatch()
    {
        SkippedMages.Clear();
        HpReducedMages.Clear();
        MageSkipHelper.ClearPromotionCache();
        Plugin.Instance.Log.LogDebug("MagePatch caches cleared on map load.");
    }
}