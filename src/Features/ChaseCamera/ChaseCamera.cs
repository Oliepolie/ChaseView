using System;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using ChaseView.Core;

namespace ChaseView.Features
{
    /// <summary>
    /// Makes the vanilla chase camera usable as a real third-person view: HUD visible, and TrackIR
    /// free-look around the aircraft. The Ace Combat / War Thunder external view the game almost has.
    ///
    /// WHAT VANILLA ALREADY DOES  [decompiled 2026-07-26, Assembly-CSharp, build f2f40b756d]
    ///   CameraChaseState carries `private bool showHUD`, a public ToggleHUD() and a public CheckHUD()
    ///   that enables FlightHud and DynamicMap for the sensible camera positions. It is complete and
    ///   it works. It is also unreachable in normal play: EnterState hardcodes `showHUD = false`, and
    ///   the only in-game caller of ToggleHUD is
    ///       if (GameManager.gameState == GameState.Editor && Input.GetKeyDown(KeyCode.H))
    ///   so the feature exists and is locked to the mission editor. This feature unlocks it rather
    ///   than reimplementing it - vanilla's own position filter is good behaviour worth keeping.
    ///
    /// WHAT IS GENUINELY NEW: TrackIR. PlayerSettings.useTrackIR is consulted in exactly one place in
    ///   the whole assembly - CameraCockpitState. Chase and orbit never look at it. [decompiled]
    ///
    /// PARITY: Local. Camera transforms and canvas visibility are client-side rendering. Nothing here
    /// is sent, and nothing reads replicated state. A vanilla server cannot tell.
    ///
    /// COMPATIBILITY with WT Mouse Aim (NuclearOption-MouseAim), which is installed here:
    ///   That mod patches CameraOrbitState.UpdateState and CameraCockpitState.UpdateState, and it
    ///   hands the camera straight back when PlayerSettings.useTrackIR is on. It never touches
    ///   CameraChaseState. [decompiled from the shipped mod] Targeting chase state specifically is
    ///   what keeps the two from fighting over the same transform - do not "simplify" this onto the
    ///   orbit camera later.
    /// </summary>
    internal sealed class ChaseCamera : Feature
    {
        public override string Name => "ChaseCamera";

        public override string Description =>
            "Third-person chase view improvements: keeps the HUD and minimap visible in chase camera "
          + "(vanilla allows this only in the mission editor) and lets TrackIR look around the "
          + "aircraft. LOCAL only - does not affect other players.";

        private ConfigEntry<bool> _showHud;
        private ConfigEntry<bool> _hudInAllPositions;
        private ConfigEntry<bool> _screenLockReadouts;
        private ConfigEntry<bool> _screenLockCompass;
        private ConfigEntry<KeyboardShortcut> _toggleKey;
        private ConfigEntry<bool> _trackIr;
        private ConfigEntry<float> _trackIrAmount;
        private ConfigEntry<float> _distance;
        private ConfigEntry<float> _height;
        private ConfigEntry<float> _rollFollow;
        private ConfigEntry<float> _velocityAlign;
        private ConfigEntry<bool> _autoFraming;
        private ConfigEntry<float> _screenFill;
        private ConfigEntry<float> _elevation;
        private ConfigEntry<float> _reticleClearance;
        private ConfigEntry<float> _momentum;
        private static ConfigEntry<float> Momentum;
        private ConfigEntry<bool> _mouseLook;
        private static ConfigEntry<bool> MouseLook;
        private ConfigEntry<bool> _inViewCycle;
        private static ConfigEntry<bool> InViewCycle;

        // Static so the patch bodies can reach them. There is exactly one CameraStateManager and one
        // chaseState, so a single instance's worth of state is the correct shape here.
        private static ConfigEntry<bool> ShowHud;
        private static ConfigEntry<bool> HudInAllPositions;
        private static ConfigEntry<bool> TrackIr;
        private static ConfigEntry<float> TrackIrAmount;
        private static ConfigEntry<float> Distance;
        private static ConfigEntry<float> Height;
        private static ConfigEntry<float> RollFollow;
        private static ConfigEntry<float> VelocityAlign;
        private static ConfigEntry<bool> AutoFraming;
        private static ConfigEntry<float> ScreenFill;
        private static ConfigEntry<float> Elevation;
        private static ConfigEntry<float> ReticleClearance;

