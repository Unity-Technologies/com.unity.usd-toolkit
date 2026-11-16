using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace Unity.USDToolkit.Samples.SimReadyCentral
{
    /// <summary>
    /// The runtime handles (Unity has no runtime transform gizmo): a UI Toolkit overlay over the
    /// viewport that draws each <see cref="SimReadyJointHandle"/> as SimReady Central's preview
    /// does (orange disc on the moving link, leader line, "Name · value" label, value badge at the
    /// joint origin) and turns pointer drags into joint values. A drag that starts off a handle
    /// orbits (left button), pans (right or middle button, or Shift) or, with the wheel, zooms
    /// the <see cref="SimReadyOrbitCamera"/>.
    /// </summary>
    public sealed class SimReadyGizmoElement : VisualElement
    {
        private const float DiscRadius = 9f;
        private const float HitRadius = 14f;
        private static readonly Vector2 LabelOffset = new Vector2(46f, -42f);

        private readonly List<Label> captions = new List<Label>();
        private readonly List<Label> badges = new List<Label>();
        private readonly Texture2D hoverCursor = SimReadyStyle.CursorTexture(false);
        private readonly Texture2D grabCursor = SimReadyStyle.CursorTexture(true);

        private SimReadyJointHandle active;
        private SimReadyJointHandle hovered;
        private float dragValue;
        private Vector2 lastPointer;
        private int cameraButton = -1;

        public SimReadyGizmoElement()
        {
            style.position = Position.Absolute;
            style.left = style.top = style.right = style.bottom = 0f;
            pickingMode = PickingMode.Position;
            focusable = true;
            generateVisualContent += Paint;
            RegisterCallback<PointerDownEvent>(OnPointerDown);
            RegisterCallback<PointerMoveEvent>(OnPointerMove);
            RegisterCallback<PointerUpEvent>(OnPointerUp);
            RegisterCallback<WheelEvent>(OnWheel);
            RegisterCallback<PointerLeaveEvent>(_ => SetCursor(null));
        }

        public SimReadyAsset Asset { get; set; }
        public SimReadyOrbitCamera OrbitCamera { get; set; }

        /// <summary>Called every frame by the viewer: moves the labels and repaints.</summary>
        public void Refresh()
        {
            int count = Asset != null ? Asset.Handles.Count : 0;
            while (captions.Count < count)
            {
                captions.Add(MakeLabel("simready-caption", SimReadyStyle.PanelColor));
                badges.Add(MakeLabel("simready-badge", SimReadyStyle.BadgeColor));
            }

            for (int i = 0; i < captions.Count; i++)
            {
                bool visible = i < count && TryScreen(Asset.Handles[i], out Vector2 _, out Vector2 _);
                captions[i].style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
                badges[i].style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
                if (!visible)
                {
                    continue;
                }

                SimReadyJointHandle handle = Asset.Handles[i];
                TryScreen(handle, out Vector2 at, out Vector2 pivot);
                float value = handle == active ? dragValue : Asset.GetValue(handle);
                double mpu = Asset.Metadata.MetersPerUnit;
                captions[i].text = SimReadyJointMath.Caption(handle.Label, value, handle.Angular, mpu);
                captions[i].style.left = at.x + LabelOffset.x;
                captions[i].style.top = at.y + LabelOffset.y - 10f;
                badges[i].text = SimReadyJointMath.Badge(value, handle.Angular, mpu);
                badges[i].style.left = pivot.x - 14f;
                badges[i].style.top = pivot.y - 9f;
            }

            MarkDirtyRepaint();
        }

        private Label MakeLabel(string className, Color background)
        {
            var label = new Label { pickingMode = PickingMode.Ignore };
            label.AddToClassList(className);
            label.style.position = Position.Absolute;
            label.style.backgroundColor = background;
            label.style.color = Color.white;
            label.style.unityFontStyleAndWeight = FontStyle.Bold;
            label.style.fontSize = 12;
            label.style.paddingLeft = label.style.paddingRight = 6f;
            label.style.paddingTop = label.style.paddingBottom = 2f;
            label.style.borderTopLeftRadius = label.style.borderTopRightRadius = 4f;
            label.style.borderBottomLeftRadius = label.style.borderBottomRightRadius = 4f;
            Add(label);
            return label;
        }

        // Handle and pivot in this element's coordinates; false when behind the camera.
        private bool TryScreen(SimReadyJointHandle handle, out Vector2 at, out Vector2 pivot)
        {
            at = pivot = Vector2.zero;
            Camera camera = OrbitCamera != null ? OrbitCamera.Camera : Camera.main;
            if (camera == null || handle?.Joint == null || handle.HandleBody == null || panel == null)
            {
                return false;
            }

            Vector3 world = handle.HandleWorld;
            if (Vector3.Dot(world - camera.transform.position, camera.transform.forward) <= 0f)
            {
                return false;
            }

            at = ToLocal(camera, world);
            pivot = ToLocal(camera, handle.Joint.PivotWorld);
            return true;
        }

        // Through the camera's viewport (0-1) to the panel's own size, not through screen pixels:
        // the screen and the camera disagree when the Game view is scaled or set to a fixed
        // resolution, on a Retina display, or when the camera renders to a texture, and the
        // handle then lands off the panel while the badge at the pivot may still show.
        private Vector2 ToLocal(Camera camera, Vector3 world)
        {
            // Pixels of the camera's render target (its rect offset included), normalised by the
            // target's size, which the camera's pixel rect and rect give without asking Screen.
            Vector3 pixel = camera.WorldToScreenPoint(world);
            Rect pixels = camera.pixelRect;
            Rect rect = camera.rect;
            float targetWidth = rect.width > 0f ? pixels.width / rect.width : pixels.width;
            float targetHeight = rect.height > 0f ? pixels.height / rect.height : pixels.height;
            Vector2 size = panel.visualTree.layout.size;
            var onPanel = new Vector2(pixel.x / Mathf.Max(targetWidth, 1f) * size.x,
                                      (1f - (pixel.y / Mathf.Max(targetHeight, 1f))) * size.y);
            return this.WorldToLocal(onPanel);
        }

        private void Paint(MeshGenerationContext context)
        {
            if (Asset == null)
            {
                return;
            }

            Painter2D painter = context.painter2D;
            foreach (SimReadyJointHandle handle in Asset.Handles)
            {
                if (!TryScreen(handle, out Vector2 at, out Vector2 _))
                {
                    continue;
                }

                painter.strokeColor = SimReadyStyle.HandleColor;
                painter.lineWidth = 2f;
                painter.BeginPath();
                painter.MoveTo(at);
                painter.LineTo(at + LabelOffset);
                painter.Stroke();

                painter.fillColor = handle == active ? SimReadyStyle.HandleActiveColor : handle == hovered ? SimReadyStyle.HandleHoverColor : SimReadyStyle.HandleColor;
                painter.BeginPath();
                painter.Arc(at, DiscRadius, 0f, 360f);
                painter.Fill();
                painter.strokeColor = Color.white;
                painter.lineWidth = 1.5f;
                painter.Stroke();
            }
        }

        private SimReadyJointHandle HandleAt(Vector2 position)
        {
            SimReadyJointHandle best = null;
            float bestDistance = HitRadius;
            if (Asset == null)
            {
                return null;
            }

            foreach (SimReadyJointHandle handle in Asset.Handles)
            {
                if (TryScreen(handle, out Vector2 at, out Vector2 _))
                {
                    float distance = Vector2.Distance(position, at);
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        best = handle;
                    }
                }
            }

            return best;
        }

        private void OnPointerDown(PointerDownEvent e)
        {
            Vector2 position = e.localPosition;
            SimReadyJointHandle handle = e.button == 0 && !e.shiftKey ? HandleAt(position) : null;
            lastPointer = position;
            if (handle != null)
            {
                active = handle;
                dragValue = Asset.GetValue(handle);
                Asset.BeginDrag(handle);
                SetCursor(grabCursor);
            }
            else
            {
                cameraButton = e.shiftKey ? 2 : e.button;
            }

            this.CapturePointer(e.pointerId);
            e.StopPropagation();
        }

        private void OnPointerMove(PointerMoveEvent e)
        {
            Vector2 position = e.localPosition;
            Vector2 delta = position - lastPointer;
            if (active != null && (Asset == null || active.Joint == null))
            {
                active = null; // the asset was replaced or destroyed mid-drag
            }

            if (active != null)
            {
                Camera camera = OrbitCamera != null ? OrbitCamera.Camera : Camera.main;
                Vector3 world = active.HandleWorld;
                Vector3 motion = active.Joint.WorldMotionPerUnit(world);
                float step = active.Angular ? 1f : (motion.magnitude > 0f ? 0.01f / motion.magnitude : 1f);
                Vector2 perUnit = SimReadyJointMath.ScreenPerUnit(p => ToLocal(camera, p), world, motion, step);
                dragValue += SimReadyJointMath.DragDelta(lastPointer, position, perUnit, -(active.Max - active.Min) / 400f);
                dragValue = Asset.SetValue(active, dragValue);
            }
            else if (cameraButton >= 0 && OrbitCamera != null)
            {
                if (cameraButton == 0)
                {
                    OrbitCamera.Orbit(delta);
                }
                else
                {
                    OrbitCamera.Pan(delta);
                }
            }
            else
            {
                SimReadyJointHandle over = HandleAt(position);
                if (over != hovered)
                {
                    hovered = over;
                    SetCursor(over != null ? hoverCursor : null);
                }
            }

            lastPointer = position;
        }

        private void OnPointerUp(PointerUpEvent e)
        {
            if (active != null && Asset != null)
            {
                Asset.EndDrag(active);
            }

            active = null;

            cameraButton = -1;
            this.ReleasePointer(e.pointerId);
            SetCursor(HandleAt(e.localPosition) != null ? hoverCursor : null);
        }

        private void OnWheel(WheelEvent e)
        {
            if (OrbitCamera != null)
            {
                OrbitCamera.Zoom(e.delta.y > 0f ? 1f : -1f);
            }

            e.StopPropagation();
        }

        private static void SetCursor(Texture2D texture) =>
            UnityEngine.Cursor.SetCursor(texture, texture != null ? new Vector2(16f, 16f) : Vector2.zero, CursorMode.Auto);
    }
}
