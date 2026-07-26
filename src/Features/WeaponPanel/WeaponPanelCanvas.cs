using System;
using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ChaseView.Features
{
    /// <summary>
    /// Draws the readout. Its own ScreenSpaceOverlay canvas anchored bottom-right, mirroring the
    /// vanilla UI's scale factor so it stays in proportion at any resolution (the same trap that had
    /// the scoreboard rendering 25% undersized at 1440p - see #scoreboard-scale).
    ///
    /// Rows are built once per LOADOUT change and only their text is refreshed per tick. Rebuilding
    /// every frame would churn TMP meshes for no reason, and destroying rows mid-frame is how the
    /// scoreboard's tooltip used to get stranded.
    /// </summary>
    internal sealed class WeaponPanelCanvas : MonoBehaviour
    {
        private const float RowH = 26f;
        // Columns. The name column's width IS the gap: the value is right-aligned against the panel
        // edge, so a wide name column strands the ammo far off to the right.
        private const float MarkerW = 20f;
        private const float NameW = 132f;
        private const float ValueW = 58f;
        private const float PanelW = MarkerW + NameW + ValueW;
        // Sits clear of the aircraft diagram, which occupies the same corner.
        private static readonly Vector2 PanelOffset = new Vector2(-14f, 250f);
        // Unselected sits well back so the selected row is unmistakable at a glance, but stays
        // bright enough to read a count off without switching to it.
        private static readonly Color Ink = new Color(0.26f, 0.58f, 0.32f);     // de-selected: dim phosphor
        private static readonly Color Sel = new Color(0.62f, 1f, 0.70f);        // selected: pops
        private static readonly Color Empty = new Color(1f, 0.16f, 0.13f);      // "out" - full red, not coral
        private static readonly Color Dim = new Color(0.45f, 1f, 0.55f, 0.55f);

        private sealed class Row
        {
            public TextMeshProUGUI Marker, Label, Value;
            public GameObject Go;
        }

        private GameObject _root;
        private RectTransform _panel;
        private CanvasScaler _scaler;
        private TMP_FontAsset _font;
        private readonly List<Row> _rows = new List<Row>();
        private TextMeshProUGUI _dmgLabel, _dmgValue;

        private Aircraft _aircraft;
        private Transform _vanillaWeapon, _vanillaCm;   // cached; FindDeep is a whole-tree walk
        private Image _vanillaPlate;                    // TopRightPanel's own backing image
        private bool _lastHideValue;
        private bool _hideApplied;
        private string _signature = "";        // what the row STRUCTURE was built for
        private float _nextTick;
        // Sentinel, NOT 1f. #scale-sentinel: the vanilla canvas scale is exactly 1.0 at 1920x1080,
        // so an initial value of 1f made the very first SyncScale early-out as "unchanged" and the
        // panel's anchoredPosition was never applied - it sat jammed in the screen corner. Worked at
        // 1440p purely because the scale there is 1.333.
        private float _scale = -1f;

        private void Update()
        {
            if (Core.Diag.Bypass) return;

            Aircraft ac = ResolveAircraft();

            // #diagram-resurrect
            //   The recolour runs as a POSTFIX on StatusDisplay.Update, and vanilla's Update sets
            //   base.enabled = false once its 10s fade expires. A postfix on a method that no longer
            //   runs never runs either - so once the diagram had faded, switching
            //   ShowDamageDiagram back on did nothing at all, permanently. It only ever appeared
            //   to work because the option used to default ON and caught the component while alive.
            //
            //   Re-enabling has to happen from OUTSIDE that method. Switching the option off is
            //   self-healing: stop topping the timer up and vanilla's own countdown resumes, fades the
            //   diagram out and disables it exactly as it would have.
            if (ac != null && WeaponPanel.ShowDiagram.Value)
            {
                var sd = ac.statusDisplay;
                if (sd != null && !sd.enabled) sd.enabled = true;
            }
            else if (!WeaponPanel.ShowDiagram.Value)
            {
                // Same reason as the resurrect above: with both options off, StatusDisplay.Update may
                // already have disabled itself, and a postfix on a dead method cannot undo anything.
                WeaponPanel.Hooks.RestoreOriginals();
            }

            // #independent-toggles
            //   Hiding the stock panel and showing ours are SEPARATE options - the whole point is that
            //   you can run either, both or neither. This call used to sit below the ShowWeaponList
            //   gate, so with the weapon list off Update returned before ever reaching it and
            //   HideVanillaWeaponPanel did nothing until you toggled the other setting. Two
            //   independent options, one of them silently dependent on the other.
            //
            //   Gated on an aircraft existing so the menu does not pay for a hierarchy search that
            //   cannot find anything; HideVanillaIfAsked then early-outs on a cached reference.
            if (ac != null) HideVanillaIfAsked();

            bool wanted = WeaponPanel.ShowPanel.Value;

            if (!wanted || ac == null || !HudVisible())
            {
                if (_root != null && _root.activeSelf) _root.SetActive(false);
                _aircraft = null;
                return;
            }

            EnsureRoot();
            if (_root == null) return;
            if (!_root.activeSelf) _root.SetActive(true);

            SyncScale();

            // #perf-throttle
            //   Everything below used to run EVERY frame, including Signature() - which builds a
            //   string from the whole station list and allocates it, once per frame, forever, just to
            //   compare it. A loadout changes on rearm, not between frames, so both the structural
            //   check and the text refresh belong behind the same 10Hz tick.
            if (Time.unscaledTime < _nextTick) return;
            _nextTick = Time.unscaledTime + 0.1f;

            string sig = Signature(ac);
            if (ac != _aircraft || sig != _signature)
            {
                _aircraft = ac;
                _signature = sig;
                BuildRows(ac);
            }

            Refresh(ac);
        }

        /// <summary>
        /// The local player's aircraft. CombatHUD.aircraft is what the game's own HUD elements key on,
        /// so using it means this panel appears and disappears exactly when the rest of the HUD does.
        /// </summary>
        private static Aircraft ResolveAircraft()
        {
            var hud = SceneSingleton<CombatHUD>.i;
            return hud != null ? hud.aircraft : null;
        }

        /// <summary>
        /// Follow the flight HUD's own visibility rather than deciding for ourselves, so pausing, the
        /// map, the settings menu and every camera mode are all handled by whatever already handles
        /// them. Chase view included, because ChaseCamera turns that canvas on.
        /// </summary>
        private static bool HudVisible()
        {
            var fh = SceneSingleton<FlightHud>.i;
            return fh != null && fh.canvas != null && fh.canvas.gameObject.activeInHierarchy;
        }

        private static string Signature(Aircraft ac)
        {
            var sb = new StringBuilder();
            var st = ac.weaponStations;
            if (st != null)
                for (int i = 0; i < st.Count; i++)
                    sb.Append(st[i]?.WeaponInfo != null ? st[i].WeaponInfo.name : "?").Append('|');

            var cm = ac.countermeasureManager;
            if (cm?.countermeasureStations != null) sb.Append('#').Append(cm.countermeasureStations.Count);
            return sb.ToString();
        }

        private void EnsureRoot()
        {
            if (_root != null) return;

            HarvestFont();

            _root = new GameObject("ChaseView_WeaponPanel", typeof(RectTransform), typeof(Canvas),
                                   typeof(CanvasScaler));
            DontDestroyOnLoad(_root);
            var cv = _root.GetComponent<Canvas>();
            cv.renderMode = RenderMode.ScreenSpaceOverlay;
            cv.sortingOrder = 300;                 // above the HUD, below the pause scoreboard (800)
            _scaler = _root.GetComponent<CanvasScaler>();
            _scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;

            var pgo = new GameObject("Panel", typeof(RectTransform));
            pgo.transform.SetParent(_root.transform, false);
            _panel = (RectTransform)pgo.transform;
            _panel.anchorMin = _panel.anchorMax = new Vector2(1f, 0f);   // bottom-right
            _panel.pivot = new Vector2(1f, 0f);
            _panel.sizeDelta = new Vector2(PanelW, 200f);
            // Also set here, so placement never depends on SyncScale happening to detect a change.
            _panel.anchoredPosition = PanelOffset;

            Plugin.Log.LogInfo("[WeaponPanel] canvas up");
        }

        /// <summary>Mirror the game's UI scale so the panel holds its proportions at any resolution.</summary>
        private void SyncScale()
        {
            float want = 1f;
            var fh = SceneSingleton<FlightHud>.i;
            if (fh != null && fh.canvas != null && fh.canvas.scaleFactor > 0.01f)
                want = fh.canvas.scaleFactor;
            want *= Mathf.Clamp(WeaponPanel.PanelScale.Value, 0.5f, 2f);

            if (Mathf.Approximately(want, _scale)) return;
            _scale = want;
            _scaler.scaleFactor = want;

            _panel.anchoredPosition = PanelOffset;
            Plugin.Log.LogInfo($"[WeaponPanel] scale {want:0.###} @ {Screen.width}x{Screen.height}");
        }

        /// <summary>
        /// Hide vanilla's weapon and countermeasure blocks, leaving the capacitor bar. Found by name
        /// under the flight HUD rather than cached: the panel is part of the per-airframe HUDExtras
        /// prefab, so it is destroyed and rebuilt on every aircraft change.
        /// </summary>
        /// <summary>
        /// #perf-treewalk
        ///   FindDeep is a full recursive walk of the flight HUD canvas, and the probe dump showed
        ///   that tree running to hundreds of nodes. This ran it TWICE PER FRAME - and with the
        ///   default HideVanillaWeaponPanel=false it walked the whole tree only to conclude that two
        ///   already-active objects should stay active. Now the transforms are resolved once and
        ///   re-resolved only when they go null, which is exactly when the per-airframe HUDExtras
        ///   prefab holding them is destroyed on an aircraft change.
        /// </summary>
        private void HideVanillaIfAsked()
        {
            bool hide = WeaponPanel.HideVanillaPanel.Value;

            // Unity's == catches the destroyed-but-not-null case, which is how an aircraft change
            // presents here.
            bool stale = _vanillaWeapon == null || _vanillaCm == null || _vanillaPlate == null;
            if (!stale && hide == _lastHideValue && _hideApplied) return;

            if (stale)
            {
                var fh = SceneSingleton<FlightHud>.i;
                if (fh == null || fh.canvas == null) return;
                _vanillaWeapon = FindDeep(fh.canvas.transform, "weaponPanel");
                _vanillaCm = FindDeep(fh.canvas.transform, "countermeasuresBackground");

                // The dark plate behind them is TopRightPanel's OWN Image (sprite 'weaponsPanel'),
                // not a child - so hiding the two children left the backing rectangle floating there.
                // Disable the COMPONENT, never the GameObject: PowerPanel (the energy capacitor bar)
                // is a child of it and has no other home.
                Transform plate = FindDeep(fh.canvas.transform, "TopRightPanel");
                _vanillaPlate = plate != null ? plate.GetComponent<Image>() : null;

                if (_vanillaWeapon == null && _vanillaCm == null && _vanillaPlate == null) return;
            }

            Apply(_vanillaWeapon, !hide);
            Apply(_vanillaCm, !hide);
            if (_vanillaPlate != null && _vanillaPlate.enabled == hide) _vanillaPlate.enabled = !hide;
            _lastHideValue = hide;
            _hideApplied = true;
        }

        private static void Apply(Transform t, bool active)
        {
            if (t != null && t.gameObject.activeSelf != active) t.gameObject.SetActive(active);
        }

        private static Transform FindDeep(Transform t, string name)
        {
            if (t.name == name) return t;
            for (int i = 0; i < t.childCount; i++)
            {
                var r = FindDeep(t.GetChild(i), name);
                if (r != null) return r;
            }
            return null;
        }

        private void BuildRows(Aircraft ac)
        {
            foreach (var r in _rows) if (r.Go != null) Destroy(r.Go);
            _rows.Clear();
            _dmgLabel = _dmgValue = null;

            float y = 0f;
            var stations = ac.weaponStations;
            if (stations != null)
                for (int i = 0; i < stations.Count; i++)
                {
                    var s = stations[i];
                    if (s?.WeaponInfo == null) continue;
                    if (s.Cargo && !WeaponPanel.ShowCargo.Value) continue;
                    if (s.WeaponInfo.hideInDisplay) continue;
                    _rows.Add(NewRow(y)); y -= RowH;
                }

            var cm = ac.countermeasureManager;
            if (cm?.countermeasureStations != null)
                for (int i = 0; i < cm.countermeasureStations.Count; i++) { _rows.Add(NewRow(y)); y -= RowH; }

            if (WeaponPanel.ShowDamagePercent.Value)
            {
                var row = NewRow(y); y -= RowH;
                _dmgLabel = row.Label; _dmgValue = row.Value;
                _rows.Add(row);
            }

            _panel.sizeDelta = new Vector2(PanelW, Mathf.Abs(y));
        }

        private Row NewRow(float y)
        {
            var go = new GameObject("row", typeof(RectTransform));
            go.transform.SetParent(_panel, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = new Vector2(0f, 1f); rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.anchoredPosition = new Vector2(0f, y);
            rt.sizeDelta = new Vector2(0f, RowH);

            return new Row
            {
                Go = go,
                Marker = NewText(rt, 0f, MarkerW, TextAlignmentOptions.Left),
                Label = NewText(rt, MarkerW, NameW, TextAlignmentOptions.Right),
                Value = NewText(rt, MarkerW + NameW, ValueW, TextAlignmentOptions.Right),
            };
        }

        private TextMeshProUGUI NewText(RectTransform parent, float x, float w, TextAlignmentOptions align)
        {
            var go = new GameObject("t", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = new Vector2(0f, 0f); rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 0.5f);
            rt.anchoredPosition = new Vector2(x, 0f);
            rt.sizeDelta = new Vector2(w, 0f);

            var t = go.AddComponent<TextMeshProUGUI>();
            if (_font != null) t.font = _font;
            t.fontSize = 19f;
            t.alignment = align;
            t.enableWordWrapping = false;      // a too-narrow label must clip, never reflow
            t.overflowMode = TextOverflowModes.Overflow;
            t.color = Ink;
            return t;
        }

        private void Refresh(Aircraft ac)
        {
            int idx = 0;
            var wm = ac.weaponManager;
            var current = wm != null ? wm.currentWeaponStation : null;

            var stations = ac.weaponStations;
            if (stations != null)
                for (int i = 0; i < stations.Count && idx < _rows.Count; i++)
                {
                    var s = stations[i];
                    if (s?.WeaponInfo == null) continue;
                    if (s.Cargo && !WeaponPanel.ShowCargo.Value) continue;
                    if (s.WeaponInfo.hideInDisplay) continue;

                    var row = _rows[idx++];
                    bool sel = current != null && ReferenceEquals(s, current);
                    bool outOfAmmo = s.Ammo <= 0;

                    row.Marker.text = sel ? ">" : "";
                    // shortName is the code ("IRM-S2"); fall back to the full name rather than showing
                    // an empty row if an airframe or mod leaves it blank.
                    row.Label.text = !string.IsNullOrEmpty(s.WeaponInfo.shortName)
                        ? s.WeaponInfo.shortName : s.WeaponInfo.weaponName;
                    row.Value.text = s.Reloading ? "..." : s.Ammo.ToString();

                    Color c = outOfAmmo ? Empty : (sel ? Sel : Ink);
                    row.Marker.color = c; row.Label.color = c; row.Value.color = c;
                }

            var cm = ac.countermeasureManager;
            if (cm?.countermeasureStations != null)
                for (int i = 0; i < cm.countermeasureStations.Count && idx < _rows.Count; i++)
                {
                    var st = cm.countermeasureStations[i];
                    var row = _rows[idx++];
                    bool sel = cm.activeIndex == i;
                    bool outOfAmmo = st.ammo <= 0;

                    row.Marker.text = sel ? ">" : "";
                    row.Label.text = ShortCm(st.displayName);
                    row.Value.text = st.ammo.ToString();

                    Color c = outOfAmmo ? Empty : (sel ? Sel : Ink);
                    row.Marker.color = c; row.Label.color = c; row.Value.color = c;
                }

            if (_dmgLabel != null && idx < _rows.Count)
            {
                idx++;
                float dmg = DamagePercent(ac);
                _dmgLabel.text = "DMG";
                _dmgValue.text = $"{dmg:0}%";
                Color c = dmg >= 60f ? Empty : dmg >= 25f ? Color.Lerp(Ink, Empty, 0.6f) : Dim;
                _dmgLabel.color = c; _dmgValue.color = c;
            }
        }

        /// <summary>
        /// Averaged over the diagram's own tracked parts, so the number and the silhouette always
        /// agree. Returns 0 when no diagram exists - some airframes ship no StatusDisplay prefab.
        /// </summary>
        private static float DamagePercent(Aircraft ac)
        {
            var sd = ac.statusDisplay;
            if (sd == null || sd.statusDisplays == null || sd.statusDisplays.Count == 0) return 0f;

            float total = 0f; int n = 0;
            foreach (var p in sd.statusDisplays)
            {
                if (p == null) continue;
                total += Mathf.Clamp01(p.displayCondition);
                n++;
            }
            return n == 0 ? 0f : (1f - total / n) * 100f;
        }

        /// <summary>"IR Flares" -> "FLR", "Radar Chaff" -> "CHF"; anything else is truncated.</summary>
        private static string ShortCm(string name)
        {
            if (string.IsNullOrEmpty(name)) return "CM";
            string n = name.ToUpperInvariant();
            if (n.Contains("FLARE")) return "FLR";
            if (n.Contains("CHAFF")) return "CHF";
            if (n.Contains("SMOKE")) return "SMK";
            return n.Length <= 6 ? n : n.Substring(0, 6);
        }

        /// <summary>Reuse the game's own font so the panel matches the rest of the HUD.</summary>
        private void HarvestFont()
        {
            if (_font != null) return;
            var fh = SceneSingleton<FlightHud>.i;
            if (fh == null) return;
            var any = fh.GetComponentInChildren<TextMeshProUGUI>(true);
            if (any != null) _font = any.font;
        }

        private void OnDestroy() { if (_root != null) Destroy(_root); }
    }
}
