using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Unity.USDToolkit.ComposedGripper.Editor
{
    /// <summary>
    /// USD Toolkit > Composed Gripper Physics > Window: the play-mode panel in edit mode. Imports
    /// the chosen asset into the open scene, frames it in the Scene view, opens and closes every
    /// gripper (each <see cref="GripperDrive"/> poses its links, since physics does not run in edit
    /// mode), and shows per gripper what composed. Selecting a gripper import in the hierarchy shows
    /// that one instead.
    /// </summary>
    public sealed class ComposedGripperWindow : EditorWindow
    {
        private readonly ComposedGripperPanel panel = new ComposedGripperPanel { CycleSeconds = 0f };
        private GameObject imported;
        private Vector2 scroll;
        private bool importPending;

        [MenuItem("USD Toolkit/Composed Gripper Physics/Window", priority = 0)]
        public static void Open() => GetWindow<ComposedGripperWindow>("Composed Gripper");

        private void OnEnable()
        {
            EditorApplication.update += OnEditorUpdate;
            OnSelectionChange();
        }

        private void OnDisable() => EditorApplication.update -= OnEditorUpdate;

        private void OnEditorUpdate()
        {
            // Outside OnGUI: importing creates objects and changes the selection.
            if (importPending)
            {
                importPending = false;
                Import(panel.Asset, panel.Preset);
            }

            // The closing each gripper reports changes every frame while they cycle.
            if (panel.CycleSeconds > 0f && imported != null)
            {
                Repaint();
            }
        }

        /// <summary>Shows a gripper import the user selects (its root or anything below it).</summary>
        private void OnSelectionChange()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || Selection.activeGameObject == null)
            {
                return;
            }

            UsdStageSource source = Selection.activeGameObject.GetComponentInParent<UsdStageSource>();
            if (source == null || source.gameObject == imported || source.GetComponentInChildren<ArticulationBody>() == null)
            {
                return;
            }

            imported = source.gameObject;
            panel.Summarize(imported, source, null);
            panel.Status = $"{imported.name}: {panel.Grippers.Count} articulation(s) (selected in the hierarchy).";
            Repaint();
        }

        private void OnGUI()
        {
            if (EditorApplication.isPlaying)
            {
                EditorGUILayout.HelpBox("In Play Mode, the grippers run on physics: use the panel in the Game view " +
                                        "(ComposedGripperPhysics.unity), or a GripperDrive in the Inspector.", MessageType.Info);
                return;
            }

            if (imported == null && panel.Grippers.Count > 0)
            {
                panel.Clear();
                panel.Status = "The import was deleted.";
            }

            scroll = EditorGUILayout.BeginScrollView(scroll);
            panel.DrawTitle("Composed Gripper Physics");
            EditorGUILayout.LabelField("Edit mode: the links are posed at each joint's target; Play Mode drives them with physics.",
                                       EditorStyles.wordWrappedMiniLabel);
            importPending |= panel.Draw();

            EditorGUILayout.EndScrollView();
        }

        /// <summary>Replaces the window's import with another asset (and, for v3 gripper, preset).</summary>
        public void Import(GripperAsset asset, string preset)
        {
            panel.Asset = asset;
            panel.Preset = preset;
            try
            {
                if (imported != null)
                {
                    Undo.DestroyObjectImmediate(imported);
                }

                UsdImportResult result = ComposedGripperMenu.Import(asset, preset);
                imported = result.RootObject;
                panel.Summarize(imported, result.Source, result.PhysicsNotes);
                FrameInSceneView(imported);
            }
            catch (Exception exception)
            {
                panel.Clear();
                panel.Status = "Failed: " + exception.Message;
                Debug.LogException(exception);
            }

            Repaint();
        }

        private static void FrameInSceneView(GameObject root)
        {
            SceneView view = SceneView.lastActiveSceneView;
            Renderer[] renderers = root.GetComponentsInChildren<Renderer>();
            if (view == null || renderers.Length == 0)
            {
                return;
            }

            Bounds bounds = renderers[0].bounds;
            foreach (Renderer renderer in renderers)
            {
                bounds.Encapsulate(renderer.bounds);
            }

            view.Frame(bounds, false);
        }
    }

    /// <summary>
    /// Puts every gripper back in the pose the import built before play mode starts and before a
    /// scene is saved: the articulations start from the authored rest pose, and a scene file never
    /// keeps a pose only the edit-mode preview made. Each GripperDrive poses its gripper again on the next Editor update.
    /// </summary>
    [InitializeOnLoad]
    internal static class GripperRestPose
    {
        static GripperRestPose()
        {
            EditorApplication.playModeStateChanged += state =>
            {
                if (state == PlayModeStateChange.ExitingEditMode)
                {
                    ResetAll(null);
                }
            };

            EditorSceneManager.sceneSaving += (scene, path) => ResetAll(scene);
        }

        private static void ResetAll(Scene? scene)
        {
            foreach (GripperDrive drive in UnityEngine.Object.FindObjectsByType<GripperDrive>(FindObjectsSortMode.None))
            {
                if (scene == null || drive.gameObject.scene == scene.Value)
                {
                    drive.ApplyPose(0f);
                }
            }
        }
    }
}
