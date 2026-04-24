using System;
using System.Collections.Generic;
using Bestiary.monsters;
using Chronicler.missions;
using ProjectMage.character;
using ProjectMage.gamestate;
using ProjectMage.gamestate.arenastate;
using ProjectMage.gamestate.mage;

namespace SaS2SkipHuntChases;

internal static class MageSkipHelper
{
    /// Tracks skipped phase count per character ID for the loot multiplier on death.
    internal static readonly Dictionary<int, int> SkippedPhasesCount = new();
    private static readonly List<int> LoggedMages = [];

    internal static bool ShouldSkipMissionMage(Mage mage)
    {
        var activeMission = GameSessionMgr.gameSession.activeMission;
        var missionType = activeMission >= 0 ? MissionCatalog.mission[activeMission].GetMissionType() : -1;

        if (!LoggedMages.Contains(mage.charIdx))
        {
            Plugin.Instance.Log.LogInfo($"mage detected: ID {mage.charIdx} ({GetMageName(mage)}), Invisible: {mage.missionInvisible}, Optional: {mage.optional}, Active Mission: {activeMission}, Mission Type: {missionType}");
            LoggedMages.Add(mage.charIdx);
        }

        // Gauntlet is mission type 6, check it first so it never bleeds into Named/Nameless so users can toggle it independently.
        if (GauntletMgr.IsActive) return Plugin.SkipGauntletMages.Value;
        
        if (activeMission < 0) return false;
        
        if (!mage.hasCustomPath) return false;

        // Optional/invisible flags take priority over mission type.
        //if (mage.optional) return Plugin.SkipOptionalMages.Value;
        //if (mage.missionInvisible) return Plugin.SkipInvisibleMages.Value;

        // Type 1 = Named hunt, type 2 = Nameless (token_nameless reward). Any other type falls back to Named hunt.
        return missionType switch
        {
            2 => Plugin.SkipNamelessMages.Value,
            _ => Plugin.SkipNamedMages.Value
        };
    }

    internal static string GetMageName(Mage mage)
    {
        if (mage.charIdx < 0) return "Unknown";
        var character = CharMgr.character[mage.charIdx];
        if (character == null) return "Unknown";
        var monsterDef = MonsterCatalog.monsterDef[character.monsterIdx];
        return monsterDef?.name ?? "Unknown";
    }

    internal static bool ShouldSkipWanderingMage() => GameSessionMgr.gameSession.activeMission < 0 && Plugin.SkipWanderingMages.Value;
    internal static bool ShouldSkipGauntletMage() => GauntletMgr.IsActive && Plugin.SkipGauntletMages.Value;

    // Mirrors the boss-promotion block inside NextCycle(), fired immediately so the boss intro (bar, name, splash) plays without an extra flee warp.
    internal static void TryPromoteToBoss(Character character, Mage mage)
    {
        try
        {
            var arenas  = GameSessionMgr.gameSession.mapMgr.arenas;
            var arenaIdx = (int)Plugin.GetAddCharToArenaIdxMethod.Invoke(arenas, [character]);

            if (!GauntletMgr.IsActive) ReduceBossHp(character, mage);
            if (arenaIdx > -1)
            {
                character.boss = true;
                Plugin.OnAddCharToArenaMethod.Invoke(null, [character.ID, arenaIdx]);
                Plugin.Instance.Log.LogInfo($"Mage {mage.charIdx} ({GetMageName(mage)}) promoted to boss in arena {arenaIdx}.");
            }
            else
            {
                // Not near an arena yet, stand in BATTLE_2 until the player walks close.
                Plugin.SetPhaseMethod?.Invoke(mage, [6]);
                Plugin.Instance.Log.LogInfo($"Mage {mage.charIdx} ({GetMageName(mage)}): no arena found, waiting in BATTLE_2.");
            }
        }
        catch (Exception ex)
        {
            Plugin.Instance.Log.LogWarning($"TryPromoteToBoss failed: {ex.Message}");
        }
    }

    internal static void ReduceBossHp(Character character, Mage mage)
    {
        if (Plugin.GetMaxHpMethod == null) return;

        var oldHp =  character.hp;
        var monsterDef = MonsterCatalog.monsterDef[character.monsterIdx];
        var maxHp = (float)Plugin.GetMaxHpMethod.Invoke(monsterDef.gameMonster, [character]);
        var targetHp = Plugin.ReduceBossHp.Value ? GauntletMgr.IsActive ? maxHp / 2f : maxHp / 4f : maxHp;
        character.hp = targetHp * Plugin.BossHpMultiplier.Value;
        mage.monsterHP = character.hp;
        if (Math.Abs(oldHp - character.hp) > 0.1) Plugin.Instance.Log.LogInfo($"Changed mage {mage.charIdx} max HP from {oldHp} to {character.hp}");
    }
    
    // Mark all hunt cycles as complete and record how many were skipped for the loot multiplier on death.
    internal static void MarkCyclesComplete(Mage mage)
    {
        var skipped = mage.totalCycles - mage.cycle;
        if (skipped > 0) SkippedPhasesCount[mage.charIdx] = skipped;

        mage.cycle       = mage.totalCycles;
        mage.cycleDamage = mage.cycleMaxDamage + 1f;
        mage.totalDamage = mage.monsterHP;
        Plugin.Instance.Log.LogInfo($"Completed cycles of mage {mage.charIdx} ({GetMageName(mage)}).");
    }
}