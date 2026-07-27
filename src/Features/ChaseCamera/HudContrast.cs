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
    ///   TWO WRONG ANSWERS WERE FLOWN BEFORE THIS ONE. Both are worth knowing, because each looks
    ///   correct in isolation:
    ///
    ///   1. _OutlineWidth with _FaceDilate = w/2. Readability got monotonically WORSE as the value
    ///      rose, best at 0. TMP carves the outline INWARD from the face edge, so half the outline
    ///      width was eaten out of every glyph. See #outline-dilate-must-equal-width for the exact
    ///      relationship - the fix is d == w, not d == w/2, and that is a one-character difference
    ///      between "sharpens the text" and "dissolves it".
    ///
    ///   2. Underlay, TMP's proper drop-shadow. Correct in principle and did NOTHING in practice:
    ///      it is gated behind the UNDERLAY_ON shader keyword, and the Mobile distance-field variant
    ///      this game ships omits underlay, glow and bevel entirely. Enabling a keyword whose variant
    ///      was never compiled fails silently. LogShader records the shader name once so this is
    ///      answerable rather than re-guessed.
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
    ///   text and leaves the glyph alone, so it needs no dilation correction at all.
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
        internal static bool WantVignette;     // false = flat tint, for a straight A/B
        internal static float WantClearCentre; // how much of the middle the vignette leaves alone

        private Image _tint;

        private Graphic[] _graphics;
        private Color[] _base;      // the game's own most recent value
        private Color[] _written;   // what we last wrote, to tell the two apart
        private Object _cachedFor;  // the HUDAppManager the cache was built from
        private float _appliedShadow = -1f;
        private float _builtEdgeOnly = -1f;
        private Sprite _vignette;
        private static bool _loggedShader;
        private static bool _warnedAdditive;

        /// <summary>See #additive-cannot-darken. Blend is dst+src, so no dark effect can render.</summary>
        private static bool IsAdditive(Material m) =>
            m != null && m.shader != null && m.shader.name.IndexOf("Additive", System.StringComparison.OrdinalIgnoreCase) >= 0;

        private void Update()
        {
            bool chase = !Core.Diag.Bypass && CameraStateManager.cameraMode == CameraMode.chase;

            if (!chase || WantTint <= 0.001f)
            {
                if (_tint != null) _tint.enabled = false;
                return;
            }

            if (!EnsureTint()) return;

            // #tint-vignette
            //   A flat full-screen tint was tried and called drastic, fairly - it dims the sky you
            //   are flying through in order to read numbers parked at the screen EDGES. The
            //   screen-locked readouts all sit around the periphery and the centre holds the reticle
            //   and the fight, so shaping the tint as a vignette darkens precisely where the
            //   symbology is and leaves the middle alone. Same mechanism, a fraction of the cost to
            //   the view.
            float edge = WantVignette ? Mathf.Clamp(WantClearCentre, 0.05f, 0.9f) : 0f;
            if (!Mathf.Approximately(edge, _builtEdgeOnly)) BuildVignette(edge);

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
            int tmp = 0, legacy = 0, skipped = 0;
            var dark = new Color(0f, 0f, 0f, 1f);

            for (int i = 0; i < _graphics.Length; i++)
            {
                Graphic g = _graphics[i];
                if (g == null) { _cachedFor = null; continue; }

                if (g is TMP_Text t)
                {
                    Material m = t.fontMaterial;   // instances per label on first touch
                    if (!_loggedShader) LogShader(m);

                    // #additive-cannot-darken
                    //   This game draws HUD text with 'TextMeshPro/Distance Field Additive'.
                    //   Additive blending is dst + src, so BLACK CONTRIBUTES NOTHING - a dark outline
                    //   is not merely subtle, it is arithmetically absent. Worse, the outline band
                    //   still replaces face pixels, so raising the width erases parts of each glyph
                    //   and adds no border: strictly, monotonically worse. That is what two separate
                    //   flights measured before the shader name was checked.
                    //
                    //   The same reasoning kills every other bright-side fix. Underlay adds black
                    //   (nothing). FaceDilate fattens a bright face against a bright sky. Alpha under
                    //   additive scales how much is ADDED, so opacity can only brighten.
                    //
                    //   On an additive HUD the only thing that raises contrast is darkening what sits
                    //   BEHIND it - which is precisely why real aircraft have tinted canopies, and
                    //   why HudTint is the lever that works. Skip rather than offer a knob that can
                    //   only do harm.
                    if (IsAdditive(m)) { skipped++; continue; }

                    // #outline-dilate-must-equal-width
                    //   TMP carves the outline INWARD from the face edge, so an outline alone erodes
                    //   the glyph it is meant to protect. _FaceDilate pushes that edge back out.
                    //
                    //   The exact relationship, and the bug that cost a flight: with dilate d and
                    //   width w, the face edge sits at +d and the outline band occupies +d-w to +d,
                    //   leaving the coloured face reaching only to +d-w. For the face to keep its
                    //   ORIGINAL extent you need d - w = 0, i.e. d == w, which puts the whole band
                    //   outside the original glyph. An earlier build used d = w/2, eroding every
                    //   glyph by half the outline width - which is why readability got monotonically
                    //   WORSE as the setting rose and 0 was best.
                    m.SetFloat(ShaderUtilities.ID_OutlineWidth, strength);
                    m.SetFloat(ShaderUtilities.ID_FaceDilate, strength);
                    m.SetColor(ShaderUtilities.ID_OutlineColor, dark);

                    // Underlay would be the softer effect, but it is keyword-gated and the Mobile
                    // distance-field shader omits it entirely - setting it produced no visible change
                    // at all. Kept off rather than left half-configured.
                    m.DisableKeyword(ShaderUtilities.Keyword_Underlay);

                    // The band extends past the original glyph bounds; without this the mesh padding
                    // still describes the bare glyph and the edge is clipped square.
                    t.UpdateMeshPadding();
                    tmp++;
                }
                else if (g is Text legacyText)
                {
                    // Legacy Outline draws offset COPIES behind the text and never shrinks the glyph,
                    // so it needs none of the dilation correction above.
                    var o = legacyText.GetComponent<Outline>();
                    if (strength <= 0.001f)
                    {
                        if (o != null) Destroy(o);
                        continue;
                    }
                    if (o == null) o = legacyText.gameObject.AddComponent<Outline>();
                    o.effectColor = new Color(0f, 0f, 0f, 0.9f);
                    float d = Mathf.Max(1f, strength * 6f);
                    o.effectDistance = new Vector2(d, -d);
                    legacy++;
                }
                // RawImage tapes and plain Images get nothing - a border round a texture quad is a box.
            }

            _appliedShadow = strength;
            if (skipped > 0 && !_warnedAdditive)
            {
                _warnedAdditive = true;
                Plugin.Log.LogWarning($"[ChaseCamera] {skipped} TMP element(s) use an ADDITIVE font "
                                    + "shader - a dark edge cannot render on those at all. Use HudTint "
                                    + "for those; see #additive-cannot-darken.");
            }
            Plugin.Log.LogInfo($"[ChaseCamera] HUD shadow {strength:0.##} applied to {tmp} TMP + "
                             + $"{legacy} legacy, {skipped} skipped (additive)");
        }

        /// <summary>
        /// Logged once. The shader NAME is the whole diagnosis - see #additive-cannot-darken. Also
        /// probes which non-additive TMP shaders the build actually contains, because a
        /// draw-the-text-twice-in-dark approach needs an alpha-blended material to darken with, and
        /// Unity strips shader variants nothing references. Shader.Find returning null means the
        /// technique is impossible here, not merely expensive.
        /// </summary>
        private static void LogShader(Material m)
        {
            _loggedShader = true;
            Plugin.Log.LogInfo($"[ChaseCamera] TMP font shader '{m.shader.name}' "
                             + $"outlineProp={m.HasProperty(ShaderUtilities.ID_OutlineWidth)} "
                             + $"underlayProp={m.HasProperty(ShaderUtilities.ID_UnderlayColor)} "
                             + $"additive={IsAdditive(m)}");

            // Is an alpha-blended TMP shader even present? Needed by any darken-behind approach.
            string[] candidates =
            {
                "TextMeshPro/Distance Field",
                "TextMeshPro/Mobile/Distance Field",
                "TextMeshPro/Distance Field Overlay",
            };
            var sb = new System.Text.StringBuilder("[ChaseCamera] alpha-blended TMP shaders:");
            foreach (string name in candidates)
                sb.Append(' ').Append(name).Append('=').Append(Shader.Find(name) != null ? "YES" : "no");
            Plugin.Log.LogInfo(sb.ToString());
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

        /// <summary>
        /// Generates the falloff texture rather than shipping one - the project deliberately ships no
        /// assets, and a 128x128 alpha ramp costs less to compute than to load. Regenerated only when
        /// the setting moves.
        ///
        /// Stretched over a 16:9 rect the radial ramp becomes elliptical, which is what a vignette
        /// should be anyway.
        /// </summary>
        private void BuildVignette(float inner)
        {
            _builtEdgeOnly = inner;

            if (inner <= 0.001f)
            {
                _tint.sprite = null;   // flat, uniform tint - Image draws a plain white quad
                return;
            }

            const int N = 128;
            var tex = new Texture2D(N, N, TextureFormat.RGBA32, mipChain: false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };

            var px = new Color32[N * N];
            for (int y = 0; y < N; y++)
            {
                // Normalised so the edge midpoints sit at 1 and the corners at ~1.41.
                float ny = (y + 0.5f) / N * 2f - 1f;
                for (int x = 0; x < N; x++)
                {
                    float nx = (x + 0.5f) / N * 2f - 1f;
                    float r = Mathf.Sqrt(nx * nx + ny * ny);
                    float t = Mathf.Clamp01(Mathf.InverseLerp(inner, 1f, r));
                    t = t * t * (3f - 2f * t);          // smoothstep, so the ramp has no visible band
                    px[y * N + x] = new Color32(255, 255, 255, (byte)(t * 255f));
                }
            }
            tex.SetPixels32(px);
            tex.Apply(updateMipmaps: false);

            if (_vignette != null) Destroy(_vignette);
            _vignette = Sprite.Create(tex, new Rect(0f, 0f, N, N), new Vector2(0.5f, 0.5f));
            _tint.sprite = _vignette;
            Plugin.Log.LogInfo($"[ChaseCamera] tint vignette rebuilt, clear centre {inner:0.##}");
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
