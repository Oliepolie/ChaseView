using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Rendering.Universal;

namespace ChaseView.Features
{
    /// <summary>
    /// Re-applies the G-force blackout VISUALS in chase view, because vanilla draws them in the
    /// cockpit only and ChaseView is what makes an external view worth fighting from.
    ///
    /// #gforce-not-a-cheat
    ///
    /// WHAT VANILLA ACTUALLY DOES  [decompiled 2026-07-27: GLOC.cs, PilotPlayerState.cs]
    ///   PilotPlayerState.FixedUpdateState calls gloc.SimulateGLOC(pilot.gForce) every physics step,
    ///   with no reference to the camera. Inside it, the PHYSIOLOGY is camera-independent:
    ///     - stamina and bloodPressure integrate
    ///     - blackoutImage.enabled flips on below bloodPressure 0.6
    ///     - bloodPressure &lt; 0.2 fires LOC(), which pins the screen fully black for 3-6 s
    ///     - the returned pilotStrength &lt; 0.2 zeroes pitch/roll/yaw and blocks weapon input
    ///   All of that already happens identically in every camera. You do NOT dodge G-LOC by leaving
    ///   the cockpit, and this feature does not change a single one of those numbers.
    ///
    ///   The VISUALS are the exception. They sit behind one gate:
    ///       if (CameraStateManager.cameraMode == CameraMode.cockpit) { ...fade, desat, vignette... }
    ///   and GLOC_OnSwitchCamera actively CLEARS them the moment you leave the cockpit. So outside the
    ///   cockpit you fly the entire run-up to unconsciousness at full clarity and full volume, and the
    ///   first thing you ever see is LOC()'s pure black - which writes blackoutImage directly, outside
    ///   the gate. That asymmetry is exactly what a player notices and correctly calls suspicious.
    ///
    /// WHY THAT IS A REAL ADVANTAGE, NOT A COSMETIC ONE
    ///   Between bloodPressure 0.6 and 0.2 the cockpit is progressively desaturated to greyscale,
    ///   vignetted to 1.0, and low-passed down to a 250 Hz cutoff. That is a genuine handicap during a
    ///   sustained turn - and it is also the WARNING that tells you to unload before you black out.
    ///   Flying that same band with a clean picture and clean audio is strictly better information and
    ///   strictly better vision. It is the difference between a fight you can see and one you cannot.
    ///
    ///   Vanilla has this hole in orbit and TV too, so ChaseView did not invent it. But vanilla's
    ///   external views carry no HUD and no turret aim, so nobody fights from them. ChaseView removes
    ///   precisely those obstacles - which would turn a quirk nobody exploits into an everyday
    ///   advantage. Closing it here is the cost of having opened it.
    ///
    /// NO CONFIG TOGGLE, DELIBERATELY.
    ///   Every other knob in this mod picks how something looks. A switch here would pick whether a
    ///   penalty applies, and a checkbox that removes a penalty is the cheat itself - shipping one
    ///   would hand back exactly what this file exists to close. It is intrinsic to the chase camera
    ///   for the same reason the HUD is, and it lives in this folder so it cannot be dropped without
    ///   dropping the camera that needs it.
    ///
    /// PARITY: Local. Reads GLOC's own already-computed bloodPressure and writes post-process values
    /// on this client. Nothing is sent, and no number the game simulates is altered.
    /// </summary>
    internal static class GForceEffects
    {
        /// <summary>
        /// Postfix on GLOC.SimulateGLOC. Runs from FixedUpdate, so it re-asserts within one physics
        /// step of anything that clears the effects - including GLOC_OnSwitchCamera, which wipes them
        /// on the way INTO chase. Worst case that is a single frame of clean picture while already
        /// greyed out, and only if you switch view mid-pull.
        /// </summary>
        internal static void SimulateGLOC_Post(GLOC __instance, float __result)
        {
            // Cockpit: vanilla has already done this, and doing it twice would be identical anyway.
            // Orbit/TV/free: leave vanilla alone. Those views show no HUD and cannot aim a turret, so
            // they are not viable to fight from and are not ours to change.
            if (CameraStateManager.cameraMode != CameraMode.chase) return;

            Image blackout = __instance.blackoutImage;
            ColorAdjustments color = __instance.colorAdjustments;
            Vignette vignette = __instance.vignette;
            if (blackout == null || color == null || vignette == null) return;

            // VANILLA'S OWN CONSTANTS, COPIED EXACTLY. This is a port of the cockpit branch, not an
            // interpretation of it: same thresholds, same endpoints, same curves. If it ever drifts
            // from the original the effect stops being equivalent and starts being a balance change,
            // which is the one thing this file must not become. Both Lerps clamp internally, so the
            // out-of-range ratios below are fine - vanilla relies on that too.
            float fade = (__result - 0.2f) / 0.4f;    // 0.2 fully black -> 0.6 clear
            float desat = (__result - 0.3f) / 0.4f;   // 0.3 greyscale   -> 0.7 full colour

            blackout.color = Color.Lerp(Color.black, Color.clear, fade);
            color.saturation.value = Mathf.Lerp(-100f, 0f, desat);
            vignette.intensity.value = Mathf.Lerp(1f, 0.4f, fade);
            AudioMixerVolume.SetMasterAudioFilterStrength(
                Mathf.Lerp(250f, 11000f, Mathf.Clamp01(fade)) + 11000f * Mathf.Clamp01(desat));

            // blackoutImage.enabled is NOT set here on purpose: SimulateGLOC toggles it outside the
            // camera gate, so it is already correct by the time this postfix runs.
        }
    }
}
