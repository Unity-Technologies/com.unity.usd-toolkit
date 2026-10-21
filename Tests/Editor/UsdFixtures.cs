using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace Unity.USDToolkit.Tests
{
    /// <summary>
    /// The USD files under Tests/Fixtures~ and the expected values make_expected.py wrote for them.
    /// Fixtures~ is hidden from the AssetDatabase, so the files are read straight from disk.
    /// </summary>
    internal static class UsdFixtures
    {
        public static string PathOf(string folder, string file)
        {
            PackageInfo package = PackageInfo.FindForAssembly(typeof(UsdImporter).Assembly);
            Assert.That(package, Is.Not.Null, "Unity.USDToolkit is not loaded from a package.");
            string path = Path.Combine(package.resolvedPath, "Tests", "Fixtures~", folder, file);
            Assert.That(File.Exists(path), Is.True, $"Missing fixture {path}");
            return path;
        }

        public static ExpectedStage LoadExpected(string folder, string fixtureName)
        {
            string json = File.ReadAllText(PathOf(folder, fixtureName + ".expected.json"));
            return JsonUtility.FromJson<ExpectedStage>(json);
        }

        /// <summary>
        /// A point in stage coordinates, as it lands in Unity world space under an import root. The
        /// importer mirrors X for every prim and puts the up-axis and unit correction on the root.
        /// </summary>
        public static Vector3 StageToWorld(Transform importRoot, Vector3 stagePoint) =>
            importRoot.TransformPoint(new Vector3(-stagePoint.x, stagePoint.y, stagePoint.z));

        public static Vector3 Point(float[] flat, int index) =>
            new Vector3(flat[3 * index], flat[3 * index + 1], flat[3 * index + 2]);

        public static ExpectedPointsFrame FrameAt(ExpectedSkinnedMesh mesh, double time)
        {
            foreach (ExpectedPointsFrame frame in mesh.frames)
            {
                if (Math.Abs(frame.time - time) < 1e-9)
                {
                    return frame;
                }
            }

            Assert.Fail($"{mesh.path} has no expected frame at {time}.");
            return null;
        }

        /// <summary>
        /// For each vertex of an imported mesh, the USD point it was made from, found by position:
        /// the importer negates X, so the vertex at Unity (x, y, z) is the point at USD (-x, y, z).
        /// Splitting only duplicates points, so every vertex finds one. Fixture points are distinct.
        /// </summary>
        public static int[] PointOfEachVertex(Vector3[] vertices, float[] restPoints)
        {
            var result = new int[vertices.Length];
            int pointCount = restPoints.Length / 3;
            for (int v = 0; v < vertices.Length; v++)
            {
                var usd = new Vector3(-vertices[v].x, vertices[v].y, vertices[v].z);
                result[v] = -1;
                for (int p = 0; p < pointCount; p++)
                {
                    if ((Point(restPoints, p) - usd).sqrMagnitude < 1e-10f)
                    {
                        result[v] = p;
                        break;
                    }
                }

                Assert.That(result[v], Is.GreaterThanOrEqualTo(0), $"vertex {v} at {usd} matches no USD point");
            }

            return result;
        }
    }

    /// <summary>One synchronous import, destroyed with everything it created when disposed.</summary>
    internal sealed class ImportedStage : IDisposable
    {
        private ImportedStage(UsdImportResult result)
        {
            Result = result;
        }

        public UsdImportResult Result { get; }

        public Transform Root => Result.RootObject.transform;

        /// <summary>Takes over an import made elsewhere, to destroy it with what it made.</summary>
        public static ImportedStage Of(UsdImportResult result) => new ImportedStage(result);

        /// <summary>
        /// Imports <paramref name="path"/>. Without <paramref name="options"/>, open meshes keep the
        /// faces USD authors (<see cref="UsdImportOptions.DoubleSidedOpenMeshes"/> off): most
        /// fixtures are open quads and boxes whose vertex counts the tests check, and the Isaac Sim
        /// back-face rule has its own test in <see cref="UsdDoubleSidedImportTests"/>.
        /// </summary>
        public static ImportedStage Import(string path, UsdImportOptions options = null)
        {
            UsdImportResult result = UsdImporter.Import(path, options ?? new UsdImportOptions { DoubleSidedOpenMeshes = false });
            Assert.That(result?.RootObject, Is.Not.Null, $"Import of {path} returned no root object.");
            return new ImportedStage(result);
        }

        /// <summary>The GameObject that came from <paramref name="primPath"/>, or null.</summary>
        public GameObject Find(string primPath) => FindPrim(Result.RootObject, primPath);

        /// <summary>The object below <paramref name="root"/> that came from <paramref name="primPath"/>, or null.</summary>
        public static GameObject FindPrim(GameObject root, string primPath)
        {
            foreach (UsdPrimSource source in root.GetComponentsInChildren<UsdPrimSource>(true))
            {
                if (source.PrimPath == primPath)
                {
                    return source.gameObject;
                }
            }

            return null;
        }

        public void Dispose() => Release(Result.RootObject);

        /// <summary>Destroys an import's root with what it made (its meshes, materials, textures, animation).</summary>
        public static void Release(GameObject root)
        {
            if (root == null)
            {
                return;
            }

            var created = new HashSet<Object>();
            UsdAnimationPlayer player = root.GetComponent<UsdAnimationPlayer>();
            if (player != null)
            {
                created.Add(player.Data);
            }

            foreach (MeshFilter filter in root.GetComponentsInChildren<MeshFilter>(true))
            {
                created.Add(filter.sharedMesh);
            }

            foreach (SkinnedMeshRenderer skinned in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                created.Add(skinned.sharedMesh);
            }

            foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                foreach (Material material in renderer.sharedMaterials)
                {
                    if (material == null)
                    {
                        continue;
                    }

                    created.Add(material);
                    foreach (int property in material.GetTexturePropertyNameIDs())
                    {
                        created.Add(material.GetTexture(property));
                    }
                }
            }

            Object.DestroyImmediate(root);
            foreach (Object item in created)
            {
                if (item != null && !EditorUtility.IsPersistent(item))
                {
                    Object.DestroyImmediate(item);
                }
            }
        }
    }

    // Record shapes of make_expected.py's output. Points are flat x,y,z arrays and matrices are 16
    // row-major values in USD's row-vector convention, all in stage coordinates. JsonUtility fills
    // the fields, which the compiler cannot see.
