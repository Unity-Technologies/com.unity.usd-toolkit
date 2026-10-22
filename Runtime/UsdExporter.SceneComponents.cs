using System;
using System.Collections.Generic;
using Unity.USDToolkit.Native;
using UnityEngine;

namespace Unity.USDToolkit
{
    // Native API 13 on export: uv1/uv2, vertex colours, lights, cameras and material flavours.
    //
    // Each conversion is the inverse of what the importer does with the same USD, so an exported
    // light, camera or UV set imports back to what it was:
    //   - UV sets go to primvars:st1 / st2, the first names the importer's UV-set table reads.
    //   - Light colours are written as Light.color converted back to linear (UsdImporter.UsdLightColor).
    //   - Intensities divide by UsdLightUnits, the factors UsdImportOptions.PhysicalLightUnits
    //     multiplies by (on by default), with inputs:normalize where the importer then ignores
    //     the emitter's size.
    //   - Lights and cameras sit on a child prim turned 180 degrees about Y, as the importer's
    //     UsdImportHelperObject is: USD lights and cameras point down -Z, Unity's down +Z.
    public static partial class UsdExporter
    {
        private const string LightPrimName = "Light";
        private const string CameraPrimName = "Camera";

        // Perspective cameras that are not physical get this sensor height (mm), the 35 mm still
        // frame's, and the focal length that gives their field of view.
        private const float DefaultSensorHeight = 24f;

        private static readonly Matrix4x4 FaceMinusZ = Matrix4x4.Rotate(Quaternion.Euler(0f, 180f, 0f));

        private static int ToNativeMaterialFlavors(UsdExportOptions options)
        {
            int flags = 0;
            if ((options.MaterialFlavors & UsdExportMaterialFlavors.OmniPbr) != 0)
            {
                flags |= UsdNative.MaterialFlavorOmniPbr;
            }

            if ((options.MaterialFlavors & UsdExportMaterialFlavors.MaterialX) != 0)
            {
                flags |= UsdNative.MaterialFlavorMaterialX;
            }

            return flags;
        }

        // Called right after a layer's context opens, before any mesh is added.
        private static void ApplyMaterialFlavors(IntPtr context, UsdExportOptions options)
        {
            int flags = ToNativeMaterialFlavors(options);
            if (flags == 0)
            {
                return;
            }

            if (!UsdNative.SupportsExportExtras)
            {
                Debug.LogWarning(
                    $"Unity USD Toolkit: OmniPBR and MaterialX materials need {UsdNative.NeedsPlugin(UsdNative.ExportExtrasApiVersion)}; " +
                    $"{UsdNative.LoadedPluginDescription}, so only UsdPreviewSurface is written.");
                return;
            }

            UsdNative.SetExportMaterialFlavors(context, flags);
        }

        // uv1, uv2 and vertex colours of a mesh RUsd_AddMesh just wrote at primPath.
        private static void WriteMeshExtras(IntPtr context, string primPath, Mesh mesh, UsdExportOptions options)
        {
            if (!UsdNative.SupportsExportExtras || (!options.ExportUvSets && !options.ExportVertexColors))
            {
                return;
            }

            int vertexCount = mesh.vertexCount;
            if (options.ExportUvSets)
            {
                WriteUvSet(context, primPath, 1, mesh.uv2, vertexCount);
                WriteUvSet(context, primPath, 2, mesh.uv3, vertexCount);
            }

            if (options.ExportVertexColors)
            {
                Color[] colors = mesh.colors;
                if (colors != null && colors.Length == vertexCount && vertexCount > 0)
                {
                    bool linear = QualitySettings.activeColorSpace == ColorSpace.Linear;
                    var values = new RUsdVec4[colors.Length];
                    for (int i = 0; i < colors.Length; i++)
                    {
                        Color c = linear ? colors[i].linear : colors[i];
                        values[i] = new RUsdVec4(c.r, c.g, c.b, colors[i].a);
                    }

                    UsdNative.SetMeshVertexColors(context, primPath, values);
                }
            }
        }

        private static void WriteUvSet(IntPtr context, string primPath, int uvSet, Vector2[] uv, int vertexCount)
        {
            if (uv == null || uv.Length != vertexCount || vertexCount == 0)
            {
                return;
            }

            var values = new RUsdVec2[uv.Length];
            for (int i = 0; i < uv.Length; i++)
            {
                values[i] = new RUsdVec2(uv[i].x, uv[i].y);
            }

            UsdNative.SetMeshUvSet(context, primPath, uvSet, values);
        }

