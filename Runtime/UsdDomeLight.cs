using UnityEngine;

namespace Unity.USDToolkit
{
    /// <summary>
    /// A UsdLuxDomeLight as authored. Unity has no light for it; with
    /// <see cref="UsdImportOptions.ApplyDomeLightToEnvironment"/> the importer turns it into the
    /// scene's ambient colour and, for a PNG or JPG texture, a panoramic skybox.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class UsdDomeLight : MonoBehaviour
    {
        /// <summary>inputs:color, times the colour temperature when it is enabled.</summary>
        public Color Color = Color.white;

        /// <summary>inputs:intensity x 2^inputs:exposure, as authored.</summary>
        public float Intensity = 1f;

        /// <summary>inputs:texture:file, resolved like a material texture; empty when none.</summary>
        public string TexturePath = string.Empty;
    }
}
