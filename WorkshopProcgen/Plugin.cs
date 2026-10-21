using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace WorkshopProcgen
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.aedin.workshopprocgen";
        public const string PluginName = "Workshop Procgen";
        public const string PluginVersion = "0.5.3";

        internal static ManualLogSource Log { get; private set; }

        internal static ConfigEntry<bool> Enabled { get; private set; }
        internal static ConfigEntry<float> ReplacementChance { get; private set; }
        internal static ConfigEntry<float> FootprintTolerance { get; private set; }
        internal static ConfigEntry<bool> VerboseLogging { get; private set; }
        internal static ConfigEntry<bool> DebugForceScifiDesk { get; private set; }
        internal static ConfigEntry<KeyboardShortcut> RulesMenuHotkey { get; private set; }

        private Harmony _harmony;

        private void Awake()
        {
            Log = Logger;
            TraceLog.Install();

            Enabled = Config.Bind("General", "Enabled", true,
                "Substitute Workshop assets for native props during room generation.");
            ReplacementChance = Config.Bind("General", "ReplacementChance", 0.3f,
                new ConfigDescription("Probability that a matching native prop is replaced by a Workshop asset.",
                    new AcceptableValueRange<float>(0f, 1f)));
            FootprintTolerance = Config.Bind("Matching", "FootprintTolerance", 0.25f,
                new ConfigDescription("How much larger (per axis) a Workshop asset may be than the native prop it replaces. Smaller assets are always allowed and keep their own size.",
                    new AcceptableValueRange<float>(0f, 1f)));
            RulesMenuHotkey = Config.Bind("General", "RulesMenuHotkey", new KeyboardShortcut(KeyCode.F6),
                "Open the menu for choosing which Workshop assets replace native props, and how often.");
            VerboseLogging = Config.Bind("Debug", "VerboseLogging", false,
                "Log every candidate considered for replacement.");
            DebugForceScifiDesk = Config.Bind("Debug", "DebugForceScifiDesk", false,
                "MVP test: replace every generated native table with the Workshop 'Scifi Desk' (model e944764a-...), ignoring chance and footprint. Works even if Enabled=false.");

            try { WorkshopIndex.Initialize(); }
            catch (System.Exception e) { Log.LogError($"Workshop index init failed: {e}"); }

            try { MultiPaint.MultiPaintModule.Init(this); }
            catch (System.Exception e) { Log.LogError($"Multi-paint init failed: {e}"); }

            _harmony = new Harmony(PluginGuid);
            _harmony.PatchAll(typeof(Plugin).Assembly);

            Log.LogInfo($"{PluginName} {PluginVersion} loaded (Enabled={Enabled.Value}, Chance={ReplacementChance.Value}, menu={RulesMenuHotkey.Value})");
        }

        // The game can destroy the BepInEx manager object; patches must outlive it.
        private void OnDestroy()
        {
            TraceLog.Write("Plugin component destroyed; patches left in place.");
        }
    }
}
