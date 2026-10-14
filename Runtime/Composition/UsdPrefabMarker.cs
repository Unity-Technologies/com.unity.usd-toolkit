using UnityEngine;

namespace Unity.USDToolkit
{
    /// <summary>
    /// Marks the root of a prefab instance so a <see cref="UsdCompositionMode.Preserve"/> export
    /// can write the prefab once, as an asset, and reference it from every instance.
    ///
    /// The prefab connection Unity keeps is an Editor-only concept; a player has nothing that
    /// says two objects came from one prefab. This component carries that link into a player:
    /// <list type="bullet">
    /// <item>Prefab instances placed in a scene get one automatically when the scene is built or
    /// enters Play Mode (define USD_TOOLKIT_DISABLE_PREFAB_MARKERS to turn that off).</item>
    /// <item>Prefabs instantiated at runtime need <see cref="Instantiate(GameObject, Transform)"/>
    /// or <see cref="Attach"/>; a plain Object.Instantiate copy has no link and is exported as
    /// ordinary objects.</item>
    /// </list>
    /// In the Editor outside Play Mode the export reads the prefab connection directly and needs
    /// no marker.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("USD Toolkit/USD Prefab Marker")]
    public sealed class UsdPrefabMarker : MonoBehaviour
    {
        [Tooltip("The prefab asset this object is an instance of. The export writes the asset from it and diffs the instance against it.")]
        public GameObject SourcePrefab;

        [Tooltip("Identifies the prefab within one export; instances with the same id share one asset. The asset GUID when set by the Editor.")]
        public string AssetId;

        [Tooltip("Name for the asset prim and file. Defaults to the prefab's name.")]
        public string AssetName;

        /// <summary>
        /// Object.Instantiate that keeps the link to <paramref name="prefab"/>, so the copy is
        /// exported as a reference to the prefab's asset.
        /// </summary>
        public static GameObject Instantiate(GameObject prefab, Transform parent = null)
        {
            GameObject instance = Object.Instantiate(prefab, parent);
            Attach(instance, prefab);
            return instance;
        }

        public static GameObject Instantiate(GameObject prefab, Vector3 position, Quaternion rotation, Transform parent = null)
        {
            GameObject instance = Object.Instantiate(prefab, position, rotation, parent);
            Attach(instance, prefab);
            return instance;
        }

        /// <summary>
        /// Links an existing object to the prefab it was made from. Pass <paramref name="assetId"/>
        /// to group instances made from different prefab references as one asset; it defaults to
        /// an id derived from the prefab object.
        /// </summary>
        public static UsdPrefabMarker Attach(GameObject instance, GameObject prefab, string assetId = null)
        {
            if (instance == null)
            {
                throw new System.ArgumentNullException(nameof(instance));
            }

            UsdPrefabMarker marker = instance.GetComponent<UsdPrefabMarker>();
            if (marker == null)
            {
                marker = instance.AddComponent<UsdPrefabMarker>();
            }

            // A marker copied from the prefab may already carry the Editor's GUID; keep it so a
            // runtime copy groups with the scene's instances of the same prefab.
            if (prefab != null)
            {
                marker.SourcePrefab = prefab;
                if (string.IsNullOrEmpty(assetId))
                {
                    UsdPrefabMarker prefabMarker = prefab.GetComponent<UsdPrefabMarker>();
                    assetId = prefabMarker != null && !string.IsNullOrEmpty(prefabMarker.AssetId)
                        ? prefabMarker.AssetId
                        : "instance-" + prefab.GetEntityId();
                }

                if (string.IsNullOrEmpty(marker.AssetName))
                {
                    marker.AssetName = prefab.name;
                }
            }

            if (!string.IsNullOrEmpty(assetId))
            {
                marker.AssetId = assetId;
            }

            return marker;
        }
    }
}
