using UnityEditor;
using UnityEngine;

namespace Unity.USDToolkit.Samples.SimReadyCentral.Editor
{
    /// <summary>
    /// The Scene-view handles of every <see cref="SimReadyAsset"/> in the open scenes, drawn the
    /// way SimReady Central's Interactive USD preview draws them: an orange disc on the moving link,
    /// a leader line to "<c>Name · value</c>", and the value in a badge at the joint's origin.
    /// Dragging a disc moves the joint by the mouse's motion along where the joint moves the disc
    /// on screen; the coupled links follow. Edit mode poses kinematically, play mode drives.
    /// </summary>
    [InitializeOnLoad]
    public static class SimReadySceneGizmo
    {
        private const float DiscRadius = 9f;
        private static readonly Vector2 LabelOffset = new Vector2(46f, -42f);
        private static readonly int Hash = "SimReadyJointHandle".GetHashCode();

        private static SimReadyJointHandle dragHandle;
        private static float dragValue;
        private static Vector2 lastMouse;
        private static GUIStyle labelStyle;
        private static GUIStyle badgeStyle;
        private static Texture2D disc;

        static SimReadySceneGizmo()
        {
            SceneView.duringSceneGui += OnSceneGUI;
            // An asset a new load replaces in edit mode can be brought back with Undo.
            SimReadyAsset.EditModeDestroy = asset => Undo.DestroyObjectImmediate(asset);
        }

        private static void OnSceneGUI(SceneView view)
        {
            Event e = Event.current;
            foreach (SimReadyAsset asset in Object.FindObjectsByType<SimReadyAsset>())
            {
                foreach (SimReadyJointHandle handle in asset.Handles)
                {
                    if (handle.Joint != null && handle.HandleBody != null)
                    {
                        DoHandle(view, e, asset, handle);
                    }
                }
            }
        }

        private static void DoHandle(SceneView view, Event e, SimReadyAsset asset, SimReadyJointHandle handle)
        {
            int id = GUIUtility.GetControlID(Hash, FocusType.Passive);
            Vector3 world = handle.HandleWorld;
            if (Vector3.Dot(world - view.camera.transform.position, view.camera.transform.forward) <= 0f)
            {
                return; // behind the camera
            }

            Vector2 screen = HandleUtility.WorldToGUIPoint(world);
            Vector2 pivot = HandleUtility.WorldToGUIPoint(handle.Joint.PivotWorld);
            bool hot = GUIUtility.hotControl == id;
            bool hover = HandleUtility.nearestControl == id && GUIUtility.hotControl == 0;

            switch (e.GetTypeForControl(id))
            {
                case EventType.Layout:
                    HandleUtility.AddControl(id, Mathf.Max(0f, Vector2.Distance(e.mousePosition, screen) - DiscRadius));
                    break;

                case EventType.MouseDown:
                    if (e.button == 0 && HandleUtility.nearestControl == id && !e.alt)
                    {
                        GUIUtility.hotControl = id;
                        dragHandle = handle;
                        dragValue = asset.GetValue(handle);
                        lastMouse = e.mousePosition;
                        if (!Application.isPlaying)
                        {
                            Undo.RecordObjects(asset.GetComponentsInChildren<Transform>(), "Drag " + handle.Label);
                        }

                        asset.BeginDrag(handle);
                        e.Use();
                    }

                    break;

                case EventType.MouseDrag:
                    if (hot && dragHandle == handle)
                    {
                        float step = handle.Angular ? 1f : StepFor(handle);
                        Vector2 perUnit = SimReadyJointMath.ScreenPerUnit(p => HandleUtility.WorldToGUIPoint(p), world,
                                                                          handle.Joint.WorldMotionPerUnit(world), step);
                        float fallback = (handle.Max - handle.Min) / 400f;
                        dragValue += SimReadyJointMath.DragDelta(lastMouse, e.mousePosition, perUnit, -fallback);
                        dragValue = asset.SetValue(handle, dragValue);
                        lastMouse = e.mousePosition;
                        e.Use();
                        view.Repaint();
                    }

                    break;

                case EventType.MouseUp:
                    if (hot && e.button == 0)
                    {
                        GUIUtility.hotControl = 0;
                        asset.EndDrag(handle);
                        dragHandle = null;
                        e.Use();
                    }

                    break;

                case EventType.Repaint:
                    Draw(view, asset, handle, screen, pivot, hot, hover);
                    break;
            }
        }

        // A prismatic step that moves the handle about a centimetre, so the projection stays linear.
        private static float StepFor(SimReadyJointHandle handle)
        {
            float perUnit = handle.Joint.WorldMotionPerUnit(handle.HandleWorld).magnitude;
            return perUnit > 0f ? 0.01f / perUnit : 1f;
        }

        private static void Draw(SceneView view, SimReadyAsset asset, SimReadyJointHandle handle, Vector2 screen, Vector2 pivot, bool hot, bool hover)
        {
            EnsureStyles();
            double mpu = asset.Metadata.MetersPerUnit;
            float value = hot ? dragValue : asset.GetValue(handle);
            Color orange = SimReadyStyle.HandleColor;
            Color fill = hot ? SimReadyStyle.HandleActiveColor : hover ? SimReadyStyle.HandleHoverColor : orange;

            Handles.BeginGUI();
            Vector2 labelAnchor = screen + LabelOffset;
            Handles.color = orange;
            Handles.DrawAAPolyLine(2f, screen, labelAnchor);

            var discRect = new Rect(screen.x - DiscRadius, screen.y - DiscRadius, DiscRadius * 2f, DiscRadius * 2f);
            Color previous = GUI.color;
            GUI.color = fill;
            GUI.DrawTexture(discRect, disc);
            GUI.color = previous;

            var caption = new GUIContent(SimReadyJointMath.Caption(handle.Label, value, handle.Angular, mpu));
            Vector2 size = labelStyle.CalcSize(caption);
            GUI.Label(new Rect(labelAnchor.x, labelAnchor.y - (size.y * 0.5f), size.x, size.y), caption, labelStyle);

            var badge = new GUIContent(SimReadyJointMath.Badge(value, handle.Angular, mpu));
            Vector2 badgeSize = badgeStyle.CalcSize(badge);
            GUI.Label(new Rect(pivot.x - (badgeSize.x * 0.5f), pivot.y - (badgeSize.y * 0.5f), badgeSize.x, badgeSize.y), badge, badgeStyle);

            // The cursor says the disc can be dragged, and keeps saying so while it is.
            EditorGUIUtility.AddCursorRect(discRect, MouseCursor.Pan);
            if (hot)
            {
                EditorGUIUtility.AddCursorRect(new Rect(0f, 0f, view.position.width, view.position.height), MouseCursor.Pan);
            }

            Handles.EndGUI();
        }

        private static void EnsureStyles()
        {
            if (disc == null)
            {
                disc = SimReadyStyle.DiscTexture(32);
            }

            if (labelStyle == null)
            {
                labelStyle = new GUIStyle(EditorStyles.boldLabel)
                {
                    normal = { textColor = Color.white, background = SimReadyStyle.PanelTexture() },
                    padding = new RectOffset(6, 6, 3, 3),
                    fontSize = 11,
                };
            }

            if (badgeStyle == null)
            {
                badgeStyle = new GUIStyle(EditorStyles.miniBoldLabel)
                {
                    normal = { textColor = Color.white, background = SimReadyStyle.BadgeTexture() },
                    padding = new RectOffset(4, 4, 1, 1),
                    alignment = TextAnchor.MiddleCenter,
                };
            }
        }
    }
}
