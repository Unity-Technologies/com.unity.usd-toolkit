using System.IO;
using UnityEditor;
using UnityEditorInternal;

namespace Unity.USDToolkit.RuntimeDemo.Editor
{
    /// <summary>
    /// Locates the demo's folder in the project.
    /// </summary>
    /// <remarks>
    /// The Package Manager imports a sample under Assets/Samples/&lt;package&gt;/&lt;version&gt;/,
    /// and the user may move it afterwards, so no path is hardcoded. The runtime assembly
    /// definition sits in Scripts/, one level below the root.
    /// </remarks>
    public static class DemoSamplePaths
    {
        private const string RuntimeAssemblyName = "Unity.USDToolkit.Samples.RuntimeDemo";

        public static string Root
        {
            get
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

        public static string PrefabPath => $"{Root}/Prefabs/DemoHud.prefab";

        public static string RuntimeShadersFolder => $"{Root}/Resources/RuntimeShaders";

        public static string UsdSamplesFolder => $"{Root}/USDSamples";
    }
}
