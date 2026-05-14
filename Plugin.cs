using System;
using System.IO;
using System.Reflection;
using System.Timers;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.NET.Common;
using HarmonyLib;
using ProjectMage;
using ProjectMage.gamestate.mage;
using ProjectMage.Monsters;
using System.Runtime.CompilerServices;

namespace SaS2SkipHuntChases;

[BepInPlugin(PluginInfo.PluginGuid, PluginInfo.PluginName, PluginInfo.PluginVersion)]
// ReSharper disable once StringLiteralTypo
[BepInDependency("amione.SaS2ModOptions", BepInDependency.DependencyFlags.SoftDependency)]
// ReSharper disable once ClassNeverInstantiated.Global
public class Plugin : BasePlugin
{
    internal static Plugin Instance;
    internal static MethodInfo GetPathNodeMethod;
    internal static MethodInfo SetPhaseMethod;
    internal static MethodInfo GetMaxHpMethod;
    internal static MethodInfo GetAddCharToArenaIdxMethod;
    internal static MethodInfo OnAddCharToArenaMethod;

    internal static ConfigEntry<bool> SkipNamedMages;
    internal static ConfigEntry<bool> SkipFatedMages;
    internal static ConfigEntry<bool> SkipNamelessMages;
    internal static ConfigEntry<bool> SkipGauntletMages;
    internal static ConfigEntry<bool> SkipWanderingMages;
    internal static ConfigEntry<bool> SpawnAtFinalLocation;
    internal static ConfigEntry<bool> DropLootRelativeAmount;
    internal static ConfigEntry<float> DropLootMultiplier;
    internal static ConfigEntry<bool> ReduceBossHp;
    internal static ConfigEntry<float> BossHpMultiplier;

    private FileSystemWatcher _configWatcher;
    private Timer _debounceTimer;

    public override void Load()
    {
        Instance = this;

        SkipNamedMages          = Config.Bind("General", "SkipNamedMages",          true,   "Skip hunt phases for named mission mages (e.g. Arzhan-Tin, Celus Zend).");
        SkipFatedMages          = Config.Bind("General", "SkipFatedMages",          true,   "Skip hunt phases for fated mages (tiered mages shown with a tier number in mission select).");
        SkipNamelessMages       = Config.Bind("General", "SkipNamelessMages",       true,   "Skip hunt phases for nameless mission mages (repeatable hunts, reward token_nameless).");
        SkipGauntletMages       = Config.Bind("General", "SkipGauntletMages",       true,   "Skip hunt phases for gauntlet mages (each one immediately starts a boss fight).");
        SkipWanderingMages      = Config.Bind("General", "SkipWanderingMages",      false,  "Skip hunt phases for wandering/roaming mages.");
        SpawnAtFinalLocation    = Config.Bind("General", "SpawnAtFinalLocation",    false,  "Teleport the primary mission mage directly to its arena entrance when skipping. Off by default, mages spawn at zone 0 and walk to the arena naturally. Only affects the non-invisible target mage; companion mages in the same hunt should be unaffected.");
        DropLootRelativeAmount  = Config.Bind("Loot",    "DropLootRelativeAmount",  true,   "Drop bonus loot on death to compensate for skipped hunt phases.");
        DropLootMultiplier      = Config.Bind("Loot",    "DropLootMultiplier",      1.0f,   new ConfigDescription("Scales the bonus loot dropped per skipped phase. 1.0 = one extra phase-equivalent drop total.", new AcceptableValueRange<float>(0.1f, 10.0f)));
        ReduceBossHp            = Config.Bind("General", "ReduceBossHP",            true,   "Start boss fight with reduced HP (simulates hunt damage).");
        BossHpMultiplier        = Config.Bind("General", "BossHpMultiplier",        1.0f,   "Multiply mage starting HP by this value after the hunt-damage reduction.");

        var modOptionsType = Type.GetType("SaS2ModOptions.SaS2ModOptions, amione.SaS2ModOptions");
        if (modOptionsType != null)
        {
            TryRegisterModOptions();
            Instance.Log.LogInfo("Successfully registered configs with SaS2ModOptions.");
        }
        else
        {
            Instance.Log.LogInfo("Mod Options not installed; config file only.");
        }

        GetPathNodeMethod = AccessTools.Method(typeof(Mage), "GetPathNode");
        if (GetPathNodeMethod == null)
            Instance.Log.LogWarning("GetPathNode not found, SpawnAtFinalLocation will be disabled.");

        SetPhaseMethod = AccessTools.Method(typeof(Mage), "SetPhase");
        if (SetPhaseMethod == null)
            Instance.Log.LogWarning("SetPhase not found, hunt phase skipping may not work correctly.");

        GetMaxHpMethod = AccessTools.Method(typeof(GameMonster), "GetMaxHP");
        if (GetMaxHpMethod == null)
            Instance.Log.LogWarning("GetMaxHP not found, HP capping on boss promotion will be skipped.");

        GetAddCharToArenaIdxMethod = AccessTools.Method(
            typeof(ProjectMage.map.arena.MapArenas), "GetAddCharToArenaIdx");
        if (GetAddCharToArenaIdxMethod == null)
            Instance.Log.LogWarning("GetAddCharToArenaIdx not found, boss promotion may require one extra warp.");

        OnAddCharToArenaMethod = AccessTools.Method(typeof(NetworkEvents), "OnAddCharToArena");
        if (OnAddCharToArenaMethod == null)
            Instance.Log.LogWarning("OnAddCharToArena not found, boss promotion may require one extra warp.");

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
            Instance.Log.LogWarning("Could not determine config directory, live reload disabled.");
        }

