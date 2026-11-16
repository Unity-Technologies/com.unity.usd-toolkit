using System;
using System.Collections.Generic;
using System.Linq;
using Unity.USDToolkit.Native;
using UnityEngine;

namespace Unity.USDToolkit
{
    // UsdImportResult.PhysicsMapping: one entry for every physics prim the native read reported,
    // from what UsdPhysicsBuilder made of it (the records it left, the colliders it built) and the
    // notes it wrote, so that an object Unity could not hold as authored is never left out silently.
    internal static class UsdPhysicsMappingLog
    {
        internal static List<UsdPhysicsMappingEntry> Build(UsdNativePhysics physics, Transform root, List<Collider>[] colliderComponents,
                                                           List<string> notes, HashSet<string> mimicked)
        {
            var entries = new List<UsdPhysicsMappingEntry>();
            var bodies = new Dictionary<string, UsdPhysicsBody>(StringComparer.Ordinal);
            var joints = new Dictionary<string, UsdPhysicsJoint>(StringComparer.Ordinal);
            foreach (UsdPhysicsBody body in root.GetComponentsInChildren<UsdPhysicsBody>(true))
            {
                bodies[body.PrimPath] = body;
            }

            foreach (UsdPhysicsJoint joint in root.GetComponentsInChildren<UsdPhysicsJoint>(true))
            {
                joints[joint.PrimPath] = joint;
            }

            for (int i = 0; i < physics.Scenes.Length; i++)
            {
                string path = physics.String(physics.Scenes[i].PathIndex) ?? string.Empty;
                entries.Add(i == 0
                    ? new UsdPhysicsMappingEntry(path, UsdPhysicsPrimKind.Scene, UsdPhysicsMappingStatus.Mapped, nameof(UsdPhysicsScene), string.Empty)
                    : new UsdPhysicsMappingEntry(path, UsdPhysicsPrimKind.Scene, UsdPhysicsMappingStatus.Skipped, string.Empty,
                                                 "Unity has one physics scene; only the first is recorded."));
            }

            foreach (RUsdPhysicsArticulation articulation in physics.Articulations)
            {
                string path = physics.String(articulation.PathIndex) ?? string.Empty;
                bool built = bodies.Values.Any(body => body.ArticulationPath == path && body.TryGetComponent(out ArticulationBody _));
                entries.Add(built
                    ? new UsdPhysicsMappingEntry(path, UsdPhysicsPrimKind.Articulation, UsdPhysicsMappingStatus.Mapped,
                                                 "ArticulationBody tree" + (articulation.FixedBase != 0 ? " (immovable root)" : string.Empty), string.Empty)
                    : new UsdPhysicsMappingEntry(path, UsdPhysicsPrimKind.Articulation, UsdPhysicsMappingStatus.Skipped, string.Empty,
                                                 NoteAbout(notes, path, "Articulation " + path)));
            }

            foreach (RUsdPhysicsBody source in physics.Bodies)
            {
                string path = physics.String(source.PathIndex) ?? string.Empty;
                if (!bodies.TryGetValue(path, out UsdPhysicsBody record))
                {
                    entries.Add(new UsdPhysicsMappingEntry(path, UsdPhysicsPrimKind.Body, UsdPhysicsMappingStatus.Skipped, string.Empty,
                                                           NoteAbout(notes, path, "Body " + path)));
                    continue;
                }

                string unity = record.TryGetComponent(out ArticulationBody _) ? nameof(ArticulationBody)
                    : record.TryGetComponent(out Rigidbody rigidbody) ? (rigidbody.isKinematic ? "Rigidbody (kinematic)" : nameof(Rigidbody))
                    : string.Empty;
                entries.Add(new UsdPhysicsMappingEntry(path, UsdPhysicsPrimKind.Body, Status(record.Note, unity), unity, record.Note));
            }

            for (int i = 0; i < physics.Colliders.Length; i++)
            {
                string path = physics.String(physics.Colliders[i].PathIndex) ?? string.Empty;
                List<Collider> made = colliderComponents != null && i < colliderComponents.Length ? colliderComponents[i] : null;
                string detail = NoteAbout(notes, path, "Collider " + path + ":");
                if (made == null || made.Count == 0)
                {
                    entries.Add(new UsdPhysicsMappingEntry(path, UsdPhysicsPrimKind.Collider, UsdPhysicsMappingStatus.Skipped, string.Empty,
                                                           string.IsNullOrEmpty(detail) ? "No Unity collider was built." : detail));
                    continue;
                }

                entries.Add(new UsdPhysicsMappingEntry(path, UsdPhysicsPrimKind.Collider,
                                                       string.IsNullOrEmpty(detail) ? UsdPhysicsMappingStatus.Mapped : UsdPhysicsMappingStatus.Approximated,
                                                       Describe(made), detail));
            }

            foreach (RUsdPhysicsJoint source in physics.Joints)
            {
                string path = physics.String(source.PathIndex) ?? string.Empty;
                if (!joints.TryGetValue(path, out UsdPhysicsJoint record))
                {
                    string why = source.Enabled == 0 ? "physics:jointEnabled is false." : NoteAbout(notes, path, "Joint " + path);
                    // The joint to the world that makes an articulation's base fixed is held by the root's immovable flag.
                    if (string.IsNullOrEmpty(why) && (source.Body0Index < 0 || source.Body1Index < 0))
                    {
                        entries.Add(new UsdPhysicsMappingEntry(path, UsdPhysicsPrimKind.Joint, UsdPhysicsMappingStatus.Mapped,
                                                               "ArticulationBody.immovable (fixed base)", string.Empty));
                        continue;
                    }

                    entries.Add(new UsdPhysicsMappingEntry(path, UsdPhysicsPrimKind.Joint, UsdPhysicsMappingStatus.Skipped, string.Empty, why));
                    continue;
                }

                if (record.UnityType == UsdPhysicsJointType.Distance)
                {
                    entries.Add(new UsdPhysicsMappingEntry(path, UsdPhysicsPrimKind.Joint, UsdPhysicsMappingStatus.Skipped, string.Empty, record.Note));
                    continue;
                }

                string unity = record.Articulated ? $"ArticulationBody ({record.UnityType} joint)"
                    : record.UnityJoint != null ? record.UnityJoint.GetType().Name
                    : string.Empty;
                entries.Add(new UsdPhysicsMappingEntry(path, UsdPhysicsPrimKind.Joint, Status(record.Note, unity), unity, record.Note));
            }

            foreach (RUsdPhysicsMaterial material in physics.Materials)
            {
                string path = physics.String(material.PathIndex) ?? string.Empty;
                entries.Add(new UsdPhysicsMappingEntry(path, UsdPhysicsPrimKind.Material, UsdPhysicsMappingStatus.Mapped, nameof(PhysicsMaterial),
                                                       material.Density > 0f ? "Density is used only for bodies without mass." : string.Empty));
            }

            foreach (RUsdPhysicsUnsupported entry in physics.Unsupported)
            {
                string path = entry.PathIndex >= 0 ? physics.String(entry.PathIndex) ?? string.Empty : "(stage)";
                // A mimic joint that is followed is part of its joint's entry.
                if (entry.Kind == UsdNative.PhysicsUnsupportedMimicJoint && mimicked.Contains(path))
                {
                    continue;
                }

                entries.Add(new UsdPhysicsMappingEntry(path, UsdPhysicsPrimKind.Other, UsdPhysicsMappingStatus.Skipped, string.Empty,
                                                       NoteAbout(notes, path, path + ":")));
            }

            return entries;
        }

