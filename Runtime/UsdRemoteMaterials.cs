using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using UnityEngine;

namespace Unity.USDToolkit
{
    // Downloads remote MDL modules a stage names by URL, and the textures they reference, into the
    // folder the native layer reads them from (RUsd_SetRemoteAssetCache). NVIDIA's material library
    // is the case this exists for: Isaac Sim stages reference
    // https://omniverse-content-production.s3.us-west-2.amazonaws.com/Materials/.../Base/... modules,
    // which Isaac Sim fetches and the importer could not, so those surfaces came in default grey.
    //
    // Only HTTPS URLs on NVIDIA's content hosts are fetched, only .mdl modules and the image files
    // their texture_2d(...) literals name beside them (no "..", no absolute or remote paths), each
    // within a size limit. Everything lands under <cache>/<host>/<path>; a file already there is not
    // fetched again.
    internal static class UsdRemoteMaterials
    {
        private static readonly string[] AllowedHosts =
        {
            "omniverse-content-production.s3.us-west-2.amazonaws.com",
            "omniverse-content-production.s3-us-west-2.amazonaws.com",
        };

        private static readonly string[] TextureExtensions = { ".png", ".jpg", ".jpeg", ".tga", ".exr", ".hdr", ".bmp" };

        private const long MaxModuleBytes = 1L << 20;
        private const long MaxTextureBytes = 64L << 20;

        private static readonly Regex TextureLiteral = new Regex("texture_2d\\(\\s*\"([^\"]+)\"", RegexOptions.CultureInvariant);

        private static readonly HttpClient Client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };

        // The default cache folder, under the application's temporary cache.
        internal static string DefaultCacheFolder =>
            Path.Combine(Application.temporaryCachePath, "UsdRemoteMaterials");

        // Fetches each requested module (and its textures) that is allowed and not cached yet.
        // Returns how many modules are now in the cache that were not before; failures are logged
        // and skipped, so the import goes on with whatever arrived.
        internal static int Fetch(IEnumerable<string> urls, string cacheFolder)
        {
            if (string.IsNullOrEmpty(cacheFolder))
            {
                return 0;
            }

            int fetched = 0;
            foreach (string url in urls)
            {
                if (!TryGetCachePath(url, cacheFolder, out Uri uri, out string file) ||
                    !uri.AbsolutePath.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))
                {
                    Debug.LogWarning($"USD import: remote material {url} is not on an allowed host or is not an .mdl module; it is not downloaded.");
                    continue;
                }

                if (File.Exists(file))
                {
                    continue;
                }

                if (!Download(uri, file, MaxModuleBytes))
                {
                    continue;
                }

                fetched++;
                string text = File.ReadAllText(file);
                foreach (Match match in TextureLiteral.Matches(text))
                {
                    string relative = match.Groups[1].Value;
                    if (relative.StartsWith("./", StringComparison.Ordinal))
                    {
                        relative = relative.Substring(2);
                    }

                    if (relative.Length == 0 || relative.Contains("..") || relative.Contains(":") ||
                        relative.StartsWith("/", StringComparison.Ordinal) ||
                        Array.IndexOf(TextureExtensions, Path.GetExtension(relative).ToLowerInvariant()) < 0)
                    {
                        continue;
                    }

                    var textureUri = new Uri(uri, relative);
                    if (TryGetCachePath(textureUri.AbsoluteUri, cacheFolder, out Uri allowed, out string textureFile) &&
                        !File.Exists(textureFile))
                    {
                        Download(allowed, textureFile, MaxTextureBytes);
                    }
                }
            }

            return fetched;
        }

        // The cache file for an allowed URL, the same mapping the native layer uses:
        // <cache>/<host>/<path as written>.
        internal static bool TryGetCachePath(string url, string cacheFolder, out Uri uri, out string file)
        {
            file = null;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri) || uri.Scheme != Uri.UriSchemeHttps ||
                Array.IndexOf(AllowedHosts, uri.Host.ToLowerInvariant()) < 0)
            {
                return false;
            }

            string path = uri.AbsolutePath.TrimStart('/');
            foreach (string part in path.Split('/'))
            {
                if (part.Length == 0 || part == "." || part == ".." || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                {
                    return false;
                }
            }

            string root = Path.GetFullPath(cacheFolder);
            file = Path.GetFullPath(Path.Combine(root, uri.Host, path));
            return file.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                                   StringComparison.OrdinalIgnoreCase);
        }

        private static bool Download(Uri uri, string file, long maxBytes)
        {
            try
            {
                using (HttpResponseMessage response = Client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead)
                           .GetAwaiter().GetResult())
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        Debug.LogWarning($"USD import: downloading {uri} failed ({(int)response.StatusCode}).");
                        return false;
                    }

                    long? length = response.Content.Headers.ContentLength;
                    if (length.HasValue && length.Value > maxBytes)
                    {
                        Debug.LogWarning($"USD import: {uri} is {length.Value} bytes, over the {maxBytes}-byte limit; not downloaded.");
                        return false;
                    }

                    byte[] bytes = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                    if (bytes.LongLength > maxBytes)
                    {
                        return false;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(file));
                    string partial = file + ".part";
                    File.WriteAllBytes(partial, bytes);
                    if (File.Exists(file))
                    {
                        File.Delete(file);
                    }

                    File.Move(partial, file);
                    return true;
                }
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"USD import: downloading {uri} failed: {exception.Message}");
                return false;
            }
        }
    }
}
