#if GFORCE_TEST
using HarmonyLib;
using UnityEngine;

namespace ChaseView.Features
{
    /// <summary>
    /// TEMPORARY. Delete this file, its GFORCE_TEST define in the csproj, and the guarded call in
    /// ChaseCamera.Apply once #gforce-not-a-cheat has been confirmed in flight.
    ///
    /// WHY IT FAKES THE INPUT RATHER THAN THE STATE
    ///   The obvious harness writes GLOC.bloodPressure directly. That would test a code path nobody
    ///   ever flies: bloodPressure would jump rather than integrate, stamina would stay untouched, and
    ///   the recovery curve on release would be wrong. Overriding the gForce ARGUMENT instead means
    ///   vanilla's own maths runs start to finish exactly as it does at a real 15G - the stamina burn,
    ///   the brief stall around 0.55 where stamina props blood pressure up, the LOC trigger at 0.2 and
    ///   the recovery afterwards are all vanilla's, unmodified. If the effect looks right under this
    ///   harness it is right, because the harness is not in the maths.
    ///
    /// NOT gated to chase view, deliberately: holding the key in the COCKPIT shows vanilla's own
    /// effect, which is the reference the chase version has to match. Comparing the two is the test.
    /// </summary>
    internal static class GForceTestHarness
    {
        private const KeyCode Modifier = KeyCode.LeftControl;
        private const KeyCode Key = KeyCode.G;

        /// <summary>
        /// 15G, chosen for pace rather than realism: bloodPressure falls 1.0 -> 0.6 in about 1.3 s and
        /// stamina is gone in about 2.4 s, so a full blackout lands in roughly four seconds of holding
        /// instead of the fifteen-plus a plausible 9G would take. Release and it recovers in about 3 s,
        /// which is the fade back IN - worth watching too, since that is the half a stuck effect fails.
        /// </summary>
        private const float FakeG = 15f;

        private static bool _wasHeld;

        internal static void Apply(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(GLOC), nameof(GLOC.SimulateGLOC)),
                prefix: new HarmonyMethod(typeof(GForceTestHarness), nameof(SimulateGLOC_Pre))
                { wrapTryCatch = true });

            Plugin.Log.LogWarning(
                "[GForceTest] TEST BUILD - hold LeftCtrl+G to inject " + FakeG + "G. NOT FOR RELEASE.");
        }

        /// <summary>
        /// Level-triggered Input.GetKey, not GetKeyDown: this runs from FixedUpdate, which may fire
        /// zero or several times per rendered frame, and an edge-triggered poll would be missed or
        /// double-counted depending on framerate.
        /// </summary>
        internal static void SimulateGLOC_Pre(ref float gForce)
        {
            bool held = Input.GetKey(Modifier) && Input.GetKey(Key);

            if (held != _wasHeld)
            {
                _wasHeld = held;
                Plugin.Log.LogInfo("[GForceTest] injection " + (held ? "ON" : "OFF")
                                 + " - view is " + CameraStateManager.cameraMode);
            }

            if (held) gForce = FakeG;
        }
    }
}
#endif
