using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace Unity.USDToolkit.RuntimeDemo
{
    /// <summary>
    /// Writes a USD layer that composes the original source files in by reference instead of
    /// baking their geometry.
    /// </summary>
    /// <remarks>
    /// com.unity.usd-toolkit 0.7.2-exp.1 cannot do this. Its whole export surface is
    /// <c>RUsd_BeginExport</c> / <c>RUsd_AddMesh</c> / <c>RUsd_AddXform</c> / <c>RUsd_EndExport</c>
    /// with no entry point for a reference, payload or sublayer arc, so every toolkit export is a
    /// flatten by construction. A referencing layer is small and fully specified though, so the
    /// demo authors it directly: the result is ordinary USD that any DCC or PLM viewer composes
    /// the same way it would composed one written by Houdini or usdcat.
    ///
    /// The consequence is that referencing mode emits .usda. That is not an ASCII dump of
    /// geometry, it is a couple of kilobytes of pointers and transforms, which is exactly the
    /// artifact you want to show a PLM audience.
    /// </remarks>
    public static class UsdReferenceLayerWriter
    {
        public sealed class Options
        {
            public string RootPrimName = "UsdRuntimeScene";

            /// <summary>
            /// Relative asset paths keep the layer portable as a set (move the layer and its
            /// sources together and it still composes). Absolute paths survive moving the layer
            /// alone but pin it to this machine.
            /// </summary>
            public bool UseRelativePaths = true;
        }

        public sealed class Report
        {
            public string OutputPath;
            public int ReferenceCount;
            public long ByteCount;
            public readonly List<string> AssetPaths = new List<string>();
            public readonly List<string> Warnings = new List<string>();
        }

        public static Report Write(string outputPath, IReadOnlyList<UsdSceneEntry> entries, Options options = null)
        {
            if (string.IsNullOrWhiteSpace(outputPath))
            {
                throw new ArgumentException("Output path is empty.", nameof(outputPath));
            }

            if (entries == null || entries.Count == 0)
            {
                throw new InvalidOperationException("Nothing to export: no USD files are loaded.");
            }

            options ??= new Options();

            string fullOutputPath = Path.GetFullPath(outputPath);
            string outputDirectory = Path.GetDirectoryName(fullOutputPath);
            if (!string.IsNullOrEmpty(outputDirectory))
            {
                Directory.CreateDirectory(outputDirectory);
            }

            var report = new Report { OutputPath = fullOutputPath };
            string rootPrim = SanitizeIdentifier(options.RootPrimName, "UsdRuntimeScene");

            var builder = new StringBuilder();
            builder.AppendLine("#usda 1.0");
            builder.AppendLine("(");
            builder.AppendLine($"    defaultPrim = \"{rootPrim}\"");
            builder.AppendLine("    metersPerUnit = 1");
            builder.AppendLine("    upAxis = \"Y\"");
            builder.AppendLine("    doc = \"\"\"Assembled in Unity at runtime by the USD Toolkit demo.");
            builder.AppendLine("Geometry is composed in by reference; the source layers remain the authority.\"\"\"");
            builder.AppendLine(")");
            builder.AppendLine();
            builder.AppendLine($"def Xform \"{rootPrim}\"");
            builder.AppendLine("{");

            var usedNames = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < entries.Count; i++)
            {
                UsdSceneEntry entry = entries[i];
                string assetPath = ResolveAssetPath(entry.SourcePath, outputDirectory, options.UseRelativePaths);
                string primName = MakeUnique(
                    SanitizeIdentifier(Path.GetFileNameWithoutExtension(entry.SourcePath), "Asset"),
                    usedNames);

                if (!File.Exists(entry.SourcePath))
                {
                    report.Warnings.Add($"{entry.DisplayName} is no longer on disk; the reference will not resolve.");
                }

                report.AssetPaths.Add(assetPath);
                report.ReferenceCount++;

                string reference = FormatAssetPath(assetPath);
                // Targeting the prim explicitly rather than leaning on the source's defaultPrim:
                // plenty of real pipeline exports never set one, and a bare @file@ reference is
                // silently empty when that happens.
                if (!string.IsNullOrEmpty(entry.DefaultPrimPath) && entry.DefaultPrimPath.StartsWith("/", StringComparison.Ordinal))
                {
                    reference += "<" + entry.DefaultPrimPath + ">";
                }

                builder.AppendLine();
                builder.AppendLine($"    def Xform \"{primName}\" (");
                builder.AppendLine($"        prepend references = {reference}");
                builder.AppendLine("    )");
                builder.AppendLine("    {");
                builder.AppendLine($"        matrix4d xformOp:transform = {FormatMatrix(entry.SourceToWorld)}");
                builder.AppendLine("        uniform token[] xformOpOrder = [\"xformOp:transform\"]");
                builder.AppendLine("    }");
            }

            builder.AppendLine("}");
            builder.AppendLine();

            string contents = builder.ToString();
            File.WriteAllText(fullOutputPath, contents, new UTF8Encoding(false));
            report.ByteCount = new FileInfo(fullOutputPath).Length;
            return report;
        }

        private static string ResolveAssetPath(string sourcePath, string outputDirectory, bool relative)
        {
            string fullSource = Path.GetFullPath(sourcePath);
            if (!relative || string.IsNullOrEmpty(outputDirectory))
            {
                return fullSource.Replace('\\', '/');
            }

            string result;
            try
            {
                result = Path.GetRelativePath(outputDirectory, fullSource);
            }
            catch (Exception)
            {
                // Different volume on Windows, or an otherwise unrelatable pair of paths.
                return fullSource.Replace('\\', '/');
            }

            result = result.Replace('\\', '/');

            // USD treats a path with no leading "./" or "../" as search-path relative, which
            // resolves against the resolver's search paths rather than the layer. Anchor it.
            if (!result.StartsWith("./", StringComparison.Ordinal) &&
                !result.StartsWith("../", StringComparison.Ordinal))
            {
                result = "./" + result;
            }

            return result;
        }

        private static string FormatAssetPath(string assetPath)
        {
            // An "@" inside the path would terminate the literal early; USD's triple-delimited
            // form exists for exactly this.
            return assetPath.IndexOf('@') >= 0
                ? "@@@" + assetPath + "@@@"
                : "@" + assetPath + "@";
        }

        /// <summary>
        /// Unity (column-vector, left-handed) to USD (row-vector, right-handed).
        /// </summary>
        /// <remarks>
        /// The toolkit's importer defines the forward direction as
        /// <c>M_unity = B * transpose(M_usd) * B</c> with <c>B = diag(-1, 1, 1)</c>. B is its own
        /// inverse, so going back is <c>M_usd = transpose(B * M_unity * B)</c>, which in elements
        /// is <c>usd[r][c] = sign(r) * sign(c) * unity[c, r]</c>. Matching the toolkit's own
        /// convention exactly is what makes export then re-import land where it started.
        /// </remarks>
        public static string FormatMatrix(Matrix4x4 unity)
        {
            var builder = new StringBuilder("( ");
            for (int r = 0; r < 4; r++)
            {
                float sr = r == 0 ? -1.0f : 1.0f;
                builder.Append('(');
                for (int c = 0; c < 4; c++)
                {
                    float sc = c == 0 ? -1.0f : 1.0f;
                    double value = sr * sc * unity[c, r];
                    if (c > 0)
                    {
                        builder.Append(", ");
                    }

                    builder.Append(Round(value).ToString("0.#########", CultureInfo.InvariantCulture));
                }

                builder.Append(')');
                if (r < 3)
                {
                    builder.Append(", ");
                }
            }

            builder.Append(" )");
            return builder.ToString();
        }

        private static double Round(double value)
        {
            // Keeps "-0" and 1e-17 dust out of a file a customer may well read on screen.
            double rounded = Math.Round(value, 9, MidpointRounding.AwayFromZero);
            return rounded == 0.0 ? 0.0 : rounded;
        }

        private static string SanitizeIdentifier(string value, string fallback)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return fallback;
            }

            var builder = new StringBuilder(value.Length);
            foreach (char c in value)
            {
                builder.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
            }

            string result = builder.ToString();
            if (result.Length == 0 || char.IsDigit(result[0]))
            {
                result = "_" + result;
            }

            return result;
        }

        private static string MakeUnique(string candidate, HashSet<string> used)
        {
            string name = candidate;
            int suffix = 1;
            while (!used.Add(name))
            {
                name = candidate + "_" + suffix.ToString(CultureInfo.InvariantCulture);
                suffix++;
            }

            return name;
        }
    }
}
