using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.AssetImporters;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Unity.USDToolkit.Editor
{
    /// <summary>
    /// Imports .usd, .usda, .usdc and .usdz files -- and Alembic (.abc) and Draco (.drc) files, which
    /// OpenUSD reads through its file format plugins -- in the project as model assets: the stage's
    /// hierarchy as the main GameObject (drag it into a scene, or make it a prefab variant), with
    /// its meshes, materials, textures and animation as sub-assets (and, with
    /// <see cref="GenerateAnimationClip"/>, the animation as an AnimationClip), so nothing has to be imported
    /// again at runtime and the content builds for every platform Unity targets. It runs the same
    /// importer as <see cref="UsdImporter.Import"/>, with the settings below, and reimports when
    /// the stage or any project file it read (sublayers, references, payloads, textures) changes.
    /// When another package that imports USD (or Alembic) files is installed, this importer is
    /// offered as an alternative for those instead of taking the extensions over: pick it in the
    /// asset's Inspector.
    /// </summary>
    // The file types this importer takes by default, and the ones it is only offered for, where
    // another installed package imports them already. .abc and .drc are read through OpenUSD's
    // usdAbc and usdDraco plugins, which every payload carries.
    [ScriptedImporter(Version,
        new string[]
        {
#if !USD_TOOLKIT_OTHER_USD_IMPORTER
            "usd", "usda", "usdc", "usdz",
#endif
#if !USD_TOOLKIT_OTHER_ALEMBIC_IMPORTER
            "abc",
#endif
            "drc",
        },
        new string[]
        {
#if USD_TOOLKIT_OTHER_USD_IMPORTER
            "usd", "usda", "usdc", "usdz",
#endif
#if USD_TOOLKIT_OTHER_ALEMBIC_IMPORTER
            "abc",
#endif
        })]
    [HelpURL("https://github.com/Unity-Technologies/com.unity.usd-toolkit")]
    public sealed class UsdStageImporter : ScriptedImporter
    {
        // Bump when what an import produces changes, so existing assets reimport.
        private const int Version = 2;

        [Header("Geometry")]
        [Tooltip("Apply the stage's upAxis and metersPerUnit to the root, so the content arrives Y-up and in meters.")]
        public bool ApplyStageUpAxisAndUnits = true;
        [Tooltip("Build one Mesh per distinct geometry and share it among identical instances.")]
        public bool ShareIdenticalMeshes = true;
        [Tooltip("Compute smooth normals for meshes that author none.")]
        public bool RecalculateNormalsIfMissing = true;
        [Tooltip("Also import prims authored invisible (inactive).")]
        public bool IncludeInvisible = true;
        [Tooltip("Add a MeshCollider to every static mesh.")]
        public bool GenerateColliders;
        [Tooltip("Build the stage's UsdPhysics: articulations as ArticulationBody trees with joint limits and drives, rigid bodies, joints, colliders and physics materials. Articulation links an Isaac Sim robot keeps as siblings are moved under their parent link.")]
        public bool ImportPhysics;

        [Header("Materials")]
        public bool ImportMaterials = true;
        public bool ImportTextures = true;
        [Tooltip("Block-compress textures (BC1/BC3). Turn off for textures that carry data, not appearance.")]
        public bool CompressTextures = true;
        [Tooltip("Give meshes without a material binding a shared grey material.")]
        public bool AssignDefaultMaterial = true;
        [Tooltip("Read textures outside the stage's folder. Only for stages you trust.")]
        public bool AllowExternalAssetPaths;

        [Header("Skinning and animation")]
        [Tooltip("Read static values at the stage's start time, as viewers do; off reads at USD's Default time.")]
        public bool ReadAtStageStartTime = true;
        [Tooltip("Build joints, SkinnedMeshRenderers and blend shapes for UsdSkel.")]
        public bool ImportSkinning = true;
        [Tooltip("Skin meshes that ask for dual-quaternion skinning as UsdSkel does, on the CPU; off skins them linearly on the GPU.")]
        public bool DualQuaternionSkinning = true;
        [Tooltip("Add a UsdAnimationPlayer for the stage's animation.")]
        public bool ImportAnimation = true;
        [Tooltip("Also make the animation an AnimationClip sub-asset, for an Animator, the Animation window or a " +
                 "Timeline Animation track. Turn the UsdAnimationPlayer's Play On Start off when an Animator plays it.")]
        public bool GenerateAnimationClip;

        [Header("Scene components")]
        public bool ImportLights = true;
        public bool ImportCameras = true;
        public bool ImportSpatialAudio = true;

        [Header("Composition")]
        [Tooltip("Off loads only the payloads listed below.")]
        public bool LoadPayloads = true;
        public List<string> PayloadsToLoad = new List<string>();
        [Tooltip("Variants to compose with, in order. The Inspector lists the stage's variant sets.")]
        public List<UsdVariantSelection> VariantSelections = new List<UsdVariantSelection>();

        [Header("Limits")]
        [Tooltip("The budgets the import is held to, and whether a stage over one fails or imports what fits.")]
        public UsdImportLimits Limits = new UsdImportLimits();

        /// <summary>The import options these settings stand for.</summary>
        public UsdImportOptions CreateOptions()
        {
            return new UsdImportOptions
            {
                ApplyStageUpAxisAndUnits = ApplyStageUpAxisAndUnits,
                ShareIdenticalMeshes = ShareIdenticalMeshes,
                RecalculateNormalsIfMissing = RecalculateNormalsIfMissing,
                IncludeInvisible = IncludeInvisible,
                GenerateColliders = GenerateColliders,
                ImportPhysics = ImportPhysics,
                ImportMaterials = ImportMaterials,
                ImportTextures = ImportTextures,
                CompressTextures = CompressTextures,
                AssignDefaultMaterial = AssignDefaultMaterial,
                AllowExternalAssetPaths = AllowExternalAssetPaths,
                ReadAtStageStartTime = ReadAtStageStartTime,
                ImportSkinning = ImportSkinning,
                DualQuaternionSkinning = DualQuaternionSkinning,
                ImportAnimation = ImportAnimation,
                ImportLights = ImportLights,
                ImportCameras = ImportCameras,
                ImportSpatialAudio = ImportSpatialAudio,
                LoadPayloads = LoadPayloads,
                PayloadsToLoad = new List<string>(PayloadsToLoad ?? new List<string>()),
                VariantSelections = new List<UsdVariantSelection>(VariantSelections ?? new List<UsdVariantSelection>()),
                Limits = (Limits ?? new UsdImportLimits()).Clone(),

                // What an asset needs, whatever the settings: a recorded source for Preserve
                // exports, textures that can be serialized, and no change to the open scene.
                RecordSource = true,
                KeepTexturesReadable = true,
                ApplyDomeLightToEnvironment = false,
            };
        }

        public override void OnImportAsset(AssetImportContext ctx)
        {
            UsdImportResult result;
            try
            {
                // An immutable package's files are not where their asset path says (they live in
                // Library/PackageCache), so the path is mapped to the file on disk first.
                UsdImportOptions options = CreateOptions();

                // The import context logs them to the Console and keeps them with the asset.
                options.StageWarningCallback = message => ctx.LogImportWarning(message);
                result = UsdImporter.Import(Path.GetFullPath(FileUtil.GetPhysicalPath(ctx.assetPath)), options);
            }
            catch (Exception exception)
            {
                ctx.LogImportError($"USD import of '{ctx.assetPath}' failed: {exception.Message}");
                return;
            }

            GameObject root = result.RootObject;

            // Point caches and dual-quaternion skins draw through vertex streams made at runtime,
            // which an asset cannot hold: the asset keeps the imported meshes, and the streams are
            // made again where an instance plays.
            foreach (MeshRenderer meshRenderer in root.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (meshRenderer.additionalVertexStreams != null)
                {
                    meshRenderer.additionalVertexStreams = null;
                    meshRenderer.ResetLocalBounds();
                }
            }

            ctx.AddObjectToAsset("root", root);
            ctx.SetMainObject(root);

            // Identifiers are the object's kind and name, numbered in hierarchy order, so a
            // reimport of the same stage gives every sub-asset the identifier it had and references
            // to it survive.
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            var added = new HashSet<Object>();
            void Add(Object item, string kind)
            {
                if (item == null || EditorUtility.IsPersistent(item) || !added.Add(item))
                {
                    return;
                }

                string key = kind + ":" + item.name;
                counts.TryGetValue(key, out int count);
                counts[key] = count + 1;
                ctx.AddObjectToAsset(count == 0 ? key : key + "#" + count, item);
            }

            void AddMaterial(Material material)
            {
                if (material == null)
                {
                    return;
                }

                Add(material, "material");
                foreach (int property in material.GetTexturePropertyNameIDs())
                {
                    Add(material.GetTexture(property), "texture");
                }
            }

            foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer is SkinnedMeshRenderer skinned)
                {
                    Add(skinned.sharedMesh, "mesh");
                }
                else if (renderer.TryGetComponent(out MeshFilter filter))
                {
                    Add(filter.sharedMesh, "mesh");
                }

                foreach (Material material in renderer.sharedMaterials)
                {
                    AddMaterial(material);
                }
            }

            foreach (MeshFilter filter in root.GetComponentsInChildren<MeshFilter>(true))
            {
                Add(filter.sharedMesh, "mesh");
            }

            foreach (UsdPointInstancer instancer in root.GetComponentsInChildren<UsdPointInstancer>(true))
            {
                Add(instancer.Mesh, "mesh");
                foreach (Material material in instancer.Materials ?? Array.Empty<Material>())
                {
                    AddMaterial(material);
                }
            }

            // UsdImportOptions.ImportPhysics: collision meshes and physics materials.
            foreach (Collider collider in root.GetComponentsInChildren<Collider>(true))
            {
                if (collider is MeshCollider meshCollider)
                {
                    Add(meshCollider.sharedMesh, "mesh");
                }

                Add(collider.sharedMaterial, "physicsMaterial");
            }

            AddMaterial(result.DefaultMaterial);
            if (result.Animation != null)
            {
                Add(result.Animation.Data, "animation");
                if (GenerateAnimationClip)
                {
                    Add(UsdAnimationClipBuilder.Build(result.Animation), "clip");
                }
            }

            // Reimport when anything the stage read changes, as far as the project tracks it: a
            // file outside Assets and Packages is not an asset, so editing it needs a manual reimport.
            foreach (string file in result.SourceFiles)
            {
                string asset = ToProjectPath(file);
                if (asset != null && !string.Equals(asset, ctx.assetPath, StringComparison.Ordinal))
                {
                    ctx.DependsOnSourceAsset(asset);
                }
            }
        }

        // The asset path of a file on disk, or null when the project does not track it. Files of
        // an immutable package live in Library/PackageCache but are named "Packages/<name>/...".
        private static string ToProjectPath(string file)
        {
            string full = Path.GetFullPath(file).Replace('\\', '/');
            string assets = Path.GetFullPath(Application.dataPath).Replace('\\', '/').TrimEnd('/') + "/";
            if (full.StartsWith(assets, StringComparison.OrdinalIgnoreCase))
            {
                return "Assets/" + full.Substring(assets.Length);
            }

            foreach (UnityEditor.PackageManager.PackageInfo package in UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages())
            {
                if (string.IsNullOrEmpty(package.resolvedPath) || string.IsNullOrEmpty(package.assetPath))
                {
                    continue;
                }

                string root = Path.GetFullPath(package.resolvedPath).Replace('\\', '/').TrimEnd('/') + "/";
                if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                {
                    return package.assetPath.TrimEnd('/') + "/" + full.Substring(root.Length);
                }
            }

            return null;
        }
    }
}
