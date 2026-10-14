using UnityEngine;

namespace Unity.USDToolkit
{
    /// <summary>
    /// Marks a GameObject the importer created only to turn a light or camera to Unity's
    /// convention. USD lights emit and cameras look along their local -Z; Unity's along +Z, so the
    /// component sits on a child turned 180 degrees about Y. The child is not a prim: exporters
    /// skip it and everything below it.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("")]
    public sealed class UsdImportHelperObject : MonoBehaviour
    {
    }

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

    /// <summary>
    /// A UsdMediaSpatialAudio as authored. The importer adds an <see cref="AudioSource"/> set up
    /// from it but does not load the clip: audio files load asynchronously, and the import is not.
    /// Load <see cref="FilePath"/> (for example with UnityWebRequestMultimedia) and assign it.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class UsdSpatialAudio : MonoBehaviour
    {
        /// <summary>inputs:filePath, resolved like a material texture.</summary>
        public string FilePath = string.Empty;

        /// <summary>startTime in time codes.</summary>
        public double StartTime;

        /// <summary>endTime in time codes.</summary>
        public double EndTime;
    }
}
