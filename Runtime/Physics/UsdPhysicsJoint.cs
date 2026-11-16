using UnityEngine;

namespace Unity.USDToolkit
{
    /// <summary>The UsdPhysics joint schema a joint prim authors.</summary>
    public enum UsdPhysicsJointType
    {
        Fixed,
        Revolute,
        Prismatic,
        Spherical,
        Distance,

        /// <summary>A plain PhysicsJoint: per-axis limits and drives (PhysX's D6 joint).</summary>
        Generic
    }

    /// <summary>A joint or degree-of-freedom axis, in the joint's own frame.</summary>
    public enum UsdPhysicsAxis
    {
        X,
        Y,
        Z
    }

    /// <summary>
    /// The values a <see cref="UsdPhysicsJoint"/>'s Unity joint holds, converted back to the units
    /// and sign the USD joint authors them in (degrees or stage units, body0 to body1), so they
    /// compare one to one with what Isaac Sim's Property panel lists for the prim.
    /// </summary>
    public struct UsdPhysicsJointReadback
    {
        /// <summary>The Unity joint limits the axis (not Free).</summary>
        public bool LimitEnabled;
        public float LowerLimit;
        public float UpperLimit;

        /// <summary>Drive stiffness and damping per degree (angular) or per stage unit (linear).</summary>
        public float DriveStiffness;
        public float DriveDamping;
        public float DriveMaxForce;
        public float DriveTargetPosition;
        public float DriveTargetVelocity;

        /// <summary>The joint axis in stage space (USD's basis, right-handed), from the Unity anchor.</summary>
        public Vector3 AxisInStage;

        /// <summary>False for what the Unity joint cannot express (drives outside an articulation).</summary>
        public bool HasDrive;
    }

    /// <summary>
    /// A UsdPhysics joint prim as authored, kept by the importer next to the Unity joint it built
    /// (<see cref="UsdImportOptions.ImportPhysics"/>). Values are USD's: degrees for rotation, stage
    /// units for translation, the stage's mass unit, body0 to body1 — what Isaac Sim shows for the
    /// prim. <see cref="TryReadUnityValues"/> converts the Unity joint back to the same units, so
    /// the two can be compared. Joints inside an articulation become the child link's
    /// <see cref="ArticulationBody"/> joint; the others a <see cref="ConfigurableJoint"/> or
    /// <see cref="FixedJoint"/> on body1.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("")]
    public sealed class UsdPhysicsJoint : MonoBehaviour
    {
        /// <summary>The joint prim.</summary>
        public string PrimPath = string.Empty;
        public string Body0Path = string.Empty;
        public string Body1Path = string.Empty;

        /// <summary>The joint's schema.</summary>
        public UsdPhysicsJointType Type;

        /// <summary>
        /// What Unity built: the schema itself, or for a <see cref="UsdPhysicsJointType.Generic"/>
        /// joint the single-axis joint its free degrees of freedom amount to.
        /// </summary>
        public UsdPhysicsJointType UnityType;

        /// <summary>physics:axis, or the axis of the degree of freedom a Generic joint maps to.</summary>
        public UsdPhysicsAxis Axis;

        /// <summary>The joint axis in stage space (USD's basis), from body0's pose and localRot0.</summary>
        public Vector3 AxisInStage = Vector3.right;

        /// <summary>physics:lowerLimit / upperLimit; lower above upper locks the axis.</summary>
        public bool LimitEnabled;
        public float LowerLimit;
        public float UpperLimit;

        /// <summary>physics:coneAngle0Limit / coneAngle1Limit of a spherical joint, degrees.</summary>
        public bool ConeLimitEnabled;
        public float ConeAngle0;
        public float ConeAngle1;

        /// <summary>PhysicsDriveAPI on the joint's axis.</summary>
        public bool DriveEnabled;
        public bool DriveIsAcceleration;
        public float DriveStiffness;
        public float DriveDamping;
        public float DriveMaxForce = float.PositiveInfinity;
        public float DriveTargetPosition;
        public float DriveTargetVelocity;

        /// <summary>PhysicsJointStateAPI: the position the joint starts at, applied when play starts.</summary>
        public bool HasInitialPosition;
        public float InitialPosition;

        /// <summary>physxJoint:jointFriction, applied to the articulation link.</summary>
        public float JointFriction;

        /// <summary>
        /// physxJoint:maxJointVelocity (recorded, not applied) and the armature, which the importer
        /// adds to the link's inertia inside an articulation (Unity has no armature).
        /// </summary>
        public float MaxJointVelocity = float.PositiveInfinity;
        public float Armature;

