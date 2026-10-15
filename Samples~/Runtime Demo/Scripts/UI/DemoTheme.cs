using UnityEngine;

namespace Unity.USDToolkit.RuntimeDemo
{
    /// <summary>
    /// Colours and sizes tuned for a projector at the back of a meeting room: high contrast on a
    /// dark ground, and nothing smaller than 16 px at the 1920x1080 reference resolution.
    /// </summary>
    public static class DemoTheme
    {
        public static readonly Color Background = new Color(0.075f, 0.082f, 0.098f, 0.94f);
        public static readonly Color PanelInset = new Color(0.125f, 0.135f, 0.155f, 0.95f);
        public static readonly Color RowIdle = new Color(0.16f, 0.175f, 0.20f, 0.90f);
        public static readonly Color RowSelected = new Color(0.10f, 0.32f, 0.40f, 0.95f);

        public static readonly Color Accent = new Color(0.16f, 0.78f, 0.94f, 1.0f);
        public static readonly Color AccentDim = new Color(0.16f, 0.78f, 0.94f, 0.22f);
        public static readonly Color Success = new Color(0.38f, 0.86f, 0.48f, 1.0f);
        public static readonly Color Warning = new Color(1.0f, 0.76f, 0.30f, 1.0f);
        public static readonly Color Error = new Color(1.0f, 0.45f, 0.40f, 1.0f);

        public static readonly Color TextPrimary = new Color(0.93f, 0.94f, 0.96f, 1.0f);
        public static readonly Color TextMuted = new Color(0.60f, 0.64f, 0.70f, 1.0f);
        public static readonly Color TextOnAccent = new Color(0.04f, 0.09f, 0.11f, 1.0f);

        public static readonly Color ButtonIdle = new Color(0.20f, 0.22f, 0.26f, 1.0f);
        public static readonly Color ButtonDisabled = new Color(0.15f, 0.16f, 0.18f, 1.0f);

        public const int SizeTitle = 26;
        public const int SizeHeading = 17;
        public const int SizeBody = 18;
        public const int SizeSmall = 15;
        public const int SizeStatus = 20;
    }
}
