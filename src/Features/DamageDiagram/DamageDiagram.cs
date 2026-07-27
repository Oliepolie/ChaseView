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
    /// Keeps the game's own aircraft damage diagram on screen, recoloured green through red.
    /// Vanilla only flashes it for about ten seconds after a hit and then fades it away.
    ///
    /// WHY THIS IS ITS OWN FEATURE
    ///   It used to live inside WeaponPanel, because the two were built in the same sitting and the
    ///   diagram sits directly under the weapon list on screen. That was a filing accident, not a
    ///   relationship: this touches StatusDisplay and has nothing to do with weapons. The practical
    ///   damage was that WeaponPanel's Enabled silently gated the diagram too, so switching off a
    ///   weapon readout you did not want also killed a damage diagram you did - with no way to tell
    ///   from a section named "WeaponPanel" that it would.
    ///
    ///   Note WeaponPanel still READS ac.statusDisplay to average its damage percentage. That is
    ///   reading vanilla state, not depending on this feature, so deleting either folder leaves the
    ///   other working - which is the rule.
    ///
    /// PARITY: Local. Recolours UI images on this client.
    /// </summary>
    internal sealed class DamageDiagram : Feature
    {
        public override string Name => "DamageDiagram";

        public override string Description =>
            "Master switch for the always-on aircraft damage diagram. Off restores vanilla's "
          + "flash-then-fade behaviour entirely. Takes effect at startup, not live - use AlwaysShow "
          + "for that. LOCAL only - display on your own machine.";

        internal static ConfigEntry<bool> AlwaysShow;
        internal static ConfigEntry<float> Opacity;
        internal static ConfigEntry<float> DamageBoost;

        protected override void BindOptions(ConfigFile config)
        {
            // The live toggle. Enabled decides whether the patch exists at all; this decides what it
            // does, and unlike Enabled it can be flipped mid-flight.
            AlwaysShow = config.Bind(Name, "AlwaysShow", false, Cfg.Basic(
                "Keep the damage diagram on screen, coloured green through red. Vanilla only flashes "
              + "it for about 10s after a hit.", 1));

            Opacity = config.Bind(Name, "Opacity", 0.45f, Cfg.Basic(
                "How solid the diagram is when undamaged.",
                new AcceptableValueRange<float>(0.05f, 1f), 2));

            DamageBoost = config.Bind(Name, "DamageBoost", 0.5f, Cfg.Adv(
                "How much more solid a part becomes as it is damaged.",
                new AcceptableValueRange<float>(0f, 1f)));
        }

        public override void DumpResolved(Action<string, object> kv)
        {
            kv("AlwaysShow", AlwaysShow.Value);
            kv("Opacity", Opacity.Value);
            kv("DamageBoost", DamageBoost.Value);
        }

        public override void Apply(Harmony harmony)
        {
            // The diagram fades itself out and then sets enabled=false. Rather than fight that from a
            // separate component - whose update order against theirs is not defined - postfix their
            // own Update: it is the one place guaranteed to run after every write they make.
            var upd = AccessTools.Method(typeof(StatusDisplay), "Update");
            if (upd != null)
                harmony.Patch(upd, postfix: Safe(typeof(Hooks), nameof(Hooks.AfterStatusUpdate)));
            else
                Plugin.Log.LogWarning($"[{Name}] StatusDisplay.Update not found - diagram left vanilla");

            Plugin.HostObject.AddComponent<Driver>();
        }

        /// <summary>
        /// #diagram-resurrect
        ///   The recolour runs as a POSTFIX on StatusDisplay.Update, and vanilla's Update sets
        ///   base.enabled = false once its 10s fade expires. A postfix on a method that no longer runs
        ///   never runs either - so once the diagram had faded, switching AlwaysShow back on did
        ///   nothing at all, permanently. It only ever appeared to work because the option used to
        ///   default ON and caught the component while still alive.
        ///
        ///   Re-enabling therefore has to happen from OUTSIDE that method. Switching the option off is
        ///   self-healing: stop topping the timer up and vanilla's own countdown resumes, fades the
        ///   diagram out and disables it exactly as it would have.
        /// </summary>
        private sealed class Driver : MonoBehaviour
        {
            private void Update()
            {
                if (Diag.Bypass) return;

                var hud = SceneSingleton<CombatHUD>.i;
                Aircraft ac = hud != null ? hud.aircraft : null;

                if (ac != null && AlwaysShow.Value)
                {
                    var sd = ac.statusDisplay;
                    if (sd != null && !sd.enabled) sd.enabled = true;
                }
                else if (!AlwaysShow.Value)
                {
                    // Same reason as the resurrect above: with the option off, StatusDisplay.Update
                    // may already have disabled itself, and a postfix on a dead method cannot undo
                    // anything.
                    Hooks.RestoreOriginals();
                }
            }
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
            /// Also called from Driver, because if the component has already disabled itself this
            /// postfix will never run again and the restore would never happen.
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
                if (Diag.Bypass) return;
                // #diagram-one-switch
                //   This was two options - "recolour" and "always show" - and the combination
                //   always-show WITHOUT recolour has no sensible output. Vanilla's scheme is
                //   color.g = min(condition*2, 1) over a prefab red, so an UNDAMAGED part is
                //   genuinely yellow; you never see it only because vanilla's alpha at full health
                //   is zero. Forcing it visible in "base game colours" therefore shows a solid
                //   yellow aircraft, which is neither what vanilla looks like nor what anyone wants.
                //   One switch, and the two behaviours travel together.
                if (!AlwaysShow.Value) { RestoreOriginals(); return; }

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
                    // its own. Undamaged sits at Opacity; damage adds up to DamageBoost on top, which
                    // keeps the useful half of vanilla's behaviour - damage draws the eye - without an
                    // undamaged airframe glowing at you the whole sortie. A missing part is held at
                    // full alpha so the black reads as a hole, not a faint smudge.
                    float baseA = Mathf.Clamp01(Opacity.Value);
                    col.a = gone ? 1f : Mathf.Clamp01(baseA + DamageBoost.Value * dmg);

                    p.partImage.color = col;
                }

                if (__instance.aircraftBackground != null)
                {
                    Color bg = __instance.aircraftBackground.color;
                    // Backdrop tracks the parts so the whole diagram dims together rather than the
                    // silhouette floating on a fixed-brightness plate.
                    bg.a = Mathf.Clamp01(Opacity.Value * 0.6f);
                    __instance.aircraftBackground.color = bg;
                }
            }
        }
    }
}
