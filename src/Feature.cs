using System;
using BepInEx.Configuration;
using HarmonyLib;

namespace ChaseView
{
    /// <summary>
    /// How a feature relates to other peers. Every feature must declare this, and the value is
    /// printed in the startup dump so the README's parity table is derivable from any user's log
    /// rather than from someone's memory.
    ///
    /// The test is mechanical, and it is NOT "does this setting feel important":
    ///   Does this value feed a computation whose OUTPUT is shared, compared, or independently
    ///   derived by each peer? If yes it must be replicated or matched. If it only changes what
    ///   this machine draws, caches or logs, it is Local and peers may differ freely.
    /// </summary>
    internal enum Parity
    {
        /// <summary>
        /// Changes only what THIS machine draws, stores or logs. Peers may differ freely; a vanilla
        /// server cannot tell the difference. This is ChaseView's default and, ideally, its only value.
        /// </summary>
        Local,

        /// <summary>
        /// Reads replicated state (SyncVars, RPCs) but writes nothing back. Still safe against a
        /// vanilla server, but the readout can be WRONG rather than merely absent when the state it
        /// reads is server-only - so it must say so on screen instead of showing a confident number.
        /// </summary>
        ReadsReplicated,

        /// <summary>
        /// Sends something, or mutates state another peer will observe. Requires an explicit written
        /// justification in docs/FEATURES.md and a real two-machine socket test before shipping.
        /// Only TurretAimInChase needs this; treat a new one as a design smell.
        /// </summary>
        TouchesServer
    }

    /// <summary>
    /// One quality-of-life feature. Deliberately small: this is a contract, not a framework.
    ///
    /// THE INDEPENDENCE RULE - the whole point of the project:
    ///   A feature may reference <see cref="Feature"/> and ChaseView.Core. It may NOT reference another
    ///   feature. Deleting src/Features/&lt;Whatever&gt;/ must leave a mod that still compiles and still
    ///   runs every other feature.
    ///
    /// That rule is what makes the reflection-based discovery in Plugin.cs worth its cost: there is no
    /// central list of features to edit, so removing a folder cannot leave a dangling reference behind.
    /// If two features start needing each other, that is the signal to stop and rethink, not to add
    /// plumbing between them.
    /// </summary>
    internal abstract class Feature
    {
        /// <summary>Config section name, and the tag this feature logs under.</summary>
        public abstract string Name { get; }

        /// <summary>One line for the config file header describing what it changes.</summary>
        public abstract string Description { get; }

        /// <summary>See <see cref="Parity"/>. Default Local, because anything else needs an argument.</summary>
        public virtual Parity Parity => Parity.Local;

        /// <summary>
        /// A BepInEx plugin GUID this feature cannot work without (e.g. another mod we integrate with).
        /// Checked immediately at Awake: every plugin's info is in the Chainloader before any plugin's
        /// Awake runs, so an absent plugin is knowable up front and need not be waited on.
        /// Null (the default) means vanilla-only.
        /// </summary>
        public virtual string RequiredPlugin => null;

        /// <summary>
        /// A fully-qualified type this feature cannot work without, when its dependency is NOT a
        /// BepInEx plugin. Blueprinter addons (BOTE and the Aryx airframes) load AFTER the BepInEx
        /// plugin pass, so [BepInDependency] cannot order us against them and cannot even see them -
        /// polling for the type to appear is the only mechanism that actually works.
        /// </summary>
        public virtual string RequiredType => null;

        /// <summary>
        /// Whether this feature is on for someone who has never edited the config.
        ///
        /// Wanted features default ON - a mod that ships its features off is a mod nobody experiences.
        /// Default OFF only for something that costs framerate, changes an aim aid, or is not yet
        /// verified in flight.
        ///
        /// NOTE the config-freeze trap: config.Bind NEVER overwrites a value already on disk, so
        /// flipping this later reaches nobody who has run the mod before - including you. That is what
        /// ConfigVersion and a migration step are for, and they get added the first time we change a
        /// SHIPPED default, not before.
        /// </summary>
        protected virtual bool DefaultEnabled => true;

        public bool Enabled { get; private set; }
        protected ConfigFile Config { get; private set; }

        private ConfigEntry<bool> _enabled;

        public void Bind(ConfigFile config)
        {
            Config = config;
            _enabled = config.Bind(Name, "Enabled", DefaultEnabled, Description);
            Enabled = _enabled.Value;

            // ALWAYS bind the options, even when the feature is off.
            //
            // Gating this on Enabled means a disabled feature's settings are never written to the
            // .cfg, so they can be neither seen nor pre-set, and turning a feature on takes two
            // launches: flip Enabled, quit, edit the keys that only then exist, launch again. A
            // disabled feature listing inert settings is normal and harmless. An enabled feature
            // whose settings are undiscoverable is not.
            //
            // It also means ConfigurationManager (already installed here) shows every knob in the
            // in-game settings UI whether or not the feature is currently on.
            BindOptions(config);
        }

        /// <summary>Bind this feature's own config entries. Called even when disabled - see Bind.</summary>
        protected virtual void BindOptions(ConfigFile config) { }

        /// <summary>Apply patches / start behaviours. Called once, after any RequiredType resolved.</summary>
        public abstract void Apply(Harmony harmony);

        /// <summary>Every value this feature resolved, for the unconditional startup dump.</summary>
        public virtual void DumpResolved(Action<string, object> kv) { }

        /// <summary>
        /// Build a patch reference whose body is wrapped in try/catch.
        ///
        /// Use this for EVERY manual harmony.Patch call. [HarmonyWrapSafe] as a class attribute is
        /// only read during attribute-driven patching (PatchAll) and is completely inert when patches
        /// are applied by hand - so the guard you think you have is decorative, and an exception in a
        /// hook propagates straight into the game's own call stack. Setting wrapTryCatch explicitly
        /// does not depend on attribute discovery behaving the way we assumed.
        ///
        /// Note this does not make a throwing patch visible: Harmony logs the exception once and the
        /// patch then silently no-ops forever. Check the fresh log after every deploy.
        /// </summary>
        protected static HarmonyMethod Safe(Type hooks, string method) =>
            new HarmonyMethod(hooks, method) { wrapTryCatch = true };
    }
}
