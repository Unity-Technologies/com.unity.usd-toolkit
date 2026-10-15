using System;
using System.Collections;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;

namespace Unity.USDToolkit.RuntimeDemo
{
    /// <summary>
    /// Wraps <see cref="UsdImporter"/> so a failed import is a status line, never a stack trace.
    /// </summary>
    public sealed class UsdImportService
    {
        private const float LoadInSeconds = 0.3f;

        private readonly Transform sceneRoot;

        public UsdImportService(Transform sceneRoot)
        {
            this.sceneRoot = sceneRoot;
        }

        public UsdConversionMode UpAxisMode { get; set; } = UsdConversionMode.Auto;
        public UsdConversionMode UnitMode { get; set; } = UsdConversionMode.Auto;

        /// <summary>
        /// Imports one file additively. Existing entries are never touched.
        /// </summary>
        /// <param name="onProgress">Fraction 0..1 plus a phase label, for the status line.</param>
        public IEnumerator Import(
            string path,
            Action<float, string> onProgress,
            Action<UsdSceneEntry> onSuccess,
            Action<string> onFailure)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                onFailure?.Invoke($"Import failed: file not found ({Path.GetFileName(path ?? "?")})");
                yield break;
            }

            string fileName = Path.GetFileName(path);

            // Built up front so the toolkit can parent straight into the corrected subtree; the
            // correction values are written once the stage metadata comes back. Kept inactive
            // until then, otherwise the time-sliced build shows the stage at its raw size and
            // orientation and it visibly snaps when the correction lands.
            var rootObject = new GameObject("USD · " + Path.GetFileNameWithoutExtension(path));
            rootObject.SetActive(false);
            rootObject.transform.SetParent(sceneRoot, false);
            var correctionObject = new GameObject("StageCorrection");
            correctionObject.transform.SetParent(rootObject.transform, false);

            var options = new UsdImportOptions
            {
                Parent = correctionObject.transform,
                RootObjectName = Path.GetFileNameWithoutExtension(path),
                ImportMaterials = true,
                ImportTextures = true,
                // Click-to-select raycasts against these; without them nothing is selectable.
                GenerateColliders = true,
                RecalculateNormalsIfMissing = true,
                // The StageCorrection transform owns up-axis and units so the HUD's Auto/Off
                // toggles work; letting the toolkit correct as well applies it twice.
                ApplyStageUpAxisAndUnits = false,
                MaxMillisecondsPerFrame = 8.0f,
                ProgressCallback = (fraction, phase) => onProgress?.Invoke(fraction, phase)
            };

            Task<UsdImportResult> task = null;
            string launchError = null;
            try
            {
                task = UsdImporter.ImportAsync(path, options);
            }
            catch (Exception exception)
            {
                launchError = Describe(exception, fileName);
            }

            if (launchError != null)
            {
                UnityEngine.Object.Destroy(rootObject);
                onFailure?.Invoke(launchError);
                yield break;
            }

            while (!task.IsCompleted)
            {
                yield return null;
            }

            if (task.IsFaulted)
            {
                Exception inner = task.Exception?.GetBaseException() ?? task.Exception;
                UnityEngine.Object.Destroy(rootObject);
                onFailure?.Invoke(Describe(inner, fileName));
                yield break;
            }

            if (task.IsCanceled)
            {
                UnityEngine.Object.Destroy(rootObject);
                onFailure?.Invoke($"Import cancelled: {fileName}");
                yield break;
            }

            UsdImportResult result = task.Result;
            if (result == null || result.MeshCount == 0)
            {
                UnityEngine.Object.Destroy(rootObject);
                onFailure?.Invoke($"Import failed: no geometry found in {fileName}");
                yield break;
            }

            UsdStageConversion conversion = UsdStageConversion.Resolve(
                result.UpAxis,
                result.MetersPerUnit,
                UpAxisMode,
                UnitMode);
            conversion.ApplyTo(correctionObject.transform);

            // Renderer bounds are empty while inactive, so this has to precede the drop position.
            rootObject.SetActive(true);

