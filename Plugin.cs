using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Timers;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.NetLauncher.Common;
using Bestiary.monsters;
using Chronicler.missions;
using Common;
using HarmonyLib;
using ProjectMage;
using ProjectMage.character;
using ProjectMage.gamestate;
using ProjectMage.gamestate.arenastate;
using ProjectMage.gamestate.mage;
using ProjectMage.Monsters;

namespace SaS2SkipHuntChases;

[BepInPlugin(PluginInfo.PluginGuid, PluginInfo.PluginName, PluginInfo.PluginVersion)]
public class Plugin : BasePlugin
{
    internal static Plugin Instance;
    internal static MethodInfo GetPathNodeMethod;
    internal static MethodInfo SetPhaseMethod;
    internal static MethodInfo GetMaxHpMethod;
    internal static MethodInfo GetAddCharToArenaIdxMethod;
    internal static MethodInfo OnAddCharToArenaMethod;

    internal static ConfigEntry<bool> SkipMissionMages;
    internal static ConfigEntry<bool> SkipWanderingMages;
    internal static ConfigEntry<bool> SkipOptionalMages;
    internal static ConfigEntry<bool> SkipInvisibleMages;
    internal static ConfigEntry<bool> SpawnAtFinalLocation;

    private FileSystemWatcher _configWatcher;
    private Timer _debounceTimer;

    public override void Load()
    {
        Instance = this;

        SkipMissionMages = Config.Bind("General", "SkipMissionMages", true,
            "Skip hunt phases for main mission mages (recommended).");
        SkipWanderingMages = Config.Bind("General", "SkipWanderingMages", false,
            "Skip hunt phases for wandering/roaming mages.");
        SkipOptionalMages = Config.Bind("General", "SkipOptionalMages", false,
            "Skip hunt phases for optional mages.");
        SkipInvisibleMages = Config.Bind("General", "SkipInvisibleMages", false,
            "Skip hunt phases for invisible mission mages.");
        SpawnAtFinalLocation = Config.Bind("General", "SpawnAtFinalLocation", true,
            "Teleport mission mages directly to their final arena location instead of spawning at zone 0.");

        GetPathNodeMethod = AccessTools.Method(typeof(Mage), "GetPathNode");
        if (GetPathNodeMethod == null)
            Instance.Log.LogWarning("GetPathNode not found — SpawnAtFinalLocation will be disabled.");

        SetPhaseMethod = AccessTools.Method(typeof(Mage), "SetPhase");
        if (SetPhaseMethod == null)
            Instance.Log.LogWarning("SetPhase not found — hunt phase skipping may not work correctly.");

        GetMaxHpMethod = AccessTools.Method(typeof(GameMonster), "GetMaxHP");
        if (GetMaxHpMethod == null)
            Instance.Log.LogWarning("GetMaxHP not found — HP capping on boss promotion will be skipped.");

        GetAddCharToArenaIdxMethod = AccessTools.Method(
            typeof(ProjectMage.map.arena.MapArenas), "GetAddCharToArenaIdx");
        if (GetAddCharToArenaIdxMethod == null)
            Instance.Log.LogWarning("GetAddCharToArenaIdx not found — boss promotion may require one extra warp.");

        OnAddCharToArenaMethod = AccessTools.Method(typeof(NetworkEvents), "OnAddCharToArena");
        if (OnAddCharToArenaMethod == null)
            Instance.Log.LogWarning("OnAddCharToArena not found — boss promotion may require one extra warp.");

        var configDirectory = Path.GetDirectoryName(Config.ConfigFilePath);
        var configFileName  = Path.GetFileName(Config.ConfigFilePath);
        if (!string.IsNullOrEmpty(configDirectory))
        {
            _configWatcher = new FileSystemWatcher(configDirectory, configFileName)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true
            };
            _debounceTimer = new Timer(1000) { AutoReset = false };
            _debounceTimer.Elapsed += (_, _) =>
            {
                Config.Reload();
                Instance.Log.LogInfo("Configuration reloaded.");
            };
            _configWatcher.Changed += (_, _) => { _debounceTimer.Stop(); _debounceTimer.Start(); };
        }
        else
        {
            Instance.Log.LogWarning("Could not determine config directory — live reload disabled.");
        }

