using UnityEngine;

namespace ChaseView.Features
{
    /// <summary>
    /// Splits the flight HUD into a screen-locked half and a world-locked half while in chase view.
    ///
    /// THE PROBLEM  [measured 2026-07-26 via HudProbe, FS-41 Eclipse, 2560x1440]
    ///   FlightHud.Update pins HUDCenter to WorldToScreenPoint(cockpit.position + cockpit.forward *
    ///   4000f) and sets HUDCenter.eulerAngles.z to -(camera.roll - cockpit.roll). Correct on cockpit
    ///   glass. From a chase camera it drags every descendant around after the nose and rolls them
    ///   with the airframe - including readouts that have nothing to do with aiming.
    ///
    /// WHAT THE PROBE FOUND
    ///   HUDCenter has only seven direct children, and the informational readouts are not seven
    ///   scattered objects - they are ALL descendants of ONE container carrying HUDAppManager:
    ///
    ///     HUDCenter/
    ///       compass                       heading tape        -> screen-locked
    ///       <Airframe>_HUDExtras(Clone)   HUDAppManager       -> screen-locked (the whole subtree)
    ///         speedGauge, AoAGauge, AoAIndexer, Altitude, Climbrate, machIndicator,
    ///         Gindicator, GameTimeObject, OtherTimeObject, BearingObject, WeaponIndicator,
    ///         GearIndicator, CatapultHUD, stallWarning
    ///       pitchCompassCenter            pitch ladder        -> stays nose-locked (it IS attitude)
    ///       waterline                     boresight           -> stays nose-locked (it IS aim)
    ///       BoresightState(Clone)         reticle             -> stays nose-locked (it IS aim)
    ///       virtualJoystickPos, HUDMessage                    -> inactive, left alone
    ///
    ///   So the entire split is two reparents, not a rebuild of the HUD.
    ///
    /// WHY HUDAppManager AND NOT THE NAME
    ///   The container is per-airframe: Aircraft.Initialize does
    ///     Instantiate(aircraftParameters.HUDExtras, SceneSingleton&lt;FlightHud&gt;.i.GetHUDCenter())
    ///   so its name is the airframe's ("Aryx_Interceptor1_HUDExtras(Clone)" on the probed aircraft).
    ///   Matching on that name would work on exactly one aircraft. HUDAppManager is a SceneSingleton
    ///   [decompiled], which is a stable handle on every airframe including modded ones.
    ///
    ///   Two lifetime facts that follow from the same code: aircraftParameters.HUDExtras is
    ///   null-checked before instantiation, so some airframes have NO readout panel at all; and
    ///   HUDAppManager destroys its own GameObject on aircraft disable, so a fresh clone appears
    ///   under HUDCenter for every new aircraft. Both mean this cannot be done once at startup -
    ///   it is re-checked, cheaply, every frame.
    ///
    /// PARITY: Local. Reparenting UI nodes on this client. Nothing sent, nothing replicated.
    /// </summary>
    internal sealed class ScreenLockedReadouts : MonoBehaviour
    {
        private RectTransform _anchor;      // our screen-fixed stand-in for HUDCenter
        private Transform _appsOriginal;    // HUDCenter, remembered so we can put things back
        private Transform _apps;            // the HUDAppManager container we moved
        private Transform _compass;
        private Transform _compassOriginal;
        private bool _moved;

        internal static bool WantScreenLock;
        internal static bool WantCompassLock;

        /// <summary>
        /// #hud-scale
        ///   Scales the screen-locked half of the HUD about screen centre. Because the children keep
        ///   the anchoredPositions they were authored with, one localScale on the anchor moves them
        ///   AND resizes them together: below 1 draws the readouts in toward the middle and shrinks
        ///   them, above 1 pushes them out and enlarges them. Two useful behaviours, one number.
        ///
        ///   This can only ever touch what we already moved. The aiming furniture - pitch ladder,
        ///   waterline, boresight reticle - stays under vanilla's HUDCenter and MUST stay unscaled:
        ///   the reticle's screen position IS the aim point, computed from a world anchor 4 km down
        ///   the nose, so scaling it about screen centre would move the crosshair off where the guns
        ///   actually point. That is a silent accuracy bug, and it is why this is not simply applied
        ///   to HUDCenter itself.
        /// </summary>
        internal static float WantScale = 1f;

        /// <summary>HUDCenter's own localScale, so our factor multiplies the canvas rather than replacing it.</summary>
        private Vector3 _baseScale = Vector3.one;

        private void Update()
        {
            // Bypass must not strand a half-reparented HUD, so it takes the Restore path rather than
            // returning outright.
            bool want = !Core.Diag.Bypass
                     && WantScreenLock
                     && CameraStateManager.cameraMode == CameraMode.chase;

            if (want) Engage(); else Restore();
        }

