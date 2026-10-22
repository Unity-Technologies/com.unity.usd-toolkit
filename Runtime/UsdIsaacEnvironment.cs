using System;
using System.Collections.Generic;
using System.IO;
using Unity.USDToolkit.Native;
using UnityEngine;
using UnityEngine.Rendering;

namespace Unity.USDToolkit
{
    // Light units and the dome environment, calibrated so that a stage imported into URP lights the
    // way Isaac Sim 6.2.0 (RTX Real-Time 2.0) draws it. Customers do not run Isaac Sim next to Unity,
    // so the match has to come from the import itself rather than from a scene set up by hand.
    //
    // The scale factors were fitted on a look bench: the same stage rendered in Isaac Sim and in
    // Unity, per-patch CIE Lab compared, with the Reference Look preset (Isaac tone curve LUT, post
    // exposure -0.2 EV) on the Unity side.
    internal static class UsdLightUnits
    {
        // Unity intensity per USD unit for a distant light (inputs:intensity x 2^exposure).
        public const float Distant = 0.0009f;

        // Rect lights: per square metre of emitting surface, for a point light carrying the
        // cosine-lobe cookie BuildRectGrid gives it. Fitted on NVIDIA's Factory against Isaac Sim
        // 6.2.0 from two interior views (a cell side-on, the mezzanine): 0.2 x the 0.0009 the
        // 179-degree spot grid used before, whose shadow map degenerated at that cone and
        // shadowed walls and floor the light reached (0.31 x Isaac on the mezzanine wall).
        // Then 1.8 x that, the value the Factory's final side-by-side settled on with the cookie
        // grid in place (car line and mezzanine within 0.92-0.96 x Isaac), made the default for
        // every stage rather than kept as a per-scene adjustment.
        public const float RectPerSquareMetre = 0.00018f * 1.8f;

        // Sphere, disk and cylinder lights: per unit of luminance x projected area (m^2). Fitted on
        // the bench's single 30000-nit, 0.1 m sphere.
        public const float SpherePerSquareMetre = 1.9e-4f;

        // A sphere, disk or cylinder light reaches until its illuminance (Unity intensity / d^2)
        // falls to this; below it the URP range window cuts nothing visible.
        public const float RangeCutoff = 0.002f;

        // Shadowed sphere, disk and cylinder lights per stage; see ApplyShadowBudget.
        public const int MaxShadowedPointLights = 4;

        // Renderers smaller than this share of the stage's diagonal cast no shadow; see
        // ApplyShadowBudget.
        public const float SmallCasterFraction = 0.004f;

        // Dome light: skybox exposure per USD unit, and the share of it that reaches ambient.
        public const float Dome = 0.00096f;
        // 1.0 over 47 stages (bench, assets, Factory, props, warehouse) against Isaac Sim 6.2. The
        // Factory once wanted 1.5, but that was while every dome reflection captured black (see
        // TryRefreshSkyReflection): the ambient was standing in for the missing specular. With the
        // reflection back, 1.5 left the bench and the props too bright (mean CIEDE2000 3.98 at
        // 1.5, 3.81 at 1.0, 3.87 at 0.85).
        public const float DomeAmbient = 1.0f;

        // Emissive surfaces: a point light of this times the emission (max channel) times the
        // emitting area (m^2). Isaac's path tracer lights the surroundings with every emitter; the
        // URP surface cache carries that weak and desaturated, so this adds it directly.
        public const float EmissivePerSquareMetre = 0.08f;

        // A light authored invisible (computed visibility). Isaac Sim does not drop it: on NVIDIA's
        // Simple_Warehouse, lit by 37 invisible ceiling rect lights, the aisle measured 0.109 with
        // them as authored, 0.330 forced visible and 0.0014 without them, while one invisible rect
        // over the open bench lit almost nothing (0.0034 against 0.22 visible) -- the light reaches
        // the scene through bounces, not directly. An invisible light keeps this share of its
        // direct light and all of its bounce (Light.bounceIntensity divided by it), which is what
        // Surface Cache GI carries. It used to keep a third of everything: measured while the
        // surface cache was failing after the first import of a session, which hid the bounce,
        // and with the bounce back the warehouse came out 2.9 x Isaac Sim (10-20).
        public static float InvisibleLight = 0.015f;

        // A light that only feeds Surface Cache GI: intensity x this, bounceIntensity / this, so
        // its direct light is negligible and its bounce is the light's own. Surface Cache GI reads
        // intensity x bounceIntensity and nothing else (no cookie), see BuildRectGrid.
        public const float GiOnlyScale = 1e-3f;

        // The cone of a rect light's bounce-only stand-in: 120 degrees is a solid angle of pi, so
        // a uniform cone of the cell intensity carries the flux of the Lambertian emitter (pi x I).
        public const float RectGiCone = 120f;

        // Rect lights become a grid of wide spots about this far apart (metres).
        public const float RectCellSize = 5f;
    }

