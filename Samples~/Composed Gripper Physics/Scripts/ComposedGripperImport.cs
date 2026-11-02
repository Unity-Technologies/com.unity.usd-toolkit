using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;

namespace Unity.USDToolkit.ComposedGripper
{
    /// <summary>The USD files the sample ships, in the sample's USD~ folder.</summary>
    public enum GripperAsset
    {
        /// <summary>v1_prismatic/gripper.usda: each finger slides on a prismatic joint.</summary>
        V1Prismatic,

        /// <summary>v2_linkage/gripper.usda: each finger rides a four-bar linkage closed by an excluded joint.</summary>
        V2Linkage,

        /// <summary>v3_composed/gripper.usda: both mechanisms in one asset, chosen by its preset variant set.</summary>
        V3Gripper,

        /// <summary>v3_composed/scene.usda: four grippers whose variant selections arrive through different arcs.</summary>
        V3Scene
    }

    /// <summary>Where the sample's USD files are, and the import options it uses for them.</summary>
    public static class ComposedGripperImport
    {
        /// <summary>The prim that carries v3's variant sets.</summary>
        public const string GripperPrim = "/composed_gripper";

        /// <summary>v3's preset variant set: each variant selects a mechanism and pads.</summary>
        public const string PresetSet = "preset";

        /// <summary>The preset variants of v3_composed/gripper.usda, the asset's default first.</summary>
        public static readonly string[] Presets = { "prismatic", "linkage", "linkage_smooth" };

        /// <summary>The folder (next to Scripts/) that holds the USD files. Unity does not import a folder ending in ~.</summary>
        public const string UsdFolderName = "USD~";

        /// <summary>Where a player finds the USD files (copied there by the sample's build step).</summary>
        public static string StreamingRoot => Path.Combine(Application.streamingAssetsPath, "ComposedGripperPhysics");

        /// <summary>The asset's root layer, relative to the USD folder.</summary>
        public static string RelativePath(GripperAsset asset)
        {
            switch (asset)
            {
                case GripperAsset.V1Prismatic: return "v1_prismatic/gripper.usda";
                case GripperAsset.V2Linkage: return "v2_linkage/gripper.usda";
                case GripperAsset.V3Gripper: return "v3_composed/gripper.usda";
                default: return "v3_composed/scene.usda";
            }
        }

        /// <summary>The folders a player needs, relative to the USD folder.</summary>
        public static readonly string[] AssetFolders = { "v1_prismatic", "v2_linkage", "v3_composed" };

        /// <summary>
        /// The import options: physics on, and for <see cref="GripperAsset.V3Gripper"/> the preset
        /// as a variant selection. The selection is applied on the composed stage before anything
        /// is built, so the geometry and the physics payload switch together.
        /// </summary>
        public static UsdImportOptions Options(GripperAsset asset, string preset, Transform parent = null)
        {
            var options = new UsdImportOptions
            {
                Parent = parent,
                ImportPhysics = true,
            };

            if (asset == GripperAsset.V3Gripper && !string.IsNullOrEmpty(preset))
            {
                options.VariantSelections.Add(new UsdVariantSelection(GripperPrim, PresetSet, preset));
            }

            return options;
        }

        public static UsdImportResult Import(string usdFolder, GripperAsset asset, string preset, Transform parent = null) =>
            UsdImporter.Import(Path.Combine(usdFolder, RelativePath(asset)), Options(asset, preset, parent));

        public static Task<UsdImportResult> ImportAsync(string usdFolder, GripperAsset asset, string preset, Transform parent = null) =>
            UsdImporter.ImportAsync(Path.Combine(usdFolder, RelativePath(asset)), Options(asset, preset, parent));

        /// <summary>
        /// Puts a <see cref="GripperDrive"/> on every articulation root of an import (one per
        /// gripper) and returns them, in the order the import built them.
        /// </summary>
        public static List<GripperDrive> AddDrives(GameObject root)
        {
            var drives = new List<GripperDrive>();
            foreach (ArticulationBody body in root.GetComponentsInChildren<ArticulationBody>())
            {
                if (body.isRoot)
                {
                    GripperDrive drive = body.GetComponent<GripperDrive>();
                    if (drive == null)
                    {
                        drive = body.gameObject.AddComponent<GripperDrive>();
                    }

                    // Awake does not run in edit mode, so collect here too.
                    drive.Collect();
                    drives.Add(drive);
                }
            }

            return drives;
        }

        /// <summary>Points a camera at the grippers from the front, a little above.</summary>
        public static void Frame(Camera camera, GameObject root)
        {
            if (camera == null || root == null)
            {
                return;
            }

            Bounds? bounds = null;
            foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>())
            {
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

            Bounds b = bounds ?? new Bounds(root.transform.position, Vector3.one * 0.25f);
            float radius = Mathf.Max(0.1f, b.extents.magnitude);

            // The narrower of the two angles, so the grippers fit a viewport beside a panel too.
            float halfVertical = 0.5f * camera.fieldOfView * Mathf.Deg2Rad;
            float halfHorizontal = Mathf.Atan(Mathf.Tan(halfVertical) * camera.aspect);
            float distance = radius / Mathf.Sin(Mathf.Min(halfVertical, halfHorizontal));
            Vector3 direction = new Vector3(0.35f, 0.45f, -1f).normalized;
            camera.transform.position = b.center + (direction * distance);
            camera.transform.LookAt(b.center);
            camera.nearClipPlane = Mathf.Max(0.01f, distance * 0.01f);
        }
    }
}
