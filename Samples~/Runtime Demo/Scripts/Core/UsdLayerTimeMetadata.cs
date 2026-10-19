using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Unity.USDToolkit.RuntimeDemo
{
    /// <summary>
    /// The time metadata a source layer authors: startTimeCode, endTimeCode, timeCodesPerSecond
    /// and framesPerSecond. A key the layer does not author stays null.
    /// </summary>
    /// <remarks>
    /// The toolkit's import reports a stage's up axis and units but not its time metadata, so the
    /// demo reads it from the layer header itself. That only works for text: a <c>.usda</c> file,
    /// or a <c>.usd</c> file that holds text. Binary crate files (<c>.usdc</c>, most <c>.usd</c>)
    /// and <c>.usdz</c> packages are reported as <see cref="IsBinary"/> with no values.
    /// </remarks>
    public readonly struct UsdLayerTimeMetadata
    {
        private static readonly Regex Entry = new Regex(
            @"\b(startTimeCode|endTimeCode|timeCodesPerSecond|framesPerSecond)\s*=\s*([-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?)",
            RegexOptions.CultureInvariant);

        public readonly bool IsBinary;
        public readonly double? StartTimeCode;
        public readonly double? EndTimeCode;
        public readonly double? TimeCodesPerSecond;
        public readonly double? FramesPerSecond;

        private UsdLayerTimeMetadata(bool isBinary, double? start, double? end, double? timeCodesPerSecond, double? framesPerSecond)
        {
            IsBinary = isBinary;
            StartTimeCode = start;
            EndTimeCode = end;
            TimeCodesPerSecond = timeCodesPerSecond;
            FramesPerSecond = framesPerSecond;
        }

        public bool HasAny => StartTimeCode.HasValue || EndTimeCode.HasValue ||
                              TimeCodesPerSecond.HasValue || FramesPerSecond.HasValue;

        /// <summary>The rate USD runs this layer's time codes at.</summary>
        public double EffectiveRate => EffectiveTimeCodesPerSecond(TimeCodesPerSecond, FramesPerSecond);

        /// <summary>
        /// USD's rule for a layer's time codes per second: timeCodesPerSecond when authored,
        /// otherwise framesPerSecond when authored, otherwise 24.
        /// </summary>
        public static double EffectiveTimeCodesPerSecond(double? timeCodesPerSecond, double? framesPerSecond)
        {
            return timeCodesPerSecond ?? framesPerSecond ?? 24.0;
        }

        /// <summary>
        /// Reads the layer metadata block of a text layer: the parenthesised block right after the
        /// <c>#usda</c> line. Only that block is read, never the prims below it, so a large file
        /// costs no more than a small one.
        /// </summary>
        public static UsdLayerTimeMetadata Read(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                // Text layers open with "#usda"; a crate opens with "PXR-USDC" and a package with "PK".
                var magic = new byte[5];
                if (stream.Read(magic, 0, magic.Length) != magic.Length || Encoding.ASCII.GetString(magic) != "#usda")
                {
                    return new UsdLayerTimeMetadata(true, null, null, null, null);
                }

                stream.Position = 0;
                using var reader = new StreamReader(stream, Encoding.UTF8, false);
                reader.ReadLine();
                string block = ReadTopLevelMetadata(reader);
                double? start = null, end = null, timeCodesPerSecond = null, framesPerSecond = null;
                foreach (Match match in Entry.Matches(block))
                {
                    double value = double.Parse(match.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture);
                    switch (match.Groups[1].Value)
                    {
                        case "startTimeCode":
                            start = value;
                            break;
                        case "endTimeCode":
                            end = value;
                            break;
                        case "timeCodesPerSecond":
                            timeCodesPerSecond = value;
                            break;
                        default:
                            framesPerSecond = value;
                            break;
                    }
                }

                return new UsdLayerTimeMetadata(false, start, end, timeCodesPerSecond, framesPerSecond);
            }
        }

        /// <summary>
        /// Returns the top level of the layer's metadata block with everything nested (dictionaries,
        /// lists, sublayer offsets) and every string, asset path and comment blanked out, so a key of
        /// the same name inside customLayerData or a doc string cannot be mistaken for the real one.
        /// Empty when the layer has no metadata block.
        /// </summary>
        private static string ReadTopLevelMetadata(StreamReader reader)
        {
            var top = new StringBuilder();
            int depth = 0;
            int c;
            while ((c = reader.Read()) >= 0)
            {
                char ch = (char)c;
                if (ch == '#')
                {
                    reader.ReadLine();
                    top.Append('\n');
                    continue;
                }

                if (ch == '"' || ch == '\'')
                {
                    SkipString(reader, ch);
                    top.Append(' ');
                    continue;
                }

                if (ch == '@')
                {
                    SkipAssetPath(reader);
                    top.Append(' ');
                    continue;
                }

                if (depth == 0)
                {
                    if (char.IsWhiteSpace(ch))
                    {
                        continue;
                    }

                    if (ch != '(')
                    {
                        // The first token is a prim, not a metadata block.
                        return string.Empty;
                    }

                    depth = 1;
                    continue;
                }

                if (ch == '(' || ch == '[' || ch == '{')
                {
                    depth++;
                }
                else if (ch == ')' || ch == ']' || ch == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        return top.ToString();
                    }
                }
                else if (depth == 1)
                {
                    top.Append(ch);
                }
            }

            return top.ToString();
        }

        // Single- or triple-quoted, with backslash escapes. The opening quote is already consumed.
        private static void SkipString(StreamReader reader, char quote)
        {
            bool triple = false;
            if (reader.Peek() == quote)
            {
                reader.Read();
                if (reader.Peek() != quote)
                {
                    return; // empty string
                }

                reader.Read();
                triple = true;
            }

            int run = 0;
            int c;
            while ((c = reader.Read()) >= 0)
            {
                if (c == '\\')
                {
                    reader.Read();
                    run = 0;
                    continue;
                }

                if (c != quote)
                {
                    run = 0;
                    continue;
                }

                if (!triple || ++run == 3)
                {
                    return;
                }
            }
        }

        // @path@ or @@@path@@@. The first '@' is already consumed.
        private static void SkipAssetPath(StreamReader reader)
        {
            bool triple = false;
            if (reader.Peek() == '@')
            {
                reader.Read();
                if (reader.Peek() != '@')
                {
                    return; // empty asset path
                }

                reader.Read();
                triple = true;
            }

            int run = 0;
            int c;
            while ((c = reader.Read()) >= 0)
            {
                if (c != '@')
                {
                    run = 0;
                    continue;
                }

                if (!triple || ++run == 3)
                {
                    return;
                }
            }
        }
    }
}
