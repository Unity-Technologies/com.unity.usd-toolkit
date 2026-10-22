using UnityEngine;

namespace Unity.USDToolkit
{
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