#pragma warning disable 0649

    [Serializable]
    internal sealed class ExpectedStage
    {
        public string source;
        public string openusd;
        public ExpectedStageMetadata stage;
        public double[] times;
        public ExpectedSkinnedMesh[] skinned;
        public ExpectedXform[] xforms;
        public ExpectedVisibility[] visibility;
        public ExpectedPointCache[] points;
    }

    [Serializable]
    internal sealed class ExpectedStageMetadata
    {
        public double startTimeCode;
        public double endTimeCode;
        public double timeCodesPerSecond;
        public double framesPerSecond;
        public string upAxis;
        public double metersPerUnit;
    }

    [Serializable]
    internal sealed class ExpectedSkinnedMesh
    {
        public string path;
        public string skeleton;
        public float[] restPoints;
        public ExpectedPointsFrame[] frames;
    }

    [Serializable]
    internal sealed class ExpectedPointsFrame
    {
        public double time;
        public float[] points;
    }

    [Serializable]
    internal sealed class ExpectedXform
    {
        public string path;
        public ExpectedMatrixFrame[] frames;
    }

    [Serializable]
    internal sealed class ExpectedMatrixFrame
    {
        public double time;
        public float[] matrix;
    }

    [Serializable]
    internal sealed class ExpectedVisibility
    {
        public string path;
        public ExpectedTokenFrame[] frames;
    }

    [Serializable]
    internal sealed class ExpectedTokenFrame
    {
        public double time;
        public string value;
    }

    [Serializable]
    internal sealed class ExpectedPointCache
    {
        public string path;
        public bool hasDefault;
        public double[] timeSamples;
        public ExpectedPointsFrame[] frames;
    }
#pragma warning restore 0649
}
