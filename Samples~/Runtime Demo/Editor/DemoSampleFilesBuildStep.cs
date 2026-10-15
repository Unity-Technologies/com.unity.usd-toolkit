using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Unity.USDToolkit.RuntimeDemo.Editor
{
    /// <summary>
    /// Copies the sample USD files into StreamingAssets/USDSamples before a player build that
    /// includes the demo scene.
    /// </summary>
    /// <remarks>
    /// A player can only read loose files from StreamingAssets, and a package sample cannot ship
    /// into Assets/StreamingAssets itself. Files already there are left alone, so a project can
    /// replace the samples with its own.
    /// </remarks>
    internal sealed class DemoSampleFilesBuildStep : IPreprocessBuildWithReport
    {
        private const string SceneFileName = "USDRuntimeDemo.unity";

        public int callbackOrder => 0;

        public void OnPreprocessBuild(BuildReport report)
        {
            if (!BuildIncludesDemoScene())
            {
                return;
            }

            string source = DemoSamplePaths.UsdSamplesFolder;
            string destination = Path.Combine(Application.streamingAssetsPath, "USDSamples");
            if (!Directory.Exists(source))
            {
                Debug.LogWarning($"USD Runtime Demo: sample files not found at {source}; the import dialog will open empty.");
                return;
            }

            Directory.CreateDirectory(destination);
            int copied = 0;
            foreach (string file in Directory.GetFiles(source))
            {
                if (file.EndsWith(".meta"))
                {
                    continue;
                }

                string target = Path.Combine(destination, Path.GetFileName(file));
                if (!File.Exists(target))
                {
                    File.Copy(file, target);
                    copied++;
                }
            }

            if (copied > 0)
            {
                AssetDatabase.Refresh();
                Debug.Log($"USD Runtime Demo: copied {copied} sample file(s) to {destination}.");
            }
        }

        private static bool BuildIncludesDemoScene()
        {
            foreach (EditorBuildSettingsScene scene in EditorBuildSettings.scenes)
            {
                if (scene.enabled && Path.GetFileName(scene.path) == SceneFileName)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
