using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using ChaseView.Core;

namespace ChaseView.Features
{
    /// <summary>
    /// An Ace Combat 7 style weapon/countermeasure readout in the bottom-right corner: every station
    /// listed at once with its short code and ammo, a marker on the selected one, countermeasures,
    /// and a damage percentage — sat above the game's own top-down aircraft diagram.
    ///
    /// WHY THIS IS NEW BEHAVIOUR, NOT A RESTYLE  [decompiled 2026-07-26]
    ///   Vanilla's TopRightPanel shows exactly ONE weapon — the selected one — via WeaponStatus
    ///   (icon + full weaponName + ammo + reload bar), with countermeasures beside it and an energy
    ///   capacitor bar above. The whole-loadout-at-a-glance list is the thing the game does not do.
    ///
    /// WHAT THE DATA ALREADY GIVES US, so none of this is invented:
    ///   WeaponInfo.shortName      - the code ("IRM-S2"); sits right beside the full weaponName.
    ///   Aircraft.weaponStations   - already merged one-per-WeaponInfo across bay/pylon carriage.
    ///   WeaponStation             - Ammo, FullAmmo, Cargo, Reloading.
    ///   WeaponManager.currentWeaponStation - what the selection marker points at.
    ///   CountermeasureManager     - per-station displayName/ammo/maxAmmo plus activeIndex.
    ///   AircraftParameters.StatusDisplay   - the per-airframe top-down silhouette, vanilla.
    ///
    /// PARITY: Local. Reads this client's own aircraft and draws on this client's screen.
    /// </summary>
    internal sealed class WeaponPanel : Feature
    {
        public override string Name => "WeaponPanel";

        // The Enabled entry shows this text, so it has to name EVERYTHING the switch gates, not just
        // the headline feature. Plugin.cs skips Apply() entirely when a feature is off, so turning
        // this off also removes the damage-diagram patch below - which is not guessable from a
        // section called "WeaponPanel", and read as redundant with ShowWeaponList until it was.
        public override string Description =>
            "MASTER SWITCH for two things: the Ace Combat style weapon readout (ShowWeaponList) and "
          + "the always-on damage diagram (ShowDamageDiagram). Off disables both and restores the "
          + "stock HUD regardless of the settings below. Takes effect at startup, not live. "
          + "LOCAL only - display on your own machine.";

        internal static ConfigEntry<bool> ShowPanel;
        private ConfigEntry<bool> _showPanel;
        internal static ConfigEntry<bool> HideVanillaPanel;
        internal static ConfigEntry<bool> ShowDiagram;
        internal static ConfigEntry<bool> ShowDamagePercent;
        internal static ConfigEntry<bool> ShowCargo;
        internal static ConfigEntry<float> DiagramOpacity;
        internal static ConfigEntry<float> DiagramDamageBoost;
        internal static ConfigEntry<float> PanelScale;

        protected override void BindOptions(ConfigFile config)
        {
            _showPanel = config.Bind(Name, "ShowWeaponList", false, Cfg.Basic(
                "Show the weapon list in the bottom-right corner. Off leaves the stock HUD alone.", 1));
            ShowPanel = _showPanel;

            // DEFAULT FALSE on purpose: with it off, BOTH readouts are on screen at once, which is
            // what makes the new panel verifiable against the one it is meant to replace. Flip it
            // once you are happy the new one carries everything you need.
            HideVanillaPanel = config.Bind(Name, "HideVanillaWeaponPanel", false, Cfg.Basic("Hide the stock top-right weapon and countermeasure blocks.", 2));

            ShowDiagram = config.Bind(Name, "ShowDamageDiagram", false, Cfg.Basic(
                "Keep the aircraft damage diagram on screen, coloured green through red. "
              + "Vanilla only flashes it for ~10s after a hit.", 3));
            ShowDamagePercent = config.Bind(Name, "ShowDamagePercent", true, Cfg.Adv("Show a damage percentage under the weapon list."));

            ShowCargo = config.Bind(Name, "ShowCargo", true, Cfg.Adv("Include the cargo station in the list."));

            DiagramOpacity = config.Bind(Name, "DiagramOpacity", 0.45f, Cfg.Basic("How solid the aircraft diagram is when undamaged.", new AcceptableValueRange<float>(0.05f, 1f), 5));

            DiagramDamageBoost = config.Bind(Name, "DiagramDamageBoost", 0.5f, Cfg.Adv("How much more solid a part becomes as it is damaged.", new AcceptableValueRange<float>(0f, 1f)));

            PanelScale = config.Bind(Name, "PanelScale", 1f, Cfg.Adv("Size of the weapon list.", new AcceptableValueRange<float>(0.5f, 2f)));
        }

