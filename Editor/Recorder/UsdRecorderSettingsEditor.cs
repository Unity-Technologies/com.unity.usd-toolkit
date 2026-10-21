using UnityEditor;
using UnityEditor.Recorder;
using UnityEngine;

namespace Unity.USDToolkit.Editor
{
    [CustomEditor(typeof(UsdRecorderSettings))]
    internal sealed class UsdRecorderSettingsEditor : RecorderEditor
    {
        private static readonly GUIContent GameObjectLabel =
            new GUIContent("GameObject", "The GameObject to record, with everything below it.");

        private static readonly GUIContent FormatLabel = new GUIContent("Format", "The file format of the recording.");

        private static readonly GUIContent TexturesLabel =
            new GUIContent("Export Textures", "Write material textures beside the recording.");

        private static readonly GUIContent OverridesLabel = new GUIContent(
            "Overrides Only",
            "Write the motion as a layer over the imported USD file the GameObject came from, instead of a copy of the whole model.");

        protected override void ImageRenderOptionsGUI()
        {
            var settings = (UsdRecorderSettings)target;
            EditorGUI.BeginChangeCheck();
            var gameObject = (GameObject)EditorGUILayout.ObjectField(GameObjectLabel, settings.Input.GameObject, typeof(GameObject), true);
            if (EditorGUI.EndChangeCheck())
            {
                settings.Input.GameObject = gameObject;
            }
        }

        protected override void FileTypeAndFormatGUI()
        {
            EditorGUILayout.PropertyField(serializedObject.FindProperty("format"), FormatLabel);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("exportTextures"), TexturesLabel);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("overridesOnly"), OverridesLabel);
        }
    }
}