        /// <summary>physics:collisionEnabled: whether body0 and body1 collide with each other.</summary>
        public bool CollisionEnabled;

        /// <summary>The joint is a link of an articulation (an <see cref="ArticulationBody"/>).</summary>
        public bool Articulated;

        /// <summary>
        /// The Unity parent is the USD joint's body1: the articulation tree reached the joint from
        /// that side, so Unity measures body0 against body1 and every value changes sign.
        /// </summary>
        public bool Reversed;

        /// <summary>The import root: stage space is its local space (USD's X negated).</summary>
        public Transform StageRoot;

        /// <summary>The stage's metersPerUnit and kilogramsPerUnit at import.</summary>
        public double MetersPerUnit = 1.0;
        public double KilogramsPerUnit = 1.0;

        /// <summary>The articulation link this joint drives, when <see cref="Articulated"/>.</summary>
        public ArticulationBody Link;

        /// <summary>The Unity joint, when not <see cref="Articulated"/>.</summary>
        public Joint UnityJoint;

        /// <summary>What the mapping could not keep, in a sentence; empty when nothing.</summary>
        public string Note = string.Empty;

        // The pose the import built the joint at (CaptureRest): the moving body in its frame's
        // space, the joint's pivot and axis there, and the USD position that pose is.
        [SerializeField, HideInInspector] private bool restCaptured;
        [SerializeField, HideInInspector] private Transform movingTransform;
        [SerializeField, HideInInspector] private Transform frameTransform;
        [SerializeField, HideInInspector] private Quaternion restRotation = Quaternion.identity;
        [SerializeField, HideInInspector] private Vector3 restOffset;
        [SerializeField, HideInInspector] private Vector3 restPivot;
        [SerializeField, HideInInspector] private Vector3 axisInFrame = Vector3.right;
        [SerializeField, HideInInspector] private Vector3 stageUnitInFrame = Vector3.right;
        [SerializeField, HideInInspector] private float restPosition;

        /// <summary>The joint has the rest pose <see cref="MeasurePosition"/> and <see cref="PoseKinematically"/> work from.</summary>
        public bool HasRestPose => restCaptured && movingTransform != null;

        /// <summary>The body the joint moves (its Unity joint's object).</summary>
        public Transform MovingTransform => movingTransform;

        /// <summary>
        /// What the joint moves <see cref="MovingTransform"/> against: the parent link, the
        /// connected body, or for a joint to the world the import root.
        /// </summary>
        public Transform FrameTransform => frameTransform;

        /// <summary>The USD position (degrees or stage units) of the pose the import built.</summary>
        public float RestPosition => restPosition;

        /// <summary>The joint's pivot in world space, where it is now.</summary>
        public Vector3 PivotWorld => frameTransform != null ? frameTransform.TransformPoint(restPivot) : Vector3.zero;

        /// <summary>The joint's axis in world space, as the frame holds it now (USD's direction, in Unity's basis).</summary>
        public Vector3 AxisWorld => frameTransform != null ? frameTransform.TransformDirection(axisInFrame).normalized : Vector3.right;

        /// <summary>
        /// World displacement of the moving body for one USD unit of the joint (a stage unit along
        /// a prismatic axis; for a revolute joint, the motion of <paramref name="worldPoint"/> per degree).
        /// </summary>
        public Vector3 WorldMotionPerUnit(Vector3 worldPoint)
        {
            if (frameTransform == null)
            {
                return Vector3.zero;
            }

            float sign = Reversed ? -1f : 1f;
            if (!IsAngular)
            {
                return frameTransform.TransformVector(stageUnitInFrame) * sign;
            }

            // USD's right-handed +1 degree is Unity's -1 degree about the mirrored axis.
            Vector3 pivot = PivotWorld;
            Vector3 turned = Quaternion.AngleAxis(-sign, AxisWorld) * (worldPoint - pivot);
            return turned - (worldPoint - pivot);
        }

        /// <summary>
        /// Records the current pose as the joint's rest pose, at <see cref="UsdPhysicsJoint.HasInitialPosition"/>
        /// or 0. The importer calls it once the joint is built; the pose it builds is the authored
        /// one, which is where the stage's JointStateAPI puts the joint.
        /// </summary>
        public void CaptureRest(Transform moving, Transform frame, Vector3 pivotWorld)
        {
            if (moving == null || StageRoot == null)
            {
                return;
            }

            frame = frame != null ? frame : StageRoot;
            movingTransform = moving;
            frameTransform = frame;
            restRotation = Quaternion.Inverse(frame.rotation) * moving.rotation;
            restOffset = frame.InverseTransformPoint(moving.position);
            restPivot = frame.InverseTransformPoint(pivotWorld);
            Vector3 axisLocal = new Vector3(-AxisInStage.x, AxisInStage.y, AxisInStage.z).normalized;
            axisInFrame = frame.InverseTransformDirection(StageRoot.TransformDirection(axisLocal)).normalized;
            stageUnitInFrame = frame.InverseTransformVector(StageRoot.TransformVector(axisLocal));
            restPosition = HasInitialPosition ? InitialPosition : 0f;
            restCaptured = true;
        }

