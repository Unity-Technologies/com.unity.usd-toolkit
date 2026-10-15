using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Unity.USDToolkit.RuntimeDemo.Editor
{
    /// <summary>
    /// Writes the materials under Resources/RuntimeShaders that keep the demo's runtime shaders in
    /// a player build.
    /// </summary>
    /// <remarks>
    /// The demo builds itself at runtime and looks every shader up with Shader.Find. A build only
    /// contains shaders that something in it references, so in a player those lookups returned null
    /// and the very first material creation threw in Awake, before the HUD was built. Everything
    /// under Resources ships unconditionally, so a material there is the reference.
    ///
    /// URP also strips keyword variants no built material uses. The toolkit's importer creates
    /// URP/Lit materials with _NORMALMAP, _METALLICSPECGLOSSMAP, _EMISSION, _ALPHATEST_ON and
    /// _SURFACE_TYPE_TRANSPARENT at runtime, so every combination it can
    /// produce, recorded in a ShaderVariantCollection; otherwise an imported glass or emissive part
    /// renders with the wrong variant. This is used instead of Always Included Shaders, which would
    /// compile every URP/Lit variant.
    /// </remarks>
    public static class RuntimeShaderIncludes
    {
        private static string Folder => DemoSamplePaths.RuntimeShadersFolder;

        private static readonly string[] PlainShaders =
        {
            "Universal Render Pipeline/Unlit",
            "USDRuntimeDemo/GizmoHandle",
            "USDRuntimeDemo/SelectionOutline",
            "USDRuntimeDemo/SelectionMask"
        };

        [MenuItem("Tools/USD Runtime Demo/Regenerate Runtime Shader Includes")]
        public static void Regenerate()
        {
            Directory.CreateDirectory(Folder);
            var written = new List<string>();

            foreach (string shaderName in PlainShaders)
            {
                Shader shader = Shader.Find(shaderName);
                if (shader == null)
                {
                    Debug.LogError($"Runtime shader includes: shader '{shaderName}' not found.");
                    continue;
                }

                written.Add(Write(new Material(shader), shaderName.Replace('/', '_')));
            }

            Shader lit = Shader.Find("Universal Render Pipeline/Lit");
            if (lit == null)
            {
                Debug.LogError("Runtime shader includes: URP/Lit not found.");
            }
            else
            {
                written.Add(Write(new Material(lit), "URP_Lit"));
                WriteLitVariants(lit);
            }

            // Remove materials from an earlier run that this one no longer writes.
            foreach (string guid in AssetDatabase.FindAssets("t:Material", new[] { Folder }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!written.Contains(path))
                {
                    AssetDatabase.DeleteAsset(path);
                }
            }

            AssetDatabase.SaveAssets();
            Debug.Log($"Runtime shader includes: {written.Count} material(s) in {Folder}.");
        }

        // The exact keyword sets UsdImporter puts on runtime URP/Lit materials. A material asset
        // cannot carry them: URP re-validates keywords on save and drops a map keyword whose
        // texture is unassigned. A variant collection records them as written.
        private static void WriteLitVariants(Shader lit)
        {
            string[][] surfaces =
            {
                new string[0],
                new[] { "_ALPHATEST_ON" },
                new[] { "_SURFACE_TYPE_TRANSPARENT" }
            };
            string[] optional = { "_NORMALMAP", "_METALLICSPECGLOSSMAP", "_EMISSION" };
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
                            // That pass does not declare one of these keywords (e.g. the shadow
                            // caster has no _EMISSION); its relevant subset is added on its own.
                        }
                    }
                }
            }

            string path = $"{Folder}/URP_Lit_ImporterVariants.shadervariants";
            AssetDatabase.DeleteAsset(path);
            AssetDatabase.CreateAsset(collection, path);
            Debug.Log($"Runtime shader includes: {added} URP/Lit variant(s) in {path}.");
        }

        private static string Write(Material material, string name)
        {
            string path = $"{Folder}/{name}.mat";
            material.name = name;
            Material existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing != null)
            {
                EditorUtility.CopySerialized(material, existing);
                Object.DestroyImmediate(material);
            }
            else
            {
                AssetDatabase.CreateAsset(material, path);
            }

            return path;
        }
    }
}
