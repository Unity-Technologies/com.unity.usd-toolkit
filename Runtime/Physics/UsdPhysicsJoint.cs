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

        /// <summary>physxJoint:maxJointVelocity and the armature: recorded, not applied.</summary>
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
            if (!Articulated || Link == null || Link.dofCount < 1)
            {
                return 0f;
            }

            float value = Link.jointPosition[0];
            float usd = IsAngular ? value * Mathf.Rad2Deg : (float)(value / MetersPerUnit);
            return Reversed ? -usd : usd;
        }

        /// <summary>Places an articulated joint at a position (USD units), at rest. Play mode only.</summary>
        public void SetPosition(float usdPosition)
        {
            if (!Articulated || Link == null || Link.dofCount < 1)
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
            if (!Articulated || Link == null)
            {
                return;
            }

            ArticulationDrive drive = Link.xDrive;
            float signed = Reversed ? -usdTarget : usdTarget;
            drive.target = IsAngular ? signed : (float)(signed * MetersPerUnit);
            Link.xDrive = drive;
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
