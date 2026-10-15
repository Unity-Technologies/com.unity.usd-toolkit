using System;
using UnityEngine;

namespace Unity.USDToolkit.RuntimeDemo
{
    public enum GizmoMode
    {
        Translate = 0,
        Rotate = 1
    }

    /// <summary>
    /// A translate/rotate gizmo that works in a player build.
    /// </summary>
    /// <remarks>
    /// Unity's editor handles do not exist at runtime, so this is a self-contained replacement
    /// rather than a wrapper around one. Scale is deliberately absent: a scale handle on a part
    /// that came out of a PLM system invites exactly the question you do not want on stage.
    ///
    /// Picking is analytic (ray against the axis line, ray against the ring plane) instead of
    /// collider based. That keeps the handles off the physics layers the scene raycast uses, so
    /// grabbing a handle can never be confused with selecting a part behind it, and it stays
    /// exact no matter how the gizmo is scaled for distance.
    /// </remarks>
    public sealed class RuntimeTransformGizmo : MonoBehaviour
    {
        private const float ShaftLength = 0.78f;
        private const float ShaftRadius = 0.018f;
        private const float HeadLength = 0.22f;
        private const float HeadRadius = 0.058f;
        private const float RingRadius = 0.85f;
        private const float RingTube = 0.016f;

        // Pick tolerance, in gizmo-local units (the gizmo is normalised to ~1 unit on screen).
        private const float AxisPickTolerance = 0.085f;
        private const float RingPickTolerance = 0.09f;

        // Ground handle: a flat ring under the part that drags it across the XZ plane.
        private const int GroundHandle = 3;
        private const float GroundRingWidth = 0.07f;
        private const float GroundRingMinRadius = 0.45f;
        private const float GroundRingPickTolerance = 0.07f;
        private const float GroundFootprintPadding = 1.08f;

        private static readonly Vector3[] Axes = { Vector3.right, Vector3.up, Vector3.forward };

        private static readonly Color GroundColor = new Color(0.86f, 0.89f, 0.95f, 0.7f);

        private static readonly Color[] AxisColors =
        {
            new Color(0.94f, 0.30f, 0.36f, 0.95f),
            new Color(0.48f, 0.86f, 0.32f, 0.95f),
            new Color(0.29f, 0.60f, 0.98f, 0.95f)
        };

        private static readonly Color HighlightColor = new Color(1.0f, 0.86f, 0.25f, 1.0f);

        private Camera targetCamera;
        private Transform target;
        private GizmoMode mode = GizmoMode.Translate;

        private readonly Transform[] translateRoots = new Transform[3];
        private readonly Transform[] rotateRoots = new Transform[3];
        private readonly Renderer[] translateRenderers = new Renderer[3];
        private readonly Renderer[] rotateRenderers = new Renderer[3];
        private MaterialPropertyBlock propertyBlock;

        private Transform groundRoot;
        private Renderer groundRenderer;
        private Mesh groundMesh;
        private float groundMeshRadius;
        private Renderer[] targetRenderers = Array.Empty<Renderer>();

        // World-space ground ring placement, refreshed every LateUpdate.
        private Vector3 groundCentre;
        private float groundRadius;

        private int hoveredAxis = -1;
        private int draggingAxis = -1;

        // Drag bookkeeping, captured on mouse down so the part never jumps to the cursor.
        private Vector3 dragStartTargetPosition;
        private Quaternion dragStartTargetRotation;
        private float dragStartAxisOffset;
        private float dragStartAngle;
        private Plane dragGroundPlane;
        private Vector3 dragStartGroundPoint;

        /// <summary>Raised when a drag finishes and the transform actually changed.</summary>
        public event Action TransformChanged;

        public bool IsDragging => draggingAxis >= 0;
        public bool HasTarget => target != null;

        public GizmoMode Mode
        {
            get => mode;
            set
            {
                if (mode == value)
                {
                    return;
                }

                mode = value;
                CancelDrag();
                RefreshVisibility();
            }
        }

