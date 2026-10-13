using System.IO;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace Unity.USDToolkit
{
    /// <summary>
    /// Which file on disk an imported texture's pixels came from, when they are that file's
    /// pixels and nothing else.
    /// </summary>
    /// <remarks>
    /// An export used to read every texture back from the GPU and re-encode it as PNG. Once the
    /// import block-compresses textures that is a lossy copy of a lossless file, and for an EXR
    /// it is 8 bits of a 16-bit image: a round trip through Unity degraded what it touched. The
    /// importer records the source here for textures it uploaded as decoded -- not for an ORM map
    /// whose channels it moved, nor for a metallic and roughness pair it packed into one -- and the
    /// exporter copies that file instead. Entries go with the texture: the table holds weak keys.
    /// </remarks>
    internal static class UsdTextureOrigins
    {
        private static readonly ConditionalWeakTable<Texture, string> Origins =
            new ConditionalWeakTable<Texture, string>();

        internal static void Record(Texture texture, string path)
        {
            if (texture == null || string.IsNullOrEmpty(path))
            {
                return;
            }

            Origins.Remove(texture);
            Origins.Add(texture, path);
        }

        /// <summary>The file the texture was decoded from, if it still exists; else null.</summary>
        internal static string Lookup(Texture texture)
        {
            if (texture == null || !Origins.TryGetValue(texture, out string path))
            {
                return null;
            }

            return File.Exists(path) ? path : null;
        }
    }
}