        private static UsdPhysicsMappingStatus Status(string note, string unity) =>
            string.IsNullOrEmpty(unity) ? UsdPhysicsMappingStatus.Skipped
            : string.IsNullOrEmpty(note) ? UsdPhysicsMappingStatus.Mapped
            : UsdPhysicsMappingStatus.Approximated;

        // The builder's note about a prim: the one that starts with `prefix`, else any naming it.
        private static string NoteAbout(List<string> notes, string path, string prefix)
        {
            string any = null;
            foreach (string note in notes)
            {
                if (note.StartsWith(prefix, StringComparison.Ordinal))
                {
                    return note;
                }

                if (any == null && !string.IsNullOrEmpty(path) && note.Contains(path))
                {
                    any = note;
                }
            }

            return any ?? string.Empty;
        }

        // "MeshCollider ×3 (convex)", "BoxCollider".
        private static string Describe(List<Collider> made)
        {
            var parts = new List<string>();
            foreach (IGrouping<string, Collider> group in made.GroupBy(c => c.GetType().Name))
            {
                int count = group.Count();
                string convex = group.First() is MeshCollider mesh ? (mesh.convex ? " (convex)" : " (triangles)") : string.Empty;
                parts.Add(count > 1 ? $"{group.Key} ×{count}{convex}" : group.Key + convex);
            }

            return string.Join(", ", parts);
        }
    }
}
