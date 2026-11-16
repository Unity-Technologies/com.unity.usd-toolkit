using UnityEngine;

namespace Unity.USDToolkit.Samples.SimReadyCentral
{
    /// <summary>Colours and the small textures both handle front-ends draw with, made in code.</summary>
    public static class SimReadyStyle
    {
        public static readonly Color HandleColor = new Color(0.96f, 0.55f, 0.13f, 1f);
        public static readonly Color HandleHoverColor = new Color(1f, 0.68f, 0.32f, 1f);
        public static readonly Color HandleActiveColor = new Color(1f, 0.78f, 0.45f, 1f);
        public static readonly Color PanelColor = new Color(0.08f, 0.08f, 0.09f, 0.85f);
        public static readonly Color BadgeColor = new Color(0.96f, 0.55f, 0.13f, 0.92f);
        public static readonly Color GridColor = new Color(0.5f, 0.5f, 0.55f, 0.35f);

        /// <summary>A filled, anti-aliased white disc (tinted when drawn).</summary>
        public static Texture2D DiscTexture(int size)
        {
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave, wrapMode = TextureWrapMode.Clamp };
            float radius = (size * 0.5f) - 1f;
            var pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(size * 0.5f, size * 0.5f));
                    float alpha = Mathf.Clamp01(radius - d + 0.5f);
                    // A thin white rim, as the preview draws it.
                    float rim = Mathf.Clamp01(1.5f - Mathf.Abs(radius - 1.5f - d));
                    byte shade = (byte)Mathf.Lerp(235f, 255f, rim);
                    pixels[(y * size) + x] = new Color32(shade, shade, shade, (byte)(alpha * 255f));
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            return texture;
        }

        public static Texture2D PanelTexture() => Solid(PanelColor);

        public static Texture2D BadgeTexture() => Solid(BadgeColor);

        /// <summary>A cursor: an open hand (hover) or a closed one (drag), drawn as discs so no art ships with the sample.</summary>
        public static Texture2D CursorTexture(bool grabbing)
        {
            const int size = 32;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
            var pixels = new Color32[size * size];
            var center = new Vector2(16f, 16f);
            float outer = grabbing ? 7f : 10f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), center);
                    byte alpha = d <= outer + 1.5f ? (byte)255 : (byte)0;
                    byte shade = d <= outer ? (byte)255 : (byte)20;
                    if (!grabbing && d < outer - 3f)
                    {
                        alpha = 0; // a ring: an open hand
                    }

                    pixels[(y * size) + x] = new Color32(shade, shade, shade, alpha);
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply(false, false);
            return texture;
        }

        private static Texture2D Solid(Color color)
        {
            var texture = new Texture2D(1, 1, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
            texture.SetPixel(0, 0, color);
            texture.Apply(false, true);
            return texture;
        }
    }
}
