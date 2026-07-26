namespace ChaseView.Core
{
    /// <summary>
    /// Shared diagnostic state. Lives in Core, not in a feature, because features may depend on Core
    /// but never on each other — a hot path reading a flag owned by PerfProbe would couple every
    /// feature to that folder and break the "delete it and everything still builds" rule.
    ///
    /// Deleting PerfProbe therefore leaves <see cref="Bypass"/> permanently false, which is exactly
    /// the shipped behaviour.
    /// </summary>
    internal static class Diag
    {
        /// <summary>
        /// When true, every per-frame thing ChaseView does idles — without disabling any feature or
        /// changing camera state. This is the A/B baseline for "is the framerate drop us or the view".
        ///
        /// A plain static bool on purpose: a ConfigEntry.Value property read inside a per-frame path
        /// is itself a cost, and paying it to measure cost would be self-defeating.
        /// </summary>
        /// <remarks>
        /// Explicitly initialised, not just declared: PerfProbe is the only thing that ever writes it
        /// and PerfProbe is excluded from release builds, so without the initialiser the compiler
        /// warns that nothing assigns it. In a release this is permanently false and every gate that
        /// reads it costs one static bool test.
        /// </remarks>
        internal static bool Bypass = false;
    }
}
