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
    ///
    /// Each source gets a wrapper Xform that carries the placement, with the reference one level
    /// below it. Authoring the placement on the referencing prim itself would replace the source
    /// defaultPrim's own xformOpOrder, and with it any transform the source puts on that prim.
    ///
    /// A stage takes its time metadata from its root layer only, so the sources' time range
    /// would otherwise be lost: the layer carries the timeCodesPerSecond and framesPerSecond of the
    /// first source with a time range and the union of the sources' ranges, converted to that rate.
    /// Sources are read through <see cref="UsdStage"/>, in any format (see
    /// <see cref="UsdLayerTimeMetadata"/>).
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
            AppendTimeMetadata(builder, entries);
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

                // The placement goes on a wrapper so the referenced prim keeps its own xformOps.
                // The referenced prim is typeless so it takes its type from the source.
                string referencedName = SanitizeIdentifier(
                    entry.DefaultPrimPath?.Substring(entry.DefaultPrimPath.LastIndexOf('/') + 1),
                    primName);

                builder.AppendLine();
                builder.AppendLine($"    def Xform \"{primName}\"");
                builder.AppendLine("    {");
                builder.AppendLine($"        matrix4d xformOp:transform = {FormatMatrix(entry.SourceToWorld)}");
                builder.AppendLine("        uniform token[] xformOpOrder = [\"xformOp:transform\"]");
                builder.AppendLine();
                builder.AppendLine($"        def \"{referencedName}\" (");
                builder.AppendLine($"            prepend references = {reference}");
                builder.AppendLine("        )");
                builder.AppendLine("        {");
                builder.AppendLine("        }");
                builder.AppendLine("    }");
            }

            builder.AppendLine("}");
            builder.AppendLine();

            string contents = builder.ToString();
            File.WriteAllText(fullOutputPath, contents, new UTF8Encoding(false));
            report.ByteCount = new FileInfo(fullOutputPath).Length;
            return report;
        }

        /// <summary>
        /// Writes startTimeCode / endTimeCode (the union of the sources' ranges) and
        /// timeCodesPerSecond / framesPerSecond (those of the first source with a time range).
        /// A source without a time range adds nothing; when no source has one, nothing is written.
        /// </summary>
        /// <remarks>
        /// USD scales a referenced layer's time samples by the root's time codes per second over
        /// the source's, so each source's range is converted into the root's rate before the union:
        /// a 30 tcps source with 0-30 covers 0-24 under a 24 tcps root.
        ///
        /// The stage API reports effective rates only, not whether a layer authors them, so both
        /// rates are written explicitly. A source that authors only framesPerSecond = 12 gives
        /// timeCodesPerSecond = 12 as well, which is the rate USD already runs it at.
        /// </remarks>
        private static void AppendTimeMetadata(StringBuilder builder, IReadOnlyList<UsdSceneEntry> entries)
        {
            var sources = new List<UsdLayerTimeMetadata>();
            var read = new HashSet<string>(StringComparer.Ordinal);
            foreach (UsdSceneEntry entry in entries)
            {
                if (!File.Exists(entry.SourcePath) || !read.Add(Path.GetFullPath(entry.SourcePath)))
                {
                    continue;
                }

                UsdLayerTimeMetadata time;
                try
                {
                    time = UsdLayerTimeMetadata.Read(entry.SourcePath);
                }
                catch (NotSupportedException exception)
                {
                    // The loaded native plugin has no stage API, so no source can be read.
                    Debug.LogWarning($"Referencing export: the sources' time range is not copied. {exception.Message}");
                    return;
                }
                catch (Exception exception) when (exception is UsdExportException || exception is UsdStageException ||
                                                  exception is IOException)
                {
                    Debug.LogWarning($"Referencing export: could not open {entry.DisplayName} to read its time range, so it adds none. {exception.Message}");
                    continue;
                }

                if (time.HasTimeRange)
                {
                    sources.Add(time);
                }
            }

            if (sources.Count == 0)
            {
                return;
            }

            double rootRate = sources[0].TimeCodesPerSecond;
            double start = double.MaxValue, end = double.MinValue;
            foreach (UsdLayerTimeMetadata time in sources)
            {
                double scale = rootRate / time.TimeCodesPerSecond;
                start = Math.Min(start, Round(time.StartTimeCode * scale));
                end = Math.Max(end, Round(time.EndTimeCode * scale));
            }

            AppendNumber(builder, "startTimeCode", start);
            AppendNumber(builder, "endTimeCode", end);
            AppendNumber(builder, "timeCodesPerSecond", sources[0].TimeCodesPerSecond);
            AppendNumber(builder, "framesPerSecond", sources[0].FramesPerSecond);
        }

        private static void AppendNumber(StringBuilder builder, string key, double value)
        {
            builder.AppendLine($"    {key} = {FormatNumber(value)}");
        }

        private static string FormatNumber(double value)
        {
            return value.ToString("R", CultureInfo.InvariantCulture);
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
