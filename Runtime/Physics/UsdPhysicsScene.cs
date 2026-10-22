using UnityEngine;

namespace Unity.USDToolkit
{
    /// <summary>
    /// A UsdPhysics PhysicsScene prim as authored. Gravity and the step rate are project-wide in
    /// Unity, so the importer records them here and leaves the project's settings alone;
    /// <see cref="Apply"/> sets them.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("")]
    public sealed class UsdPhysicsScene : MonoBehaviour
    {
        public string PrimPath = string.Empty;

        /// <summary>Gravity's direction in the import root's local space (Unity's basis).</summary>
        public Vector3 GravityDirection = Vector3.down;

        /// <summary>Gravity in m/s², converted from stage units.</summary>
        public float GravityMagnitude = 9.81f;

        /// <summary>physxScene:timeStepsPerSecond; 0 when unauthored.</summary>
        public float TimeStepsPerSecond;

        /// <summary>
        /// Sets <see cref="Physics.gravity"/> to the stage's, turned with the import root, and
        /// <see cref="Time.fixedDeltaTime"/> to its step rate when authored.
        /// </summary>
        public void Apply()
        {
            Vector3 direction = transform.TransformDirection(GravityDirection).normalized;
            Physics.gravity = direction * GravityMagnitude;
            if (TimeStepsPerSecond > 0f)
            {
                Time.fixedDeltaTime = 1f / TimeStepsPerSecond;
            }
        }
    }
}