        protected override void BindOptions(ConfigFile config)
        {
            _showHud = config.Bind(Name, "ShowHudInChase", true, Cfg.Basic("Show the HUD and minimap in chase view.", 1));

            _screenLockReadouts = config.Bind(Name, "ScreenLockReadouts", true, Cfg.Basic("Keep speed, altitude and the other readouts still instead of following the nose.", 2));

            _screenLockCompass = config.Bind(Name, "ScreenLockCompass", true, Cfg.Basic("Pin the heading tape to the top of the screen.", 3));

            _inViewCycle = config.Bind(Name, "InViewCycle", true, Cfg.Basic(
                "Put chase view in the Switch View cycle, right after the cockpit.", 0));
            _mouseLook = config.Bind(Name, "MouseLook", true, Cfg.Basic(
                "Look around in chase view with your Pan/Tilt View bindings, exactly as the "
              + "cockpit does. Uses the game's own sensitivity, inversion and smoothing.", 11));
            MouseLook = _mouseLook;

            InViewCycle = _inViewCycle;

            _momentum = config.Bind(Name, "Momentum", 0f, Cfg.Basic(
                "Sit behind where the aircraft is TRAVELLING rather than where its nose points. "
              + "0 = behind the nose, 1 = fully behind the flight path.",
                new AcceptableValueRange<float>(0f, 1f), 10));
            Momentum = _momentum;

            _hudInAllPositions = config.Bind(Name, "HudInAllPositions", false, Cfg.Adv("Show the HUD from the wingtip, top and front camera positions too."));

            _toggleKey = config.Bind(Name, "ToggleHudKey", new KeyboardShortcut(KeyCode.None), Cfg.Adv("Optional key to toggle the chase HUD. Pick an UNBOUND key - it also fires whatever it is bound to."));

            _trackIr = config.Bind(Name, "TrackIrInChase", true, Cfg.Basic("Let TrackIR look around from the chase camera. Needs TrackIR on in the game settings.", 4));

            _trackIrAmount = config.Bind(Name, "TrackIrAmount", 1f, Cfg.Adv("How far head movement swings the camera.", new AcceptableValueRange<float>(0f, 3f)));

            // ---- Camera feel. EVERY DEFAULT IS AN EXACT VANILLA NO-OP. ----
            //
            // These exist to be tuned in flight through ConfigurationManager, because camera feel is
            // not something anyone gets right by reasoning about it. They are deliberately shipped
            // inert so the default build is "vanilla chase + HUD" and any change you see is a change
            // YOU made - which is the only way the tuning session produces usable information.
            //
            // #feel-untested: the maths below is derived from the decompiled UpdateState, not from a
            // test flight. Treat non-default values as experimental until they have been flown.

            // #auto-framing
            //   Vanilla derives the chase offset from ONE size number - max(L, W*0.7) + max(W, L*0.7),
            //   roughly length+width - and then makes height a flat 0.1x of it. So elevation scales
            //   with an aircraft's LENGTH, and a multiplier tuned on a compact fighter puts the camera
            //   far too high on a long one. That is why a single Distance/Height pair cannot fit the
            //   whole roster. [decompiled]
            //
            //   Auto framing replaces both with quantities that are aircraft-independent by
            //   construction: how much of the screen the aircraft should fill, and how many degrees
            //   above its centreline to sit. Distance falls out of the actual dimensions and the FOV,
            //   so one setting frames every airframe the same way.
            _autoFraming = config.Bind(Name, "AutoFraming", true, Cfg.Basic("Frame every aircraft alike using its real size. Off = use the Distance/Height trims.", 5));

            _screenFill = config.Bind(Name, "ScreenFill", 0.8f, Cfg.Basic(
                "How much of the screen the aircraft fills.",
                new AcceptableValueRange<float>(0.1f, 1f), 6));

            _elevation = config.Bind(Name, "Elevation", 8f, Cfg.Basic(
                "Degrees the camera sits above the aircraft.",
                new AcceptableValueRange<float>(0f, 45f), 7));

            _reticleClearance = config.Bind(Name, "ReticleClearance", 1f, Cfg.Adv("Minimum camera height above the aircraft, so the tail cannot block the aiming reticle.", new AcceptableValueRange<float>(0f, 3f)));

            _distance = config.Bind(Name, "Distance", 1f, Cfg.Adv("Fine trim on camera distance. 1 = leave auto framing alone.", new AcceptableValueRange<float>(0.3f, 4f)));

            _height = config.Bind(Name, "Height", 1f, Cfg.Adv("Fine trim on camera height. 1 = leave auto framing alone.", new AcceptableValueRange<float>(0f, 6f)));

            _rollFollow = config.Bind(Name, "RollFollow", 0.9f, Cfg.Basic(
                "How much the camera rolls with the aircraft. 1 = locked to it, 0 = horizon stays level.",
                new AcceptableValueRange<float>(0f, 1f), 9));

            _velocityAlign = config.Bind(Name, "VelocityAlign", 0f, Cfg.Adv("Aim toward where the aircraft is travelling rather than where it points. Helps at high AoA.", new AcceptableValueRange<float>(0f, 1f)));

            ScreenLockedReadouts.WantScreenLock = _screenLockReadouts.Value;
            ScreenLockedReadouts.WantCompassLock = _screenLockCompass.Value;
            // Live-editable through ConfigurationManager: mirror later changes too, so toggling the
            // split mid-flight takes effect instead of needing a restart.
            _screenLockReadouts.SettingChanged += (s2, e) => ScreenLockedReadouts.WantScreenLock = _screenLockReadouts.Value;
            _screenLockCompass.SettingChanged += (s2, e) => ScreenLockedReadouts.WantCompassLock = _screenLockCompass.Value;

            ShowHud = _showHud;
            HudInAllPositions = _hudInAllPositions;
            TrackIr = _trackIr;
            TrackIrAmount = _trackIrAmount;
            Distance = _distance;
            Height = _height;
            RollFollow = _rollFollow;
            VelocityAlign = _velocityAlign;
            AutoFraming = _autoFraming;
            ScreenFill = _screenFill;
            Elevation = _elevation;
            ReticleClearance = _reticleClearance;
        }

