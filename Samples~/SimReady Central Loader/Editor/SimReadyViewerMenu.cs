using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Unity.USDToolkit.Samples.SimReadyCentral.Editor
{
    /// <summary>
    /// <b>GameObject &gt; SimReady Central &gt; Viewer</b>: sets up the runtime viewer in the open
    /// scene, as SimReadyCentral.unity has it. The main camera (made if there is none) gets the orbit
    /// controls and the floor; a light is added if the scene has none; and an object with a
    /// UIDocument (the sample's Panel Settings) and <see cref="SimReadyViewer"/> carries the panel.
    /// </summary>
    public static class SimReadyViewerMenu
    {
        [MenuItem("GameObject/SimReady Central/Viewer", false, 10)]
        public static void CreateViewer()
        {
            PanelSettings settings = SimReadyViewer.FindPanelSettings();
            if (settings == null)
            {
                EditorUtility.DisplayDialog("SimReady Central", "The sample's UI/SimReadyPanelSettings asset was not found. Import the " +
                                            "SimReady Central Loader sample from the Package Manager first.", "OK");
                return;
            }

            Undo.SetCurrentGroupName("Create SimReady Viewer");
            int group = Undo.GetCurrentGroup();

            Camera camera = Camera.main != null ? Camera.main : Object.FindAnyObjectByType<Camera>();
            if (camera == null)
            {
                var cameraObject = new GameObject("Main Camera", typeof(Camera)) { tag = "MainCamera" };
                Undo.RegisterCreatedObjectUndo(cameraObject, "Create camera");
                camera = cameraObject.GetComponent<Camera>();
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(0.16f, 0.17f, 0.19f);
                camera.fieldOfView = 35f;
                camera.nearClipPlane = 0.001f;
                camera.farClipPlane = 1000f;
            }

            SimReadyOrbitCamera orbit = camera.GetComponent<SimReadyOrbitCamera>();
            if (orbit == null)
            {
                orbit = Undo.AddComponent<SimReadyOrbitCamera>(camera.gameObject);
            }

            SimReadyStageDecor decor = camera.GetComponent<SimReadyStageDecor>();
            if (decor == null)
            {
                decor = Undo.AddComponent<SimReadyStageDecor>(camera.gameObject);
            }

            if (Object.FindAnyObjectByType<Light>() == null)
            {
                var lightObject = new GameObject("Directional Light", typeof(Light));
                Undo.RegisterCreatedObjectUndo(lightObject, "Create light");
                Light light = lightObject.GetComponent<Light>();
                light.type = LightType.Directional;
                light.shadows = LightShadows.Soft;
                lightObject.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
            }

            SimReadyViewer viewer = Object.FindAnyObjectByType<SimReadyViewer>();
            if (viewer == null)
            {
                var viewerObject = new GameObject("SimReady Viewer", typeof(UIDocument));
                Undo.RegisterCreatedObjectUndo(viewerObject, "Create viewer");
                viewerObject.GetComponent<UIDocument>().panelSettings = settings;
                viewer = viewerObject.AddComponent<SimReadyViewer>();
            }
            else if (viewer.GetComponent<UIDocument>().panelSettings == null)
            {
                Undo.RecordObject(viewer.GetComponent<UIDocument>(), "Assign panel settings");
                viewer.GetComponent<UIDocument>().panelSettings = settings;
            }

            Undo.RecordObject(viewer, "Wire viewer");
            viewer.OrbitCamera = orbit;
            viewer.StageDecor = decor;
            Undo.CollapseUndoOperations(group);
            Selection.activeGameObject = viewer.gameObject;
        }
    }
}