    internal static class UsdIsaacEnvironment
    {
        // Sets the scene's skybox, ambient and reflection from a dome light. Returns false when
        // nothing was applied.
        public static bool ApplyDome(UsdDomeLight dome, Transform node)
        {
            float exposure = Mathf.Max(0f, dome.Intensity) * UsdLightUnits.Dome;
            Material sky = null;
            Texture2D texture = LoadEnvironmentTexture(dome.TexturePath, DomeOrientation(node));
            if (texture != null)
            {
                Shader panoramic = Shader.Find("Skybox/Panoramic");
                if (panoramic != null)
                {
                    sky = new Material(panoramic) { name = "UsdDomeLight" };
                    sky.SetTexture("_MainTex", texture);
                    sky.SetFloat("_Mapping", 1f);   // latitude-longitude
                    sky.SetFloat("_ImageType", 0f); // 360 degrees
                    sky.SetFloat("_Rotation", 0f); // orientation is baked into the texture
                }
            }
            else if (string.IsNullOrEmpty(dome.TexturePath))
            {
                // An untextured dome is a uniform environment of its colour.
                Shader cubeShader = Shader.Find("Skybox/Cubemap");
                if (cubeShader != null)
                {
                    sky = new Material(cubeShader) { name = "UsdDomeLight" };
                    sky.SetTexture("_Tex", UniformCube());
                }
            }

            if (sky == null)
            {
                // A texture that could not be read: keep the old flat ambient so the scene is lit.
                RenderSettings.ambientMode = AmbientMode.Flat;
                RenderSettings.ambientLight = dome.Color * exposure;
                return false;
            }

            // _Exposure is a [Gamma] property: in a Linear project Unity raises it to ~2.2 before the
            // shader sees it (measured on the bench: domes of 300/1000/3000 needed 0.45/0.8/1.5).
            sky.SetFloat("_Exposure", QualitySettings.activeColorSpace == ColorSpace.Linear
                ? Mathf.LinearToGammaSpace(exposure)
                : exposure);
            sky.SetColor("_Tint", new Color(0.5f * dome.Color.r, 0.5f * dome.Color.g, 0.5f * dome.Color.b, 1f));

            RenderSettings.skybox = sky;
            RenderSettings.ambientMode = AmbientMode.Skybox;
            RenderSettings.ambientIntensity = UsdLightUnits.DomeAmbient;
            RenderSettings.defaultReflectionMode = DefaultReflectionMode.Skybox;
            RenderSettings.reflectionIntensity = 1f;
            DynamicGI.UpdateEnvironment();
            // The reflection cubemap is rendered later (RefreshSkyReflection, after the import's
            // first frame): see UsdDeferredRender for why nothing renders during the import.
            return true;
        }

        // Ambient follows the skybox through UpdateEnvironment; the reflection cubemap does not
        // without a lighting bake, so the skybox is rendered to a cubemap and used directly.
        public static void RefreshSkyReflection() => TryRefreshSkyReflection();

