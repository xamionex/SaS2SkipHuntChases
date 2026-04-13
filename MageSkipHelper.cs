using System;
using System.Collections.Generic;
using Bestiary.monsters;
using ProjectMage.character;
using ProjectMage.gamestate;
using ProjectMage.gamestate.arenastate;
using ProjectMage.gamestate.mage;

namespace SaS2SkipHuntChases;

internal static class MageSkipHelper
{
    // Tracks extra drops based on character ID to multiply loot upon death.
    internal static readonly Dictionary<int, int> SkippedPhasesCount = new();
    
    internal static bool ShouldSkipMissionMage(Mage mage)
    {
        // activeMission == -1 means this is a wandering mage; handled separately.
        if (GameSessionMgr.gameSession.activeMission < 0) return false;
        if (mage.optional)         return Plugin.SkipOptionalMages.Value;
        if (mage.missionInvisible) return Plugin.SkipInvisibleMages.Value;
        return Plugin.SkipMissionMages.Value;
    }

    internal static bool ShouldSkipWanderingMage() => GameSessionMgr.gameSession.activeMission < 0 && Plugin.SkipWanderingMages.Value;

    // Mirrors the boss-promotion block inside NextCycle(), fired immediately so the boss intro (bar, name, splash) plays without an extra flee warp.
    internal static void TryPromoteToBoss(Character character, Mage mage)
    {
        if (Plugin.GetAddCharToArenaIdxMethod == null || Plugin.OnAddCharToArenaMethod == null)
        {
            // Reflection unavailable, fall back to BATTLE_2 and let Update promote naturally.
            Plugin.SetPhaseMethod?.Invoke(mage, [6]);
            return;
        }

        try
        {
            var arenas  = GameSessionMgr.gameSession.mapMgr.arenas;
            var arenaIdx = (int)Plugin.GetAddCharToArenaIdxMethod.Invoke(arenas, [character]);

            if (arenaIdx > -1)
            {
                if (Plugin.GetMaxHpMethod != null && Plugin.ReduceBossHp.Value)
                {
                    var monsterDef = MonsterCatalog.monsterDef[character.monsterIdx];
                    var maxHp = (float)Plugin.GetMaxHpMethod.Invoke(monsterDef.gameMonster, [character]);
                    character.hp = GauntletMgr.IsActive switch
                    {
                        false when character.hp >= maxHp / 4f => maxHp / 4f,
                        true when character.hp >= maxHp / 2f => maxHp / 2f,
                        _ => character.hp
                    };
                }

                character.boss = true;
                Plugin.OnAddCharToArenaMethod.Invoke(null, [character.ID, arenaIdx]);
                Plugin.Instance.Log.LogDebug($"Mage {mage.charIdx} promoted to boss in arena {arenaIdx}.");
            }
            else
            {
                // Not near an arena yet, stand in BATTLE_2 until the player walks close.
                Plugin.SetPhaseMethod?.Invoke(mage, [6]);
                Plugin.Instance.Log.LogDebug($"Mage {mage.charIdx}: no arena found, waiting in BATTLE_2.");
            }
        }
        catch (Exception ex)
        {
            Plugin.Instance.Log.LogWarning($"TryPromoteToBoss failed: {ex.Message}");
            Plugin.SetPhaseMethod?.Invoke(mage, [6]);
        }
    }

    // Change values of mage so the hunt gets finished
    internal static void MarkCyclesComplete(Mage mage)
    {
        var skipped = mage.totalCycles - mage.cycle;
        if (skipped > 0)
        {
            SkippedPhasesCount[mage.charIdx] = skipped;
        }
        
        mage.cycle       = mage.totalCycles;
        mage.cycleDamage = mage.cycleMaxDamage + 1f;
        mage.totalDamage = mage.monsterHP;
    }
}