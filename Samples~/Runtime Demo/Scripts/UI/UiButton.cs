using UnityEngine;

namespace Unity.USDToolkit.RuntimeDemo
{
    /// <summary>
    /// A themed button: the uGUI Button plus its background and label, and the idle palette the
    /// selected/disabled states fall back to.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class UiButton : MonoBehaviour
    {
        public UnityEngine.UI.Button Button;
        public UnityEngine.UI.Image Background;
        public UnityEngine.UI.Text Label;

        [SerializeField] private Color idleColor = DemoTheme.ButtonIdle;
        [SerializeField] private Color idleTextColor = DemoTheme.TextPrimary;

        public void SetPalette(Color background, Color text)
        {
            idleColor = background;
            idleTextColor = text;
            SetSelected(false);
        }

        /// <summary>Styles the button as the active choice in a group.</summary>
        public void SetSelected(bool selected)
        {
            Background.color = selected ? DemoTheme.Accent : idleColor;
            Label.color = selected ? DemoTheme.TextOnAccent : idleTextColor;
            Label.fontStyle = selected ? FontStyle.Bold : FontStyle.Normal;
        }

        public void SetInteractable(bool interactable)
        {
            Button.interactable = interactable;
            if (!interactable)
            {
                Background.color = DemoTheme.ButtonDisabled;
                Label.color = DemoTheme.TextMuted;
            }
        }
    }
}