        // Renders the skybox into the reflection cubemap and checks it: false when the capture came
        // back black (no light in its 1x1 mip) under a sky that is not. Captures in the first frames
        // after an import did that on the bench's uniform dome -- every face zero, while the same
        // call later filled it -- and every chrome and gold sphere reflected black; the importer
        // retries on the next frames until one takes.
        public static bool TryRefreshSkyReflection()
        {
            DynamicGI.UpdateEnvironment();
            if (RenderSettings.skybox == null)
            {
                return true;
            }

            var cube = new RenderTexture(256, 256, 16, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear)
            {
                dimension = TextureDimension.Cube,
                useMipMap = true,
                autoGenerateMips = true,
                name = "UsdDomeReflection",
            };
            var go = new GameObject("UsdDomeCapture") { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                var capture = go.AddComponent<Camera>();
                capture.enabled = false;
                capture.cullingMask = 0;
                capture.clearFlags = CameraClearFlags.Skybox;
                if (!capture.RenderToCubemap(cube))
                {
                    return false;
                }

                if (!HasLight(cube) && RenderSettings.skybox.HasProperty("_Exposure") &&
                    RenderSettings.skybox.GetFloat("_Exposure") > 0f)
                {
                    cube.Release();
                    UnityEngine.Object.DestroyImmediate(cube);
                    return false;
                }

                RenderSettings.defaultReflectionMode = DefaultReflectionMode.Custom;
                RenderSettings.customReflectionTexture = Prefiltered(cube);
                return true;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        // The capture with its mips convolved for roughness (UsdReflectionConvolution): the render
        // texture's automatic mips are box-filtered, so rough surfaces reflected the environment
        // nearly as sharply as a mirror. The render texture is released once the convolved copy
        // exists; if it cannot be read back here, it is used as it is.
        private static Texture Prefiltered(RenderTexture cube)
        {

            Cubemap convolved = UsdReflectionConvolution.Convolve(cube, Math.Min(cube.width, 256));
            if (convolved == null)
            {
                return cube;
            }

            convolved.name = cube.name;
            cube.Release();
            UnityEngine.Object.DestroyImmediate(cube);
            return convolved;
        }

        // Whether a captured cubemap holds any light: its +Y face's 1x1 mip, read back.
        private static bool HasLight(RenderTexture cube)
        {
            int last = cube.mipmapCount - 1;
            var texel = RenderTexture.GetTemporary(1, 1, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
            var previous = RenderTexture.active;
            var read = new Texture2D(1, 1, TextureFormat.RGBAHalf, false, true);
            try
            {
                Graphics.CopyTexture(cube, (int)CubemapFace.PositiveY, last, texel, 0, 0);
                RenderTexture.active = texel;
                read.ReadPixels(new Rect(0, 0, 1, 1), 0, 0);
                read.Apply();
                Color c = read.GetPixel(0, 0);
                return c.r + c.g + c.b > 1e-6f;
            }
            catch (Exception)
            {
                return true; // cannot check here: keep the capture
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(texel);
                UnityEngine.Object.DestroyImmediate(read);
            }
        }

        private static Cubemap s_uniformCube;

        private static Cubemap UniformCube()
        {
            if (s_uniformCube != null)
            {
                return s_uniformCube;
            }

            s_uniformCube = new Cubemap(16, TextureFormat.RGBAHalf, false) { name = "UsdUniformDome" };
            var pixels = new Color[16 * 16];
            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i] = Color.white;
            }

            foreach (CubemapFace face in new[] { CubemapFace.PositiveX, CubemapFace.NegativeX, CubemapFace.PositiveY,
                                                 CubemapFace.NegativeY, CubemapFace.PositiveZ, CubemapFace.NegativeZ })
            {
                s_uniformCube.SetPixels(pixels, face);
            }

            s_uniformCube.Apply();
            return s_uniformCube;
        }

        // OpenEXR and Radiance HDR keep their range (half floats); PNG and JPG go through the
        // importer's guarded loader. Anything else, or a file over the size cap, returns null.
        private const long MaxEnvironmentBytes = 256L * 1024 * 1024;

        // The rotation from a Unity world direction to the direction Unity's panoramic skybox must
        // sample to show what Isaac Sim shows there.
        //
        // Isaac Sim (RTX) reads a latlong dome in the dome's own space with its pole on Z, whatever
        // the stage's upAxis. The importer maps a Z-up stage to Unity with Euler(-90, 0, 0) on the
        // root (and USD's X negated below it), and on that mapping Unity's panoramic skybox lines up
        // with Isaac's (measured on the look bench). So a dome direction u, in the dome node's Unity
        // space, is Isaac's latlong sample at Euler(-90, 0, 0) * u, and u = Inverse(node) * world.
        // An unrotated dome on a Z-up stage gives the identity; a Y-up stage turns the sky by 90
        // degrees, which is what Isaac shows for one (its sky stands on the stage's Z axis).
        private static Quaternion DomeOrientation(Transform node)
        {
            Quaternion world = node != null ? node.rotation : Quaternion.identity;
            return Quaternion.Euler(-90f, 0f, 0f) * Quaternion.Inverse(world);
        }

        // Unity's Skybox/Panoramic latlong: u = 0.5 - atan2(z, x) / 2pi, v = 1 - acos(y) / pi, with
        // row 0 at the bottom (v = 0).
        private static float[] BakeOrientation(float[] src, int w, int h, Quaternion q)
        {
            var dst = new float[src.Length];
            for (int j = 0; j < h; j++)
            {
                float lat = (1f - (j + 0.5f) / h) * Mathf.PI;
                float sinLat = Mathf.Sin(lat), cosLat = Mathf.Cos(lat);
                for (int i = 0; i < w; i++)
                {
                    float lon = (0.5f - (i + 0.5f) / w) * 2f * Mathf.PI;
                    var dir = new Vector3(sinLat * Mathf.Cos(lon), cosLat, sinLat * Mathf.Sin(lon));
                    Vector3 p = q * dir;
                    float u = 0.5f - Mathf.Atan2(p.z, p.x) / (2f * Mathf.PI);
                    float v = 1f - Mathf.Acos(Mathf.Clamp(p.y, -1f, 1f)) / Mathf.PI;
                    Sample(src, w, h, u * w - 0.5f, v * h - 0.5f, dst, (j * w + i) * 4);
                }
            }

            return dst;
        }

        // Bilinear, wrapping in u and clamped in v.
        private static void Sample(float[] src, int w, int h, float x, float y, float[] dst, int at)
        {
            int x0 = Mathf.FloorToInt(x), y0 = Mathf.FloorToInt(y);
            float fx = x - x0, fy = y - y0;
            int xa = ((x0 % w) + w) % w, xb = (xa + 1) % w;
            int ya = Mathf.Clamp(y0, 0, h - 1), yb = Mathf.Clamp(y0 + 1, 0, h - 1);
            for (int c = 0; c < 4; c++)
            {
                float a = src[(ya * w + xa) * 4 + c], b = src[(ya * w + xb) * 4 + c];
                float d = src[(yb * w + xa) * 4 + c], e = src[(yb * w + xb) * 4 + c];
                dst[at + c] = Mathf.Lerp(Mathf.Lerp(a, b, fx), Mathf.Lerp(d, e, fx), fy);
            }
        }

        private static Texture2D LoadEnvironmentTexture(string path, Quaternion orientation)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                return null;
            }

            string extension = Path.GetExtension(path).ToLowerInvariant();
            bool rotated = Quaternion.Angle(orientation, Quaternion.identity) > 0.01f;
            if (extension == ".png" || extension == ".jpg" || extension == ".jpeg")
            {
                if (rotated)
                {
                    Debug.LogWarning($"USD import: dome light texture '{path}' is a PNG or JPG on a rotated dome or a Y-up stage; its orientation is not corrected (EXR and HDR are).");
                }

                return UsdImporter.LoadImageFile(path);
            }

            if (extension != ".exr" && extension != ".hdr")
            {
                Debug.LogWarning($"USD import: dome light texture '{path}' is not an EXR, HDR, PNG or JPG; the dome is lit flat.");
                return null;
            }

