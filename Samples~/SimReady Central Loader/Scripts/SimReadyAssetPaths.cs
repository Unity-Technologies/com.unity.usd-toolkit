using System;
using System.Collections.Generic;
using System.IO;

namespace Unity.USDToolkit.Samples.SimReadyCentral
{
    /// <summary>
    /// Resolving and checking the asset paths a layer authors, before anything is fetched: each
    /// is taken relative to the layer that names it and must stay inside the asset's folder of the
    /// repo; absolute paths, other schemes and anything that climbs out are refused. Only data
    /// files are fetched (USD layers, images, MDL source); nothing executable.
    /// </summary>
    public static class SimReadyAssetPaths
    {
        /// <summary>Layer formats: these are scanned for what they reference.</summary>
        public static readonly string[] LayerExtensions = { ".usd", ".usda", ".usdc" };

        /// <summary>Everything that may be fetched. MDL is shader source, read as data, never compiled here.</summary>
        public static readonly string[] DataExtensions =
        {
            ".usd", ".usda", ".usdc",
            ".png", ".jpg", ".jpeg", ".exr", ".hdr", ".tga", ".tif", ".tiff", ".bmp", ".ktx2",
            ".mdl",
            ".json", ".txt",
        };

        public static bool IsUsdLayer(string path) => HasExtension(path, LayerExtensions);

        public static bool IsAllowedData(string path) => HasExtension(path, DataExtensions);

        /// <summary>One path segment that may appear in a repo path: no traversal, no separators or control characters.</summary>
        public static bool IsSafeSegment(string segment)
        {
            if (string.IsNullOrEmpty(segment) || segment == "." || segment == ".." || segment.Length > 255)
            {
                return false;
            }

            foreach (char c in segment)
            {
                if (c < 0x20 || c == 0x7f || c == '/' || c == '\\' || c == ':' || c == '*' || c == '?' || c == '"' ||
                    c == '<' || c == '>' || c == '|')
                {
                    return false;
                }
            }

            // Trailing dots and spaces are dropped by Windows, which would alias another file.
            return !segment.EndsWith(".", StringComparison.Ordinal) && !segment.EndsWith(" ", StringComparison.Ordinal);
        }

        /// <summary>
        /// Resolves <paramref name="authored"/> (as a layer at <paramref name="layerRepoPath"/>
        /// writes it) to a repo path inside <paramref name="packageFolder"/>. False with the reason
        /// when it is absolute, has a scheme, is a template, or leaves the folder.
        /// </summary>
        public static bool TryResolve(string layerRepoPath, string authored, string packageFolder, out string repoPath, out string error)
        {
            repoPath = null;
            error = null;
            string path = (authored ?? string.Empty).Trim();
            if (path.Length == 0)
            {
                error = "empty asset path";
                return false;
            }

            if (path.IndexOf('<') >= 0 || path.IndexOf('>') >= 0)
            {
                error = "template asset paths (<UDIM>, clip templates) are not fetched";
                return false;
            }

            // USD package-relative paths name a file inside a .usdz: not used by SimReady assets.
            if (path.IndexOf('[') >= 0)
            {
                error = "package-relative paths are not fetched";
                return false;
            }

            if (path.StartsWith("/", StringComparison.Ordinal) || path.StartsWith("\\", StringComparison.Ordinal) ||
                (path.Length >= 2 && path[1] == ':') || path.IndexOf("://", StringComparison.Ordinal) >= 0 ||
                path.StartsWith("omniverse:", StringComparison.OrdinalIgnoreCase) || path.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            {
                error = "absolute paths and other locations are not fetched";
                return false;
            }

            if (path.IndexOf('\\') >= 0)
            {
                error = "backslash-separated paths are not fetched";
                return false;
            }

            int slash = layerRepoPath.LastIndexOf('/');
            var segments = new List<string>(slash > 0 ? layerRepoPath.Substring(0, slash).Split('/') : Array.Empty<string>());
            foreach (string part in path.Split('/'))
            {
                if (part.Length == 0 || part == ".")
                {
                    continue;
                }

                if (part == "..")
                {
                    if (segments.Count == 0)
                    {
                        error = "climbs above the repo";
                        return false;
                    }

                    segments.RemoveAt(segments.Count - 1);
                    continue;
                }

                if (!IsSafeSegment(part))
                {
                    error = $"the segment \"{part}\" is not allowed";
                    return false;
                }

                segments.Add(part);
            }

            if (segments.Count == 0)
            {
                error = "names a folder, not a file";
                return false;
            }

            string resolved = string.Join("/", segments);
            if (!IsInside(resolved, packageFolder))
            {
                error = $"leaves the asset's folder ({packageFolder}/)";
                return false;
            }

            if (!IsAllowedData(resolved))
            {
                error = $"\"{Path.GetExtension(resolved)}\" is not a data type this sample fetches";
                return false;
            }

            repoPath = resolved;
            return true;
        }

        /// <summary>The repo path lies in <paramref name="folder"/> (or the folder is the repo root, "").</summary>
        public static bool IsInside(string repoPath, string folder)
        {
            if (string.IsNullOrEmpty(folder))
            {
                return true;
            }

            return repoPath.StartsWith(folder.TrimEnd('/') + "/", StringComparison.Ordinal);
        }

        /// <summary>
        /// The local file for a repo path under <paramref name="root"/>, checked again on disk: the
        /// full path must stay inside the root after the OS has normalised it.
        /// </summary>
        public static string LocalPath(string root, string repoPath)
        {
            string full = Path.GetFullPath(Path.Combine(root, repoPath.Replace('/', Path.DirectorySeparatorChar)));
            string rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(rootFull, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Refusing a path outside the cache: {repoPath}");
            }

            return full;
        }

        /// <summary>
        /// The first bytes look like a program (PE, ELF, Mach-O, a script): a data file never does,
        /// so such a download is deleted however it is named.
        /// </summary>
        public static bool LooksExecutable(byte[] head, int length)
        {
            if (head == null || length < 2)
            {
                return false;
            }

            if (head[0] == 'M' && head[1] == 'Z')
            {
                return true; // PE (Windows)
            }

            if (head[0] == '#' && head[1] == '!')
            {
                return true; // script
            }

            if (length >= 4)
            {
                uint magic = (uint)(head[0] | (head[1] << 8) | (head[2] << 16) | (head[3] << 24));
                if ((head[0] == 0x7f && head[1] == 'E' && head[2] == 'L' && head[3] == 'F') ||
                    magic == 0xfeedfaceu || magic == 0xfeedfacfu || magic == 0xcefaedfeu || magic == 0xcffaedfeu ||
                    magic == 0xcafebabeu || magic == 0xbebafecau)
                {
                    return true; // ELF, Mach-O (32/64, both byte orders), universal binary
                }
            }

            return false;
        }

        private static bool HasExtension(string path, string[] extensions)
        {
            string extension = Path.GetExtension(path ?? string.Empty).ToLowerInvariant();
            return Array.IndexOf(extensions, extension) >= 0;
        }
    }
}
