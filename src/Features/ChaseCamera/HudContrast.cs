using UnityEngine;
using UnityEngine.UI;

namespace ChaseView.Features
{
    /// <summary>
    /// Puts the canopy back, optically. In the cockpit the canopy glass tints everything behind the
    /// HUD, so bright symbology sits against a darkened sky and stays readable. Chase view has no
    /// canopy, so the same symbology competes with raw daylight and washes out.
    ///
    /// #hud-tint
    ///   A single full-screen black Image, inserted as the FIRST child of FlightHud's own canvas.
    ///   Canvas children draw in sibling order, so first-sibling means it renders over the 3D world
    ///   and UNDER every HUD element - which is exactly the canopy's optical position. One quad, one
    ///   draw call, no per-element work.
    ///
    /// WHY NOT OUTLINE THE TEXT, which is the obvious answer
    ///   The 0.34.0 update migrated nearly every readout from UnityEngine.UI.Text to
    ///   TextMeshProUGUI - GIndicators, AoADisplay, SpeedGauge, ClimbRate, MachIndicator,
    ///   GearIndicator - while WeaponIndicator is still legacy Text. [decompiled]
    ///   Unity's Outline component is a BaseMeshEffect and does not apply to TMP at all; TMP needs
    ///   its outline set through material properties, which means instancing a material per label and
    ///   owning its cleanup. That is two code paths over a boundary the game is visibly still moving,
    ///   and it would break again at the next migration. A tint is indifferent to what the text is.
    ///
    ///   It also darkens the SKY, not just the glyph edges, which is the actual complaint - an outline
    ///   sharpens letters against a bright background but does nothing about the background itself.
    ///
    /// Only the FlightHud canvas is tinted, so the minimap and combat HUD - separate canvases drawn
    /// later - stay at full brightness. Deliberate: those are already high-contrast icons on dark
    /// panels and dimming them would cost readability rather than buy it.
    ///
    /// PARITY: Local. One translucent quad on this client's own UI canvas.
    /// </summary>
    internal sealed class HudContrast : MonoBehaviour
    {
        /// <summary>Alpha of the tint. 0 disables it outright and creates nothing.</summary>
        internal static float WantTint;

        private Image _tint;

        private void Update()
        {
            bool want = !Core.Diag.Bypass
                     && WantTint > 0.001f
                     && CameraStateManager.cameraMode == CameraMode.chase;

            if (!want)
            {
                // Disable rather than destroy: switching view is common, and rebuilding the quad on
                // every switch would churn the canvas for no gain. Fake-null guarded because a scene
                // change destroys it underneath us.
                if (_tint != null) _tint.enabled = false;
                return;
            }

            if (!EnsureTint()) return;

            _tint.enabled = true;
            _tint.color = new Color(0f, 0f, 0f, Mathf.Clamp01(WantTint));
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

            // Stretch to the full canvas so it covers the screen at any resolution without needing to
            // track Screen.width/height the way the readout anchor does.
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            rt.localScale = Vector3.one;

            _tint = go.GetComponent<Image>();
            // A full-screen quad that swallowed clicks would break every UI beneath it.
            _tint.raycastTarget = false;

            // Behind every other HUD element, in front of the world.
            rt.SetAsFirstSibling();

            Plugin.Log.LogInfo("[ChaseCamera] HUD tint created");
            return true;
        }

        private void OnDestroy()
        {
            if (_tint != null) Destroy(_tint.gameObject);
            _tint = null;
        }
    }
}