            if (new FileInfo(path).Length > MaxEnvironmentBytes)
            {
                Debug.LogWarning($"USD import: dome light texture '{path}' is larger than {MaxEnvironmentBytes >> 20} MB; the dome is lit flat.");
                return null;
            }

            byte[] bytes = File.ReadAllBytes(path);
            int width, height;
            float[] rgba;
            string reason;
            bool ok = extension == ".exr"
                ? UsdExrDecoder.TryDecodeFloat(bytes, out width, out height, out rgba, out reason)
                : UsdHdrDecoder.TryDecode(bytes, out width, out height, out rgba, out reason);
            if (!ok)
            {
                Debug.LogWarning($"USD import: could not read dome light texture '{path}': {reason}.");
                return null;
            }

            if ((long)width * height > (long)SystemInfo.maxTextureSize * SystemInfo.maxTextureSize ||
                width > SystemInfo.maxTextureSize || height > SystemInfo.maxTextureSize)
            {
                Debug.LogWarning($"USD import: dome light texture '{path}' ({width}x{height}) exceeds the device limit.");
                return null;
            }

            if (rotated)
            {
                rgba = BakeOrientation(rgba, width, height, orientation);
            }

            var halves = new ushort[rgba.Length];
            for (int i = 0; i < rgba.Length; i++)
            {
                halves[i] = Mathf.FloatToHalf(rgba[i]);
            }

            var texture = new Texture2D(width, height, TextureFormat.RGBAHalf, true, true)
            {
                name = Path.GetFileNameWithoutExtension(path),
                wrapModeU = TextureWrapMode.Repeat,
                wrapModeV = TextureWrapMode.Clamp,
            };
            texture.SetPixelData(halves, 0);
            texture.Apply(true, true);
            return texture;
        }

        // Light component values for a USD light under the calibrated units. worldScale is the
        // stage unit in metres at the light (the node's lossy scale).
        public static void ConfigurePhysical(Light light, RUsdImportLight usd, float worldScale)
        {
            float intensity = Mathf.Max(0f, usd.Intensity);
            light.shadows = LightShadows.Soft;
            switch (usd.Type)
            {
                case 1: // distant
                    light.type = LightType.Directional;
                    light.intensity = intensity * UsdLightUnits.Distant;
                    break;
                default: // sphere, disk, cylinder (rect is handled by BuildRectGrid)
                {
                    float r = Mathf.Max(0f, usd.Radius) * worldScale;
                    float area = usd.Type == 5 // cylinder: projected side
                        ? 2f * r * Mathf.Max(0f, usd.Length) * worldScale
                        : Mathf.PI * r * r;
                    if (usd.Normalize != 0)
                    {
                        area = 1f;
                    }

                    light.intensity = intensity * area * UsdLightUnits.SpherePerSquareMetre;
                    if (usd.ConeAngle > 0f)
                    {
                        light.type = LightType.Spot;
                        light.spotAngle = Mathf.Clamp(usd.ConeAngle * 2f, 1f, 179f);
                        light.innerSpotAngle = light.spotAngle * Mathf.Clamp01(1f - usd.ConeSoftness);
                    }
                    else
                    {
                        light.type = LightType.Point;
                    }

                    // Isaac Sim's lights have no range. A URP light fades to zero at Light.range, so
                    // a range set by size alone (30 m for NVIDIA's Factory fixtures) cut the
                    // strong ones off across the hall. The range now reaches where the light's
                    // illuminance falls to RangeCutoff.
                    float strength = light.intensity * Mathf.Max(usd.ColorR, Mathf.Max(usd.ColorG, usd.ColorB));
                    light.range = Mathf.Max(Mathf.Max(30f, 40f * Mathf.Sqrt(area)), Mathf.Sqrt(Mathf.Max(0f, strength) / UsdLightUnits.RangeCutoff));
                    break;
                }
            }
        }

