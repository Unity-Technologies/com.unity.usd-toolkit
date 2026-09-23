using UnityEngine;

namespace Unity.USDToolkit.Samples
{
    // The dark/green theme shared by the export and import samples.
    // Every background and icon is generated in code (rounded-rectangle 9-slice plus icons):
    // the alpha stays clean, the colours are exact, and with no external asset or native
    // dependency it looks the same on Windows, macOS and Linux, in the Editor and in a build.
    internal static class SampleTheme
    {
        // Mock-up palette
        public static readonly Color PageBg = Hex(0x0D0F12);
        public static readonly Color CardFill = Hex(0x16181C);
        public static readonly Color CardBorder = Hex(0x2A2D33);
        public static readonly Color FieldFill = Hex(0x0E1013);
        public static readonly Color FieldBorder = Hex(0x2A2D33);
        public static readonly Color ButtonFill = Hex(0x1C1F24);
        public static readonly Color ButtonBorder = Hex(0x2A2D33);
        public static readonly Color AccentGreen = Hex(0x2E7D32);
        public static readonly Color TextPrimary = Hex(0xE8EDF7);
        public static readonly Color TextMuted = Hex(0x9AA3B0);
        public static readonly Color SectionColor = Hex(0xF1F4F9);

        public static Texture2D FieldBg, CardTile, ButtonTile, AccentTile, CheckOn, CheckOff, Folder, Chevron, PageTex;
        public static GUIStyle Title, Subtitle, Section, FieldLabel, Body, Card, Field, PathField, Button, PrimaryButton, SegmentOn, SegmentOff, Segment, Dropdown;

        private const int Tile = 40;        // 9-slice tile size
        private const int Radius = 10;      // corner radius
        private const int Slice = 12;       // GUIStyle.border (radius + border + slack)

        private static bool built;

        public static void EnsureBuilt()
        {
            if (built && Card != null && Card.normal.background != null)
            {
                return;
            }

            PageTex = Solid(PageBg);
            Texture2D cardTex = RoundedTile(CardFill, CardBorder, 1);
            CardTile = cardTex;
            FieldBg = RoundedTile(FieldFill, FieldBorder, 1);
            Texture2D buttonTex = RoundedTile(ButtonFill, ButtonBorder, 1);
            ButtonTile = buttonTex;
            Texture2D primaryTex = RoundedTile(AccentGreen, AccentGreen, 0);
            AccentTile = primaryTex;
            Texture2D segmentTex = RoundedTile(AccentGreen, AccentGreen, 0);

            CheckOn = CheckboxTex(true);
            CheckOff = CheckboxTex(false);
            Folder = FolderIconTex();
            Chevron = ChevronTex();

            var border = new RectOffset(Slice, Slice, Slice, Slice);

            Title = new GUIStyle { fontSize = 21, fontStyle = FontStyle.Bold, normal = { textColor = TextPrimary } };
            Subtitle = new GUIStyle { fontSize = 12, wordWrap = true, normal = { textColor = TextMuted } };
            Section = new GUIStyle { fontSize = 14, fontStyle = FontStyle.Bold, normal = { textColor = SectionColor }, margin = new RectOffset(0, 0, 8, 4) };
            FieldLabel = new GUIStyle { fontSize = 12, wordWrap = true, normal = { textColor = TextMuted }, margin = new RectOffset(0, 0, 6, 2) };
            Body = new GUIStyle { fontSize = 12, wordWrap = true, normal = { textColor = TextPrimary } };

            Card = new GUIStyle { border = border, normal = { background = cardTex }, padding = new RectOffset(20, 20, 18, 18) };

            Field = new GUIStyle
            {
                border = border,
                fontSize = 13,
                alignment = TextAnchor.MiddleLeft,
                clipping = TextClipping.Clip,
                padding = new RectOffset(12, 12, 8, 8),
                normal = { background = FieldBg, textColor = TextPrimary },
                hover = { background = FieldBg, textColor = TextPrimary },
                focused = { background = FieldBg, textColor = TextPrimary },
                active = { background = FieldBg, textColor = TextPrimary }
            };

            // For folder paths: right-aligned, so a long path still shows its tail (the
            // folder name).
            PathField = new GUIStyle(Field) { alignment = TextAnchor.MiddleRight };

            Button = new GUIStyle
            {
                border = border,
                fontSize = 13,
                alignment = TextAnchor.MiddleCenter,
                padding = new RectOffset(10, 10, 9, 9),
                normal = { background = buttonTex, textColor = TextPrimary },
                hover = { background = buttonTex, textColor = Color.white },
                active = { background = buttonTex, textColor = Color.white }
            };

            PrimaryButton = new GUIStyle(Button)
            {
                fontStyle = FontStyle.Bold,
                normal = { background = primaryTex, textColor = Color.white },
                hover = { background = primaryTex, textColor = Color.white },
                active = { background = primaryTex, textColor = Color.white }
            };

            SegmentOn = new GUIStyle(Button)
            {
                fontStyle = FontStyle.Bold,
                normal = { background = segmentTex, textColor = Color.white },
                hover = { background = segmentTex, textColor = Color.white },
                active = { background = segmentTex, textColor = Color.white }
            };
            SegmentOff = new GUIStyle(Button);

            // For GUILayout.Toolbar: the selected segment (onNormal) is green, the rest use
            // the button colour, split evenly.
            Segment = new GUIStyle(Button)
            {
                fontStyle = FontStyle.Bold,
                onNormal = { background = segmentTex, textColor = Color.white },
                onHover = { background = segmentTex, textColor = Color.white },
                onActive = { background = segmentTex, textColor = Color.white }
            };

            Dropdown = new GUIStyle(Field) { alignment = TextAnchor.MiddleLeft };

            built = true;
        }

