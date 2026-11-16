using UnityEngine;

namespace Unity.USDToolkit.Samples.SimReadyCentral
{
    /// <summary>
    /// An orbit camera for the viewer: orbits a target, pans in the view plane, zooms toward it,
    /// and frames an asset's bounds when one loads. Driven by <see cref="SimReadyViewer"/>'s
    /// pointer events (UI Toolkit), so it needs no input package.
    /// </summary>
    [RequireComponent(typeof(Camera))]
    public sealed class SimReadyOrbitCamera : MonoBehaviour
    {
        public Vector3 Target;
        public float Distance = 1f;
        public float Yaw = 35f;
        public float Pitch = 22f;
        public float OrbitDegreesPerPixel = 0.3f;
        public float ZoomPerWheelStep = 0.1f;

        [Tooltip("Far clip plane (metres) the camera keeps when it frames an asset.")]
        public float FarClip = 1000f;

        [Tooltip("The share of the screen (from the left) a panel covers: the view is centred on the rest.")]
        [Range(0f, 0.9f)]
        public float ViewportLeft;

        private Camera view;

        public Camera Camera => view != null ? view : view = GetComponent<Camera>();

        private void LateUpdate() => Apply();

        /// <summary>Looks at <paramref name="bounds"/> from the current direction, the whole of it in view.</summary>
        public void Frame(Bounds bounds)
        {
            Target = bounds.center;
            float radius = Mathf.Max(bounds.extents.magnitude, 1e-3f);
            float fov = Camera.fieldOfView * Mathf.Deg2Rad * 0.5f;
            // Only the part of the screen right of the panel is seen.
            float visibleAspect = Camera.aspect * (1f - Mathf.Clamp(ViewportLeft, 0f, 0.9f));
            float aspectFov = Mathf.Atan(Mathf.Tan(fov) * Mathf.Max(visibleAspect, 1e-3f));
            Distance = radius / Mathf.Sin(Mathf.Min(fov, aspectFov)) * 1.15f;
            Camera.nearClipPlane = Mathf.Max(radius * 0.01f, 0.001f);
            Camera.farClipPlane = Mathf.Max(FarClip, Distance * 2f);
            Apply();
        }

        public void Orbit(Vector2 pixels)
        {
            Yaw += pixels.x * OrbitDegreesPerPixel;
            Pitch = Mathf.Clamp(Pitch - (pixels.y * OrbitDegreesPerPixel), -85f, 85f);
        }

        /// <summary>Moves the target in the view plane so the point under the mouse follows it.</summary>
        public void Pan(Vector2 pixels)
        {
            float worldPerPixel = 2f * Distance * Mathf.Tan(Camera.fieldOfView * Mathf.Deg2Rad * 0.5f) / Mathf.Max(Screen.height, 1);
            Target -= (transform.right * pixels.x - transform.up * pixels.y) * worldPerPixel;
        }

        public void Zoom(float steps) => Distance = Mathf.Max(Distance * Mathf.Pow(1f + ZoomPerWheelStep, steps), 1e-3f);

        private void Apply()
        {
            Quaternion rotation = Quaternion.Euler(Pitch, Yaw, 0f);
            transform.SetPositionAndRotation(Target - (rotation * Vector3.forward * Distance), rotation);

            // An off-centre projection: the camera still covers the whole screen, but its centre
            // moves to the middle of the part a panel does not cover, so the target is seen there.
            Camera.ResetProjectionMatrix();
            float left = Mathf.Clamp(ViewportLeft, 0f, 0.9f);
            if (left > 0f)
            {
                Matrix4x4 projection = Camera.projectionMatrix;
                projection.m02 = -left;
                Camera.projectionMatrix = projection;
            }
        }
    }
}