        // A joint outside an articulation: its rest is the pose the ConfigurableJoint was built at.
        internal void CaptureFreeRest()
        {
            if (UnityJoint == null)
            {
                return;
            }

            Transform host = UnityJoint.transform;
            Transform frame = UnityJoint.connectedBody != null ? UnityJoint.connectedBody.transform
                : UnityJoint.connectedArticulationBody != null ? UnityJoint.connectedArticulationBody.transform
                : null;
            CaptureRest(host, frame, host.TransformPoint(UnityJoint.anchor));
        }

        /// <summary>
        /// The joint's position read from the transforms (USD units and sign), in edit mode or
        /// play mode, inside an articulation or not. 0 without a rest pose.
        /// </summary>
        public float MeasurePosition()
        {
            if (!HasRestPose || frameTransform == null)
            {
                return 0f;
            }

            float sign = Reversed ? -1f : 1f;
            if (IsAngular)
            {
                Quaternion current = Quaternion.Inverse(frameTransform.rotation) * movingTransform.rotation;
                Quaternion delta = current * Quaternion.Inverse(restRotation);
                // USD's right-handed angle is minus Unity's about the mirrored axis.
                return restPosition - (sign * TwistDegrees(delta, axisInFrame));
            }

            Vector3 moved = frameTransform.InverseTransformPoint(movingTransform.position) - restOffset;
            float unit = stageUnitInFrame.sqrMagnitude;
            return unit > 0f ? restPosition + (sign * Vector3.Dot(moved, stageUnitInFrame) / unit) : restPosition;
        }

        /// <summary>
        /// Places the moving body where the joint at <paramref name="usdPosition"/> puts it,
        /// against its frame where that is now, without simulating: the edit-mode way to pose an
        /// asset. Bodies below it in the hierarchy move with it; a body jointed to it elsewhere
        /// needs its own joint posed after this one.
        /// </summary>
        public void PoseKinematically(float usdPosition)
        {
            if (!HasRestPose || frameTransform == null)
            {
                return;
            }

            float delta = (usdPosition - restPosition) * (Reversed ? -1f : 1f);
            Vector3 offset = restOffset;
            Quaternion rotation = restRotation;
            if (IsAngular)
            {
                Quaternion turn = Quaternion.AngleAxis(-delta, axisInFrame);
                offset = restPivot + (turn * (restOffset - restPivot));
                rotation = turn * restRotation;
            }
            else
            {
                offset += stageUnitInFrame * delta;
            }

            movingTransform.SetPositionAndRotation(frameTransform.TransformPoint(offset), frameTransform.rotation * rotation);
        }

        // The signed angle (degrees, Unity's sense) of the part of `rotation` about `axis`.
        internal static float TwistDegrees(Quaternion rotation, Vector3 axis)
        {
            Vector3 v = new Vector3(rotation.x, rotation.y, rotation.z);
            float angle = 2f * Mathf.Atan2(Vector3.Dot(v, axis.normalized), rotation.w) * Mathf.Rad2Deg;
            return Mathf.DeltaAngle(0f, angle);
        }

        /// <summary>A rotation (degrees) rather than a translation (stage units).</summary>
        public bool IsAngular => UnityType == UsdPhysicsJointType.Revolute || UnityType == UsdPhysicsJointType.Spherical;

        private void Start()
        {
            if (HasInitialPosition && Articulated && Link != null && Application.isPlaying)
            {
                SetPosition(InitialPosition);
            }
        }

        /// <summary>
        /// The joint's current position in USD's units and sign: degrees or stage units of body1
        /// relative to body0. Articulated joints only; 0 otherwise.
        /// </summary>
        public float GetPosition()
        {
            if (!Articulated)
            {
                return UnityJoint != null ? MeasurePosition() : 0f;
            }

            if (Link == null || Link.dofCount < 1)
            {
                return 0f;
            }

            float value = Link.jointPosition[0];
            float usd = IsAngular ? value * Mathf.Rad2Deg : (float)(value / MetersPerUnit);
            return Reversed ? -usd : usd;
        }

