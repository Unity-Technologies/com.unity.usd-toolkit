using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Unity.USDToolkit.RuntimeDemo
{
    public enum UsdExportMode
    {
        /// <summary>
        /// Compose the original source layers in by reference. Small file, source stays the
        /// authority. Authored by <see cref="UsdReferenceLayerWriter"/>, not by the toolkit.
        /// </summary>
        Referencing = 0,

        /// <summary>
        /// Bake every mesh into one self-contained stage via the toolkit's exporter.
        /// </summary>
        Flattening = 1
    }

    public enum UsdExportFormat
    {
        /// <summary>Binary crate. The customer-facing default.</summary>
        Usdc = 0,

        /// <summary>ASCII. Debug fallback, and the only option for a referencing layer.</summary>
        Usda = 1,

        /// <summary>
        /// Binary crate under the generic .usd extension, for pipelines that expect that name.
        /// OpenUSD picks crate for a new .usd file, so the bytes match .usdc.
        /// </summary>
        Usd = 2
    }

    public sealed class UsdExportOutcome
    {
        public bool Success;
        public string Message;
        public string OutputPath;
        public long ByteCount;
        public int MeshCount;
        public int ReferenceCount;
        public readonly List<string> Warnings = new List<string>();
    }

    public sealed class UsdExportService
    {
        private readonly Transform sceneRoot;

        public UsdExportService(Transform sceneRoot)
        {
            this.sceneRoot = sceneRoot;
        }

        /// <summary>
        /// A referencing layer has to be ASCII: the toolkit's binary writer has no way to author a
        /// composition arc, so the demo writes that layer itself and .usda is the text format.
        /// </summary>
        public static UsdExportFormat EffectiveFormat(UsdExportMode mode, UsdExportFormat requested)
        {
            return mode == UsdExportMode.Referencing ? UsdExportFormat.Usda : requested;
        }

        public static string Extension(UsdExportFormat format)
        {
            switch (format)
            {
                case UsdExportFormat.Usda:
                    return ".usda";
                case UsdExportFormat.Usd:
                    return ".usd";
                default:
                    return ".usdc";
            }
        }

        public static string SuggestedFileName(UsdExportMode mode, UsdExportFormat format)
        {
            string stem = mode == UsdExportMode.Referencing ? "assembled_referencing" : "assembled_flattened";
            return stem + Extension(EffectiveFormat(mode, format));
        }

        public UsdExportOutcome Export(
            string outputPath,
            UsdExportMode mode,
            IReadOnlyList<UsdSceneEntry> entries,
            bool useRelativeReferencePaths)
        {
            var outcome = new UsdExportOutcome { OutputPath = outputPath };

            if (entries == null || entries.Count == 0)
            {
                outcome.Success = false;
                outcome.Message = "Export skipped: nothing is loaded yet.";
                return outcome;
            }

            if (string.IsNullOrWhiteSpace(outputPath))
            {
                outcome.Success = false;
                outcome.Message = "Export cancelled.";
                return outcome;
            }

            try
            {
                return mode == UsdExportMode.Referencing
                    ? ExportReferencing(outputPath, entries, useRelativeReferencePaths)
                    : ExportFlattened(outputPath, entries);
            }
            catch (Exception exception)
            {
                outcome.Success = false;
                outcome.Message = DescribeExportFailure(exception, Path.GetFileName(outputPath));
                return outcome;
            }
        }

        private UsdExportOutcome ExportReferencing(
            string outputPath,
            IReadOnlyList<UsdSceneEntry> entries,
            bool useRelativePaths)
        {
            UsdReferenceLayerWriter.Report report = UsdReferenceLayerWriter.Write(
                outputPath,
                entries,
                new UsdReferenceLayerWriter.Options
                {
                    RootPrimName = "UsdRuntimeScene",
                    UseRelativePaths = useRelativePaths
                });

            var outcome = new UsdExportOutcome
            {
                Success = true,
                OutputPath = report.OutputPath,
                ByteCount = report.ByteCount,
                ReferenceCount = report.ReferenceCount,
                Message = $"Exported {report.ReferenceCount} reference(s) → {Path.GetFileName(report.OutputPath)} " +
                          $"({FormatBytes(report.ByteCount)}, sources stay live)"
            };
            outcome.Warnings.AddRange(report.Warnings);
            return outcome;
        }

        private UsdExportOutcome ExportFlattened(string outputPath, IReadOnlyList<UsdSceneEntry> entries)
        {
            var options = new UsdExportOptions
            {
                RootPrimName = "UsdRuntimeScene",
                // Unity works in metres; the toolkit writes this straight to stage metadata and
                // never scales geometry with it, so anything but 1 would mislabel the stage.
                MetersPerUnit = 1.0f,
                IncludeInactive = false,
                ExportNormals = true,
                ExportUv0 = true,
                ExportBounds = true,
                TransformPolicy = UsdTransformPolicy.BakedMesh,
                // Materials/shading round-trip is out of scope for this demo.
                ExportTextures = false,
                LogExportSummary = false
            };

            UsdExportResult result = UsdExporter.ExportGameObjectWithResult(sceneRoot.gameObject, outputPath, options);

            long bytes = File.Exists(result.OutputPath) ? new FileInfo(result.OutputPath).Length : 0L;
            return new UsdExportOutcome
            {
                Success = true,
                OutputPath = result.OutputPath,
                ByteCount = bytes,
                MeshCount = result.MeshCount,
                Message = $"Exported {result.MeshCount} mesh(es), {result.TriangleCount:N0} tris → " +
                          $"{Path.GetFileName(result.OutputPath)} ({FormatBytes(bytes)}, self-contained)"
            };
        }

        public static string DescribeExportFailure(Exception exception, string fileName)
        {
            switch (exception)
            {
                case null:
                    return $"Export failed: {fileName}";
                case UsdExportException:
                    return $"Export failed ({fileName}): {FirstLine(exception.Message)}";
                case DllNotFoundException:
                    return "Export failed: USD native plugin not loaded for this platform.";
                case UnauthorizedAccessException:
                    return $"Export failed: no permission to write {fileName}.";
                case DirectoryNotFoundException:
                    return $"Export failed: destination folder for {fileName} does not exist.";
                case IOException:
                    return $"Export failed: could not write {fileName}. {FirstLine(exception.Message)}";
                case InvalidOperationException:
                    return $"Export failed: {FirstLine(exception.Message)}";
                default:
                    return $"Export failed ({fileName}): {FirstLine(exception.Message)}";
            }
        }

        private static string FirstLine(string message)
        {
            if (string.IsNullOrEmpty(message))
            {
                return "unknown error";
            }

            int breakIndex = message.IndexOfAny(new[] { '\r', '\n' });
            string line = breakIndex < 0 ? message : message.Substring(0, breakIndex);
            return line.Length > 160 ? line.Substring(0, 157) + "..." : line;
        }

        public static string FormatBytes(long bytes)
        {
            if (bytes < 1024)
            {
                return bytes + " B";
            }

            if (bytes < 1024 * 1024)
            {
                return (bytes / 1024.0).ToString("0.#") + " KB";
            }

            return (bytes / (1024.0 * 1024.0)).ToString("0.#") + " MB";
        }
    }
}
