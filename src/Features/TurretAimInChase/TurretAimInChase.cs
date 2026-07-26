using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using ChaseView.Core;

namespace ChaseView.Features
{
    /// <summary>
    /// Lets manually-aimed turrets follow the camera in chase view, the way they already do in the
    /// cockpit.
    ///
    /// THE BUG THIS FIXES  [decompiled 2026-07-26]
    ///   Turret.FixedUpdate updates a manual turret's aim from ONE place:
    ///
    ///     if (aircraft.LocalSim &amp;&amp; CameraStateManager.i.currentState == CameraStateManager.i.cockpitState)
    ///         SetVector(CameraStateManager.i.transform.forward);
    ///     ...
    ///     AimTurret(manualVector);
    ///
    ///   Cockpit only. In any other view the vector is never refreshed, but AimTurret still runs on it
    ///   every fixed frame - and SetVector stores the camera's WORLD-space forward with no conversion
    ///   to the aircraft's frame. So the turret holds a fixed compass bearing while the aircraft turns
    ///   underneath it. Come round 180 degrees and it is aiming behind you, with the pitch component
    ///   unchanged because world-down does not rotate with you.
    ///
    ///   That is vanilla behaviour, not a regression. It was simply unreachable until ChaseView put a
    ///   HUD in the chase camera and made turret aiming look available there.
    ///
    /// PARITY: TouchesServer — the only feature here that is. Justification:
    ///   - It sends Aircraft.SetTurretVector, which is EXACTLY the message vanilla sends from the
    ///     cockpit path, for the local player's own aircraft only.
    ///   - It honours vanilla's own 0.2s send throttle, and the RPC carries the game's rate limit
    ///     (CmdSetTurretVector: 20/100 per second) regardless of what we do.
    ///   - No new message type, no new field, nothing another peer parses differently.
    ///   So the wire traffic is indistinguishable from a player flying in cockpit view. It is still
    ///   TouchesServer rather than Local because it does send, and that deserves to be visible in the
    ///   config dump rather than buried.
    ///   OWED: a real two-machine socket test. This has only been reasoned about.
    ///
    /// Its own feature, not part of ChaseCamera, precisely because of that: anyone who wants the chase
    /// camera without anything that transmits can delete this folder and keep the rest.
    /// </summary>
    internal sealed class TurretAimInChase : Feature
    {
        public override string Name => "TurretAimInChase";

        public override string Description =>
            "Manually-aimed turrets follow the camera in chase view. Vanilla only updates turret aim "
          + "in the cockpit, so in any external view the turret holds a fixed compass bearing and ends "
          + "up pointing the wrong way as you turn. Sends the same message the cockpit path sends.";

        public override Parity Parity => Parity.TouchesServer;

        internal static ConfigEntry<bool> Converge;
        internal static ConfigEntry<float> FarAimDistance;

        protected override void BindOptions(ConfigFile config)
        {
            Converge = config.Bind(Name, "ConvergeOnAimPoint", true, Cfg.Adv("Aim turrets at the point you are looking at rather than parallel to the camera."));

            FarAimDistance = config.Bind(Name, "FarAimDistance", 4000f, Cfg.Adv("Aim distance when nothing is under the crosshair.", new AcceptableValueRange<float>(500f, 12000f)));
        }

        public override void DumpResolved(System.Action<string, object> kv)
        {
            kv("ConvergeOnAimPoint", Converge.Value);
            kv("FarAimDistance", FarAimDistance.Value);
        }

        public override void Apply(Harmony harmony)
        {
            var m = AccessTools.Method(typeof(Turret), "FixedUpdate");
            if (m == null)
            {
                Plugin.Log.LogWarning($"[{Name}] Turret.FixedUpdate not found - skipped (did the game update?)");
                return;
            }
            harmony.Patch(m, prefix: Safe(typeof(Hooks), nameof(Hooks.BeforeFixedUpdate)));

            // #turret-reticle
            //   Turret.GetDirection() returns elevationTransform.position + forward * 10000f - a point
            //   ten kilometres down the barrel - and its ONLY caller is HUDTurretCrosshair.Refresh,
            //   which projects it to screen. [decompiled] That is fine while the barrel is parallel to
            //   the camera, but once it CONVERGES on a nearby point the two rays cross there and
            //   diverge again, so the 10km point projects off-centre by exactly the parallax we just
            //   removed from the bullets. Fixing the aim without this simply moved the error from the
            //   rounds to the reticle.
            //
            //   Display-only, single caller, so overriding the result is safe and affects nothing else.
            var dir = AccessTools.Method(typeof(Turret), nameof(Turret.GetDirection));
            if (dir != null)
                harmony.Patch(dir, postfix: Safe(typeof(Hooks), nameof(Hooks.AfterGetDirection)));
            else
                Plugin.Log.LogWarning($"[{Name}] Turret.GetDirection not found - reticle will sit off-centre");
        }

