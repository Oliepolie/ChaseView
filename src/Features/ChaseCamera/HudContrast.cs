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
    /// #hud-shadow (default ON) puts a dark halo BEHIND the symbology. The problem is not that the
    ///   HUD is too dim - it is that bright green on bright cloud has almost no luminance
    ///   difference - so a dark backing supplies the missing contrast at the glyph edge and costs
    ///   nothing anywhere else on screen.
    ///
    ///   IT MUST BE TMP'S UNDERLAY, NOT ITS OUTLINE. An earlier build drove _OutlineWidth and was
    ///   REJECTED in flight: readability got monotonically WORSE as the value rose, and 0 was best.
    ///   The reason is that TMP grows an outline INWARD from the glyph edge, consuming the face
    ///   rather than surrounding it. On small HUD digits with thin strokes a 0.2 width eats most of
    ///   the glyph, so the text turns into chunky mud. Compensating with _FaceDilate did not come
    ///   close. Underlay is a separate shader feature that renders behind the glyph and never
    ///   touches the face, which is what was wanted all along. Do not "simplify" this back onto
    ///   _OutlineWidth - it was tried and flown.
    ///
    /// #hud-legibility (default ON, but see below) raises alpha toward opaque. Kept because some
    ///   elements genuinely are translucent, but on measurement most of the HUD is already at alpha
    ///   1, so on its own this changed nothing visible. The one-line summary logged on build reports
    ///   how many elements were actually below full alpha - if that is near zero on your airframe,
    ///   this knob is doing nothing and the shadow is carrying the result.
    ///
    /// #hud-tint (default OFF) darkens the world behind the HUD. The most faithful reproduction of
    ///   the canopy, and the worst trade: dimming the whole screen to read four numbers.
    ///
    /// WHY BOTH TEXT PIPELINES, AND WHY THAT IS THE ROBUST CHOICE
    ///   Unity's Outline component is a BaseMeshEffect and does nothing on TextMeshPro; TMP carries
    ///   its effects in material properties instead. 0.34.0 moved most readouts to TMP but left
    ///   WeaponIndicator on legacy Text. [decompiled] An earlier version of this file argued the
    ///   split made this too fragile to attempt - that was wrong. Handling BOTH is what makes it
    ///   robust: a future Text -> TMP migration just moves an element from one branch to the other,
    ///   and neither branch notices.
    ///
    ///   Note the legacy Outline component was never the problem: it draws offset COPIES behind the
    ///   text and leaves the glyph itself alone, which is the behaviour TMP needed Underlay for.
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
    ///   The shadow needs none of this - nothing in the game writes these material properties, so it
    ///   is applied once when the cache is built or the setting changes, not per frame.
    ///
    /// PARITY: Local. Vertex colours and font materials on this client's own UI.
    /// </summary>
    internal sealed class HudContrast : MonoBehaviour
    {
        internal static float WantTint;        // 0 = no full-screen darkening, and nothing is created
        internal static float WantOpacity;     // 0 = leave vanilla alpha alone
        internal static float WantBrightness;  // 1 = leave vanilla colour alone
        internal static float WantShadow;      // 0 = no dark halo behind the symbology

        private Image _tint;

        private Graphic[] _graphics;
        private Color[] _base;      // the game's own most recent value
        private Color[] _written;   // what we last wrote, to tell the two apart
        private Object _cachedFor;  // the HUDAppManager the cache was built from
        private float _appliedShadow = -1f;

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

            if (!Mathf.Approximately(WantShadow, _appliedShadow))
                ApplyShadow(Mathf.Clamp01(WantShadow));

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
        private void ApplyShadow(float strength)
        {
            int tmp = 0, legacy = 0;

            // TMP's underlay is a soft dark copy rendered BEHIND the glyph. Dilate spreads it outward
            // past the glyph edge, softness blurs it into a halo rather than a hard drop shadow, and
            // the offset stays at zero so the contrast lands on every side - a directional shadow
            // leaves two edges of every digit as exposed as they were.
            float dilate = strength * 0.4f;
            var shadow = new Color(0f, 0f, 0f, 0.9f);

            for (int i = 0; i < _graphics.Length; i++)
            {
                Graphic g = _graphics[i];
                if (g == null) { _cachedFor = null; continue; }

                if (g is TMP_Text t)
                {
                    Material m = t.fontMaterial;   // instances per label on first touch

                    // Belt and braces: an earlier build drove these, and a material instance lives as
                    // long as the label does. Zero them on every apply so nothing lingers.
                    m.SetFloat(ShaderUtilities.ID_OutlineWidth, 0f);
                    m.SetFloat(ShaderUtilities.ID_FaceDilate, 0f);

                    if (strength <= 0.001f)
                    {
                        m.DisableKeyword(ShaderUtilities.Keyword_Underlay);
                        m.SetFloat(ShaderUtilities.ID_UnderlayDilate, 0f);
                        m.SetColor(ShaderUtilities.ID_UnderlayColor, Color.clear);
                    }
                    else
                    {
                        // The shader branch is keyword-gated; setting the properties without this
                        // does nothing at all and looks like the feature is broken.
                        m.EnableKeyword(ShaderUtilities.Keyword_Underlay);
                        m.SetColor(ShaderUtilities.ID_UnderlayColor, shadow);
                        m.SetFloat(ShaderUtilities.ID_UnderlayOffsetX, 0f);
                        m.SetFloat(ShaderUtilities.ID_UnderlayOffsetY, 0f);
                        m.SetFloat(ShaderUtilities.ID_UnderlayDilate, dilate);
                        m.SetFloat(ShaderUtilities.ID_UnderlaySoftness, 0.15f);
                    }

                    // The underlay extends past the original glyph bounds; without this the mesh
                    // padding still describes the bare glyph and the halo is clipped square.
                    t.UpdateMeshPadding();
                    tmp++;
                }
                else if (g is Text legacyText)
                {
                    // Legacy Outline draws offset COPIES behind the text - it never shrinks the glyph,
                    // so it was always the right effect here and needs no equivalent of the underlay.
                    var o = legacyText.GetComponent<Outline>();
                    if (strength <= 0.001f)
                    {
                        if (o != null) Destroy(o);
                        continue;
                    }
                    if (o == null) o = legacyText.gameObject.AddComponent<Outline>();
                    o.effectColor = shadow;
                    float d = Mathf.Max(1f, strength * 3f);
                    o.effectDistance = new Vector2(d, -d);
                    legacy++;
                }
                // RawImage tapes and plain Images get nothing - a halo round a texture quad is a box.
            }

            _appliedShadow = strength;
            Plugin.Log.LogInfo($"[ChaseCamera] HUD shadow {strength:0.##} applied to {tmp} TMP + {legacy} legacy");
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
            _appliedShadow = -1f;   // force a re-apply onto the new aircraft's elements

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

            if (_appliedShadow > 0.001f) ApplyShadow(0f);

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
            _appliedShadow = -1f;
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
