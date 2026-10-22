using System;
using System.Collections.Generic;
using Unity.USDToolkit.Native;
using UnityEngine;

namespace Unity.USDToolkit
{
    // Turns what RUsd_ReadPhysics returned (UsdNative.ReadPhysics) into Unity physics under an
    // imported stage (UsdImportOptions.ImportPhysics):
    //
    //   - each articulation into an ArticulationBody tree, the root immovable when the stage fixes
    //     the base, every other link holding the joint that ties it to its parent. ArticulationBody
    //     needs each link under its parent link in the hierarchy; Isaac Sim robots keep their links
    //     as siblings, so a link whose nearest link ancestor is not its joint parent is moved under
    //     it (the world pose stays; UsdPrimSource keeps the prim path);
    //   - every other rigid body into a Rigidbody, and its joints into a FixedJoint or a
    //     ConfigurableJoint;
    //   - every collider into a Unity collider on a helper child of its body (or of the stage, when
    //     static), so the collider prim itself, often a guide-purpose mesh the import skipped, needs
    //     no GameObject;
    //   - and a UsdPhysicsJoint / UsdPhysicsBody next to each with the values as authored, which
    //     is what checks the mapping (UsdPhysicsJoint.TryReadUnityValues).
    //
    // Space and units: see UsdPhysicsSpace. Everything is placed in the import root's local space,
    // which is the stage with X negated; the root carries the up-axis and unit correction.
    internal static class UsdPhysicsBuilder
    {
        // Unity's ConfigurableJoint takes angular X limits in [-177, 177] degrees.
        private const float ConfigurableAngularLimit = 177f;

        // Collider pairs one UsdPhysicsCollisionFilter holds before the rest are left out.
        private const int MaxFilteredPairs = 200000;

        private sealed class Context
        {
            public UsdNativePhysics Physics;
            public Transform Root;
            public Func<string, bool, GameObject> ObjectForPath;
            public double MetersPerUnit;
            public double KilogramsPerUnit;
            public GameObject[] BodyObjects;
            public Vector3[] BodyScales;
            public ArticulationBody[] Links;
            public Rigidbody[] Rigidbodies;
            public List<Collider>[] BodyColliders;
            public List<Collider>[] ColliderComponents;
            public PhysicsMaterial[] Materials;
            public readonly List<string> Notes = new List<string>();
            public readonly HashSet<int> ArticulatedJoints = new HashSet<int>();
            public readonly HashSet<string> Mimicked = new HashSet<string>(StringComparer.Ordinal);
            public readonly List<KeyValuePair<Collider, Collider>> Filtered = new List<KeyValuePair<Collider, Collider>>();

            public string Path(int index) => Physics.String(index) ?? string.Empty;
        }

        // objectForPath(path, create): the GameObject the import made for a prim path, made when
        // `create` is set and it is missing; null otherwise.
        internal static List<string> Build(UsdNativePhysics physics, Transform root, Func<string, bool, GameObject> objectForPath)
        {
            var context = new Context
            {
                Physics = physics,
                Root = root,
                ObjectForPath = objectForPath,
                MetersPerUnit = physics.MetersPerUnit > 0.0 && !double.IsInfinity(physics.MetersPerUnit) ? physics.MetersPerUnit : 1.0,
                KilogramsPerUnit = physics.KilogramsPerUnit > 0.0 && !double.IsInfinity(physics.KilogramsPerUnit) ? physics.KilogramsPerUnit : 1.0,
            };

            int bodyCount = physics.Bodies.Length;
            context.BodyObjects = new GameObject[bodyCount];
            context.BodyScales = new Vector3[bodyCount];
            context.Links = new ArticulationBody[bodyCount];
            context.Rigidbodies = new Rigidbody[bodyCount];
            context.BodyColliders = new List<Collider>[bodyCount];
            for (int i = 0; i < bodyCount; i++)
            {
                RUsdPhysicsBody body = physics.Bodies[i];
                context.BodyObjects[i] = objectForPath(context.Path(body.PathIndex), true);
                context.BodyScales[i] = UsdPhysicsSpace.SafeScale(body.Scale);
                context.BodyColliders[i] = new List<Collider>();
            }

            BuildMaterials(context);
            for (int i = 0; i < physics.Articulations.Length; i++)
            {
                BuildArticulation(context, i);
            }

            for (int i = 0; i < bodyCount; i++)
            {
                if (context.Links[i] == null && context.BodyObjects[i] != null)
                {
                    BuildRigidbody(context, i);
                }
            }

            BuildColliders(context);
            CheckInertia(context);
            for (int i = 0; i < physics.Joints.Length; i++)
            {
                if (!context.ArticulatedJoints.Contains(i))
                {
                    BuildFreeJoint(context, i);
                }
            }

            BuildMimics(context);
            CollectFilteredPairs(context);
            BuildScene(context);
            ReportUnsupported(context);
            return context.Notes;
        }

        // ---- Articulations ----

        private struct TreeEdge
        {
            public int Parent;
            public int Joint;
            public bool Reversed;
        }

        private static void BuildArticulation(Context context, int articulationIndex)
        {
            UsdNativePhysics physics = context.Physics;
            RUsdPhysicsArticulation articulation = physics.Articulations[articulationIndex];
            string articulationPath = context.Path(articulation.PathIndex);
            int[] members = physics.List(articulation.Bodies);
            var memberSet = new HashSet<int>(members);
            int rootBody = articulation.RootBodyIndex >= 0 ? articulation.RootBodyIndex : members.Length > 0 ? members[0] : -1;
            if (rootBody < 0 || context.BodyObjects[rootBody] == null)
            {
                context.Notes.Add($"Articulation {articulationPath}: no root body; its links import as free rigid bodies.");
                return;
            }

            // Undirected adjacency over the articulation's joints between two of its bodies.
            var adjacency = new Dictionary<int, List<int>>();
            foreach (int jointIndex in physics.List(articulation.Joints))
            {
                RUsdPhysicsJoint joint = physics.Joints[jointIndex];
                if (joint.Enabled == 0 || joint.Body0Index < 0 || joint.Body1Index < 0 ||
                    !memberSet.Contains(joint.Body0Index) || !memberSet.Contains(joint.Body1Index))
                {
                    if (joint.Enabled != 0 && (joint.Body0Index < 0 || joint.Body1Index < 0))
                    {
                        // The joint to the world: articulation.FixedBase already says what it does.
                        context.ArticulatedJoints.Add(jointIndex);
                    }

                    continue;
                }

                AddEdge(adjacency, joint.Body0Index, jointIndex);
                AddEdge(adjacency, joint.Body1Index, jointIndex);
            }

            // Breadth first from the root: the tree. A joint reaching a body already in it closes a
            // loop, which a reduced-coordinate articulation cannot hold.
            var edges = new Dictionary<int, TreeEdge>();
            var order = new List<int> { rootBody };
            var visited = new HashSet<int> { rootBody };
            var used = new HashSet<int>();
            for (int head = 0; head < order.Count; head++)
            {
                int body = order[head];
                if (!adjacency.TryGetValue(body, out List<int> joints))
                {
                    continue;
                }

                foreach (int jointIndex in joints)
                {
                    if (!used.Add(jointIndex))
                    {
                        continue;
                    }

                    RUsdPhysicsJoint joint = physics.Joints[jointIndex];
                    int other = joint.Body0Index == body ? joint.Body1Index : joint.Body0Index;
                    if (!visited.Add(other))
                    {
                        context.Notes.Add($"Joint {context.Path(joint.PathIndex)} closes a loop in articulation {articulationPath}; " +
                                          "it is built as a joint outside the articulation.");
                        continue;
                    }

                    if (context.BodyObjects[other] == null)
                    {
                        continue;
                    }

                    edges[other] = new TreeEdge { Parent = body, Joint = jointIndex, Reversed = joint.Body1Index == body };
                    order.Add(other);
                    context.ArticulatedJoints.Add(jointIndex);
                }
            }

            foreach (int member in members)
            {
                if (!visited.Contains(member))
                {
                    context.Notes.Add($"Body {context.Path(physics.Bodies[member].PathIndex)} has no joint path to the root of " +
                                      $"articulation {articulationPath}; it imports as a free rigid body.");
                }
            }

            // Parents before children: move each link under its parent link where the hierarchy
            // does not already put it there, then add the ArticulationBody.
            var linkTransforms = new HashSet<Transform>();
            foreach (int body in order)
            {
                linkTransforms.Add(context.BodyObjects[body].transform);
            }

            foreach (int body in order)
            {
                Transform transform = context.BodyObjects[body].transform;
                if (edges.TryGetValue(body, out TreeEdge edge))
                {
                    Transform parent = context.BodyObjects[edge.Parent].transform;
                    if (NearestLinkAncestor(transform, linkTransforms) != parent)
                    {
                        transform.SetParent(parent, true);
                    }
                }
                else if (NearestLinkAncestor(transform, linkTransforms) != null)
                {
                    // The root sits under another link of its own articulation: lift it out.
                    transform.SetParent(context.Root, true);
                }

                if (!transform.TryGetComponent(out ArticulationBody link))
                {
                    link = transform.gameObject.AddComponent<ArticulationBody>();
                }

                context.Links[body] = link;
                ConfigureBody(context, body, link, null, articulationPath);
            }

            ArticulationBody rootLink = context.Links[rootBody];
            rootLink.immovable = articulation.FixedBase != 0;
            if ((articulation.SetMask & UsdNative.PhysicsArticulationSetPositionIterations) != 0)
            {
                rootLink.solverIterations = Mathf.Max(1, articulation.SolverPositionIterations);
            }

            if ((articulation.SetMask & UsdNative.PhysicsArticulationSetVelocityIterations) != 0)
            {
                rootLink.solverVelocityIterations = Mathf.Max(1, articulation.SolverVelocityIterations);
            }

            foreach (KeyValuePair<int, TreeEdge> entry in edges)
            {
                ConfigureLinkJoint(context, entry.Key, entry.Value);
            }
        }

