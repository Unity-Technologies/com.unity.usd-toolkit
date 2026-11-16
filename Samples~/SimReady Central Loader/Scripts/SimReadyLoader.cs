using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Unity.USDToolkit.Samples.SimReadyCentral
{
    /// <summary>
    /// From a pasted link to an asset in the scene: downloads it (<see cref="SimReadyDownloader"/>),
    /// imports it through the toolkit's own import with its UsdPhysics (variant sets kept live, not
    /// flattened; Z-up and the stage's units corrected on the root), and puts a
    /// <see cref="SimReadyAsset"/> on the root with the SimReady metadata, the physics log and the
    /// joint handles. Used the same way by the Editor window and the runtime viewer.
    /// </summary>
    public static class SimReadyLoader
    {
        public sealed class LoadResult
        {
            public SimReadyAsset Asset;
            public UsdImportResult Import;
            public SimReadyDownloadResult Download;
        }

        /// <summary>
        /// Downloads and imports the asset at <paramref name="link"/>. It replaces every SimReady
        /// asset already in the scene (<see cref="SimReadyAsset.RemoveAll"/>) unless
        /// <paramref name="replaceExisting"/> is false: two assets at the origin would collide, and
        /// the front-ends show one asset at a time.
        /// </summary>
        public static async Task<LoadResult> LoadAsync(string link, SimReadyHostPolicy policy, Transform parent,
                                                       IProgress<SimReadyProgress> progress, CancellationToken cancellation,
                                                       string cacheRoot = null, ISimReadyTransport transport = null,
                                                       bool replaceExisting = true)
        {
            var downloader = new SimReadyDownloader(policy, cacheRoot ?? SimReadyDownloader.DefaultCacheRoot, transport);
            SimReadyDownloadResult download = await downloader.DownloadAsync(link, progress, cancellation);
            cancellation.ThrowIfCancellationRequested();
            if (replaceExisting)
            {
                SimReadyAsset.RemoveAll();
            }

            progress?.Report(new SimReadyProgress { Stage = "Importing", FilesDone = download.Files.Count, FilesKnown = download.Files.Count });
            UsdImportResult import = await ImportAsync(download.RootLocalPath, Options(parent, null));
            SimReadyAsset asset = Attach(import, download, link);
            return new LoadResult { Asset = asset, Import = import, Download = download };
        }

        /// <summary>The import: physics on, payloads loaded, composition recorded so variants can be switched.</summary>
        public static UsdImportOptions Options(Transform parent, UsdVariantSelection variant)
        {
            var options = new UsdImportOptions
            {
                Parent = parent,
                ImportPhysics = true,
                ApplyStageUpAxisAndUnits = true,
                RecordSource = true,
                LoadPayloads = true,
                // Every file lives inside the asset's folder of the cache; nothing outside is read.
                AllowExternalAssetPaths = false,
            };

            if (variant != null)
            {
                options.VariantSelections.Add(variant);
            }

            return options;
        }

        private static async Task<UsdImportResult> ImportAsync(string path, UsdImportOptions options) =>
            Application.isPlaying ? await UsdImporter.ImportAsync(path, options) : UsdImporter.Import(path, options);

        /// <summary>
        /// Selects a variant (the Physics variant set, say) and imports the asset again with it,
        /// replacing the old one. The selection is made on the composed stage, so the geometry,
        /// the physics payload and the variant sets stay what USD composes; the asset keeps its
        /// metadata and source.
        /// </summary>
        public static async Task<SimReadyAsset> SwitchVariantAsync(SimReadyAsset asset, string primPath, string variantSet, string variant)
        {
            UsdStageSource source = asset.GetComponent<UsdStageSource>();
            if (source == null)
            {
                throw new InvalidOperationException("The asset was imported without its composition recorded.");
            }

            UsdImportOptions options = Options(asset.transform.parent, null);
            foreach (UsdVariantSelection selection in source.VariantSelections)
            {
                if (selection.PrimPath != primPath || selection.VariantSet != variantSet)
                {
                    options.VariantSelections.Add(new UsdVariantSelection(selection.PrimPath, selection.VariantSet, selection.Variant));
                }
            }

            options.VariantSelections.Add(new UsdVariantSelection(primPath, variantSet, variant));
            Vector3 position = asset.transform.localPosition;
            Quaternion rotation = asset.transform.localRotation;

            // The old asset steps aside while its replacement imports, so the two never collide.
            asset.gameObject.SetActive(false);
            UsdImportResult import;
            try
            {
                import = await ImportAsync(asset.RootLocalPath, options);
            }
            catch
            {
                asset.gameObject.SetActive(true);
                throw;
            }

            import.RootObject.transform.SetLocalPositionAndRotation(position, rotation);

            SimReadyAsset replacement = import.RootObject.AddComponent<SimReadyAsset>();
            replacement.Metadata = asset.Metadata;
            replacement.SourceLink = asset.SourceLink;
            replacement.RepoId = asset.RepoId;
            replacement.RepoUrl = asset.RepoUrl;
            replacement.Commit = asset.Commit;
            replacement.RootRepoPath = asset.RootRepoPath;
            replacement.RootLocalPath = asset.RootLocalPath;
            replacement.ThumbnailPath = asset.ThumbnailPath;
            replacement.DownloadWarnings = new List<string>(asset.DownloadWarnings);
            replacement.Kinematic = asset.Kinematic;
            Finish(replacement, import);

            if (Application.isPlaying)
            {
                UnityEngine.Object.Destroy(asset.gameObject);
            }
            else
            {
                UnityEngine.Object.DestroyImmediate(asset.gameObject);
            }

            return replacement;
        }

        /// <summary>
        /// Imports a SimReady asset already on disk (a cached download, or a package unpacked by
        /// hand) the same way a download is imported, and puts its <see cref="SimReadyAsset"/> on it.
        /// </summary>
        public static SimReadyAsset ImportLocal(string rootLayerPath, Transform parent = null, string validationJsonPath = null,
                                                bool replaceExisting = true)
        {
            if (replaceExisting)
            {
                SimReadyAsset.RemoveAll();
            }

            UsdImportResult import = UsdImporter.Import(rootLayerPath, Options(parent, null));
            SimReadyAsset asset = CreateAsset(import, rootLayerPath, validationJsonPath);
            Finish(asset, import);
            return asset;
        }

        private static SimReadyAsset CreateAsset(UsdImportResult import, string rootLocalPath, string validationJsonPath)
        {
            SimReadyLayerScan scan = SimReadyLayerScanner.Scan(rootLocalPath);
            SimReadyAsset asset = import.RootObject.AddComponent<SimReadyAsset>();
            asset.Kinematic = SimReadyAsset.KinematicByDefault;
            asset.Metadata = SimReadyMetadata.FromScan(scan, validationJsonPath);
            if (string.IsNullOrEmpty(asset.Metadata.AssetName))
            {
                asset.Metadata.AssetName = System.IO.Path.GetFileNameWithoutExtension(rootLocalPath);
            }

            asset.RootLocalPath = rootLocalPath;
            return asset;
        }

        private static SimReadyAsset Attach(UsdImportResult import, SimReadyDownloadResult download, string link)
        {
            SimReadyAsset asset = CreateAsset(import, download.RootLocalPath, download.ValidationLocalPath);
            asset.SourceLink = link;
            asset.ThumbnailPath = download.ThumbnailLocalPath ?? string.Empty;
            asset.RepoId = download.Link.RepoId;
            asset.RepoUrl = download.Link.RepoUrl;
            asset.Commit = download.Commit;
            asset.RootRepoPath = download.RootRepoPath;
            asset.DownloadWarnings = new List<string>(download.Warnings);
            if (download.UnreferencedFiles > 0)
            {
                asset.DownloadWarnings.Add($"{download.UnreferencedFiles} file(s) in the asset's folder are not referenced by any layer and were not downloaded.");
            }

            Finish(asset, import);
            return asset;
        }

        private static void Finish(SimReadyAsset asset, UsdImportResult import)
        {
            asset.PhysicsLog = SimReadyPhysicsReport.Build(import);
            asset.SetDisplayNames(ReadDisplayNames(import));
            asset.BuildModel();
            SimReadyPhysicsReport.Log(asset.Metadata.AssetName, asset.PhysicsLog);
            SimReadyAsset.NotifyLoaded(asset);
        }

        // A joint's displayName, where the stage authors one, names its handle.
        private static Dictionary<string, string> ReadDisplayNames(UsdImportResult import)
        {
            var names = new Dictionary<string, string>(StringComparer.Ordinal);
            UsdStage stage;
            try
            {
                stage = import.Source != null ? import.Source.Stage : null;
            }
            catch (Exception exception) when (exception is UsdStageException || exception is NotSupportedException)
            {
                return names;
            }

            if (stage == null)
            {
                return names;
            }

            foreach (UsdPhysicsJoint joint in import.RootObject.GetComponentsInChildren<UsdPhysicsJoint>(true))
            {
                try
                {
                    string displayName = stage.GetPrimAtPath(joint.PrimPath).GetMetadata("displayName");
                    if (!string.IsNullOrEmpty(displayName))
                    {
                        names[joint.PrimPath] = displayName;
                    }
                }
                catch (UsdStageException)
                {
                    // A joint path the stage API cannot address keeps its prim name.
                }
            }

            return names;
        }

        /// <summary>The variant sets of the asset's default prim, as the import recorded them.</summary>
        public static IReadOnlyList<UsdVariantSetInfo> VariantSets(SimReadyAsset asset)
        {
            UsdStageSource source = asset != null ? asset.GetComponent<UsdStageSource>() : null;
            if (source == null)
            {
                return Array.Empty<UsdVariantSetInfo>();
            }

            string defaultPrim = source.DefaultPrimPath;
            return source.VariantSets.Where(set => string.IsNullOrEmpty(defaultPrim) || set.PrimPath == defaultPrim).ToList();
        }
    }
}
