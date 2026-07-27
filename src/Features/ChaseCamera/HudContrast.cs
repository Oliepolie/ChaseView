using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ChaseView.Features
{
    /// <summary>
    /// Makes the chase HUD readable against raw daylight. In the cockpit the canopy glass darkens
    /// everything behind the symbology; chase has no canopy, so the same symbology competes with a
    /// blown-out sky.
    ///
    /// THREE LEVERS, IN THE ORDER THEY ACTUALLY HELP
    ///
    /// #hud-outline (default ON) draws a dark border around the symbology. This is the one that
    ///   works, and it is what every game does, because the problem is not that the HUD is too dim -
    ///   it is that bright green on bright cloud has almost no luminance difference. A dark stroke
    ///   supplies the missing contrast at the only place it matters, the glyph edge, and costs
    ///   nothing anywhere else on screen.
    ///
    /// #hud-legibility (default ON, but see below) raises alpha toward opaque. Kept because some
    ///   elements genuinely are translucent, but on measurement most of the HUD is already at alpha
    ///   1, so on its own this changed nothing visible. The one-line summary logged on build reports
    ///   how many elements were actually below full alpha - if that is near zero on your airframe,
    ///   this knob is doing nothing and the outline is carrying the result.
    ///
    /// #hud-tint (default OFF) darkens the world behind the HUD. The most faithful reproduction of
    ///   the canopy, and the worst trade: dimming the whole screen to read four numbers.
    ///
    /// WHY BOTH TEXT PIPELINES, AND WHY THAT IS THE ROBUST CHOICE
    ///   Unity's Outline component is a BaseMeshEffect and does nothing on TextMeshPro; TMP carries
    ///   its outline in material properties instead. 0.34.0 moved most readouts to TMP but left
    ///   WeaponIndicator on legacy Text. [decompiled] An earlier version of this file argued the
    ///   split made outlines too fragile to attempt - that was wrong. Handling BOTH is what makes it
    ///   robust: a future Text -> TMP migration just moves an element from one branch to the other,
    ///   and neither branch notices.
    ///
    /// THE COMPOUNDING TRAP, for the colour pass only
    ///   HUD apps rewrite their colours every frame from HUDAppManager.Update - AoADisplay evaluates
    ///   a gradient, others apply the player's hud colour - so the colour pass runs in LateUpdate to
    ///   land after them. But some elements are coloured ONCE and never touched again, and blindly
    ///   transforming Graphic.color every frame would re-transform our own output on those and drive
    ///   them to white within a second.
    ///
    ///   So each graphic remembers what WE wrote. If its colour still equals that, the game has not
    ///   touched it and the stored base is still truth; if it differs, the game wrote something new
    ///   and that becomes the new base. The transform is always computed from the game's value, never
    ///   from ours. Same invariant as #no-feedback on the camera rotation.
    ///
    ///   The outline needs none of this - nothing in the game writes outline width, so it is applied
    ///   once when the cache is built or the setting changes, not per frame.
    ///
    /// PARITY: Local. Vertex colours and font materials on this client's own UI.
    /// </summary>
    internal sealed class HudContrast : MonoBehaviour
    {
        internal static float WantTint;        // 0 = no full-screen darkening, and nothing is created
        internal static float WantOpacity;     // 0 = leave vanilla alpha alone
        internal static float WantBrightness;  // 1 = leave vanilla colour alone
        internal static float WantOutline;     // 0 = no dark border on the symbology

        private Image _tint;

        private Graphic[] _graphics;
        private Color[] _base;      // the game's own most recent value
        private Color[] _written;   // what we last wrote, to tell the two apart
        private Object _cachedFor;  // the HUDAppManager the cache was built from
        private float _appliedOutline = -1f;

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

        /// <summary>After HUDAppManager.Update has written this frame's values.</summary>
        private void LateUpdate()
        {
            bool chase = !Core.Diag.Bypass && CameraStateManager.cameraMode == CameraMode.chase;
            if (!chase) { Restore(); return; }

            if (!EnsureGraphics()) return;

            if (!Mathf.Approximately(WantOutline, _appliedOutline))
                ApplyOutline(Mathf.Clamp(WantOutline, 0f, 0.4f));

            if (WantOpacity > 0.001f || WantBrightness > 1.001f) ApplyColour();
        }

        private void ApplyColour()
        {
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
                // this. Forcing those opaque would paint HUD that is meant to be gone.
                if (opacity > 0.001f && _base[i].a > 0.01f)
                    outc.a = Mathf.Lerp(_base[i].a, 1f, opacity);

                if (outc != cur) g.color = outc;
                _written[i] = outc;
            }
        }

        /// <summary>
        /// Applied on change rather than per frame: nothing in the game writes these, so there is no
        /// value to fight over and no compounding to guard against. TMP also instances a material on
        /// first fontMaterial access, which is not something to do sixty times a second.
        /// </summary>
        private void ApplyOutline(float width)
        {
            int tmp = 0, legacy = 0;

            for (int i = 0; i < _graphics.Length; i++)
            {
                Graphic g = _graphics[i];
                if (g == null) { _cachedFor = null; continue; }

                if (g is TMP_Text t)
                {
                    Material m = t.fontMaterial;   // instances per label on first touch
                    m.SetFloat(ShaderUtilities.ID_OutlineWidth, width);
                    m.SetColor(ShaderUtilities.ID_OutlineColor, Color.black);
                    // TMP grows the outline INWARD from the glyph edge, so a stroke thins the face it
                    // is meant to protect. Dilating by half the width keeps the original weight.
                    m.SetFloat(ShaderUtilities.ID_FaceDilate, width * 0.5f);
                    // Without this the mesh bounds still describe the un-outlined glyph and the new
                    // stroke is clipped at the edges - visible as chipped corners on wide characters.
                    t.UpdateMeshPadding();
                    tmp++;
                }
                else if (g is Text legacyText)
                {
                    var o = legacyText.GetComponent<Outline>();
                    if (width <= 0.001f)
                    {
                        if (o != null) Destroy(o);
                        continue;
                    }
                    if (o == null) o = legacyText.gameObject.AddComponent<Outline>();
                    o.effectColor = new Color(0f, 0f, 0f, 0.9f);
                    // TMP width is a 0..1 fraction of the glyph; legacy Outline is in pixels. Six is
                    // the factor that makes the two look like the same stroke at 1440p.
                    float d = Mathf.Max(1f, width * 6f);
                    o.effectDistance = new Vector2(d, -d);
                    legacy++;
                }
                // RawImage tapes and plain Images get nothing - an outline round a texture quad is a
                // box, not a border.
            }

            _appliedOutline = width;
            Plugin.Log.LogInfo($"[ChaseCamera] HUD outline {width:0.##} applied to {tmp} TMP + {legacy} legacy");
        }

        /// <summary>
        /// Collected from the two roots that between them cover the flight symbology, deduped:
        /// HUDCenter (pitch ladder, waterline, reticle) and the HUDAppManager subtree (the readouts).
        ///
        /// Taking the app subtree by its own root rather than walking HUDCenter is deliberate:
        /// ScreenLockedReadouts may have reparented it onto our anchor, so it is not always under
        /// HUDCenter - but it is the same object set either way, which is why toggling the screen lock
        /// needs no rebuild. Rebuilt only when the aircraft changes, per #perf-treewalk.
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
            if (_tint != null) set.Remove(_tint);   // never restyle our own quad

            if (set.Count == 0) return false;

            _graphics = new Graphic[set.Count];
            set.CopyTo(_graphics);
            _base = new Color[_graphics.Length];
            _written = new Color[_graphics.Length];

            int tmp = 0, legacy = 0, translucent = 0;
            for (int i = 0; i < _graphics.Length; i++)
            {
                _base[i] = _graphics[i].color;
                _written[i] = _base[i];
                if (_graphics[i] is TMP_Text) tmp++;
                else if (_graphics[i] is Text) legacy++;
                if (_base[i].a < 0.99f && _base[i].a > 0.01f) translucent++;
            }

            _cachedFor = mgr;
            _appliedOutline = -1f;   // force a re-apply onto the new aircraft's elements

            // One line, because it answers the only question worth asking when a knob does nothing:
            // how much of this HUD is even translucent, and how much is text at all.
            Plugin.Log.LogInfo($"[ChaseCamera] HUD legibility: {_graphics.Length} elements "
                             + $"({tmp} TMP, {legacy} legacy Text), {translucent} below full alpha");
            return true;
        }

        private static void Collect(Transform root, HashSet<Graphic> into)
        {
            var found = root.GetComponentsInChildren<Graphic>(includeInactive: true);
            for (int i = 0; i < found.Length; i++) into.Add(found[i]);
        }

        /// <summary>
        /// Hand every element back the last value the GAME chose, not the value it had when chase was
        /// entered - those differ on anything animated, and restoring a stale snapshot would freeze a
        /// gradient at whatever it happened to read.
        /// </summary>
        private void Restore()
        {
            if (_graphics == null) return;

            if (_appliedOutline > 0.001f) ApplyOutline(0f);

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
            _appliedOutline = -1f;
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

        private void OnDisable() => Restore();

        private void OnDestroy()
        {
            Restore();
            if (_tint != null) Destroy(_tint.gameObject);
            _tint = null;
        }
    }
}
