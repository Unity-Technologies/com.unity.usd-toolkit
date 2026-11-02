using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Unity.USDToolkit.ComposedGripper.Editor
{
    /// <summary>
    /// Copies the sample's USD files to StreamingAssets/ComposedGripperPhysics before a player
    /// build that includes ComposedGripperPhysics.unity: a player reads loose files only from
    /// StreamingAssets, and a package sample cannot ship into it. The layers keep their folders,
    /// since they reference each other by relative path.
    /// </summary>
    internal sealed class ComposedGripperBuildStep : IPreprocessBuildWithReport
    {
        private const string SceneFileName = "ComposedGripperPhysics.unity";

        public int callbackOrder => 0;

        public void OnPreprocessBuild(BuildReport report)
        {
            bool included = false;
            foreach (EditorBuildSettingsScene scene in EditorBuildSettings.scenes)
            {
                included |= scene.enabled && Path.GetFileName(scene.path) == SceneFileName;
            }

            if (!included)
            {
                return;
            }

            string source = ComposedGripperMenu.UsdFolder;
            string destination = ComposedGripperImport.StreamingRoot;
            foreach (string folder in ComposedGripperImport.AssetFolders)
            {
                string from = Path.Combine(source, folder);
                foreach (string file in Directory.GetFiles(from, "*.usd*", SearchOption.AllDirectories))
                {
                    string to = Path.Combine(destination, folder, Path.GetRelativePath(from, file));
                    Directory.CreateDirectory(Path.GetDirectoryName(to));
                    File.Copy(file, to, true);
                }
            }

            Debug.Log($"Composed Gripper Physics: copied the gripper assets to {destination} for the player.");
        }
    }
}