        public override void DumpResolved(Action<string, object> kv)
        {
            kv("InViewCycle", _inViewCycle.Value);
            kv("MouseLook", _mouseLook.Value);
            kv("ShowHudInChase", _showHud.Value);
            kv("HudInAllPositions", _hudInAllPositions.Value);
            kv("ScreenLockReadouts", _screenLockReadouts.Value);
            kv("ScreenLockCompass", _screenLockCompass.Value);
            kv("ToggleHudKey", _toggleKey.Value);
            kv("TrackIrInChase", _trackIr.Value);
            kv("TrackIrAmount", _trackIrAmount.Value);
            kv("Distance", _distance.Value);
            kv("Height", _height.Value);
            kv("RollFollow", _rollFollow.Value);
            kv("Momentum", _momentum.Value);
            kv("VelocityAlign", _velocityAlign.Value);
            kv("AutoFraming", _autoFraming.Value);
            kv("ScreenFill", _screenFill.Value);
            kv("Elevation", _elevation.Value);
            kv("ReticleClearance", _reticleClearance.Value);
            // Vanilla's own switch. Worth in the dump because "TrackIR does nothing" is almost always
            // this being off, and without the line the report is a guessing game.
            kv("(game) PlayerSettings.useTrackIR", PlayerSettings.useTrackIR);
        }

        public override void Apply(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(CameraChaseState), nameof(CameraChaseState.EnterState)),
                postfix: Safe(typeof(Hooks), nameof(Hooks.EnterState_Post)));

            // Prefix AND postfix - see Hooks.UpdateState_Pre for why the prefix is load-bearing.
            harmony.Patch(AccessTools.Method(typeof(CameraChaseState), nameof(CameraChaseState.UpdateState)),
                prefix: Safe(typeof(Hooks), nameof(Hooks.UpdateState_Pre)),
                postfix: Safe(typeof(Hooks), nameof(Hooks.UpdateState_Post)));

            harmony.Patch(AccessTools.Method(typeof(CameraChaseState), nameof(CameraChaseState.CheckHUD)),
                postfix: Safe(typeof(Hooks), nameof(Hooks.CheckHUD_Post)));

            // Closing the map turns the HUD back on for the COCKPIT only - twice, once for the flight
            // HUD and once for the minimap. Without this the HUD vanishes the first time you open and
            // close the map in chase view, which reads as "the mod stopped working".
            // #cockpit-only-restore
            //   THREE places re-enable the flight HUD and every one of them is gated on being in the
            //   cockpit:
            //     DynamicMap.Minimize   - closing the map
            //     GameplayUI.ResumeGame - closing the pause menu
            //     (and CameraCockpitState.EnterState, which is correct)
            //   Vanilla never needed more, because vanilla never shows this HUD anywhere else. Each
            //   one turns the HUD off for a chase-view player and leaves it off until they happen to
            //   switch views, which re-runs CheckHUD by accident. Both are routed through the same
            //   restore below rather than fixed separately - if a fourth turns up, it patches here.
            harmony.Patch(AccessTools.Method(typeof(DynamicMap), nameof(DynamicMap.Minimize)),
                postfix: Safe(typeof(Hooks), nameof(Hooks.RestoreChaseHud_Post)));

