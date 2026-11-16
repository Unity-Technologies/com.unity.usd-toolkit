using System;
using System.Collections.Generic;
using UnityEngine;

namespace Unity.USDToolkit.Samples.SimReadyCentral
{
    /// <summary>One line of the physics log the info panel lists.</summary>
    [Serializable]
    public struct SimReadyMappingRow
    {
        public string PrimPath;
        public string Kind;
        public string Status;
        public string Unity;
        public string Detail;

        public override string ToString() =>
            string.IsNullOrEmpty(Detail) ? $"[{Status}] {Kind} {PrimPath} -> {Unity}" : $"[{Status}] {Kind} {PrimPath} -> {Unity}: {Detail}";
    }

    /// <summary>
    /// The import's physics log: every UsdPhysics prim as mapped, approximated or skipped
    /// (<see cref="UsdImportResult.PhysicsMapping"/>), plus every PhysxSchema attribute the
    /// composed stage authors, each marked applied, recorded (kept on the joint record but not
    /// simulated) or logged (no Unity counterpart). Nothing is dropped without a line.
    /// </summary>
    public static class SimReadyPhysicsReport
    {
        public const string Mapped = "mapped";
        public const string Approximated = "approximated";
        public const string Skipped = "skipped";
        public const string Applied = "applied";
        public const string Recorded = "recorded";
        public const string Logged = "logged";

        // PhysX attributes the toolkit's physics import reads and applies to Unity physics.
        private static readonly string[] AppliedAttributes =
        {
            "physxArticulation:solverPositionIterationCount", "physxArticulation:solverVelocityIterationCount",
            "physxArticulation:enabledSelfCollisions", "physxArticulation:sleepThreshold",
            "physxCollision:contactOffset",
            "physxJoint:jointFriction",
            "physxMaterial:frictionCombineMode", "physxMaterial:restitutionCombineMode",
            "physxRigidBody:angularDamping", "physxRigidBody:linearDamping", "physxRigidBody:disableGravity",
            "physxRigidBody:enableCCD", "physxRigidBody:maxAngularVelocity", "physxRigidBody:maxLinearVelocity",
            "physxRigidBody:maxDepenetrationVelocity", "physxRigidBody:sleepThreshold",
            "physxRigidBody:solverPositionIterationCount", "physxRigidBody:solverVelocityIterationCount",
            "physxScene:timeStepsPerSecond",
        };

        // Read and kept on UsdPhysicsJoint, but not simulated.
        private static readonly string[] RecordedAttributes = { "physxJoint:maxJointVelocity" };

        public static List<SimReadyMappingRow> Build(UsdImportResult result)
        {
            var rows = new List<SimReadyMappingRow>();
            foreach (UsdPhysicsMappingEntry entry in result.PhysicsMapping)
            {
                rows.Add(new SimReadyMappingRow
                {
                    PrimPath = entry.PrimPath,
                    Kind = entry.Kind.ToString(),
                    Status = entry.Status == UsdPhysicsMappingStatus.Mapped ? Mapped
                        : entry.Status == UsdPhysicsMappingStatus.Approximated ? Approximated
                        : Skipped,
                    Unity = entry.Unity,
                    Detail = entry.Detail,
                });
            }

            UsdStage stage = null;
            try
            {
                stage = result.Source != null ? result.Source.Stage : null;
            }
            catch (Exception exception) when (exception is UsdStageException || exception is NotSupportedException)
            {
                rows.Add(new SimReadyMappingRow { Kind = "PhysxSchema", Status = Skipped, Detail = "The composed stage could not be opened to list PhysX attributes: " + exception.Message });
            }

            if (stage != null)
            {
                AddPhysxAttributes(stage, rows);
            }

            return rows;
        }

        /// <summary>Every <c>physx*</c> attribute on the composed stage (instance proxies included), classified.</summary>
        public static void AddPhysxAttributes(UsdStage stage, List<SimReadyMappingRow> rows)
        {
            foreach (UsdPrim prim in stage.Traverse())
            {
                foreach (UsdAttribute attribute in prim.Attributes)
                {
                    string name = attribute.Name;
                    if (!name.StartsWith("physx", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    string status = Classify(name, out string detail);
                    rows.Add(new SimReadyMappingRow { PrimPath = prim.Path, Kind = "PhysxSchema", Status = status, Unity = name, Detail = detail });
                }
            }
        }

        /// <summary>Whether the import applies, records or only logs a PhysX attribute, and why.</summary>
        public static string Classify(string attribute, out string detail)
        {
            if (attribute.StartsWith("physxMimicJoint:", StringComparison.Ordinal))
            {
                detail = "Followed with a drive (UsdPhysicsMimicJoint): a coupling, not PhysX's constraint.";
                return Applied;
            }

            if (Array.IndexOf(AppliedAttributes, attribute) >= 0 || IsAxisAttribute(attribute, "jointFriction"))
            {
                detail = string.Empty;
                return Applied;
            }

            if (attribute == "physxJoint:armature" || IsAxisAttribute(attribute, "armature"))
            {
                detail = "Added to the link's inertia: Unity's articulation has no armature.";
                return Applied;
            }

            if (Array.IndexOf(RecordedAttributes, attribute) >= 0 || IsAxisAttribute(attribute, "maxJointVelocity"))
            {
                detail = "Kept on UsdPhysicsJoint; not applied to the simulation.";
                return Recorded;
            }

            detail = "No Unity equivalent; logged, not applied.";
            return Logged;
        }

        private static bool IsAxisAttribute(string attribute, string field) =>
            attribute.StartsWith("physxJointAxis:", StringComparison.Ordinal) && attribute.EndsWith(":" + field, StringComparison.Ordinal);

        /// <summary>Counts per status, for the panel's summary line.</summary>
        public static string Summary(IEnumerable<SimReadyMappingRow> rows)
        {
            var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
            foreach (SimReadyMappingRow row in rows)
            {
                counts.TryGetValue(row.Status, out int count);
                counts[row.Status] = count + 1;
            }

            var parts = new List<string>();
            foreach (KeyValuePair<string, int> entry in counts)
            {
                parts.Add($"{entry.Value} {entry.Key}");
            }

            return string.Join(", ", parts);
        }

        /// <summary>Writes the log to the Console, one line per entry, so nothing is only in the panel.</summary>
        public static void Log(string assetName, IReadOnlyList<SimReadyMappingRow> rows)
        {
            var text = new System.Text.StringBuilder();
            text.AppendLine($"SimReady Central: physics of {assetName}: {Summary(rows)}");
            foreach (SimReadyMappingRow row in rows)
            {
                text.AppendLine("  " + row);
            }

            Debug.Log(text.ToString());
        }
    }
}