        public override void DumpResolved(Action<string, object> kv)
        {
            kv("ShowWeaponList", _showPanel.Value);
            kv("HideVanillaWeaponPanel", HideVanillaPanel.Value);
            kv("ShowDamageDiagram", ShowDiagram.Value);
            kv("ShowDamagePercent", ShowDamagePercent.Value);
            kv("ShowCargo", ShowCargo.Value);
            kv("DiagramOpacity", DiagramOpacity.Value);
            kv("DiagramDamageBoost", DiagramDamageBoost.Value);
            kv("PanelScale", PanelScale.Value);
        }

        public override void Apply(Harmony harmony)
        {
            Plugin.HostObject.AddComponent<WeaponPanelCanvas>();

            // The diagram fades itself out and then sets enabled=false. Rather than fight that from a
            // separate component - whose update order against theirs is not defined - postfix their
            // own Update: it is the one place guaranteed to run after every write they make.
            var upd = AccessTools.Method(typeof(StatusDisplay), "Update");
            if (upd != null)
                harmony.Patch(upd, postfix: Safe(typeof(Hooks), nameof(Hooks.AfterStatusUpdate)));
            else
                Plugin.Log.LogWarning($"[{Name}] StatusDisplay.Update not found - diagram left vanilla");
        }

        internal static class Hooks
        {
            // #diagram-restore
            //   Vanilla's Update writes only color.a, and PartStatusDisplay's damage handler writes
            //   only color.g. Nothing in the game ever rewrites R or B - so the colours we put there
            //   survive after the feature is switched off, and the diagram stays green/red forever.
            //   The fade does not save us either: it drives alpha, not hue.
            //
            //   So the original has to be captured before the first write and put back explicitly.
            //   Keyed on the Image because a StatusDisplay is destroyed and rebuilt per aircraft; the
            //   map is cleared whenever the tracked instance changes, so it holds one airframe's
            //   parts at most.
            private static StatusDisplay _tracked;
            private static readonly Dictionary<Image, Color> _original = new Dictionary<Image, Color>();

            /// <summary>
            /// Put vanilla's colours back. Safe to call repeatedly - it no-ops once the map is empty.
            /// Also called from WeaponPanelCanvas, because if the component has already disabled
            /// itself this postfix will never run again and the restore would never happen.
            /// </summary>
            internal static void RestoreOriginals()
            {
                if (_original.Count == 0) return;
                foreach (var kv in _original)
                    if (kv.Key != null) kv.Key.color = kv.Value;
                _original.Clear();
                _tracked = null;
            }

