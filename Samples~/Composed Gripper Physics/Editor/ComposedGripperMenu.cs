using System.IO;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace Unity.USDToolkit.ComposedGripper.Editor
{
    /// <summary>
    /// USD Toolkit > Composed Gripper Physics: imports one of the sample's assets with its physics
    /// into the open scene (edit mode), to inspect the ArticulationBody tree, colliders and drives.
    /// The window (<see cref="ComposedGripperWindow"/>) opens and closes them in edit mode too.
    /// </summary>
    internal static class ComposedGripperMenu
    {
        private const string RuntimeAssemblyName = "Unity.USDToolkit.Samples.ComposedGripper";
        private const string Menu = "USD Toolkit/Composed Gripper Physics/";

        /// <summary>The sample's folder in the project (the Package Manager may put it anywhere).</summary>
        public static string SampleRoot
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

        /// <summary>The sample's USD~ folder, which Unity does not import.</summary>
        public static string UsdFolder => Path.GetFullPath($"{SampleRoot}/{ComposedGripperImport.UsdFolderName}");

        [MenuItem(Menu + "Import v1 prismatic")]
        private static void ImportV1() => Import(GripperAsset.V1Prismatic, null);

        [MenuItem(Menu + "Import v2 linkage")]
        private static void ImportV2() => Import(GripperAsset.V2Linkage, null);

        [MenuItem(Menu + "Import v3 gripper (preset prismatic)")]
        private static void ImportV3Prismatic() => Import(GripperAsset.V3Gripper, "prismatic");

        [MenuItem(Menu + "Import v3 gripper (preset linkage)")]
        private static void ImportV3Linkage() => Import(GripperAsset.V3Gripper, "linkage");

        [MenuItem(Menu + "Import v3 gripper (preset linkage_smooth)")]
        private static void ImportV3LinkageSmooth() => Import(GripperAsset.V3Gripper, "linkage_smooth");

        [MenuItem(Menu + "Import v3 scene (four grippers)")]
        private static void ImportV3Scene() => Import(GripperAsset.V3Scene, null);

        /// <summary>Imports an asset into the open scene, adds a GripperDrive per gripper and selects it.</summary>
        public static UsdImportResult Import(GripperAsset asset, string preset)
        {
            UsdImportResult result = ComposedGripperImport.Import(UsdFolder, asset, preset);
            GameObject root = result.RootObject;
            if (asset == GripperAsset.V3Gripper)
            {
                root.name += $" ({preset})";
            }

            ComposedGripperImport.AddDrives(root);
            Undo.RegisterCreatedObjectUndo(root, "Import composed gripper");
            Selection.activeGameObject = root;

            string notes = result.PhysicsNotes.Count > 0 ? "\n" + string.Join("\n", result.PhysicsNotes) : string.Empty;
            Debug.Log($"Composed Gripper Physics: imported {ComposedGripperImport.RelativePath(asset)}" +
                      $"{(preset != null ? $" with preset={preset}" : string.Empty)}: " +
                      $"{root.GetComponentsInChildren<ArticulationBody>().Length} ArticulationBodies, " +
                      $"{root.GetComponentsInChildren<MeshCollider>().Length} MeshColliders, " +
                      $"{result.PhysicsNotes.Count} physics notes.{notes}", root);
            return result;
        }
    }
}
