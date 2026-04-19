using System;
using Chronicler.missions;
using Common;
using HarmonyLib;
using ProjectMage;
using ProjectMage.character;
using ProjectMage.gamestate;
using ProjectMage.gamestate.mage;

namespace SaS2SkipHuntChases;

[HarmonyPatch]
public static class MageMgrPatch
{
    // WANDERING MAGES: patch AddMage, which is how non-mission mages get registered.
    // SetMissionTarget is never called for them, so this is the correct hook.
    // Wandering mages have no custom path and no arena to promote into, so we just force cycle completion and BATTLE_2 immediately.
    [HarmonyPatch(typeof(MageMgr), "AddMage")]
    [HarmonyPostfix]
    // ReSharper disable InconsistentNaming
    public static void AddMagePatch(MageMgr __instance, Character character, int __result)
    // ReSharper restore InconsistentNaming
    {
        if (!NetworkMgr.Instance.IsHost()) return;
        if (__result < 0) return; // AddMage returns -1 if the slot was full

        if (!MageSkipHelper.ShouldSkipWanderingMage()) return;

        var mage = __instance.mage[__result];

        MageSkipHelper.MarkCyclesComplete(mage);

        // Wandering mages have no custom path and no designated arena, so we just park them in BATTLE.
        // They'll fight in place like a boss and won't try to flee to any zone.
        Plugin.SetPhaseMethod?.Invoke(mage, [3]);

        Plugin.Instance.Log.LogInfo($"Wandering mage {mage.charIdx} hunt phases skipped.");
    }
    
    // MISSION MAGES: patch SetMissionTarget, which is the last step in CreateMages() after Activate() and after character.loc is set.
    // We override position here, then immediately run the same arena-promotion that NextCycle() would have run after the final warp.
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

        // Check if there is an arena near the mage's current position (before teleport)
        var hasArena = false;
        if (Plugin.GetAddCharToArenaIdxMethod != null)
        {
            var arenas = GameSessionMgr.gameSession.mapMgr.arenas;
            var arenaIdxObj = Plugin.GetAddCharToArenaIdxMethod.Invoke(arenas, [character]);
            if (arenaIdxObj != null && (int)arenaIdxObj != -1)
                hasArena = true;
        }

        // Only teleport if an arena already exists (i.e., this is the main mage)
        if (hasArena && Plugin.SpawnAtFinalLocation.Value && Plugin.GetPathNodeMethod != null && mage.hasCustomPath)
        {
            try
            {
                var finalNode = (Vector2)Plugin.GetPathNodeMethod.Invoke(mage, [mage.totalCycles]);
                if (finalNode.X > 0f)
                {
                    finalNode.Y = CharCols.GetGround(finalNode);
                    character.loc = finalNode;
                    character.warp.SetWarpIn(2f, finalNode, 3f, 1);
                }
            }
            catch (Exception ex)
            {
                Plugin.Instance.Log.LogWarning($"SpawnAtFinalLocation failed: {ex.Message}");
            }
        }

        MageSkipHelper.TryPromoteToBoss(character, mage);
        Plugin.Instance.Log.LogInfo($"Mission mage {mage.charIdx} hunt phases skipped.");
    }
}