            var entry = new UsdSceneEntry(path, rootObject, correctionObject.transform, result, conversion);

            // Park the new asset clear of whatever is already loaded so an additive import is
            // visibly additive rather than a pile at the origin.
            rootObject.transform.localPosition = ResolveDropPosition(entry);

            yield return PlayLoadIn(rootObject.transform);

            onSuccess?.Invoke(entry);
        }

        /// <summary>
        /// Lays imports out left to right along X, spaced by the bounds already on screen.
        /// </summary>
        private Vector3 ResolveDropPosition(UsdSceneEntry entry)
        {
            Bounds? incoming = TryGetWorldBounds(entry.Root.transform);
            float halfWidth = incoming.HasValue ? Mathf.Max(incoming.Value.extents.x, 0.05f) : 0.25f;

            float occupiedMaxX = float.NegativeInfinity;
            for (int i = 0; i < sceneRoot.childCount; i++)
            {
                Transform child = sceneRoot.GetChild(i);
                if (child == entry.Root.transform)
                {
                    continue;
                }

                Bounds? existing = TryGetWorldBounds(child);
                if (existing.HasValue)
                {
                    occupiedMaxX = Mathf.Max(occupiedMaxX, existing.Value.max.x);
                }
            }

            if (float.IsNegativeInfinity(occupiedMaxX))
            {
                // First asset in: centre it, and sit it on the grid rather than through it.
                return incoming.HasValue
                    ? new Vector3(-incoming.Value.center.x, -incoming.Value.min.y, -incoming.Value.center.z)
                    : Vector3.zero;
            }

            const float gap = 0.25f;
            float targetCentreX = occupiedMaxX + gap + halfWidth;
            if (!incoming.HasValue)
            {
                return new Vector3(targetCentreX, 0.0f, 0.0f);
            }

            return new Vector3(
                targetCentreX - incoming.Value.center.x,
                -incoming.Value.min.y,
                -incoming.Value.center.z);
        }

        private static Bounds? TryGetWorldBounds(Transform target)
        {
            Renderer[] renderers = target.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0)
            {
                return null;
            }

            Bounds bounds = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++)
            {
                bounds.Encapsulate(renderers[i].bounds);
            }

            return bounds;
        }

        /// <summary>
        /// A short ease-out scale-in. A hard pop-in reads as unfinished on a projector.
        /// </summary>
        private static IEnumerator PlayLoadIn(Transform target)
        {
            Vector3 finalScale = target.localScale;
            float elapsed = 0.0f;

            while (elapsed < LoadInSeconds)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.Clamp01(elapsed / LoadInSeconds);
                float eased = 1.0f - Mathf.Pow(1.0f - t, 3.0f);
                target.localScale = finalScale * Mathf.Lerp(0.82f, 1.0f, eased);
                yield return null;
            }

            target.localScale = finalScale;
        }

        /// <summary>
        /// Turns a toolkit exception into one sentence a customer can read over your shoulder.
        /// </summary>
        public static string Describe(Exception exception, string fileName)
        {
            switch (exception)
            {
                case null:
                    return $"Import failed: {fileName}";
                case UsdImportException:
                    return $"Import failed ({fileName}): {FirstLine(exception.Message)}";
                case DllNotFoundException:
                    return "Import failed: USD native plugin not loaded for this platform.";
                case UnauthorizedAccessException:
                    return $"Import failed: no permission to read {fileName}.";
                case FileNotFoundException:
                case DirectoryNotFoundException:
                    return $"Import failed: {fileName} is no longer on disk.";
                case IOException:
                    return $"Import failed: could not read {fileName}. {FirstLine(exception.Message)}";
                default:
                    return $"Import failed ({fileName}): {FirstLine(exception.Message)}";
            }
        }

        private static string FirstLine(string message)
        {
            if (string.IsNullOrEmpty(message))
            {
                return "unknown error";
            }

            int breakIndex = message.IndexOfAny(new[] { '\r', '\n' });
            string line = breakIndex < 0 ? message : message.Substring(0, breakIndex);
            return line.Length > 160 ? line.Substring(0, 157) + "..." : line;
        }
    }
}