        public void Initialise(Camera camera, Material handleMaterial)
        {
            targetCamera = camera;
            propertyBlock = new MaterialPropertyBlock();

            Mesh shaft = GizmoMeshes.CreateCylinder(ShaftRadius, ShaftLength);
            Mesh head = GizmoMeshes.CreateCone(HeadRadius, HeadLength);
            Mesh ring = GizmoMeshes.CreateTorus(RingRadius, RingTube);

            for (int i = 0; i < 3; i++)
            {
                translateRoots[i] = BuildArrow(i, shaft, head, handleMaterial, out translateRenderers[i]);
                rotateRoots[i] = BuildRing(i, ring, handleMaterial, out rotateRenderers[i]);
            }

            BuildGroundRing(handleMaterial);

            SetTarget(null);
            RefreshVisibility();
        }

        private Transform BuildArrow(int axis, Mesh shaft, Mesh head, Material material, out Renderer combined)
        {
            var root = new GameObject("Translate" + "XYZ"[axis]).transform;
            root.SetParent(transform, false);
            // Local +Y is the handle's own axis; rotate that onto the world axis it drives.
            root.localRotation = Quaternion.FromToRotation(Vector3.up, Axes[axis]);

            var shaftObject = new GameObject("Shaft");
            shaftObject.transform.SetParent(root, false);
            shaftObject.transform.localPosition = new Vector3(0.0f, ShaftLength * 0.5f, 0.0f);
            shaftObject.AddComponent<MeshFilter>().sharedMesh = shaft;
            Renderer shaftRenderer = shaftObject.AddComponent<MeshRenderer>();
            ConfigureRenderer(shaftRenderer, material);

            var headObject = new GameObject("Head");
            headObject.transform.SetParent(root, false);
            headObject.transform.localPosition = new Vector3(0.0f, ShaftLength, 0.0f);
            headObject.AddComponent<MeshFilter>().sharedMesh = head;
            Renderer headRenderer = headObject.AddComponent<MeshRenderer>();
            ConfigureRenderer(headRenderer, material);

            combined = shaftRenderer;
            SetColor(shaftRenderer, AxisColors[axis]);
            SetColor(headRenderer, AxisColors[axis]);
            return root;
        }

        private Transform BuildRing(int axis, Mesh ring, Material material, out Renderer renderer)
        {
            var root = new GameObject("Rotate" + "XYZ"[axis]).transform;
            root.SetParent(transform, false);
            // The torus lies in XZ (normal +Y); aim that normal down the axis being rotated about.
            root.localRotation = Quaternion.FromToRotation(Vector3.up, Axes[axis]);

            var ringObject = new GameObject("Ring");
            ringObject.transform.SetParent(root, false);
            ringObject.AddComponent<MeshFilter>().sharedMesh = ring;
            renderer = ringObject.AddComponent<MeshRenderer>();
            ConfigureRenderer(renderer, material);
            SetColor(renderer, AxisColors[axis]);
            return root;
        }

        private void BuildGroundRing(Material material)
        {
            groundRoot = new GameObject("TranslateXZ").transform;
            groundRoot.SetParent(transform, false);

            groundMesh = new Mesh { name = "GizmoGroundRing", hideFlags = HideFlags.DontSave };
            groundMesh.MarkDynamic();
            groundRoot.gameObject.AddComponent<MeshFilter>().sharedMesh = groundMesh;
            groundRenderer = groundRoot.gameObject.AddComponent<MeshRenderer>();
            ConfigureRenderer(groundRenderer, material);
            // Draw beneath the arrows where they overlap on screen.
            groundRenderer.sortingOrder = -1;
            SetColor(groundRenderer, GroundColor);
        }

        private static void ConfigureRenderer(Renderer renderer, Material material)
        {
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            renderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
        }

        private void SetColor(Renderer renderer, Color color)
        {
            propertyBlock ??= new MaterialPropertyBlock();
            renderer.GetPropertyBlock(propertyBlock);
            propertyBlock.SetColor("_BaseColor", color);
            renderer.SetPropertyBlock(propertyBlock);
        }

        public void SetTarget(Transform newTarget)
        {
            target = newTarget;
            targetRenderers = newTarget != null
                ? newTarget.GetComponentsInChildren<Renderer>(false)
                : Array.Empty<Renderer>();
            CancelDrag();
            hoveredAxis = -1;
            RefreshVisibility();
        }

