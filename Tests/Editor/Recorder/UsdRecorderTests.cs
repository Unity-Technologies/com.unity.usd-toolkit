using System;
using System.Collections;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using Unity.USDToolkit.Editor;
using UnityEditor.Recorder;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Unity.USDToolkit.Tests
{
    /// <summary>
    /// The "USD Animation" recorder driven by the Unity Recorder: a session, frame by frame, becomes
    /// an animated stage. The first test drives the Recorder's own session the way its frame
    /// component does, so it runs anywhere, batch mode and CI included; the last one goes through
    /// the RecorderController and the Game view as the Recorder window does, which needs an Editor
    /// with a window.
    /// </summary>
    public sealed class UsdRecorderTests
    {
        [Test]
        public void TheSettingsNameTheFileAfterTheFormat()
        {
            var settings = ScriptableObject.CreateInstance<UsdRecorderSettings>();
            try
            {
                settings.OutputFile = "/tmp/take";
                settings.Format = UsdRecorderFormat.Usda;
                Assert.That(settings.OutputFile, Does.EndWith("take"));
                Assert.That(settings.Format, Is.EqualTo(UsdRecorderFormat.Usda));
                Assert.That(settings.Input.GameObject, Is.Null);
                Assert.That(settings.IsPlatformSupported, Is.True);
            }
            finally
            {
                Object.DestroyImmediate(settings);
            }
        }

        [Test]
        public void ASessionIsWrittenFrameByFrame()
        {
            string directory = Path.Combine(Path.GetTempPath(), "com.unity.usd-toolkit-tests", Guid.NewGuid().ToString("N"));
            var root = new GameObject("Recorded");
            var mover = new GameObject("Mover");
            mover.transform.SetParent(root.transform, false);
            var blinker = GameObject.CreatePrimitive(PrimitiveType.Cube);
            blinker.name = "Blinker";
            blinker.transform.SetParent(root.transform, false);
            Object.DestroyImmediate(blinker.GetComponent<Collider>());

            var settings = ScriptableObject.CreateInstance<UsdRecorderSettings>();
            settings.name = "USD";
            settings.Enabled = true;
            settings.Input.GameObject = root;
            settings.Format = UsdRecorderFormat.Usda;
            settings.OutputFile = Path.Combine(directory, "take");
            settings.RecordMode = RecordMode.Manual;
            settings.FrameRatePlayback = FrameRatePlayback.Constant;
            settings.FrameRate = 30f;
            settings.CapFrameRate = false;

            RecordingSession session = null;
            UsdImportResult result = null;
            try
            {
                session = SessionDriver.Begin(settings);
                for (int frame = 0; frame < 10; frame++)
                {
                    mover.transform.localPosition = new Vector3(0.1f * frame, 0f, 0f);
                    blinker.GetComponent<MeshRenderer>().enabled = frame % 4 < 2;
                    SessionDriver.Frame(session);
                }

                SessionDriver.End(session);

                string path = Path.Combine(directory, "take.usda");
                Assert.That(File.Exists(path), Is.True, $"{path} was written");
                result = UsdImporter.Import(path, new UsdImportOptions());
                UsdAnimationPlayer player = result.Animation;
                Assert.That(player, Is.Not.Null, "the recording is animated");
                Assert.That(player.Data.TimeCodesPerSecond, Is.EqualTo(30.0));
                Assert.That(player.Data.StartTimeCode, Is.EqualTo(0.0));
                Assert.That(player.Data.EndTimeCode, Is.EqualTo(9.0), "one time code per recorded frame");
                Assert.That(player.Data.TransformTracks.Count, Is.EqualTo(1), "only the Mover moved");
                Assert.That(player.Data.VisibilityTracks.Count, Is.EqualTo(1), "the Blinker's renderer was switched");

                Transform moved = FindByName(result.RootObject.transform, "Mover");
                player.EvaluateAtTimeCode(9.0);
                Assert.That(moved.localPosition.x, Is.EqualTo(0.9f).Within(1e-5f), "the last frame's position");

                GameObject shown = FindVisibilityTarget(result.RootObject.transform, player.Data);
                for (int frame = 0; frame < 10; frame++)
                {
                    player.EvaluateAtTimeCode(frame);
                    Assert.That(shown.activeSelf, Is.EqualTo(frame % 4 < 2), $"the Blinker at frame {frame}");
                }
            }
            finally
            {
                if (result != null)
                {
                    if (result.Animation != null)
                    {
                        Object.DestroyImmediate(result.Animation.Data);
                    }

                    Object.DestroyImmediate(result.RootObject);
                }

                session?.Dispose();
                Object.DestroyImmediate(root);
                Object.DestroyImmediate(settings);
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, true);
                }
            }
        }

        // Overrides Only: a session recording an imported stage is a layer over its source file.
        [Test]
        public void AnOverridesOnlySessionIsALayerOverItsSource()
        {
            string directory = Path.Combine(Path.GetTempPath(), "com.unity.usd-toolkit-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string source = Path.Combine(directory, "rig.usda");
            File.WriteAllText(source, "#usda 1.0\n(\n    defaultPrim = \"Rig\"\n)\n\ndef Xform \"Rig\"\n{\n" +
                                      "    def Xform \"Mover\"\n    {\n        def Cube \"Box\"\n        {\n        }\n    }\n}\n");
            UsdImportResult import = UsdImporter.Import(source, new UsdImportOptions());
            var settings = ScriptableObject.CreateInstance<UsdRecorderSettings>();
            settings.name = "USD";
            settings.Enabled = true;
            settings.Input.GameObject = import.RootObject;
            settings.Format = UsdRecorderFormat.Usda;
            settings.OverridesOnly = true;
            settings.OutputFile = Path.Combine(directory, "take");
            settings.RecordMode = RecordMode.Manual;
            settings.FrameRatePlayback = FrameRatePlayback.Constant;
            settings.FrameRate = 30f;
            settings.CapFrameRate = false;

            RecordingSession session = null;
            UsdImportResult result = null;
            try
            {
                Transform mover = FindByName(import.RootObject.transform, "Mover");
                session = SessionDriver.Begin(settings);
                for (int frame = 0; frame < 10; frame++)
                {
                    mover.localPosition = new Vector3(0.1f * frame, 0f, 0f);
                    SessionDriver.Frame(session);
                }

                SessionDriver.End(session);

                string path = Path.Combine(directory, "take.usda");
                string text = File.ReadAllText(path);
                Assert.That(text, Does.Contain("@./rig.usda@"), "the source is sublayered");
                Assert.That(text, Does.Not.Contain("def Cube"), "and not copied");
                result = UsdImporter.Import(path, new UsdImportOptions());
                Assert.That(result.Animation, Is.Not.Null);
                Assert.That(result.Animation.Data.EndTimeCode, Is.EqualTo(9.0));
                result.Animation.EvaluateAtTimeCode(9.0);
                Assert.That(FindByName(result.RootObject.transform, "Mover").localPosition.x, Is.EqualTo(0.9f).Within(1e-5f));
                Assert.That(FindByName(result.RootObject.transform, "Box"), Is.Not.Null, "the box comes from the source");
            }
            finally
            {
                if (result != null)
                {
                    if (result.Animation != null)
                    {
                        Object.DestroyImmediate(result.Animation.Data);
                    }

                    Object.DestroyImmediate(result.RootObject);
                }

                session?.Dispose();
                Object.DestroyImmediate(import.RootObject);
                Object.DestroyImmediate(settings);
                Directory.Delete(directory, true);
            }
        }

        // Everything happens after EnterPlayMode: the domain reload there resets what came before.
        // The Recorder captures at the end of each rendered Game view frame, which batch mode has
        // none of, so this runs in an Editor with a window (Unity -runTests without -batchmode).
        [UnityTest]
        public IEnumerator APlayModeSessionIsWrittenAsAnimatedUsd()
        {
            if (Application.isBatchMode)
            {
                Assert.Ignore("The Recorder captures nothing in batch mode: there is no Game view frame to end.");
            }

            yield return new EnterPlayMode();

            string directory = Path.Combine(Path.GetTempPath(), "com.unity.usd-toolkit-tests", Guid.NewGuid().ToString("N"));
            var root = new GameObject("Recorded");
            var mover = new GameObject("Mover");
            mover.transform.SetParent(root.transform, false);

            var settings = ScriptableObject.CreateInstance<UsdRecorderSettings>();
            settings.name = "USD";
            settings.Enabled = true;
            settings.Input.GameObject = root;
            settings.Format = UsdRecorderFormat.Usda;
            settings.OutputFile = Path.Combine(directory, "take");

            var controllerSettings = ScriptableObject.CreateInstance<RecorderControllerSettings>();
            controllerSettings.AddRecorderSettings(settings);
            controllerSettings.SetRecordModeToManual();
            controllerSettings.FrameRatePlayback = FrameRatePlayback.Constant;
            controllerSettings.FrameRate = 30f;
            var controller = new RecorderController(controllerSettings);
            controller.PrepareRecording();
            Assert.That(controller.StartRecording(), Is.True, "the session starts");

            for (int frame = 0; frame < 12; frame++)
            {
                mover.transform.localPosition = new Vector3(0.1f * frame, 0f, 0f);
                yield return null;
            }

            controller.StopRecording();
            string path = Path.Combine(directory, "take.usda");
            try
            {
                Assert.That(File.Exists(path), Is.True, $"{path} was written");
                UsdImportResult result = UsdImporter.Import(path, new UsdImportOptions());
                UsdAnimationPlayer player = result.Animation;
                Assert.That(player, Is.Not.Null, "the recording is animated");
                Assert.That(player.Data.TimeCodesPerSecond, Is.EqualTo(30.0));
                Assert.That(player.Data.TransformTracks.Count, Is.EqualTo(1), "only the Mover moved");
                double end = player.Data.EndTimeCode;
                Assert.That(end, Is.GreaterThanOrEqualTo(5.0), "most frames were captured");
                Transform moved = FindByName(result.RootObject.transform, "Mover");
                player.EvaluateAtTimeCode(0.0);
                float first = moved.localPosition.x;
                player.EvaluateAtTimeCode(end);
                float last = moved.localPosition.x;
                Assert.That(last - first, Is.EqualTo(0.1f * (float)end).Within(1e-4f), "one time code per recorded frame");
                Object.Destroy(player.Data);
                Object.Destroy(result.RootObject);
            }
            finally
            {
                Object.Destroy(root);
                Object.DestroyImmediate(controllerSettings);
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, true);
                }
            }

            yield return new ExitPlayMode();
        }

        private static GameObject FindVisibilityTarget(Transform root, UsdAnimationData data)
        {
            Transform target = root.Find(data.VisibilityTracks[0].Path);
            Assert.That(target, Is.Not.Null, $"no object at {data.VisibilityTracks[0].Path}");
            Assert.That(target.name, Does.StartWith("Blinker"));
            return target.gameObject;
        }

        // The Recorder's frame component ends each frame with WaitForEndOfFrame on the Game view,
        // which batch mode never reaches. This makes the calls it makes, in its order, through the
        // Recorder's internal session API -- reflection, so a Recorder that renames them makes the
        // test inconclusive rather than failing the build.
        private static class SessionDriver
        {
            private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

            public static RecordingSession Begin(RecorderSettings settings)
            {
                Type inventory = typeof(RecordingSession).Assembly.GetType("UnityEditor.Recorder.RecordersInventory");
                MethodInfo create = inventory?.GetMethod("CreateDefaultRecorder", Any);
                if (create == null)
                {
                    Assert.Inconclusive("This version of the Recorder has no RecordersInventory.CreateDefaultRecorder.");
                }

                var session = new RecordingSession { recorder = (UnityEditor.Recorder.Recorder)create.Invoke(null, new object[] { settings }) };
                Assert.That(session.recorder, Is.Not.Null, "the Recorder knows the USD Animation recorder");
                Assert.That(Call(session, "SessionCreated"), Is.True, "the session is created");
                Assert.That(Call(session, "BeginRecording"), Is.True, "the recording begins");
                return session;
            }

            public static void Frame(RecordingSession session)
            {
                Call(session, "PrepareNewFrame");
                Call(session, "RecordFrame");
            }

            public static void End(RecordingSession session) => Call(session, "EndRecording");

            private static object Call(RecordingSession session, string name)
            {
                MethodInfo method = typeof(RecordingSession).GetMethod(name, Any, null, Type.EmptyTypes, null);
                if (method == null)
                {
                    Assert.Inconclusive($"This version of the Recorder has no RecordingSession.{name}().");
                }

                return method.Invoke(session, null);
            }
        }

        private static Transform FindByName(Transform root, string name)
        {
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t.name == name)
                {
                    return t;
                }
            }

            Assert.Fail($"No {name} below {root.name}.");
            return null;
        }
    }
}
