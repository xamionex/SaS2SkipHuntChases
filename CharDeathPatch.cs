using System;
using System.Reflection;
using HarmonyLib;
using Common;
using ProjectMage.character;
using Bestiary.monsters;
using ProjectMage.particles;
using ProjectMage.gamestate;
using LootHero.loot;

namespace SaS2SkipHuntChases;

[HarmonyPatch]
public static class CharDeathPatch
{
    private static readonly FieldInfo CharacterField = AccessTools.Field(typeof(CharDeath), "character");
    private static readonly FieldInfo RandField      = AccessTools.Field(typeof(CharDeath), "Rand");
    private static MethodInfo _getCharmValMethod;

    [HarmonyPatch(typeof(CharDeath), nameof(CharDeath.DropLoot))]
    [HarmonyPrefix]
    // ReSharper disable once InconsistentNaming
    public static bool DropLootPatch(CharDeath __instance, MonsterDef mDef)
    {
        var character = (Character)CharacterField.GetValue(__instance);
        if (character == null) return true;

        if (!Plugin.DropLootRelativeAmount.Value
            || !MageSkipHelper.SkippedPhasesCount.TryGetValue(character.ID, out var skippedPhases))
            return true;

        MageSkipHelper.SkippedPhasesCount.Remove(character.ID);
        MageSkipHelper.TotalCyclesCount.TryGetValue(character.ID, out var totalCycles);
        MageSkipHelper.TotalCyclesCount.Remove(character.ID);

        if (skippedPhases <= 0) return true;

        // totalCycles is used as a divisor so that the *total* bonus across all extra drops equals roughly one normal phase-drop worth of loot, multiplied by the user's configured multiplier.
        // Without this, a mage with 4 cycles would yield 4x full boss-drop amounts as bonus, far too much.
        var effectiveTotalCycles = totalCycles > 0 ? totalCycles : 1;

        var randObj = RandField.GetValue(null);
        for (var i = 0; i < skippedPhases; i++)
            DropStandardPhaseLoot(character, mDef, randObj, effectiveTotalCycles);

        return true; // always let the original DropLoot run for artifacts/quest items
    }

    private static void DropStandardPhaseLoot(Character character, MonsterDef mDef, object rand, int totalCycles)
    {
        if (mDef.type != 1) return;

        var multiplier = Plugin.DropLootMultiplier.Value;

        // Item Find from the killing player.
        var itemFind = 1f;
        if (character.lastHitBy > -1)
        {
            var killer = CharMgr.character[character.lastHitBy];
            if (killer.playerIdx > -1)
            {
                var player = killer.GetPlayer();
                itemFind += player.equipment.GetItemFind(true) * 0.01f;

                _getCharmValMethod ??= AccessTools.Method(player.equipment.GetType(), "GetCharmVal");
                var charmVal = (float)_getCharmValMethod.Invoke(player.equipment, [6]);
                itemFind += charmVal * 0.2f;
            }
        }

        var spawnLoc = character.loc + new Vector2(0f, Math.Min(400, mDef.boxHeight) * -0.5f);

        // Silver, divided by totalCycles so the cumulative bonus across all extra drops stays proportional to a single hunt phase, not a full kill.
        var silverCount = (int)AccessTools.Method(rand.GetType(), "GetRandomInt", [typeof(int), typeof(int)])
            .Invoke(rand, [0, 3]);
        
        if (silverCount > 0)
        {
            var silverBaseVal = (float)mDef.monsterField[62].iData;
            for (var i = 0; i < silverCount; i++)
            {
                var amount = silverBaseVal / silverCount * 0.75f * multiplier / totalCycles;
                ParticleManager.AddBackAdditiveParticle(42, spawnLoc, new Vector2(0f, -200f), amount, 0f, 0, 0, character.ID);
            }
        }

        // Materials (monsterField indices 45-59, 5 slots * 3 fields each).
        for (var j = 0; j < 5; j++)
        {
            var itemKey = mDef.monsterField[45 + j * 3].strData;
            if (string.IsNullOrEmpty(itemKey)) continue;

            // Scale probability down by totalCycles for the same reason as silver.
            var prob    = mDef.monsterField[46 + j * 3].fData * itemFind / totalCycles;
            var maxDrop = mDef.monsterField[47 + j * 3].iData;
            var lootIdx = LootCatalog.GetLootIdxOrNegative(itemKey);

            if (lootIdx <= -1) continue;

            var baseCount = 0;
            for (var l = 0; l < maxDrop; l++)
            {
                // Standard "CoinToss" for each potential stack item
                var success = (bool)AccessTools.Method(rand.GetType(), "CoinToss").Invoke(rand, [prob / 100f]);
                if (success) baseCount++;
            }

            if (baseCount <= 0) continue;

            var finalCount  = (int)Math.Max(1, Math.Round(baseCount * multiplier));
            var adjustedIdx = GameSessionMgr.gameSession.missionTierAdjustor
                .AdjustUpgradeItem(lootIdx, character, false, false, true);
            GameSessionMgr.gameSession.mapMgr.pickups.AddPickup(spawnLoc, adjustedIdx, finalCount);
        }
    }
}