        var harmony = new Harmony(PluginInfo.PluginGuid);
        harmony.PatchAll();
        Instance.Log.LogInfo($"{PluginInfo.PluginName} v{PluginInfo.PluginVersion} loaded, hunt phases will be skipped.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void TryRegisterModOptions()
    {
        // ReSharper disable RedundantAssignment
        var order = 0;
        SaS2ModOptions.SaS2ModOptions.RegisterConfig(SkipNamedMages,         "Skip Hunt Chases", "Skip Named Mages",                   order += 1);
        SaS2ModOptions.SaS2ModOptions.RegisterConfig(SkipFatedMages,         "Skip Hunt Chases", "Skip Fated Mages",                   order += 1);
        SaS2ModOptions.SaS2ModOptions.RegisterConfig(SkipNamelessMages,      "Skip Hunt Chases", "Skip Nameless Mages",                order += 1);
        SaS2ModOptions.SaS2ModOptions.RegisterConfig(SkipGauntletMages,      "Skip Hunt Chases", "Skip Gauntlet Mages",                order += 1);
        SaS2ModOptions.SaS2ModOptions.RegisterConfig(SkipWanderingMages,     "Skip Hunt Chases", "Skip Wandering Mages",               order += 1);
        SaS2ModOptions.SaS2ModOptions.RegisterConfig(SpawnAtFinalLocation,   "Skip Hunt Chases", "Spawn At Final Location",            order += 1);
        SaS2ModOptions.SaS2ModOptions.RegisterConfig(DropLootRelativeAmount, "Skip Hunt Chases", "Extra Loot Based on Skipped Phases", order += 1);
        SaS2ModOptions.SaS2ModOptions.RegisterConfig(DropLootMultiplier,     "Skip Hunt Chases", "Loot Multiplier",                    order += 1);
        SaS2ModOptions.SaS2ModOptions.RegisterConfig(ReduceBossHp,           "Skip Hunt Chases", "Reduce Boss HP",                     order += 1);
        SaS2ModOptions.SaS2ModOptions.RegisterConfig(BossHpMultiplier,       "Skip Hunt Chases", "Boss HP Multiplier",                 order += 1);
        // ReSharper restore RedundantAssignment
    }

    public override bool Unload()
    {
        _configWatcher?.Dispose();
        _debounceTimer?.Dispose();
        return base.Unload();
    }
}
