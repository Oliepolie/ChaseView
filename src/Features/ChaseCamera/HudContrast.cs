using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace ChaseView.Features
{
    /// <summary>
    /// Makes the chase HUD readable against raw daylight. In the cockpit the canopy glass darkens
    /// everything behind the symbology; chase has no canopy, so the same symbology competes with a
    /// blown-out sky and washes out.
    ///
    /// TWO LEVERS, AND THE ORDER MATTERS
    ///
    /// #hud-legibility (default ON) acts on the SYMBOLOGY. Every HUD element - legacy Text, TMP,
    ///   RawImage tapes, the reticle - derives from UnityEngine.UI.Graphic, so Graphic.color is one
    ///   uniform lever across the whole HUD regardless of which text pipeline it uses. Raising alpha
    ///   toward opaque is what actually buys readability: the symbology is already bright, it is the
    ///   translucency that lets the sky through it.
    ///
    ///   This is why an Outline component was not the answer. Unity's Outline is a BaseMeshEffect and
    ///   does not apply to TextMeshPro at all, and 0.34.0 moved nearly every readout to TMP while
    ///   leaving WeaponIndicator on legacy Text. [decompiled] Graphic.color spans both.
    ///
    /// #hud-tint (default OFF) darkens the world behind the HUD instead. It works and it is the most
    ///   faithful reproduction of the canopy, but dimming the entire screen to read four numbers is a
    ///   heavy trade - so it stays available and stays off.
    ///
    /// THE COMPOUNDING TRAP, which is the whole difficulty here
    ///   The HUD apps rewrite their own colours every frame from HUDAppManager.Update - AoADisplay
    ///   evaluates a gradient, others apply the player's hud colour. So we must run in LateUpdate to
    ///   land after them. But some elements are coloured ONCE and never touched again, and blindly
    ///   transforming Graphic.color every frame would re-transform our own output on those, driving
    ///   them to full white within a second.
    ///
    ///   So each graphic remembers what WE last wrote. If its current colour still equals that, the
    ///   game has not touched it and the stored base is still the truth; if it differs, the game has
    ///   written something new and that becomes the new base. Either way the transform is always
    ///   computed from the game's value, never from ours. Same invariant as #no-feedback on the
    ///   camera rotation, for the same reason.
    ///
    /// PARITY: Local. Vertex colours on this client's own UI.
    /// </summary>
    internal sealed class HudContrast : MonoBehaviour
    {
        internal static float WantTint;        // 0 = no full-screen darkening, and nothing is created
        internal static float WantOpacity;     // 0 = leave vanilla alpha alone
        internal static float WantBrightness;  // 1 = leave vanilla colour alone

        private Image _tint;

        private Graphic[] _graphics;
        private Color[] _base;      // the game's own most recent value
        private Color[] _written;   // what we last wrote, to tell the two apart
        private Object _cachedFor;  // the HUDAppManager the cache was built from

        private void Update()
        {
            bool chase = !Core.Diag.Bypass && CameraStateManager.cameraMode == CameraMode.chase;

            if (!chase || WantTint <= 0.001f)
            {
                if (_tint != null) _tint.enabled = false;
                return;
            }

            if (!EnsureTint()) return;
            _tint.enabled = true;
            _tint.color = new Color(0f, 0f, 0f, Mathf.Clamp01(WantTint));
        }

        /// <summary>
        /// After HUDAppManager.Update has written this frame's values - see the compounding note.
        /// </summary>
        private void LateUpdate()
        {
            bool want = !Core.Diag.Bypass
                     && CameraStateManager.cameraMode == CameraMode.chase
                     && (WantOpacity > 0.001f || WantBrightness > 1.001f);

            if (!want) { RestoreColours(); return; }

            if (!EnsureGraphics()) return;

            float opacity = Mathf.Clamp01(WantOpacity);
            float bright = Mathf.Clamp(WantBrightness, 1f, 4f);

            for (int i = 0; i < _graphics.Length; i++)
            {
                Graphic g = _graphics[i];
                if (g == null) { _cachedFor = null; continue; }   // destroyed - rebuild next frame

                Color cur = g.color;
                if (cur != _written[i]) _base[i] = cur;           // the game moved it; retake the base

                Color outc = _base[i];
                if (bright > 1.001f)
                {
                    outc.r = Mathf.Clamp01(outc.r * bright);
                    outc.g = Mathf.Clamp01(outc.g * bright);
                    outc.b = Mathf.Clamp01(outc.b * bright);
                }
                // Alpha 0 is how the game HIDES an element - the undamaged damage diagram is exactly
                // this. Forcing those opaque would paint parts of the HUD that are meant to be gone,
                // so only elements already drawing something get pushed toward solid.
                if (opacity > 0.001f && _base[i].a > 0.01f)
                    outc.a = Mathf.Lerp(_base[i].a, 1f, opacity);

                if (outc != cur) g.color = outc;
                _written[i] = outc;
            }
        }

        /// <summary>
        /// Collected from the two roots that between them cover the flight symbology, deduped:
        /// HUDCenter (pitch ladder, waterline, reticle) and the HUDAppManager subtree (every readout).
        ///
        /// Taking the app subtree by its own root rather than by walking HUDCenter is deliberate:
        /// ScreenLockedReadouts may have reparented it onto our screen-locked anchor, so it is not
        /// always under HUDCenter - but it is the same set of objects either way, which is why
        /// toggling the screen lock needs no rebuild. Rebuilt only when the aircraft changes, per
        /// #perf-treewalk; the per-frame path touches a flat array and allocates nothing.
        /// </summary>
        private bool EnsureGraphics()
        {
            HUDAppManager mgr = SceneSingleton<HUDAppManager>.i;
            if (_graphics != null && _cachedFor == mgr && mgr != null) return true;

            FlightHud hud = SceneSingleton<FlightHud>.i;
            if (hud == null) return false;

            var set = new HashSet<Graphic>();
            Transform hudCenter = hud.GetHUDCenter();
            if (hudCenter != null) Collect(hudCenter, set);
            if (mgr != null) Collect(mgr.transform, set);
            // compass is a private [SerializeField]; reachable via the publicizer. Explicit because it
            // may have been screen-locked out of HUDCenter on its own.
            if (hud.compass != null) set.Add(hud.compass);
            if (_tint != null) set.Remove(_tint);   // never re-tint our own quad

            if (set.Count == 0) return false;

            _graphics = new Graphic[set.Count];
            set.CopyTo(_graphics);
            _base = new Color[_graphics.Length];
            _written = new Color[_graphics.Length];
            for (int i = 0; i < _graphics.Length; i++)
            {
                _base[i] = _graphics[i].color;
                _written[i] = _base[i];
            }

            _cachedFor = mgr;
            Plugin.Log.LogInfo($"[ChaseCamera] HUD legibility tracking {_graphics.Length} element(s)");
            return true;
        }

        private static void Collect(Transform root, HashSet<Graphic> into)
        {
            var found = root.GetComponentsInChildren<Graphic>(includeInactive: true);
            for (int i = 0; i < found.Length; i++) into.Add(found[i]);
        }

        /// <summary>
        /// Hand every element back the last value the GAME chose, not the value it had when we started
        /// - those differ on anything animated, and restoring a stale snapshot would freeze a gradient
        /// at whatever it read when chase was entered.
        /// </summary>
        private void RestoreColours()
        {
            if (_graphics == null) return;

            for (int i = 0; i < _graphics.Length; i++)
            {
                Graphic g = _graphics[i];
                if (g == null) continue;
                if (g.color == _written[i]) g.color = _base[i];
            }

            _graphics = null;
            _base = null;
            _written = null;
            _cachedFor = null;
        }

        private bool EnsureTint()
        {
            if (_tint != null) return true;

            FlightHud hud = SceneSingleton<FlightHud>.i;
            // canvas is a private [SerializeField]; readable because the csproj publicizes
            // Assembly-CSharp at compile time.
            if (hud == null || hud.canvas == null) return false;

            var go = new GameObject("ChaseView_HudTint",
                                    typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            var rt = (RectTransform)go.transform;
            rt.SetParent(hud.canvas.transform, worldPositionStays: false);

            // Stretch to the full canvas so it covers the screen at any resolution.
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            rt.localScale = Vector3.one;

            _tint = go.GetComponent<Image>();
            _tint.raycastTarget = false;   // a full-screen quad must not swallow clicks

            // Canvas children draw in sibling order, so first means over the world and under every
            // HUD element - the canopy's optical position.
            rt.SetAsFirstSibling();

            // Our own quad must never be fed to the legibility pass.
            _graphics = null;
            _cachedFor = null;

            Plugin.Log.LogInfo("[ChaseCamera] HUD tint created");
            return true;
        }

        private void OnDisable() => RestoreColours();

        private void OnDestroy()
        {
            RestoreColours();
            if (_tint != null) Destroy(_tint.gameObject);
            _tint = null;
        }
    }
}