        private static void AddEdge(Dictionary<int, List<int>> adjacency, int body, int joint)
        {
            if (!adjacency.TryGetValue(body, out List<int> list))
            {
                list = new List<int>();
                adjacency[body] = list;
            }

            list.Add(joint);
        }

        private static Transform NearestLinkAncestor(Transform transform, HashSet<Transform> links)
        {
            for (Transform current = transform.parent; current != null; current = current.parent)
            {
                if (links.Contains(current))
                {
                    return current;
                }
            }

            return null;
        }

        private static void ConfigureLinkJoint(Context context, int body, TreeEdge edge)
        {
            RUsdPhysicsJoint joint = context.Physics.Joints[edge.Joint];
            ArticulationBody link = context.Links[body];
            UsdPhysicsJoint record = CreateJointRecord(context, edge.Joint, link.gameObject);
            record.Articulated = true;
            record.Link = link;
            record.Reversed = edge.Reversed;

            Frame(context, joint, edge.Reversed ? 1 : 0, edge.Parent, record.UnityType, record.Axis,
                  out Vector3 parentPosition, out Quaternion parentRotation);
            Frame(context, joint, edge.Reversed ? 0 : 1, body, record.UnityType, record.Axis,
                  out Vector3 childPosition, out Quaternion childRotation);

            link.matchAnchors = false;
            link.anchorPosition = childPosition;
            link.anchorRotation = childRotation;
            link.parentAnchorPosition = parentPosition;
            link.parentAnchorRotation = parentRotation;

            switch (record.UnityType)
            {
                case UsdPhysicsJointType.Revolute:
                    link.jointType = ArticulationJointType.RevoluteJoint;
                    link.twistLock = LockFor(record);
                    link.xDrive = MakeDrive(record);
                    break;
                case UsdPhysicsJointType.Prismatic:
                    link.jointType = ArticulationJointType.PrismaticJoint;
                    link.linearLockX = LockFor(record);
                    link.linearLockY = ArticulationDofLock.LockedMotion;
                    link.linearLockZ = ArticulationDofLock.LockedMotion;
                    link.xDrive = MakeDrive(record);
                    break;
                case UsdPhysicsJointType.Spherical:
                    link.jointType = ArticulationJointType.SphericalJoint;
                    link.twistLock = ArticulationDofLock.FreeMotion;
                    ArticulationDofLock swing = record.ConeLimitEnabled ? ArticulationDofLock.LimitedMotion : ArticulationDofLock.FreeMotion;
                    link.swingYLock = swing;
                    link.swingZLock = swing;
                    ArticulationDrive y = link.yDrive;
                    y.lowerLimit = -record.ConeAngle1;
                    y.upperLimit = record.ConeAngle1;
                    link.yDrive = y;
                    ArticulationDrive z = link.zDrive;
                    z.lowerLimit = -record.ConeAngle0;
                    z.upperLimit = record.ConeAngle0;
                    link.zDrive = z;
                    break;
                default:
                    link.jointType = ArticulationJointType.FixedJoint;
                    break;
            }

            if (record.JointFriction > 0f)
            {
                link.jointFriction = record.JointFriction;
            }
        }

        private static ArticulationDofLock LockFor(UsdPhysicsJoint record)
        {
            if (!record.LimitEnabled)
            {
                return ArticulationDofLock.FreeMotion;
            }

            return record.LowerLimit > record.UpperLimit ? ArticulationDofLock.LockedMotion : ArticulationDofLock.LimitedMotion;
        }

        private static ArticulationDrive MakeDrive(UsdPhysicsJoint record)
        {
            UsdPhysicsSpace.DriveScales(record.IsAngular, record.MetersPerUnit, record.KilogramsPerUnit,
                                        out float length, out float stiffness, out float force);
            float sign = record.Reversed ? -1f : 1f;
            float lower = record.LowerLimit * length;
            float upper = record.UpperLimit * length;
            var drive = new ArticulationDrive
            {
                lowerLimit = record.Reversed ? -upper : lower,
                upperLimit = record.Reversed ? -lower : upper,
                forceLimit = float.MaxValue,
                driveType = record.DriveIsAcceleration ? ArticulationDriveType.Acceleration : ArticulationDriveType.Force,
            };

            if (record.DriveEnabled)
            {
                drive.stiffness = record.DriveStiffness * stiffness;
                drive.damping = record.DriveDamping * stiffness;
                drive.forceLimit = float.IsInfinity(record.DriveMaxForce) || record.DriveMaxForce * force >= float.MaxValue
                    ? float.MaxValue
                    : record.DriveMaxForce * force;
                drive.target = sign * record.DriveTargetPosition * length;
                drive.targetVelocity = sign * record.DriveTargetVelocity * length;
            }

            return drive;
        }

        // ---- The joint record ----

        private static UsdPhysicsJoint CreateJointRecord(Context context, int jointIndex, GameObject host)
        {
            RUsdPhysicsJoint joint = context.Physics.Joints[jointIndex];
            UsdPhysicsJoint record = host.AddComponent<UsdPhysicsJoint>();
            record.PrimPath = context.Path(joint.PathIndex);
            record.Body0Path = joint.Body0Index >= 0 ? context.Path(context.Physics.Bodies[joint.Body0Index].PathIndex) : string.Empty;
            record.Body1Path = joint.Body1Index >= 0 ? context.Path(context.Physics.Bodies[joint.Body1Index].PathIndex) : string.Empty;
            record.Type = JointType(joint.Type);
            record.StageRoot = context.Root;
            record.MetersPerUnit = context.MetersPerUnit;
            record.KilogramsPerUnit = context.KilogramsPerUnit;
            record.CollisionEnabled = joint.CollisionEnabled != 0;
            record.ConeLimitEnabled = joint.ConeLimitEnabled != 0;
            record.ConeAngle0 = joint.ConeAngle0;
            record.ConeAngle1 = joint.ConeAngle1;

            int axis = joint.Axis;
            int dof = -1;
            switch (record.Type)
            {
                case UsdPhysicsJointType.Revolute:
                    record.UnityType = UsdPhysicsJointType.Revolute;
                    dof = UsdNative.PhysicsDofRotX + axis;
                    break;
                case UsdPhysicsJointType.Prismatic:
                    record.UnityType = UsdPhysicsJointType.Prismatic;
                    dof = UsdNative.PhysicsDofTransX + axis;
                    break;
                case UsdPhysicsJointType.Spherical:
                    record.UnityType = UsdPhysicsJointType.Spherical;
                    break;
                case UsdPhysicsJointType.Generic:
                    record.UnityType = MapGeneric(joint, out dof);
                    if (dof >= 0)
                    {
                        axis = dof >= UsdNative.PhysicsDofRotX ? dof - UsdNative.PhysicsDofRotX : dof - UsdNative.PhysicsDofTransX;
                    }

                    if (record.UnityType == UsdPhysicsJointType.Fixed && HasFreeDof(joint))
                    {
                        record.Note = "More than one free degree of freedom: Unity has no general six-axis articulation joint, so it is held fixed.";
                    }

                    break;
                case UsdPhysicsJointType.Distance:
                    record.UnityType = UsdPhysicsJointType.Distance;
                    record.Note = "Distance joints have no Unity counterpart; not built.";
                    break;
                default:
                    record.UnityType = UsdPhysicsJointType.Fixed;
                    break;
            }

            record.Axis = (UsdPhysicsAxis)Mathf.Clamp(axis, 0, 2);
            if (dof >= 0 && joint.Dofs != null && dof < joint.Dofs.Length)
            {
                RUsdPhysicsJointDof d = joint.Dofs[dof];
                record.LimitEnabled = d.LimitEnabled != 0;
                record.LowerLimit = d.Lower;
                record.UpperLimit = d.Upper;
                record.DriveEnabled = d.DriveEnabled != 0;
                record.DriveIsAcceleration = d.DriveIsAcceleration != 0;
                record.DriveStiffness = d.Stiffness;
                record.DriveDamping = d.Damping;
                record.DriveMaxForce = d.MaxForce;
                record.DriveTargetPosition = d.TargetPosition;
                record.DriveTargetVelocity = d.TargetVelocity;
                record.HasInitialPosition = (d.SetMask & UsdNative.PhysicsDofSetStatePosition) != 0;
                record.InitialPosition = d.StatePosition;
                record.JointFriction = (d.SetMask & UsdNative.PhysicsDofSetFriction) != 0 ? d.JointFriction : 0f;
                record.MaxJointVelocity = (d.SetMask & UsdNative.PhysicsDofSetMaxVelocity) != 0 ? d.MaxJointVelocity : float.PositiveInfinity;
                record.Armature = (d.SetMask & UsdNative.PhysicsDofSetArmature) != 0 ? d.Armature : 0f;
            }

            // The axis in stage space: body0's world rotation (or the world) times localRot0.
            Quaternion frame0 = UsdPhysicsSpace.UsdRotation(joint.LocalRotation0);
            if (joint.Body0Index >= 0)
            {
                frame0 = UsdPhysicsSpace.UsdRotation(context.Physics.Bodies[joint.Body0Index].Rotation) * frame0;
            }

            record.AxisInStage = (frame0 * UsdPhysicsSpace.AxisVector((int)record.Axis)).normalized;
            return record;
        }