            /// <summary>
            /// Take over the diagram's colours after vanilla has written them.
            ///
            /// #diagram-ramp
            ///   displayCondition is (hitPoints - redStatusThreshold) / (100 - redStatusThreshold),
            ///   clamped at 0 and forced to 0 on detachment - so 1 = undamaged, 0 = gone. [decompiled]
            ///   Vanilla writes only `color.g = min(condition*2, 1)` and drives ALPHA from
            ///   (1 - condition) * displayTimer * 0.1, which is why an undamaged aircraft shows
            ///   nothing at all and a damaged one fades back to nothing after ten seconds.
            ///
            ///   We keep their condition maths (it already accounts for the per-part red threshold and
            ///   for detachment) and replace the presentation: a full green->yellow->red ramp at
            ///   constant alpha.
            /// </summary>
            internal static void AfterStatusUpdate(StatusDisplay __instance)
            {
                if (Core.Diag.Bypass) return;
                // #diagram-one-switch
                //   This was two options - "recolour" and "always show" - and the combination
                //   always-show WITHOUT recolour has no sensible output. Vanilla's scheme is
                //   color.g = min(condition*2, 1) over a prefab red, so an UNDAMAGED part is
                //   genuinely yellow; you never see it only because vanilla's alpha at full health
                //   is zero. Forcing it visible in "base game colours" therefore shows a solid
                //   yellow aircraft, which is neither what vanilla looks like nor what anyone wants.
                //   One switch, and the two behaviours travel together.
                if (!ShowDiagram.Value) { RestoreOriginals(); return; }

                // A new aircraft means new Images; the old entries can never be restored and would
                // just accumulate.
                if (!ReferenceEquals(__instance, _tracked)) { _original.Clear(); _tracked = __instance; }

                // Their Update disables the component once displayTimer hits zero, which would stop
                // this postfix along with it. Holding the timer above their 10s window keeps both
                // alive without touching any other behaviour.
                __instance.displayTimer = 60f;
                __instance.enabled = true;

                var parts = __instance.statusDisplays;
                if (parts == null) return;

                for (int i = 0; i < parts.Count; i++)
                {
                    var p = parts[i];
                    if (p == null || p.partImage == null) continue;

                    // Capture BEFORE the first write, not after - otherwise we would be remembering
                    // our own output as the thing to restore to.
                    if (!_original.ContainsKey(p.partImage)) _original[p.partImage] = p.partImage.color;

                    float c = Mathf.Clamp01(p.displayCondition);
                    float dmg = 1f - c;

                    // A part that is GONE is not "very damaged" - it is absent, and it should read as
                    // a hole in the silhouette rather than as the same colour a critical part shows.
                    // displayCondition cannot tell us: it is forced to 0 on detachment AND reaches 0
                    // from ordinary damage, so the two are indistinguishable there. [decompiled]
                    // Ask the part itself.
                    bool gone = p.unitPart != null && p.unitPart.IsDetached();

                    // Green -> yellow -> RED, reaching full red at maximum damage. An earlier version
                    // floored the green channel at 0.35 to soften the healthy end, which also stopped
                    // the damaged end ever getting past orange - the floor applied at both ends.
                    // Desaturation now rides on `c`, so it fades out as damage rises and leaves
                    // (1, 0, 0) at the bottom.
                    Color col = gone
                        ? Color.black
                        : new Color(Mathf.Clamp01(2f * dmg), Mathf.Clamp01(2f * c), 0.18f * c);

                    // Vanilla's alpha IS its damage signal, so keeping the diagram up has to supply
                    // its own. Undamaged sits at DiagramOpacity; damage adds up to DiagramDamageBoost
                    // on top, which keeps the useful half of vanilla's behaviour - damage draws the
                    // eye - without an undamaged airframe glowing at you the whole sortie. A missing
                    // part is held at full alpha so the black reads as a hole, not a faint smudge.
                    float baseA = Mathf.Clamp01(DiagramOpacity.Value);
                    col.a = gone ? 1f : Mathf.Clamp01(baseA + DiagramDamageBoost.Value * dmg);

                    p.partImage.color = col;
                }

                if (__instance.aircraftBackground != null)
                {
                    Color bg = __instance.aircraftBackground.color;
                    // Backdrop tracks the parts so the whole diagram dims together rather than the
                    // silhouette floating on a fixed-brightness plate.
                    bg.a = Mathf.Clamp01(DiagramOpacity.Value * 0.6f);
                    __instance.aircraftBackground.color = bg;
                }
            }
        }
    }
}
