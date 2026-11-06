using System;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace Oddhatter.MachineArbalest
{
    [BepInPlugin(Guid, Name, Version)]
    [BepInProcess("valheim.exe")]
    public sealed class MachineArbalestPlugin : BaseUnityPlugin
    {
        public const string Guid = "com.oddhatter.machinearbalest";
        public const string Name = "Gatling";
        public const string Version = "2.0.0";

        internal static ManualLogSource Log;

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<float> FireInterval;
        internal static ConfigEntry<float> BowFireInterval;
        internal static ConfigEntry<float> StaffFireInterval;
        internal static ConfigEntry<bool> RequireHold;
        internal static ConfigEntry<float> StaminaCostMultiplier;
        internal static ConfigEntry<float> EitrCostMultiplier;
        internal static ConfigEntry<float> AmmoCostMultiplier;
        internal static ConfigEntry<float> DamageMultiplier;
        internal static ConfigEntry<bool> Crossbows;
        internal static ConfigEntry<bool> Bows;
        internal static ConfigEntry<bool> ElementalStaffs;
        internal static ConfigEntry<bool> OnlyArbalest;
        internal static ConfigEntry<float> RecoilMultiplier;
        internal static ConfigEntry<float> DurabilityMultiplier;
        internal static ConfigEntry<bool> UseVanillaReload;
        internal static ConfigEntry<bool> DebugLogging;

        /// <summary>Wait this long after the last file event before reloading (editors write in several steps).</summary>
        private const float ConfigReloadDelay = 0.5f;

        private Harmony _harmony;
        private FileSystemWatcher _configWatcher;
        private volatile bool _configFileChanged; // set on a thread-pool thread by the watcher
        private float _configReloadAt = -1f;
        private bool _configLockedLogged;

        private void Awake()
        {
            Log = Logger;

            Enabled = Config.Bind("General", "Enabled", true,
                "Master switch. When false the mod changes nothing.");

            FireInterval = Config.Bind("General", "FireInterval", 0.10f,
                new ConfigDescription(
                    "Crossbows: seconds between automatic shots (0.10 = 10 shots/second). Ignored when UseVanillaReload is true. " +
                    "Shots are evaluated once per physics step (~0.02s), so values below that fire once per step (~50/s).",
                    new AcceptableValueRange<float>(0.01f, 10f)));

            BowFireInterval = Config.Bind("General", "BowFireInterval", 0.20f,
                new ConfigDescription(
                    "Bows: seconds between automatic shots (0.20 = 5 arrows/second). Every arrow flies as if fully drawn.",
                    new AcceptableValueRange<float>(0.01f, 10f)));

            StaffFireInterval = Config.Bind("General", "StaffFireInterval", 0.30f,
                new ConfigDescription(
                    "Elemental staffs (Dundr, Staff of Embers, Staff of Frost, Staff of Fracturing): seconds between automatic casts " +
                    "(0.30 = about 3/second). Each cast costs eitr; see EitrCostMultiplier.",
                    new AcceptableValueRange<float>(0.01f, 10f)));

            RequireHold = Config.Bind("General", "RequireHold", true,
                "true: fire only while primary attack is held; releasing stops immediately. " +
                "false: a click toggles automatic fire on/off (also stops on weapon change, no bolts, death, or opening any menu).");

            StaminaCostMultiplier = Config.Bind("Balance", "StaminaCostMultiplier", 1.0f,
                new ConfigDescription(
                    "Multiplier on the stamina each shot costs. Auto-fire skips the reload (crossbows) and the draw (bows), so each shot " +
                    "is charged what that reload or draw would have cost, plus the attack itself. 1 = vanilla, 0.5 = half, 0 = free.",
                    new AcceptableValueRange<float>(0f, 10f)));

            EitrCostMultiplier = Config.Bind("Balance", "EitrCostMultiplier", 1.0f,
                new ConfigDescription(
                    "Multiplier on the eitr each shot costs (staffs), including any reload or draw eitr a weapon uses. 1 = vanilla, 0.5 = half, 0 = free.",
                    new AcceptableValueRange<float>(0f, 10f)));

            AmmoCostMultiplier = Config.Bind("Balance", "AmmoCostMultiplier", 1.0f,
                new ConfigDescription(
                    "Bolts/arrows used per shot. 1 = vanilla (one per shot), 0.1 = one every 10 shots, 0 = never used up " +
                    "(you still need at least one in your inventory; its type still sets the damage and effects).",
                    new AcceptableValueRange<float>(0f, 1f)));

            DamageMultiplier = Config.Bind("Balance", "DamageMultiplier", 1.0f,
                new ConfigDescription(
                    "Multiplier on projectile damage. 1 = unchanged.",
                    new AcceptableValueRange<float>(0f, 100f)));

            RecoilMultiplier = Config.Bind("Balance", "RecoilMultiplier", 1.0f,
                new ConfigDescription(
                    "Multiplier on the pushback the shooter receives per shot. 1 = vanilla, 0 = none.",
                    new AcceptableValueRange<float>(0f, 10f)));

            DurabilityMultiplier = Config.Bind("Balance", "DurabilityMultiplier", 1.0f,
                new ConfigDescription(
                    "Multiplier on weapon durability lost per shot. 1 = vanilla, 0.1 = a tenth (offsets a 10x fire rate), 0 = no wear.",
                    new AcceptableValueRange<float>(0f, 10f)));

            Crossbows = Config.Bind("Weapons", "Crossbows", true,
                "Automatic fire for crossbows (Arbalest, Ripper, gold crossbows, modded ones). See also OnlyArbalest.");

            Bows = Config.Bind("Weapons", "Bows", true,
                "Automatic fire for bows. Arrows fly as if fully drawn, and each one costs the stamina of a full draw.");

            ElementalStaffs = Config.Bind("Weapons", "ElementalStaffs", true,
                "Automatic fire for elemental magic staffs (Dundr, Staff of Embers, Staff of Frost, Staff of Fracturing). " +
                "Blood magic staffs (summons, shields) are never affected.");

            OnlyArbalest = Config.Bind("Weapons", "OnlyArbalest", false,
                "Crossbows only. false: every crossbow (every weapon using the Crossbows skill) is affected. " +
                "true: only the vanilla Arbalest.");

            UseVanillaReload = Config.Bind("Weapons", "UseVanillaReload", false,
                "Crossbows only (bows and staffs always use machine mode). " +
                "false: machine-gun mode, shots are paced by FireInterval and no reload occurs. " +
                "true: the normal reload (including FastArbalest's) paces the shots; holding still auto-fires " +
                "and the multipliers above still apply.");

            DebugLogging = Config.Bind("Diagnostics", "DebugLogging", false,
                "Write what the mod sees (equipped weapon, trigger state, why a shot was or wasn't fired) to LogOutput.log. " +
                "Only logs when something changes.");

            _harmony = new Harmony(Guid);
            _harmony.PatchAll(typeof(MachineArbalestPlugin).Assembly);
            foreach (System.Reflection.MethodBase method in _harmony.GetPatchedMethods())
            {
                Log.LogInfo($"Patched {method.DeclaringType?.Name}.{method.Name}");
            }
            WatchConfigFile();
            Log.LogInfo($"{Name} {Version} loaded.");
        }

        private void Update()
        {
            if (_configFileChanged)
            {
                _configFileChanged = false;
                _configReloadAt = Time.realtimeSinceStartup + ConfigReloadDelay;
            }
            if (_configReloadAt >= 0f && Time.realtimeSinceStartup >= _configReloadAt)
            {
                _configReloadAt = -1f;
                ReloadConfigFromDisk();
            }
        }

        private void OnDestroy()
        {
            _configWatcher?.Dispose();
            _harmony?.UnpatchSelf();
        }

        /// <summary>
        /// Picks up edits to the .cfg file made while the game runs (text editor, r2modman config editor).
        /// Settings are read on every shot, so reloaded values apply immediately.
        /// </summary>
        private void WatchConfigFile()
        {
            string path = Config.ConfigFilePath;
            _configWatcher = new FileSystemWatcher(Path.GetDirectoryName(path), Path.GetFileName(path))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                IncludeSubdirectories = false,
            };
            _configWatcher.Changed += OnConfigFileEvent;
            _configWatcher.Created += OnConfigFileEvent;
            _configWatcher.Renamed += OnConfigFileEvent;
            _configWatcher.EnableRaisingEvents = true;
        }

        private void OnConfigFileEvent(object sender, FileSystemEventArgs e)
        {
            _configFileChanged = true; // handled on the main thread in Update
        }

        private void ReloadConfigFromDisk()
        {
            if (!File.Exists(Config.ConfigFilePath))
            {
                return;
            }
            // ConfigFile.Reload sets each entry; with SaveOnConfigSet every changed value would rewrite the file
            // and re-trigger this watcher. The file already holds these values, so don't save while reloading.
            bool saveOnSet = Config.SaveOnConfigSet;
            Config.SaveOnConfigSet = false;
            try
            {
                Config.Reload();
                _configLockedLogged = false;
                Log.LogInfo("Configuration reloaded from disk.");
            }
            catch (IOException ex)
            {
                // File still being written or locked by an editor: keep retrying, but only log once.
                if (!_configLockedLogged)
                {
                    _configLockedLogged = true;
                    Log.LogWarning($"Config file busy, will retry: {ex.Message}");
                }
                _configReloadAt = Time.realtimeSinceStartup + ConfigReloadDelay;
            }
            catch (Exception ex)
            {
                Log.LogError($"Failed to reload configuration: {ex.Message}");
            }
            finally
            {
                Config.SaveOnConfigSet = saveOnSet;
            }
        }
    }
}
