using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace Unity.USDToolkit.RuntimeDemo
{
    public enum StatusKind
    {
        Info,
        Busy,
        Success,
        Warning,
        Error
    }

    /// <summary>
    /// The on-screen control surface: one sidebar on the left, authored as a uGUI prefab
    /// (Prefabs/DemoHud.prefab).
    /// </summary>
    /// <remarks>
    /// Everything lives on one side so the viewport is a single uninterrupted rectangle rather
    /// than a letterbox between two panels — on a projector that is the difference between the
    /// part being the subject of the slide and the UI being the subject.
    ///
    /// More controls than fit at 1080p, so the sidebar is header / scroll / footer: Import is
    /// pinned to the top and Export to the bottom, and only the middle scrolls. Neither of the
    /// two actions the demo is actually about can ever end up below the fold.
    ///
    /// This class only holds references and pushes state into them; the hierarchy and its
    /// layout are edited in the prefab. Button clicks are wired here rather than as persistent
    /// listeners so the prefab carries no method-name strings that a rename could silently break.
    /// </remarks>
    public sealed class DemoHud : MonoBehaviour
    {
        public event Action ImportRequested;
        public event Action ExportRequested;
        public event Action ClearRequested;
        public event Action<GizmoMode> GizmoModeChanged;
        public event Action<UsdExportMode> ExportModeChanged;
        public event Action<UsdExportFormat> ExportFormatChanged;
        public event Action<bool> RelativePathsChanged;
        public event Action<UsdConversionMode> UpAxisModeChanged;
        public event Action<UsdConversionMode> UnitModeChanged;
        public event Action<UsdSceneEntry> EntryClicked;

        [Header("Source")]
        [SerializeField] private UiButton importButton;
        [SerializeField] private UiButton upAxisAutoButton;
        [SerializeField] private UiButton upAxisOffButton;
        [SerializeField] private UiButton unitAutoButton;
        [SerializeField] private UiButton unitOffButton;

        [Header("Stages")]
        [SerializeField] private Text loadedHeading;
        [SerializeField] private Transform listContainer;
        [Tooltip("Inactive row cloned once per loaded stage.")]
        [SerializeField] private StageListRow rowTemplate;
        [Tooltip("Shown in place of the list when nothing is loaded.")]
        [SerializeField] private GameObject emptyListRow;

        [Header("Manipulate")]
        [SerializeField] private UiButton translateButton;
        [SerializeField] private UiButton rotateButton;
        [SerializeField] private Text selectionLabel;

        [Header("Export")]
        [SerializeField] private UiButton referencingButton;
        [SerializeField] private UiButton flatteningButton;
        [SerializeField] private Text exportModeBlurb;
        [SerializeField] private UiButton usdcButton;
        [SerializeField] private UiButton usdaButton;
        [SerializeField] private UiButton usdButton;
        [SerializeField] private UiButton relativeButton;
        [SerializeField] private UiButton absoluteButton;
        [SerializeField] private UiButton exportButton;
        [SerializeField] private UiButton clearButton;

        [Header("Chrome")]
        [SerializeField] private RectTransform panel;
        [SerializeField] private UiButton panelTab;
        [SerializeField] private Text runtimeInfoLabel;
        [SerializeField] private Text statusText;
        [SerializeField] private Image statusStripe;

        private bool panelVisible = true;

        private readonly List<GameObject> listRows = new List<GameObject>();

        private void Awake()
        {
            Wire(importButton, () => ImportRequested?.Invoke());
            Wire(upAxisAutoButton, () => UpAxisModeChanged?.Invoke(UsdConversionMode.Auto));
            Wire(upAxisOffButton, () => UpAxisModeChanged?.Invoke(UsdConversionMode.Off));
            Wire(unitAutoButton, () => UnitModeChanged?.Invoke(UsdConversionMode.Auto));
            Wire(unitOffButton, () => UnitModeChanged?.Invoke(UsdConversionMode.Off));

            Wire(translateButton, () => GizmoModeChanged?.Invoke(GizmoMode.Translate));
            Wire(rotateButton, () => GizmoModeChanged?.Invoke(GizmoMode.Rotate));

            Wire(referencingButton, () => ExportModeChanged?.Invoke(UsdExportMode.Referencing));
            Wire(flatteningButton, () => ExportModeChanged?.Invoke(UsdExportMode.Flattening));
            Wire(usdcButton, () => ExportFormatChanged?.Invoke(UsdExportFormat.Usdc));
            Wire(usdaButton, () => ExportFormatChanged?.Invoke(UsdExportFormat.Usda));
            Wire(usdButton, () => ExportFormatChanged?.Invoke(UsdExportFormat.Usd));
            Wire(relativeButton, () => RelativePathsChanged?.Invoke(true));
            Wire(absoluteButton, () => RelativePathsChanged?.Invoke(false));
            Wire(exportButton, () => ExportRequested?.Invoke());
            Wire(clearButton, () => ClearRequested?.Invoke());

            Wire(panelTab, TogglePanel);

            // The template is only ever cloned, never shown.
            if (rowTemplate != null)
            {
                rowTemplate.gameObject.SetActive(false);
            }

            panelVisible = panel == null || panel.gameObject.activeSelf;
            ApplyPanelVisibility();
        }

        private void Wire(UiButton target, Action onClick)
        {
            if (target == null)
            {
                Debug.LogError($"{nameof(DemoHud)}: a button reference is not assigned in the prefab.", this);
                return;
            }

            target.Button.onClick.AddListener(() => onClick());
        }

        // ---------------------------------------------------------------- collapse

        /// <summary>Collapses the sidebar for a full-bleed look at the geometry, or restores it.</summary>
        public void TogglePanel()
        {
            panelVisible = !panelVisible;
            ApplyPanelVisibility();
        }

        private void ApplyPanelVisibility()
        {
            if (panel != null && panel.gameObject.activeSelf != panelVisible)
            {
                panel.gameObject.SetActive(panelVisible);

                // Layout groups do not rebuild while disabled, so a sidebar that gained or lost
                // list rows while hidden would come back mis-sized for a frame.
                if (panelVisible)
                {
                    LayoutRebuilder.ForceRebuildLayoutImmediate(panel);
                }
            }

            // The tab rides the panel's inner edge, and falls back to the screen edge once the
            // panel is gone, so there is always something visible to click.
            if (panelTab != null)
            {
                float panelWidth = panel != null ? panel.rect.width : 0.0f;
                ((RectTransform)panelTab.transform).anchoredPosition =
                    new Vector2(panelVisible ? panelWidth : 0.0f, 0.0f);
                panelTab.Label.text = panelVisible ? "<" : ">";
            }
        }

        // ---------------------------------------------------------------- state in

        public void SetRuntimeInfo(string text)
        {
            runtimeInfoLabel.text = text;
        }

        public void SetStatus(string message, StatusKind kind)
        {
            statusText.text = message;
            statusText.color = kind == StatusKind.Error ? DemoTheme.Error : DemoTheme.TextPrimary;
            statusStripe.color = kind switch
            {
                StatusKind.Success => DemoTheme.Success,
                StatusKind.Warning => DemoTheme.Warning,
                StatusKind.Error => DemoTheme.Error,
                StatusKind.Busy => DemoTheme.Warning,
                _ => DemoTheme.Accent
            };
        }

        public void SetBusy(bool busy)
        {
            importButton.Button.interactable = !busy;
            exportButton.Button.interactable = !busy;
            clearButton.Button.interactable = !busy;
        }

        public void SetGizmoMode(GizmoMode mode)
        {
            translateButton.SetSelected(mode == GizmoMode.Translate);
            rotateButton.SetSelected(mode == GizmoMode.Rotate);
        }

        public void SetExportMode(UsdExportMode mode)
        {
            referencingButton.SetSelected(mode == UsdExportMode.Referencing);
            flatteningButton.SetSelected(mode == UsdExportMode.Flattening);

            // Two lines each: the blurb sits in a fixed-height slot so the Export button below
            // it never moves when the mode changes.
            exportModeBlurb.text = mode == UsdExportMode.Referencing
                ? "References the original layers — small file, source stays the authority. Written as .usda."
                : "Bakes all geometry into one self-contained stage. Larger, portable, no dependencies.";
        }

        public void SetExportFormat(UsdExportFormat format, bool selectable)
        {
            usdcButton.SetSelected(selectable && format == UsdExportFormat.Usdc);
            usdaButton.SetSelected(format == UsdExportFormat.Usda);
            usdButton.SetSelected(selectable && format == UsdExportFormat.Usd);

            // Referencing has no binary option, so the choice is shown as made, not as available.
            usdcButton.SetInteractable(selectable);
            usdaButton.SetInteractable(selectable);
            usdButton.SetInteractable(selectable);
            if (!selectable)
            {
                usdaButton.SetSelected(true);
            }
        }

        public void SetRelativePaths(bool relative, bool selectable)
        {
            relativeButton.SetSelected(selectable && relative);
            absoluteButton.SetSelected(selectable && !relative);
            relativeButton.SetInteractable(selectable);
            absoluteButton.SetInteractable(selectable);
        }

        public void SetConversionModes(UsdConversionMode upAxis, UsdConversionMode units)
        {
            upAxisAutoButton.SetSelected(upAxis == UsdConversionMode.Auto);
            upAxisOffButton.SetSelected(upAxis == UsdConversionMode.Off);
            unitAutoButton.SetSelected(units == UsdConversionMode.Auto);
            unitOffButton.SetSelected(units == UsdConversionMode.Off);
        }

        public void SetSelection(UsdSceneEntry entry)
        {
            // The highlighted row in the list already carries the name and counts, so this
            // line only has to confirm what the gizmo is pointed at.
            selectionLabel.text = entry == null
                ? "Click a part to select it · scale not offered"
                : entry.DisplayName + " · scale not offered";
            selectionLabel.color = entry == null ? DemoTheme.TextMuted : DemoTheme.Accent;
        }

        public void SetLoadedEntries(IReadOnlyList<UsdSceneEntry> entries, UsdSceneEntry selected)
        {
            foreach (GameObject row in listRows)
            {
                Destroy(row);
            }

            listRows.Clear();

            loadedHeading.text = $"LOADED STAGES ({entries.Count})";
            emptyListRow.SetActive(entries.Count == 0);

            for (int i = 0; i < entries.Count; i++)
            {
                UsdSceneEntry entry = entries[i];

                StageListRow row = Instantiate(rowTemplate, listContainer);
                row.name = "Row_" + i;
                row.Bind(entry, ReferenceEquals(entry, selected), clicked => EntryClicked?.Invoke(clicked));
                row.gameObject.SetActive(true);

                listRows.Add(row.gameObject);
            }
        }
    }
}
