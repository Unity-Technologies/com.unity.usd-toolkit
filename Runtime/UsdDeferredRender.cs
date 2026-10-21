using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Unity.USDToolkit
{
    // Runs work that renders (the dome and interior reflection captures) only after the scene has
    // drawn one ordinary frame with the import in it. Rendering a cubemap in the same frame the
    // import created thousands of renderers made URP's Surface Cache GI ingest them mid-change and
    // throw "An item with the same key has already been added" from its acceleration structure;
    // the renderers after the failing one never entered it, so the Factory's roof stopped
    // occluding the sky in GI and the hall was lit 7.6 x too bright under dome + sun. The deferred
    // call runs from an update, never from inside a render callback.
    internal static class UsdDeferredRender
    {
        static readonly List<Action> s_pending = new List<Action>();
        static bool s_hooked;
        static bool s_frameSeen;

        public static void AfterNextFrame(Action action)
        {
            if (action == null)
            {
                return;
            }

            s_pending.Add(action);
            if (!s_hooked)
            {
                s_hooked = true;
                s_frameSeen = false;
                RenderPipelineManager.endContextRendering += OnEndContextRendering;
#if UNITY_EDITOR
                if (!Application.isPlaying)
                {
                    UnityEditor.EditorApplication.update += EditorTick;
                    // Make sure a frame is drawn even in an idle editor.
                    UnityEditor.EditorApplication.QueuePlayerLoopUpdate();
                    UnityEditor.SceneView.RepaintAll();
                    return;
                }
#endif
                Runner.Ensure();
            }
        }

        static void OnEndContextRendering(ScriptableRenderContext context, List<Camera> cameras)
        {
            s_frameSeen = true;
            RenderPipelineManager.endContextRendering -= OnEndContextRendering;
        }

        // Called from an update once a frame has been drawn; runs everything queued.
        static void Flush()
        {
            if (!s_frameSeen)
            {
                return;
            }

            s_hooked = false;
#if UNITY_EDITOR
            UnityEditor.EditorApplication.update -= EditorTick;
#endif
            Action[] run = s_pending.ToArray();
            s_pending.Clear();
            foreach (Action action in run)
            {
                try
                {
                    action();
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception);
                }
            }
        }

#if UNITY_EDITOR
        static void EditorTick()
        {
            if (!s_frameSeen)
            {
                UnityEditor.EditorApplication.QueuePlayerLoopUpdate();
                return;
            }

            Flush();
        }
#endif

        // Player-side tick: a hidden object whose Update flushes once a frame has rendered.
        sealed class Runner : MonoBehaviour
        {
            static Runner s_instance;

            public static void Ensure()
            {
                if (s_instance != null)
                {
                    return;
                }

                var go = new GameObject("UsdDeferredRender") { hideFlags = HideFlags.HideAndDontSave };
                DontDestroyOnLoad(go);
                s_instance = go.AddComponent<Runner>();
            }

            void Update()
            {
                if (!s_hooked)
                {
                    Destroy(gameObject);
                    s_instance = null;
                    return;
                }

                Flush();
            }
        }
    }
}
