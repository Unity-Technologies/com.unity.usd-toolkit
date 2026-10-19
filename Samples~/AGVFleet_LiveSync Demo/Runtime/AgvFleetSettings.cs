using UnityEngine;

namespace Unity.USDToolkit.Samples.AGVFleet.LiveSyncDemo
{
    public enum EnvironmentMode
    {
        [Tooltip("The NVIDIA factory when its USD is found, otherwise the generated layout.")]
        Auto,
        Factory,
        Generated,
    }

    public enum FleetSimulationMode
    {
        [Tooltip("Isaac Sim when its hub answers at start, otherwise the local fleet.")]
        Auto,
        [Tooltip("The fleet runs in Unity; Isaac Sim is not used.")]
        Local,
        [Tooltip("Isaac Sim simulates the fleet; Unity reconnects until the hub is up.")]
        IsaacSim,
    }

    /// <summary>All user-facing settings of the sample, in one asset.</summary>
    [CreateAssetMenu(menuName = "USD Toolkit Samples/AGV Fleet Settings", fileName = "AgvFleetSettings")]
    public sealed class AgvFleetSettings : ScriptableObject
    {
        public const string AssetPackName = "USD Explorer Sample Asset Pack";
        public const string AssetPackUrl = "https://d4i3qtqj3r0z5.cloudfront.net/USD_Explorer_Sample_NVD%4010011.zip";
        /// <summary>Overrides <see cref="assetPackPath"/> (e.g. for a player build).</summary>
        public const string AssetPackPathVariable = "AGV_FLEET_ASSET_PACK";
        /// <summary>Overrides <see cref="stageDir"/>; Tools~/agv_fleet reads the same variable.</summary>
        public const string StageDirVariable = "AGV_FLEET_STAGE_DIR";

        [Header("Environment")]
        public EnvironmentMode environment = EnvironmentMode.Auto;

        [Tooltip("Folder the asset pack was unzipped to. Relative paths are resolved from the project folder " +
                 "(the parent of Assets) or, in a player, the folder of the executable. Keep it outside Assets.")]
        public string assetPackPath = "ExternalAssets/USD_Explorer_Sample";

        [Tooltip("The factory scene inside the asset pack folder.")]
        public string factoryUsdInPack = "Usd_Explorer/Samples/Examples/2023_2/Factory/Factory.usd";

        [Tooltip("Spawns, loops, stations and planning bounds for the factory (numbers only, no NVIDIA content).")]
        public FactoryLayout factoryLayout;

        [Tooltip("The generated hall (used when the environment is Generated, or Auto without the factory).")]
        public GeneratedFactoryParams generator = new GeneratedFactoryParams();

        [Header("Simulation")]
        public FleetSimulationMode simulation = FleetSimulationMode.Auto;

        [Tooltip("Shared stage folder, relative to the project folder (or the player's folder).")]
        public string stageDir = "UsdSync/AGVFleet";

        public string hubHost = "127.0.0.1";
        public int hubPort = 10010;

        [Tooltip("Optional AGV model (USD). Empty: the primitive AGV built by the sample.")]
        public string agvModelUsd = "";
    }
}
