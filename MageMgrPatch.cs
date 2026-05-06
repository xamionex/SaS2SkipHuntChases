using System;
using Chronicler.missions;
using Common;
using HarmonyLib;
using ProjectMage;
using ProjectMage.character;
using ProjectMage.gamestate;
using ProjectMage.gamestate.arenastate;
using ProjectMage.gamestate.mage;
using ProjectMage.map.arena;

namespace SaS2SkipHuntChases;

[HarmonyPatch]
public static class MageMgrPatch
{
    /// WANDERING MAGES: patch AddMage, which is how non-mission mages get registered.
    /// SetMissionTarget is never called for them, so this is the correct hook.
    /// Wandering mages have no custom path and no arena to promote into, so we just force cycle completion and BATTLE_2 immediately.
    [HarmonyPatch(typeof(MageMgr), "AddMage")]
    [HarmonyPostfix]
    // ReSharper disable InconsistentNaming
    public static void AddMagePatch(MageMgr __instance, Character character, int __result)
    // ReSharper restore InconsistentNaming
    {
        if (!NetworkMgr.Instance.IsHost()) return;
        if (__result < 0) return;

        // Gauntlet mages are handled in OnCharacterSpawnPatch because GauntletMgr sets totalCycles AFTER AddMage returns, so totalCycles == 0 here.
        if (GauntletMgr.IsActive) return;

        if (!MageSkipHelper.ShouldSkipWanderingMage()) return;

        var mage = __instance.mage[__result];
        MageSkipHelper.MarkCyclesComplete(mage);
        MageSkipHelper.TryPromoteToBoss(character, mage);
        MageSkipHelper.ReduceBossHp(character, mage);
        Plugin.Instance.Log.LogInfo($"Wandering mage {mage.charIdx} hunt phases skipped.");
    }

    /// GAUNTLET MAGES: OnCharacterSpawn fires after GauntletMgr sets totalCycles.
    [HarmonyPatch(typeof(NetworkEvents), "OnCharacterSpawn")]
    [HarmonyPostfix]
    public static void OnCharacterSpawnPatch(int charIndex, bool summoned, float warpInDelay)
    {
        if (!NetworkMgr.Instance.IsHost()) return;
        if (!GauntletMgr.IsActive || !MageSkipHelper.ShouldSkipGauntletMage()) return;

        if (charIndex < 0 || charIndex >= CharMgr.character.Length) return;
        var character = CharMgr.character[charIndex];
        if (!character.exists || character.mageIdx < 0) return;

        var mage = GameSessionMgr.gameSession.mageMgr.mage[character.mageIdx];
        if (mage is not { exists: true }) return;

        if (mage.totalCycles == 0 || mage.cycle >= mage.totalCycles) return;

        MageSkipHelper.MarkCyclesComplete(mage);
        MageSkipHelper.TryPromoteToBoss(character, mage);
        MageSkipHelper.ReduceBossHp(character, mage);
        Plugin.Instance.Log.LogInfo($"Gauntlet mage {mage.charIdx} promoted on spawn.");
    }
    
    /// MISSION MAGES: patch SetMissionTarget, which is the last step in CreateMages() after Activate() and after character.loc is set.
    /// We override position here, then immediately run the same arena-promotion that NextCycle() would have run after the final warp.
    [HarmonyPatch(typeof(MageMgr), "SetMissionTarget")]
    [HarmonyPostfix]
    // ReSharper disable once InconsistentNaming
    public static void SetMissionTargetPatch(MageMgr __instance, MissionTarget target, int mageIdx)
    {
        if (!NetworkMgr.Instance.IsHost()) return;

        var mage = __instance.mage[mageIdx];
        if (!MageSkipHelper.ShouldSkipMissionMage(mage)) return;

        MageSkipHelper.MarkCyclesComplete(mage);

        var character = CharMgr.character[mage.charIdx];

        // Teleport to final path node before the arena lookup so GetAddCharToArenaIdx can match the right arena by position.
        if (Plugin.SpawnAtFinalLocation.Value && !mage.missionInvisible && Plugin.GetPathNodeMethod != null && mage.hasCustomPath)
        {
            try
            {
                var finalNode = (Vector2)Plugin.GetPathNodeMethod.Invoke(mage, [mage.totalCycles]);
                if (finalNode.X > 0f)
                {
                    finalNode.Y = CharCols.GetGround(finalNode);
                    character.loc = finalNode;
                    character.warp.SetWarpIn(2f, finalNode, 3f, 1);
                    Plugin.Instance.Log.LogInfo($"Mission mage {mage.charIdx} ({MageSkipHelper.GetMageName(mage)}) warped to final location.");
                }
            }
            catch (Exception ex)
            {
                Plugin.Instance.Log.LogWarning($"SpawnAtFinalLocation failed: {ex.Message}");
            }
        }

        MageSkipHelper.TryPromoteToBoss(character, mage);

        // Apply HP reduction immediately after promotion.
        // When SpawnAtFinalLocation is on the mage is already inside the arena rect so TryPromoteToBoss promotes it right here, we must set HP now, before the network sync in OnGoToArena can overwrite it.
        // MapArenaActivatePatch handles the BATTLE_2 fallback case where the mage waits outside and gets promoted later.
        MageSkipHelper.ReduceBossHp(character, mage);
    }

    /// Re-apply HP reduction the moment the arena fight begins.
    /// Covers BATTLE_2 fallback mages promoted later when the player walks close, and acts as a safety net in case anything reset HP between spawn and fight.
    [HarmonyPatch(typeof(MapArena), "Activate")]
    [HarmonyPostfix]
    // ReSharper disable once InconsistentNaming
    public static void MapArenaActivatePatch(MapArena __instance)
    {
        if (!NetworkMgr.Instance.IsHost()) return;
        if (__instance.boss == null) return;

        foreach (var charId in __instance.boss)
        {
            if (charId < 0 || charId >= CharMgr.character.Length) continue;
            var character = CharMgr.character[charId];
            if (!character.exists || character.mageIdx < 0) continue;

            var mage = GameSessionMgr.gameSession.mageMgr.mage[character.mageIdx];
            if (mage is not { exists: true }) continue;

            MageSkipHelper.ReduceBossHp(character, mage);
        }
    }

    /// Clear the promotion cache on map load so stale IDs don't block fresh mages.
    [HarmonyPatch(typeof(NetworkEvents), "OnMapLoading")]
    [HarmonyPostfix]
    public static void OnMapLoadingPatch()
    {
        MageSkipHelper.ClearPromotionCache();
        Plugin.Instance.Log.LogDebug("Promotion cache cleared on map load.");
    }
}