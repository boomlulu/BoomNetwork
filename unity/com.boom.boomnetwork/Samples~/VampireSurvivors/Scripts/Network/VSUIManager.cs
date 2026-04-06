// BoomNetwork VampireSurvivors Demo — UGUI Manager
//
// All UI is created at runtime via VSUIManager.Create() — no prefabs, no scene assets.
// Canvas uses ScreenSpaceOverlay + ScaleWithScreenSize (1920×1080, match=0.5)
// with sortingOrder=10 so the virtual joystick canvas (order=100) stays on top.
//
// Public API used by VSNetworkManager:
//   Create()             — factory, call once in Start()
//   SetVisible(bool)     — show/hide during sync
//   UpdateHUD(sim, slot, rtt) — call every OnFrame
//   ShowDesync(frame)    — call on desync detected
//   OnUpgradeSelected    — event fired when a weapon upgrade button is tapped/clicked

using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace BoomNetwork.Samples.VampireSurvivors
{
    public sealed class VSUIManager : MonoBehaviour
    {
        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>Fired when the player taps an upgrade button. Byte is a weapon bitmask (1<<slot).</summary>
        public event Action<byte> OnUpgradeSelected;

        /// <summary>Factory — call once in Start(). Creates Canvas + all child panels.</summary>
        public static VSUIManager Create()
        {
            var go = new GameObject("[VS] UIManager");
            DontDestroyOnLoad(go);

            var canvas = go.AddComponent<Canvas>();
            canvas.renderMode  = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 10; // joystick canvas uses 100 — stays on top

            var scaler = go.AddComponent<CanvasScaler>();
            scaler.uiScaleMode         = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight  = 0.5f;

            go.AddComponent<GraphicRaycaster>();

            // EventSystem — create only if none exists (VSVirtualJoystick may create one too)
            if (FindObjectOfType<EventSystem>() == null)
            {
                var evGo = new GameObject("[VS] EventSystem");
                evGo.AddComponent<EventSystem>();
                evGo.AddComponent<StandaloneInputModule>();
            }

            var ui = go.AddComponent<VSUIManager>();
            ui._canvas = canvas;
            ui.Build();
            return ui;
        }

        /// <summary>Show/hide all VS UI (hidden before sync starts).</summary>
        public void SetVisible(bool visible) => _root.SetActive(visible);

        /// <summary>
        /// Update all panels every frame.
        /// Call from OnFrame (after Tick) with current sim + localSlot + RTT.
        /// </summary>
        public void UpdateHUD(VSSimulation sim, int localSlot, int rttMs)
        {
            var state = sim.State;

            int aliveEnemies = 0;
            for (int i = 0; i < GameState.MaxEnemies; i++)
                if (state.Enemies[i].IsAlive) aliveEnemies++;

            _hudTitle.text = $"Vampire Survivors  F:{state.FrameNumber}  RTT:{rttMs}ms";
            _hudWave.text  = $"Wave {state.WaveNumber}  Enemies: {aliveEnemies}/{GameState.MaxEnemies}";

            for (int i = 0; i < GameState.MaxPlayers; i++)
            {
                ref var p = ref state.Players[i];
                if (!p.IsActive) { _playerRows[i].SetActive(false); continue; }
                _playerRows[i].SetActive(true);

                string star = (i == localSlot) ? " ★" : "";
                _playerNameLabels[i].text = p.IsAlive
                    ? $"P{i + 1}{star}"
                    : $"<color=#ff4444>P{i + 1}{star} DEAD</color>";

                float hpFill = p.MaxHp > 0 ? Mathf.Clamp01((float)p.Hp / p.MaxHp) : 0f;
                _hpBars[i].fillAmount = hpFill;
                _hpTexts[i].text = $"HP {p.Hp}/{p.MaxHp}";

                float xpFill = p.XpToNextLevel > 0 ? Mathf.Clamp01((float)p.Xp / p.XpToNextLevel) : 0f;
                _xpBars[i].fillAmount = xpFill;
                _xpTexts[i].text = $"Lv{p.Level}  K:{p.KillCount}";

                _weaponLabels[i].text = BuildWeaponString(ref p);
            }

            // ── Upgrade panel ──
            bool showUpgrade = localSlot >= 0 && localSlot < GameState.MaxPlayers
                && state.Players[localSlot].PendingLevelUp;
            _upgradePanel.SetActive(showUpgrade);
            if (showUpgrade)
                RefreshUpgradeButtons(ref state.Players[localSlot]);

            // ── Pause overlay (another player is choosing) ──
            int upgradingSlot = -1;
            for (int i = 0; i < GameState.MaxPlayers; i++)
                if (state.Players[i].IsActive && state.Players[i].PendingLevelUp)
                { upgradingSlot = i; break; }
            bool showPause = upgradingSlot >= 0 && upgradingSlot != localSlot;
            _pauseOverlay.SetActive(showPause);
            if (showPause)
                _pauseLabel.text = $"PAUSED\nP{upgradingSlot + 1} is choosing an upgrade...";
        }

        /// <summary>Show desync error overlay. Permanent — no hide path needed.</summary>
        public void ShowDesync(uint frame)
        {
            _desyncOverlay.SetActive(true);
            _desyncLabel.text = $"DESYNC DETECTED\nFrame {frame} \u2014 State hashes differ. Game paused.";
        }

        // ── Fields ────────────────────────────────────────────────────────────

        static readonly string[] WeaponNames = {
            "", "Knife", "Orb", "Lightning", "Holy Water",
            "Link Beam", "Heal Aura", "Shield Wall", "Chain Lightning+", "Focus Fire",
            "Revival Totem", "Frost Nova", "Fire Trail", "Magnet Field", "Split Shot"
        };
        static readonly string[] WeaponIcons = {
            "", "\ud83d\udde1", "\ud83d\udd2e", "\u26a1", "\ud83d\udca7",
            "\ud83d\udd17", "\ud83d\udc9a", "\ud83d\udee1", "\u26a1\u26a1", "\ud83c\udfaf",
            "\ud83e\uddf9", "\u2744", "\ud83d\udd25", "\ud83e\uddf2", "\u2194"
        };

        Canvas      _canvas;
        GameObject  _root;

        // HUD — top-left panel
        Text        _hudTitle, _hudWave;
        GameObject[] _playerRows       = new GameObject[GameState.MaxPlayers];
        Text[]       _playerNameLabels = new Text[GameState.MaxPlayers];
        Image[]      _hpBars           = new Image[GameState.MaxPlayers];
        Text[]       _hpTexts          = new Text[GameState.MaxPlayers];
        Image[]      _xpBars           = new Image[GameState.MaxPlayers];
        Text[]       _xpTexts          = new Text[GameState.MaxPlayers];
        Text[]       _weaponLabels     = new Text[GameState.MaxPlayers];

        // Overlays
        GameObject _desyncOverlay;
        Text       _desyncLabel;
        GameObject _pauseOverlay;
        Text       _pauseLabel;

        // Upgrade panel
        GameObject _upgradePanel;
        Text[]     _upgradeBtnLabels = new Text[4];

        // ── Build ─────────────────────────────────────────────────────────────

        void Build()
        {
            // Root stretches to fill entire canvas
            _root = new GameObject("Root");
            _root.transform.SetParent(transform, false);
            var rootRt = _root.AddComponent<RectTransform>();
            rootRt.anchorMin = Vector2.zero;
            rootRt.anchorMax = Vector2.one;
            rootRt.offsetMin = Vector2.zero;
            rootRt.offsetMax = Vector2.zero;

            BuildHUD();
            BuildDesyncOverlay();
            BuildPauseOverlay();
            BuildUpgradePanel();

            _root.SetActive(false); // hidden until sync starts
        }

        void BuildHUD()
        {
            // Top-left panel with VerticalLayoutGroup + ContentSizeFitter so
            // it auto-grows to fit however many players are active.
            var panel = MakePanel("HUD", _root.transform,
                anchorMin: new Vector2(0f, 1f), anchorMax: new Vector2(0f, 1f),
                pivot: new Vector2(0f, 1f),
                sizeDelta: new Vector2(444f, 100f), // height overridden by ContentSizeFitter
                anchoredPos: new Vector2(10f, -10f));

            var vlg = panel.AddComponent<VerticalLayoutGroup>();
            vlg.padding              = new RectOffset(8, 8, 6, 8);
            vlg.spacing              = 3f;
            vlg.childAlignment       = TextAnchor.UpperLeft;
            vlg.childForceExpandWidth  = true;
            vlg.childForceExpandHeight = false;

            var csf = panel.AddComponent<ContentSizeFitter>();
            csf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            _hudTitle = MakeText("Title", panel.transform, 20, FontStyle.Bold, Color.white);
            _hudTitle.supportRichText = false; // plain text, no rich markup needed
            LE(_hudTitle.gameObject, 428f, 26f);

            _hudWave = MakeText("Wave", panel.transform, 17, FontStyle.Normal, new Color(0.8f, 0.8f, 0.8f));
            LE(_hudWave.gameObject, 428f, 22f);

            for (int i = 0; i < GameState.MaxPlayers; i++)
                _playerRows[i] = BuildPlayerRow(panel.transform, i);

            var footer = MakeText("Footer", panel.transform, 13, FontStyle.Normal, new Color(0.45f, 0.45f, 0.45f));
            footer.text = "0 extra bandwidth (pure FrameSync)";
            LE(footer.gameObject, 428f, 17f);
        }

        GameObject BuildPlayerRow(Transform parent, int idx)
        {
            var row = new GameObject($"P{idx + 1}Row");
            row.transform.SetParent(parent, false);
            var vlg = row.AddComponent<VerticalLayoutGroup>();
            vlg.padding              = new RectOffset(0, 0, 2, 2);
            vlg.spacing              = 2f;
            vlg.childForceExpandWidth  = true;
            vlg.childForceExpandHeight = false;
            LE(row, 428f, 68f);

            // ── Row 1: name + weapons ──
            var nameRow = MakeHRow("NameRow", row.transform, 428f, 22f);
            _playerNameLabels[idx] = MakeText("Name", nameRow.transform, 17, FontStyle.Bold, Color.white);
            LE(_playerNameLabels[idx].gameObject, 110f, 22f);
            _weaponLabels[idx] = MakeText("Weapons", nameRow.transform, 14, FontStyle.Normal, new Color(1f, 0.9f, 0.5f));
            LE(_weaponLabels[idx].gameObject, 290f, 22f);

            // ── Row 2: HP ──
            var hpRow = MakeHRow("HpRow", row.transform, 428f, 18f);
            _hpTexts[idx] = MakeText("HpTxt", hpRow.transform, 13, FontStyle.Normal, new Color(0.5f, 1f, 0.5f));
            LE(_hpTexts[idx].gameObject, 90f, 18f);
            _hpBars[idx] = MakeBarFill(MakeBarTrack("HpTrack", hpRow.transform, 300f, 14f).transform,
                new Color(0.2f, 0.85f, 0.2f));

            // ── Row 3: XP ──
            var xpRow = MakeHRow("XpRow", row.transform, 428f, 18f);
            _xpTexts[idx] = MakeText("XpTxt", xpRow.transform, 13, FontStyle.Normal, new Color(0.5f, 0.7f, 1f));
            LE(_xpTexts[idx].gameObject, 90f, 18f);
            _xpBars[idx] = MakeBarFill(MakeBarTrack("XpTrack", xpRow.transform, 300f, 14f).transform,
                new Color(0.15f, 0.4f, 1f));

            return row;
        }

        void BuildDesyncOverlay()
        {
            _desyncOverlay = MakePanel("DesyncOverlay", _root.transform,
                anchorMin: new Vector2(0.5f, 0.5f), anchorMax: new Vector2(0.5f, 0.5f),
                pivot: new Vector2(0.5f, 0.5f),
                sizeDelta: new Vector2(580f, 96f), anchoredPos: new Vector2(0f, 100f));

            _desyncLabel = MakeText("Label", _desyncOverlay.transform, 22, FontStyle.Bold, new Color(1f, 0.3f, 0.3f));
            _desyncLabel.alignment = TextAnchor.MiddleCenter;
            Stretch(_desyncLabel.rectTransform);

            _desyncOverlay.SetActive(false);
        }

        void BuildPauseOverlay()
        {
            _pauseOverlay = MakePanel("PauseOverlay", _root.transform,
                anchorMin: new Vector2(0.5f, 0.5f), anchorMax: new Vector2(0.5f, 0.5f),
                pivot: new Vector2(0.5f, 0.5f),
                sizeDelta: new Vector2(460f, 80f), anchoredPos: new Vector2(0f, 60f));

            _pauseLabel = MakeText("Label", _pauseOverlay.transform, 22, FontStyle.Bold, new Color(1f, 1f, 0.3f));
            _pauseLabel.alignment = TextAnchor.MiddleCenter;
            Stretch(_pauseLabel.rectTransform);

            _pauseOverlay.SetActive(false);
        }

        void BuildUpgradePanel()
        {
            // 480 wide × (12 + 40 + 12 + 4×(80+10) + 12) = 12+40+12+360+12 = 436 → round to 440
            _upgradePanel = MakePanel("UpgradePanel", _root.transform,
                anchorMin: new Vector2(0.5f, 0.5f), anchorMax: new Vector2(0.5f, 0.5f),
                pivot: new Vector2(0.5f, 0.5f),
                sizeDelta: new Vector2(480f, 440f), anchoredPos: Vector2.zero);

            var vlg = _upgradePanel.AddComponent<VerticalLayoutGroup>();
            vlg.padding              = new RectOffset(12, 12, 12, 12);
            vlg.spacing              = 10f;
            vlg.childForceExpandWidth  = true;
            vlg.childForceExpandHeight = false;

            var title = MakeText("Title", _upgradePanel.transform, 22, FontStyle.Bold, new Color(1f, 0.9f, 0.2f));
            title.alignment = TextAnchor.MiddleCenter;
            title.text = "LEVEL UP!  Choose upgrade:";
            LE(title.gameObject, 456f, 40f);

            for (int i = 0; i < 4; i++)
            {
                int captured = i;
                var btn = MakeButton($"Btn{i}", _upgradePanel.transform, 80f,
                    () => OnUpgradeSelected?.Invoke((byte)(1 << captured)));
                _upgradeBtnLabels[i] = btn.GetComponentInChildren<Text>();
            }

            _upgradePanel.SetActive(false);
        }

        void RefreshUpgradeButtons(ref PlayerState player)
        {
            for (int i = 0; i < 4; i++)
            {
                var wt = (WeaponType)player.GetUpgradeOpt(i);
                if (wt == WeaponType.None) { _upgradeBtnLabels[i].text = $"[{i + 1}] —"; continue; }
                int slot = player.FindWeaponSlot(wt);
                int wtIdx = (int)wt;
                string icon = wtIdx < WeaponIcons.Length ? WeaponIcons[wtIdx] : "?";
                string name = wtIdx < WeaponNames.Length ? WeaponNames[wtIdx] : wt.ToString();
                _upgradeBtnLabels[i].text = slot >= 0
                    ? $"[{i + 1}] {icon} {name} Lv{player.GetWeapon(slot).Level} \u2192 Lv{player.GetWeapon(slot).Level + 1}"
                    : $"[{i + 1}] {icon} {name} (NEW)";
            }
        }

        static string BuildWeaponString(ref PlayerState p)
        {
            string s = "";
            for (int i = 0; i < PlayerState.MaxWeaponSlots; i++)
            {
                var w = p.GetWeapon(i);
                if (w.Type == WeaponType.None) continue;
                if (s.Length > 0) s += " ";
                s += $"{WeaponIcons[(int)w.Type]}{w.Level}";
            }
            return s;
        }

        // ── UGUI Helpers ──────────────────────────────────────────────────────

        // Lazy-loaded shared resources (created once, reused across all elements)
        static Font       _builtinFont;
        static Texture2D  _darkTex;
        static Texture2D  _whiteTex;
        static Texture2D  _darkBarTex;

        static Font BuiltinFont =>
            _builtinFont ??= Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

        static Sprite DarkSprite()
        {
            if (_darkTex == null)
            {
                _darkTex = new Texture2D(1, 1, TextureFormat.ARGB32, false);
                _darkTex.SetPixel(0, 0, new Color(0f, 0f, 0f, 0.78f));
                _darkTex.Apply();
            }
            return Sprite.Create(_darkTex, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f));
        }

        static Sprite WhiteSprite()
        {
            if (_whiteTex == null)
            {
                _whiteTex = new Texture2D(1, 1, TextureFormat.ARGB32, false);
                _whiteTex.SetPixel(0, 0, Color.white);
                _whiteTex.Apply();
            }
            return Sprite.Create(_whiteTex, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f));
        }

        static Sprite DarkBarSprite()
        {
            if (_darkBarTex == null)
            {
                _darkBarTex = new Texture2D(1, 1, TextureFormat.ARGB32, false);
                _darkBarTex.SetPixel(0, 0, new Color(0.12f, 0.12f, 0.12f, 1f));
                _darkBarTex.Apply();
            }
            return Sprite.Create(_darkBarTex, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f));
        }

        /// <summary>Panel with dark semi-transparent background.</summary>
        static GameObject MakePanel(string name, Transform parent,
            Vector2 anchorMin, Vector2 anchorMax, Vector2 pivot,
            Vector2 sizeDelta, Vector2 anchoredPos)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var rt       = go.AddComponent<RectTransform>();
            rt.anchorMin        = anchorMin;
            rt.anchorMax        = anchorMax;
            rt.pivot            = pivot;
            rt.sizeDelta        = sizeDelta;
            rt.anchoredPosition = anchoredPos;
            var img = go.AddComponent<Image>();
            img.sprite        = DarkSprite();
            img.raycastTarget = false;
            return go;
        }

        /// <summary>Text with builtin Arial font. No RectTransform manipulation — parent controls it.</summary>
        static Text MakeText(string name, Transform parent, int fontSize, FontStyle style, Color color)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.AddComponent<RectTransform>();
            var t = go.AddComponent<Text>();
            t.font          = BuiltinFont;
            t.fontSize      = fontSize;
            t.fontStyle     = style;
            t.color         = color;
            t.raycastTarget = false;
            t.supportRichText = true;
            return t;
        }

        /// <summary>Horizontal layout row with fixed preferred dimensions.</summary>
        static GameObject MakeHRow(string name, Transform parent, float w, float h)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.AddComponent<RectTransform>();
            var hlg = go.AddComponent<HorizontalLayoutGroup>();
            hlg.spacing              = 6f;
            hlg.childAlignment       = TextAnchor.MiddleLeft;
            hlg.childForceExpandWidth  = false;
            hlg.childForceExpandHeight = false;
            LE(go, w, h);
            return go;
        }

        /// <summary>Dark bar track (background). Returns the GameObject so Fill can be parented to it.</summary>
        static GameObject MakeBarTrack(string name, Transform parent, float width, float height)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var rt = go.AddComponent<RectTransform>();
            rt.sizeDelta = new Vector2(width, height);
            var img = go.AddComponent<Image>();
            img.sprite        = DarkBarSprite();
            img.raycastTarget = false;
            LE(go, width, height);
            return go;
        }

        /// <summary>Colored fill image inside a bar track. Set fillAmount to update the bar.</summary>
        static Image MakeBarFill(Transform trackParent, Color color)
        {
            var go = new GameObject("Fill");
            go.transform.SetParent(trackParent, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            var img = go.AddComponent<Image>();
            img.sprite     = WhiteSprite();
            img.color      = color;
            img.type       = Image.Type.Filled;
            img.fillMethod = Image.FillMethod.Horizontal;
            img.fillAmount = 1f;
            img.raycastTarget = false;
            return img;
        }

        /// <summary>Styled button with a centered text label. Height is LayoutElement preferred height.</summary>
        static GameObject MakeButton(string name, Transform parent, float height, Action onClick)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.AddComponent<RectTransform>();

            var img = go.AddComponent<Image>();
            img.sprite = WhiteSprite();
            img.color  = new Color(0.18f, 0.18f, 0.32f, 1f);

            var btn = go.AddComponent<Button>();
            var cs  = btn.colors;
            cs.normalColor      = new Color(0.18f, 0.18f, 0.32f, 1f);
            cs.highlightedColor = new Color(0.28f, 0.28f, 0.48f, 1f);
            cs.pressedColor     = new Color(0.10f, 0.10f, 0.20f, 1f);
            btn.colors = cs;
            btn.onClick.AddListener(() => onClick?.Invoke());

            LE(go, 456f, height);

            var labelGo = new GameObject("Label");
            labelGo.transform.SetParent(go.transform, false);
            var labelRt = labelGo.AddComponent<RectTransform>();
            labelRt.anchorMin = Vector2.zero;
            labelRt.anchorMax = Vector2.one;
            labelRt.offsetMin = new Vector2(8f, 4f);
            labelRt.offsetMax = new Vector2(-8f, -4f);
            var t = labelGo.AddComponent<Text>();
            t.font      = BuiltinFont;
            t.fontSize  = 20;
            t.fontStyle = FontStyle.Bold;
            t.color     = Color.white;
            t.alignment = TextAnchor.MiddleCenter;
            t.raycastTarget = false;
            return go;
        }

        /// <summary>Shorthand for LayoutElement preferredWidth/Height.</summary>
        static void LE(GameObject go, float w, float h)
        {
            var le = go.GetComponent<LayoutElement>() ?? go.AddComponent<LayoutElement>();
            le.preferredWidth  = w;
            le.preferredHeight = h;
        }

        /// <summary>Stretch RectTransform to fill parent with a small inset.</summary>
        static void Stretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(8f, 4f);
            rt.offsetMax = new Vector2(-8f, -4f);
        }
    }
}
