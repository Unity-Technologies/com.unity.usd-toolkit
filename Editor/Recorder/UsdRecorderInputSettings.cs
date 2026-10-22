using System;
using System.Collections.Generic;
using System.ComponentModel;
using UnityEditor.Recorder;
using UnityEngine;

namespace Unity.USDToolkit.Editor
{
    /// <summary>
    /// What a <see cref="UsdRecorderSettings"/> records: a GameObject and everything below it. The
    /// GameObject lives in a scene and the settings in an asset, so it is kept through the
    /// Recorder's scene binding, as the Animation Clip recorder keeps its own.
    /// </summary>
    [Serializable]
    [DisplayName("USD Animation")]
    public sealed class UsdRecorderInputSettings : RecorderInputSettings
    {
        [SerializeField] private string bindingId;

        /// <summary>The GameObject to record, with its hierarchy.</summary>
        public GameObject GameObject
        {
            get => string.IsNullOrEmpty(bindingId) ? null : BindingManager.Get(bindingId) as GameObject;
            set
            {
                if (string.IsNullOrEmpty(bindingId))
                {
                    bindingId = Guid.NewGuid().ToString("N");
                }

                BindingManager.Set(bindingId, value);
            }
        }

        // UsdRecorder samples the scene itself in RecordFrame; the input only carries the binding.
        protected override Type InputType => typeof(UsdRecorderInput);

        protected override void CheckForErrors(List<string> errors)
        {
            base.CheckForErrors(errors);
            if (GameObject == null)
            {
                errors.Add("No GameObject to record.");
            }
        }

        // A duplicated recorder gets its own binding, so changing one does not change the other.
        internal void DuplicateBinding()
        {
            if (string.IsNullOrEmpty(bindingId))
            {
                return;
            }

            string source = bindingId;
            bindingId = Guid.NewGuid().ToString("N");
            BindingManager.Duplicate(source, bindingId);
        }
    }

    internal sealed class UsdRecorderInput : RecorderInput
    {
    }
}
