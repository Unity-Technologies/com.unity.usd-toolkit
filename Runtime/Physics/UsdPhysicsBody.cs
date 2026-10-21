using UnityEngine;

namespace Unity.USDToolkit
{
    /// <summary>
    /// The values a <see cref="UsdPhysicsBody"/>'s Unity body holds, converted back to the stage's
    /// units (its kilogramsPerUnit and metersPerUnit, USD's basis).
    /// </summary>
    public struct UsdPhysicsBodyReadback
    {
        public float Mass;
        public Vector3 CenterOfMass;
        public Vector3 DiagonalInertia;
        public bool Kinematic;
        public bool Immovable;
        public bool UseGravity;
        public float LinearDamping;
        public float AngularDamping;
    }

    /// <summary>
    /// A UsdPhysics rigid body prim as authored (PhysicsRigidBodyAPI, PhysicsMassAPI and PhysX's
    /// rigid body attributes), kept by the importer next to the <see cref="ArticulationBody"/> or
    /// <see cref="Rigidbody"/> it built (<see cref="UsdImportOptions.ImportPhysics"/>). Values are
    /// in the stage's units and basis; each <c>Has…</c> flag says the attribute is authored, so
    /// Unity's own default was kept otherwise.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("")]
    public sealed class UsdPhysicsBody : MonoBehaviour
    {
        public string PrimPath = string.Empty;

        /// <summary>The PhysicsArticulationRootAPI prim this body is a link of; empty when free.</summary>
        public string ArticulationPath = string.Empty;

        public bool HasMass;
        public float Mass;
        public bool HasCenterOfMass;
        public Vector3 CenterOfMass;
        public bool HasInertia;
        public Vector3 DiagonalInertia;
        public Quaternion PrincipalAxes = Quaternion.identity;

        public bool Kinematic;
        public bool DisableGravity;
        public bool StartsAsleep;
        public float LinearDamping;
        public float AngularDamping;

        /// <summary>physics:velocity (stage units / s) and physics:angularVelocity (degrees / s).</summary>
        public Vector3 LinearVelocity;
        public Vector3 AngularVelocity;

        /// <summary>
        /// The authored inertia is more than a thousand times below what the body's colliders and
        /// mass give: a placeholder, which Unity's PhysX cannot keep stable in a driven
        /// articulation. The body still uses the authored value; <see cref="UseColliderInertia"/>
        /// switches to <see cref="ColliderInertia"/>.
        /// </summary>
        public bool ImplausibleInertia;

        /// <summary>The inertia Unity derives from the body's colliders and mass, in Unity's units (kg·m²).</summary>
        public Vector3 ColliderInertia;
        public Quaternion ColliderInertiaRotation = Quaternion.identity;

        public Transform StageRoot;
        public double MetersPerUnit = 1.0;
        public double KilogramsPerUnit = 1.0;

        /// <summary>What the mapping could not keep; empty when nothing.</summary>
        public string Note = string.Empty;

        private void Start()
        {
            if (!Application.isPlaying || !TryGetComponent(out Rigidbody body) || body.isKinematic)
            {
                return;
            }

            if (LinearVelocity != Vector3.zero || AngularVelocity != Vector3.zero)
            {
                body.linearVelocity = StageToWorld(LinearVelocity) * (float)MetersPerUnit;
                body.angularVelocity = -StageToWorld(AngularVelocity) * Mathf.Deg2Rad;
            }

            if (StartsAsleep)
            {
                body.Sleep();
            }
        }

        /// <summary>
        /// Gives the Unity body the inertia derived from its colliders (true) or the authored one
        /// (false). For simulating a stage whose authored inertia is a placeholder.
        /// </summary>
        public void UseColliderInertia(bool collider)
        {
            if (!HasInertia)
            {
                return;
            }

            float scale = (float)(KilogramsPerUnit * MetersPerUnit * MetersPerUnit);
            Vector3 tensor = collider ? ColliderInertia : DiagonalInertia * scale;
            Quaternion rotation = collider ? ColliderInertiaRotation : new Quaternion(PrincipalAxes.x, -PrincipalAxes.y, -PrincipalAxes.z, PrincipalAxes.w);
            if (TryGetComponent(out ArticulationBody link))
            {
                link.automaticInertiaTensor = false;
                link.inertiaTensor = tensor;
                link.inertiaTensorRotation = rotation;
            }
            else if (TryGetComponent(out Rigidbody body))
            {
                body.automaticInertiaTensor = false;
                body.inertiaTensor = tensor;
                body.inertiaTensorRotation = rotation;
            }
        }

        /// <summary>Reads the Unity body back in the stage's units.</summary>
        public bool TryReadUnityValues(out UsdPhysicsBodyReadback values)
        {
            values = default;
            double kpu = KilogramsPerUnit > 0.0 ? KilogramsPerUnit : 1.0;
            double mpu = MetersPerUnit > 0.0 ? MetersPerUnit : 1.0;
            Vector3 com;
            Vector3 inertia;
            if (TryGetComponent(out ArticulationBody link))
            {
                values.Mass = (float)(link.mass / kpu);
                com = link.centerOfMass;
                inertia = link.inertiaTensor;
                values.Immovable = link.isRoot && link.immovable;
                values.UseGravity = link.useGravity;
                values.LinearDamping = link.linearDamping;
                values.AngularDamping = link.angularDamping;
            }
            else if (TryGetComponent(out Rigidbody body))
            {
                values.Mass = (float)(body.mass / kpu);
                com = body.centerOfMass;
                inertia = body.inertiaTensor;
                values.Kinematic = body.isKinematic;
                values.UseGravity = body.useGravity;
                values.LinearDamping = body.linearDamping;
                values.AngularDamping = body.angularDamping;
            }
            else
            {
                return false;
            }

            values.CenterOfMass = new Vector3(-com.x, com.y, com.z);
            values.DiagonalInertia = inertia / (float)(kpu * mpu * mpu);
            return true;
        }

        private Vector3 StageToWorld(Vector3 usd)
        {
            var stage = new Vector3(-usd.x, usd.y, usd.z);
            return StageRoot != null ? StageRoot.TransformDirection(stage) : stage;
        }
    }
}
