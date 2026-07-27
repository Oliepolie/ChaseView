using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace ChaseView
{
    [BepInPlugin(Guid, DisplayName, Version)]
    public sealed class Plugin : BaseUnityPlugin
    {
        /// <summary>
        /// FROZEN. The GUID names BepInEx/config/com.olie.chaseview.cfg and identifies our Harmony
        /// instance. Renaming it orphans every user's settings - they silently revert to defaults on
        /// a file the user can still see sitting there. The DISPLAY NAME and the deploy folder are
        /// the free rebrand levers; this is not one of them.
        /// </summary>
        public const string Guid = "com.olie.chaseview";

        /// <summary>Free to change - shows up in the BepInEx console and plugin list, nothing else.</summary>
        public const string DisplayName = "ChaseView";

        public const string Version = "1.1.0";

        internal static ManualLogSource Log;

        /// <summary>
        /// BepInEx's own plugin GameObject, which is DontDestroyOnLoad. Any MonoBehaviour a feature
        /// needs must live HERE. One created fresh is destroyed at the first scene transition and the
        /// feature goes permanently quiet with no error at all - at exactly the moment a mission
        /// loads, which is when you were about to test it.
        /// </summary>
        internal static GameObject HostObject;

        private readonly List<Feature> _features = new List<Feature>();
        private readonly List<Feature> _pending = new List<Feature>();
        private Harmony _harmony;
        private float _nextTry;
        private int _attempts;
        private const int MaxAttempts = 180;

        private void Awake()
        {
            Log = Logger;
            HostObject = gameObject;
            _harmony = new Harmony(Guid);

            // The ONLY trustworthy freshness signal for a log. File timestamps and the BepInEx log
            // header have both lied on these projects; a leftover -batchmode process holding the log
            // open once caused a milestone to be falsely marked PASSED off last run's output.
            Log.LogInfo($"{DisplayName} v{Version} loaded");
            LogEnvironment();

            foreach (var f in Discover())
            {
                _features.Add(f);
                try { f.Bind(Config); }
                catch (Exception ex) { Log.LogError($"[{f.Name}] config bind failed: {ex}"); }
            }

            DumpResolvedConfig();

            foreach (var f in _features)
            {
                if (!f.Enabled) { Log.LogInfo($"[{f.Name}] disabled by config"); continue; }

                // A dependency that ships as a BepInEx plugin can be checked RIGHT NOW - every
                // plugin's info is in the Chainloader before any plugin Awake runs. If it is absent
                // the mod is simply not installed, so disable this feature immediately rather than
                // polling for three minutes for a type that will never load.
                if (f.RequiredPlugin != null && !Chainloader.PluginInfos.ContainsKey(f.RequiredPlugin))
                {
                    Log.LogInfo($"[{f.Name}] optional dependency {f.RequiredPlugin} is not installed - feature inactive");
                    continue;
                }

                // Apply NOW if the type is already loaded rather than always deferring to the poll.
                // The poll costs up to a full second, and for anything patching another BepInEx
                // plugin that is far too late: plugins load in one pass, so by the first poll every
                // other plugin's Awake has already been and gone. Features waiting on a NON-plugin
                // assembly (Blueprinter addons, loaded later) fall through to the poll, which is
                // what it is for.
                if (f.RequiredType == null || AccessTools.TypeByName(f.RequiredType) != null) TryApply(f);
                else _pending.Add(f);
            }

            if (_pending.Count > 0)
                Log.LogInfo($"[chaseview] {_pending.Count} feature(s) waiting on another mod's assembly");
        }

        /// <summary>
        /// Find every feature by reflection instead of a hand-maintained list.
        ///
        /// This is the mechanism behind the project's core promise: a feature lives entirely in one
        /// folder, and deleting that folder removes the feature. A central `new FeatureX()` list would
        /// make every deletion a compile error in a shared file, which is exactly the coupling we are
        /// trying not to have.
        ///
        /// Ordered by type name so the .cfg section order and the startup dump are stable across runs
        /// - reflection order is not guaranteed and an unstable config file churns diffs for no reason.
        /// </summary>
        private static IEnumerable<Feature> Discover()
        {
            Type[] types;
            try
            {
                types = Assembly.GetExecutingAssembly().GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                // A feature that integrates with another mod names that mod's types in its own
                // signature. When the mod is absent, loading THAT type fails - and an unguarded
                // GetTypes() would take every other feature down with it. Keep what loaded.
                types = ex.Types.Where(t => t != null).ToArray();
                Log.LogInfo($"[chaseview] {ex.Types.Length - types.Length} type(s) could not load "
                          + "(expected when an integration's target mod is not installed)");
            }

            var found = new List<Feature>();
            foreach (var t in types.Where(t => !t.IsAbstract && typeof(Feature).IsAssignableFrom(t))
                                   .OrderBy(t => t.Name, StringComparer.Ordinal))
            {
                try { found.Add((Feature)Activator.CreateInstance(t)); }
                catch (Exception ex) { Log.LogError($"[chaseview] could not construct feature {t.Name}: {ex}"); }
            }

            Log.LogInfo($"[chaseview] discovered {found.Count} feature(s): {string.Join(", ", found.Select(f => f.Name).ToArray())}");
            return found;
        }

        private void Update()
        {
            if (_pending.Count == 0 || Time.realtimeSinceStartup < _nextTry) return;
            _nextTry = Time.realtimeSinceStartup + 1f;

            if (++_attempts > MaxAttempts)
            {
                foreach (var f in _pending)
                    Log.LogInfo($"[{f.Name}] gave up waiting for {f.RequiredType} - that mod is presumably not installed");
                _pending.Clear();
                return;
            }

            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                var f = _pending[i];
                if (AccessTools.TypeByName(f.RequiredType) == null) continue;
                _pending.RemoveAt(i);
                TryApply(f);
            }
        }

        /// <summary>One feature blowing up must not take the others with it.</summary>
        private void TryApply(Feature f)
        {
            try
            {
                f.Apply(_harmony);
                Log.LogInfo($"[{f.Name}] applied");
            }
            catch (Exception ex)
            {
                Log.LogError($"[{f.Name}] FAILED to apply and is now inactive: {ex}");
            }
        }

        /// <summary>
        /// Pin the environment. Every "measured" number in the modding notes was established on
        /// Unity 2022.3.62f2 / URP 14.x / Mono; if this line disagrees with that, treat those numbers
        /// as unverified rather than as facts.
        ///
        /// Deliberately does NOT read GameManager.gameState or GameManager.IsHeadless - both are set
        /// later in init, so reading them here gets the pre-init value. Features that need them sample
        /// at a defined epoch (mission loaded) instead.
        /// </summary>
        private static void LogEnvironment()
        {
            Log.LogInfo($"[env] unity={Application.unityVersion} game={Application.version} "
                      + $"colorSpace={QualitySettings.activeColorSpace} layer6={LayerMask.LayerToName(6)}");
        }

        /// <summary>
        /// Dump what the config actually RESOLVED to, unconditionally, every launch.
        ///
        /// The single highest-value line in the plugin. config.Bind writes a default ONLY when the key
        /// is absent, so a .cfg from an older build keeps its old values forever while the regenerated
        /// comment cheerfully advertises the new default. Without this dump every later log is open to
        /// misinterpretation, and you can spend a whole session measuring a configuration you did not
        /// know you were running. It is also what makes a friend's bug report self-describing.
        ///
        /// Not behind a verbose flag. Two dozen lines once per session is nothing.
        /// </summary>
        private void DumpResolvedConfig()
        {
            Log.LogInfo($"===== {DisplayName} v{Version} - resolved config =====");
            foreach (var f in _features)
            {
                Log.LogInfo($"  [{f.Name}] Enabled = {f.Enabled}  (parity: {f.Parity})");
                f.DumpResolved((k, v) => Log.LogInfo($"      {k} = {v}"));
            }
            Log.LogInfo("==================================================");
        }
    }
}
