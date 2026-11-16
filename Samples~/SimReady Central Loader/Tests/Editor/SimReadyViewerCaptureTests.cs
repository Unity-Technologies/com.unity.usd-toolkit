using System.Collections;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Unity.USDToolkit.Samples.SimReadyCentral.Tests
{
    [Explicit]
    /// <summary>
    /// Renders the runtime viewer (the camera and the UI panel) to PNGs, to look at what it draws.
    /// Needs a graphics device, so it runs only when asked; SIMREADY_CAPTURE names the folder.
    /// </summary>
    public sealed class SimReadyViewerCaptureTests
    {
        [UnityTest]
        public IEnumerator CaptureViewer() => Capture(true, "viewer");

        [UnityTest]
        public IEnumerator CaptureEmptyViewer() => Capture(false, "viewer_empty");

        private static IEnumerator Capture(bool withAsset, string name)
        {
            // Without a graphics device (-nographics, CI) rendering an offscreen camera crashes the Editor.
            Assume.That(SystemInfo.graphicsDeviceType, Is.Not.EqualTo(UnityEngine.Rendering.GraphicsDeviceType.Null),
                        "needs a graphics device: run without -nographics");
            yield return new EnterPlayMode();
            Physics.gravity = Vector3.zero;
            SimReadyAsset asset = withAsset ? SimReadyLoader.ImportLocal(SimReadyFixtures.PathOf("gripper.usda")) : null;

            var rtScene = new RenderTexture(1600, 900, 24);
            var rtUi = new RenderTexture(1600, 900, 0);
            var cameraObject = new GameObject("Camera", typeof(Camera), typeof(SimReadyOrbitCamera), typeof(SimReadyStageDecor));
            Camera camera = cameraObject.GetComponent<Camera>();
            camera.targetTexture = rtScene;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.16f, 0.17f, 0.19f);
            camera.fieldOfView = 35f;
            new GameObject("Light", typeof(Light)).GetComponent<Light>().type = LightType.Directional;

            string guid = AssetDatabase.FindAssets("SimReadyPanelSettings t:PanelSettings").First();
            var settings = Object.Instantiate(AssetDatabase.LoadAssetAtPath<PanelSettings>(AssetDatabase.GUIDToAssetPath(guid)));
            settings.targetTexture = rtUi;
            settings.clearColor = true;
            settings.colorClearValue = Color.clear;
            var viewerObject = new GameObject("Viewer");
            viewerObject.SetActive(false);
            viewerObject.AddComponent<UIDocument>().panelSettings = settings;
            SimReadyViewer viewer = viewerObject.AddComponent<SimReadyViewer>();
            viewer.OrbitCamera = cameraObject.GetComponent<SimReadyOrbitCamera>();
            viewer.StageDecor = cameraObject.GetComponent<SimReadyStageDecor>();
            viewerObject.SetActive(true);
            for (int i = 0; i < 20; i++) yield return null;

            string folder = System.Environment.GetEnvironmentVariable("SIMREADY_CAPTURE") ?? Application.temporaryCachePath;
            Save(rtScene, Path.Combine(folder, name + "_scene.png"));
            Save(rtUi, Path.Combine(folder, name + "_ui.png"));
            if (asset == null)
            {
                yield return new ExitPlayMode();
                yield break;
            }

            var gizmo = viewerObject.GetComponent<UIDocument>().rootVisualElement.Q<SimReadyGizmoElement>();
            SimReadyJointHandle h = asset.Handles[0];
            Vector3 hw = h.HandleWorld, pw = h.Joint.PivotWorld;
            var panel = gizmo.panel;
            Debug.Log($"CAPTURE handleBody={h.HandleBody?.name} handleLocal={h.HandleLocal:F4} handleWorld={hw:F4} pivotWorld={pw:F4} " +
                      $"handlePanel={RuntimePanelUtils.CameraTransformWorldToPanel(panel, hw, camera)} pivotPanel={RuntimePanelUtils.CameraTransformWorldToPanel(panel, pw, camera)} " +
                      $"handleViewport={camera.WorldToViewportPoint(hw):F3} pivotViewport={camera.WorldToViewportPoint(pw):F3} screen={Screen.width}x{Screen.height}");
            Debug.Log($"CAPTURE gizmo layout={gizmo.layout} worldBound={gizmo.worldBound} handles={asset.Handles.Count} camera={camera.transform.position} panel scale={settings.scale}");
            yield return new ExitPlayMode();
        }

        private static void Save(RenderTexture rt, string path)
        {
            RenderTexture.active = rt;
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            tex.Apply();
            RenderTexture.active = null;
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Debug.Log("CAPTURE wrote " + path);
        }
    }
}
