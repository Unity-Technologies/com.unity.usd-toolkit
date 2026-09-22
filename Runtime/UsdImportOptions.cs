using System;
using UnityEngine;

namespace Unity.USDToolkit
{
    public sealed class UsdImportOptions
    {
        public Transform Parent;
        public string RootObjectName;
        public bool ImportMaterials = true;
        public bool ImportTextures = true;
        public bool IncludeInvisible = true;
        public bool GenerateColliders;
        public bool RecalculateNormalsIfMissing = true;
        public bool CaptureNativeDiagnostics;
        public string NativeDiagnosticsLogPath;

        // Async import only (UsdImporter.ImportAsync): the main-thread object-build pass yields
        // to the next frame once it has spent this many milliseconds, so large imports do not
        // freeze the editor/player. Ignored by the synchronous UsdImporter.Import.
        public float MaxMillisecondsPerFrame = 10f;

        // Optional progress callback for ImportAsync: (fraction 0..1, phase label). Invoked on
        // the main thread.
        public Action<float, string> ProgressCallback;
    }
}
