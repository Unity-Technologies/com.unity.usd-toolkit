using System.Collections;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using Unity.USDToolkit.Samples.SimReadyCentral.Editor;

namespace Unity.USDToolkit.Samples.SimReadyCentral.Tests
{
    /// <summary>The Editor window across play mode: it never keeps showing an asset that is gone.</summary>
    public sealed class SimReadyCentralWindowTests
    {
        [UnityTest]
        public IEnumerator LeavingPlayModeDropsAnAssetImportedThere()
        {
            // An Editor window cannot open without a graphics device (-nographics, CI).
            Assume.That(SystemInfo.graphicsDeviceType, Is.Not.EqualTo(UnityEngine.Rendering.GraphicsDeviceType.Null),
                        "needs a graphics device: run without -nographics");
            var window = EditorWindow.GetWindow<SimReadyCentralWindow>();
            for (int i = 0; i < 5; i++) yield return null;
            yield return new EnterPlayMode();
            SimReadyAsset runtime = SimReadyLoader.ImportLocal(SimReadyFixtures.PathOf("gripper.usda"));
            for (int i = 0; i < 30; i++) yield return null;
            window = Resources.FindObjectsOfTypeAll<SimReadyCentralWindow>()[0];
            yield return new ExitPlayMode();
            window = Resources.FindObjectsOfTypeAll<SimReadyCentralWindow>()[0];
            for (int i = 0; i < 60; i++) { yield return null; window.Repaint(); }
            object shown = typeof(SimReadyCentralWindow).GetField("shown", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(window);
            Assert.That(shown, Is.Null, "the window no longer shows the asset that only existed in play mode");
            window.Close();
        }

        private static string Shown(SimReadyCentralWindow window)
        {
            var o = (Object)typeof(SimReadyCentralWindow).GetField("shown", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(window);
            return o == null ? "none" : o.name;
        }
    }
}
