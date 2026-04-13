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
    private static readonly FieldInfo RandField = AccessTools.Field(typeof(CharDeath), "Rand");
    private static MethodInfo _getCharmValMethod;

    [HarmonyPatch(typeof(CharDeath), nameof(CharDeath.DropLoot))]
    [HarmonyPrefix]
    // ReSharper disable once InconsistentNaming
    public static bool DropLootPatch(CharDeath __instance, MonsterDef mDef)
    {
        var character = (Character)CharacterField.GetValue(__instance);
        if (character == null) return true;

        if (!Plugin.DropLootRelativeAmount.Value || !MageSkipHelper.SkippedPhasesCount.TryGetValue(character.ID, out var extraPhases)) return true; // Let original DropLoot run once for Artifacts/Quest items
        MageSkipHelper.SkippedPhasesCount.Remove(character.ID);

        if (extraPhases <= 0) return true; // Let original DropLoot run once for Artifacts/Quest items

        var randObj = RandField.GetValue(null);
        for (var i = 0; i < extraPhases; i++)
        {
            DropStandardPhaseLoot(character, mDef, randObj);
        }

        return true; // Let original DropLoot run once for Artifacts/Quest items
    }

    private static void DropStandardPhaseLoot(Character character, MonsterDef mDef, object rand)
    {
        if (mDef.type != 1) return;

        var multiplier = Plugin.DropLootMultiplier.Value;

        // Calculate Item Find
        var itemFind = 1f;
        if (character.lastHitBy > -1)
        {
            var killer = CharMgr.character[character.lastHitBy];
            if (killer.playerIdx > -1)
            {
                var player = killer.GetPlayer();
                itemFind += player.equipment.GetItemFind(true) * 0.01f;

                if (_getCharmValMethod == null) 
                    _getCharmValMethod = AccessTools.Method(player.equipment.GetType(), "GetCharmVal");
                
                var charmVal = (float)_getCharmValMethod.Invoke(player.equipment, [6]);
                itemFind += charmVal * 0.2f;
            }
        }

        var spawnLoc = character.loc + new Vector2(0f, Math.Min(400, mDef.boxHeight) * -0.5f);

        // 2. Drop Silver/XP Particles
        var silverCount = (int)AccessTools.Method(rand.GetType(), "GetRandomInt", [typeof(int), typeof(int)])
            .Invoke(rand, [0, 3]);
        
        if (silverCount > 0)
        {
            var silverBaseVal = (float)mDef.monsterField[62].iData;
            for (var i = 0; i < silverCount; i++)
            {
                // Multiplied the silver amount by our config value
                var amount = silverBaseVal / silverCount * 0.75f * multiplier;
                ParticleManager.AddBackAdditiveParticle(42, spawnLoc, new Vector2(0, -200f), amount, 0f, 0, 0, character.ID);
            }
        }

        // 3. Drop Materials (Indices 45-59)
        for (var j = 0; j < 5; j++)
        {
            var strIdx = 45 + j * 3;
            var probIdx = 46 + j * 3;
            var countIdx = 47 + j * 3;

            var itemKey = mDef.monsterField[strIdx].strData;
            if (string.IsNullOrEmpty(itemKey)) continue;

            // Keep standard probability (affected by Item Find)
            var prob = mDef.monsterField[probIdx].fData * itemFind;
            var maxDrop = mDef.monsterField[countIdx].iData;
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
            // Multiply the resulting quantity by the multiplier instead of adjusting probability
            var finalCount = (int)Math.Max(1, Math.Round(baseCount * multiplier));

            var adjustedIdx = GameSessionMgr.gameSession.missionTierAdjustor.AdjustUpgradeItem(lootIdx, character, false, false, true);
            GameSessionMgr.gameSession.mapMgr.pickups.AddPickup(spawnLoc, adjustedIdx, finalCount);
        }
    }
}