        /// <summary>
        /// The joint's velocity in USD's units and sign: degrees or stage units per second of body1
        /// relative to body0. Articulated joints only; 0 otherwise.
        /// </summary>
        public float GetVelocity()
        {
            if (!Articulated || Link == null || Link.dofCount < 1)
            {
                return 0f;
            }

            float value = Link.jointVelocity[0];
            float usd = IsAngular ? value * Mathf.Rad2Deg : (float)(value / MetersPerUnit);
            return Reversed ? -usd : usd;
        }

        /// <summary>
        /// Sets the drive's target velocity (USD units per second). Articulated joints only: with it
        /// the drive's damping pulls towards that velocity instead of towards rest.
        /// </summary>
        public void SetDriveTargetVelocity(float usdVelocity)
        {
            if (!Articulated || Link == null)
            {
                return;
            }

            ArticulationDrive drive = Link.xDrive;
            float signed = Reversed ? -usdVelocity : usdVelocity;
            drive.targetVelocity = IsAngular ? signed : (float)(signed * MetersPerUnit);
            Link.xDrive = drive;
        }

        /// <summary>Places an articulated joint at a position (USD units), at rest. Play mode only.</summary>
        public void SetPosition(float usdPosition)
        {
            if (!Articulated)
            {
                if (UnityJoint != null)
                {
                    PoseKinematically(usdPosition);
                    if (UnityJoint.TryGetComponent(out Rigidbody body) && !body.isKinematic)
                    {
                        body.position = body.transform.position;
                        body.rotation = body.transform.rotation;
                        body.linearVelocity = Vector3.zero;
                        body.angularVelocity = Vector3.zero;
                    }
                }

                return;
            }

            if (Link == null || Link.dofCount < 1)
            {
                return;
            }

            float signed = Reversed ? -usdPosition : usdPosition;
            float unity = IsAngular ? signed * Mathf.Deg2Rad : (float)(signed * MetersPerUnit);
            Link.jointPosition = new ArticulationReducedSpace(unity);
            Link.jointVelocity = new ArticulationReducedSpace(0f);
        }

        /// <summary>
        /// Sets the drive target (USD units). Articulated joints only: the drive keeps its authored
        /// stiffness and damping, so a joint without a drive does not move.
        /// </summary>
        public void SetDriveTarget(float usdTarget)
        {
            if (!Articulated)
            {
                SetFreeDriveTarget(usdTarget);
                return;
            }

            if (Link == null)
            {
                return;
            }

            ArticulationDrive drive = Link.xDrive;
            float signed = Reversed ? -usdTarget : usdTarget;
            drive.target = IsAngular ? signed : (float)(signed * MetersPerUnit);
            Link.xDrive = drive;
        }

        // ConfigurableJoint targets are relative to the pose the joint was built at (the rest pose)
        // and point the opposite way to the joint's own measure: PhysX's D6 drives actor0 towards
        // the target expressed in actor1's frame. Checked by SimReady's FreeJointDriveTests.
        private const float FreeAngularTargetSign = -1f;
        private const float FreeLinearTargetSign = -1f;

        private void SetFreeDriveTarget(float usdTarget)
        {
            if (!(UnityJoint is ConfigurableJoint joint))
            {
                return;
            }

            float delta = (usdTarget - restPosition) * (Reversed ? -1f : 1f);
            if (IsAngular)
            {
                joint.targetRotation = Quaternion.AngleAxis(FreeAngularTargetSign * delta, Vector3.right);
            }
            else
            {
                // The builder centres the connected anchor on the limit range (UsdPhysicsBuilder.ConfigureFreeJoint).
                float middle = LimitEnabled && LowerLimit <= UpperLimit ? 0.5f * (LowerLimit + UpperLimit) : 0f;
                float fromMiddle = (usdTarget - middle) * (Reversed ? -1f : 1f);
                joint.targetPosition = new Vector3(FreeLinearTargetSign * (float)(fromMiddle * MetersPerUnit), 0f, 0f);
            }
        }

        /// <summary>
        /// Reads the Unity joint back in USD's units and sign. False when there is no Unity joint
        /// to read (a joint the mapping skipped).
        /// </summary>
        public bool TryReadUnityValues(out UsdPhysicsJointReadback values)
        {
            values = default;
            if (Articulated)
            {
                return Link != null && ReadArticulation(ref values);
            }

            return UnityJoint != null && ReadJoint(ref values);
        }

