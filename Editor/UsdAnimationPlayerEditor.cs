using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Unity.USDToolkit.Editor
{
    /// <summary>
    /// Inspector for <see cref="UsdAnimationPlayer"/>: what the animation holds, play, pause, stop
    /// and a time slider in Play Mode, and in Edit Mode a <see cref="UsdAnimationPreview"/> that
    /// scrubs the stage without changing the scene. The preview ends when Preview is clicked again,
    /// the selection changes, the scene is saved or Play Mode starts.
    /// </summary>
    [CustomEditor(typeof(UsdAnimationPlayer))]
    public sealed class UsdAnimationPlayerEditor : UnityEditor.Editor
    {
        private UsdAnimationPreview preview;

        private bool Previewing => preview != null && preview.IsActive;

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            DrawPropertiesExcluding(serializedObject, "m_Script", "time");
            serializedObject.ApplyModifiedProperties();

            var player = (UsdAnimationPlayer)target;
            UsdAnimationData data = player.Data;
            if (data == null)
            {
                return;
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Length", $"{data.Duration:0.###} s (time codes {data.StartTimeCode:0.##} to {data.EndTimeCode:0.##} at {data.TimeCodesPerSecond:0.##} per second)");
            EditorGUILayout.LabelField("Tracks",
                $"{data.TransformTracks.Count} transform, {data.VisibilityTracks.Count} visibility, " +
                $"{data.SkeletonTracks.Count} skeleton, {data.BlendShapeTracks.Count} blend shape, " +
                $"{data.PointCacheTracks.Count} point cache");

            if (Application.isPlaying)
            {
                DrawPlayModeControls(player, data);
            }
            else
            {
                DrawPreview(player, data);
            }
        }

        public override bool RequiresConstantRepaint() =>
            Application.isPlaying && target is UsdAnimationPlayer player && player.IsPlaying;

        private void OnEnable()
        {
            EditorSceneManager.sceneSaving += OnSceneSaving;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            AssemblyReloadEvents.beforeAssemblyReload += StopPreview;
        }

        private void OnDisable()
        {
            EditorSceneManager.sceneSaving -= OnSceneSaving;
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            AssemblyReloadEvents.beforeAssemblyReload -= StopPreview;
            StopPreview();
        }

        private static void DrawPlayModeControls(UsdAnimationPlayer player, UsdAnimationData data)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(player.IsPlaying ? "Pause" : "Play"))
                {
                    if (player.IsPlaying)
                    {
                        player.Pause();
                    }
                    else
                    {
                        player.Play();
                    }
                }

                if (GUILayout.Button("Stop"))
                {
                    player.Stop();
                }
            }

            EditorGUI.BeginChangeCheck();
            float time = EditorGUILayout.Slider("Time", (float)player.CurrentTime, 0f, (float)data.Duration);
            if (EditorGUI.EndChangeCheck())
            {
                player.Evaluate(time);
            }
        }

        private void DrawPreview(UsdAnimationPlayer player, UsdAnimationData data)
        {
            bool asset = EditorUtility.IsPersistent(player);
            bool busy = !Previewing && AnimationMode.InAnimationMode();
            using (new EditorGUI.DisabledScope(busy || asset))
            {
                bool wanted = GUILayout.Toggle(Previewing, "Preview", "Button");
                if (wanted && !Previewing)
                {
                    StopPreview();
                    preview = UsdAnimationPreview.Start(player);
                }
                else if (!wanted && Previewing)
                {
                    StopPreview();
                }
            }

            if (busy)
            {
                EditorGUILayout.HelpBox("Another window (the Animation window or a Timeline) is previewing; stop it to preview here.",
                                        MessageType.Info);
                return;
            }

            if (!Previewing)
            {
                // A prefab asset is not in a scene; there is nothing to pose.
                EditorGUILayout.HelpBox(asset
                    ? "Open the asset in a scene or in Prefab Mode to preview it."
                    : "Preview scrubs the stage in Edit Mode and puts the scene back when it ends.", MessageType.None);
                return;
            }

            EditorGUI.BeginChangeCheck();
            float time = EditorGUILayout.Slider("Time", preview.Time, 0f, (float)data.Duration);
            if (EditorGUI.EndChangeCheck())
            {
                preview.SetTime(time);
            }
        }

        private void StopPreview()
        {
            preview?.Dispose();
            preview = null;
        }

        private void OnSceneSaving(UnityEngine.SceneManagement.Scene scene, string path) => StopPreview();

        private void OnPlayModeChanged(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.ExitingEditMode)
            {
                StopPreview();
            }
        }

        [MenuItem("CONTEXT/UsdAnimationPlayer/Create Animation Clip...")]
        private static void CreateClip(MenuCommand command)
        {
            var player = (UsdAnimationPlayer)command.context;
            if (player.Data == null)
            {
                EditorUtility.DisplayDialog("Create Animation Clip", "This player has no animation data.", "OK");
                return;
            }

            string path = EditorUtility.SaveFilePanelInProject("Create Animation Clip", player.name + ".anim", "anim",
                                                               "Save the stage's animation as an AnimationClip.");
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            AnimationClip clip = UsdAnimationClipBuilder.Build(player);
            AssetDatabase.CreateAsset(clip, path);
            EditorGUIUtility.PingObject(clip);
        }
    }
}
