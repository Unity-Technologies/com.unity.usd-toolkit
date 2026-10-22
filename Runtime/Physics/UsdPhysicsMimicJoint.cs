using UnityEngine;

namespace Unity.USDToolkit
{
    /// <summary>
    /// A PhysxMimicJointAPI coupling as authored: the joint on this object (the follower) follows
    /// its reference joint. PhysX holds <c>follower + Gearing * reference + Offset = 0</c> with a
    /// constraint; Unity has no joint coupling, so the importer gives the follower a drive and this
    /// component moves the drive's target with the reference joint every physics step. It follows
    /// a driven gripper closely, but it is a drive: it lags a step and yields under load.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("")]
    public sealed class UsdPhysicsMimicJoint : MonoBehaviour
    {
        /// <summary>The joint that follows (on this object).</summary>
        public UsdPhysicsJoint Follower;

        /// <summary>The joint it follows (<c>physxMimicJoint:&lt;dof&gt;:referenceJoint</c>).</summary>
        public UsdPhysicsJoint Reference;

        public string ReferenceJointPath = string.Empty;

        /// <summary><c>physxMimicJoint:&lt;dof&gt;:gearing</c>: -1 makes the follower match the reference.</summary>
        public float Gearing = 1f;

        /// <summary><c>physxMimicJoint:&lt;dof&gt;:offset</c>, in radians for a rotation and stage units for a translation.</summary>
        public float Offset;

        /// <summary>
        /// Where the follower is held (USD units: degrees or stage units) for a reference joint
        /// position (USD units). PhysX's relation is in radians, so rotations are converted.
        /// </summary>
        public float FollowerPosition(float referencePosition)
        {
            float reference = Reference != null && Reference.IsAngular ? referencePosition * Mathf.Deg2Rad : referencePosition;
            float follower = -(Gearing * reference) - Offset;
            return Follower != null && Follower.IsAngular ? follower * Mathf.Rad2Deg : follower;
        }

        /// <summary>Sets the follower's drive target from where the reference joint is now.</summary>
        public void Step()
        {
            if (Follower != null && Reference != null)
            {
                Follower.SetDriveTarget(FollowerPosition(Reference.GetPosition()));
            }
        }

        private void FixedUpdate() => Step();
    }
}