        private void Engage()
        {
            FlightHud hud = SceneSingleton<FlightHud>.i;
            if (hud == null) return;

            Transform hudCenter = hud.GetHUDCenter();
            if (hudCenter == null) return;

            EnsureAnchor(hudCenter);
            if (_anchor == null) return;

            // Track the screen centre every frame rather than caching it once. Cheap, and it survives
            // a resolution change or an alt-tab without needing an event we would have to find first.
            _anchor.position = new Vector3(Screen.width * 0.5f, Screen.height * 0.5f, 0f);
            _anchor.rotation = Quaternion.identity;

            // Re-applied every frame for the same reason the position is: it costs nothing, and it
            // makes the ConfigurationManager slider live so the scale can be dialled in while flying,
            // which is the only way anyone arrives at a value they actually like. Clamped wider than
            // the config's own range so a hand-edited .cfg cannot produce a zero-scale invisible HUD.
            _anchor.localScale = _baseScale * Mathf.Clamp(WantScale, 0.1f, 4f);

            // The readout panel. Re-resolved every frame because the aircraft's panel is destroyed and
            // re-instantiated under HUDCenter on every aircraft change - a cached reference would be a
            // dead object after the first re-spawn, and Unity's fake null makes that fail quietly.
            HUDAppManager mgr = SceneSingleton<HUDAppManager>.i;
            Transform apps = mgr != null ? mgr.transform : null;

            if (apps != null && apps.parent != _anchor)
            {
                // Only remember the original parent when it is genuinely vanilla's HUDCenter,
                // otherwise a mid-flight re-entry would record OUR anchor as the restore target and
                // the panel would be stranded in the wrong place forever.
                if (apps.parent == hudCenter) _appsOriginal = hudCenter;
                _apps = apps;
                // worldPositionStays:false keeps localPosition/anchoredPosition intact, so every
                // child keeps the offset it was authored with and the panel lands exactly where it
                // sits in the cockpit - just no longer chasing the nose or rolling with the airframe.
                apps.SetParent(_anchor, worldPositionStays: false);
                _moved = true;
                Plugin.Log.LogInfo($"[ChaseCamera] screen-locked readout panel '{apps.name}'");
            }

            if (WantCompassLock)
            {
                if (_compass == null || _compass.parent == null) _compass = hudCenter.Find("compass");
                if (_compass != null && _compass.parent != _anchor)
                {
                    if (_compass.parent == hudCenter) _compassOriginal = hudCenter;
                    _compass.SetParent(_anchor, worldPositionStays: false);
                    _moved = true;
                    Plugin.Log.LogInfo("[ChaseCamera] screen-locked heading tape");
                }
            }
        }

        private void EnsureAnchor(Transform hudCenter)
        {
            if (_anchor != null) return;

            var hudCenterRect = hudCenter as RectTransform;
            if (hudCenterRect == null) return;

            // Sits alongside HUDCenter under the same canvas, copying its anchoring so the children we
            // move keep the coordinate frame they were authored in. Built at runtime; nothing shipped.
            var go = new GameObject("ChaseView_ScreenLockedHUD", typeof(RectTransform));
            _anchor = (RectTransform)go.transform;
            _anchor.SetParent(hudCenter.parent, worldPositionStays: false);
            _anchor.anchorMin = hudCenterRect.anchorMin;
            _anchor.anchorMax = hudCenterRect.anchorMax;
            _anchor.pivot = hudCenterRect.pivot;
            _anchor.sizeDelta = hudCenterRect.sizeDelta;
            _baseScale = hudCenterRect.localScale;
            _anchor.localScale = _baseScale;

            // Directly after HUDCenter in the hierarchy, so draw order relative to the rest of the HUD
            // is what it was. Canvas children render in sibling order.
            _anchor.SetSiblingIndex(hudCenter.GetSiblingIndex() + 1);
        }

        /// <summary>
        /// Put everything back. Runs on leaving chase view, on the feature being switched off live,
        /// and on destroy - the HUD must never be left half-reparented, because the state that
        /// produces is invisible until the player switches to the cockpit and finds it broken.
        /// </summary>
        private void Restore()
        {
            if (!_moved) return;

            if (_apps != null && _appsOriginal != null)
                _apps.SetParent(_appsOriginal, worldPositionStays: false);

            if (_compass != null && _compassOriginal != null)
                _compass.SetParent(_compassOriginal, worldPositionStays: false);

            _apps = null;
            _compass = null;
            _moved = false;
        }

        private void OnDisable() => Restore();
        private void OnDestroy() => Restore();
    }
}
