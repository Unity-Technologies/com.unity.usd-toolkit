using UnityEditor;
using UnityEditor.AssetImporters;
using UnityEngine;

namespace Unity.USDToolkit.Editor
{
    /// <summary>
    /// Inspector for <see cref="UsdStageImporter"/>: the import settings, then one dropdown per
    /// variant set the stage offers, which writes the choice into the variant selections. Apply
    /// reimports the stage composed with it.
    /// </summary>
    [CustomEditor(typeof(UsdStageImporter))]
    public sealed class UsdStageImporterEditor : ScriptedImporterEditor
    {
        private const string SelectionsProperty = nameof(UsdStageImporter.VariantSelections);

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            DrawPropertiesExcluding(serializedObject, "m_Script", SelectionsProperty);
            DrawVariantSets();
            EditorGUILayout.PropertyField(serializedObject.FindProperty(SelectionsProperty), true);
            serializedObject.ApplyModifiedProperties();
            ApplyRevertGUI();
        }

        private void DrawVariantSets()
        {
            var importer = (UsdStageImporter)target;
            var imported = AssetDatabase.LoadAssetAtPath<GameObject>(importer.assetPath);
            UsdStageSource source = imported != null ? imported.GetComponent<UsdStageSource>() : null;
            if (source == null || source.VariantSets.Count == 0)
            {
                return;
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Variant sets", EditorStyles.boldLabel);
            SerializedProperty selections = serializedObject.FindProperty(SelectionsProperty);
            foreach (UsdVariantSetInfo set in source.VariantSets)
            {
                if (set.Variants == null || set.Variants.Count == 0)
                {
                    continue;
                }

                int entry = FindSelection(selections, set.PrimPath, set.Name);
                string current = entry >= 0
                    ? selections.GetArrayElementAtIndex(entry).FindPropertyRelative(nameof(UsdVariantSelection.Variant)).stringValue
                    : set.Selection;
                int index = Mathf.Max(0, set.Variants.IndexOf(current));
                var label = new GUIContent($"{set.Name}", $"{set.PrimPath} {{{set.Name}}}");
                int chosen = EditorGUILayout.Popup(label, index, set.Variants.ToArray());
                if (chosen == index)
                {
                    continue;
                }

                if (entry < 0)
                {
                    entry = selections.arraySize;
                    selections.InsertArrayElementAtIndex(entry);
                }

                SerializedProperty element = selections.GetArrayElementAtIndex(entry);
                element.FindPropertyRelative(nameof(UsdVariantSelection.PrimPath)).stringValue = set.PrimPath;
                element.FindPropertyRelative(nameof(UsdVariantSelection.VariantSet)).stringValue = set.Name;
                element.FindPropertyRelative(nameof(UsdVariantSelection.Variant)).stringValue = set.Variants[chosen];
            }
        }

        private static int FindSelection(SerializedProperty selections, string primPath, string variantSet)
        {
            for (int i = 0; i < selections.arraySize; i++)
            {
                SerializedProperty element = selections.GetArrayElementAtIndex(i);
                if (element.FindPropertyRelative(nameof(UsdVariantSelection.PrimPath)).stringValue == primPath &&
                    element.FindPropertyRelative(nameof(UsdVariantSelection.VariantSet)).stringValue == variantSet)
                {
                    return i;
                }
            }

            return -1;
        }
    }
}
