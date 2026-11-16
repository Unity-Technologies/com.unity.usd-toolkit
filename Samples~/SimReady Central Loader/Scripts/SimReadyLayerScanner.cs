using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Unity.USDToolkit.Samples.SimReadyCentral
{
    /// <summary>What one layer names, and whether that list is all of it.</summary>
    public sealed class SimReadyLayerScan
    {
        public readonly List<string> AssetPaths = new List<string>();

        /// <summary>Layer metadata and customLayerData (flattened "outer:inner" keys), when readable.</summary>
        public readonly Dictionary<string, string> CustomLayerData = new Dictionary<string, string>(StringComparer.Ordinal);

        public string UpAxis = string.Empty;
        public double? MetersPerUnit;
        public double? KilogramsPerUnit;

        /// <summary>Every asset path is listed (false for a binary layer without the 0.8.2 native plugin).</summary>
        public bool Complete = true;

        public string Reason = string.Empty;
    }

    /// <summary>
    /// Lists the asset paths a layer authors, without composing it. With the 0.8.2 native plugin
    /// that is <see cref="UsdLayerInfo"/>, which reads text and binary (crate) layers alike and
    /// sees payloads in every variant. Without it, a text layer is scanned for its <c>@path@</c>
    /// asset paths and its customLayerData; a binary one cannot be, and says so.
    /// </summary>
    public static class SimReadyLayerScanner
    {
        // @path@ (or @@@path@@@ with '@' inside), as usda writes asset paths.
        private static readonly Regex AssetPath = new Regex("@@@(.+?)@@@|@([^@\\r\\n]+)@", RegexOptions.CultureInvariant);

        /// <summary>Use the native reader when the plugin has it. Off in tests of the fallback.</summary>
        public static bool PreferNative = true;

        public static SimReadyLayerScan Scan(string localPath)
        {
            if (PreferNative && UsdLayerInfo.IsSupported)
            {
                UsdLayerInfo info = UsdLayerInfo.Read(localPath);
                var scan = new SimReadyLayerScan
                {
                    UpAxis = info.UpAxis,
                    MetersPerUnit = info.MetersPerUnit,
                    KilogramsPerUnit = info.KilogramsPerUnit,
                };
                scan.AssetPaths.AddRange(info.AllAssetPaths);
                foreach (KeyValuePair<string, string> entry in info.CustomLayerData)
                {
                    scan.CustomLayerData[entry.Key] = entry.Value;
                }

                return scan;
            }

            if (!IsText(localPath))
            {
                return new SimReadyLayerScan
                {
                    Complete = false,
                    Reason = $"{Path.GetFileName(localPath)} is a binary layer, which only the 0.8.2 native plugin reads on its own.",
                };
            }

            return ScanText(File.ReadAllText(localPath, Encoding.UTF8));
        }

        /// <summary>A usda layer's asset paths and customLayerData, read from its text.</summary>
        public static SimReadyLayerScan ScanText(string text)
        {
            var scan = new SimReadyLayerScan();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match match in AssetPath.Matches(StripComments(text)))
            {
                string path = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
                if (path.Length > 0 && seen.Add(path))
                {
                    scan.AssetPaths.Add(path);
                }
            }

            ReadHeader(text, scan);
            return scan;
        }

        private static bool IsText(string path)
        {
            var head = new byte[8];
            int read;
            using (FileStream stream = File.OpenRead(path))
            {
                read = stream.Read(head, 0, head.Length);
            }

            return read >= 5 && Encoding.ASCII.GetString(head, 0, 5) == "#usda";
        }

        // '#' comments to the end of the line, outside strings and asset paths.
        private static string StripComments(string text)
        {
            var builder = new StringBuilder(text.Length);
            bool inString = false;
            bool inAsset = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (!inString && c == '@')
                {
                    inAsset = !inAsset;
                }
                else if (!inAsset && c == '"' && (i == 0 || text[i - 1] != '\\'))
                {
                    inString = !inString;
                }
                else if (!inString && !inAsset && c == '#' && i > 0)
                {
                    while (i < text.Length && text[i] != '\n')
                    {
                        i++;
                    }

                    builder.Append('\n');
                    continue;
                }

                builder.Append(c);
            }

            return builder.ToString();
        }

        // ---- The layer header: upAxis, metersPerUnit, kilogramsPerUnit and customLayerData ----

        private static readonly Regex Scalar = new Regex(
            "^\\s*(?:(?:uniform|custom)\\s+)?(?<type>[A-Za-z0-9\\[\\]]+)\\s+(?<key>[A-Za-z_][A-Za-z0-9_:]*)\\s*=\\s*(?<value>.+?)\\s*$",
            RegexOptions.CultureInvariant);

        private static readonly Regex Dictionary = new Regex("^\\s*dictionary\\s+(?<key>[A-Za-z_][A-Za-z0-9_]*)\\s*=\\s*\\{\\s*$",
                                                             RegexOptions.CultureInvariant);

        private static void ReadHeader(string text, SimReadyLayerScan scan)
        {
            // The layer's metadata is the first parenthesised block after the #usda line.
            int open = text.IndexOf('(');
            int prim = text.IndexOf("\ndef ", StringComparison.Ordinal);
            if (open < 0 || (prim >= 0 && open > prim))
            {
                return;
            }

            var scopes = new Stack<string>();
            bool inCustom = false;
            foreach (string raw in text.Substring(open + 1).Split('\n'))
            {
                string line = raw.Trim();
                if (line.StartsWith(")", StringComparison.Ordinal) && scopes.Count == 0 && !inCustom)
                {
                    break;
                }

                if (!inCustom)
                {
                    if (line.StartsWith("customLayerData", StringComparison.Ordinal) && line.EndsWith("{", StringComparison.Ordinal))
                    {
                        inCustom = true;
                        continue;
                    }

                    ReadLayerField(line, scan);
                    continue;
                }

                if (line == "}")
                {
                    if (scopes.Count == 0)
                    {
                        inCustom = false;
                    }
                    else
                    {
                        scopes.Pop();
                    }

                    continue;
                }

                Match dictionary = Dictionary.Match(line);
                if (dictionary.Success)
                {
                    scopes.Push(dictionary.Groups["key"].Value);
                    continue;
                }

                Match scalar = Scalar.Match(line);
                if (scalar.Success)
                {
                    var keys = new List<string>(scopes);
                    keys.Reverse();
                    keys.Add(scalar.Groups["key"].Value);
                    scan.CustomLayerData[string.Join(":", keys)] = Unquote(scalar.Groups["value"].Value);
                }
            }
        }

        private static void ReadLayerField(string line, SimReadyLayerScan scan)
        {
            int equals = line.IndexOf('=');
            if (equals < 0)
            {
                return;
            }

            string key = line.Substring(0, equals).Trim();
            string value = Unquote(line.Substring(equals + 1).Trim());
            switch (key)
            {
                case "upAxis":
                    scan.UpAxis = value;
                    break;
                case "metersPerUnit":
                    scan.MetersPerUnit = ParseDouble(value);
                    break;
                case "kilogramsPerUnit":
                    scan.KilogramsPerUnit = ParseDouble(value);
                    break;
            }
        }

        private static string Unquote(string value)
        {
            value = value.Trim();
            return value.Length >= 2 && value[0] == '"' && value[value.Length - 1] == '"' ? value.Substring(1, value.Length - 2) : value;
        }

        private static double? ParseDouble(string value) =>
            double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double parsed)
                ? parsed
                : (double?)null;
    }
}