        internal static class Hooks
        {
            private static Turret _aimTurret;
            private static Vector3 _aimPoint;
            private static float _aimAt = -99f;

            /// <summary>
            /// Point the crosshair at the convergence point. See #turret-reticle.
            ///
            /// Guarded on freshness and on still being in chase: if the player switches to the cockpit
            /// or the turret stops being manually aimed, the cached point goes stale within a couple of
            /// frames and vanilla's own 10km projection takes over again — which is correct there,
            /// because the cockpit camera has no parallax to correct for.
            /// </summary>
            internal static void AfterGetDirection(Turret __instance, ref Vector3 __result)
            {
                if (!Converge.Value) return;
                if (_aimTurret == null || __instance != _aimTurret) return;
                if (Time.timeSinceLevelLoad - _aimAt > 0.5f) return;

                var cam = SceneSingleton<CameraStateManager>.i;
                if (cam == null || cam.currentState != cam.chaseState) return;

                __result = _aimPoint;
            }

            /// <summary>
            /// #turret-parallax
            ///   Vanilla feeds the turret the camera's world-space FORWARD. That is right in the
            ///   cockpit, where the camera is effectively on top of the turret, so "the direction the
            ///   camera looks" and "the direction from the turret to what the camera is looking at"
            ///   are the same ray. In chase view the camera is tens of metres behind and above, and
            ///   those two rays diverge by more the CLOSER the target is - which is why strafing
            ///   ground targets put rounds consistently off to one side while a distant target looked
            ///   fine. An earlier comment in this file called that error negligible; a screenshot of
            ///   a gun run said otherwise.
            ///
            ///   So: find the point the camera is actually looking at, and aim the turret AT it.
            ///   Beyond a few hundred metres this converges back to vanilla's answer on its own.
            /// </summary>
            private static Vector3 AimDirection(CameraStateManager cam, Aircraft ac, Turret turret)
            {
                Vector3 camPos = cam.transform.position;
                Vector3 fwd = cam.transform.forward;

                if (!Converge.Value) return fwd;

                float far = FarAimDistance.Value;
                Vector3 aimPoint = camPos + fwd * far;

                // Start the ray PAST our own airframe. The chase camera looks straight over the
                // aircraft, so a ray cast from the camera would happily report a hit on our own wing
                // and swing the turret into the ground.
                float skip = Vector3.Distance(camPos, ac.transform.position)
                           + (ac.definition != null ? ac.definition.length : 0f);
                Vector3 origin = camPos + fwd * skip;

                if (Physics.Raycast(origin, fwd, out RaycastHit hit, far,
                                    Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                    aimPoint = hit.point;

                // Cached for the crosshair, so it marks the convergence point rather than a fixed
                // 10km projection. Recomputing there would mean a second raycast per frame per turret
                // for something purely cosmetic.
                _aimTurret = turret;
                _aimPoint = aimPoint;
                _aimAt = Time.timeSinceLevelLoad;

                Vector3 dir = aimPoint - turret.transform.position;
                return dir.sqrMagnitude > 0.001f ? dir.normalized : fwd;
            }

            /// <summary>
            /// Do for chase view what vanilla does for the cockpit, then let the original run so its
            /// own AimTurret(manualVector) picks up the fresh value.
            ///
            /// This is a per-turret FixedUpdate on every turret in the world, so the guards are ordered
            /// cheapest-first and the common case (an AI turret, or a turret under fire control) leaves
            /// on the first comparison.
            /// </summary>
            internal static void BeforeFixedUpdate(Turret __instance)
            {
                // Cheapest possible rejection: the overwhelming majority of turrets are not manual.
                if (!__instance.manual) return;
                if (__instance.target != null) return;      // auto-tracking; vanilla owns the vector

                Aircraft ac = __instance.aircraft;
                if (ac == null || !ac.LocalSim) return;     // never touch a remote peer's turret

                var cam = SceneSingleton<CameraStateManager>.i;
                if (cam == null) return;

                // Cockpit is vanilla's job - patching it too would double the sends.
                if (cam.currentState != cam.chaseState) return;

                var station = __instance.currentWeaponStation;
                if (station == null || cam.transform == null) return;

                __instance.SetVector(AimDirection(cam, ac, __instance));

                // Vanilla's own throttle, reproduced rather than improved on: the RPC is rate-limited
                // server-side and sending faster would just get dropped.
                if (Time.timeSinceLevelLoad - __instance.lastVectorSent > 0.2f)
                {
                    ac.SetTurretVector(station.Number, __instance.manualVector);
                    __instance.lastVectorSent = Time.timeSinceLevelLoad;
                }
            }
        }
    }
}
