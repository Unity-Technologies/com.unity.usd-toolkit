using System;
using UnityEngine;

namespace Unity.USDToolkit.Samples.AGVFleet.LiveSyncDemo
{
    /// <summary>Inputs of the generated warehouse hall. The same values and seed always give the same hall.</summary>
    [Serializable]
    public class GeneratedFactoryParams
    {
        [Tooltip("Hall length along USD x (m).")]
        [Range(24f, 120f)] public float hallLength = 48f;
        [Tooltip("Hall width along USD y (m).")]
        [Range(16f, 80f)] public float hallWidth = 32f;
        [Tooltip("Rack rows (along x). Fewer are built when the hall is too narrow.")]
        [Range(1, 10)] public int rackRows = 4;
        [Tooltip("Rack segments per row, separated by cross aisles. Fewer are built when the hall is too short.")]
        [Range(1, 6)] public int rackColumns = 3;
        [Tooltip("Aisle width (m). AGVs drive on the aisle centre lines.")]
        [Range(2.6f, 6f)] public float aisleWidth = 3.2f;
        [Tooltip("Pick stations (east wall) and drop stations (north wall), each.")]
        [Range(1, 8)] public int stationCount = 3;
        [Tooltip("AGVs, each with a charging dock on the west wall.")]
        [Range(1, 6)] public int agvCount = 3;
        [Tooltip("Rack loads, obstacle placement and station assignment.")]
        public int seed = 1;

        public GeneratedFactoryParams Clone() => (GeneratedFactoryParams)MemberwiseClone();

        /// <summary>The values clamped to their ranges (the inspector enforces them; code may not).</summary>
        public GeneratedFactoryParams Clamped()
        {
            GeneratedFactoryParams p = Clone();
            p.hallLength = Mathf.Clamp(p.hallLength, 24f, 120f);
            p.hallWidth = Mathf.Clamp(p.hallWidth, 16f, 80f);
            p.rackRows = Mathf.Clamp(p.rackRows, 1, 10);
            p.rackColumns = Mathf.Clamp(p.rackColumns, 1, 6);
            p.aisleWidth = Mathf.Clamp(p.aisleWidth, 2.6f, 6f);
            p.stationCount = Mathf.Clamp(p.stationCount, 1, 8);
            p.agvCount = Mathf.Clamp(p.agvCount, 1, 6);
            return p;
        }

        public override string ToString() =>
            string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "{0:0.##}x{1:0.##} m, {2} rows x {3} columns, aisle {4:0.##} m, {5} stations, {6} AGVs, seed {7}",
                hallLength, hallWidth, rackRows, rackColumns, aisleWidth, stationCount, agvCount, seed);
    }
}
