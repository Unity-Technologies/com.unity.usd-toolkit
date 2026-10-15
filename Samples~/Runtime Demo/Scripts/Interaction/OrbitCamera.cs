using UnityEngine;
using UnityEngine.InputSystem;

namespace Unity.USDToolkit.RuntimeDemo
{
    /// <summary>
    /// DCC-style orbit/pan/zoom. Right drag orbits, left or middle drag grabs and pans the scene,
    /// wheel zooms toward the cursor.
    /// </summary>
    /// <remarks>
    /// Left drag is started by the app, not here: it owns the click-versus-drag decision so a
    /// click still selects and only a deliberate drag moves the view. A missed click therefore
    /// never swings the camera, which looks like a bug from the back of a room.
    ///
    /// Panning is a "grab": the point under the cursor when the drag starts stays under the
    /// cursor, so the scene moves exactly with the hand at any distance. Orbit, zoom and framing
    /// are damped; the grab is not, because any lag would make the scene slide off the cursor.
    ///
    /// This project is set to the new Input System only (activeInputHandler: 1), so the legacy
    /// UnityEngine.Input API would throw here.
    /// </remarks>
    [RequireComponent(typeof(Camera))]
    public sealed class OrbitCamera : MonoBehaviour
    {
        [SerializeField] private Vector3 pivot = new Vector3(0.0f, 0.35f, 0.0f);
        [SerializeField] private float distance = 3.2f;
        [SerializeField] private float yaw = 35.0f;
        [SerializeField] private float pitch = 22.0f;

        private const float MinDistance = 0.35f;
        private const float MaxDistance = 40.0f;
        private const float OrbitSpeed = 0.22f;
        private const float ZoomSpeed = 0.1f;
        private const float MaxGrabDistance = 1000.0f;

        // Exponential damping rate; higher settles faster. 12 settles in roughly a quarter second.
        private const float Damping = 12.0f;

        public bool InputEnabled { get; set; } = true;

        /// <summary>
        /// Set each frame by the app. Blocks the wheel and blocks *starting* a drag over the UI,
        /// but never interrupts a drag already in progress — otherwise an orbit that wanders
        /// across the sidebar would stop dead halfway.
        /// </summary>
        public bool PointerOverUi { get; set; }

        public bool IsGrabbing => grabbing;

        private Camera cameraComponent;

        // What the camera is heading toward; the serialized fields above are where it is now.
        private Vector3 targetPivot;
        private float targetDistance;
        private float targetYaw;
        private float targetPitch;

        private bool orbiting;
        private bool middlePanning;
        private bool grabbing;
        private Plane grabPlane;
        private Vector3 grabPoint;

        private void Awake()
        {
            cameraComponent = GetComponent<Camera>();
            SnapTargetsToCurrent();
        }

        private void Start()
        {
            Apply();
        }

        private void Update()
        {
            Mouse mouse = Mouse.current;
            if (mouse != null && InputEnabled)
            {
                HandleMouse(mouse);
            }

            float t = 1.0f - Mathf.Exp(-Damping * Time.unscaledDeltaTime);
            yaw = Mathf.LerpAngle(yaw, targetYaw, t);
            pitch = Mathf.Lerp(pitch, targetPitch, t);
            distance = Mathf.Lerp(distance, targetDistance, t);
            if (!grabbing)
            {
                pivot = Vector3.Lerp(pivot, targetPivot, t);
            }

            Apply();

            // The orientation may have moved this frame; keep the grabbed point pinned.
            if (grabbing && mouse != null)
            {
                UpdateGrab(ScreenRay(mouse));
            }
        }

