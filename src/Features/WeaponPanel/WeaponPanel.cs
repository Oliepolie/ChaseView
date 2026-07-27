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
            "Master switch for the Ace Combat style weapon readout: every station with its short code "
          + "and ammo, a marker on the selected one, countermeasures and a damage percentage. Off "
          + "restores the stock HUD regardless of the settings below, and takes effect at startup "
          + "rather than live. LOCAL only - display on your own machine.";

        internal static ConfigEntry<bool> ShowPanel;
        private ConfigEntry<bool> _showPanel;
        internal static ConfigEntry<bool> HideVanillaPanel;
        internal static ConfigEntry<bool> ShowDamagePercent;
        internal static ConfigEntry<bool> ShowCargo;
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

            ShowDamagePercent = config.Bind(Name, "ShowDamagePercent", true, Cfg.Adv("Show a damage percentage under the weapon list."));

            ShowCargo = config.Bind(Name, "ShowCargo", true, Cfg.Adv("Include the cargo station in the list."));

            PanelScale = config.Bind(Name, "PanelScale", 1f, Cfg.Adv("Size of the weapon list.", new AcceptableValueRange<float>(0.5f, 2f)));
        }

        public override void DumpResolved(Action<string, object> kv)
        {
            kv("ShowWeaponList", _showPanel.Value);
            kv("HideVanillaWeaponPanel", HideVanillaPanel.Value);
            kv("ShowDamagePercent", ShowDamagePercent.Value);
            kv("ShowCargo", ShowCargo.Value);
            kv("PanelScale", PanelScale.Value);
        }

        public override void Apply(Harmony harmony)
        {
            Plugin.HostObject.AddComponent<WeaponPanelCanvas>();

        }

    }
}
