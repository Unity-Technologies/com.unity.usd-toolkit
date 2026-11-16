using System;
using System.Collections.Generic;
using System.Globalization;
using Unity.USDToolkit.Native;

namespace Unity.USDToolkit
{
    /// <summary>
    /// What one layer file authors, read without composing a stage: the asset paths it names
    /// (sublayers, references, payloads in every variant, asset-valued attributes such as textures
    /// and MDL modules), its layer metadata and its <c>customLayerData</c>. Only the one file is
    /// opened; nothing it names is read, so this is how a caller finds what else to fetch before a
    /// stage can open. Paths are as authored, relative to the layer. Needs the 0.8.2 native plugin
    /// (<see cref="IsSupported"/>).
    /// </summary>
    public sealed class UsdLayerInfo
    {
        private static readonly IReadOnlyDictionary<string, string> Empty = new Dictionary<string, string>();

        /// <summary>The loaded native plugin can read a layer (<see cref="Read"/>).</summary>
        public static bool IsSupported
        {
            get
            {
                if (!UsdNative.IsSupportedPlatform)
                {
                    return false;
                }

                // Asking the plugin its version loads OpenUSD, which reads its plugin search path
                // once, as it loads (Plug_InitConfig): the path has to be set before that.
                UsdExporter.EnsureRuntimeConfigured();
                return UsdNative.SupportsLayerInspection;
            }
        }

        /// <summary>The file <see cref="Read"/> opened.</summary>
        public string LayerPath { get; private set; } = string.Empty;

        /// <summary>subLayers, as authored.</summary>
        public IReadOnlyList<string> SubLayers { get; private set; } = Array.Empty<string>();

        /// <summary>References, and asset-valued attributes (textures, MDL modules), as authored.</summary>
        public IReadOnlyList<string> References { get; private set; } = Array.Empty<string>();

        /// <summary>Payloads, those of every variant included, as authored.</summary>
        public IReadOnlyList<string> Payloads { get; private set; } = Array.Empty<string>();

        /// <summary>The layer's upAxis ("Y", "Z"); empty when it authors none.</summary>
        public string UpAxis { get; private set; } = string.Empty;

        /// <summary>The layer's metersPerUnit; null when it authors none.</summary>
        public double? MetersPerUnit { get; private set; }

        /// <summary>The layer's kilogramsPerUnit; null when it authors none.</summary>
        public double? KilogramsPerUnit { get; private set; }

        /// <summary>The layer's defaultPrim name; empty when it authors none.</summary>
        public string DefaultPrim { get; private set; } = string.Empty;

        /// <summary>
        /// customLayerData, nested dictionaries flattened to "outer:inner" keys. Strings come back as
        /// they are; other values in USD's text form ("(0.15, 0.07, 0.2)", "14").
        /// </summary>
        public IReadOnlyDictionary<string, string> CustomLayerData { get; private set; } = Empty;

        /// <summary>Every asset path the layer names, sublayers first, each once.</summary>
        public IReadOnlyList<string> AllAssetPaths
        {
            get
            {
                var all = new List<string>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (IReadOnlyList<string> list in new[] { SubLayers, References, Payloads })
                {
                    foreach (string path in list)
                    {
                        if (seen.Add(path))
                        {
                            all.Add(path);
                        }
                    }
                }

                return all;
            }
        }

        /// <summary>
        /// Opens <paramref name="layerPath"/> on its own and reports what it authors. Throws
        /// <see cref="NotSupportedException"/> on a plugin older than 0.8.2 and
        /// <see cref="UsdStageException"/> when the file cannot be read as a layer.
        /// </summary>
        public static UsdLayerInfo Read(string layerPath)
        {
            if (string.IsNullOrEmpty(layerPath))
            {
                throw new ArgumentException("A layer path is required.", nameof(layerPath));
            }

            // OpenUSD aborts the process if it resolves an asset before its plugin path is set
            // (and the payload is checked against its digests first).
            UsdExporter.EnsureRuntimeConfigured();
            if (!IsSupported)
            {
                throw new NotSupportedException(
                    $"Reading a layer on its own needs {UsdNative.NeedsPlugin(UsdNative.LayerInspectionApiVersion)}; {UsdNative.LoadedPluginDescription}.");
            }

            string report;
            try
            {
                report = UsdNative.InspectLayer(layerPath);
            }
            catch (UsdExportException exception)
            {
                throw new UsdStageException(exception.Message);
            }

            UsdLayerInfo info = Parse(report);
            info.LayerPath = layerPath;
            return info;
        }

        // RUsd_InspectLayer's report: one record per line, tab-separated (see the native header).
        internal static UsdLayerInfo Parse(string report)
        {
            var subLayers = new List<string>();
            var references = new List<string>();
            var payloads = new List<string>();
            var custom = new Dictionary<string, string>(StringComparer.Ordinal);
            var info = new UsdLayerInfo();
            foreach (string line in (report ?? string.Empty).Split('\n'))
            {
                string[] fields = line.TrimEnd('\r').Split('\t');
                if (fields.Length < 2 || fields[0].Length != 1)
                {
                    continue;
                }

                switch (fields[0][0])
                {
                    case 'S':
                        subLayers.Add(fields[1]);
                        break;
                    case 'R':
                        references.Add(fields[1]);
                        break;
                    case 'P':
                        payloads.Add(fields[1]);
                        break;
                    case 'M' when fields.Length >= 3:
                        info.SetMetadata(fields[1], fields[2]);
                        break;
                    case 'C' when fields.Length >= 3:
                        custom[fields[1]] = fields[2];
                        break;
                }
            }

            info.SubLayers = subLayers;
            info.References = references;
            info.Payloads = payloads;
            info.CustomLayerData = custom;
            return info;
        }

        private void SetMetadata(string field, string value)
        {
            switch (field)
            {
                case "upAxis":
                    UpAxis = value;
                    break;
                case "metersPerUnit":
                    MetersPerUnit = ParseDouble(value);
                    break;
                case "kilogramsPerUnit":
                    KilogramsPerUnit = ParseDouble(value);
                    break;
                case "defaultPrim":
                    DefaultPrim = value;
                    break;
            }
        }

        private static double? ParseDouble(string value) =>
            double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) ? parsed : (double?)null;
    }
}
