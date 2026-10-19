using System;
using UnityEngine;

namespace Unity.USDToolkit.Samples.AGVFleet.LiveSyncDemo
{
    /// <summary>
    /// Who simulates the fleet. Both backends speak the hub's message model (<see cref="HubMsg"/>:
    /// hello, state, path, fleet, event), so the scene and the views are the same whether Isaac Sim or
    /// the local fleet drives the AGVs. Unity always owns the obstacles and the goal
    /// (<see cref="UnityOpsLayer"/>); a backend reads them from there.
    /// </summary>
    public interface IFleetBackend : IDisposable
    {
        /// <summary>"Isaac Sim" or "Local fleet", for the panel.</summary>
        string DisplayName { get; }

        /// <summary>Where it runs ("127.0.0.1:10010", "in-process").</summary>
        string Endpoint { get; }

        bool Connected { get; }
        int Reconnects { get; }

        /// <summary>One line for the panel when something needs the operator's attention; null otherwise.</summary>
        string Note { get; }

        void Start(IFleetEnvironment environment, UnityOpsLayer ops);

        /// <summary>Called once per frame on the main thread.</summary>
        void Tick(float deltaTime);

        /// <summary>Next message for the scene; <paramref name="rxMs"/> is the wall clock it arrived at.</summary>
        bool TryReceive(out HubMsg msg, out double rxMs);

        /// <summary>The ops layer was just written (obstacle added/removed, goal set).</summary>
        void OpsChanged(int revision);

        /// <summary>Clear the queue and tasks; AGVs go back to their loops. The ops layer is already reset.</summary>
        void Reset();
    }

    /// <summary>AGV colours, shared by the local fleet and Tools~/agv_fleet/agv_fleet/agv.py.</summary>
    public static class AgvPalette
    {
        private static readonly Color[] Known =
        {
            new Color(0.10f, 0.55f, 1.00f),   // AGV_01 blue
            new Color(1.00f, 0.55f, 0.05f),   // AGV_02 orange
            new Color(0.20f, 0.80f, 0.30f),   // AGV_03 green
        };

        public static Color ForIndex(int i) =>
            i < Known.Length ? Known[i] : Color.HSVToRGB((0.13f + i * 0.31f) % 1f, 0.75f, 0.95f);
    }
}
