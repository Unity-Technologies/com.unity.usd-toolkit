using UnityEngine;

namespace Unity.USDToolkit.Samples
{
    // Export/Import 샘플 공용 다크/그린 테마.
    // 모든 배경/아이콘을 코드로 생성한다(둥근 사각형 9-slice + 아이콘). 투명 알파가 깨끗하고
    // 색이 정확하며 외부 에셋/네이티브 의존이 없어 Win/macOS/Linux·에디터·빌드 모두 동일하게 동작.
    internal static class SampleTheme
    {
        // 목업 팔레트
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

        private const int Tile = 40;        // 9-slice 타일 크기
        private const int Radius = 10;      // 모서리 반경
        private const int Slice = 12;       // GUIStyle.border (radius + border + 여유)

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

            // 폴더 경로용 — 우측 정렬이라 긴 경로에서 끝(폴더명)이 보인다.
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

            // GUILayout.Toolbar 용 — 선택 세그먼트(onNormal)는 초록, 비선택은 버튼색. 균등 분할.
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

        // 초록 체크 on / 빈 박스 off, 행 전체 클릭으로 토글
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

        // HUD 가 3D 씬 위에 그려지는 샘플용 — 카드 배경만 반투명으로 만든 사본.
        // 호출할 때마다 텍스처를 새로 만들므로 한 번만 만들어 캐시해서 쓴다.
        public static GUIStyle TranslucentCard(float alpha)
        {
            EnsureBuilt();
            return new GUIStyle(Card) { normal = { background = RoundedTile(CardFill, CardBorder, 1, alpha) } };
        }

        // ---------- 텍스처 생성 ----------

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

        // 둥근 사각형 타일(투명 코너) — 9-slice 로 늘려 씀. borderW>0 이면 가장자리에 보더색.
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
                    float d = outside + Mathf.Min(Mathf.Max(dx, dy), 0f) - Radius; // <0 내부
                    float a = Mathf.Clamp01(0.5f - d);                            // 1px 안티앨리어싱
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
                // 흰 체크표시
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
            // 본체 + 좌상단 탭 (y는 아래가 0)
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
            // 아래 방향 ˅
            DrawSeg(px, s, 0.26f, 0.58f, 0.5f, 0.36f, 2.6f, g);
            DrawSeg(px, s, 0.5f, 0.36f, 0.74f, 0.58f, 2.6f, g);
            FlipV(px, s);
            var t = NewTex(s, s);
            t.SetPixels(px);
            t.Apply();
            return t;
        }

        // GUI.DrawTexture 는 텍스처를 위아래 반대로 그리므로, 비대칭 아이콘은 수직 플립해 보정한다.
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

        // 정규화 좌표(0..1)로 두꺼운 선분 그리기(안티앨리어싱)
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
