using System;
using UnityEngine;

namespace Unity.USDToolkit.RuntimeDemo
{
    /// <summary>
    /// One row of the loaded-stages list. The HUD prefab carries a single inactive instance as a
    /// template; <see cref="DemoHud"/> clones it per loaded stage.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class StageListRow : MonoBehaviour
    {
        private const float HeightPlain = 50.0f;
        private const float HeightWithConversion = 68.0f;

        [SerializeField] private UnityEngine.UI.Image background;
        [SerializeField] private UnityEngine.UI.Button button;
        [SerializeField] private UnityEngine.UI.LayoutElement layout;
        [SerializeField] private UnityEngine.UI.Text nameLabel;
        [SerializeField] private UnityEngine.UI.Text detailLabel;
        [SerializeField] private UnityEngine.UI.Text conversionLabel;

        public void Bind(UsdSceneEntry entry, bool isSelected, Action<UsdSceneEntry> onClick)
        {
            background.color = isSelected ? DemoTheme.RowSelected : DemoTheme.RowIdle;

            nameLabel.text = entry.DisplayName;
            nameLabel.fontStyle = isSelected ? FontStyle.Bold : FontStyle.Normal;
            detailLabel.text = entry.SummaryLine;

            bool converted = !entry.Conversion.IsIdentity;
            conversionLabel.gameObject.SetActive(converted);
            if (converted)
            {
                conversionLabel.text = "↻ " + entry.Conversion.Describe();
            }

            layout.minHeight = layout.preferredHeight = converted ? HeightWithConversion : HeightPlain;

            button.onClick.RemoveAllListeners();
            button.onClick.AddListener(() => onClick(entry));
        }
    }
}