        // Green tick when on, empty box when off; the whole row toggles it.
        public static bool Checkbox(bool value, GUIContent content)
        {
            EnsureBuilt();
            GUILayout.BeginHorizontal();
            Rect box = GUILayoutUtility.GetRect(20f, 20f, GUILayout.Width(20f), GUILayout.Height(20f));
            if (Event.current.type == EventType.Repaint)
            {
                GUI.color = Color.white;
                GUI.DrawTexture(box, value ? CheckOn : CheckOff);
            }

            GUILayout.Space(8f);
            GUILayout.Label(content, Body, GUILayout.ExpandWidth(true));
            GUILayout.EndHorizontal();

            Rect row = GUILayoutUtility.GetLastRect();
            if (Event.current.type == EventType.MouseDown && row.Contains(Event.current.mousePosition))
            {
                value = !value;
                Event.current.Use();
            }

            return value;
        }

        // For samples whose HUD draws over the 3D scene: a copy with only the card background
        // made translucent.
        // Each call builds a new texture, so build once and cache.
        public static GUIStyle TranslucentCard(float alpha)
        {
            EnsureBuilt();
            return new GUIStyle(Card) { normal = { background = RoundedTile(CardFill, CardBorder, 1, alpha) } };
        }

        // ---------- Texture generation ----------

        private static Color Hex(int rgb)
        {
            return new Color(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, 1f);
        }

        private static Texture2D NewTex(int w, int h)
        {
            return new Texture2D(w, h, TextureFormat.RGBA32, false, false)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
        }

        private static Texture2D Solid(Color c)
        {
            var t = NewTex(1, 1);
            t.SetPixel(0, 0, c);
            t.Apply();
            return t;
        }

        // Rounded-rectangle tile with transparent corners, stretched as a 9-slice. With
        // borderW > 0 the edge takes the border colour.
        private static Texture2D RoundedTile(Color fill, Color border, int borderW, float alpha = 1f)
        {
            int s = Tile;
            float hw = s / 2f;
            var px = new Color[s * s];
            for (int y = 0; y < s; y++)
            {
                for (int x = 0; x < s; x++)
                {
                    float fx = x + 0.5f;
                    float fy = y + 0.5f;
                    float dx = Mathf.Abs(fx - hw) - hw + Radius;
                    float dy = Mathf.Abs(fy - hw) - hw + Radius;
                    float outside = Mathf.Sqrt(Mathf.Max(dx, 0f) * Mathf.Max(dx, 0f) + Mathf.Max(dy, 0f) * Mathf.Max(dy, 0f));
                    float d = outside + Mathf.Min(Mathf.Max(dx, dy), 0f) - Radius; // < 0 is inside
                    float a = Mathf.Clamp01(0.5f - d);                            // 1px antialiasing
                    Color c = (borderW > 0 && d > -borderW) ? border : fill;
                    px[y * s + x] = new Color(c.r, c.g, c.b, a * alpha);
                }
            }

            FlipV(px, s);
            var t = NewTex(s, s);
            t.SetPixels(px);
            t.Apply();
            return t;
        }