        // URP has no real-time area light. A rect light becomes a grid of point lights, about
        // RectCellSize apart, each carrying luminance x its share of the area and a cube-map cookie
        // that shapes it into the cosine lobe a Lambertian emitter has (nothing upward). A spot
        // did this before with a 179-degree cone, but a spot's one shadow map covers that cone so
        // coarsely that it shadowed what the light reached: on the Factory's mezzanine the wall
        // came out at 0.31 x Isaac Sim and the floor at 0.50 x, and narrowing the cone to recover
        // the shadow map left the far side unlit. A point light's six shadow faces never degenerate;
        // with the cookie both views sit within 0.75-1.9 x Isaac Sim. Measured on NVIDIA's
        // Factory (20 x 15 m at 8.35 m): a single light left the corners dark, a 4 x 3 grid matched.
        // The cookie needs the render pipeline's light cookies enabled (URP: on by default).
        public static void BuildRectGrid(Transform node, RUsdImportLight usd, Color color, float worldScale)
        {
            float w = Mathf.Max(1e-4f, usd.Width), h = Mathf.Max(1e-4f, usd.Height);
            float wm = w * worldScale, hm = h * worldScale;
            int nx = Mathf.Clamp(Mathf.RoundToInt(wm / UsdLightUnits.RectCellSize), 1, 8);
            int ny = Mathf.Clamp(Mathf.RoundToInt(hm / UsdLightUnits.RectCellSize), 1, 8);
            float cellArea = usd.Normalize != 0 ? 1f / (nx * ny) : (wm / nx) * (hm / ny);
            float intensity = Mathf.Max(0f, usd.Intensity) * cellArea * UsdLightUnits.RectPerSquareMetre;
            float range = Mathf.Max(10f, 2.5f * Mathf.Max(wm, hm));
            Cubemap lobe = RectLobeCookie();

            // The bounce: Surface Cache GI ignores cookies, so the grid's point lights fed it the
            // whole sphere, the ceiling above each fixture included -- four times the flux of the
            // lobe they draw, half of it upward. NVIDIA's Simple_Warehouse came out 5 x Isaac Sim
            // with its roof lit white (10-20). The grid lights the scene directly and bounces
            // nothing; one bounce-only spot at the rect's centre, facing the same way, carries the
            // light into the surface cache with the lobe's flux.
            GameObject giHelper = UsdImporter.CreateLightHelper(node, "UsdLightGI");
            Light gi = giHelper.AddComponent<Light>();
            gi.type = LightType.Spot;
            gi.spotAngle = UsdLightUnits.RectGiCone;
            gi.innerSpotAngle = UsdLightUnits.RectGiCone;
            gi.color = color;
            gi.intensity = intensity * nx * ny * UsdLightUnits.GiOnlyScale;
            gi.bounceIntensity = 1f / UsdLightUnits.GiOnlyScale;
            gi.range = range;
            gi.shadows = LightShadows.None;
            for (int ix = 0; ix < nx; ix++)
            {
                for (int iy = 0; iy < ny; iy++)
                {
                    GameObject helper = UsdImporter.CreateLightHelper(node, nx * ny > 1 ? $"UsdLight_{ix}_{iy}" : "UsdLight");
                    // In the node's USD space the rect lies in XY and emits along -Z; the helper's
                    // 180 degree turn makes its +Z that direction, which is the cookie's lobe axis.
                    helper.transform.localPosition = new Vector3(w * ((ix + 0.5f) / nx - 0.5f), h * ((iy + 0.5f) / ny - 0.5f), 0f);
                    Light light = helper.AddComponent<Light>();
                    light.type = LightType.Point;
                    light.color = color;
                    light.intensity = intensity;
                    light.range = range;
                    light.shadows = LightShadows.Soft;
                    light.cookie = lobe;
                    light.bounceIntensity = 0f;
                }
            }
        }

        // A shadowed point light renders the stage once per cube face. NVIDIA's Factory (40 M
        // triangles, 15 sphere lights) spent about 24 ms a frame on each, 90 % of the frame, while
        // switching their shadows off changed the hall by 0-2 %: a fixture 7 m up lights the
        // floor around it, and what shades that floor is mostly the rect light's grid and the
        // ambient occlusion. Sphere, disk and cylinder lights keep their shadows only for the
        // MaxShadowedPointLights strongest; ties go to the lights nearest the middle of the set,
        // which reach the most of the stage. Rect grids and the sun keep theirs.
        //
        // Then the casters: every shadow face draws every renderer in range, and two thirds of the
        // Factory's 12,219 renderers are bolts, clips and cables under half a metre whose shadow
        // falls inside a texel or two of a point light's map. A renderer smaller than
        // SmallCasterFraction of the stage's diagonal casts no shadow (it still receives them):
        // the Factory's frame went from 180-380 ms to 70-210 ms, with the wide view and the cell
        // interior unchanged against Isaac Sim. Relative to the stage, so a chess set keeps its
        // pieces' shadows.
        public static void ApplyShadowBudget(Transform root)
        {
            DropSmallShadowCasters(root);

            var candidates = new List<Light>();
            Vector3 centre = Vector3.zero;
            foreach (Light light in root.GetComponentsInChildren<Light>(true))
            {
                if (light.name != "UsdLight" || light.cookie != null || light.shadows == LightShadows.None ||
                    (light.type != LightType.Point && light.type != LightType.Spot))
                {
                    continue;
                }

                candidates.Add(light);
                centre += light.transform.position;
            }

            if (candidates.Count <= UsdLightUnits.MaxShadowedPointLights)
            {
                return;
            }

            centre /= candidates.Count;
            candidates.Sort((a, b) =>
            {
                float pa = a.intensity * a.color.maxColorComponent, pb = b.intensity * b.color.maxColorComponent;
                if (!Mathf.Approximately(pa, pb))
                {
                    return pb.CompareTo(pa);
                }

                return (a.transform.position - centre).sqrMagnitude.CompareTo((b.transform.position - centre).sqrMagnitude);
            });
            for (int i = UsdLightUnits.MaxShadowedPointLights; i < candidates.Count; i++)
            {
                candidates[i].shadows = LightShadows.None;
            }
        }

        static void DropSmallShadowCasters(Transform root)
        {
            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0)
            {
                return;
            }

            Bounds stage = renderers[0].bounds;
            foreach (Renderer renderer in renderers)
            {
                stage.Encapsulate(renderer.bounds);
            }