        private bool ReadArticulation(ref UsdPhysicsJointReadback values)
        {
            ArticulationBody parent = FindParentLink(Link);
            Transform parentTransform = parent != null ? parent.transform : null;
            Quaternion anchor = parentTransform != null
                ? parentTransform.rotation * Link.parentAnchorRotation
                : Link.transform.rotation * Link.anchorRotation;
            Vector3 axisWorld = anchor * Vector3.right;
            values.AxisInStage = UsdPhysicsSpace.WorldDirectionToStage(StageRoot, axisWorld);
            if (UnityType == UsdPhysicsJointType.Revolute)
            {
                // The anchor's X points along -B(axis) for a rotation (see UsdPhysicsBuilder.AnchorFrame).
                values.AxisInStage = -values.AxisInStage;
            }

            switch (UnityType)
            {
                case UsdPhysicsJointType.Revolute:
                    values.LimitEnabled = Link.twistLock != ArticulationDofLock.FreeMotion;
                    ReadDrive(Link.xDrive, ref values);
                    break;
                case UsdPhysicsJointType.Prismatic:
                    values.LimitEnabled = Link.linearLockX != ArticulationDofLock.FreeMotion;
                    ReadDrive(Link.xDrive, ref values);
                    break;
                case UsdPhysicsJointType.Spherical:
                    values.LimitEnabled = Link.swingYLock != ArticulationDofLock.FreeMotion;
                    values.LowerLimit = -Link.zDrive.upperLimit;
                    values.UpperLimit = Link.zDrive.upperLimit;
                    break;
            }

            return true;
        }

        private void ReadDrive(ArticulationDrive drive, ref UsdPhysicsJointReadback values)
        {
            UsdPhysicsSpace.DriveScales(IsAngular, MetersPerUnit, KilogramsPerUnit, out float length, out float stiffness, out float force);
            float lower = drive.lowerLimit / length;
            float upper = drive.upperLimit / length;
            values.LowerLimit = Reversed ? -upper : lower;
            values.UpperLimit = Reversed ? -lower : upper;
            values.HasDrive = true;
            values.DriveStiffness = drive.stiffness / stiffness;
            values.DriveDamping = drive.damping / stiffness;
            values.DriveMaxForce = drive.forceLimit >= float.MaxValue ? float.PositiveInfinity : drive.forceLimit / force;
            values.DriveTargetPosition = (Reversed ? -drive.target : drive.target) / length;
            values.DriveTargetVelocity = (Reversed ? -drive.targetVelocity : drive.targetVelocity) / length;
        }

        private bool ReadJoint(ref UsdPhysicsJointReadback values)
        {
            Vector3 axisWorld = UnityJoint.transform.TransformDirection(UnityJoint.axis);
            values.AxisInStage = UsdPhysicsSpace.WorldDirectionToStage(StageRoot, axisWorld);
            if (UnityType == UsdPhysicsJointType.Revolute)
            {
                values.AxisInStage = -values.AxisInStage;
            }

            if (UnityJoint is ConfigurableJoint configurable)
            {
                switch (UnityType)
                {
                    case UsdPhysicsJointType.Revolute:
                        values.LimitEnabled = configurable.angularXMotion != ConfigurableJointMotion.Free;
                        // A ConfigurableJoint measures its connected body against its own, the
                        // opposite way to USD's body1 against body0 (UsdPhysicsBuilder.ConfigurableAngularSign).
                        float low = configurable.lowAngularXLimit.limit * UsdPhysicsSpace.ConfigurableAngularSign;
                        float high = configurable.highAngularXLimit.limit * UsdPhysicsSpace.ConfigurableAngularSign;
                        values.LowerLimit = Mathf.Min(low, high);
                        values.UpperLimit = Mathf.Max(low, high);
                        break;
                    case UsdPhysicsJointType.Prismatic:
                        values.LimitEnabled = configurable.xMotion != ConfigurableJointMotion.Free;
                        float half = (float)(configurable.linearLimit.limit / MetersPerUnit);
                        float mid = 0.5f * (LowerLimit + UpperLimit);
                        values.LowerLimit = mid - half;
                        values.UpperLimit = mid + half;
                        break;
                    case UsdPhysicsJointType.Spherical:
                        values.LimitEnabled = configurable.angularZMotion != ConfigurableJointMotion.Free;
                        values.LowerLimit = -configurable.angularZLimit.limit;
                        values.UpperLimit = configurable.angularZLimit.limit;
                        break;
                }
            }

            if (Reversed)
            {
                (values.LowerLimit, values.UpperLimit) = (-values.UpperLimit, -values.LowerLimit);
            }

            return true;
        }

        private static ArticulationBody FindParentLink(ArticulationBody link)
        {
            Transform current = link.transform.parent;
            while (current != null)
            {
                if (current.TryGetComponent(out ArticulationBody body))
                {
                    return body;
                }

                current = current.parent;
            }

            return null;
        }
    }
}