        private static UsdPhysicsJointType JointType(int type)
        {
            switch (type)
            {
                case UsdNative.PhysicsJointRevolute: return UsdPhysicsJointType.Revolute;
                case UsdNative.PhysicsJointPrismatic: return UsdPhysicsJointType.Prismatic;
                case UsdNative.PhysicsJointSpherical: return UsdPhysicsJointType.Spherical;
                case UsdNative.PhysicsJointDistance: return UsdPhysicsJointType.Distance;
                case UsdNative.PhysicsJointD6: return UsdPhysicsJointType.Generic;
                default: return UsdPhysicsJointType.Fixed;
            }
        }

        // A generic joint's degrees of freedom: locked when its limit has lower above upper, free
        // otherwise (limited or not). One free rotation is a revolute joint about that axis, one
        // free translation a prismatic one; anything else is held fixed.
        private static UsdPhysicsJointType MapGeneric(RUsdPhysicsJoint joint, out int dof)
        {
            dof = -1;
            int freeRotations = 0;
            int freeTranslations = 0;
            for (int i = UsdNative.PhysicsDofTransX; i <= UsdNative.PhysicsDofRotZ; i++)
            {
                if (IsLocked(joint, i))
                {
                    continue;
                }

                if (i >= UsdNative.PhysicsDofRotX)
                {
                    freeRotations++;
                }
                else
                {
                    freeTranslations++;
                }

                dof = i;
            }

            if (freeRotations == 1 && freeTranslations == 0)
            {
                return UsdPhysicsJointType.Revolute;
            }

            if (freeRotations == 0 && freeTranslations == 1)
            {
                return UsdPhysicsJointType.Prismatic;
            }

            dof = -1;
            return UsdPhysicsJointType.Fixed;
        }

