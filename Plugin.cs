using System;
using System.IO;
using System.Reflection;
using System.Timers;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.NetLauncher.Common;
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

    internal static ConfigEntry<bool> SkipMissionMages;
    internal static ConfigEntry<bool> SkipWanderingMages;
    internal static ConfigEntry<bool> SkipOptionalMages;
    internal static ConfigEntry<bool> SkipInvisibleMages;
    internal static ConfigEntry<bool> SpawnAtFinalLocation;
    internal static ConfigEntry<bool> DropLootRelativeAmount;
    internal static ConfigEntry<float> DropLootMultiplier;
    internal static ConfigEntry<bool> ReduceBossHp;
    
    private FileSystemWatcher _configWatcher;
    private Timer _debounceTimer;

    public override void Load()
    {
        Instance = this;

        SkipMissionMages = Config.Bind("General", "SkipMissionMages", true, "Skip hunt phases for main mission mages (recommended).");
        SkipWanderingMages = Config.Bind("General", "SkipWanderingMages", false, "Skip hunt phases for wandering/roaming mages.");
        SkipOptionalMages = Config.Bind("General", "SkipOptionalMages", false, "Skip hunt phases for optional mages.");
        SkipInvisibleMages = Config.Bind("General", "SkipInvisibleMages", false, "Skip hunt phases for invisible mission mages.");
        SpawnAtFinalLocation = Config.Bind("General", "SpawnAtFinalLocation", true, "Teleport mission mages directly to their final arena location instead of spawning at zone 0.");
        DropLootRelativeAmount = Config.Bind("General", "DropLootRelativeAmount", true, "Drop loot relative to the amount of skipped phases when the mage dies.");
        DropLootRelativeAmount = Config.Bind("Loot", "DropLootRelativeAmount", true, "Drop loot for each skipped hunt phase.");
        DropLootMultiplier = Config.Bind("Loot", "DropLootMultiplier", 1.0f, new ConfigDescription("Multiplier for the amount of silver and probability of materials dropped from skipped phases.", new AcceptableValueRange<float>(0.1f, 10.0f)));
        ReduceBossHp = Config.Bind("General", "ReduceBossHP", true, "Start boss fight with reduced HP (simulates hunt damage).");
        
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
        SaS2ModOptions.SaS2ModOptions.RegisterConfig(SkipMissionMages, "Skip Hunt Chases", "Skip Mission Mages", order += 1);
        SaS2ModOptions.SaS2ModOptions.RegisterConfig(SkipWanderingMages, "Skip Hunt Chases", "Skip Wandering Mages", order += 1);
        SaS2ModOptions.SaS2ModOptions.RegisterConfig(SkipOptionalMages, "Skip Hunt Chases", "Skip Optional Mages", order += 1);
        SaS2ModOptions.SaS2ModOptions.RegisterConfig(SkipInvisibleMages, "Skip Hunt Chases", "Skip Invisible Mages", order += 1);
        SaS2ModOptions.SaS2ModOptions.RegisterConfig(SpawnAtFinalLocation, "Skip Hunt Chases", "Spawn At Final Location", order += 1);
        SaS2ModOptions.SaS2ModOptions.RegisterConfig(DropLootRelativeAmount, "Skip Hunt Chases", "Extra Loot Based on Skipped Phases", order += 1);
        SaS2ModOptions.SaS2ModOptions.RegisterConfig(DropLootMultiplier, "Skip Hunt Chases", "Loot Multiplier", order += 1);
        SaS2ModOptions.SaS2ModOptions.RegisterConfig(ReduceBossHp, "Skip Hunt Chases", "Reduce Boss HP", order += 1);
        // ReSharper restore RedundantAssignment
    }

    public override bool Unload()
    {
        _configWatcher?.Dispose();
        _debounceTimer?.Dispose();
        return base.Unload();
    }
}