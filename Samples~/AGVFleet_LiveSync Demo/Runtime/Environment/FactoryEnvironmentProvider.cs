using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using UnityEngine;

namespace Unity.USDToolkit.Samples.AGVFleet.LiveSyncDemo
{
    /// <summary>
    /// The NVIDIA "Factory" scene from the USD Explorer Sample Asset Pack, imported with the USD Toolkit,
    /// plus the authored <see cref="FactoryLayout"/> overlay (the factory has no route data of its own).
    ///
    /// When Isaac Sim has run on this stage folder before, its composed entry point (root_unity.usda,
    /// else root.usda) is imported instead if it references the same factory file: that adds the
    /// UsdPreviewSurface look layer and the frozen welding-cell animation Isaac generates. Otherwise
    /// Factory.usd is imported directly.
    /// </summary>
    public sealed class FactoryEnvironmentProvider : IFleetEnvironment
    {
        public const string EnvironmentPrimPath = "/World/Environment";

        private readonly string stageDir;

        public FactoryEnvironmentProvider(string factoryUsdPath, FactoryLayout overlay, string stageDir)
        {
            UsdPath = factoryUsdPath;
            this.stageDir = stageDir;
            Layout = overlay != null ? overlay.layout : FactoryLayout.CreateDefault();
            MetersPerUnit = overlay != null ? overlay.metersPerUnit : 0.01f;
        }

        public string Name => "factory";
        public string Identity => "factory:" + Path.GetFileName(UsdPath);
        public string UsdPath { get; }
        public string RootPrimPath => EnvironmentPrimPath;
        public float MetersPerUnit { get; }
        public FleetLayout Layout { get; }

        public async Task<Transform> InstantiateAsync(Transform parent, Action<string> progress)
        {
            if (!File.Exists(UsdPath))
            {
                throw new FileNotFoundException(
                    $"Factory USD not found: {UsdPath}. Download the {AgvFleetSettings.AssetPackName} " +
                    $"({AgvFleetSettings.AssetPackUrl}) and set Asset Pack Path in the AGV Fleet Settings asset.");
            }
            string composed = FindIsaacComposedStage();
            if (composed != null)
            {
                progress?.Invoke("importing factory (Isaac Sim's composed stage)...");
                UsdImportResult res = await UsdImporter.ImportAsync(composed, Options(parent, Path.GetFileName(composed)));
                CheckStage(res, 1.0, composed);
                Transform world = res.RootObject.transform.Find("World");
                // Live prims come from the fleet backend, not from the last saved Isaac layer.
                foreach (string live in new[] { "AGVs", "Dynamic", "Lights" })
                {
                    Transform t = world ? world.Find(live) : null;
                    if (t) UnityEngine.Object.Destroy(t.gameObject);
                }
                Transform env = world ? world.Find("Environment") : null;
                if (env) return env;
                Debug.LogWarning($"[AGV] {composed} has no /World/Environment; importing {UsdPath} directly");
                UnityEngine.Object.Destroy(res.RootObject);
            }

            progress?.Invoke("importing factory...");
            // The stage's /World/Environment prim: scales the centimetre file to meters.
            var wrapper = new GameObject("Environment (" + Path.GetFileName(UsdPath) + ")").transform;
            wrapper.SetParent(parent, false);
            wrapper.localScale = Vector3.one * MetersPerUnit;
            UsdImportResult direct = await UsdImporter.ImportAsync(UsdPath, Options(wrapper, Path.GetFileName(UsdPath)));
            CheckStage(direct, MetersPerUnit, UsdPath);
            // A reference brings in the default prim's contents: bind like /World/Environment does.
            Transform rootT = direct.RootObject.transform;
            return rootT.childCount == 1 ? rootT.GetChild(0) : rootT;
        }

        private static UsdImportOptions Options(Transform parent, string name) => new UsdImportOptions
        {
            Parent = parent,
            RootObjectName = name,
            AllowExternalAssetPaths = true,   // the factory's textures and sub-USDs live next to it
            ImportTextures = true,
            IncludeInvisible = false,
            ApplyStageUpAxisAndUnits = false, // the conversion lives in UsdFrame and the wrapper scale only
        };

        private static void CheckStage(UsdImportResult res, double expectedMpu, string path)
        {
            if (Math.Abs(res.MetersPerUnit - expectedMpu) > 1e-6 || res.UpAxis != "Z")
            {
                Debug.LogWarning($"[AGV] {Path.GetFileName(path)} is {res.UpAxis}-up, metersPerUnit {res.MetersPerUnit}; " +
                                 $"the sample expects Z-up, {expectedMpu}");
            }
        }

        /// <summary>
        /// Isaac Sim's entry point in the stage folder, when it references this factory file; else null.
        /// </summary>
        private string FindIsaacComposedStage()
        {
            if (string.IsNullOrEmpty(stageDir)) return null;
            string root = Path.Combine(stageDir, "root.usda");
            if (!File.Exists(root)) return null;
            try
            {
                Match m = Regex.Match(File.ReadAllText(root), @"references\s*=\s*@([^@]+)@");
                if (!m.Success) return null;
                string reference = m.Groups[1].Value;
                string resolved = Path.GetFullPath(Path.IsPathRooted(reference) ? reference : Path.Combine(stageDir, reference));
                if (!string.Equals(resolved, Path.GetFullPath(UsdPath), StringComparison.OrdinalIgnoreCase)) return null;
            }
            catch (Exception e) when (e is IOException || e is ArgumentException || e is NotSupportedException)
            {
                return null;
            }
            string unityRoot = Path.Combine(stageDir, "root_unity.usda");
            return File.Exists(unityRoot) ? unityRoot : root;
        }
    }
}