        // Lights and cameras under root. With xformPaths (PreserveHierarchy) each goes on a child
        // of its object's prim; without (baked), on a prim under the root with the object's
        // transform relative to the root, as baked meshes are.
        private static void ExportLightsAndCameras(
            Transform root,
            string rootPrimName,
            IntPtr context,
            UsdExportOptions options,
            Dictionary<Transform, string> xformPaths,
            HashSet<string> usedPaths,
            UsdExportResult result)
        {
            if (!options.ExportLights && !options.ExportCameras)
            {
                return;
            }

            if (!UsdNative.SupportsExportExtras)
            {
                if (root.GetComponentInChildren<Light>(options.IncludeInactive) != null ||
                    root.GetComponentInChildren<Camera>(options.IncludeInactive) != null)
                {
                    Debug.LogWarning(
                        $"Unity USD Toolkit: lights and cameras need {UsdNative.NeedsPlugin(UsdNative.ExportExtrasApiVersion)}; " +
                        $"{UsdNative.LoadedPluginDescription}, so they are left out.");
                }

                return;
            }

            // A child prim named Light or Camera must not land on an object's own prim path.
            if (xformPaths != null)
            {
                usedPaths.UnionWith(xformPaths.Values);
            }

            if (options.ExportLights)
            {
                foreach (Light light in root.GetComponentsInChildren<Light>(options.IncludeInactive))
                {
                    if (IsImporterHelper(light.transform) || !TryCreateExportLight(light, out RUsdExportLight usd))
                    {
                        continue;
                    }

                    string path = MakeComponentPrimPath(root, light.transform, rootPrimName, LightPrimName, xformPaths, usedPaths);
                    bool visible = light.enabled && light.gameObject.activeInHierarchy;
                    UsdNative.AddLight(context, path, ComponentMatrix(root, light.transform, xformPaths), usd, visible);
                    result.LightCount++;
                }
            }

            if (options.ExportCameras)
            {
                foreach (Camera camera in root.GetComponentsInChildren<Camera>(options.IncludeInactive))
                {
                    if (IsImporterHelper(camera.transform))
                    {
                        continue;
                    }

                    string path = MakeComponentPrimPath(root, camera.transform, rootPrimName, CameraPrimName, xformPaths, usedPaths);
                    bool visible = camera.enabled && camera.gameObject.activeInHierarchy;
                    UsdNative.AddCamera(context, path, ComponentMatrix(root, camera.transform, xformPaths), CreateExportCamera(camera), visible);
                    result.CameraCount++;
                }
            }
        }

        // Preserve exports: `t`'s own light and camera, as children of its prim. uniqueChild makes
        // a free child path of the prim from a name.
        private static void WriteLightAndCameraOn(
            IntPtr context,
            Transform t,
            Func<string, string> uniqueChild,
            UsdExportOptions options,
            UsdExportResult result)
        {
            if (!UsdNative.SupportsExportExtras || IsImporterHelper(t))
            {
                return;
            }

            if (options.ExportLights && t.TryGetComponent(out Light light) && TryCreateExportLight(light, out RUsdExportLight usdLight))
            {
                UsdNative.AddLight(context, uniqueChild(LightPrimName), ToUsdMatrix(FaceMinusZ), usdLight, light.enabled);
                result.LightCount++;
            }

            if (options.ExportCameras && t.TryGetComponent(out Camera camera))
            {
                UsdNative.AddCamera(context, uniqueChild(CameraPrimName), ToUsdMatrix(FaceMinusZ), CreateExportCamera(camera), camera.enabled);
                result.CameraCount++;
            }
        }

        // Lights and cameras the importer made for a stage's own (rect-light grids included) are
        // conversions, not content; the source keeps the originals.
        private static bool IsImporterHelper(Transform transform)
        {
            return transform.GetComponentInParent<UsdImportHelperObject>(true) != null;
        }

        private static string MakeComponentPrimPath(
            Transform root,
            Transform owner,
            string rootPrimName,
            string childName,
            Dictionary<Transform, string> xformPaths,
            HashSet<string> usedPaths)
        {
            string parent = xformPaths != null && xformPaths.TryGetValue(owner, out string ownerPath)
                ? ownerPath
                : "/" + rootPrimName + "/" + SanitizePrimName(owner == root ? root.name : owner.name);
            return MakeUniquePath(parent + "/" + childName, usedPaths);
        }

