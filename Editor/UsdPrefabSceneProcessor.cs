#if !USD_TOOLKIT_DISABLE_PREFAB_MARKERS
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Unity.USDToolkit.Editor
{
    // Carries prefab connections into Play Mode and players, where Unity no longer has them, by
    // putting a UsdPrefabMarker on every prefab instance root of a scene as it is built or
    // entered. Only the built copy (or the Play Mode copy) of the scene is touched; scene files
    // and prefab assets on disk stay as they are. Define USD_TOOLKIT_DISABLE_PREFAB_MARKERS to
    // turn it off.
    internal sealed class UsdPrefabSceneProcessor : IProcessSceneWithReport
    {
        public int callbackOrder => 0;

        public void OnProcessScene(Scene scene, BuildReport report)
        {
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                {
                    MarkIfPrefabInstanceRoot(t.gameObject);
                }
            }
        }

        private static void MarkIfPrefabInstanceRoot(GameObject gameObject)
        {
            if (!PrefabUtility.IsAnyPrefabInstanceRoot(gameObject))
            {
                return;
            }

            string assetPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(gameObject);
            GameObject asset = string.IsNullOrEmpty(assetPath) ? null : AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (asset == null)
            {
                return;
            }

            UsdPrefabMarker marker = gameObject.GetComponent<UsdPrefabMarker>();
            if (marker == null)
            {
                marker = gameObject.AddComponent<UsdPrefabMarker>();
            }

            marker.SourcePrefab = asset;
            marker.AssetId = AssetDatabase.AssetPathToGUID(assetPath);
            if (string.IsNullOrEmpty(marker.AssetName))
            {
                marker.AssetName = asset.name;
            }
        }
    }
}
#endif
