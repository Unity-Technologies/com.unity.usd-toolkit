using System.Collections;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Unity.USDToolkit.Samples.SimReadyCentral.Tests
{
    /// <summary>
    /// Play mode, where a handle sets the leader's drive target and the physics moves the
    /// linkage: the fixture gripper at rest, driven, and seen through the runtime viewer.
    /// </summary>
    public sealed class SimReadyPlayModeTests
    {
        private GameObject root;
        private GameObject viewer;

        private static string Fixture(string name, [CallerFilePath] string file = "") =>
            Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file) ?? ".", "..", "Fixtures~", name));

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            Assume.That(UsdExporter.GetRuntimeInfo().NativeApiVersion, Is.GreaterThanOrEqualTo(13), "needs the physics import (API 13)");
            Physics.gravity = Vector3.zero;
            SimReadyAsset asset = SimReadyLoader.ImportLocal(Fixture("gripper.usda"));
            root = asset.gameObject;
            yield return new WaitForFixedUpdate();
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            Physics.gravity = new Vector3(0f, -9.81f, 0f);
            Object.Destroy(root);
            if (viewer != null)
            {
                Object.Destroy(viewer);
            }

            yield return null;
        }

        private static IEnumerator Seconds(float seconds)
        {
            for (float t = 0f; t < seconds; t += Time.fixedDeltaTime)
            {
                yield return new WaitForFixedUpdate();
            }
        }

        [UnityTest]
        public IEnumerator AtRestTheGripperHoldsItsAuthoredPose()
        {
            SimReadyAsset asset = root.GetComponent<SimReadyAsset>();
            yield return Seconds(1f);
            SimReadyJointHandle fingers = asset.Handles.Single();
            Assert.That(asset.GetValue(fingers), Is.EqualTo(30f).Within(0.5f));
            Assert.That(fingers.Followers[0].Follower.GetPosition(), Is.EqualTo(-30f).Within(0.5f));
        }

        [UnityTest]
        public IEnumerator AHandleDrivesTheLeaderAndTheFollowerFollows()
        {
            SimReadyAsset asset = root.GetComponent<SimReadyAsset>();
            SimReadyJointHandle fingers = asset.Handles.Single();
            asset.BeginDrag(fingers);
            asset.SetValue(fingers, 55f);
            asset.EndDrag(fingers);
            yield return Seconds(3f);
            Assert.That(asset.GetValue(fingers), Is.EqualTo(55f).Within(1.5f));
            Assert.That(fingers.Followers[0].Follower.GetPosition(), Is.EqualTo(-55f).Within(1.5f));

            asset.ResetToRest();
            yield return Seconds(2f);
            Assert.That(asset.GetValue(fingers), Is.EqualTo(30f).Within(1.5f));
        }

        [UnityTest]
        public IEnumerator ALoadReplacesTheAssetInTheSceneAndEveryViewFollows()
        {
            // The gripper from SetUp stands in for one imported in edit mode; the viewer shows it.
            viewer = new GameObject("SimReady viewer");
            var document = viewer.AddComponent<UIDocument>();
            document.panelSettings = ScriptableObject.CreateInstance<PanelSettings>();
            viewer.AddComponent<SimReadyViewer>();
            yield return null;
            SimReadyGizmoElement gizmo = document.rootVisualElement.Q<SimReadyGizmoElement>();
            Assert.That(gizmo.Asset, Is.EqualTo(root.GetComponent<SimReadyAsset>()));

            SimReadyAsset previous = root.GetComponent<SimReadyAsset>();
            SimReadyAsset next = SimReadyLoader.ImportLocal(Fixture("cabinet.usda"));
            Assert.That(previous.gameObject.activeSelf, Is.False, "the old asset steps aside at once, before the physics runs");
            yield return null;
            yield return new WaitForFixedUpdate();

            Assert.That(previous == null, Is.True, "the old asset is gone");
            Assert.That(Object.FindObjectsByType<SimReadyAsset>(), Is.EqualTo(new[] { next }));
            Assert.That(gizmo.Asset, Is.EqualTo(next), "the runtime viewer follows the load it did not make");
            Assert.That(GameObject.Find("SimReady Ground"), Is.Not.Null, "the cabinet stands on a floor");
            root = next.gameObject;

            SimReadyLoader.ImportLocal(Fixture("gripper.usda"));
            yield return null;
            Assert.That(GameObject.Find("SimReady Ground"), Is.Null, "the cabinet's floor went with it; the gripper is fixed to the world");
            root = Object.FindAnyObjectByType<SimReadyAsset>().gameObject;
        }

        [UnityTest]
        public IEnumerator TheRuntimeViewerShowsTheHandles()
        {
            var cameraObject = new GameObject("Viewer camera", typeof(Camera), typeof(SimReadyOrbitCamera), typeof(SimReadyStageDecor));
            viewer = new GameObject("SimReady viewer");
            cameraObject.transform.SetParent(viewer.transform);
            var document = viewer.AddComponent<UIDocument>();
            document.panelSettings = ScriptableObject.CreateInstance<PanelSettings>();
            SimReadyViewer view = viewer.AddComponent<SimReadyViewer>();
            view.OrbitCamera = cameraObject.GetComponent<SimReadyOrbitCamera>();
            view.StageDecor = cameraObject.GetComponent<SimReadyStageDecor>();
            yield return null;
            yield return null;

            SimReadyGizmoElement gizmo = document.rootVisualElement.Q<SimReadyGizmoElement>();
            Assert.That(gizmo, Is.Not.Null);
            Assert.That(gizmo.Asset, Is.EqualTo(root.GetComponent<SimReadyAsset>()), "an asset already in the scene is picked up");
            Label caption = gizmo.Query<Label>(className: "simready-caption").First();
            Assert.That(caption, Is.Not.Null);
            Assert.That(caption.text, Does.StartWith("Fingers · "));

            // The camera frames the asset on load: the handle is in front of it.
            Camera camera = view.OrbitCamera.Camera;
            Vector3 handle = root.GetComponent<SimReadyAsset>().Handles[0].HandleWorld;
            Vector3 viewport = camera.WorldToViewportPoint(handle);
            Assert.That(viewport.z, Is.GreaterThan(0f));
            Assert.That(viewport.x, Is.InRange(0f, 1f));
            Assert.That(viewport.y, Is.InRange(0f, 1f));
        }

        [UnityTest]
        public IEnumerator KinematicPosesTheLinksDirectlyAndDynamicHandsBackToThePhysics()
        {
            Physics.gravity = new Vector3(0f, -9.81f, 0f);
            SimReadyAsset asset = root.GetComponent<SimReadyAsset>();
            SimReadyJointHandle fingers = asset.Handles.Single();
            UsdPhysicsJoint follower = fingers.Followers[0].Follower;

            asset.Kinematic = true;
            Assert.That(root.GetComponentsInChildren<ArticulationBody>(true).All(b => !b.enabled), "the articulation is off");
            Assert.That(root.GetComponentsInChildren<UsdPhysicsMimicJoint>(true).All(m => !m.enabled), "the mimic drives are off");
            Assert.That(asset.Simulating, Is.False);

            // At once, without a physics step: the leader and its follower are posed.
            asset.SetValue(fingers, 55f);
            Assert.That(fingers.Joint.MeasurePosition(), Is.EqualTo(55f).Within(0.1f));
            Assert.That(follower.MeasurePosition(), Is.EqualTo(-55f).Within(0.1f));
            Vector3 handle = fingers.HandleWorld;
            yield return Seconds(1f);
            Assert.That(Vector3.Distance(fingers.HandleWorld, handle), Is.LessThan(1e-5f), "nothing moves under gravity");
            Assert.That(asset.GetValue(fingers), Is.EqualTo(55f).Within(0.1f));

            // Back to the physics: the drive holds the handle where kinematic mode left it.
            asset.Kinematic = false;
            Assert.That(root.GetComponentsInChildren<ArticulationBody>(true).All(b => b.enabled));
            yield return Seconds(2f);
            Assert.That(asset.GetValue(fingers), Is.EqualTo(55f).Within(1.5f));
            Assert.That(follower.GetPosition(), Is.EqualTo(-55f).Within(1.5f));
        }

        [UnityTest]
        public IEnumerator AKinematicPropStandsWithoutAFloor()
        {
            SimReadyAsset.KinematicByDefault = true;
            try
            {
                Physics.gravity = new Vector3(0f, -9.81f, 0f);
                SimReadyAsset cabinet = SimReadyLoader.ImportLocal(Fixture("cabinet.usda"));
                root = cabinet.gameObject;
                Assert.That(cabinet.Kinematic, Is.True, "a load takes the remembered mode");
                Vector3 start = cabinet.GetComponentsInChildren<Rigidbody>().First().position;
                yield return null;
                yield return Seconds(1f);

                Assert.That(GameObject.Find("SimReady Ground"), Is.Null, "a kinematic asset needs no floor");
                Assert.That(cabinet.GetComponentsInChildren<Rigidbody>().All(b => b.isKinematic));
                Assert.That(Vector3.Distance(cabinet.GetComponentsInChildren<Rigidbody>().First().position, start), Is.LessThan(1e-4f));

                SimReadyJointHandle door = cabinet.Handles.First(h => h.Angular);
                cabinet.SetValue(door, 60f);
                Assert.That(door.Joint.MeasurePosition(), Is.EqualTo(60f).Within(0.1f));
                cabinet.ResetToRest();
                Assert.That(door.Joint.MeasurePosition(), Is.EqualTo(door.RestValue).Within(0.1f));
            }
            finally
            {
                SimReadyAsset.KinematicByDefault = false;
            }
        }
    }
}