        private void RefreshVisibility()
        {
            bool active = target != null;
            for (int i = 0; i < 3; i++)
            {
                if (translateRoots[i] != null)
                {
                    translateRoots[i].gameObject.SetActive(active && mode == GizmoMode.Translate);
                }

                if (rotateRoots[i] != null)
                {
                    rotateRoots[i].gameObject.SetActive(active && mode == GizmoMode.Rotate);
                }
            }

            if (groundRoot != null)
            {
                groundRoot.gameObject.SetActive(active && mode == GizmoMode.Translate);
            }
        }

        private void LateUpdate()
        {
            if (target == null || targetCamera == null)
            {
                return;
            }

            transform.position = target.position;
            // World-aligned handles. Local-space handles would be more capable, but on a shared
            // screen a fixed frame of reference is far easier for an audience to follow.
            transform.rotation = Quaternion.identity;
            float scale = ScreenConstantScale(target.position);
            transform.localScale = Vector3.one * scale;

            UpdateGroundRing(scale);
        }

        /// <summary>
        /// Lays the ground ring flat under the part's lowest point, wide enough to encircle its
        /// footprint. The ring hugs the bounds, so it is world-sized; only its width and minimum
        /// radius track screen size, which keeps it grabbable at any zoom.
        /// </summary>
        private void UpdateGroundRing(float scale)
        {
            if (groundRoot == null || mode != GizmoMode.Translate)
            {
                return;
            }

            Bounds? bounds = null;
            foreach (Renderer renderer in targetRenderers)
            {
                if (renderer == null || !renderer.enabled)
                {
                    continue;
                }

                if (bounds.HasValue)
                {
                    Bounds combined = bounds.Value;
                    combined.Encapsulate(renderer.bounds);
                    bounds = combined;
                }
                else
                {
                    bounds = renderer.bounds;
                }
            }

            float footprint = 0.0f;
            if (bounds.HasValue)
            {
                Bounds b = bounds.Value;
                groundCentre = new Vector3(b.center.x, b.min.y, b.center.z);
                footprint = new Vector2(b.extents.x, b.extents.z).magnitude * GroundFootprintPadding;
            }
            else
            {
                groundCentre = target.position;
            }

            groundRadius = Mathf.Max(footprint, GroundRingMinRadius * scale);

            // Child of the scaled gizmo, so express placement in gizmo-local units.
            groundRoot.localPosition = (groundCentre - transform.position) / scale;
            float localRadius = groundRadius / scale;
            if (Mathf.Abs(localRadius - groundMeshRadius) > groundMeshRadius * 0.01f)
            {
                groundMeshRadius = localRadius;
                GizmoMeshes.FillAnnulus(groundMesh, localRadius - GroundRingWidth * 0.5f,
                    localRadius + GroundRingWidth * 0.5f);
            }
        }

        /// <summary>Keeps the gizmo a constant size on screen regardless of distance or FOV.</summary>
        private float ScreenConstantScale(Vector3 worldPosition)
        {
            const float screenFraction = 0.16f;

            if (targetCamera.orthographic)
            {
                return targetCamera.orthographicSize * 2.0f * screenFraction;
            }

            float distance = Vector3.Distance(targetCamera.transform.position, worldPosition);
            float frustumHeight = 2.0f * distance * Mathf.Tan(targetCamera.fieldOfView * 0.5f * Mathf.Deg2Rad);
            return Mathf.Max(frustumHeight * screenFraction, 0.001f);
        }

        /// <summary>Updates handle highlighting for a hovering cursor. No-op while dragging.</summary>
        public void UpdateHover(Ray ray)
        {
            if (target == null || IsDragging)
            {
                return;
            }

            int axis = Pick(ray, out _, out _);
            if (axis == hoveredAxis)
            {
                return;
            }

            hoveredAxis = axis;
            ApplyColors();
        }

