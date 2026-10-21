using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace Unity.USDToolkit.RobotPhysics
{
    /// <summary>
    /// Puts what Isaac Sim shows for an imported robot next to what Unity built: for each joint and
    /// body the importer recorded (<see cref="UsdPhysicsJoint"/>, <see cref="UsdPhysicsBody"/>), the
    /// value the stage authors (the number Isaac Sim's Property panel lists for the prim) and the
    /// Unity component's value read back into the same units and sign. A row matches when the two
    /// agree within a relative 1e-4 (axes: within 0.05 degrees).
    /// </summary>
    public sealed class RobotPhysicsReport
    {
        public sealed class Row
        {
            public string Prim;
            public string Property;
            public string IsaacSim;
            public string Unity;

            /// <summary>The value as the Unity component holds it, in Unity's units; empty when the same.</summary>
            public string UnityNative = string.Empty;

            /// <summary>Null for information rows that are not compared.</summary>
            public bool? Match;
        }

        public readonly List<Row> Rows = new List<Row>();
        public readonly List<string> Notes = new List<string>();
        public int JointCount;
        public int BodyCount;

        public int Compared
        {
            get
            {
                int count = 0;
                foreach (Row row in Rows)
                {
                    count += row.Match.HasValue ? 1 : 0;
                }

                return count;
            }
        }

        public int Mismatches
        {
            get
            {
                int count = 0;
                foreach (Row row in Rows)
                {
                    count += row.Match == false ? 1 : 0;
                }

                return count;
            }
        }

        public string Summary =>
            $"{JointCount} joints, {BodyCount} bodies: {Compared - Mismatches} of {Compared} values match Isaac Sim" +
            (Mismatches > 0 ? $", {Mismatches} differ" : string.Empty) +
            (Notes.Count > 0 ? $"; {Notes.Count} note(s)" : string.Empty);

        public static RobotPhysicsReport Build(GameObject root, IEnumerable<string> importNotes = null)
        {
            var report = new RobotPhysicsReport();
            if (root == null)
            {
                return report;
            }

            var joints = new List<UsdPhysicsJoint>(root.GetComponentsInChildren<UsdPhysicsJoint>(true));
            joints.Sort((a, b) => string.CompareOrdinal(a.PrimPath, b.PrimPath));
            foreach (UsdPhysicsJoint joint in joints)
            {
                report.AddJoint(joint);
            }

            var bodies = new List<UsdPhysicsBody>(root.GetComponentsInChildren<UsdPhysicsBody>(true));
            bodies.Sort((a, b) => string.CompareOrdinal(a.PrimPath, b.PrimPath));
            foreach (UsdPhysicsBody body in bodies)
            {
                report.AddBody(body);
            }

            if (importNotes != null)
            {
                report.Notes.AddRange(importNotes);
            }

            return report;
        }

        private void AddJoint(UsdPhysicsJoint joint)
        {
            JointCount++;
            string prim = joint.PrimPath;
            string unityKind = joint.Articulated ? "ArticulationBody " + ArticulationKind(joint.UnityType)
                             : joint.UnityJoint != null ? joint.UnityJoint.GetType().Name
                             : "not built";
            Info(prim, "Type", $"Physics{joint.Type}Joint, axis {joint.Axis}", unityKind);
            if (!string.IsNullOrEmpty(joint.Note))
            {
                Notes.Add($"{prim}: {joint.Note}");
            }

            if (!joint.TryReadUnityValues(out UsdPhysicsJointReadback unity))
            {
                return;
            }

            if (joint.UnityType == UsdPhysicsJointType.Fixed || joint.UnityType == UsdPhysicsJointType.Distance)
            {
                return;
            }

            string unit = joint.IsAngular ? "°" : Length(joint);
            float angle = Vector3.Angle(joint.AxisInStage, unity.AxisInStage);
            Rows.Add(new Row
            {
                Prim = prim,
                Property = "Axis (stage)",
                IsaacSim = Vector(joint.AxisInStage),
                Unity = Vector(unity.AxisInStage),
                UnityNative = $"{angle:0.###}° apart",
                Match = angle < 0.05f,
            });

            if (joint.UnityType == UsdPhysicsJointType.Spherical)
            {
                Compare(prim, "Cone limit", joint.ConeAngle0, unity.UpperLimit, "°");
                return;
            }

            Rows.Add(new Row
            {
                Prim = prim,
                Property = "Limits enabled",
                IsaacSim = joint.LimitEnabled ? "yes" : "no",
                Unity = unity.LimitEnabled ? "yes" : "no",
                Match = joint.LimitEnabled == unity.LimitEnabled,
            });

            if (joint.LimitEnabled)
            {
                Compare(prim, "Lower Limit", joint.LowerLimit, unity.LowerLimit, unit, NativeLimit(joint, lower: true));
                Compare(prim, "Upper Limit", joint.UpperLimit, unity.UpperLimit, unit, NativeLimit(joint, lower: false));
            }

            if (joint.TryGetComponent(out UsdPhysicsMimicJoint mimic))
            {
                Info(prim, "Mimic", $"follows {mimic.ReferenceJointPath}, gearing {mimic.Gearing:0.###}, offset {mimic.Offset:0.###}",
                     $"UsdPhysicsMimicJoint: a drive (stiffness {joint.Link.xDrive.stiffness:0.###}) follows it");
            }

            if (!joint.DriveEnabled)
            {
                return;
            }

            if (!unity.HasDrive)
            {
                Info(prim, "Drive", "authored", "not applied (joint outside an articulation)");
                return;
            }

            string per = joint.IsAngular ? "/°" : "/" + Length(joint);
            ArticulationDrive drive = joint.Link.xDrive;
            Compare(prim, "Drive Stiffness", joint.DriveStiffness, unity.DriveStiffness, per,
                    joint.IsAngular ? $"{drive.stiffness:0.###} N·m/rad" : $"{drive.stiffness:0.###} N/m");
            Compare(prim, "Drive Damping", joint.DriveDamping, unity.DriveDamping, per,
                    joint.IsAngular ? $"{drive.damping:0.###} N·m·s/rad" : $"{drive.damping:0.###} N·s/m");
            Compare(prim, "Drive Max Force", joint.DriveMaxForce, unity.DriveMaxForce, string.Empty,
                    drive.forceLimit >= float.MaxValue ? "unlimited" : $"{drive.forceLimit:0.###} {(joint.IsAngular ? "N·m" : "N")}");
            Compare(prim, "Drive Target Position", joint.DriveTargetPosition, unity.DriveTargetPosition, unit);
            Rows.Add(new Row
            {
                Prim = prim,
                Property = "Drive Type",
                IsaacSim = joint.DriveIsAcceleration ? "acceleration" : "force",
                Unity = drive.driveType.ToString(),
                Match = (drive.driveType == ArticulationDriveType.Acceleration) == joint.DriveIsAcceleration,
            });
        }

        private void AddBody(UsdPhysicsBody body)
        {
            BodyCount++;
            if (!string.IsNullOrEmpty(body.Note))
            {
                Notes.Add($"{body.PrimPath}: {body.Note}");
            }

            if (!body.TryReadUnityValues(out UsdPhysicsBodyReadback unity))
            {
                return;
            }

            if (body.HasMass)
            {
                Compare(body.PrimPath, "Mass", body.Mass, unity.Mass, " kg", null);
            }

            if (body.HasCenterOfMass)
            {
                float distance = Vector3.Distance(body.CenterOfMass, unity.CenterOfMass);
                Rows.Add(new Row
                {
                    Prim = body.PrimPath,
                    Property = "Center of Mass",
                    IsaacSim = Vector(body.CenterOfMass),
                    Unity = Vector(unity.CenterOfMass),
                    Match = distance <= 1e-5f + (1e-4f * body.CenterOfMass.magnitude),
                });
            }

            if (body.HasInertia)
            {
                bool same = Close(body.DiagonalInertia.x, unity.DiagonalInertia.x) &&
                            Close(body.DiagonalInertia.y, unity.DiagonalInertia.y) &&
                            Close(body.DiagonalInertia.z, unity.DiagonalInertia.z);
                Rows.Add(new Row
                {
                    Prim = body.PrimPath,
                    Property = "Diagonal Inertia",
                    IsaacSim = Vector(body.DiagonalInertia, "0.######"),
                    Unity = Vector(unity.DiagonalInertia, "0.######"),
                    Match = same,
                });
            }
        }

        private void Compare(string prim, string property, float authored, float unity, string unit, string native = null)
        {
            Rows.Add(new Row
            {
                Prim = prim,
                Property = property,
                IsaacSim = Number(authored) + unit,
                Unity = Number(unity) + unit,
                UnityNative = native ?? string.Empty,
                Match = Close(authored, unity),
            });
        }

        private void Info(string prim, string property, string authored, string unity)
        {
            Rows.Add(new Row { Prim = prim, Property = property, IsaacSim = authored, Unity = unity });
        }

        private static string NativeLimit(UsdPhysicsJoint joint, bool lower)
        {
            if (!joint.Articulated || joint.Link == null)
            {
                return string.Empty;
            }

            ArticulationDrive drive = joint.Link.xDrive;
            float value = lower ? drive.lowerLimit : drive.upperLimit;
            return joint.IsAngular ? $"{value:0.####}°" : $"{value:0.####} m";
        }

        private static bool Close(float a, float b)
        {
            if (float.IsInfinity(a) || float.IsInfinity(b))
            {
                return a == b;
            }

            return Mathf.Abs(a - b) <= 1e-5f + (1e-4f * Mathf.Max(Mathf.Abs(a), Mathf.Abs(b)));
        }

        private static string Length(UsdPhysicsJoint joint) =>
            Math.Abs(joint.MetersPerUnit - 1.0) < 1e-9 ? " m" : $" units ({joint.MetersPerUnit:0.###} m)";

        private static string ArticulationKind(UsdPhysicsJointType type)
        {
            switch (type)
            {
                case UsdPhysicsJointType.Revolute: return "RevoluteJoint";
                case UsdPhysicsJointType.Prismatic: return "PrismaticJoint";
                case UsdPhysicsJointType.Spherical: return "SphericalJoint";
                default: return "FixedJoint";
            }
        }

        private static string Number(float value) =>
            float.IsPositiveInfinity(value) ? "inf" : value.ToString("0.#####", CultureInfo.InvariantCulture);

        private static string Vector(Vector3 v, string format = "0.####") =>
            "(" + v.x.ToString(format, CultureInfo.InvariantCulture) + ", " + v.y.ToString(format, CultureInfo.InvariantCulture) +
            ", " + v.z.ToString(format, CultureInfo.InvariantCulture) + ")";

        /// <summary>The report as CSV, one row per compared value, notes at the end.</summary>
        public string ToCsv()
        {
            var csv = new StringBuilder("prim,property,isaac_sim,unity,unity_native,match\n");
            foreach (Row row in Rows)
            {
                csv.Append(Quote(row.Prim)).Append(',').Append(Quote(row.Property)).Append(',')
                   .Append(Quote(row.IsaacSim)).Append(',').Append(Quote(row.Unity)).Append(',')
                   .Append(Quote(row.UnityNative)).Append(',')
                   .Append(row.Match.HasValue ? (row.Match.Value ? "yes" : "NO") : string.Empty).Append('\n');
            }

            foreach (string note in Notes)
            {
                csv.Append("note,").Append(Quote(note)).Append(",,,,\n");
            }

            return csv.ToString();
        }

        private static string Quote(string value) => "\"" + (value ?? string.Empty).Replace("\"", "\"\"") + "\"";
    }
}
