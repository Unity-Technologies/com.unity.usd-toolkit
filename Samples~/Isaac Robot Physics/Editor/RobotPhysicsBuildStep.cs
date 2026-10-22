using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Unity.USDToolkit.RobotPhysics.Editor
{
    /// <summary>
    /// Copies the bundled arm to StreamingAssets/IsaacRobotPhysics before a player build that
    /// includes RobotPhysics.unity: a player reads loose files only from StreamingAssets, and a
    /// package sample cannot ship into it. The Franka is downloaded by the player itself.
    /// </summary>
    internal sealed class RobotPhysicsBuildStep : IPreprocessBuildWithReport
    {
        private const string SceneFileName = "RobotPhysics.unity";

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

            string destination = RobotPhysicsImport.StreamingArmPath;
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            File.Copy(RobotPhysicsWindow.BundledArmPath, destination, true);
            Debug.Log($"Robot Physics: copied the bundled arm to {destination} for the player.");
        }
    }
}
