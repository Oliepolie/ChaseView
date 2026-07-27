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
        /// 15G, chosen for pace rather than realism: bloodPressure falls 1.0 -> 0.55 in about 1.4 s,
        /// stamina is gone in about 2.4 s, and the LOC threshold arrives around 3.2 s. Fifteen-plus
        /// seconds at a plausible 9G would make this useless as a test.
        /// </summary>
        private const float FakeG = 15f;

        /// <summary>
        /// Long enough to run the full range - greyout, then LOC - and the recovery afterwards shows
        /// the same band in reverse, which is the half a stuck effect fails at. Drop to about 2.5f for
        /// a deep greyout that stops short of actually passing out.
        /// </summary>
        private const float BurstSeconds = 4f;

        private static bool _prevDown;
        private static float _remaining;

        internal static void Apply(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(GLOC), nameof(GLOC.SimulateGLOC)),
                prefix: new HarmonyMethod(typeof(GForceTestHarness), nameof(SimulateGLOC_Pre))
                { wrapTryCatch = true });

            Plugin.Log.LogWarning("[GForceTest] TEST BUILD - press LeftCtrl+G to inject " + FakeG
                                + "G for " + BurstSeconds + "s. NOT FOR RELEASE.");
        }

        /// <summary>
        /// One press runs a timed burst, rather than requiring the key to be held: you cannot hold a
        /// combo, fly, and watch the screen at once - and past the LOC threshold the controls lock,
        /// so a hold would end the moment the interesting part started.
        ///
        /// Edge-detected by hand instead of with Input.GetKeyDown. This runs from FixedUpdate, which
        /// may fire zero or several times per rendered frame, and GetKeyDown stays true for the whole
        /// frame it fires in - so a fast physics step reads one press several times and a slow one
        /// misses it entirely. Comparing against our own previous sample is exact at any framerate.
        /// </summary>
        internal static void SimulateGLOC_Pre(ref float gForce)
        {
            bool down = Input.GetKey(Modifier) && Input.GetKey(Key);
            bool pressed = down && !_prevDown;
            _prevDown = down;

            if (pressed)
            {
                // Pressing again mid-burst aborts it, which matters if you triggered it low.
                _remaining = _remaining > 0f ? 0f : BurstSeconds;
                Plugin.Log.LogInfo("[GForceTest] "
                    + (_remaining > 0f ? "injecting " + FakeG + "G for " + BurstSeconds + "s" : "aborted")
                    + " - view is " + CameraStateManager.cameraMode);
            }

            if (_remaining <= 0f) return;

            // SimulateGLOC is called once per physics step from PilotPlayerState.FixedUpdateState, so
            // accumulating fixedDeltaTime measures the burst exactly and pauses when the game does.
            _remaining -= Time.fixedDeltaTime;
            gForce = FakeG;

            if (_remaining <= 0f) Plugin.Log.LogInfo("[GForceTest] burst over - recovering");
        }
    }
}
#endif