        var harmony = new Harmony(PluginInfo.PluginGuid);
        harmony.PatchAll();
        Instance.Log.LogInfo($"{PluginInfo.PluginName} v{PluginInfo.PluginVersion} loaded — hunt phases will be skipped.");
    }

    public override bool Unload()
    {
        _configWatcher?.Dispose();
        _debounceTimer?.Dispose();
        return base.Unload();
    }
}

internal static class MageSkipHelper
{
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
                if (Plugin.GetMaxHpMethod != null)
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
        catch (System.Exception ex)
        {
            Plugin.Instance.Log.LogWarning($"TryPromoteToBoss failed: {ex.Message}");
            Plugin.SetPhaseMethod?.Invoke(mage, [6]);
        }
    }

    // Change values of mage so the hunt gets finished
    internal static void MarkCyclesComplete(Mage mage)
    {
        mage.cycle       = mage.totalCycles;
        mage.cycleDamage = mage.cycleMaxDamage + 1f;
        mage.totalDamage = mage.monsterHP;
    }
}

// MISSION MAGES: patch SetMissionTarget, which is the last step in CreateMages() after Activate() and after character.loc is set.
// We override position here, then immediately run the same arena-promotion that NextCycle() would have run after the final warp.
[HarmonyPatch(typeof(MageMgr), "SetMissionTarget")]
public static class SetMissionTargetPatch
{
    [HarmonyPostfix]
    // ReSharper disable once InconsistentNaming
    public static void Postfix(MageMgr __instance, MissionTarget target, int mageIdx)
    {
        if (!NetworkMgr.Instance.IsHost()) return;

        var mage = __instance.mage[mageIdx];

        if (!MageSkipHelper.ShouldSkipMissionMage(mage)) return;

        MageSkipHelper.MarkCyclesComplete(mage);

        var character = CharMgr.character[mage.charIdx];

        // Teleport to final path node before the arena lookup so GetAddCharToArenaIdx can match the right arena by position.
        if (Plugin.SpawnAtFinalLocation.Value && Plugin.GetPathNodeMethod != null && mage.hasCustomPath)
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
            catch (System.Exception ex)
            {
                Plugin.Instance.Log.LogWarning($"SpawnAtFinalLocation failed: {ex.Message}");
            }
        }

        MageSkipHelper.TryPromoteToBoss(character, mage);

        Plugin.Instance.Log.LogDebug($"Mission mage {mage.charIdx} hunt phases skipped.");
    }
}

// WANDERING MAGES: patch AddMage, which is how non-mission mages get registered.
// SetMissionTarget is never called for them, so this is the correct hook.
// Wandering mages have no custom path and no arena to promote into, so we just force cycle completion and BATTLE_2 immediately.
[HarmonyPatch(typeof(MageMgr), "AddMage")]
public static class AddMagePatch
{
    [HarmonyPostfix]
    // ReSharper disable once InconsistentNaming
    public static void Postfix(MageMgr __instance, Character character, int __result)
    {
        if (!NetworkMgr.Instance.IsHost()) return;
        if (__result < 0) return; // AddMage returns -1 if the slot was full

        if (!MageSkipHelper.ShouldSkipWanderingMage()) return;

        var mage = __instance.mage[__result];

        MageSkipHelper.MarkCyclesComplete(mage);

        // Wandering mages have no custom path and no designated arena, so we just park them in BATTLE_2.
        // They'll fight in place like a boss and won't try to flee to any zone.
        Plugin.SetPhaseMethod?.Invoke(mage, [6]);

        Plugin.Instance.Log.LogDebug($"Wandering mage {mage.charIdx} hunt phases skipped.");
    }
}

// Safety net: catches mages that were already alive when the mod was first
// loaded (e.g. pre-existing wandering mages from an unmodded save), plus
// anything that slipped through the spawn patches via network events.
[HarmonyPatch(typeof(Mage), "Update")]
public static class MageUpdatePatch
{
    // Tracks charIdx values we have already promoted.
    // A mage dying and its slot being reused will go through Activate() -> AddMage/SetMissionTarget again,
    // so the spawn patches will catch it cleanly the second time around.
    private static readonly HashSet<int> Handled = [];

    [HarmonyPrefix]
    // ReSharper disable once InconsistentNaming
    public static void Prefix(Mage __instance)
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