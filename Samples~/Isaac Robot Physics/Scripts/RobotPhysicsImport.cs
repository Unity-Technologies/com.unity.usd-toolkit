using System.IO;
using System.Threading.Tasks;
using UnityEngine;

namespace Unity.USDToolkit.RobotPhysics
{
    /// <summary>The robots this sample imports, and the import options it uses for them.</summary>
    public static class RobotPhysicsImport
    {
        /// <summary>The folder the Franka download is kept in.</summary>
        public const string FrankaFolderName = "franka_panda_isaac_6.0";

        private const string DownloadRootKey = "Unity.USDToolkit.RobotPhysics.DownloadRoot";

        /// <summary>Where downloads go unless you choose another folder.</summary>
        public static string DefaultDownloadRoot => Path.Combine(Application.persistentDataPath, "IsaacRobots");

        /// <summary>
        /// The folder downloaded robots are kept in, each in a folder of its own. Kept in
        /// PlayerPrefs, so the Editor window and the Play Mode panel share it. Set null or empty
        /// to go back to <see cref="DefaultDownloadRoot"/>.
        /// </summary>
        public static string DownloadRoot
        {
            get
            {
                string root = PlayerPrefs.GetString(DownloadRootKey, string.Empty);
                return string.IsNullOrWhiteSpace(root) ? DefaultDownloadRoot : root;
            }
            set
            {
                if (string.IsNullOrWhiteSpace(value) || value == DefaultDownloadRoot)
                {
                    PlayerPrefs.DeleteKey(DownloadRootKey);
                }
                else
                {
                    PlayerPrefs.SetString(DownloadRootKey, value.Trim());
                }

                PlayerPrefs.Save();
            }
        }

        /// <summary>The folder the Franka is downloaded to.</summary>
        public static string FrankaFolder => Path.Combine(DownloadRoot, FrankaFolderName);

        /// <summary>Where a player finds the bundled arm (copied there by the sample's build step).</summary>
        public static string StreamingArmPath =>
            Path.Combine(Application.streamingAssetsPath, "IsaacRobotPhysics", "isaac_style_arm.usda");

        public static UsdImportOptions Options(Transform parent = null) => new UsdImportOptions
        {
            Parent = parent,
            ImportPhysics = true,

            // Isaac Sim robots reference their meshes and materials from sibling folders.
            AllowExternalAssetPaths = true,
        };

        public static UsdImportResult Import(string path, Transform parent = null) =>
            UsdImporter.Import(path, Options(parent));

        public static Task<UsdImportResult> ImportAsync(string path, Transform parent = null) =>
            UsdImporter.ImportAsync(path, Options(parent));

        /// <summary>
        /// The robot's bounds: the renderers that move with an articulation link, else all of
        /// them (a stage's ground would otherwise make the robot a speck).
        /// </summary>
        public static Bounds RendererBounds(GameObject root)
        {
            Renderer[] renderers = root.GetComponentsInChildren<Renderer>();
            bool anyLink = false;
            foreach (Renderer renderer in renderers)
            {
                anyLink |= renderer.GetComponentInParent<ArticulationBody>() != null;
            }

            Bounds? bounds = null;
            foreach (Renderer renderer in renderers)
            {
                if (anyLink && renderer.GetComponentInParent<ArticulationBody>() == null)
                {
                    continue;
                }

                if (bounds.HasValue)
                {
                    Bounds grown = bounds.Value;
                    grown.Encapsulate(renderer.bounds);
                    bounds = grown;
                }
                else
                {
                    bounds = renderer.bounds;
                }
            }

            return bounds ?? new Bounds(root.transform.position, Vector3.one);
        }

        /// <summary>Points a camera at the robot from the front-right, a little above.</summary>
        public static void Frame(Camera camera, GameObject root)
        {
            if (camera == null || root == null)
            {
                return;
            }

            Bounds bounds = RendererBounds(root);
            float radius = Mathf.Max(0.25f, bounds.extents.magnitude);

            // The narrower of the two angles, so the robot fits a viewport beside a panel too.
            float halfVertical = 0.5f * camera.fieldOfView * Mathf.Deg2Rad;
            float halfHorizontal = Mathf.Atan(Mathf.Tan(halfVertical) * camera.aspect);
            float distance = radius / Mathf.Sin(Mathf.Min(halfVertical, halfHorizontal));
            Vector3 direction = new Vector3(1f, 0.6f, -1.2f).normalized;
            camera.transform.position = bounds.center + (direction * distance);
            camera.transform.LookAt(bounds.center);
            camera.nearClipPlane = Mathf.Max(0.01f, distance * 0.01f);
        }
    }
}
