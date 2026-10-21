using System;
using System.Collections.Generic;
using UnityEditor.Recorder;
using UnityEngine;

namespace Unity.USDToolkit.Editor
{
    /// <summary>File format of a USD Animation recording.</summary>
    public enum UsdRecorderFormat
    {
        /// <summary>Binary crate (.usdc): compact, the usual choice for animation.</summary>
        Usdc = 0,

        /// <summary>Text (.usda): readable and diffable, larger.</summary>
        Usda = 1,

        /// <summary>A .usdz package, textures included when they are exported.</summary>
        Usdz = 2
    }

    /// <summary>
    /// Unity Recorder settings for a "USD Animation" recording: what a GameObject hierarchy does in
    /// Play Mode, written as an animated USD stage by <see cref="UsdAnimationRecorder"/> (transform
    /// and active-state samples on prims, UsdSkel animation for skinned characters). Add it in the
    /// Recorder window, or from code with RecorderControllerSettings.AddRecorderSettings.
    /// </summary>
    [Serializable]
    [RecorderSettings(typeof(UsdRecorder), "USD Animation", "animation_recorder")]
    public sealed class UsdRecorderSettings : RecorderSettings
    {
        [SerializeField] private UsdRecorderInputSettings input = new UsdRecorderInputSettings();
        [SerializeField] private UsdRecorderFormat format = UsdRecorderFormat.Usdc;
        [SerializeField] private bool exportTextures;
        [SerializeField] private bool overridesOnly;

        public UsdRecorderSettings()
        {
            string gameObject = DefaultWildcard.GeneratePattern("GameObject");
            FileNameGenerator.AddWildcard(gameObject, session => Input.GameObject != null ? Input.GameObject.name : "None");
            FileNameGenerator.FileName = DefaultWildcard.Recorder + "_" + gameObject + "_" + DefaultWildcard.Take;
        }

        /// <summary>The GameObject to record.</summary>
        public UsdRecorderInputSettings Input
        {
            get => input;
            set => input = value ?? throw new ArgumentNullException(nameof(value));
        }

        /// <summary>The file format, and so the extension, of the recording.</summary>
        public UsdRecorderFormat Format
        {
            get => format;
            set => format = value;
        }

        /// <summary>Write material textures beside the recording (see <see cref="UsdExportOptions.ExportTextures"/>).</summary>
        public bool ExportTextures
        {
            get => exportTextures;
            set => exportTextures = value;
        }

        /// <summary>
        /// Write the recording as overrides on the imported stage the GameObject came from, instead
        /// of a copy of the whole hierarchy (see <see cref="UsdAnimationRecorder.Save"/> with
        /// <see cref="UsdCompositionMode.Preserve"/>).
        /// </summary>
        public bool OverridesOnly
        {
            get => overridesOnly;
            set => overridesOnly = value;
        }

        public override IEnumerable<RecorderInputSettings> InputsSettings
        {
            get { yield return input; }
        }

        protected override string Extension
        {
            get
            {
                switch (format)
                {
                    case UsdRecorderFormat.Usda:
                        return "usda";
                    case UsdRecorderFormat.Usdz:
                        return "usdz";
                    default:
                        return "usdc";
                }
            }
        }

        public override bool IsPlatformSupported =>
            Application.platform == RuntimePlatform.OSXEditor ||
            Application.platform == RuntimePlatform.WindowsEditor ||
            Application.platform == RuntimePlatform.LinuxEditor;

        protected override void GetErrors(List<string> errors)
        {
            base.GetErrors(errors);
            if (input.GameObject == null)
            {
                errors.Add("No GameObject to record.");
            }
        }

        public override void OnAfterDuplicate()
        {
            input.DuplicateBinding();
        }
    }
}
