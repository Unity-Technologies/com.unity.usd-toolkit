using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Unity.USDToolkit.Samples.SimReadyCentral
{
    /// <summary>
    /// One handle: a user-facing joint (a movable joint that is not a mimic follower), its range,
    /// its label and the followers its coupling moves with it.
    /// </summary>
    [Serializable]
    public sealed class SimReadyJointHandle
    {
        public UsdPhysicsJoint Joint;
        public string Label;
        public bool Angular;
        public float Min;
        public float Max;
        public float RestValue;

        /// <summary>The value the handle was last set to (USD units: degrees or stage units).</summary>
        public float Value;

        /// <summary>The mimic followers the joint drives, with their gearing and offset.</summary>
        public List<UsdPhysicsMimicJoint> Followers = new List<UsdPhysicsMimicJoint>();

        /// <summary>Where the handle sits: a point on the moving link, in its local space.</summary>
        public Transform HandleBody;
        public Vector3 HandleLocal;

        public Vector3 HandleWorld => HandleBody != null ? HandleBody.TransformPoint(HandleLocal) : Vector3.zero;
    }

    /// <summary>
    /// The joint-model layer both front-ends share (the Editor window with its Scene-view
    /// gizmo, and the runtime viewer with its own): which joints get a handle, their ranges,
    /// labels and units, and what moving one does. In edit mode a handle poses the links
    /// kinematically (forward kinematics on the transforms, no physics step), the coupled
    /// followers with it. In play mode it sets the leader's drive target and the physics moves
    /// the linkage, the followers through their <see cref="UsdPhysicsMimicJoint"/> drives, unless
    /// the asset is <see cref="Kinematic"/>: then the physics is off and play mode poses like edit mode.
    /// Sits on the import's root with the asset's SimReady metadata and its physics log.
    /// </summary>
    [DefaultExecutionOrder(1000)]
    [DisallowMultipleComponent]
    public sealed class SimReadyAsset : MonoBehaviour
    {
        // How fast a handle pulls a joint that has no drive of its own, while it is dragged.
        private const float HandleFrequencyHz = 3f;

        public SimReadyMetadata Metadata = new SimReadyMetadata();
        public string SourceLink = string.Empty;
        public string RepoId = string.Empty;
        public string RepoUrl = string.Empty;
        public string Commit = string.Empty;
        public string RootRepoPath = string.Empty;
        public string RootLocalPath = string.Empty;

        /// <summary>The package's thumbnail of the root layer, on disk; empty when it has none.</summary>
        public string ThumbnailPath = string.Empty;
        public List<string> DownloadWarnings = new List<string>();
        public List<SimReadyMappingRow> PhysicsLog = new List<SimReadyMappingRow>();
        public List<SimReadyJointHandle> Handles = new List<SimReadyJointHandle>();

        [Tooltip("In play mode, put a static floor under an asset that nothing fixes to the world, so it stands instead of falling.")]
        public bool GroundInPlayMode = true;

        [Tooltip("In play mode, switch the asset's physics off and let the handles pose the links directly, as in edit mode. " +
                 "For assets that cannot hold themselves up, such as a humanoid without joint drives.")]
        [SerializeField] private bool kinematic;

        /// <summary>
        /// What <see cref="Kinematic"/> starts as on an asset the loader creates; the front-ends
        /// remember the last choice here, so the next asset loads the same way.
        /// </summary>
        public static bool KinematicByDefault;

        [NonSerialized] private GameObject ground;

        /// <summary>
        /// Raised when an asset has been loaded or re-imported (a variant switch), by the Editor
        /// window or the runtime viewer alike, so every front-end shows the same, current asset.
        /// </summary>
        public static event Action<SimReadyAsset> Loaded;

        internal static void NotifyLoaded(SimReadyAsset asset) => Loaded?.Invoke(asset);

        /// <summary>How an edit-mode asset is removed; the Editor sets it to an undoable delete.</summary>
        public static Action<GameObject> EditModeDestroy;

        /// <summary>
        /// Takes every SimReady asset in the open scenes out of the way of a new one, except
        /// <paramref name="keep"/>: deactivated at once, so it neither renders nor collides with the
        /// new one in this frame, then destroyed. Returns how many.
        /// </summary>
        public static int RemoveAll(SimReadyAsset keep = null)
        {
            int removed = 0;
            foreach (SimReadyAsset other in FindObjectsByType<SimReadyAsset>(FindObjectsInactive.Include))
            {
                if (other == keep)
                {
                    continue;
                }

                if (Application.isPlaying)
                {
                    // Destroy waits for the end of the frame: out of the way of the new asset now.
                    other.gameObject.SetActive(false);
                    Destroy(other.gameObject);
                }
                else if (EditModeDestroy != null)
                {
                    EditModeDestroy(other.gameObject);
                }
                else
                {
                    DestroyImmediate(other.gameObject);
                }

                removed++;
            }

            return removed;
        }

        [NonSerialized] private List<UsdPhysicsJoint> poseOrder;
        [NonSerialized] private readonly Dictionary<UsdPhysicsJoint, DriveBackup> dragging = new Dictionary<UsdPhysicsJoint, DriveBackup>();

        /// <summary>The asset's moving parts: handles, i.e. user-facing joints.</summary>
        public int MovingParts => Handles.Count;

        // ---- Building the model ----

        /// <summary>
        /// Finds the handles: every revolute or prismatic joint the import built with a rest pose
        /// that is not locked and does not follow another joint. Each takes the mimic followers
        /// that reference it.
        /// </summary>
        public void BuildModel()
        {
            Handles.Clear();
            poseOrder = null;
            UsdPhysicsJoint[] joints = GetComponentsInChildren<UsdPhysicsJoint>(true);
            UsdPhysicsMimicJoint[] mimics = GetComponentsInChildren<UsdPhysicsMimicJoint>(true);
            var followers = new HashSet<UsdPhysicsJoint>(mimics.Where(m => m.Follower != null).Select(m => m.Follower));
            float unlimitedLength = UnlimitedLength();

            foreach (UsdPhysicsJoint joint in joints)
            {
                if (followers.Contains(joint) || !joint.HasRestPose ||
                    (joint.UnityType != UsdPhysicsJointType.Revolute && joint.UnityType != UsdPhysicsJointType.Prismatic))
                {
                    continue;
                }

                if (!SimReadyJointMath.TryRange(joint.LimitEnabled, joint.LowerLimit, joint.UpperLimit, joint.IsAngular, unlimitedLength,
                                                out float min, out float max))
                {
                    continue;
                }

                List<UsdPhysicsMimicJoint> coupled = mimics.Where(m => m.Reference == joint && m.Follower != null).ToList();
                var handle = new SimReadyJointHandle
                {
                    Joint = joint,
                    Angular = joint.IsAngular,
                    Min = min,
                    Max = max,
                    RestValue = joint.RestPosition,
                    Value = joint.RestPosition,
                    Followers = coupled,
                    Label = SimReadyJointMath.Label(LastSegment(joint.PrimPath), DisplayName(joint),
                                                    coupled.Select(m => LastSegment(m.Follower.PrimPath)), LastSegment(joint.Body1Path),
                                                    Metadata.AssetName),
                    HandleBody = joint.MovingTransform,
                };
                handle.HandleLocal = HandlePoint(handle.HandleBody);
                Handles.Add(handle);
            }

            Handles.Sort((a, b) => string.CompareOrdinal(a.Joint.PrimPath, b.Joint.PrimPath));
        }

        // displayName is recorded by the sample's loader (SimReadyLoader) when the stage authors one.
        [SerializeField] private List<string> displayNamePaths = new List<string>();
        [SerializeField] private List<string> displayNames = new List<string>();

        internal void SetDisplayNames(Dictionary<string, string> names)
        {
            displayNamePaths = names.Keys.ToList();
            displayNames = names.Values.ToList();
        }

        private string DisplayName(UsdPhysicsJoint joint)
        {
            int index = displayNamePaths.IndexOf(joint.PrimPath);
            return index >= 0 ? displayNames[index] : null;
        }

        // A prismatic joint without limits gets a range the size of the asset.
        private float UnlimitedLength()
        {
            if (Metadata.HasExtents)
            {
                return Mathf.Max(Metadata.Extents.x, Metadata.Extents.y, Metadata.Extents.z);
            }

            return (float)(1.0 / Math.Max(Metadata.MetersPerUnit, 1e-9));
        }

        // The middle of the moving link's own geometry (not that of links below it).
        private static Vector3 HandlePoint(Transform body)
        {
            if (body == null)
            {
                return Vector3.zero;
            }

            Bounds? bounds = null;
            foreach (Renderer renderer in body.GetComponentsInChildren<Renderer>(true))
            {
                if (OwningBody(renderer.transform) != body)
                {
                    continue;
                }

                if (bounds.HasValue)
                {
                    Bounds b = bounds.Value;
                    b.Encapsulate(renderer.bounds);
                    bounds = b;
                }
                else
                {
                    bounds = renderer.bounds;
                }
            }

            return bounds.HasValue ? body.InverseTransformPoint(bounds.Value.center) : Vector3.zero;
        }

        private static Transform OwningBody(Transform transform)
        {
            for (Transform current = transform; current != null; current = current.parent)
            {
                if (current.GetComponent<UsdPhysicsBody>() != null)
                {
                    return current;
                }
            }

            return null;
        }

        // ---- Values ----

        /// <summary>The handle's value now: measured from the physics in play mode, the posed value in edit mode.</summary>
        public float GetValue(SimReadyJointHandle handle)
        {
            if (handle?.Joint == null)
            {
                return 0f;
            }

            return Simulating ? handle.Joint.GetPosition() : handle.Value;
        }

        /// <summary>
        /// Moves a handle to <paramref name="value"/> (USD units), clamped to its range. Edit
        /// mode, or <see cref="Kinematic"/> in play mode: the leader and its followers are posed
        /// kinematically. Play mode otherwise: the leader's drive target is set and the followers
        /// follow it through the physics.
        /// </summary>
        public float SetValue(SimReadyJointHandle handle, float value)
        {
            if (handle?.Joint == null)
            {
                return 0f;
            }

            value = SimReadyJointMath.Clamp(value, handle.Min, handle.Max);
            handle.Value = value;
            if (Simulating)
            {
                handle.Joint.SetDriveTarget(value);
            }
            else
            {
                Pose(handle, value);
            }

            return value;
        }

        /// <summary>Every handle back to where the import built it.</summary>
        public void ResetToRest()
        {
            if (Application.isPlaying && Kinematic)
            {
                // The asset stands where play mode started it, every handle at rest.
                RestoreHome();
            }

            foreach (SimReadyJointHandle handle in Handles)
            {
                if (Simulating)
                {
                    handle.Value = handle.RestValue;
                    PlaceInPlayMode(handle, handle.RestValue);
                }
                else
                {
                    SetValue(handle, handle.RestValue);
                }
            }
        }

        // Kinematic posing: the targets for this handle's joints, every other joint outside an
        // articulation carried at its current value, parents before children.
        private void Pose(SimReadyJointHandle handle, float value)
        {
            var targets = new Dictionary<UsdPhysicsJoint, float> { [handle.Joint] = value };
            foreach (UsdPhysicsMimicJoint follower in handle.Followers)
            {
                targets[follower.Follower] = SimReadyJointMath.FollowerPosition(value, follower.Gearing, follower.Offset,
                                                                                handle.Joint.IsAngular, follower.Follower.IsAngular);
            }

            List<UsdPhysicsJoint> order = PoseOrder();
            var carried = new Dictionary<UsdPhysicsJoint, float>();
            foreach (UsdPhysicsJoint joint in order)
            {
                if (!targets.ContainsKey(joint) && !joint.Articulated)
                {
                    carried[joint] = joint.MeasurePosition();
                }
            }

            foreach (UsdPhysicsJoint joint in order)
            {
                if (targets.TryGetValue(joint, out float target))
                {
                    joint.PoseKinematically(target);
                }
                else if (carried.TryGetValue(joint, out float current))
                {
                    // A body jointed outside the hierarchy rides along with the body it hangs on.
                    joint.PoseKinematically(current);
                }
            }
        }

        // Joints ordered so a joint's frame is placed before the body it moves.
        private List<UsdPhysicsJoint> PoseOrder()
        {
            if (poseOrder != null && poseOrder.All(j => j != null))
            {
                return poseOrder;
            }

            UsdPhysicsJoint[] joints = GetComponentsInChildren<UsdPhysicsJoint>(true).Where(j => j.HasRestPose).ToArray();
            var depth = new Dictionary<UsdPhysicsJoint, int>();
            int Depth(UsdPhysicsJoint joint, int guard)
            {
                if (depth.TryGetValue(joint, out int known))
                {
                    return known;
                }

                int result = 0;
                if (guard < 64 && joint.FrameTransform != null)
                {
                    foreach (UsdPhysicsJoint other in joints)
                    {
                        if (other != joint && other.MovingTransform != null &&
                            (joint.FrameTransform == other.MovingTransform || joint.FrameTransform.IsChildOf(other.MovingTransform)))
                        {
                            result = Math.Max(result, Depth(other, guard + 1) + 1);
                        }
                    }
                }

                depth[joint] = result;
                return result;
            }

            poseOrder = joints.OrderBy(j => Depth(j, 0)).ThenBy(j => j.PrimPath, StringComparer.Ordinal).ToList();
            return poseOrder;
        }

        // ---- Play mode ----

        /// <summary>
        /// The asset can fall: it has a body that moves and is not held by the world (a dynamic
        /// rigid body, or an articulation whose root is not immovable). SimReady props such as
        /// cabinets and appliances author no fixed joint; the grippers fix their base.
        /// </summary>
        public bool NeedsGround =>
            GetComponentsInChildren<Rigidbody>().Any(b => !b.isKinematic) ||
            GetComponentsInChildren<ArticulationBody>().Any(b => b.isRoot && !b.immovable);

        /// <summary>A static floor whose top is the asset's lowest point; removed with the asset.</summary>
        public GameObject CreateGround()
        {
            if (ground != null)
            {
                return ground;
            }

            Bounds? bounds = null;
            foreach (Collider collider in GetComponentsInChildren<Collider>())
            {
                bounds = Grow(bounds, collider.bounds);
            }

            foreach (Renderer renderer in GetComponentsInChildren<Renderer>())
            {
                bounds = Grow(bounds, renderer.bounds);
            }

            Bounds b = bounds ?? new Bounds(transform.position, Vector3.one);
            float span = (Mathf.Max(b.size.x, b.size.z) * 20f) + 10f;
            ground = new GameObject("SimReady Ground");
            ground.transform.position = new Vector3(b.center.x, b.min.y - 0.5f, b.center.z);
            BoxCollider floor = ground.AddComponent<BoxCollider>();
            floor.size = new Vector3(span, 1f, span);
            return ground;
        }

        private static Bounds Grow(Bounds? bounds, Bounds add)
        {
            if (!bounds.HasValue)
            {
                return add;
            }

            Bounds b = bounds.Value;
            b.Encapsulate(add);
            return b;
        }

        // Deactivated (replaced by another asset, or switched off): its floor goes with it.
        private void OnDisable() => RemoveGround();

        private void RemoveGround()
        {
            if (ground != null)
            {
                if (Application.isPlaying)
                {
                    Destroy(ground);
                }
                else
                {
                    DestroyImmediate(ground);
                }

                ground = null;
            }
        }

        private void Start()
        {
            if (!Application.isPlaying)
            {
                return;
            }

            started = true;
            if (kinematic)
            {
                ApplyKinematic();
                return;
            }

            if (GroundInPlayMode && NeedsGround)
            {
                CreateGround();
            }

            // Runs after UsdPhysicsJoint.Start (which places joints at their authored state):
            // each handle starts where it was left in edit mode, its drive holding it there.
            foreach (SimReadyJointHandle handle in Handles)
            {
                PlaceInPlayMode(handle, handle.Value);
            }
        }

        // ---- Kinematic mode ----

        [NonSerialized] private bool started;
        [NonSerialized] private bool physicsOff;
        [NonSerialized] private Transform[] homeTransforms;
        [NonSerialized] private Vector3[] homePositions;
        [NonSerialized] private Quaternion[] homeRotations;
        [NonSerialized] private readonly Dictionary<Rigidbody, bool> wasKinematic = new Dictionary<Rigidbody, bool>();

        /// <summary>
        /// Kinematic mode: in play mode the asset's physics is switched off (its articulations
        /// disabled, its rigid bodies kinematic, its mimic drives stopped) and the handles pose the
        /// links directly, as in edit mode, so an asset that cannot hold itself up (a humanoid
        /// without joint drives) stands still. Off: the physics moves the linkage. Can be switched
        /// at any time; in play mode the change applies at once. Edit mode always poses kinematically.
        /// </summary>
        public bool Kinematic
        {
            get => kinematic;
            set
            {
                if (kinematic == value)
                {
                    return;
                }

                if (value && Simulating && started)
                {
                    // Keep where the physics has the handles; the asset stands up at its start pose with them.
                    foreach (SimReadyJointHandle handle in Handles)
                    {
                        if (handle.Joint != null)
                        {
                            handle.Value = SimReadyJointMath.Clamp(handle.Joint.GetPosition(), handle.Min, handle.Max);
                        }
                    }
                }

                kinematic = value;
                if (Application.isPlaying && started && isActiveAndEnabled)
                {
                    if (value)
                    {
                        ApplyKinematic();
                    }
                    else
                    {
                        ApplyDynamic();
                    }
                }
            }
        }

        /// <summary>In play mode, the physics moves the asset (not <see cref="Kinematic"/>).</summary>
        public bool Simulating => Application.isPlaying && !kinematic;

        private void Awake()
        {
            if (!Application.isPlaying)
            {
                return;
            }

            // The pose the links start play mode at: what the articulations are built from, and
            // where a kinematic asset stands. Before any Start, so before any joint is placed.
            homeTransforms = GetComponentsInChildren<Transform>(true);
            homePositions = homeTransforms.Select(t => t.localPosition).ToArray();
            homeRotations = homeTransforms.Select(t => t.localRotation).ToArray();
        }

        private void RestoreHome()
        {
            if (homeTransforms == null)
            {
                return;
            }

            for (int i = 0; i < homeTransforms.Length; i++)
            {
                if (homeTransforms[i] != null && homeTransforms[i] != transform)
                {
                    homeTransforms[i].SetLocalPositionAndRotation(homePositions[i], homeRotations[i]);
                }
            }
        }

        private void ApplyKinematic()
        {
            RemoveGround();
            SetPhysics(false);
            RestoreHome();
            foreach (SimReadyJointHandle handle in Handles)
            {
                if (handle.Joint != null)
                {
                    Pose(handle, handle.Value);
                }
            }
        }

        private void ApplyDynamic()
        {
            // The articulations are rebuilt from the links' pose: the one play mode started from.
            RestoreHome();
            SetPhysics(true);
            foreach (UsdPhysicsJoint joint in GetComponentsInChildren<UsdPhysicsJoint>(true))
            {
                if (joint.Articulated && joint.HasInitialPosition)
                {
                    joint.SetPosition(joint.InitialPosition); // as UsdPhysicsJoint.Start does
                }
            }

            if (GroundInPlayMode && NeedsGround)
            {
                CreateGround();
            }

            foreach (SimReadyJointHandle handle in Handles)
            {
                PlaceInPlayMode(handle, handle.Value);
            }
        }

        // Off: articulations disabled (their links become plain transforms with static
        // colliders), rigid bodies kinematic, mimic drives stopped. On: all as they were.
        private void SetPhysics(bool on)
        {
            if (physicsOff == !on)
            {
                return;
            }

            physicsOff = !on;
            foreach (ArticulationBody link in GetComponentsInChildren<ArticulationBody>(true))
            {
                link.enabled = on;
            }

            foreach (Rigidbody body in GetComponentsInChildren<Rigidbody>(true))
            {
                if (!on)
                {
                    wasKinematic[body] = body.isKinematic;
                    body.isKinematic = true;
                }
                else if (wasKinematic.TryGetValue(body, out bool was))
                {
                    body.isKinematic = was;
                    if (!was)
                    {
                        body.linearVelocity = Vector3.zero;
                        body.angularVelocity = Vector3.zero;
                    }
                }
            }

            if (on)
            {
                wasKinematic.Clear();
            }

            foreach (UsdPhysicsMimicJoint mimic in GetComponentsInChildren<UsdPhysicsMimicJoint>(true))
            {
                mimic.enabled = on;
            }
        }

        private static void PlaceInPlayMode(SimReadyJointHandle handle, float value)
        {
            if (handle.Joint == null)
            {
                return;
            }

            handle.Joint.SetPosition(value);
            foreach (UsdPhysicsMimicJoint follower in handle.Followers)
            {
                follower.Follower.SetPosition(SimReadyJointMath.FollowerPosition(value, follower.Gearing, follower.Offset,
                                                                                 handle.Joint.IsAngular, follower.Follower.IsAngular));
                follower.Step();
            }

            handle.Joint.SetDriveTarget(value);
        }

        /// <summary>
        /// Before edit mode is left for play mode: the links go back to the pose the import built,
        /// so Unity's joints start from it, and <see cref="Start"/> moves them to the handle values.
        /// </summary>
        public void PrepareForPlayMode()
        {
            var values = Handles.Select(h => h.Value).ToList();
            for (int i = 0; i < Handles.Count; i++)
            {
                Pose(Handles[i], Handles[i].RestValue);
                Handles[i].Value = values[i];
            }
        }

        /// <summary>
        /// A drag starts in play mode: a joint whose drive has no stiffness (a cabinet door, a
        /// free hinge) gets a spring towards the handle for as long as the drag lasts, sized to
        /// pull its link at about <see cref="HandleFrequencyHz"/> Hz, critically damped.
        /// </summary>
        public void BeginDrag(SimReadyJointHandle handle)
        {
            UsdPhysicsJoint joint = handle?.Joint;
            if (!Simulating || joint == null || dragging.ContainsKey(joint) || (joint.DriveEnabled && joint.DriveStiffness > 0f))
            {
                return;
            }

            float omega = 2f * Mathf.PI * HandleFrequencyHz;
            if (joint.Articulated && joint.Link != null)
            {
                ArticulationDrive drive = joint.Link.xDrive;
                dragging[joint] = new DriveBackup { Articulation = drive };
                float inertia = EffectiveInertia(joint, joint.Link.mass, joint.Link.worldCenterOfMass, joint.Link.inertiaTensor);
                drive.stiffness = inertia * omega * omega;
                drive.damping = 2f * inertia * omega;
                drive.forceLimit = float.MaxValue;
                drive.target = joint.Link.xDrive.target;
                joint.Link.xDrive = drive;
            }
            else if (joint.UnityJoint is ConfigurableJoint configurable && configurable.TryGetComponent(out Rigidbody body))
            {
                dragging[joint] = new DriveBackup { Angular = configurable.angularXDrive, Linear = configurable.xDrive };
                float inertia = EffectiveInertia(joint, body.mass, body.worldCenterOfMass, body.inertiaTensor);
                var spring = new JointDrive { positionSpring = inertia * omega * omega, positionDamper = 2f * inertia * omega, maximumForce = float.MaxValue };
                if (joint.IsAngular)
                {
                    configurable.rotationDriveMode = RotationDriveMode.XYAndZ;
                    configurable.angularXDrive = spring;
                }
                else
                {
                    configurable.xDrive = spring;
                }
            }
        }

        /// <summary>The drag ends: an authored drive keeps the new target; a handle spring is taken off again.</summary>
        public void EndDrag(SimReadyJointHandle handle)
        {
            UsdPhysicsJoint joint = handle?.Joint;
            if (joint == null || !dragging.TryGetValue(joint, out DriveBackup backup))
            {
                return;
            }

            dragging.Remove(joint);
            if (joint.Articulated && joint.Link != null)
            {
                ArticulationDrive restored = backup.Articulation;
                restored.target = joint.Link.xDrive.target;
                joint.Link.xDrive = restored;
            }
            else if (joint.UnityJoint is ConfigurableJoint configurable)
            {
                configurable.angularXDrive = backup.Angular;
                configurable.xDrive = backup.Linear;
            }
        }

        // Inertia about the axis (rotation) or mass (translation), in Unity's drive units.
        private static float EffectiveInertia(UsdPhysicsJoint joint, float mass, Vector3 centerOfMass, Vector3 inertiaTensor)
        {
            if (!joint.IsAngular)
            {
                return Mathf.Max(mass, 1e-4f);
            }

            Vector3 axis = joint.AxisWorld;
            Vector3 offset = centerOfMass - joint.PivotWorld;
            float r = Vector3.Cross(axis, offset).magnitude;
            return Mathf.Max((mass * r * r) + Mathf.Max(inertiaTensor.x, Mathf.Max(inertiaTensor.y, inertiaTensor.z)), 1e-6f);
        }

        private struct DriveBackup
        {
            public ArticulationDrive Articulation;
            public JointDrive Angular;
            public JointDrive Linear;
        }

        private static string LastSegment(string path)
        {
            int slash = (path ?? string.Empty).LastIndexOf('/');
            return slash >= 0 ? path.Substring(slash + 1) : path ?? string.Empty;
        }
    }
}
