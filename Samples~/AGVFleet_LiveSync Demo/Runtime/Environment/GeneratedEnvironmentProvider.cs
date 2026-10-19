using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

namespace Unity.USDToolkit.Samples.AGVFleet.LiveSyncDemo
{
    /// <summary>
    /// A procedurally generated warehouse hall. <see cref="GeneratedFactoryBuilder"/> makes the geometry and
    /// the layout together; the geometry is written as USD (<see cref="FileName"/> in the stage folder, default
    /// prim <c>/GeneratedFactory</c>) and instantiated in Unity through the USD Toolkit importer, so Unity shows
    /// exactly what is on disk. Isaac Sim opens the same file: the shared stage references it at
    /// <c>/World/Environment</c>, where every environment of this sample sits.
    /// </summary>
    public sealed class GeneratedEnvironmentProvider : IFleetEnvironment
    {
        public const string FileName = "generated_factory.usda";

        public GeneratedEnvironmentProvider(GeneratedFactoryParams parameters, string stageDir)
        {
            Factory = GeneratedFactoryBuilder.Build(parameters);
            UsdPath = Path.Combine(stageDir, FileName);
            foreach (string note in Factory.Adjustments) Debug.Log("[AGV] generated layout: " + note);
        }

        public GeneratedFactory Factory { get; }
        public string Name => "generated";
        public string Identity => "generated:" + Factory.Params;
        public string UsdPath { get; }
        public string RootPrimPath => FactoryEnvironmentProvider.EnvironmentPrimPath;
        public float MetersPerUnit => 1f;
        public FleetLayout Layout => Factory.Layout;

        /// <summary>Writes the layer when its content changed (atomically: Isaac Sim may be reading it).</summary>
        public void WriteUsd() => AtomicFile.WriteIfChanged(UsdPath, Factory.ToUsda());

        public async Task<Transform> InstantiateAsync(Transform parent, Action<string> progress)
        {
            progress?.Invoke("generating layout...");
            WriteUsd();
            progress?.Invoke("importing generated layout...");
            UsdImportResult res = await UsdImporter.ImportAsync(UsdPath, new UsdImportOptions
            {
                Parent = parent,
                RootObjectName = FileName,
                IncludeInvisible = false,          // collision proxies are invisible
                ImportTextures = false,
                ApplyStageUpAxisAndUnits = false,  // Z-up meters; the conversion lives in UsdFrame
            });
            Debug.Log($"[AGV] generated hall ({Factory.Params}): {Factory.Parts.Count} parts, " +
                      $"{Layout.routeGraph.nodes.Count} lane nodes, {res.MeshCount} meshes");
            Transform rootT = res.RootObject.transform;
            Transform hall = rootT.Find(UsdaSceneWriter.RootName);
            return hall ? hall : rootT;
        }
    }
}
