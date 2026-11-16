using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using UnityEngine;

namespace Unity.USDToolkit.Samples.SimReadyCentral
{
    /// <summary>
    /// The root layer's <c>customLayerData["SimReady_Metadata"]</c> (asset extents, mass, rigid
    /// body count, type, category, author, licence) and the SimReady profile the asset was
    /// validated against, for the info panel. Values are as authored; extents are in the stage's
    /// units along its own axes (X, Y, Z with Z up for SimReady).
    /// </summary>
    [Serializable]
    public sealed class SimReadyMetadata
    {
        public string AssetName = string.Empty;
        public string AssetType = string.Empty;
        public string Category = string.Empty;
        public string Author = string.Empty;
        public string License = string.Empty;
        public string Profile = string.Empty;
        public string UpAxis = "Y";
        public double MetersPerUnit = 1.0;
        public double KilogramsPerUnit = 1.0;

        public bool HasExtents;
        public Vector3 Extents;

        public bool HasMass;
        public double Mass;

        public bool HasRigidBodyCount;
        public int RigidBodyCount;

        /// <summary>Every customLayerData entry, flattened, for the "all metadata" foldout.</summary>
        public List<string> Keys = new List<string>();
        public List<string> Values = new List<string>();

        private static readonly Regex Tuple = new Regex(@"^\(\s*([-+0-9.eE]+)\s*,\s*([-+0-9.eE]+)\s*,\s*([-+0-9.eE]+)\s*\)$", RegexOptions.CultureInvariant);

        /// <summary>The extents in metres (stage units times metersPerUnit).</summary>
        public Vector3 ExtentsMeters => Extents * (float)MetersPerUnit;

        /// <summary>The asset's mass in kilograms (stage mass units times kilogramsPerUnit).</summary>
        public double MassKilograms => Mass * KilogramsPerUnit;

        public static SimReadyMetadata FromScan(SimReadyLayerScan scan, string validationJsonPath = null)
        {
            var metadata = new SimReadyMetadata
            {
                UpAxis = string.IsNullOrEmpty(scan.UpAxis) ? "Y" : scan.UpAxis,
                MetersPerUnit = scan.MetersPerUnit ?? 1.0,
                KilogramsPerUnit = scan.KilogramsPerUnit ?? 1.0,
            };

            const string prefix = "SimReady_Metadata:";
            Dictionary<string, string> data = scan.CustomLayerData;
            foreach (KeyValuePair<string, string> entry in data)
            {
                metadata.Keys.Add(entry.Key);
                metadata.Values.Add(entry.Value);
            }

            string Get(string key) =>
                data.TryGetValue(prefix + key, out string value) ? value : data.TryGetValue(key, out value) ? value : string.Empty;

            metadata.AssetName = Get("asset_name");
            metadata.AssetType = Get("asset_type");
            metadata.Category = Get("category");
            metadata.Author = Get("author");
            metadata.License = Get("asset_license");

            Match tuple = Tuple.Match(Get("asset_extents"));
            if (tuple.Success)
            {
                metadata.HasExtents = true;
                metadata.Extents = new Vector3(Float(tuple.Groups[1].Value), Float(tuple.Groups[2].Value), Float(tuple.Groups[3].Value));
            }

            if (double.TryParse(Get("mass"), NumberStyles.Float, CultureInfo.InvariantCulture, out double mass))
            {
                metadata.HasMass = true;
                metadata.Mass = mass;
            }

            if (int.TryParse(Get("rigid_body_count"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int count))
            {
                metadata.HasRigidBodyCount = true;
                metadata.RigidBodyCount = count;
            }

            // NVIDIA's assets carry the profile in customLayerData; OnRobot's in .metadata/<asset>_validation.json.
            string profile = Get("validation:profile");
            string version = Get("validation:profile_version");
            if (string.IsNullOrEmpty(profile) && !string.IsNullOrEmpty(validationJsonPath) && File.Exists(validationJsonPath))
            {
                ReadValidation(File.ReadAllText(validationJsonPath), out profile, out version);
            }

            metadata.Profile = string.IsNullOrEmpty(profile) ? string.Empty : string.IsNullOrEmpty(version) ? profile : $"{profile} {version}";
            return metadata;
        }

        // The validation report: { "<root layer>": { "profile_id": "...", "profile_version": "..." } }.
        // Read with a pattern rather than a schema, as the report's outer key is the layer path.
        public static void ReadValidation(string json, out string profile, out string version)
        {
            profile = Field(json, "profile_id");
            version = Field(json, "profile_version");
        }

        private static string Field(string json, string name)
        {
            Match match = Regex.Match(json ?? string.Empty, "\"" + name + "\"\\s*:\\s*\"([^\"]{0,200})\"", RegexOptions.CultureInvariant);
            return match.Success ? match.Groups[1].Value : string.Empty;
        }

        private static float Float(string text) =>
            float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float value) ? value : 0f;
    }
}
