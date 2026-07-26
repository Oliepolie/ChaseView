using System;
using BepInEx.Configuration;

namespace ChaseView.Core
{
    /// <summary>
    /// ConfigurationManager reads this by REFLECTION on field names — it never references our type,
    /// and we never reference its assembly. So the class must be declared locally, the field names
    /// must match exactly, and every field must be nullable so "not specified" is distinguishable
    /// from "explicitly false". Getting a name wrong fails silently: the setting just renders plainly.
    ///
    /// Only the fields actually used are declared; the real attribute class has more.
    /// </summary>
    internal sealed class ConfigurationManagerAttributes
    {
        public bool? IsAdvanced;
        public int? Order;
    }

    /// <summary>
    /// Builders for config descriptions, so the shipped UI stays short and the reasoning stays in the
    /// source where it costs nothing.
    ///
    /// House style: a config description is one line telling the player what the setting DOES. Why it
    /// exists, what it was measured against and what breaks without it belong in comments — a user
    /// opening the settings menu wants a label, not an essay.
    /// </summary>
    internal static class Cfg
    {
        /// <summary>Ordinary setting. Shown in the normal list.</summary>
        internal static ConfigDescription Basic(string text, int order = 0) =>
            new ConfigDescription(text, null, Attr(false, order));

        internal static ConfigDescription Basic(string text, AcceptableValueBase range, int order = 0) =>
            new ConfigDescription(text, range, Attr(false, order));

        /// <summary>
        /// Tuning knob, hidden behind ConfigurationManager's "Advanced settings" toggle. Use for
        /// anything a player does not need to touch to enjoy the mod, and for anything whose wrong
        /// value produces a confusing result rather than an obviously-off one.
        /// </summary>
        internal static ConfigDescription Adv(string text, int order = 0) =>
            new ConfigDescription(text, null, Attr(true, order));

        internal static ConfigDescription Adv(string text, AcceptableValueBase range, int order = 0) =>
            new ConfigDescription(text, range, Attr(true, order));

        // Order counts DOWN the list in ConfigurationManager, so negate to get the intuitive
        // "lower number appears first" that every call site assumes.
        private static ConfigurationManagerAttributes Attr(bool advanced, int order) =>
            new ConfigurationManagerAttributes { IsAdvanced = advanced, Order = -order };
    }
}
