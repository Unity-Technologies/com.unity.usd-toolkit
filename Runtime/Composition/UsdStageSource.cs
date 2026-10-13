using System;
using System.Collections.Generic;
using UnityEngine;

namespace Unity.USDToolkit
{
    /// <summary>
    /// Sits on the root object an import creates and records the stage it came from: the file,
    /// its metadata, the variant and payload choices the import composed with, and what the
    /// stage offers. A <see cref="UsdCompositionMode.Preserve"/> export reads it to write the
    /// object back as overrides on that file instead of copying it.
    ///
    /// The root object stands for the stage's pseudo-root; its children are the stage's root
    /// prims. Each object below carries a <see cref="UsdPrimSource"/>.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("")]
    public sealed class UsdStageSource : MonoBehaviour
    {
        [SerializeField] private string sourcePath;
        [SerializeField] private string defaultPrimPath;
        [SerializeField] private double metersPerUnit = 1.0;
        [SerializeField] private int upAxis = 1;
        [SerializeField] private Quaternion stageCorrectionRotation = Quaternion.identity;
        [SerializeField] private float stageCorrectionScale = 1f;
        [SerializeField] private bool payloadsLoaded = true;
        [SerializeField] private List<string> payloadsToLoad = new List<string>();
        [SerializeField] private List<UsdVariantSelection> variantSelections = new List<UsdVariantSelection>();
        [SerializeField] private List<UsdVariantSetInfo> variantSets = new List<UsdVariantSetInfo>();
        [SerializeField] private List<UsdPayloadInfo> payloads = new List<UsdPayloadInfo>();
        [SerializeField] private List<UsdCompositionArcInfo> arcs = new List<UsdCompositionArcInfo>();
        [SerializeField] private List<string> primPaths = new List<string>();

        /// <summary>Absolute path of the imported file.</summary>
        public string SourcePath => sourcePath;

        /// <summary>The stage's defaultPrim ("/World"), or empty when it has none.</summary>
        public string DefaultPrimPath => defaultPrimPath;

        public double MetersPerUnit => metersPerUnit;

        /// <summary>"Y" or "Z".</summary>
        public string UpAxis => upAxis == 2 ? "Z" : "Y";

        /// <summary>
        /// The up-axis rotation the import put on this root's local rotation
        /// (UsdImportOptions.ApplyStageUpAxisAndUnits). Identity when none was applied.
        /// </summary>
        public Quaternion StageCorrectionRotation => stageCorrectionRotation;

        /// <summary>
        /// The unit scale (metersPerUnit) the import put on this root's local scale. 1 when none
        /// was applied.
        /// </summary>
        public float StageCorrectionScale => stageCorrectionScale;

        /// <summary>False when the import left payloads unloaded (UsdImportOptions.LoadPayloads).</summary>
        public bool PayloadsLoaded => payloadsLoaded;

        /// <summary>Payloads loaded individually while the rest were left unloaded.</summary>
        public IReadOnlyList<string> PayloadsToLoad => payloadsToLoad;

        /// <summary>The selections the import applied (UsdImportOptions.VariantSelections).</summary>
        public IReadOnlyList<UsdVariantSelection> VariantSelections => variantSelections;

        /// <summary>Every variant set on the stage, with the selection composed. Empty on native API 5.</summary>
        public IReadOnlyList<UsdVariantSetInfo> VariantSets => variantSets;

        /// <summary>Every prim with a payload, and whether it was loaded. Empty on native API 5.</summary>
        public IReadOnlyList<UsdPayloadInfo> Payloads => payloads;

        /// <summary>Direct references, payloads, inherits, specializes and relocates. Empty on native API 5.</summary>
        public IReadOnlyList<UsdCompositionArcInfo> Arcs => arcs;

        /// <summary>The prim path of every object the import created, in creation order.</summary>
        public IReadOnlyList<string> PrimPaths => primPaths;

        internal int UpAxisCode => upAxis;