        private void HandleMouse(Mouse mouse)
        {
            Vector2 delta = mouse.delta.ReadValue();

            if (mouse.rightButton.wasPressedThisFrame && !PointerOverUi)
            {
                orbiting = true;
            }

            if (!mouse.rightButton.isPressed)
            {
                orbiting = false;
            }

            if (mouse.middleButton.wasPressedThisFrame && !PointerOverUi && !grabbing)
            {
                middlePanning = true;
                BeginGrab(ScreenRay(mouse));
            }

            if (middlePanning && !mouse.middleButton.isPressed)
            {
                middlePanning = false;
                EndGrab();
            }

            if (orbiting)
            {
                targetYaw += delta.x * OrbitSpeed;
                targetPitch = Mathf.Clamp(targetPitch - delta.y * OrbitSpeed, -85.0f, 85.0f);
            }

            float scroll = PointerOverUi ? 0.0f : mouse.scroll.ReadValue().y;
            if (!Mathf.Approximately(scroll, 0.0f))
            {
                ZoomTowards(ScreenRay(mouse), scroll);
            }
        }

        /// <summary>
        /// Dollies in or out while keeping the point under the cursor fixed on screen, so zooming
        /// goes where the presenter is pointing instead of always into the middle.
        /// </summary>
        private void ZoomTowards(Ray ray, float scroll)
        {
            float factor = 1.0f - Mathf.Clamp(scroll * ZoomSpeed * 0.02f, -0.5f, 0.5f);
            float newDistance = Mathf.Clamp(targetDistance * factor, MinDistance, MaxDistance);
            float applied = newDistance / targetDistance;

            // Scale about the cursor point on the plane through the pivot, facing the camera.
            var plane = new Plane(-transform.forward, targetPivot);
            if (plane.Raycast(ray, out float enter) && enter > 0.0f)
            {
                Vector3 cursorPoint = ray.GetPoint(enter);
                targetPivot = cursorPoint + (targetPivot - cursorPoint) * applied;
            }

            targetDistance = newDistance;
        }

        /// <summary>
        /// Starts a grab-pan. The anchor is the surface under the cursor if there is one, else a
        /// plane through the orbit pivot, so empty background still drags at a sensible rate.
        /// </summary>
        public void BeginGrab(Ray ray)
        {
            Vector3 anchor;
            if (Physics.Raycast(ray, out RaycastHit hit, MaxGrabDistance))
            {
                anchor = hit.point;
            }
            else
            {
                var pivotPlane = new Plane(-transform.forward, pivot);
                anchor = pivotPlane.Raycast(ray, out float enter) && enter > 0.0f
                    ? ray.GetPoint(enter)
                    : pivot;
            }

            grabbing = true;
            grabPoint = anchor;
            grabPlane = new Plane(-transform.forward, anchor);
            // Settle any pending pan/zoom so the grab starts from where the camera really is.
            targetPivot = pivot;
        }

        private void UpdateGrab(Ray ray)
        {
            if (!grabbing)
            {
                return;
            }

            // Re-face the plane each frame so an orbit mid-grab does not skew the drag.
            grabPlane = new Plane(-transform.forward, grabPoint);
            if (!grabPlane.Raycast(ray, out float enter) || enter <= 0.0f || enter > MaxGrabDistance)
            {
                return;
            }

            Vector3 shift = grabPoint - ray.GetPoint(enter);
            pivot += shift;
            targetPivot = pivot;
            Apply();
        }

        public void EndGrab()
        {
            grabbing = false;
            middlePanning = false;
        }

        /// <summary>Frames a world bounds, used after an import so new content is never off screen.</summary>
        public void Frame(Bounds bounds)
        {
            targetPivot = bounds.center;
            float radius = Mathf.Max(bounds.extents.magnitude, 0.2f);
            targetDistance = Mathf.Clamp(radius * 2.6f, MinDistance, MaxDistance);
        }

        private Ray ScreenRay(Mouse mouse)
        {
            return cameraComponent.ScreenPointToRay(mouse.position.ReadValue());
        }

        private void SnapTargetsToCurrent()
        {
            targetPivot = pivot;
            targetDistance = distance;
            targetYaw = yaw;
            targetPitch = pitch;
        }

        private void Apply()
        {
            Quaternion rotation = Quaternion.Euler(pitch, yaw, 0.0f);
            transform.SetPositionAndRotation(pivot - rotation * Vector3.forward * distance, rotation);
        }
    }
}
