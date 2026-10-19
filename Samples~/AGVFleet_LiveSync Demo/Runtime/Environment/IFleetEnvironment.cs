using System.Threading.Tasks;
using UnityEngine;

namespace Unity.USDToolkit.Samples.AGVFleet.LiveSyncDemo
{
    /// <summary>
    /// One environment the fleet drives in: the USD that holds its geometry, plus the layout the fleet
    /// logic uses. The fleet code depends on this interface only, never on a specific factory.
    /// </summary>
    public interface IFleetEnvironment
    {
        /// <summary>Short name for logs and the panel ("factory", "generated").</summary>
        string Name { get; }

        /// <summary>
        /// Stable identity of this environment's content. Saved obstacles are dropped when it changes
        /// (they would land inside walls or machines).
        /// </summary>
        string Identity { get; }

        /// <summary>The USD file the environment's geometry lives in (both Unity and Isaac Sim open it).</summary>
        string UsdPath { get; }

        /// <summary>Prim path the environment sits under on the shared stage.</summary>
        string RootPrimPath { get; }

        /// <summary>metersPerUnit of <see cref="UsdPath"/>; the stage scales the environment prim by it.</summary>
        float MetersPerUnit { get; }

        /// <summary>Spawns, loops, stations, planning bounds and (when known) exact obstacle footprints.</summary>
        FleetLayout Layout { get; }

        /// <summary>
        /// Builds the environment's GameObjects under <paramref name="parent"/>, which is in the Unity
        /// frame (Y-up). Returns the environment root; renderers below it are the drivable scene.
        /// </summary>
        Task<Transform> InstantiateAsync(Transform parent, System.Action<string> progress);
    }
}