        private void ApplyColors()
        {
            int active = IsDragging ? draggingAxis : hoveredAxis;
            if (groundRenderer != null)
            {
                SetColor(groundRenderer, active == GroundHandle ? HighlightColor : GroundColor);
            }

            for (int i = 0; i < 3; i++)
            {
                Color color = i == active ? HighlightColor : AxisColors[i];
                if (mode == GizmoMode.Translate)
                {
                    foreach (Renderer renderer in translateRoots[i].GetComponentsInChildren<Renderer>(true))
                    {
                        SetColor(renderer, color);
                    }
                }
                else
                {
                    SetColor(rotateRenderers[i], color);
                }
            }
        }

        /// <summary>
        /// Attempts to grab a handle. Returns true when the gizmo has taken the drag, in which
        /// case the caller must not run its own selection raycast this frame.
        /// </summary>
        public bool TryBeginDrag(Ray ray)
        {
            if (target == null)
            {
                return false;
            }

            int axis = Pick(ray, out float axisOffset, out float angle);
            if (axis < 0)
            {
                return false;
            }

            if (axis == GroundHandle)
            {
                // Lock the drag plane at the height it was grabbed, so the part slides level.
                dragGroundPlane = new Plane(Vector3.up, groundCentre);
                if (!TryRaycastGround(ray, dragGroundPlane, out dragStartGroundPoint))
                {
                    return false;
                }
            }

            draggingAxis = axis;
            dragStartTargetPosition = target.position;
            dragStartTargetRotation = target.rotation;
            dragStartAxisOffset = axisOffset;
            dragStartAngle = angle;
            ApplyColors();
            return true;
        }

        public void UpdateDrag(Ray ray)
        {
            if (draggingAxis < 0 || target == null)
            {
                return;
            }

            if (draggingAxis == GroundHandle)
            {
                if (TryRaycastGround(ray, dragGroundPlane, out Vector3 point))
                {
                    Vector3 delta = point - dragStartGroundPoint;
                    delta.y = 0.0f;
                    target.position = dragStartTargetPosition + delta;
                }

                return;
            }

            Vector3 axis = Axes[draggingAxis];

            if (mode == GizmoMode.Translate)
            {
                if (TryClosestPointOnAxis(ray, dragStartTargetPosition, axis, out float offset))
                {
                    target.position = dragStartTargetPosition + axis * (offset - dragStartAxisOffset);
                }

                return;
            }

            if (TryAngleAroundAxis(ray, dragStartTargetPosition, axis, out float angle))
            {
                float delta = Mathf.DeltaAngle(dragStartAngle, angle);
                target.rotation = Quaternion.AngleAxis(delta, axis) * dragStartTargetRotation;
            }
        }

        public void EndDrag()
        {
            if (draggingAxis < 0)
            {
                return;
            }

            bool changed = target != null &&
                           (target.position != dragStartTargetPosition || target.rotation != dragStartTargetRotation);

            draggingAxis = -1;
            ApplyColors();

            if (changed)
            {
                TransformChanged?.Invoke();
            }
        }

        public void CancelDrag()
        {
            draggingAxis = -1;
            if (translateRoots[0] != null)
            {
                ApplyColors();
            }
        }

        /// <summary>Returns the axis index under the ray, or -1.</summary>
        private int Pick(Ray ray, out float axisOffset, out float angle)
        {
            axisOffset = 0.0f;
            angle = 0.0f;

            float scale = transform.localScale.x;
            Vector3 origin = transform.position;
            int best = -1;
            float bestDistance = float.MaxValue;

            for (int i = 0; i < 3; i++)
            {
                Vector3 axis = Axes[i];

                if (mode == GizmoMode.Translate)
                {
                    if (!TryClosestPointOnAxis(ray, origin, axis, out float offset))
                    {
                        continue;
                    }

                    // Only the drawn part of the shaft is grabbable.
                    float span = (ShaftLength + HeadLength) * scale;
                    if (offset < -HeadRadius * scale || offset > span)
                    {
                        continue;
                    }

                    Vector3 pointOnAxis = origin + axis * offset;
                    float distance = DistanceFromRay(ray, pointOnAxis);
                    if (distance < AxisPickTolerance * scale && distance < bestDistance)
                    {
                        bestDistance = distance;
                        best = i;
                        axisOffset = offset;
                    }
                }
                else
                {
                    if (!TryRingHit(ray, origin, axis, scale, out float distance, out float hitAngle))
                    {
                        continue;
                    }

                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        best = i;
                        angle = hitAngle;
                    }
                }
            }