            harmony.Patch(AccessTools.Method(typeof(GameplayUI), nameof(GameplayUI.ResumeGame)),
                postfix: Safe(typeof(Hooks), nameof(Hooks.RestoreChaseHud_Post)));

            // #view-cycle
            //   Vanilla's Switch View runs cockpit -> orbit -> TV -> cockpit and never passes through
            //   chase at all; chase has exactly one entry point, the Center button from orbit. That
            //   makes the whole mod hard to find. Rewriting the ARGUMENT to SwitchState is the least
            //   invasive way in: no extra state transition, no EnterState running twice, no
            //   re-entrancy to guard - the state machine simply receives a different destination.
            harmony.Patch(AccessTools.Method(typeof(CameraStateManager), nameof(CameraStateManager.SwitchState)),
                prefix: Safe(typeof(Hooks), nameof(Hooks.SwitchState_Pre)));

            Plugin.HostObject.AddComponent<HotkeyPump>().Init(_toggleKey);
            Plugin.HostObject.AddComponent<ScreenLockedReadouts>();
        }

        /// <summary>
        /// Patch bodies. Static, and reached through the static config fields above - Harmony patch
        /// methods cannot close over an instance.
        /// </summary>
        private static class Hooks
        {
            /// <summary>
            /// The clean chase rotation, i.e. what the state machine computed with no head tracking
            /// mixed in. See UpdateState_Pre.
            /// </summary>
            /// <summary>
            /// #orbit-reverted (2026-07-26): free-look orbiting the camera around the aircraft was
            /// built, flown and REJECTED. Panning in place is steadier and always keeps a clear line
            /// of sight forward; orbiting swung the forward view away exactly when you needed it and
            /// fought vanilla's position lerp for control of the transform. Do not rebuild it without
            /// a new reason - this was tested, not assumed.
            ///
            /// VANILLA'S OUTPUT ONLY. Nothing this feature computes may ever be written back into
            /// this field or into CleanPosition — that is the whole invariant. Breaking it feeds our
            /// own result back in as next frame's input, and the correction compounds.
            /// </summary>
            internal static Quaternion CleanLocalRotation = Quaternion.identity;
            private static float _pan, _tilt;              // applied, smoothed
            private static float _panTarget, _tiltTarget;  // accumulated from the axes
            private static bool _loggedLook;
            internal static bool HaveClean;

            /// <summary>
            /// Slot chase into the view cycle: cockpit -> CHASE -> orbit -> TV -> cockpit.
            ///
            /// Only the two hops that need moving are touched. Everything else - Center from orbit,
            /// the death and spectate transitions, the mission editor - still resolves wherever
            /// vanilla sends it, because we only rewrite a destination we recognise arriving from a
            /// source we recognise.
            /// </summary>
            internal static bool SwitchState_Pre(CameraStateManager __instance, ref CameraBaseState state)
            {
                if (state == null) return true;

                var cur = __instance.currentState;

                // #center-recentres-first
                //   In chase, vanilla's "Center" LEAVES for the orbit camera - it is the same button
                //   that got you into chase from orbit, so it toggles. Players press it expecting the
                //   VIEW to recentre and lose the camera mode instead. Off-centre, the first press now
                //   zeroes the look target and the smoothing lerp pans it home; once centred, a second
                //   press exits exactly as vanilla does. The exit is preserved - it just no longer
                //   costs you your place.
                if (cur == __instance.chaseState && state == __instance.orbitState
                    && (_panTarget != 0f || _tiltTarget != 0f))
                {
                    _panTarget = 0f; _tiltTarget = 0f;
                    return false;
                }

                if (!InViewCycle.Value) return true;
                if (cur == __instance.cockpitState && state == __instance.orbitState)
                    state = __instance.chaseState;          // cockpit -> chase, instead of straight to orbit
                else if (cur == __instance.chaseState && state == __instance.TVState)
                    state = __instance.orbitState;          // chase -> orbit, so orbit is not skipped

                return true;
            }

            internal static void EnterState_Post(CameraChaseState __instance, CameraStateManager cam)
            {
                // EnterState bails to the orbit state when the followed unit is not an Aircraft, and a
                // Postfix runs anyway. cameraMode is assigned at the very END of the successful path,
                // so this is the cheapest honest test that the early return did not happen.
                if (CameraStateManager.cameraMode != CameraMode.chase) return;

                // Head tracking must not carry a stale rotation across into a new sortie.
                HaveClean = false;
                CleanLocalRotation = Quaternion.identity;

                if (!ShowHud.Value) return;

                // showHUD is `private bool` - readable here because the csproj publicizes
                // Assembly-CSharp at compile time. No reflection needed.
                __instance.showHUD = true;
                __instance.CheckHUD();
            }

            /// <summary>
            /// Restore the rotation the state machine last produced ON ITS OWN, before it runs.
            ///
            /// #trackir-no-compound
            /// UpdateState lerps cam.transform.localRotation toward cameraCustomRotation every frame,
            /// reading the CURRENT value as the lerp start. If the postfix leaves a head-rotated value
            /// in there, the next frame lerps from the rotated value and the postfix rotates it again:
            /// the offset compounds and the camera walks away. Handing the state machine back its own
            /// clean value each frame means it never sees our contribution at all, and the head offset
            /// stays a pure function of the current head pose.
            /// </summary>
            internal static void UpdateState_Pre(CameraChaseState __instance, CameraStateManager cam)
            {
                if (!Modifying(cam)) return;
                // Free-look PANS the camera in place; it never moves it. So only the rotation is ours
                // to hand back, and vanilla's position lerp is left entirely alone.
                if (HaveClean) cam.transform.localRotation = CleanLocalRotation;

                // Drive vanilla's INPUT rather than fighting its output: posVector is what UpdateState
                // reads to place the camera, so overwriting it here means vanilla's own smoothing,
                // zoom (viewDistAdjust) and terrain-collision linecast all still apply unchanged.
                //
                // Only for the default Back view - CheckInput writes posVector when the player picks a
                // numpad camera position, and stomping that every frame would break those views.
                // Vanilla's Back is (0, 0.1 * orbitDist, -orbitDist). [decompiled]
                if (__instance.currentPos != CameraChaseState.ChasePos.Back) return;

                float d = Distance.Value, h = Height.Value;

                // #aim-at-reticle
                //   vanilla's targetVector is the LOCAL-space direction the pivot looks along, and for
                //   the Back view it is plain Vector3.forward. Point it at the RETICLE'S OWN WORLD
                //   ANCHOR instead - FlightHud pins the reticle to cockpit.position +
                //   cockpit.forward * 4000f - so "centred" means the reticle is at screen centre by
                //   construction, at any camera height and on any airframe.
                //
                //   This replaced a fixed look-down tilt. That tilt aimed BELOW the flight path to show
                //   ground ahead, which necessarily pushed the reticle off centre by the same angle -
                //   the two cannot both be true, and aiming where you shoot won. Driving vanilla's own
                //   field means its smoothing still applies rather than us fighting it.
                Transform aimRef = cam.followingUnit is Aircraft rac && rac.cockpit != null
                    ? rac.cockpit.transform : cam.followingUnit.transform;
                Vector3 anchor = aimRef.position + aimRef.forward * 4000f;
                Vector3 toAnchor = anchor - cam.transform.position;
                if (toAnchor.sqrMagnitude > 1f)
                    __instance.targetVector =
                        cam.followingUnit.transform.InverseTransformDirection(toAnchor.normalized);

                if (AutoFraming.Value && TryAutoFrame(cam, d, h, out Vector3 auto))
                {
                    __instance.posVector = auto;
                    return;
                }

                if (d < 0.999f || d > 1.001f || h < 0.999f || h > 1.001f || Momentum.Value > 0.001f)
                    __instance.posVector = BackVector(cam) * (__instance.orbitDist * d)
                                         + Vector3.up * (0.1f * __instance.orbitDist * h);
            }

            /// <summary>
            /// Which way is "behind", in the aircraft's own axes.
            ///
            /// #momentum
            ///   Vanilla puts the camera behind the NOSE. Under high AoA, in a hard turn, or in a
            ///   sideslip the nose and the flight path are not the same direction, and sitting behind
            ///   the nose means the aircraft looks like it is flying straight when it is not.
            ///   Blending toward the reverse velocity vector puts the camera behind where you are
            ///   actually GOING, so the airframe visibly crabs and yaws within the frame - which is
            ///   what reads as weight.
            ///
            ///   Deliberately NOT smoothed here: vanilla already lerps the camera toward posVector
            ///   every frame, so feeding it a moving target gives the lag for free. Adding a second
            ///   smoothing stage on top would just be two filters fighting.
            ///
            ///   This is the POSITION half of the idea; VelocityAlign is the AIM half. They compose,
            ///   and either is useful alone.
            /// </summary>
            private static Vector3 BackVector(CameraStateManager cam)
            {
                float m = Momentum.Value;
                if (m < 0.001f || cam.followingRB == null || cam.followingUnit == null)
                    return Vector3.back;

                Vector3 vel = cam.followingRB.velocity;
                // Below the floor the velocity DIRECTION is noise, and a parked or hovering aircraft
                // would swing the camera around at random.
                if (vel.sqrMagnitude < 100f) return Vector3.back;

                Vector3 velLocal = cam.followingUnit.transform.InverseTransformDirection(vel.normalized);
                return Vector3.Slerp(Vector3.back, -velLocal, m).normalized;
            }

            /// <summary>
            /// Frame the aircraft to a target screen fill at a target elevation angle — see
            /// #auto-framing. Both inputs are dimensionless, so the same pair works on a light fighter
            /// and a heavy bomber without retuning.
            /// </summary>
            private static bool TryAutoFrame(CameraStateManager cam, float trimD, float trimH, out Vector3 pos)
            {
                pos = Vector3.zero;
                var def = cam.followingUnit != null ? cam.followingUnit.definition : null;
                if (def == null) return false;

                // The dimension that actually governs how big the aircraft looks from behind. Summing
                // length and width the way vanilla does over-weights long airframes.
                float size = Mathf.Max(def.length, def.width);
                if (size <= 0.01f) return false;

                // desiredFOV, not the live camera FOV: the player's FOV zoom should MAGNIFY, not drag
                // the camera in and out chasing a constant screen fill.
                float fov = Mathf.Clamp(cam.desiredFOV > 1f ? cam.desiredFOV : 60f, 10f, 170f);
                float halfAngle = Mathf.Max(1f, 0.5f * fov * Mathf.Clamp01(ScreenFill.Value));

                float dist = (size * 0.5f) / Mathf.Tan(halfAngle * Mathf.Deg2Rad);
                float height = dist * Mathf.Tan(Mathf.Clamp(Elevation.Value, 0f, 45f) * Mathf.Deg2Rad);

                // #reticle-clearance
                //   FlightHud pins the aiming reticle to cockpit.position + cockpit.forward * 4000f.
                //   [decompiled] With the aim point 4 km out and the camera only tens of metres back,
                //   the sightline to it is very nearly level: at the aircraft's own position it sits
                //   at height * 4000/(dist+4000), i.e. within a percent or two of the camera's own
                //   height. So "can I see the reticle past my own tail" reduces to a single question -
                //   is the camera higher than the airframe - and definition.height answers it per
                //   aircraft. Raising the floor is enough; no raycast, no per-frame test.
                float clearance = ReticleClearance.Value;
                if (clearance > 0.001f && def.height > 0.01f)
                    height = Mathf.Max(height, def.height * clearance);

                pos = BackVector(cam) * (dist * trimD) + Vector3.up * (height * trimH);
                return true;
            }

            internal static void UpdateState_Post(CameraStateManager cam)
            {
                if (!Modifying(cam)) { HaveClean = false; return; }

                // Vanilla's own output, before anything of ours. The prefix hands this back next frame.
                CleanLocalRotation = cam.transform.localRotation;
                HaveClean = true;

                Quaternion world = cam.transform.rotation;
                Vector3 fwd = world * Vector3.forward;
                Vector3 up = world * Vector3.up;
                bool reaim = false;
                Quaternion reaimed = CleanLocalRotation;

                // High-AOA fix: the nose points at the sky while the aircraft is still travelling
                // forward, so a nose-locked camera stares at nothing. Aim along a blend toward the
                // velocity vector instead. The speed floor keeps a parked or hovering aircraft (where
                // velocity direction is noise) from spinning the view.
                float va = VelocityAlign.Value;
                if (va > 0.001f && cam.followingRB.velocity.sqrMagnitude > 100f)
                {
                    fwd = Vector3.Slerp(fwd, cam.followingRB.velocity.normalized, va).normalized;
                    reaim = true;
                }

                // Roll damping: 1 welds the camera to the airframe (vanilla), 0 keeps the horizon
                // level and lets the aircraft roll inside the frame.
                float rf = RollFollow.Value;
                Vector3 desiredUp = up;
                if (rf < 0.999f)
                {
                    desiredUp = Vector3.Slerp(Vector3.up, up, rf);
                    reaim = true;
                }

                if (reaim)
                {
                    // Degenerate when looking straight up or down - LookRotation would produce a wild
                    // roll. Leave vanilla's rotation alone for those frames rather than snapping.
                    Vector3 upN = desiredUp.sqrMagnitude > 1e-6f ? desiredUp.normalized : Vector3.up;
                    if (Mathf.Abs(Vector3.Dot(fwd, upN)) < 0.999f)
                    {
                        Quaternion parent = cam.transform.parent != null
                            ? cam.transform.parent.rotation : Quaternion.identity;
                        // Rebuild in world space, then convert back to local. Writing localRotation
                        // rather than moving the pivot is deliberate: it re-aims the view without
                        // displacing the camera, so distance/height stay exactly where they were set.
                        // #no-feedback: into a LOCAL, never back into CleanLocalRotation. Writing the
                        // re-aimed value there made vanilla's next lerp start from OUR output, so the
                        // roll correction re-applied on top of itself every frame - which is exactly
                        // the compounding #trackir-no-compound exists to prevent, reintroduced.
                        reaimed = Quaternion.Inverse(parent) * Quaternion.LookRotation(fwd, upN);
                    }
                }

                Quaternion final = reaim ? reaimed : CleanLocalRotation;

                // #mouselook-mirrors-cockpit
                //   A faithful copy of CameraCockpitState's free-look, because anything else feels
                //   wrong sitting next to it. The part earlier attempts of mine missed: the cockpit
                //   does NOT apply the accumulated angles directly. It accumulates a TARGET and lerps
                //   the applied angle toward it by min(2*dt / viewSmoothing, 1). That one lerp is
                //   where all the smoothness lives - and it smooths recentring for free, because
                //   "Center" only has to set the target to zero.
                //
                //   Consequences, all deliberate: no idle auto-recentre, so the view stays where you
                //   put it just as in the cockpit; sensitivity, pitch inversion and smoothing come
                //   from the player's own game settings rather than knobs of ours; and Free Look is
                //   required only when the virtual joystick is enabled, which is vanilla's own rule.
                if (MouseLookActive())
                {
                    bool vj = PlayerSettings.virtualJoystickEnabled;
                    if (!vj || GameManager.playerInput.GetButton("Free Look"))
                    {
                        float rate = 120f * PlayerSettings.viewSensitivity * Time.unscaledDeltaTime;
                        _panTarget  += GameManager.playerInput.GetAxis("Pan View")  * rate;
                        _tiltTarget += GameManager.playerInput.GetAxis("Tilt View") * rate
                                     * (PlayerSettings.viewInvertPitch ? -1f : 1f);
                    }
                    else { _panTarget = 0f; _tiltTarget = 0f; }   // vanilla's virtual-joystick release

                    _panTarget  = Mathf.Clamp(_panTarget, -165f, 165f);
                    _tiltTarget = Mathf.Clamp(_tiltTarget, -65f, 65f);

                    float k = Mathf.Min(2f * Time.unscaledDeltaTime
                                        / Mathf.Max(PlayerSettings.viewSmoothing, 0.01f), 1f);
                    _pan  = Mathf.Lerp(_pan,  _panTarget,  k);
                    _tilt = Mathf.Lerp(_tilt, _tiltTarget, k);

                    // Settle exactly, so a centred view drops the composed rotation entirely instead
                    // of trailing an ever-smaller offset forever.
                    if (_panTarget  == 0f && Mathf.Abs(_pan)  < 0.05f) _pan  = 0f;
                    if (_tiltTarget == 0f && Mathf.Abs(_tilt) < 0.05f) _tilt = 0f;

                    if (_pan != 0f || _tilt != 0f)
                    {
                        final = final * Quaternion.Euler(_tilt, _pan, 0f);

                        // Proof-of-life, once: separates a dead gate from an unbound axis.
                        if (!_loggedLook)
                        {
                            _loggedLook = true;
                            Plugin.Log.LogInfo("[ChaseCamera] mouse look active (sens "
                                             + PlayerSettings.viewSensitivity.ToString("0.##")
                                             + ", smoothing "
                                             + PlayerSettings.viewSmoothing.ToString("0.##") + ")");
                        }
                    }
                }
                else { _pan = _tilt = _panTarget = _tiltTarget = 0f; }

                if (TrackIrActive())
                {
                    // GetTrackIROffset returns the ABSOLUTE head pose; its arguments are only the
                    // fallback when the client is missing and the recenter target when tracking is
                    // lost. Passing identity means "recenter to looking down the chase axis", and
                    // composing rather than assigning gives look-around-from-behind instead of
                    // replacing the camera's aim the way the cockpit does.
                    // [decompiled: Assembly-CSharp-firstpass]
                    Quaternion head = TrackIRComponent.i.GetTrackIROffset(Vector3.zero, Quaternion.identity).Item2;

                    float amount = TrackIrAmount.Value;
                    if (amount < 0.999f || amount > 1.001f)
                        head = Quaternion.SlerpUnclamped(Quaternion.identity, head, amount);

                    final = final * head;
                }

                cam.transform.localRotation = final;
            }

/// <summary>
            /// Composes WITH TrackIR rather than deferring to it: an earlier version returned false
            /// whenever TrackIR was enabled, which meant that on any machine with UseTrackIR=1 - a
            /// setting made once in the game's own options - mouse look was dead, and no ChaseView
            /// toggle could revive it. Cursor and map guards mirror vanilla's.
            /// </summary>
            private static bool MouseLookActive()
            {
                if (!MouseLook.Value) return false;
                if (!GameManager.flightControlsEnabled) return false;
                if (Cursor.visible || DynamicMap.mapMaximized) return false;
                return true;
            }

            private static bool TrackIrActive() => TrackIr.Value && PlayerSettings.useTrackIR;

            /// <summary>
            /// Are we touching the camera at all this frame? Kept as one test so the prefix's
            /// clean-rotation restore and the postfix's write can never disagree about whether our
            /// state is live - a mismatch there is what produces slow, hard-to-attribute drift.
            /// </summary>
            private static bool Modifying(CameraStateManager cam)
            {
                if (Core.Diag.Bypass) return false;
                if (CameraStateManager.cameraMode != CameraMode.chase) return false;
                // UpdateState early-returns on this, leaving the transform stale. A Postfix runs
                // regardless, so the guard has to be repeated here.
                if (cam == null || cam.followingRB == null) return false;

                return TrackIrActive()
                    || MouseLook.Value
                    || AutoFraming.Value
                    || RollFollow.Value < 0.999f
                    || VelocityAlign.Value > 0.001f
                    || Momentum.Value > 0.001f
                    || Distance.Value < 0.999f || Distance.Value > 1.001f
                    || Height.Value < 0.999f || Height.Value > 1.001f;
            }

            /// <summary>
            /// Purely additive: vanilla's CheckHUD already handles every case correctly except the one
            /// where the user has asked for the HUD in positions vanilla considers unhelpful.
            /// </summary>
            internal static void CheckHUD_Post(CameraChaseState __instance)
            {
                if (!HudInAllPositions.Value || !__instance.showHUD) return;
                FlightHud.EnableCanvas(enable: true);
                DynamicMap.EnableCanvas(enable: true);
            }

            internal static void RestoreChaseHud_Post()
            {
                if (!ShowHud.Value || CameraStateManager.cameraMode != CameraMode.chase) return;

                CameraStateManager cam = SceneSingleton<CameraStateManager>.i;
                if (cam == null || cam.chaseState == null) return;

                // Route through vanilla's own CheckHUD so the position filter still applies and our
                // CheckHUD_Post gets its say. One entry point, one behaviour.
                cam.chaseState.CheckHUD();
            }
        }

        /// <summary>
        /// The optional in-flight toggle key. Lives on BepInEx's plugin GameObject (DontDestroyOnLoad);
        /// one we created would be destroyed at the first scene transition and go silently dead.
        /// </summary>
        private sealed class HotkeyPump : MonoBehaviour
        {
            private ConfigEntry<KeyboardShortcut> _key;

            internal void Init(ConfigEntry<KeyboardShortcut> key) => _key = key;

            private void Update()
            {
                if (_key == null || _key.Value.MainKey == KeyCode.None) return;
                if (CameraStateManager.cameraMode != CameraMode.chase) return;
                if (!_key.Value.IsDown()) return;

                // Do not steal the key from the map or the camera-position editor. Vanilla's own chase
                // input guards on CameraControlUI.isOpen for exactly this reason.
                if (DynamicMap.mapMaximized) return;
                var ui = SceneSingleton<CameraControlUI>.i;
                if (ui != null && ui.isOpen) return;

                CameraStateManager cam = SceneSingleton<CameraStateManager>.i;
                if (cam == null || cam.chaseState == null) return;

                cam.chaseState.ToggleHUD();
                Plugin.Log.LogInfo($"[ChaseCamera] HUD toggled -> {cam.chaseState.showHUD}");
            }
        }
    }
}