        private static RUsdMatrix4x4 ComponentMatrix(Transform root, Transform owner, Dictionary<Transform, string> xformPaths)
        {
            if (xformPaths != null && xformPaths.ContainsKey(owner))
            {
                return ToUsdMatrix(FaceMinusZ);
            }

            // Baked: the owner's prim is not written, so the component carries the owner's
            // transform relative to the root, without scale (a light or camera has no size from it).
            Matrix4x4 relative = root.worldToLocalMatrix * owner.localToWorldMatrix;
            Vector3 position = relative.GetColumn(3);
            Quaternion rotation = relative.rotation;
            return ToUsdMatrix(Matrix4x4.TRS(position, rotation, Vector3.one) * FaceMinusZ);
        }

        private static bool TryCreateExportLight(Light light, out RUsdExportLight usd)
        {
            // Back to USD's linear colour; the inverse of UsdImporter.UsdLightColor.
            Color color = QualitySettings.activeColorSpace == ColorSpace.Linear ? light.color.linear : light.color;
            usd = new RUsdExportLight
            {
                ColorR = color.r,
                ColorG = color.g,
                ColorB = color.b,
                CastShadows = light.shadows != LightShadows.None ? 1 : 0,
                ColorTemperature = light.useColorTemperature ? light.colorTemperature : 0f
            };

            float intensity = Mathf.Max(0f, light.intensity);
            switch (light.type)
            {
                case LightType.Directional:
                    usd.Type = UsdNative.LightDistant;
                    usd.Intensity = intensity / UsdLightUnits.Distant;
                    usd.Angle = 0.53f; // the sun's, and UsdLux's default
                    return true;

                case LightType.Point:
                case LightType.Spot:
                    // Normalized, so the importer reads the intensity as is (area 1) and the
                    // small radius only keeps a renderer from drawing a visible bulb.
                    usd.Type = UsdNative.LightSphere;
                    usd.Normalize = 1;
                    usd.Radius = 0.05f;
                    usd.Intensity = intensity / UsdLightUnits.SpherePerSquareMetre;
                    if (light.type == LightType.Spot)
                    {
                        usd.ConeAngle = Mathf.Clamp(light.spotAngle * 0.5f, 0.5f, 90f);
                        usd.ConeSoftness = light.spotAngle > 0f
                            ? Mathf.Clamp01(1f - light.innerSpotAngle / light.spotAngle)
                            : 0f;
                    }

                    return true;

                case LightType.Rectangle:
                    // Unity's area intensity is per unit area; the importer's rect grid gives each
                    // cell intensity x cell area x RectPerSquareMetre, so this round-trips.
                    usd.Type = UsdNative.LightRect;
                    usd.Width = Mathf.Max(1e-4f, light.areaSize.x);
                    usd.Height = Mathf.Max(1e-4f, light.areaSize.y);
                    usd.Intensity = intensity / UsdLightUnits.RectPerSquareMetre;
                    return true;

                case LightType.Disc:
                    usd.Type = UsdNative.LightDisk;
                    usd.Normalize = 1;
                    usd.Radius = Mathf.Max(1e-4f, light.areaSize.x);
                    usd.Intensity = intensity / UsdLightUnits.SpherePerSquareMetre;
                    return true;

                default:
                    Debug.LogWarning(
                        $"Unity USD Toolkit: light '{light.name}' is a {light.type} light, which has no UsdLux " +
                        "equivalent; it is not exported.");
                    return false;
            }
        }

        private static RUsdExportCamera CreateExportCamera(Camera camera)
        {
            var usd = new RUsdExportCamera
            {
                NearClip = camera.nearClipPlane,
                FarClip = camera.farClipPlane
            };

            if (camera.orthographic)
            {
                // Apertures in tenths of a stage unit, as the importer reads them.
                usd.Projection = UsdNative.CameraOrthographic;
                usd.VerticalAperture = camera.orthographicSize * 2f * 10f;
                usd.HorizontalAperture = usd.VerticalAperture * camera.aspect;
                usd.FocalLength = 50f;
                return usd;
            }

            usd.Projection = UsdNative.CameraPerspective;
            if (camera.usePhysicalProperties)
            {
                usd.FocalLength = camera.focalLength;
                usd.HorizontalAperture = camera.sensorSize.x;
                usd.VerticalAperture = camera.sensorSize.y;
                usd.HorizontalApertureOffset = camera.lensShift.x * camera.sensorSize.x;
                usd.VerticalApertureOffset = camera.lensShift.y * camera.sensorSize.y;
            }
            else
            {
                usd.VerticalAperture = DefaultSensorHeight;
                usd.HorizontalAperture = DefaultSensorHeight * camera.aspect;
                usd.FocalLength = DefaultSensorHeight / (2f * Mathf.Tan(camera.fieldOfView * 0.5f * Mathf.Deg2Rad));
            }

            return usd;
        }
    }
}
