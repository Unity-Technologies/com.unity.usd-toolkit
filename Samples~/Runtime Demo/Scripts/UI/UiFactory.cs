using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace Unity.USDToolkit.RuntimeDemo
{
    /// <summary>
    /// Minimal uGUI construction helpers.
    /// </summary>
    /// <remarks>
    /// Used by the editor-side HUD prefab builder (Editor/DemoHudPrefabBuilder.cs). The HUD itself
    /// is an authored prefab, so day-to-day layout changes belong in the prefab, not here.
    /// </remarks>
    public static class UiFactory
    {
        private static Font cachedFont;

        public static Font Font
        {
            get
            {
                if (cachedFont != null)
                {
                    return cachedFont;
                }

                // Unity 6 renamed the built-in Arial resource to LegacyRuntime.ttf. Falling back
                // to an OS font keeps the HUD readable rather than blank if that ever changes.
                cachedFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                if (cachedFont == null)
                {
                    cachedFont = Font.CreateDynamicFontFromOSFont("Helvetica", DemoTheme.SizeBody);
                }

                return cachedFont;
            }
        }

        public static RectTransform Rect(GameObject target)
        {
            return target.GetComponent<RectTransform>() ?? target.AddComponent<RectTransform>();
        }

        public static GameObject Create(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return go;
        }

        public static Image Panel(Transform parent, string name, Color color)
        {
            GameObject go = Create(name, parent);
            var image = go.AddComponent<Image>();
            image.color = color;
            return image;
        }

        public static Text Label(
            Transform parent,
            string name,
            string text,
            int size,
            Color color,
            FontStyle style = FontStyle.Normal,
            TextAnchor anchor = TextAnchor.UpperLeft)
        {
            GameObject go = Create(name, parent);
            var label = go.AddComponent<Text>();
            label.font = Font;
            label.text = text;
            label.fontSize = size;
            label.color = color;
            label.fontStyle = style;
            label.alignment = anchor;
            label.horizontalOverflow = HorizontalWrapMode.Wrap;
            label.verticalOverflow = VerticalWrapMode.Truncate;
            label.supportRichText = false;
            label.raycastTarget = false;
            return label;
        }

        public static UiButton Button(
            Transform parent,
            string name,
            string text,
            int size,
            float height,
            UnityAction onClick)
        {
            GameObject go = Create(name, parent);
            var image = go.AddComponent<Image>();
            image.color = DemoTheme.ButtonIdle;

            var button = go.AddComponent<Button>();
            button.targetGraphic = image;
            var colors = button.colors;
            colors.highlightedColor = new Color(1.12f, 1.12f, 1.12f, 1.0f);
            colors.pressedColor = new Color(0.82f, 0.82f, 0.82f, 1.0f);
            colors.disabledColor = Color.white;
            colors.fadeDuration = 0.06f;
            button.colors = colors;
            if (onClick != null)
            {
                button.onClick.AddListener(onClick);
            }

            Text label = Label(go.transform, "Label", text, size, DemoTheme.TextPrimary, FontStyle.Normal, TextAnchor.MiddleCenter);
            RectTransform labelRect = Rect(label.gameObject);
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.offsetMin = new Vector2(6.0f, 0.0f);
            labelRect.offsetMax = new Vector2(-6.0f, 0.0f);

            var layout = go.AddComponent<LayoutElement>();
            layout.minHeight = height;
            layout.preferredHeight = height;

            var uiButton = go.AddComponent<UiButton>();
            uiButton.Button = button;
            uiButton.Background = image;
            uiButton.Label = label;
            return uiButton;
        }

        /// <summary>Creates a child object that carries a vertical layout group.</summary>
        public static VerticalLayoutGroup VerticalGroup(
            Transform parent,
            string name,
            float spacing,
            RectOffset padding,
            bool controlHeight = true)
        {
            return AddVerticalGroup(Create(name, parent), spacing, padding, controlHeight);
        }

        /// <summary>Adds a vertical layout group to an existing object.</summary>
        public static VerticalLayoutGroup AddVerticalGroup(
            GameObject target,
            float spacing,
            RectOffset padding,
            bool controlHeight = true)
        {
            var group = target.AddComponent<VerticalLayoutGroup>();
            group.spacing = spacing;
            group.padding = padding;
            group.childControlWidth = true;
            group.childControlHeight = controlHeight;
            group.childForceExpandWidth = true;
            group.childForceExpandHeight = false;
            group.childAlignment = TextAnchor.UpperLeft;
            return group;
        }

        public static HorizontalLayoutGroup HorizontalGroup(
            Transform parent,
            string name,
            float spacing,
            RectOffset padding = null)
        {
            GameObject go = Create(name, parent);
            var group = go.AddComponent<HorizontalLayoutGroup>();
            group.spacing = spacing;
            group.padding = padding ?? new RectOffset(0, 0, 0, 0);
            group.childControlWidth = true;
            group.childControlHeight = true;
            group.childForceExpandWidth = true;
            group.childForceExpandHeight = false;
            return group;
        }

        /// <summary>A small all-caps section heading with a rule under it.</summary>
        public static Text SectionHeading(Transform parent, string text)
        {
            GameObject holder = Create("Heading_" + text, parent);
            AddVerticalGroup(holder, 3.0f, new RectOffset(0, 0, 5, 2));

            Text label = Label(holder.transform, "Text", text.ToUpperInvariant(), DemoTheme.SizeHeading,
                DemoTheme.Accent, FontStyle.Bold);
            SetPreferredHeight(label.gameObject, DemoTheme.SizeHeading + 6.0f);

            Image rule = Panel(holder.transform, "Rule", DemoTheme.AccentDim);
            SetPreferredHeight(rule.gameObject, 2.0f);
            return label;
        }

        public static void SetPreferredHeight(GameObject target, float height)
        {
            var layout = target.GetComponent<LayoutElement>() ?? target.AddComponent<LayoutElement>();
            layout.minHeight = height;
            layout.preferredHeight = height;
        }

        public static void Stretch(GameObject target, float left, float top, float right, float bottom)
        {
            RectTransform rect = Rect(target);
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(left, bottom);
            rect.offsetMax = new Vector2(-right, -top);
        }
    }
}