            if (best >= 0 && mode == GizmoMode.Rotate)
            {
                TryAngleAroundAxis(ray, origin, Axes[best], out angle);
            }

            // Arrows win over the ground ring where the two overlap on screen.
            if (best < 0 && mode == GizmoMode.Translate && IsOverGroundRing(ray, scale))
            {
                best = GroundHandle;
            }

            return best;
        }

        private bool IsOverGroundRing(Ray ray, float scale)
        {
            if (groundRoot == null || groundRadius <= 0.0f)
            {
                return false;
            }

            if (!TryRaycastGround(ray, new Plane(Vector3.up, groundCentre), out Vector3 hit))
            {
                return false;
            }

            float radial = Vector3.Distance(hit, groundCentre);
            float halfBand = (GroundRingWidth * 0.5f + GroundRingPickTolerance) * scale;
            return Mathf.Abs(radial - groundRadius) <= halfBand;
        }

        private static bool TryRaycastGround(Ray ray, Plane plane, out Vector3 point)
        {
            point = Vector3.zero;
            // Near-horizontal rays meet the plane far away and send the part flying.
            if (Mathf.Abs(Vector3.Dot(ray.direction, plane.normal)) < 0.02f)
            {
                return false;
            }

            if (!plane.Raycast(ray, out float enter) || enter <= 0.0f)
            {
                return false;
            }

            point = ray.GetPoint(enter);
            return true;
        }

        private bool TryRingHit(Ray ray, Vector3 centre, Vector3 axis, float scale, out float distance, out float angle)
        {
            distance = float.MaxValue;
            angle = 0.0f;

            var plane = new Plane(axis, centre);
            if (!plane.Raycast(ray, out float enter) || enter <= 0.0f)
            {
                return false;
            }

            Vector3 hit = ray.GetPoint(enter);
            float radial = Vector3.Distance(hit, centre);
            float ringWorldRadius = RingRadius * scale;
            float error = Mathf.Abs(radial - ringWorldRadius);
            if (error > RingPickTolerance * scale)
            {
                return false;
            }

            distance = error;
            angle = AngleOnPlane(hit - centre, axis);
            return true;
        }

        private static bool TryClosestPointOnAxis(Ray ray, Vector3 origin, Vector3 axis, out float offset)
        {
            // Closest approach between the axis line and the mouse ray.
            Vector3 w0 = origin - ray.origin;
            float b = Vector3.Dot(axis, ray.direction);
            float denominator = 1.0f - b * b;
            if (Mathf.Abs(denominator) < 1e-5f)
            {
                // Looking straight down the axis; there is no stable answer.
                offset = 0.0f;
                return false;
            }

            float d = Vector3.Dot(axis, w0);
            float e = Vector3.Dot(ray.direction, w0);
            offset = (b * e - d) / denominator;
            return true;
        }

        private bool TryAngleAroundAxis(Ray ray, Vector3 centre, Vector3 axis, out float angle)
        {
            angle = 0.0f;
            var plane = new Plane(axis, centre);
            if (!plane.Raycast(ray, out float enter) || enter <= 0.0f)
            {
                return false;
            }

            angle = AngleOnPlane(ray.GetPoint(enter) - centre, axis);
            return true;
        }

        private static float AngleOnPlane(Vector3 vector, Vector3 axis)
        {
            Vector3 reference = Mathf.Abs(Vector3.Dot(axis, Vector3.up)) > 0.9f ? Vector3.forward : Vector3.up;
            Vector3 basisX = Vector3.Normalize(Vector3.Cross(reference, axis));
            Vector3 basisY = Vector3.Cross(axis, basisX);
            return Mathf.Atan2(Vector3.Dot(vector, basisY), Vector3.Dot(vector, basisX)) * Mathf.Rad2Deg;
        }

        private static float DistanceFromRay(Ray ray, Vector3 point)
        {
            Vector3 toPoint = point - ray.origin;
            float along = Vector3.Dot(toPoint, ray.direction);
            Vector3 closest = ray.origin + ray.direction * Mathf.Max(along, 0.0f);
            return Vector3.Distance(closest, point);
        }
    }
}