        /// <summary>
        /// Import options that reproduce this import with one more variant selection (replacing
        /// any earlier choice for the same prim and set). Re-import with them to switch a variant:
        /// <c>UsdImporter.Import(source.SourcePath, source.CreateOptionsWithVariant(...))</c>.
        /// </summary>
        public UsdImportOptions CreateOptionsWithVariant(string primPath, string variantSet, string variant)
        {
            var options = new UsdImportOptions
            {
                LoadPayloads = payloadsLoaded,
                PayloadsToLoad = new List<string>(payloadsToLoad)
            };

            foreach (UsdVariantSelection selection in variantSelections)
            {
                if (selection.PrimPath != primPath || selection.VariantSet != variantSet)
                {
                    options.VariantSelections.Add(new UsdVariantSelection(selection.PrimPath, selection.VariantSet, selection.Variant));
                }
            }

            options.VariantSelections.Add(new UsdVariantSelection(primPath, variantSet, variant));
            return options;
        }

        internal void Initialize(
            string path,
            string defaultPrim,
            double unit,
            int axis,
            UsdImportOptions options)
        {
            sourcePath = path;
            defaultPrimPath = defaultPrim ?? string.Empty;
            metersPerUnit = unit;
            upAxis = axis;
            payloadsLoaded = options.LoadPayloads;
            payloadsToLoad = new List<string>(options.PayloadsToLoad ?? new List<string>());
            variantSelections = new List<UsdVariantSelection>();
            foreach (UsdVariantSelection selection in options.VariantSelections ?? new List<UsdVariantSelection>())
            {
                if (selection != null && !string.IsNullOrEmpty(selection.PrimPath) && !string.IsNullOrEmpty(selection.VariantSet))
                {
                    variantSelections.Add(new UsdVariantSelection(selection.PrimPath, selection.VariantSet, selection.Variant));
                }
            }
        }

        internal void SetStageCorrection(Quaternion rotation, float scale)
        {
            stageCorrectionRotation = rotation;
            stageCorrectionScale = scale;
        }

        internal void SetComposition(
            List<UsdVariantSetInfo> sets,
            List<UsdPayloadInfo> payloadInfos,
            List<UsdCompositionArcInfo> arcInfos)
        {
            variantSets = sets;
            payloads = payloadInfos;
            arcs = arcInfos;
        }

        internal void AddPrimPath(string primPath)
        {
            primPaths.Add(primPath);
        }

        // The selection the import composed for a set, or null.
        internal string GetComposedSelection(string primPath, string variantSet)
        {
            foreach (UsdVariantSetInfo info in variantSets)
            {
                if (info.PrimPath == primPath && info.Name == variantSet)
                {
                    return info.Selection;
                }
            }

            return null;
        }

        // Parses RUsd_GetImportComposition's report (see the native header).
        internal static void ParseCompositionReport(
            string report,
            List<UsdVariantSetInfo> sets,
            List<UsdPayloadInfo> payloadInfos,
            List<UsdCompositionArcInfo> arcInfos)
        {
            if (string.IsNullOrEmpty(report))
            {
                return;
            }

            foreach (string line in report.Split('\n'))
            {
                string[] fields = line.TrimEnd('\r').Split('\t');
                if (fields.Length < 2)
                {
                    continue;
                }

                switch (fields[0])
                {
                    case "V" when fields.Length >= 4:
                        var set = new UsdVariantSetInfo { PrimPath = fields[1], Name = fields[2], Selection = fields[3] };
                        for (int i = 4; i < fields.Length; i++)
                        {
                            set.Variants.Add(fields[i]);
                        }

                        sets.Add(set);
                        break;
                    case "P" when fields.Length >= 3:
                        payloadInfos.Add(new UsdPayloadInfo { PrimPath = fields[1], Loaded = fields[2] == "1" });
                        break;
                    case "A" when fields.Length >= 5:
                        arcInfos.Add(new UsdCompositionArcInfo
                        {
                            PrimPath = fields[1],
                            Kind = fields[2],
                            Layer = fields[3],
                            TargetPrimPath = fields[4]
                        });
                        break;
                }
            }
        }
    }
}