            float cut = stage.size.magnitude * UsdLightUnits.SmallCasterFraction;
            foreach (Renderer renderer in renderers)
            {
                if (renderer.shadowCastingMode == ShadowCastingMode.On && renderer.bounds.size.magnitude < cut)
                {
                    renderer.shadowCastingMode = ShadowCastingMode.Off;
                }
            }
        }

        static Cubemap s_rectLobeCookie;

        // One shared 64 px cube map holding max(0, cos theta) about the light's local +Z: the
        // angular distribution of a flat Lambertian emitter, zero behind it. Verified on the
        // Factory by flipping the lobe to -Z, which left only the roof-bounced light.
        static Cubemap RectLobeCookie()
        {
            if (s_rectLobeCookie != null)
            {
                return s_rectLobeCookie;
            }

            const int size = 64;
            var cube = new Cubemap(size, TextureFormat.RGBA32, false)
            {
                name = "UsdRectLobeCookie",
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave,
            };
            var pixels = new Color[size * size];
            for (int face = 0; face < 6; face++)
            {
                for (int y = 0; y < size; y++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        float u = (x + 0.5f) / size * 2f - 1f;
                        float v = (y + 0.5f) / size * 2f - 1f;
                        float c = Mathf.Max(0f, CubeFaceDirection((CubemapFace)face, u, v).normalized.z);
                        pixels[y * size + x] = new Color(c, c, c, 1f);
                    }
                }

                cube.SetPixels(pixels, (CubemapFace)face);
            }

            cube.Apply(false, true);
            s_rectLobeCookie = cube;
            return cube;
        }

        // Direction a cube-map texel looks along, in Unity's cube-map convention (+X face: u
        // runs toward -Z, v toward -Y, and so on for the others).
        static Vector3 CubeFaceDirection(CubemapFace face, float u, float v)
        {
            switch (face)
            {
                case CubemapFace.PositiveX: return new Vector3(1f, -v, -u);
                case CubemapFace.NegativeX: return new Vector3(-1f, -v, u);
                case CubemapFace.PositiveY: return new Vector3(u, 1f, v);
                case CubemapFace.NegativeY: return new Vector3(u, -1f, -v);
                case CubemapFace.PositiveZ: return new Vector3(u, -v, 1f);
                default: return new Vector3(-u, -v, -1f);
            }
        }

        // True when the stage has a roof: roof-like renderers (flatter than half their width) lying
        // wholly above the interior capture point (1.5 m over the stage's lowest point) cover at
        // least half of the stage's footprint. Overlaps are counted twice, so this errs towards
        // "enclosed" for a cluttered ceiling. Only flat parts count: a stage authored in
        // centimetres and read as metres (Vehicle Variants, a 3 m tractor 300 m tall) has its cab
        // and body above 1.5 m, and they covered 70% of its footprint, so the interior reflection
        // was captured from inside the bodywork and the tractor reflected black under a dome.
        // Warehouse ceiling panels and the Factory's roof are flat and still count (1.37 and 3.13
        // of the footprint).
        public static bool LooksEnclosed(Transform root)
        {
            if (root == null)
            {
                return false;
            }

            bool any = false;
            Bounds bounds = default;
            var renderers = root.GetComponentsInChildren<Renderer>();
            foreach (Renderer renderer in renderers)
            {
                if (!renderer.enabled)
                {
                    continue;
                }

                if (any) bounds.Encapsulate(renderer.bounds); else { bounds = renderer.bounds; any = true; }
            }

            float footprint = bounds.size.x * bounds.size.z;
            if (!any || footprint <= 0f)
            {
                return false;
            }

            float eye = bounds.min.y + 1.5f;
            float covered = 0f;
            foreach (Renderer renderer in renderers)
            {
                Vector3 size = renderer.bounds.size;
                if (renderer.enabled && renderer.bounds.min.y > eye && size.y < 0.5f * Mathf.Max(size.x, size.z))
                {
                    covered += size.x * size.z;
                }
            }

            return covered >= 0.5f * footprint;
        }

        // The scene's environment reflection, captured from inside the stage: at the horizontal
        // centre of everything under root, 1.5 m above its lowest point. Industrial stages are
        // looked at from inside (a factory, a warehouse), where a sky-only reflection shows blue in
        // every metal; a probe there shows the hall. Known limitation: a single probe has no
        // parallax, and surfaces outside the building (a roof seen from above) reflect the
        // interior too. Call again after changing the lights; the capture is a snapshot.
        public static bool CaptureInteriorReflection(Transform root, int resolution = 256)
        {
            if (root == null)
            {
                return false;
            }

            bool any = false;
            Bounds bounds = default;
            foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>())
            {
                if (!renderer.enabled)
                {
                    continue;
                }

                if (!any)
                {
                    bounds = renderer.bounds;
                    any = true;
                }
                else
                {
                    bounds.Encapsulate(renderer.bounds);
                }
            }

            if (!any)
            {
                return false;
            }

            Vector3 point = new Vector3(bounds.center.x, bounds.min.y + 1.5f, bounds.center.z);
            var cube = new RenderTexture(resolution, resolution, 24, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear)
            {
                dimension = TextureDimension.Cube,
                useMipMap = true,
                autoGenerateMips = true,
                name = "UsdInteriorReflection",
            };
            var go = new GameObject("UsdReflectionCapture") { hideFlags = HideFlags.HideAndDontSave };
            // Whatever reflection the scene has while the capture renders ends up inside the
            // capture: the first one after import reflects the sky in every interior surface,
            // and lit the Factory's shade 6-9 x too bright under dome + sun. Repeating the capture
            // on later frames, each with the previous one as the scene's reflection, converges in
            // two or three steps to the hall's own specular bounce, which matched Isaac Sim best
            // under full lighting (0.94 x on the car line). The passes must be frames apart: a
            // second RenderToCubemap in the same frame does not see the cube map just assigned to
            // RenderSettings and renders with no reflection at all (0.44 x on a chrome car body).
            // So this does one pass; the importer chains three across frames (UsdDeferredRender),
            // and a caller that changes the lights calls RefreshReflection on consecutive frames.
            try
            {
                go.transform.position = point;
                var capture = go.AddComponent<Camera>();
                capture.enabled = false;
                capture.nearClipPlane = 0.05f;
                capture.farClipPlane = Mathf.Max(100f, bounds.size.magnitude * 2f);
                capture.clearFlags = RenderSettings.skybox != null ? CameraClearFlags.Skybox : CameraClearFlags.SolidColor;
                capture.backgroundColor = Color.black;
                if (!capture.RenderToCubemap(cube))
                {
                    return false;
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }

            RenderSettings.defaultReflectionMode = DefaultReflectionMode.Custom;
            RenderSettings.customReflectionTexture = Prefiltered(cube);
            RenderSettings.reflectionIntensity = 1f;
            return true;
        }

        // URP shadows one directional light, the main one; every other directional light lights
        // straight through walls and roofs. Isaac Sim shadows all of them: NVIDIA's
        // Simple_Warehouse has two low suns, and the unshadowed one lit its closed interior (2 x
        // Isaac's level, where Isaac with the suns alone is black). The first distant light stays
        // directional; each further one becomes a spot light far away along its direction, aimed
        // at the stage, with a cone just covering it and its intensity times the distance squared
        // (URP's inverse-square falloff), so it lights the stage as the distant light would, within
        // about 10% from the near side to the far, and casts shadows. Returns how many it moved.
        public static int ShadowExtraSuns(Transform root)
        {
            var suns = new List<Light>();
            foreach (Light light in root.GetComponentsInChildren<Light>(true))
            {
                if (light.type == LightType.Directional && light.isActiveAndEnabled)
                {
                    suns.Add(light);
                }
            }

            if (suns.Count < 2)
            {
                return 0;
            }

            bool any = false;
            Bounds bounds = default;
            foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>())
            {
                if (!renderer.enabled)
                {
                    continue;
                }

                if (any) bounds.Encapsulate(renderer.bounds); else { bounds = renderer.bounds; any = true; }
            }

            if (!any)
            {
                return 0;
            }

            // The brightest stays the directional light URP shadows.
            suns.Sort((x, y) => y.intensity.CompareTo(x.intensity));
            float radius = Mathf.Max(0.5f, bounds.extents.magnitude);
            float distance = 10f * radius * 2f;
            int moved = 0;
            for (int i = 1; i < suns.Count; i++)
            {
                Light sun = suns[i];
                Vector3 direction = sun.transform.forward;
                sun.transform.position = bounds.center - direction * distance;
                sun.transform.rotation = Quaternion.LookRotation(direction, Mathf.Abs(Vector3.Dot(direction, Vector3.up)) > 0.99f ? Vector3.forward : Vector3.up);
                sun.type = LightType.Spot;
                sun.spotAngle = Mathf.Min(179f, 2f * Mathf.Rad2Deg * Mathf.Atan(radius * 1.1f / distance));
                sun.innerSpotAngle = sun.spotAngle * 0.9f;
                sun.range = distance + 2f * radius;
                sun.intensity *= distance * distance;
                sun.shadows = LightShadows.Soft;
                moved++;
            }

            return moved;
        }

        // A shadowless point light at each emissive submesh, sized by its emitting area.
        public static int BuildEmissiveLights(Transform root)
        {
            int made = 0;
            foreach (MeshRenderer renderer in root.GetComponentsInChildren<MeshRenderer>(true))
            {
                MeshFilter filter = renderer.GetComponent<MeshFilter>();
                Mesh mesh = filter != null ? filter.sharedMesh : null;
                if (mesh == null)
                {
                    continue;
                }

                Material[] materials = renderer.sharedMaterials;
                Matrix4x4 toWorld = renderer.transform.localToWorldMatrix;
                Vector3[] vertices = null;
                for (int s = 0; s < mesh.subMeshCount && s < materials.Length; s++)
                {
                    Material material = materials[s];
                    if (material == null || !material.IsKeywordEnabled("_EMISSION") || !material.HasProperty("_EmissionColor"))
                    {
                        continue;
                    }

                    Color emission = material.GetColor("_EmissionColor");
                    float peak = Mathf.Max(emission.r, Mathf.Max(emission.g, emission.b));
                    if (peak < 0.01f)
                    {
                        continue;
                    }

                    float area = 0f;
                    Vector3 centre = Vector3.zero;
                    if (mesh.isReadable)
                    {
                        vertices = vertices ?? mesh.vertices;
                        int[] triangles = mesh.GetTriangles(s);
                        for (int i = 0; i + 2 < triangles.Length; i += 3)
                        {
                            Vector3 a = toWorld.MultiplyPoint3x4(vertices[triangles[i]]);
                            Vector3 b = toWorld.MultiplyPoint3x4(vertices[triangles[i + 1]]);
                            Vector3 c = toWorld.MultiplyPoint3x4(vertices[triangles[i + 2]]);
                            float t = 0.5f * Vector3.Cross(b - a, c - a).magnitude;
                            area += t;
                            centre += (a + b + c) / 3f * t;
                        }

                        centre = area > 0f ? centre / area : renderer.bounds.center;
                    }
                    else
                    {
                        Bounds local = mesh.GetSubMesh(s).bounds;
                        Vector3 size = Vector3.Scale(local.size, renderer.transform.lossyScale);
                        area = 2f * (size.x * size.y + size.y * size.z + size.z * size.x);
                        centre = toWorld.MultiplyPoint3x4(local.center);
                    }

                    if (area <= 0f)
                    {
                        continue;
                    }

                    var helper = new GameObject("UsdEmissiveLight");
                    helper.transform.SetParent(renderer.transform, false);
                    helper.transform.position = centre;
                    helper.AddComponent<UsdImportHelperObject>();
                    Light light = helper.AddComponent<Light>();
                    light.type = LightType.Point;
                    light.color = UsdImporter.UsdLightColor(new Color(emission.r / peak, emission.g / peak, emission.b / peak, 1f));
                    light.intensity = UsdLightUnits.EmissivePerSquareMetre * peak * area;
                    light.range = Mathf.Max(5f, 20f * Mathf.Sqrt(area));
                    light.shadows = LightShadows.None;
                    made++;
                }
            }

            return made;
        }
    }

    // Radiance RGBE (.hdr): flat or new-style run-length scanlines. Output is linear float RGBA,
    // row 0 = bottom, like the other decoders.
    internal static class UsdHdrDecoder
    {
        public static bool TryDecode(byte[] data, out int width, out int height, out float[] rgba, out string reason)
        {
            width = height = 0;
            rgba = null;
            reason = null;
            if (data == null || data.Length < 16 || data[0] != (byte)'#' || data[1] != (byte)'?')
            {
                reason = "not a Radiance HDR file";
                return false;
            }

            int at = 0;
            bool rgbe = false;
            while (true)
            {
                string line = ReadLine(data, ref at);
                if (line == null)
                {
                    reason = "Radiance header ends early";
                    return false;
                }

                if (line.Length == 0)
                {
                    break;
                }

                if (line.StartsWith("FORMAT=", StringComparison.Ordinal))
                {
                    rgbe = line == "FORMAT=32-bit_rle_rgbe";
                }
            }

            if (!rgbe)
            {
                reason = "only 32-bit_rle_rgbe is supported";
                return false;
            }

            string[] size = (ReadLine(data, ref at) ?? string.Empty).Split(' ');
            if (size.Length != 4 || size[0] != "-Y" || size[2] != "+X" ||
                !int.TryParse(size[1], out height) || !int.TryParse(size[3], out width) ||
                width <= 0 || height <= 0 || width > 32768 || height > 32768)
            {
                reason = "unsupported Radiance resolution line";
                return false;
            }

            rgba = new float[(long)width * height * 4];
            var scan = new byte[width * 4];
            for (int y = 0; y < height; y++)
            {
                if (!ReadScanline(data, ref at, scan, width))
                {
                    reason = "Radiance scanline runs past the end of the file";
                    rgba = null;
                    return false;
                }

                long row = (long)(height - 1 - y) * width * 4;
                for (int x = 0; x < width; x++)
                {
                    int e = scan[x * 4 + 3];
                    float f = e == 0 ? 0f : (float)Math.Pow(2.0, e - 136);
                    rgba[row + x * 4] = scan[x * 4] * f;
                    rgba[row + x * 4 + 1] = scan[x * 4 + 1] * f;
                    rgba[row + x * 4 + 2] = scan[x * 4 + 2] * f;
                    rgba[row + x * 4 + 3] = 1f;
                }
            }

            return true;
        }

        private static string ReadLine(byte[] data, ref int at)
        {
            int start = at;
            while (at < data.Length && data[at] != (byte)'\n')
            {
                at++;
            }

            if (at >= data.Length)
            {
                return null;
            }

            string line = System.Text.Encoding.ASCII.GetString(data, start, at - start).TrimEnd('\r');
            at++;
            return line;
        }

        private static bool ReadScanline(byte[] data, ref int at, byte[] scan, int width)
        {
            if (at + 4 > data.Length)
            {
                return false;
            }

            bool rle = width >= 8 && width < 32768 && data[at] == 2 && data[at + 1] == 2 &&
                       ((data[at + 2] << 8) | data[at + 3]) == width && (data[at + 2] & 0x80) == 0;
            if (!rle)
            {
                if (at + width * 4 > data.Length)
                {
                    return false;
                }

                Buffer.BlockCopy(data, at, scan, 0, width * 4);
                at += width * 4;
                return true;
            }

            at += 4;
            for (int channel = 0; channel < 4; channel++)
            {
                int x = 0;
                while (x < width)
                {
                    if (at >= data.Length)
                    {
                        return false;
                    }

                    int count = data[at++];
                    if (count > 128)
                    {
                        count -= 128;
                        if (at >= data.Length || x + count > width)
                        {
                            return false;
                        }

                        byte value = data[at++];
                        for (int i = 0; i < count; i++)
                        {
                            scan[(x++) * 4 + channel] = value;
                        }
                    }
                    else
                    {
                        if (count == 0 || at + count > data.Length || x + count > width)
                        {
                            return false;
                        }

                        for (int i = 0; i < count; i++)
                        {
                            scan[(x++) * 4 + channel] = data[at++];
                        }
                    }
                }
            }

            return true;
        }
    }
}
