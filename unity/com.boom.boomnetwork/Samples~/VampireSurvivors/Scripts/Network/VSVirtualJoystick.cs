// BoomNetwork VampireSurvivors Demo — Virtual Joystick (Mobile)
//
// Self-bootstrapping mobile-only virtual joystick.
// Call VSVirtualJoystick.Create() once; returns null on non-mobile platforms.
// Query Direction (Vector2, normalized, dead-zone applied) each frame — zero GC.
//
// Architecture:
//   Create() builds: Canvas → SafeArea panel → Outer ring (this component) → Inner handle
//   Canvas uses Scale With Screen Size (1920×1080, match 0.5) + ScreenSpaceOverlay.
//   Safe area is applied to the panel so the joystick stays inside notch/punch-hole bounds.

using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace BoomNetwork.Samples.VampireSurvivors
{
    /// <summary>
    /// Mobile virtual joystick. Created at runtime via <see cref="Create"/>.
    /// Implements pointer event interfaces — attach point is the outer ring Image.
    /// </summary>
    public sealed class VSVirtualJoystick : MonoBehaviour,
        IPointerDownHandler, IDragHandler, IPointerUpHandler
    {
        // ─── Public API ───────────────────────────────────────────────────────

        /// <summary>
        /// Normalized movement direction with dead-zone applied.
        /// Zero when idle, no touch active, or within dead-zone threshold.
        /// Read this in your Update loop — no allocations.
        /// </summary>
        public Vector2 Direction { get; private set; }

        /// <summary>
        /// Factory. Returns null on non-mobile platforms (including Editor running as PC).
        /// Safe to call multiple times — only the first call creates a joystick.
        /// </summary>
        public static VSVirtualJoystick Create()
        {
#if UNITY_ANDROID || UNITY_IOS
            const bool isMobileBuild = true;
#else
            const bool isMobileBuild = false;
#endif
            if (!isMobileBuild && !Application.isMobilePlatform) return null;

            // ── Canvas ──────────────────────────────────────────────────────
            var canvasGo = new GameObject("[VS] JoystickCanvas");
            Object.DontDestroyOnLoad(canvasGo);

            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode  = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 100;

            var scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode        = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight  = 0.5f;

            canvasGo.AddComponent<GraphicRaycaster>();

            // EventSystem (create only if none exists in scene)
            if (Object.FindObjectOfType<EventSystem>() == null)
            {
                var evGo = new GameObject("[VS] EventSystem");
                evGo.AddComponent<EventSystem>();
                evGo.AddComponent<StandaloneInputModule>();
            }

            // ── Safe-area panel ─────────────────────────────────────────────
            // Shrinks to Screen.safeArea so children stay inside notch/punch-hole.
            var safeGo   = new GameObject("SafeArea");
            safeGo.transform.SetParent(canvasGo.transform, false);
            var safeRect = safeGo.AddComponent<RectTransform>();
            ApplySafeArea(safeRect);

            // ── Outer ring (this component lives here) ──────────────────────
            var outerGo  = new GameObject("JoystickOuter");
            outerGo.transform.SetParent(safeGo.transform, false);

            var joystick          = outerGo.AddComponent<VSVirtualJoystick>();
            joystick._canvas      = canvas;
            joystick._rect        = outerGo.AddComponent<RectTransform>();

            // Anchor: bottom-left corner of safe area
            joystick._rect.anchorMin = Vector2.zero;
            joystick._rect.anchorMax = Vector2.zero;
            joystick._rect.pivot     = new Vector2(0.5f, 0.5f);
            joystick._rect.sizeDelta = new Vector2(OuterDiameter, OuterDiameter);
            joystick._rect.anchoredPosition = new Vector2(
                OuterRadius + MarginRef,
                OuterRadius + MarginRef);

            var outerImg = outerGo.AddComponent<Image>();
            outerImg.sprite        = BuildCircleSprite(64);
            outerImg.color         = new Color(1f, 1f, 1f, 0.25f);
            outerImg.raycastTarget = true;

            // ── Inner handle ────────────────────────────────────────────────
            var innerGo = new GameObject("JoystickInner");
            innerGo.transform.SetParent(outerGo.transform, false);

            joystick._innerRect              = innerGo.AddComponent<RectTransform>();
            joystick._innerRect.anchorMin    = new Vector2(0.5f, 0.5f);
            joystick._innerRect.anchorMax    = new Vector2(0.5f, 0.5f);
            joystick._innerRect.pivot        = new Vector2(0.5f, 0.5f);
            joystick._innerRect.sizeDelta    = new Vector2(InnerDiameter, InnerDiameter);
            joystick._innerRect.anchoredPosition = Vector2.zero;

            var innerImg = innerGo.AddComponent<Image>();
            innerImg.sprite        = BuildCircleSprite(48);
            innerImg.color         = new Color(1f, 1f, 1f, 0.55f);
            innerImg.raycastTarget = false;

            return joystick;
        }

        // ─── Constants (in CanvasScaler reference-resolution units) ──────────

        const float OuterRadius   = 100f;
        const float OuterDiameter = OuterRadius * 2f;
        const float InnerDiameter = OuterRadius * 0.85f;
        /// <summary>Distance from screen edge to joystick center (ref-res units).</summary>
        const float MarginRef     = 28f;
        /// <summary>
        /// Fraction of outer radius below which movement is ignored.
        /// Prevents accidental micro-input from finger placement wobble.
        /// </summary>
        const float DeadZone = 0.15f;

        // ─── Fields ──────────────────────────────────────────────────────────

        Canvas        _canvas;
        RectTransform _rect;
        RectTransform _innerRect;
        int           _pointerId = -1;

        // ─── IPointerDownHandler ─────────────────────────────────────────────

        public void OnPointerDown(PointerEventData e)
        {
            if (_pointerId != -1) return; // ignore simultaneous second finger
            _pointerId = e.pointerId;
            UpdateHandle(e.position);
        }

        // ─── IDragHandler ────────────────────────────────────────────────────

        public void OnDrag(PointerEventData e)
        {
            if (e.pointerId != _pointerId) return;
            UpdateHandle(e.position);
        }

        // ─── IPointerUpHandler ───────────────────────────────────────────────

        public void OnPointerUp(PointerEventData e)
        {
            if (e.pointerId != _pointerId) return;
            _pointerId = -1;
            _innerRect.anchoredPosition = Vector2.zero;
            Direction = Vector2.zero;
        }

        // ─── Private helpers ─────────────────────────────────────────────────

        void UpdateHandle(Vector2 screenPos)
        {
            // Convert screen-space touch position → local space of outer ring rect.
            // worldCamera = null is correct for ScreenSpaceOverlay canvas.
            RectTransformUtility.ScreenPointToLocalPointInRectangle(
                _rect, screenPos, null, out var local);

            // Clamp to edge of outer ring, leaving room for inner handle.
            float maxTravel = OuterRadius - InnerDiameter * 0.5f;
            var   clamped   = Vector2.ClampMagnitude(local, maxTravel);
            _innerRect.anchoredPosition = clamped;

            // Normalize and apply dead zone.
            var raw = clamped / maxTravel;
            Direction = raw.magnitude > DeadZone ? raw : Vector2.zero;
        }

        /// <summary>
        /// Sets a RectTransform to cover only Screen.safeArea (notch/punch-hole aware).
        /// </summary>
        static void ApplySafeArea(RectTransform rt)
        {
            Rect safe = Screen.safeArea;
            rt.anchorMin = new Vector2(safe.xMin / Screen.width,
                                       safe.yMin / Screen.height);
            rt.anchorMax = new Vector2(safe.xMax / Screen.width,
                                       safe.yMax / Screen.height);
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }

        /// <summary>
        /// Builds a soft-edge filled-circle sprite at runtime.
        /// Called once at startup — not in hot path.
        /// </summary>
        static Sprite BuildCircleSprite(int size)
        {
            var   tex = new Texture2D(size, size, TextureFormat.ARGB32, false);
            float r   = size * 0.5f;
            float cx  = r - 0.5f;
            float cy  = r - 0.5f;
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float dx   = x - cx;
                float dy   = y - cy;
                float dist = Mathf.Sqrt(dx * dx + dy * dy);
                float a    = Mathf.Clamp01(r - dist); // 1px soft edge anti-aliasing
                tex.SetPixel(x, y, new Color(1f, 1f, 1f, a));
            }
            tex.Apply();
            return Sprite.Create(tex, new Rect(0f, 0f, size, size),
                                  new Vector2(0.5f, 0.5f), size);
        }
    }
}