        private static bool HasFreeDof(RUsdPhysicsJoint joint)
        {
            for (int i = UsdNative.PhysicsDofTransX; i <= UsdNative.PhysicsDofRotZ; i++)
            {
                if (!IsLocked(joint, i))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsLocked(RUsdPhysicsJoint joint, int dof) =>
            joint.Dofs == null || dof >= joint.Dofs.Length ||
            (joint.Dofs[dof].LimitEnabled != 0 && joint.Dofs[dof].Lower > joint.Dofs[dof].Upper);

        // A joint frame (side 0 or 1) as a Unity anchor in its body's local space: the position
        // with the body's scale taken out (the parser reports it scaled), and a rotation whose X
        // is the joint axis as Unity drives it. For a rotation that is -B(axis), so angles and
        // their limits keep their USD sign; for a translation +B(axis), so distances do (see
        // UsdPhysicsSpace). Y and Z follow the axis cyclically, which keeps a spherical joint's
        // cone angles on the axes UsdPhysics names them for.
        private static void Frame(Context context, RUsdPhysicsJoint joint, int side, int body, UsdPhysicsJointType type,
                                  UsdPhysicsAxis axis, out Vector3 position, out Quaternion rotation)
        {
            RUsdVec3 localPosition = side == 0 ? joint.LocalPosition0 : joint.LocalPosition1;
            RUsdVec4 localRotation = side == 0 ? joint.LocalRotation0 : joint.LocalRotation1;
            Vector3 scale = body >= 0 ? context.BodyScales[body] : Vector3.one;
            position = UsdPhysicsSpace.Divide(UsdPhysicsSpace.Position(localPosition), scale);
            rotation = UsdPhysicsSpace.Mirror(UsdPhysicsSpace.UsdRotation(localRotation) * AnchorFrame(type, axis));
        }

        internal static Quaternion AnchorFrame(UsdPhysicsJointType type, UsdPhysicsAxis axis)
        {
            // x -> axis, y -> next, z -> the one after (cyclic): 0, 120 and 240 degrees about (1, 1, 1).
            Quaternion cyclic = axis == UsdPhysicsAxis.Y ? new Quaternion(0.5f, 0.5f, 0.5f, 0.5f)
                              : axis == UsdPhysicsAxis.Z ? new Quaternion(-0.5f, -0.5f, -0.5f, 0.5f)
                              : Quaternion.identity;
            // A translation turns X onto -axis (half a turn about Z), so Mirror's -B lands on +B(axis).
            return type == UsdPhysicsJointType.Prismatic ? cyclic * new Quaternion(0f, 0f, 1f, 0f) : cyclic;
        }

        // ---- Bodies ----

        private static void ConfigureBody(Context context, int index, ArticulationBody link, Rigidbody rigidbody, string articulationPath)
        {
            RUsdPhysicsBody body = context.Physics.Bodies[index];
            GameObject host = context.BodyObjects[index];
            if (!host.TryGetComponent(out UsdPhysicsBody record))
            {
                record = host.AddComponent<UsdPhysicsBody>();
            }

            record.PrimPath = context.Path(body.PathIndex);
            record.ArticulationPath = articulationPath ?? string.Empty;
            record.StageRoot = context.Root;
            record.MetersPerUnit = context.MetersPerUnit;
            record.KilogramsPerUnit = context.KilogramsPerUnit;
            record.HasMass = (body.SetMask & UsdNative.PhysicsBodySetMass) != 0 && body.Mass > 0f;
            record.Mass = body.Mass;
            record.HasCenterOfMass = (body.SetMask & UsdNative.PhysicsBodySetCenterOfMass) != 0;
            record.CenterOfMass = UsdPhysicsSpace.UsdVector(body.CenterOfMass);
            record.HasInertia = (body.SetMask & UsdNative.PhysicsBodySetInertia) != 0 &&
                                body.DiagonalInertia.X > 0f && body.DiagonalInertia.Y > 0f && body.DiagonalInertia.Z > 0f;
            record.DiagonalInertia = UsdPhysicsSpace.UsdVector(body.DiagonalInertia);
            record.PrincipalAxes = (body.SetMask & UsdNative.PhysicsBodySetPrincipalAxes) != 0
                ? UsdPhysicsSpace.UsdRotation(body.PrincipalAxes)
                : Quaternion.identity;
            record.Kinematic = body.Kinematic != 0 || body.Enabled == 0;
            record.DisableGravity = (body.SetMask & UsdNative.PhysicsBodySetDisableGravity) != 0 && body.DisableGravity != 0;
            record.StartsAsleep = body.StartsAsleep != 0;
            record.LinearDamping = (body.SetMask & UsdNative.PhysicsBodySetLinearDamping) != 0 ? body.LinearDamping : 0f;
            record.AngularDamping = (body.SetMask & UsdNative.PhysicsBodySetAngularDamping) != 0 ? body.AngularDamping : 0.05f;
            record.LinearVelocity = UsdPhysicsSpace.UsdVector(body.LinearVelocity);
            record.AngularVelocity = UsdPhysicsSpace.UsdVector(body.AngularVelocity);

            double kpu = context.KilogramsPerUnit;
            double mpu = context.MetersPerUnit;
            float mass = record.HasMass ? (float)(body.Mass * kpu) : ColliderMass(context, index, record);
            Vector3 centerOfMass = UsdPhysicsSpace.Position(body.CenterOfMass);
            Vector3 inertia = record.DiagonalInertia * (float)(kpu * mpu * mpu);
            Quaternion inertiaRotation = UsdPhysicsSpace.Mirror(record.PrincipalAxes);
            float maxDepenetration = (body.SetMask & UsdNative.PhysicsBodySetMaxDepenetration) != 0 && !float.IsInfinity(body.MaxDepenetrationVelocity)
                ? (float)(body.MaxDepenetrationVelocity * mpu)
                : -1f;

            if (link != null)
            {
                if (mass > 0f)
                {
                    link.mass = mass;
                }

                if (record.HasCenterOfMass)
                {
                    link.automaticCenterOfMass = false;
                    link.centerOfMass = centerOfMass;
                }

                if (record.HasInertia)
                {
                    link.automaticInertiaTensor = false;
                    link.inertiaTensor = inertia;
                    link.inertiaTensorRotation = inertiaRotation;
                }

                link.useGravity = !record.DisableGravity;
                link.linearDamping = record.LinearDamping;
                link.angularDamping = record.AngularDamping;
                if ((body.SetMask & UsdNative.PhysicsBodySetMaxAngularVelocity) != 0 && !float.IsInfinity(body.MaxAngularVelocity))
                {
                    link.maxAngularVelocity = body.MaxAngularVelocity * Mathf.Deg2Rad;
                }

                if (maxDepenetration >= 0f)
                {
                    link.maxDepenetrationVelocity = maxDepenetration;
                }

                if ((body.SetMask & UsdNative.PhysicsBodySetSleepThreshold) != 0)
                {
                    link.sleepThreshold = (float)(body.SleepThreshold * mpu * mpu);
                }

                if (record.Kinematic)
                {
                    record.Note = AppendNote(record.Note, "Kinematic links are not supported in an articulation; simulated as dynamic.");
                }

                return;
            }

            if (mass > 0f)
            {
                rigidbody.mass = mass;
            }

            if (record.HasCenterOfMass)
            {
                rigidbody.automaticCenterOfMass = false;
                rigidbody.centerOfMass = centerOfMass;
            }

            if (record.HasInertia)
            {
                rigidbody.automaticInertiaTensor = false;
                rigidbody.inertiaTensor = inertia;
                rigidbody.inertiaTensorRotation = inertiaRotation;
            }

            rigidbody.isKinematic = record.Kinematic;
            rigidbody.useGravity = !record.DisableGravity;
            rigidbody.linearDamping = record.LinearDamping;
            rigidbody.angularDamping = record.AngularDamping;
            if ((body.SetMask & UsdNative.PhysicsBodySetMaxAngularVelocity) != 0 && !float.IsInfinity(body.MaxAngularVelocity))
            {
                rigidbody.maxAngularVelocity = body.MaxAngularVelocity * Mathf.Deg2Rad;
            }

            if ((body.SetMask & UsdNative.PhysicsBodySetMaxLinearVelocity) != 0 && !float.IsInfinity(body.MaxLinearVelocity))
            {
                rigidbody.maxLinearVelocity = (float)(body.MaxLinearVelocity * mpu);
            }

            if (maxDepenetration >= 0f)
            {
                rigidbody.maxDepenetrationVelocity = maxDepenetration;
            }

            if ((body.SetMask & UsdNative.PhysicsBodySetSleepThreshold) != 0)
            {
                rigidbody.sleepThreshold = (float)(body.SleepThreshold * mpu * mpu);
            }

            if ((body.SetMask & UsdNative.PhysicsBodySetPositionIterations) != 0)
            {
                rigidbody.solverIterations = Mathf.Max(1, body.SolverPositionIterations);
            }

            if ((body.SetMask & UsdNative.PhysicsBodySetVelocityIterations) != 0)
            {
                rigidbody.solverVelocityIterations = Mathf.Max(1, body.SolverVelocityIterations);
            }

            if ((body.SetMask & UsdNative.PhysicsBodySetCcd) != 0 && body.EnableCcd != 0)
            {
                rigidbody.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            }
        }

        // A body without PhysicsMassAPI mass of its own: the sum of the masses its colliders author,
        // else 0, which leaves Unity's default of 1 kg and says so.
        private static float ColliderMass(Context context, int body, UsdPhysicsBody record)
        {
            double total = 0.0;
            foreach (int colliderIndex in context.Physics.List(context.Physics.Bodies[body].Colliders))
            {
                RUsdPhysicsCollider collider = context.Physics.Colliders[colliderIndex];
                if ((collider.SetMask & UsdNative.PhysicsColliderSetMass) != 0 && collider.Mass > 0f)
                {
                    total += collider.Mass;
                }
            }

            if (total > 0.0)
            {
                record.Note = AppendNote(record.Note, "Mass is the sum of the colliders' authored masses.");
                return (float)(total * context.KilogramsPerUnit);
            }

            record.Note = AppendNote(record.Note, "No mass authored: Unity's default of 1 kg applies (Isaac Sim derives it from collider volume and density).");
            return 0f;
        }

        private static void BuildRigidbody(Context context, int index)
        {
            GameObject host = context.BodyObjects[index];
            if (host.TryGetComponent(out ArticulationBody _))
            {
                return;
            }

            if (!host.TryGetComponent(out Rigidbody rigidbody))
            {
                rigidbody = host.AddComponent<Rigidbody>();
            }

            context.Rigidbodies[index] = rigidbody;
            ConfigureBody(context, index, null, rigidbody, null);
        }

        // An authored inertia far below what the body's colliders and mass give is a placeholder:
        // Isaac Sim's Franka Panda (6.0) authors about 1e-6 kg·m² for links of 2 to 3 kg, a thousand
        // times below a real link, which leaves a driven joint swinging past its limits in Unity's
        // PhysX. The body keeps the authored value; the collider one is recorded beside it.
        private static void CheckInertia(Context context)
        {
            // The colliders were just made: let the bodies see them before deriving from them.
            Physics.SyncTransforms();
            int flagged = 0;
            for (int i = 0; i < context.BodyObjects.Length; i++)
            {
                GameObject host = context.BodyObjects[i];
                if (host == null || !host.TryGetComponent(out UsdPhysicsBody record) || !record.HasInertia ||
                    context.BodyColliders[i].Count == 0)
                {
                    continue;
                }

                Vector3 authored;
                Quaternion authoredRotation;
                Vector3 derived;
                Quaternion derivedRotation;
                if (context.Links[i] != null)
                {
                    ArticulationBody link = context.Links[i];
                    authored = link.inertiaTensor;
                    authoredRotation = link.inertiaTensorRotation;
                    link.ResetInertiaTensor();
                    derived = link.inertiaTensor;
                    derivedRotation = link.inertiaTensorRotation;
                    link.automaticInertiaTensor = false;
                    link.inertiaTensor = authored;
                    link.inertiaTensorRotation = authoredRotation;
                }
                else if (context.Rigidbodies[i] != null)
                {
                    Rigidbody body = context.Rigidbodies[i];
                    authored = body.inertiaTensor;
                    authoredRotation = body.inertiaTensorRotation;
                    body.ResetInertiaTensor();
                    derived = body.inertiaTensor;
                    derivedRotation = body.inertiaTensorRotation;
                    body.automaticInertiaTensor = false;
                    body.inertiaTensor = authored;
                    body.inertiaTensorRotation = authoredRotation;
                }
                else
                {
                    continue;
                }

                record.ColliderInertia = derived;
                record.ColliderInertiaRotation = derivedRotation;
                float authoredMax = Mathf.Max(authored.x, Mathf.Max(authored.y, authored.z));
                float derivedMax = Mathf.Max(derived.x, Mathf.Max(derived.y, derived.z));
                record.ImplausibleInertia = derivedMax > 0f && authoredMax < 1e-3f * derivedMax;
                if (record.ImplausibleInertia)
                {
                    flagged++;
                    record.Note = AppendNote(record.Note,
                        $"Authored diagonalInertia (max {authoredMax:G3} kg·m²) is over 1000× below what its colliders and mass give " +
                        $"({derivedMax:G3} kg·m²); kept as authored (UsdPhysicsBody.UseColliderInertia switches).");
                }
            }

            if (flagged > 0)
            {
                context.Notes.Add($"{flagged} bodies author an inertia over 1000× below what their colliders and mass give; " +
                                  "kept as authored, which Unity's PhysX may not keep stable under drives (UsdPhysicsBody.UseColliderInertia).");
            }
        }

        // ---- Joints outside an articulation ----

        private static void BuildFreeJoint(Context context, int jointIndex)
        {
            RUsdPhysicsJoint joint = context.Physics.Joints[jointIndex];
            if (joint.Enabled == 0)
            {
                return;
            }

            // Unity puts a joint on one body and connects it to the other: body1 if it has one,
            // else body0 against the world.
            int child = joint.Body1Index >= 0 ? joint.Body1Index : joint.Body0Index;
            int parent = joint.Body1Index >= 0 ? joint.Body0Index : -1;
            bool reversed = joint.Body1Index < 0;
            if (child < 0 || context.BodyObjects[child] == null)
            {
                context.Notes.Add($"Joint {context.Path(joint.PathIndex)} connects no rigid body; not built.");
                return;
            }

            GameObject host = context.BodyObjects[child];
            bool isLink = host.TryGetComponent(out ArticulationBody _);
            if (!host.TryGetComponent(out Rigidbody _) && !isLink)
            {
                context.Notes.Add($"Joint {context.Path(joint.PathIndex)}: body {context.Path(context.Physics.Bodies[child].PathIndex)} has no Unity body; not built.");
                return;
            }

            if (isLink)
            {
                context.Notes.Add($"Joint {context.Path(joint.PathIndex)} ties an articulation link to another body outside the tree; " +
                                  "Unity joints cannot sit on an articulation link, so it is not built.");
                return;
            }

            // The record goes on a helper so a body with several joints keeps one per joint.
            GameObject recordHost = CreateHelper(context, host.transform, "Joint " + LastSegment(context.Path(joint.PathIndex)));
            UsdPhysicsJoint record = CreateJointRecord(context, jointIndex, recordHost);
            record.Reversed = reversed;
            if (record.UnityType == UsdPhysicsJointType.Distance)
            {
                return;
            }

            Frame(context, joint, reversed ? 1 : 0, parent, record.UnityType, record.Axis, out Vector3 parentPosition, out Quaternion parentRotation);
            Frame(context, joint, reversed ? 0 : 1, child, record.UnityType, record.Axis, out Vector3 childPosition, out Quaternion childRotation);

            Joint unityJoint;
            if (record.UnityType == UsdPhysicsJointType.Fixed)
            {
                unityJoint = host.AddComponent<FixedJoint>();
            }
            else
            {
                ConfigurableJoint configurable = host.AddComponent<ConfigurableJoint>();
                configurable.xMotion = ConfigurableJointMotion.Locked;
                configurable.yMotion = ConfigurableJointMotion.Locked;
                configurable.zMotion = ConfigurableJointMotion.Locked;
                configurable.angularXMotion = ConfigurableJointMotion.Locked;
                configurable.angularYMotion = ConfigurableJointMotion.Locked;
                configurable.angularZMotion = ConfigurableJointMotion.Locked;
                ConfigureFreeJoint(context, record, configurable, ref parentPosition, parentRotation, parent);
                configurable.axis = childRotation * Vector3.right;
                configurable.secondaryAxis = childRotation * Vector3.up;
                unityJoint = configurable;
            }

            unityJoint.autoConfigureConnectedAnchor = false;
            unityJoint.anchor = childPosition;
            if (parent >= 0)
            {
                if (context.Rigidbodies[parent] != null)
                {
                    unityJoint.connectedBody = context.Rigidbodies[parent];
                }
                else if (context.Links[parent] != null)
                {
                    unityJoint.connectedArticulationBody = context.Links[parent];
                }

                unityJoint.connectedAnchor = parentPosition;
            }
            else
            {
                // Against the world: the frame is in stage space, the anchor in world space.
                unityJoint.connectedAnchor = context.Root.TransformPoint(parentPosition);
            }

            unityJoint.enableCollision = record.CollisionEnabled;
            if (!float.IsInfinity(joint.BreakForce) && joint.BreakForce < 1e30f)
            {
                unityJoint.breakForce = (float)(joint.BreakForce * context.KilogramsPerUnit * context.MetersPerUnit);
            }

            if (!float.IsInfinity(joint.BreakTorque) && joint.BreakTorque < 1e30f)
            {
                unityJoint.breakTorque = (float)(joint.BreakTorque * context.KilogramsPerUnit * context.MetersPerUnit * context.MetersPerUnit);
            }

            record.UnityJoint = unityJoint;
            if (record.DriveEnabled)
            {
                record.Note = AppendNote(record.Note, "Drives on joints outside an articulation are not applied.");
            }
        }

        private static void ConfigureFreeJoint(Context context, UsdPhysicsJoint record, ConfigurableJoint configurable,
                                               ref Vector3 parentPosition, Quaternion parentRotation, int parent)
        {
            float lower = record.Reversed ? -record.UpperLimit : record.LowerLimit;
            float upper = record.Reversed ? -record.LowerLimit : record.UpperLimit;
            switch (record.UnityType)
            {
                case UsdPhysicsJointType.Revolute:
                {
                    ConfigurableJointMotion motion = MotionFor(record);
                    configurable.angularXMotion = motion;
                    if (motion != ConfigurableJointMotion.Limited)
                    {
                        break;
                    }

                    float low = Mathf.Clamp(lower, -ConfigurableAngularLimit, ConfigurableAngularLimit);
                    float high = Mathf.Clamp(upper, -ConfigurableAngularLimit, ConfigurableAngularLimit);
                    if (low != lower || high != upper)
                    {
                        record.Note = AppendNote(record.Note, $"Limits beyond ±{ConfigurableAngularLimit}° are clamped to it outside an articulation.");
                    }

                    // The joint measures the other way round (UsdPhysicsSpace.ConfigurableAngularSign).
                    float s = UsdPhysicsSpace.ConfigurableAngularSign;
                    configurable.lowAngularXLimit = new SoftJointLimit { limit = Mathf.Min(low * s, high * s) };
                    configurable.highAngularXLimit = new SoftJointLimit { limit = Mathf.Max(low * s, high * s) };
                    break;
                }

                case UsdPhysicsJointType.Prismatic:
                {
                    ConfigurableJointMotion motion = MotionFor(record);
                    configurable.xMotion = motion;
                    if (motion != ConfigurableJointMotion.Limited)
                    {
                        break;
                    }

                    // Unity's linear limit is symmetric about the connected anchor: move the anchor
                    // to the middle of the authored range so [lower, upper] is kept.
                    float middle = 0.5f * (lower + upper);
                    float half = 0.5f * (upper - lower);
                    configurable.linearLimit = new SoftJointLimit { limit = (float)(half * context.MetersPerUnit) };
                    Vector3 scale = parent >= 0 ? context.BodyScales[parent] : Vector3.one;
                    parentPosition += UsdPhysicsSpace.Divide(parentRotation * Vector3.right * middle, scale);
                    break;
                }

                case UsdPhysicsJointType.Spherical:
                    configurable.angularXMotion = ConfigurableJointMotion.Free;
                    ConfigurableJointMotion swing = record.ConeLimitEnabled ? ConfigurableJointMotion.Limited : ConfigurableJointMotion.Free;
                    configurable.angularYMotion = swing;
                    configurable.angularZMotion = swing;
                    configurable.angularYLimit = new SoftJointLimit { limit = Mathf.Min(record.ConeAngle1, ConfigurableAngularLimit) };
                    configurable.angularZLimit = new SoftJointLimit { limit = Mathf.Min(record.ConeAngle0, ConfigurableAngularLimit) };
                    break;
            }
        }

        private static ConfigurableJointMotion MotionFor(UsdPhysicsJoint record)
        {
            if (!record.LimitEnabled)
            {
                return ConfigurableJointMotion.Free;
            }

            return record.LowerLimit > record.UpperLimit ? ConfigurableJointMotion.Locked : ConfigurableJointMotion.Limited;
        }

        // ---- Colliders ----

        private static void BuildMaterials(Context context)
        {
            RUsdPhysicsMaterial[] materials = context.Physics.Materials;
            context.Materials = new PhysicsMaterial[materials.Length];
            for (int i = 0; i < materials.Length; i++)
            {
                RUsdPhysicsMaterial source = materials[i];
                context.Materials[i] = new PhysicsMaterial(LastSegment(context.Path(source.PathIndex)))
                {
                    staticFriction = source.StaticFriction,
                    dynamicFriction = source.DynamicFriction,
                    bounciness = Mathf.Clamp01(source.Restitution),
                    frictionCombine = (source.SetMask & UsdNative.PhysicsMaterialSetFrictionCombine) != 0
                        ? Combine(source.FrictionCombineMode)
                        : PhysicsMaterialCombine.Average,
                    bounceCombine = (source.SetMask & UsdNative.PhysicsMaterialSetRestitutionCombine) != 0
                        ? Combine(source.RestitutionCombineMode)
                        : PhysicsMaterialCombine.Average,
                };
            }
        }

        private static PhysicsMaterialCombine Combine(int mode)
        {
            switch (mode)
            {
                case UsdNative.PhysicsCombineMin: return PhysicsMaterialCombine.Minimum;
                case UsdNative.PhysicsCombineMultiply: return PhysicsMaterialCombine.Multiply;
                case UsdNative.PhysicsCombineMax: return PhysicsMaterialCombine.Maximum;
                default: return PhysicsMaterialCombine.Average;
            }
        }

        private static void BuildColliders(Context context)
        {
            RUsdPhysicsCollider[] colliders = context.Physics.Colliders;
            context.ColliderComponents = new List<Collider>[colliders.Length];
            for (int i = 0; i < colliders.Length; i++)
            {
                context.ColliderComponents[i] = new List<Collider>();
                RUsdPhysicsCollider collider = colliders[i];
                string path = context.Path(collider.PathIndex);
                int bodyIndex = collider.BodyIndex >= 0 && collider.BodyIndex < context.BodyObjects.Length &&
                                context.BodyObjects[collider.BodyIndex] != null
                    ? collider.BodyIndex
                    : -1;

                Transform parent = bodyIndex >= 0 ? context.BodyObjects[bodyIndex].transform : context.Root;
                GameObject helper = CreateHelper(context, parent, "Collider " + LastSegment(path));
                Vector3 bodyScale = bodyIndex >= 0 ? context.BodyScales[bodyIndex] : Vector3.one;
                helper.transform.localPosition = UsdPhysicsSpace.Divide(UsdPhysicsSpace.Position(collider.LocalPosition), bodyScale);
                helper.transform.localRotation = UsdPhysicsSpace.Rotation(collider.LocalRotation);

                // Shape sizes arrive with the scale applied, so the helper sits at stage scale 1;
                // a mesh's points are its prim's own, so its helper takes the prim's world scale.
                Vector3 stageScale = collider.Shape == UsdNative.PhysicsShapeMesh ? UsdPhysicsSpace.SafeScale(collider.MeshScale) : Vector3.one;
                helper.transform.localScale = UsdPhysicsSpace.Divide(stageScale, bodyScale);

                bool dynamic = bodyIndex >= 0 && (context.Links[bodyIndex] != null ||
                                                  (context.Rigidbodies[bodyIndex] != null && !context.Rigidbodies[bodyIndex].isKinematic));
                List<Collider> made = context.ColliderComponents[i];
                AddShape(context, collider, helper, dynamic, path, made);

                PhysicsMaterial material = null;
                int[] materials = context.Physics.List(collider.Materials);
                if (materials.Length > 0 && materials[0] >= 0 && materials[0] < context.Materials.Length)
                {
                    material = context.Materials[materials[0]];
                }

                foreach (Collider component in made)
                {
                    component.enabled = collider.Enabled != 0;
                    if (material != null)
                    {
                        component.sharedMaterial = material;
                    }

                    if ((collider.SetMask & UsdNative.PhysicsColliderSetContactOffset) != 0 && collider.ContactOffset > 0f)
                    {
                        component.contactOffset = (float)(collider.ContactOffset * context.MetersPerUnit);
                    }

                    if (bodyIndex >= 0)
                    {
                        context.BodyColliders[bodyIndex].Add(component);
                    }
                }

                // A static collider follows its prim when the import made one for it.
                if (bodyIndex < 0)
                {
                    GameObject prim = context.ObjectForPath(path, false);
                    if (prim != null)
                    {
                        helper.transform.SetParent(prim.transform, true);
                    }
                }
            }
        }

        private static void AddShape(Context context, RUsdPhysicsCollider collider, GameObject helper, bool dynamic, string path, List<Collider> made)
        {
            int axis = Mathf.Clamp(collider.Axis, 0, 2);
            switch (collider.Shape)
            {
                case UsdNative.PhysicsShapeSphere:
                {
                    SphereCollider sphere = helper.AddComponent<SphereCollider>();
                    sphere.radius = collider.Radius0;
                    made.Add(sphere);
                    break;
                }

                case UsdNative.PhysicsShapeCube:
                {
                    BoxCollider box = helper.AddComponent<BoxCollider>();
                    box.size = 2f * UsdPhysicsSpace.UsdVector(collider.HalfExtents);
                    made.Add(box);
                    break;
                }

                case UsdNative.PhysicsShapeCapsule:
                case UsdNative.PhysicsShapeCapsuleTapered:
                {
                    CapsuleCollider capsule = helper.AddComponent<CapsuleCollider>();
                    float radius = Mathf.Max(collider.Radius0, collider.Radius1);
                    capsule.radius = radius;
                    capsule.height = 2f * (collider.HalfHeight + radius);
                    capsule.direction = axis;
                    made.Add(capsule);
                    if (collider.Shape == UsdNative.PhysicsShapeCapsuleTapered)
                    {
                        context.Notes.Add($"Collider {path}: tapered capsule approximated with the larger radius.");
                    }

                    break;
                }

                case UsdNative.PhysicsShapeCylinder:
                case UsdNative.PhysicsShapeCylinderTapered:
                case UsdNative.PhysicsShapeCone:
                {
                    float top = collider.Shape == UsdNative.PhysicsShapeCone ? 0f : collider.Radius0;
                    float bottom = collider.Shape == UsdNative.PhysicsShapeCylinder ? collider.Radius0 : collider.Radius1;
                    if (collider.Shape == UsdNative.PhysicsShapeCone)
                    {
                        bottom = collider.Radius0;
                    }

                    MeshCollider mesh = helper.AddComponent<MeshCollider>();
                    mesh.sharedMesh = RoundMesh(LastSegment(path), top, bottom, collider.HalfHeight, axis);
                    mesh.convex = true;
                    made.Add(mesh);
                    break;
                }

                case UsdNative.PhysicsShapePlane:
                {
                    // Unity has no infinite plane: a slab 2 km across, its top face on the plane.
                    BoxCollider box = helper.AddComponent<BoxCollider>();
                    float span = (float)(2000.0 / context.MetersPerUnit);
                    float thickness = (float)(1.0 / context.MetersPerUnit);
                    Vector3 size = Vector3.one * span;
                    size[axis] = thickness;
                    box.size = size;
                    Vector3 center = Vector3.zero;
                    center[axis] = -0.5f * thickness * (axis == 0 ? -1f : 1f);
                    box.center = center;
                    made.Add(box);
                    break;
                }

                case UsdNative.PhysicsShapeSpherePoints:
                {
                    RUsdPhysicsList spheres = collider.SpherePoints;
                    float[] floats = context.Physics.Floats;
                    for (int s = 0; s < spheres.Count; s++)
                    {
                        int at = spheres.Offset + (s * 4);
                        if (at + 3 >= floats.Length)
                        {
                            break;
                        }

                        SphereCollider sphere = helper.AddComponent<SphereCollider>();
                        sphere.center = new Vector3(-floats[at], floats[at + 1], floats[at + 2]);
                        sphere.radius = floats[at + 3];
                        made.Add(sphere);
                    }

                    break;
                }

                case UsdNative.PhysicsShapeMesh:
                    AddMeshShape(context, collider, helper, dynamic, path, made);
                    break;

                default:
                    context.Notes.Add($"Collider {path}: shape {collider.Shape} is not supported.");
                    break;
            }
        }

        private static void AddMeshShape(Context context, RUsdPhysicsCollider collider, GameObject helper, bool dynamic, string path, List<Collider> made)
        {
            Mesh mesh = CollisionMesh(context, collider, LastSegment(path));
            if (mesh == null)
            {
                context.Notes.Add($"Collider {path}: the collision mesh has no points; not built.");
                return;
            }

            switch (collider.MeshApproximation)
            {
                case UsdNative.PhysicsMeshBoundingSphere:
                {
                    SphereCollider sphere = helper.AddComponent<SphereCollider>();
                    sphere.center = mesh.bounds.center;
                    sphere.radius = mesh.bounds.extents.magnitude;
                    made.Add(sphere);
                    return;
                }

                case UsdNative.PhysicsMeshBoundingCube:
                {
                    BoxCollider box = helper.AddComponent<BoxCollider>();
                    box.center = mesh.bounds.center;
                    box.size = mesh.bounds.size;
                    made.Add(box);
                    return;
                }
            }

            MeshCollider meshCollider = helper.AddComponent<MeshCollider>();
            meshCollider.sharedMesh = mesh;
            bool triangles = collider.MeshApproximation == UsdNative.PhysicsMeshNone ||
                             collider.MeshApproximation == UsdNative.PhysicsMeshSimplification;
            meshCollider.convex = !triangles || dynamic;
            if (triangles && dynamic)
            {
                context.Notes.Add($"Collider {path}: a moving body's triangle mesh collider is built convex (Unity needs that).");
            }
            else if (collider.MeshApproximation == UsdNative.PhysicsMeshConvexDecomposition)
            {
                context.Notes.Add($"Collider {path}: convex decomposition approximated by a single convex hull.");
            }
            else if (collider.MeshApproximation == UsdNative.PhysicsMeshSdf)
            {
                context.Notes.Add($"Collider {path}: SDF collision approximated by a convex hull.");
            }

            made.Add(meshCollider);
        }

        // The collision mesh in the prim's local space, X negated, winding reversed to match.
        private static Mesh CollisionMesh(Context context, RUsdPhysicsCollider collider, string name)
        {
            float[] floats = context.Physics.Floats;
            int[] indices = context.Physics.Indices;
            RUsdPhysicsList points = collider.MeshPoints;
            RUsdPhysicsList triangles = collider.MeshTriangles;
            if (points.Count <= 0 || points.Offset < 0 || points.Offset + (points.Count * 3) > floats.Length)
            {
                return null;
            }

            var vertices = new Vector3[points.Count];
            for (int p = 0; p < points.Count; p++)
            {
                int at = points.Offset + (p * 3);
                vertices[p] = new Vector3(-floats[at], floats[at + 1], floats[at + 2]);
            }

            int triangleCount = triangles.Offset >= 0 && triangles.Offset + triangles.Count <= indices.Length ? triangles.Count / 3 : 0;
            var mesh = new Mesh { name = "Collision " + name };
            if (vertices.Length > 65535)
            {
                mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            }

            mesh.vertices = vertices;
            if (triangleCount > 0)
            {
                var winding = new int[triangleCount * 3];
                for (int t = 0; t < triangleCount; t++)
                {
                    int at = triangles.Offset + (t * 3);
                    winding[(t * 3) + 0] = indices[at];
                    winding[(t * 3) + 1] = indices[at + 2];
                    winding[(t * 3) + 2] = indices[at + 1];
                }

                mesh.triangles = winding;
            }

            mesh.RecalculateBounds();
            return mesh;
        }

        // A cylinder or cone (top radius 0) about `axis`, for a convex MeshCollider.
        private static Mesh RoundMesh(string name, float top, float bottom, float halfHeight, int axis)
        {
            const int segments = 24;
            var vertices = new List<Vector3>(segments * 2);
            for (int ring = 0; ring < 2; ring++)
            {
                float radius = ring == 0 ? bottom : top;
                float height = ring == 0 ? -halfHeight : halfHeight;
                for (int s = 0; s < segments; s++)
                {
                    float angle = s * Mathf.PI * 2f / segments;
                    float a = Mathf.Cos(angle) * radius;
                    float b = Mathf.Sin(angle) * radius;
                    vertices.Add(axis == 0 ? new Vector3(height, a, b) : axis == 1 ? new Vector3(a, height, b) : new Vector3(a, b, height));
                }
            }

            // Convex cooking only needs the points; a fan keeps the mesh valid.
            var triangles = new List<int>();
            for (int s = 1; s + 1 < segments; s++)
            {
                triangles.Add(0);
                triangles.Add(s);
                triangles.Add(s + 1);
            }

            var mesh = new Mesh { name = "Collision " + name };
            mesh.SetVertices(vertices);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        private static GameObject CreateHelper(Context context, Transform parent, string name)
        {
            var helper = new GameObject(name);
            helper.transform.SetParent(parent, false);
            helper.AddComponent<UsdImportHelperObject>();
            return helper;
        }

        // ---- Filtering ----

        private static void CollectFilteredPairs(Context context)
        {
            UsdNativePhysics physics = context.Physics;
            var pairs = new HashSet<long>();
            var colliders = new List<Collider>();
            var ids = new Dictionary<Collider, int>();
            int Id(Collider collider)
            {
                if (!ids.TryGetValue(collider, out int id))
                {
                    id = colliders.Count;
                    ids[collider] = id;
                    colliders.Add(collider);
                }

                return id;
            }

            bool truncated = false;
            void AddPairs(IEnumerable<Collider> first, IEnumerable<Collider> second)
            {
                foreach (Collider a in first)
                {
                    foreach (Collider b in second)
                    {
                        if (a == null || b == null || a == b)
                        {
                            continue;
                        }

                        if (pairs.Count >= MaxFilteredPairs)
                        {
                            truncated = true;
                            return;
                        }

                        int x = Id(a);
                        int y = Id(b);
                        pairs.Add(x < y ? ((long)x << 32) | (uint)y : ((long)y << 32) | (uint)x);
                    }
                }
            }

            // An articulation with self-collisions off: every pair of its links' colliders.
            for (int i = 0; i < physics.Articulations.Length; i++)
            {
                RUsdPhysicsArticulation articulation = physics.Articulations[i];
                if ((articulation.SetMask & UsdNative.PhysicsArticulationSetSelfCollisions) == 0 || articulation.EnabledSelfCollisions != 0)
                {
                    continue;
                }

                var all = new List<Collider>();
                foreach (int body in physics.List(articulation.Bodies))
                {
                    all.AddRange(context.BodyColliders[body]);
                }

                AddPairs(all, all);
            }

            // physics:filteredPairs on bodies and colliders, naming bodies, colliders or articulations.
            Dictionary<string, List<Collider>> byPath = CollidersByPath(context);
            for (int i = 0; i < physics.Bodies.Length; i++)
            {
                foreach (int target in physics.List(physics.Bodies[i].Filtered))
                {
                    if (byPath.TryGetValue(context.Path(target), out List<Collider> other))
                    {
                        AddPairs(context.BodyColliders[i], other);
                    }
                }
            }

            for (int i = 0; i < physics.Colliders.Length; i++)
            {
                foreach (int target in physics.List(physics.Colliders[i].Filtered))
                {
                    if (byPath.TryGetValue(context.Path(target), out List<Collider> other))
                    {
                        AddPairs(context.ColliderComponents[i], other);
                    }
                }
            }

            // Collision groups that filter each other, with merged groups acting as one.
            BuildGroupPairs(context, AddPairs);

            // A free joint's bodies, when it turns their collisions off, are filtered by the
            // joint itself (Joint.enableCollision); articulation links by Unity's own rule.
            if (truncated)
            {
                context.Notes.Add($"More than {MaxFilteredPairs} filtered collider pairs; the rest still collide.");
            }

            if (pairs.Count == 0)
            {
                return;
            }

            UsdPhysicsCollisionFilter filter = context.Root.gameObject.AddComponent<UsdPhysicsCollisionFilter>();
            foreach (long pair in pairs)
            {
                filter.First.Add(colliders[(int)(pair >> 32)]);
                filter.Second.Add(colliders[(int)(pair & 0xffffffff)]);
            }

            filter.ApplyNow();
        }

        private static Dictionary<string, List<Collider>> CollidersByPath(Context context)
        {
            UsdNativePhysics physics = context.Physics;
            var byPath = new Dictionary<string, List<Collider>>(StringComparer.Ordinal);
            for (int i = 0; i < physics.Bodies.Length; i++)
            {
                byPath[context.Path(physics.Bodies[i].PathIndex)] = context.BodyColliders[i];
            }

            for (int i = 0; i < physics.Colliders.Length; i++)
            {
                byPath[context.Path(physics.Colliders[i].PathIndex)] = context.ColliderComponents[i];
            }

            for (int i = 0; i < physics.Articulations.Length; i++)
            {
                var all = new List<Collider>();
                foreach (int body in physics.List(physics.Articulations[i].Bodies))
                {
                    all.AddRange(context.BodyColliders[body]);
                }

                byPath[context.Path(physics.Articulations[i].PathIndex)] = all;
            }

            return byPath;
        }

        private static void BuildGroupPairs(Context context, Action<IEnumerable<Collider>, IEnumerable<Collider>> addPairs)
        {
            UsdNativePhysics physics = context.Physics;
            int groupCount = physics.Groups.Length;
            if (groupCount == 0)
            {
                return;
            }

            var members = new List<Collider>[groupCount];
            for (int g = 0; g < groupCount; g++)
            {
                members[g] = new List<Collider>();
            }

            for (int c = 0; c < physics.Colliders.Length; c++)
            {
                foreach (int g in physics.List(physics.Colliders[c].Groups))
                {
                    if (g >= 0 && g < groupCount)
                    {
                        members[g].AddRange(context.ColliderComponents[c]);
                    }
                }
            }

            // Groups sharing a merge name are one group.
            var merged = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            for (int g = 0; g < groupCount; g++)
            {
                string name = physics.Groups[g].MergeGroupNameIndex >= 0 ? context.Path(physics.Groups[g].MergeGroupNameIndex) : null;
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                if (!merged.TryGetValue(name, out List<int> list))
                {
                    list = new List<int>();
                    merged[name] = list;
                }

                list.Add(g);
            }

            List<Collider> MembersOf(int g)
            {
                string name = physics.Groups[g].MergeGroupNameIndex >= 0 ? context.Path(physics.Groups[g].MergeGroupNameIndex) : null;
                if (string.IsNullOrEmpty(name) || !merged.TryGetValue(name, out List<int> list) || list.Count < 2)
                {
                    return members[g];
                }

                var all = new List<Collider>();
                foreach (int other in list)
                {
                    all.AddRange(members[other]);
                }

                return all;
            }

            for (int g = 0; g < groupCount; g++)
            {
                RUsdPhysicsCollisionGroup group = physics.Groups[g];
                if (group.InvertFilteredGroups != 0)
                {
                    context.Notes.Add($"Collision group {context.Path(group.PathIndex)} inverts its filter; not supported, its colliders collide with everything.");
                    continue;
                }

                foreach (int other in physics.List(group.FilteredGroups))
                {
                    if (other >= 0 && other < groupCount)
                    {
                        addPairs(MembersOf(g), MembersOf(other));
                    }
                }
            }
        }

        // ---- Scene and what was left out ----

        private static void BuildScene(Context context)
        {
            UsdNativePhysics physics = context.Physics;
            if (physics.Scenes.Length == 0)
            {
                return;
            }

            RUsdPhysicsScene scene = physics.Scenes[0];
            UsdPhysicsScene record = context.Root.gameObject.AddComponent<UsdPhysicsScene>();
            record.PrimPath = context.Path(scene.PathIndex);
            Vector3 direction = UsdPhysicsSpace.Position(scene.GravityDirection);
            record.GravityDirection = direction.sqrMagnitude > 0f ? direction.normalized : Vector3.down;
            record.GravityMagnitude = (float)(scene.GravityMagnitude * context.MetersPerUnit);
            record.TimeStepsPerSecond = (scene.SetMask & UsdNative.PhysicsSceneSetTimeSteps) != 0 ? scene.TimeStepsPerSecond : 0f;
            if (physics.Scenes.Length > 1)
            {
                context.Notes.Add($"{physics.Scenes.Length} physics scenes; Unity has one, so only {record.PrimPath} is recorded.");
            }
        }

        private static void ReportUnsupported(Context context)
        {
            foreach (RUsdPhysicsUnsupported entry in context.Physics.Unsupported)
            {
                string path = entry.PathIndex >= 0 ? context.Path(entry.PathIndex) : "(stage)";
                string detail = entry.DetailIndex >= 0 ? context.Path(entry.DetailIndex) : string.Empty;
                string what;
                switch (entry.Kind)
                {
                    case UsdNative.PhysicsUnsupportedCustomShape: what = "custom collision shape"; break;
                    case UsdNative.PhysicsUnsupportedCustomJoint: what = "custom joint"; break;
                    case UsdNative.PhysicsUnsupportedMimicJoint:
                        what = context.Mimicked.Contains(path)
                            ? "mimic joint, followed with a drive (UsdPhysicsMimicJoint): PhysX holds it with a constraint, Unity has no joint coupling, so it lags a step and yields under load"
                            : "mimic joint not followed (Unity has no joint coupling, and both joints must be single-axis joints in an articulation to emulate it)";
                        break;
                    case UsdNative.PhysicsUnsupportedTendon: what = "tendon"; break;
                    case UsdNative.PhysicsUnsupportedInvalid: what = "invalid physics object, skipped"; break;
                    case UsdNative.PhysicsUnsupportedLimit: what = "import limit reached"; break;
                    case UsdNative.PhysicsUnsupportedMesh: what = "collision mesh could not be read"; break;
                    default: what = "unsupported physics"; break;
                }

                context.Notes.Add(string.IsNullOrEmpty(detail) ? $"{path}: {what}." : $"{path}: {what} ({detail}).");
            }
        }

        // ---- Mimic joints ----

        // A PhysxMimicJointAPI follower gets a drive and a UsdPhysicsMimicJoint that moves the
        // drive's target with its reference joint. The follower authors no drive of its own (the
        // constraint needs none), so it takes the reference joint's stiffness, damping and force
        // limit when both move the same way, and a stiff drive otherwise. UsdPhysicsJoint keeps
        // DriveEnabled false: the drive is the coupling's, not one the stage authors.
        private static void BuildMimics(Context context)
        {
            if (context.Physics.Mimics.Length == 0)
            {
                return;
            }

            var joints = new Dictionary<string, UsdPhysicsJoint>(StringComparer.Ordinal);
            foreach (UsdPhysicsJoint joint in context.Root.GetComponentsInChildren<UsdPhysicsJoint>(true))
            {
                joints[joint.PrimPath] = joint;
            }

            foreach (UsdPhysicsMimicSource source in context.Physics.Mimics)
            {
                if (!joints.TryGetValue(source.JointPath, out UsdPhysicsJoint follower) ||
                    !joints.TryGetValue(source.ReferenceJointPath, out UsdPhysicsJoint reference) ||
                    !IsSingleAxisLink(follower) || !IsSingleAxisLink(reference))
                {
                    continue;
                }

                UsdPhysicsMimicJoint mimic = follower.gameObject.AddComponent<UsdPhysicsMimicJoint>();
                mimic.Follower = follower;
                mimic.Reference = reference;
                mimic.ReferenceJointPath = source.ReferenceJointPath;
                mimic.Gearing = source.Gearing;
                mimic.Offset = source.Offset;

                ArticulationDrive drive = follower.Link.xDrive;
                ArticulationDrive lead = reference.Link.xDrive;
                bool borrow = reference.DriveEnabled && reference.IsAngular == follower.IsAngular && lead.stiffness > 0f;
                drive.stiffness = borrow ? lead.stiffness : MimicStiffness;
                drive.damping = borrow ? lead.damping : MimicDamping;
                drive.forceLimit = borrow ? lead.forceLimit : float.MaxValue;
                drive.driveType = ArticulationDriveType.Force;
                follower.Link.xDrive = drive;
                mimic.Step();

                follower.Note = AppendNote(follower.Note,
                    $"Follows {LastSegment(source.ReferenceJointPath)} (gearing {source.Gearing:0.###}, offset {source.Offset:0.###}) " +
                    "through a drive with " + (borrow ? "its drive's gains." : "stiff default gains."));
                context.Mimicked.Add(source.JointPath);
            }
        }

        // Gains for a follower whose reference joint has no drive of the same kind to borrow.
        private const float MimicStiffness = 10000f;
        private const float MimicDamping = 100f;

        private static bool IsSingleAxisLink(UsdPhysicsJoint joint) =>
            joint.Articulated && joint.Link != null &&
            (joint.UnityType == UsdPhysicsJointType.Revolute || joint.UnityType == UsdPhysicsJointType.Prismatic);

        private static string AppendNote(string note, string addition) =>
            string.IsNullOrEmpty(note) ? addition : note + " " + addition;

        private static string LastSegment(string path)
        {
            int slash = path.LastIndexOf('/');
            return slash >= 0 && slash + 1 < path.Length ? path.Substring(slash + 1) : path;
        }
    }
}
