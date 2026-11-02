using System.Collections.Generic;
using UnityEngine;

namespace Unity.USDToolkit.ComposedGripper
{
    /// <summary>
    /// Opens and closes one imported gripper. Sits on the gripper's articulation root and commands
    /// every driven joint of that articulation with the same closing fraction: 0 is open (joint
    /// value 0 in the asset), 1 is closed (the joint limit with the larger magnitude). For the
    /// linkage gripper it commands the driver, follower and finger joints together, which keeps
    /// the four-bar closed where Unity cannot build the loop-closing joint.
    /// <para>
    /// In play mode the targets go to the joint drives through
    /// <see cref="UsdPhysicsJoint.SetDriveTarget"/>, in the asset's units and sign, and the physics
    /// moves the links. In edit mode, where physics does not run, each link is posed where the
    /// joint at its target puts it: on the parent's anchor, moved along or turned about the anchor's
    /// X axis, as the articulation measures its joint position.
    /// </para>
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    public sealed class GripperDrive : MonoBehaviour
    {
        [Tooltip("0 = open, 1 = closed.")]
        [Range(0f, 1f)]
        public float Closing;

        [Tooltip("Seconds for a full open, close, open cycle. 0 holds Closing as set.")]
        public float CycleSeconds;

        private readonly List<UsdPhysicsJoint> joints = new List<UsdPhysicsJoint>();
        private readonly List<float> closedTargets = new List<float>();

        // The closing fraction the links are posed at in edit mode (NaN: not posed since loading).
        private float posedClosing = float.NaN;

        /// <summary>The driven joints of this gripper.</summary>
        public IReadOnlyList<UsdPhysicsJoint> Joints => joints;

        /// <summary>
        /// The gripper's prim (/composed_gripper, or /World/gripper_a in the scene): its joints
        /// sit in its Physics scope.
        /// </summary>
        public string GripperPrimPath { get; private set; } = string.Empty;

        private void Awake() => Collect();

#if UNITY_EDITOR
        // Edit mode runs no physics and, unless something in the scene changes, no Update either,
        // so the pose follows Closing from the Editor's own update loop.
        private void OnEnable()
        {
            if (!Application.isPlaying)
            {
                if (joints.Count == 0)
                {
                    Collect();
                }

                UnityEditor.EditorApplication.update += EditorUpdate;
            }
        }

        private void OnDisable() => UnityEditor.EditorApplication.update -= EditorUpdate;

        private void EditorUpdate()
        {
            if (this == null || Application.isPlaying)
            {
                UnityEditor.EditorApplication.update -= EditorUpdate;
                return;
            }

            if (CycleSeconds > 0f)
            {
                Closing = CycleAt(Time.realtimeSinceStartup);
            }

            if (!(posedClosing == Closing))
            {
                ApplyPose(Closing);
                UnityEditor.SceneView.RepaintAll();
            }
        }
#endif

        /// <summary>Finds this articulation's driven joints among the import's joints.</summary>
        public void Collect()
        {
            joints.Clear();
            closedTargets.Clear();
            GripperPrimPath = string.Empty;

            UsdStageSource stage = GetComponentInParent<UsdStageSource>();
            Transform searchRoot = stage != null ? stage.transform : transform.root;
            foreach (UsdPhysicsJoint joint in searchRoot.GetComponentsInChildren<UsdPhysicsJoint>(true))
            {
                if (!joint.Articulated || joint.Link == null || !joint.Link.transform.IsChildOf(transform))
                {
                    continue;
                }

                if (GripperPrimPath.Length == 0)
                {
                    int physics = joint.PrimPath.LastIndexOf("/Physics/", System.StringComparison.Ordinal);
                    GripperPrimPath = physics > 0 ? joint.PrimPath.Substring(0, physics) : string.Empty;
                }

                if (!joint.DriveEnabled || joint.DriveStiffness <= 0f || !joint.LimitEnabled)
                {
                    continue;
                }

                joints.Add(joint);
                closedTargets.Add(Mathf.Abs(joint.UpperLimit) >= Mathf.Abs(joint.LowerLimit) ? joint.UpperLimit : joint.LowerLimit);
            }

            posedClosing = float.NaN;
        }

        private void FixedUpdate()
        {
            if (CycleSeconds > 0f)
            {
                Closing = CycleAt(Time.fixedTime);
            }

            for (int i = 0; i < joints.Count; i++)
            {
                if (joints[i] != null)
                {
                    joints[i].SetDriveTarget(Closing * closedTargets[i]);
                }
            }
        }

        private float CycleAt(float time) => 0.5f - (0.5f * Mathf.Cos(2f * Mathf.PI * time / CycleSeconds));

        /// <summary>
        /// Edit mode: poses the links at a closing fraction (0 puts them back where the import
        /// built them). Does nothing in play mode, where the joint drives move the links.
        /// </summary>
        public void ApplyPose(float closing)
        {
            if (Application.isPlaying)
            {
                return;
            }

            for (int i = 0; i < joints.Count; i++)
            {
                UsdPhysicsJoint joint = joints[i];
                if (joint == null || joint.Link == null)
                {
                    continue;
                }

                // The joint position the drive target would reach, in Unity's sign and units:
                // degrees about, or meters along, the anchor's X axis.
                float target = closing * closedTargets[i];
                float signed = joint.Reversed ? -target : target;
                Matrix4x4 motion = joint.IsAngular
                    ? Matrix4x4.Rotate(Quaternion.AngleAxis(signed, Vector3.right))
                    : Matrix4x4.Translate(Vector3.right * (float)(signed * joint.MetersPerUnit));

                ArticulationBody link = joint.Link;
                Matrix4x4 local = Matrix4x4.TRS(link.parentAnchorPosition, link.parentAnchorRotation, Vector3.one) * motion *
                                  Matrix4x4.TRS(link.anchorPosition, link.anchorRotation, Vector3.one).inverse;
                link.transform.SetLocalPositionAndRotation(local.GetColumn(3), local.rotation);
            }

            posedClosing = closing;
        }

        /// <summary>
        /// How far the gripper has closed, 0 to 1: in play mode from the first driven joint's
        /// position, in edit mode the pose applied.
        /// </summary>
        public float MeasuredClosing()
        {
            if (!Application.isPlaying)
            {
                return float.IsNaN(posedClosing) ? 0f : posedClosing;
            }

            if (joints.Count == 0 || joints[0] == null || closedTargets[0] == 0f)
            {
                return 0f;
            }

            return joints[0].GetPosition() / closedTargets[0];
        }
    }
}