        private static Texture2D CheckboxTex(bool on)
        {
            int s = 32;
            float hw = s / 2f;
            int rad = 7;
            Color fill = on ? AccentGreen : FieldFill;
            Color bdr = on ? AccentGreen : Hex(0x3A3D44);
            var px = new Color[s * s];
            for (int y = 0; y < s; y++)
            {
                for (int x = 0; x < s; x++)
                {
                    float fx = x + 0.5f, fy = y + 0.5f;
                    float dx = Mathf.Abs(fx - hw) - hw + rad;
                    float dy = Mathf.Abs(fy - hw) - hw + rad;
                    float outside = Mathf.Sqrt(Mathf.Max(dx, 0f) * Mathf.Max(dx, 0f) + Mathf.Max(dy, 0f) * Mathf.Max(dy, 0f));
                    float d = outside + Mathf.Min(Mathf.Max(dx, dy), 0f) - rad;
                    float a = Mathf.Clamp01(0.5f - d);
                    Color c = (d > -1f) ? bdr : fill;
                    px[y * s + x] = new Color(c.r, c.g, c.b, a);
                }
            }

            if (on)
            {
                // White tick mark
                DrawSeg(px, s, 0.24f, 0.52f, 0.43f, 0.70f, 2.4f, Color.white);
                DrawSeg(px, s, 0.43f, 0.70f, 0.76f, 0.32f, 2.4f, Color.white);
            }

            FlipV(px, s);
            var t = NewTex(s, s);
            t.SetPixels(px);
            t.Apply();
            return t;
        }

        private static Texture2D FolderIconTex()
        {
            int s = 32;
            var px = new Color[s * s];
            Color body = Hex(0xD7DCE4);
            // Body plus the top-left tab (y = 0 is the bottom)
            FillRect(px, s, 4, 6, 28, 23, body);
            FillRect(px, s, 5, 22, 14, 26, body);
            FlipV(px, s);
            var t = NewTex(s, s);
            t.SetPixels(px);
            t.Apply();
            return t;
        }

        private static Texture2D ChevronTex()
        {
            int s = 32;
            var px = new Color[s * s];
            Color g = Hex(0xAFB6C0);
            // Downward chevron
            DrawSeg(px, s, 0.26f, 0.58f, 0.5f, 0.36f, 2.6f, g);
            DrawSeg(px, s, 0.5f, 0.36f, 0.74f, 0.58f, 2.6f, g);
            FlipV(px, s);
            var t = NewTex(s, s);
            t.SetPixels(px);
            t.Apply();
            return t;
        }

        // GUI.DrawTexture draws a texture upside down, so asymmetric icons are flipped
        // vertically to compensate.
        private static void FlipV(Color[] px, int s)
        {
            for (int y = 0; y < s / 2; y++)
            {
                for (int x = 0; x < s; x++)
                {
                    int a = y * s + x;
                    int b = (s - 1 - y) * s + x;
                    Color tmp = px[a];
                    px[a] = px[b];
                    px[b] = tmp;
                }
            }
        }

        private static void FillRect(Color[] px, int s, int x0, int y0, int x1, int y1, Color c)
        {
            for (int y = y0; y <= y1; y++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    if (x >= 0 && x < s && y >= 0 && y < s)
                    {
                        px[y * s + x] = c;
                    }
                }
            }
        }

        // Draws a thick, antialiased line segment in normalized (0..1) coordinates.
        private static void DrawSeg(Color[] px, int s, float ax, float ay, float bx, float by, float thick, Color c)
        {
            float x1 = ax * s, y1 = ay * s, x2 = bx * s, y2 = by * s;
            float half = thick * 0.5f;
            for (int y = 0; y < s; y++)
            {
                for (int x = 0; x < s; x++)
                {
                    float dist = SegDist(x + 0.5f, y + 0.5f, x1, y1, x2, y2);
                    float a = Mathf.Clamp01(half + 0.5f - dist);
                    if (a <= 0f)
                    {
                        continue;
                    }

                    Color cur = px[y * s + x];
                    float na = Mathf.Max(cur.a, a * c.a);
                    px[y * s + x] = new Color(
                        Mathf.Lerp(cur.r, c.r, a),
                        Mathf.Lerp(cur.g, c.g, a),
                        Mathf.Lerp(cur.b, c.b, a),
                        na);
                }
            }
        }

        private static float SegDist(float px, float py, float ax, float ay, float bx, float by)
        {
            float dx = bx - ax, dy = by - ay;
            float len2 = dx * dx + dy * dy;
            float t = len2 <= 0f ? 0f : Mathf.Clamp01(((px - ax) * dx + (py - ay) * dy) / len2);
            float cx = ax + t * dx, cy = ay + t * dy;
            return Mathf.Sqrt((px - cx) * (px - cx) + (py - cy) * (py - cy));
        }
    }
}
