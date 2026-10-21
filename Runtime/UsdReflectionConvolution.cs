using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace Unity.USDToolkit
{
    // Prefilters a captured reflection cubemap for rough surfaces, the way Unity convolves its own
    // reflection probes. URP picks a cubemap mip from the surface's perceptual roughness
    // (perceptualRoughnessToMipmapLevel) and expects that mip to hold the environment already blurred
    // by the GGX lobe of that roughness. The importer's dome and interior captures are render
    // textures with automatic mips, which are box-filtered downsamples instead: at roughness 0.5 a
    // softbox in the HDRI stayed a sharp, bright patch, and an OmniPBR pallet (roughness 0.5) and a
    // warehouse floor reflected the studio like polished metal where Isaac Sim draws them matte
    // (pallet close-up, dome alone: Unity 2-4 x Isaac on the wood, 2026-10-20). Realtime reflection
    // probes would convolve on the GPU but do not render in the Editor outside Play mode.
    internal static class UsdReflectionConvolution
    {
        // URP's UNITY_SPECCUBE_LOD_STEPS: the mip that perceptual roughness 1 maps to.
        private const int LodSteps = 6;

        private const int SampleCount = 64;

        // A cubemap whose mip m holds `source` convolved with the GGX lobe for the roughness URP
        // reads from mip m; mip 0 is the source as it is (a mirror). `size` is the face size of
        // the result (the source is read at that size or the nearest mip above). Null when the
        // source cannot be read back here.
        public static Cubemap Convolve(RenderTexture source, int size)
        {
            if (source == null || source.dimension != TextureDimension.Cube)
            {
                return null;
            }

            int sourceMip = 0;
            while ((source.width >> (sourceMip + 1)) >= size && sourceMip + 1 < source.mipmapCount)
            {
                sourceMip++;
            }

            size = Math.Max(1, source.width >> sourceMip);
            int levels = Math.Max(1, (int)Math.Log(size, 2) + 1);

            // The source pyramid, read once: level k is the source's mip sourceMip + k.
            var pyramid = new Vector3[levels][][];
            for (int k = 0; k < levels; k++)
            {
                int mip = sourceMip + k;
                if (mip >= source.mipmapCount)
                {
                    levels = k;
                    break;
                }

                pyramid[k] = new Vector3[6][];
                for (int face = 0; face < 6; face++)
                {
                    pyramid[k][face] = ReadFace(source, face, mip, Math.Max(1, size >> k));
                    if (pyramid[k][face] == null)
                    {
                        return null;
                    }
                }
            }

            var result = new Cubemap(size, GraphicsFormat.R16G16B16A16_SFloat, TextureCreationFlags.MipChain)
            {
                name = source.name + " (GGX)",
                hideFlags = HideFlags.DontSave,
                filterMode = FilterMode.Trilinear,
                wrapMode = TextureWrapMode.Clamp,
            };

            int resultMips = result.mipmapCount;
            float baseTexelSolidAngle = 4f * Mathf.PI / (6f * size * size);
            var samples = HammersleyGgxSamples(SampleCount);

            for (int mip = 0; mip < resultMips; mip++)
            {
                int faceSize = Math.Max(1, size >> mip);
                float perceptual = PerceptualRoughnessForMip(mip);
                float alpha = Math.Max(perceptual * perceptual, 1e-4f);
                var faces = new Color[6][];
                System.Threading.Tasks.Parallel.For(0, 6, face =>
                {
                    var pixels = new Color[faceSize * faceSize];
                    for (int y = 0; y < faceSize; y++)
                    {
                        for (int x = 0; x < faceSize; x++)
                        {
                            Vector3 n = Direction(face, x, y, faceSize);
                            Vector3 c = mip == 0 || levels <= 1
                                ? SampleLevel(pyramid[Math.Min(mip, levels - 1)], n, faceSize)
                                : Integrate(pyramid, levels, size, n, alpha, samples, baseTexelSolidAngle);
                            pixels[y * faceSize + x] = new Color(c.x, c.y, c.z, 1f);
                        }
                    }

                    faces[face] = pixels;
                });

                for (int face = 0; face < 6; face++)
                {
                    result.SetPixels(faces[face], (CubemapFace)face, mip);
                }
            }

            result.Apply(false, true);
            return result;
        }

        // The roughness URP reads from `mip`: the inverse of
        // mip = perceptualRoughness * (1.7 - 0.7 * perceptualRoughness) * UNITY_SPECCUBE_LOD_STEPS.
        internal static float PerceptualRoughnessForMip(int mip)
        {
            float m = Mathf.Clamp(mip / (float)LodSteps, 0f, 1f);
            return Mathf.Clamp01((1.7f - Mathf.Sqrt(Mathf.Max(0f, 2.89f - 2.8f * m))) / 1.4f);
        }

        // GGX-weighted average of the environment around n (n = v = r, the split-sum assumption),
        // each sample read from the pyramid level whose texel covers the sample's solid angle
        // (filtered importance sampling), so a few dozen samples do not alias.
        private static Vector3 Integrate(Vector3[][][] pyramid, int levels, int size, Vector3 n, float alpha,
                                         Vector2[] samples, float baseTexelSolidAngle)
        {
            Vector3 up = Mathf.Abs(n.z) < 0.999f ? Vector3.forward : Vector3.right;
            Vector3 tx = Vector3.Normalize(Vector3.Cross(up, n));
            Vector3 ty = Vector3.Cross(n, tx);
            Vector3 sum = Vector3.zero;
            float weight = 0f;
            float a2 = alpha * alpha;
            foreach (Vector2 s in samples)
            {
                float phi = 2f * Mathf.PI * s.x;
                float cosTheta = Mathf.Sqrt((1f - s.y) / (1f + (a2 - 1f) * s.y));
                float sinTheta = Mathf.Sqrt(Mathf.Max(0f, 1f - cosTheta * cosTheta));
                Vector3 h = tx * (sinTheta * Mathf.Cos(phi)) + ty * (sinTheta * Mathf.Sin(phi)) + n * cosTheta;
                float nDotH = cosTheta;
                Vector3 l = 2f * Vector3.Dot(n, h) * h - n;
                float nDotL = Vector3.Dot(n, l);
                if (nDotL <= 0f)
                {
                    continue;
                }

                float d = a2 / (Mathf.PI * Sq((nDotH * nDotH) * (a2 - 1f) + 1f));
                float pdf = d * 0.25f; // D * NdotH / (4 VdotH) with v = n
                float sampleSolidAngle = 1f / (SampleCount * Math.Max(pdf, 1e-6f));
                float lod = Mathf.Clamp(0.5f * Mathf.Log(sampleSolidAngle / baseTexelSolidAngle, 2f) + 1f, 0f, levels - 1);
                int level = Math.Min((int)lod, levels - 1);
                int next = Math.Min(level + 1, levels - 1);
                float t = lod - level;
                Vector3 c0 = SampleLevel(pyramid[level], l, Math.Max(1, size >> level));
                Vector3 c1 = next == level ? c0 : SampleLevel(pyramid[next], l, Math.Max(1, size >> next));
                sum += (c0 * (1f - t) + c1 * t) * nDotL;
                weight += nDotL;
            }

            return weight > 0f ? sum / weight : Vector3.zero;
        }

        private static float Sq(float x) => x * x;

        private static Vector2[] HammersleyGgxSamples(int count)
        {
            var samples = new Vector2[count];
            for (uint i = 0; i < count; i++)
            {
                uint bits = i;
                bits = (bits << 16) | (bits >> 16);
                bits = ((bits & 0x55555555u) << 1) | ((bits & 0xAAAAAAAAu) >> 1);
                bits = ((bits & 0x33333333u) << 2) | ((bits & 0xCCCCCCCCu) >> 2);
                bits = ((bits & 0x0F0F0F0Fu) << 4) | ((bits & 0xF0F0F0F0u) >> 4);
                bits = ((bits & 0x00FF00FFu) << 8) | ((bits & 0xFF00FF00u) >> 8);
                samples[i] = new Vector2(i / (float)count, bits * 2.3283064365386963e-10f);
            }

            return samples;
        }

        // The direction through texel (x, y) of a face, in the layout Cubemap.SetPixels and
        // ReadFace use (D3D/Unity face orientation).
        internal static Vector3 Direction(int face, int x, int y, int size)
        {
            float s = 2f * (x + 0.5f) / size - 1f;
            float t = 2f * (y + 0.5f) / size - 1f;
            Vector3 d;
            switch (face)
            {
                case 0: d = new Vector3(1f, -t, -s); break;  // +X
                case 1: d = new Vector3(-1f, -t, s); break;  // -X
                case 2: d = new Vector3(s, 1f, t); break;    // +Y
                case 3: d = new Vector3(s, -1f, -t); break;  // -Y
                case 4: d = new Vector3(s, -t, 1f); break;   // +Z
                default: d = new Vector3(-s, -t, -1f); break; // -Z
            }

            return d.normalized;
        }

        // The inverse of Direction, bilinear within the face (edges clamp to the face).
        private static Vector3 SampleLevel(Vector3[][] faces, Vector3 d, int size)
        {
            float ax = Mathf.Abs(d.x), ay = Mathf.Abs(d.y), az = Mathf.Abs(d.z);
            int face;
            float s, t, m;
            if (ax >= ay && ax >= az)
            {
                m = ax;
                if (d.x > 0f) { face = 0; s = -d.z; t = -d.y; }
                else { face = 1; s = d.z; t = -d.y; }
            }
            else if (ay >= az)
            {
                m = ay;
                if (d.y > 0f) { face = 2; s = d.x; t = d.z; }
                else { face = 3; s = d.x; t = -d.z; }
            }
            else
            {
                m = az;
                if (d.z > 0f) { face = 4; s = d.x; t = -d.y; }
                else { face = 5; s = -d.x; t = -d.y; }
            }

            float fx = ((s / m) + 1f) * 0.5f * size - 0.5f;
            float fy = ((t / m) + 1f) * 0.5f * size - 0.5f;
            int x0 = Mathf.Clamp((int)Mathf.Floor(fx), 0, size - 1), y0 = Mathf.Clamp((int)Mathf.Floor(fy), 0, size - 1);
            int x1 = Math.Min(x0 + 1, size - 1), y1 = Math.Min(y0 + 1, size - 1);
            float tx = Mathf.Clamp01(fx - x0), ty = Mathf.Clamp01(fy - y0);
            Vector3[] p = faces[face];
            Vector3 a = Vector3.Lerp(p[y0 * size + x0], p[y0 * size + x1], tx);
            Vector3 b = Vector3.Lerp(p[y1 * size + x0], p[y1 * size + x1], tx);
            return Vector3.Lerp(a, b, ty);
        }

        // One face mip of a render texture cube map as linear RGB, in the layout Cubemap.SetPixels
        // takes: a face read here and written back unchanged renders the same reflection (checked
        // on a chrome sphere, 2026-10-20), and Direction/SampleLevel agree with it across the 12
        // edges (relative jump 0.22 against 0.66 with the rows flipped).
        private static Vector3[] ReadFace(RenderTexture cube, int face, int mip, int size)
        {
            var flat = RenderTexture.GetTemporary(size, size, 0, GraphicsFormat.R16G16B16A16_SFloat);
            var read = new Texture2D(size, size, GraphicsFormat.R16G16B16A16_SFloat, TextureCreationFlags.None);
            var previous = RenderTexture.active;
            try
            {
                Graphics.CopyTexture(cube, face, mip, flat, 0, 0);
                RenderTexture.active = flat;
                read.ReadPixels(new Rect(0, 0, size, size), 0, 0);
                read.Apply();
                Color[] pixels = read.GetPixels();
                var result = new Vector3[pixels.Length];
                for (int i = 0; i < pixels.Length; i++)
                {
                    result[i] = new Vector3(pixels[i].r, pixels[i].g, pixels[i].b);
                }

                return result;
            }
            catch (Exception)
            {
                return null;
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(flat);
                UnityEngine.Object.DestroyImmediate(read);
            }
        }
    }
}
