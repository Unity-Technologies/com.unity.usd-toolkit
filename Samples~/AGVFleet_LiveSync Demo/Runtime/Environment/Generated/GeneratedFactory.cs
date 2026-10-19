using System.Collections.Generic;
using UnityEngine;

namespace Unity.USDToolkit.Samples.AGVFleet.LiveSyncDemo
{
    public enum PartShape
    {
        Box,
        /// <summary>A Z-up UsdGeom.Plane at <see cref="BoxPart.center"/> (z = top), <see cref="BoxPart.size"/> x/y.</summary>
        Plane,
    }

    /// <summary>One prim of the generated hall (USD frame: Z-up, meters): a box, or the floor plane.</summary>
    public struct BoxPart
    {
        public PartShape shape;
        /// <summary>Prim path below the generated root, e.g. "Racks/Row_01/Seg_02/Upright_03".</summary>
        public string path;
        public Vector3 center;
        public Vector3 size;
        public float yawDeg;
        public string look;
        /// <summary>Gets UsdPhysics.CollisionAPI.</summary>
        public bool collision;
        /// <summary>False for collision proxies (invisible, so renderers skip them).</summary>
        public bool visible;
    }

    /// <summary>A UsdPreviewSurface material.</summary>
    public struct LookDef
    {
        public string name;
        public Color color;
        public float roughness;
        public float metallic;
        public Color emissive;
    }

    /// <summary>
    /// The generated warehouse hall: its geometry (<see cref="Parts"/>, <see cref="Looks"/>) and the layout the
    /// fleet drives on, made together so the lanes always match the geometry.
    /// </summary>
    public sealed class GeneratedFactory
    {
        /// <summary>The values actually built (after fitting to the hall).</summary>
        public GeneratedFactoryParams Params;
        public readonly FleetLayout Layout = new FleetLayout();
        public readonly List<BoxPart> Parts = new List<BoxPart>();
        public readonly List<LookDef> Looks = new List<LookDef>();
        /// <summary>One line per parameter that had to be reduced to fit the hall.</summary>
        public readonly List<string> Adjustments = new List<string>();

        public string ToUsda() => UsdaSceneWriter.Write(this);
    }
}
