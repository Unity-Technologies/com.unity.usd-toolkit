using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace Unity.USDToolkit.ComposedGripper
{
    /// <summary>
    /// The sample's panel, drawn with IMGUI by the play-mode demo (in the Game view) and by the
    /// Composed Gripper Physics window (in edit mode): the asset and preset to import, the closing
    /// controls, and per gripper what composed.
    /// </summary>
    public sealed class ComposedGripperPanel
    {
        /// <summary>What the panel shows for one imported gripper.</summary>
        public sealed class GripperSummary
        {
            public string PrimPath = string.Empty;
            public string Selections = string.Empty;
            public int Links;
            public int Colliders;
            public string Drive = string.Empty;
            public string Pads = string.Empty;
            public string Arcs = string.Empty;
            public GripperDrive Drives;
        }

        // What scene.usda authors for each gripper, and so through which arc its values arrive.
        // The panel shows the composed result next to it.
        private static readonly Dictionary<string, string> SceneArcs = new Dictionary<string, string>
        {
            { "/World/gripper_a", "Authors nothing: the asset's defaults. Its pads are relocated to fingertip_*, and its finger stiffness comes from the class override in scene_tuning.usda (inherits)." },
            { "/World/gripper_b", "preset = linkage locally, pads = smooth from scene_tuning.usda (sublayer). Restitution 0.05 from the override of elastomer_base (specializes)." },
            { "/World/gripper_c", "Inherits _class_linkage_grippers, whose mechanism = linkage beats the asset's preset." },
            { "/World/gripper_d", "References presets/linkage_smooth.usda, which pins mechanism and pads." },
        };

        private static readonly string[] AssetLabels = { "v1 prismatic", "v2 linkage", "v3 gripper", "v3 scene" };

        /// <summary>The asset the panel imports, and for v3 gripper the preset.</summary>
        public GripperAsset Asset = GripperAsset.V3Scene;
        public string Preset = "linkage";

        /// <summary>Seconds for a full open, close, open cycle; 0 leaves <see cref="Closing"/> in charge.</summary>
        public float CycleSeconds = 4f;

        /// <summary>The closing fraction set with the slider while not cycling.</summary>
        public float Closing;

        /// <summary>A line about the last import, or why it failed.</summary>
        public string Status = string.Empty;

        /// <summary>True while an import runs: the asset controls are disabled.</summary>
        public bool Busy;

        private readonly List<GripperSummary> grippers = new List<GripperSummary>();
        private IReadOnlyList<string> notes = Array.Empty<string>();
        private GUISkin stylesSkin;
        private GUIStyle richLabel;
        private GUIStyle wrapLabel;
        private GUIStyle richWrapLabel;

        public IReadOnlyList<GripperSummary> Grippers => grippers;

        public void Clear()
        {
            grippers.Clear();
            notes = Array.Empty<string>();
        }

        /// <summary>
        /// Puts a <see cref="GripperDrive"/> on every gripper of an import and reads what composed:
        /// the variant selections from <paramref name="source"/>, the articulation, the finger drive
        /// and the pad material from the objects the import built.
        /// </summary>
        public void Summarize(GameObject root, UsdStageSource source, IReadOnlyList<string> physicsNotes)
        {
            Clear();
            notes = physicsNotes ?? Array.Empty<string>();
            List<GripperDrive> drives = ComposedGripperImport.AddDrives(root);
            drives.Sort((a, b) => string.CompareOrdinal(a.GripperPrimPath, b.GripperPrimPath));
            foreach (GripperDrive drive in drives)
            {
                drive.CycleSeconds = CycleSeconds;
                drive.Closing = Closing;
                var summary = new GripperSummary { PrimPath = drive.GripperPrimPath, Drives = drive };
                SceneArcs.TryGetValue(summary.PrimPath, out summary.Arcs);

                // The selections the stage composed, whichever arc supplied them.
                var selections = new StringBuilder();
                if (source != null)
                {
                    foreach (UsdVariantSetInfo set in source.VariantSets)
                    {
                        if (set.PrimPath == summary.PrimPath)
                        {
                            selections.Append(selections.Length > 0 ? "  " : string.Empty).Append(set.Name).Append('=').Append(set.Selection);
                        }
                    }
                }

                summary.Selections = selections.ToString();
                summary.Links = drive.GetComponentsInChildren<ArticulationBody>().Length;

                // The pads are the links whose colliders carry a pad material; their names show
                // a relocation (gripper_a's pads arrive as fingertip_left / fingertip_right).
                var pads = new SortedSet<string>();
                var padLinks = new SortedSet<string>();
                foreach (MeshCollider collider in drive.GetComponentsInChildren<MeshCollider>())
                {
                    summary.Colliders++;
                    PhysicsMaterial material = collider.sharedMaterial;
                    if (material != null && material.name.StartsWith("pad", StringComparison.Ordinal))
                    {
                        pads.Add($"{material.name} μs {material.staticFriction:0.##} μd {material.dynamicFriction:0.##} e {material.bounciness:0.##}");
                        ArticulationBody link = collider.GetComponentInParent<ArticulationBody>();
                        if (link != null)
                        {
                            padLinks.Add(link.name);
                        }
                    }
                }

                summary.Pads = string.Join("\n", pads) + (padLinks.Count > 0 ? "\non " + string.Join(", ", padLinks) : string.Empty);

                if (drive.Joints.Count > 0)
                {
                    UsdPhysicsJoint joint = drive.Joints[0];
                    string unit = joint.IsAngular ? "/deg" : "/m";
                    summary.Drive = $"{drive.Joints.Count} driven {joint.UnityType} joints; {joint.name}: " +
                                    $"k {joint.DriveStiffness:0.###}{unit}, c {joint.DriveDamping:0.###}{unit}";
                }

                grippers.Add(summary);
            }

            Status = $"{ComposedGripperImport.RelativePath(Asset)}: {grippers.Count} articulation(s), {notes.Count} physics note(s).";
        }

        /// <summary>
        /// Draws the panel in the current GUILayout area. Returns true when the asset or preset
        /// was changed, or Import again pressed: import <see cref="Asset"/> / <see cref="Preset"/> then.
        /// </summary>
        public bool Draw()
        {
            BuildStyles();
            bool import = false;

            GUI.enabled = !Busy;
            int asset = GUILayout.Toolbar((int)Asset, AssetLabels);
            if (asset != (int)Asset)
            {
                Asset = (GripperAsset)asset;
                import = true;
            }

            if (Asset == GripperAsset.V3Gripper)
            {
                GUILayout.Label("preset (UsdImportOptions.VariantSelections)");
                int index = Array.IndexOf(ComposedGripperImport.Presets, Preset);
                int preset = GUILayout.Toolbar(Mathf.Max(0, index), ComposedGripperImport.Presets);
                if (preset != index)
                {
                    Preset = ComposedGripperImport.Presets[preset];
                    import = true;
                }
            }

            import |= GUILayout.Button("Import again");
            GUI.enabled = true;
            GUILayout.Label(Status, wrapLabel);

            GUILayout.Space(6f);
            float cycleBefore = CycleSeconds;
            float closingBefore = Closing;
            bool cycle = GUILayout.Toggle(CycleSeconds > 0f, $"Cycle open / closed every {Mathf.Max(CycleSeconds, 4f):0.#} s");
            CycleSeconds = cycle ? Mathf.Max(CycleSeconds, 4f) : 0f;
            if (!cycle)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("Closing", GUILayout.Width(60f));
                Closing = GUILayout.HorizontalSlider(Closing, 0f, 1f);
                GUILayout.Label(Closing.ToString("0.00"), GUILayout.Width(36f));
                GUILayout.EndHorizontal();
            }

            // Only when the controls change, so a GripperDrive edited in the Inspector keeps its values.
            if (CycleSeconds != cycleBefore || Closing != closingBefore)
            {
                foreach (GripperSummary gripper in grippers)
                {
                    if (gripper.Drives != null)
                    {
                        gripper.Drives.CycleSeconds = CycleSeconds;
                        if (!cycle)
                        {
                            gripper.Drives.Closing = Closing;
                        }
                    }
                }
            }

            foreach (GripperSummary gripper in grippers)
            {
                GUILayout.Space(6f);
                GUILayout.BeginVertical(GUI.skin.box);
                string closed = gripper.Drives != null ? $"  (closed {gripper.Drives.MeasuredClosing():0.00})" : string.Empty;
                GUILayout.Label($"<b>{gripper.PrimPath}</b>{closed}", richLabel);
                if (!string.IsNullOrEmpty(gripper.Arcs))
                {
                    GUILayout.Label($"<i>{gripper.Arcs}</i>", richWrapLabel);
                }

                GUILayout.Label(gripper.Selections, wrapLabel);
                GUILayout.Label($"{gripper.Links} ArticulationBodies, {gripper.Colliders} convex MeshColliders", wrapLabel);
                GUILayout.Label(gripper.Drive, wrapLabel);
                GUILayout.Label(gripper.Pads, wrapLabel);
                GUILayout.EndVertical();
            }

            if (notes.Count > 0)
            {
                GUILayout.Space(6f);
                GUILayout.Label($"<b>Physics notes ({notes.Count})</b>", richLabel);
                foreach (string note in notes)
                {
                    GUILayout.Label("• " + note, wrapLabel);
                }
            }

            return import;
        }

        /// <summary>A bold title in the panel's label style.</summary>
        public void DrawTitle(string title)
        {
            BuildStyles();
            GUILayout.Label($"<b>{title}</b>", richLabel);
        }

        // The Game view and an editor window draw with different skins, so the styles follow the
        // skin in use.
        private void BuildStyles()
        {
            if (stylesSkin == GUI.skin && richLabel != null)
            {
                return;
            }

            stylesSkin = GUI.skin;
            richLabel = new GUIStyle(GUI.skin.label) { richText = true };
            wrapLabel = new GUIStyle(GUI.skin.label) { wordWrap = true };
            richWrapLabel = new GUIStyle(GUI.skin.label) { wordWrap = true, richText = true };
        }
    }
}
