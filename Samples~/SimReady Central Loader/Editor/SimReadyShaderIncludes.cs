using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.Rendering;

namespace Unity.USDToolkit.Samples.SimReadyCentral.Editor
{
    /// <summary>
    /// Writes Resources/SimReadyShaders/SimReady_URP_Lit_ImporterVariants.shadervariants, which keeps
    /// the URP/Lit keyword variants the importer uses in a player build.
    /// </summary>
    /// <remarks>
    /// The importer creates URP/Lit materials at runtime and enables _NORMALMAP,
    /// _METALLICSPECGLOSSMAP, _OCCLUSIONMAP, _EMISSION, _ALPHATEST_ON and
    /// _SURFACE_TYPE_TRANSPARENT on them. A build strips every keyword variant that no material in
    /// it uses, and a player then falls back to the closest variant it has: imported parts lost
    /// their normal, metallic and occlusion maps, glass rendered opaque, and emitters went dark,
    /// although the same asset looked right in the Editor. Anything under Resources ships, so the
    /// collection there keeps every combination the importer can produce. That is used instead of
    /// Always Included Shaders, which would compile every URP/Lit variant.
    /// </remarks>
    public static class SimReadyShaderIncludes
    {
        private const string RuntimeAssemblyName = "Unity.USDToolkit.Samples.SimReadyCentral";
        private const string CollectionName = "SimReady_URP_Lit_ImporterVariants";

        [MenuItem("Tools/SimReady Central/Regenerate Shader Variants")]
        public static void Regenerate()
        {
            Shader lit = Shader.Find("Universal Render Pipeline/Lit");
            if (lit == null)
            {
                Debug.LogError("SimReady shader variants: URP/Lit not found. The collection is only needed for URP.");
                return;
            }

            string folder = $"{SampleRoot()}/Resources/SimReadyShaders";
            Directory.CreateDirectory(folder);

            // The exact keyword sets UsdImporter.AssembleMaterial and ApplyOpacity put on a URP/Lit
            // material. Cutout and transparent never come together: blending clears the cutout.
            string[][] surfaces =
            {
                new string[0],
                new[] { "_ALPHATEST_ON" },
                new[] { "_SURFACE_TYPE_TRANSPARENT" }
            };
            string[] optional = { "_NORMALMAP", "_METALLICSPECGLOSSMAP", "_OCCLUSIONMAP", "_EMISSION" };
            PassType[] passes = { PassType.ScriptableRenderPipeline, PassType.ShadowCaster };

            var collection = new ShaderVariantCollection();
            int added = 0;
            foreach (string[] surface in surfaces)
            {
                for (int mask = 0; mask < 1 << optional.Length; mask++)
                {
                    var keywords = new List<string>(surface);
                    for (int i = 0; i < optional.Length; i++)
                    {
                        if ((mask & (1 << i)) != 0)
                        {
                            keywords.Add(optional[i]);
                        }
                    }

                    foreach (PassType pass in passes)
                    {
                        try
                        {
                            if (collection.Add(new ShaderVariantCollection.ShaderVariant(lit, pass, keywords.ToArray())))
                            {
                                added++;
                            }
                        }
                        catch (System.ArgumentException)
                        {
                            // The shadow caster declares only _ALPHATEST_ON of these; its subset is
                            // added by the combinations without the others.
                        }
                    }
                }
            }

            string path = $"{folder}/{CollectionName}.shadervariants";
            AssetDatabase.DeleteAsset(path);
            AssetDatabase.CreateAsset(collection, path);
            AssetDatabase.SaveAssets();
            Debug.Log($"SimReady shader variants: {added} URP/Lit variant(s) in {path}.");
        }

        // The Package Manager imports a sample under Assets/Samples/<package>/<version>/ and it may
        // be moved afterwards; the runtime assembly definition sits in Scripts/, one level down.
        private static string SampleRoot()
        {
            foreach (string guid in AssetDatabase.FindAssets($"{RuntimeAssemblyName} t:{nameof(AssemblyDefinitionAsset)}"))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (Path.GetFileNameWithoutExtension(path) == RuntimeAssemblyName)
                {
                    return Path.GetDirectoryName(Path.GetDirectoryName(path)).Replace('\\', '/');
                }
            }

            throw new FileNotFoundException($"The {RuntimeAssemblyName} assembly definition was not found in the project.");
        }
    }
}
