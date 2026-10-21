using System.IO;
using System.Linq;
using NUnit.Framework;
using Unity.USDToolkit.Editor;
using UnityEditor;
using UnityEngine;

namespace Unity.USDToolkit.Tests
{
    /// <summary>
    /// USD files in the project import as model assets that keep everything a runtime import
    /// builds: meshes, materials, textures with their pixels, skinning and animation.
    /// </summary>
    public sealed class UsdStageImporterTests
    {
        private const string Folder = "Assets/UsdToolkitImporterTests";

        [SetUp]
        public void CreateFolder()
        {
            if (!AssetDatabase.IsValidFolder(Folder))
            {
                AssetDatabase.CreateFolder("Assets", "UsdToolkitImporterTests");
            }
        }

        [TearDown]
        public void DeleteFolder()
        {
            AssetDatabase.DeleteAsset(Folder);
        }

        [Test]
        public void AStageImportsAsAModelAssetWithItsSubAssets()
        {
            string path = AddFixture("skel_arm.usda");
            Assert.That(AssetImporter.GetAtPath(path), Is.InstanceOf<UsdStageImporter>());
            var root = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            Assert.That(root, Is.Not.Null, "the main asset is the stage's root object");

            Object[] all = AssetDatabase.LoadAllAssetsAtPath(path);
            Assert.That(all.OfType<Mesh>().Count(), Is.GreaterThanOrEqualTo(4), "Strip, Hand, the boxes and the point cache");
            Assert.That(all.OfType<Material>(), Is.Not.Empty);
            Assert.That(all.OfType<UsdAnimationData>().Count(), Is.EqualTo(1));

            foreach (Component component in root.GetComponentsInChildren<Component>(true))
            {
                Assert.That(component, Is.Not.Null, "no component lost its script in the asset");
            }

            var skinned = ImportedStage.FindPrim(root, "/Root/Arm/Strip").GetComponent<SkinnedMeshRenderer>();
            Assert.That(AssetDatabase.GetAssetPath(skinned.sharedMesh), Is.EqualTo(path), "the mesh is a sub-asset");
            Assert.That(skinned.bones.All(b => b != null), Is.True, "bones resolve inside the asset");
            Assert.That(root.GetComponent<UsdAnimationPlayer>().Data, Is.Not.Null);
        }

        [Test]
        public void AnInstanceAnimatesLikeARuntimeImport()
        {
            ExpectedStage expected = UsdFixtures.LoadExpected("Skel", "skel_arm");
            string path = AddFixture("skel_arm.usda");
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(path));
            try
            {
                instance.GetComponent<UsdAnimationPlayer>().EvaluateAtTimeCode(7.5);
                foreach (ExpectedSkinnedMesh mesh in expected.skinned)
                {
                    var skinned = ImportedStage.FindPrim(instance, mesh.path).GetComponent<SkinnedMeshRenderer>();
                    UsdSkinningTests.AssertSkinnedPoints(instance.transform, skinned, mesh, UsdFixtures.FrameAt(mesh, 7.5));
                }
            }
            finally
            {
                Object.DestroyImmediate(instance);
            }
        }

        [Test]
        public void TheSourceIsRecordedByItsProjectPath()
        {
            string path = AddFixture("skel_arm.usda");
            var source = AssetDatabase.LoadAssetAtPath<GameObject>(path).GetComponent<UsdStageSource>();
            Assert.That(source.RecordedSourcePath, Is.EqualTo(path), "no machine's absolute path in the asset");
            Assert.That(File.Exists(source.SourcePath), Is.True, source.SourcePath);
            Assert.That(Path.IsPathRooted(source.SourcePath), Is.True);

            // A runtime import of a file in a package records the package path.
            using (ImportedStage stage = ImportedStage.Import(UsdFixtures.PathOf("Skel", "skel_arm.usda")))
            {
                UsdStageSource runtime = stage.Result.Source;
                Assert.That(runtime.RecordedSourcePath, Does.StartWith("Packages/com.unity.usd-toolkit/Tests/Fixtures~/Skel/"));
                Assert.That(Path.GetFullPath(runtime.SourcePath), Is.EqualTo(Path.GetFullPath(UsdFixtures.PathOf("Skel", "skel_arm.usda"))));
            }
        }

        [Test]
        public void AnAnimationClipIsAddedWhenAsked()
        {
            string path = AddFixture("skel_arm.usda");
            Assert.That(AssetDatabase.LoadAllAssetsAtPath(path).OfType<AnimationClip>(), Is.Empty, "off by default");
            var importer = (UsdStageImporter)AssetImporter.GetAtPath(path);
            importer.GenerateAnimationClip = true;
            EditorUtility.SetDirty(importer);
            importer.SaveAndReimport();

            AnimationClip clip = AssetDatabase.LoadAllAssetsAtPath(path).OfType<AnimationClip>().Single();
            Assert.That(clip.length, Is.EqualTo(10f / 24f).Within(1e-5f));
            var root = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(root);
            try
            {
                clip.SampleAnimation(instance, 10f / 24f);
                var player = instance.GetComponent<UsdAnimationPlayer>();
                Vector3 sampled = ImportedStage.FindPrim(instance, "/Root/Spinner").transform.localPosition;
                player.EvaluateAtTimeCode(10.0);
                Vector3 played = ImportedStage.FindPrim(instance, "/Root/Spinner").transform.localPosition;
                Assert.That(Vector3.Distance(sampled, played), Is.LessThan(1e-4f), "the clip's curves bind inside the asset");
            }
            finally
            {
                Object.DestroyImmediate(instance);
            }
        }

        [Test]
        public void DeformedMeshesKeepTheirMeshesInTheAsset()
        {
            string path = AddFixture("dqs_twist.usda");
            var root = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            GameObject tube = ImportedStage.FindPrim(root, "/Root/Bar/Tube");
            Assert.That(tube.GetComponent<MeshRenderer>().additionalVertexStreams, Is.Null, "no runtime stream in the asset");
            Assert.That(AssetDatabase.GetAssetPath(tube.GetComponent<MeshFilter>().sharedMesh), Is.EqualTo(path));

            var instance = (GameObject)PrefabUtility.InstantiatePrefab(root);
            try
            {
                instance.GetComponent<UsdAnimationPlayer>().EvaluateAtTimeCode(10.0);
                var skin = ImportedStage.FindPrim(instance, "/Root/Bar/Tube").GetComponent<UsdDualQuaternionSkin>();
                skin.Deform();
                Assert.That(skin.GetComponent<MeshRenderer>().additionalVertexStreams, Is.Not.Null, "an instance draws its pose");
                ExpectedSkinnedMesh expected = UsdFixtures.LoadExpected("Skel", "dqs_twist").skinned[0];
                UsdDualQuaternionTests.AssertDraws(instance.transform, skin, expected, UsdFixtures.FrameAt(expected, 10.0));
            }
            finally
            {
                Object.DestroyImmediate(instance);
            }
        }

        [Test]
        public void AnInstanceOfAnAssetSwitchesIntoARuntimeImport()
        {
            string path = WriteStage("variants.usda", VariantStage);
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            Assert.Throws<System.InvalidOperationException>(
                () => asset.GetComponent<UsdStageSource>().SwitchVariant("/Root", "shape", "b"), "an asset changes through its settings");

            var instance = (GameObject)PrefabUtility.InstantiatePrefab(asset);
            Mesh assetMesh = ImportedStage.FindPrim(instance, "/Root/A").GetComponent<MeshFilter>().sharedMesh;
            // In Edit Mode a prefab instance cannot lose a part of itself, so the switch imports the
            // whole stage again in its place.
            UsdSwitchResult switched = instance.GetComponent<UsdStageSource>().SwitchVariant("/Root", "shape", "b");
            Assert.That(switched.Reimported, Is.True);
            Assert.That(switched.ReimportReason, Does.Contain("prefab instance"));
            UsdImportResult result = switched.Import;
            using (ImportedStage.Of(result))
            {
                Assert.That(instance == null, Is.True, "the instance is replaced");
                Assert.That(assetMesh != null, Is.True, "the asset's mesh is not the switch's to destroy");
                Assert.That(ImportedStage.FindPrim(result.RootObject, "/Root/B"), Is.Not.Null);
            }
        }

        [Test]
        public void ChangedSettingsReimport()
        {
            string path = AddFixture("skel_arm.usda");
            var importer = (UsdStageImporter)AssetImporter.GetAtPath(path);
            importer.ImportSkinning = false;
            EditorUtility.SetDirty(importer); // as the Inspector's SerializedObject does
            importer.SaveAndReimport();

            var root = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            GameObject strip = ImportedStage.FindPrim(root, "/Root/Arm/Strip");
            Assert.That(strip.GetComponent<SkinnedMeshRenderer>(), Is.Null);
            Assert.That(strip.GetComponent<MeshFilter>(), Is.Not.Null);
        }

        [Test]
        public void TexturesKeepTheirPixelsAndFollowTheirFile()
        {
            WritePng(Path.Combine(Folder, "tex.png"), Color.red);
            AssetDatabase.ImportAsset(Path.Combine(Folder, "tex.png"), ImportAssetOptions.ForceSynchronousImport);
            string path = WriteStage("textured.usda", TexturedStage);

            Texture2D texture = AssetDatabase.LoadAllAssetsAtPath(path).OfType<Texture2D>().Single();
            Assert.That(texture.isReadable, Is.True, "the CPU copy is what the asset serializes");
            Color pixel = texture.GetPixel(1, 1);
            Assert.That(pixel.r, Is.GreaterThan(0.9f));
            Assert.That(pixel.b, Is.LessThan(0.1f));

            // The stage depends on the texture file, so rewriting it reimports the stage.
            WritePng(Path.Combine(Folder, "tex.png"), Color.blue);
            AssetDatabase.ImportAsset(Path.Combine(Folder, "tex.png"), ImportAssetOptions.ForceSynchronousImport);
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            texture = AssetDatabase.LoadAllAssetsAtPath(path).OfType<Texture2D>().Single();
            Assert.That(texture.GetPixel(1, 1).b, Is.GreaterThan(0.9f), "reimported with the new pixels");
        }

        [Test]
        public void VariantSelectionsInTheSettingsCompose()
        {
            string path = WriteStage("variants.usda", VariantStage);
            var root = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            Assert.That(ImportedStage.FindPrim(root, "/Root/A"), Is.Not.Null, "the authored selection");
            UsdVariantSetInfo set = root.GetComponent<UsdStageSource>().VariantSets.Single();
            Assert.That(set.Variants, Is.EqualTo(new[] { "a", "b" }));

            var importer = (UsdStageImporter)AssetImporter.GetAtPath(path);
            importer.VariantSelections.Add(new UsdVariantSelection("/Root", "shape", "b"));
            EditorUtility.SetDirty(importer);
            importer.SaveAndReimport();

            root = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            Assert.That(ImportedStage.FindPrim(root, "/Root/A"), Is.Null);
            Assert.That(ImportedStage.FindPrim(root, "/Root/B"), Is.Not.Null);
        }

        private static string AddFixture(string file)
        {
            string path = Folder + "/" + file;
            File.Copy(UsdFixtures.PathOf("Skel", file), path, true);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            return path;
        }

        private static string WriteStage(string file, string text)
        {
            string path = Folder + "/" + file;
            File.WriteAllText(path, text);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            return path;
        }

        private static void WritePng(string path, Color color)
        {
            var texture = new Texture2D(4, 4, TextureFormat.RGBA32, false);
            try
            {
                texture.SetPixels(Enumerable.Repeat(color, 16).ToArray());
                texture.Apply();
                File.WriteAllBytes(path, texture.EncodeToPNG());
            }
            finally
            {
                Object.DestroyImmediate(texture);
            }
        }

        private const string TexturedStage = @"#usda 1.0
(
    defaultPrim = ""Root""
)

def Xform ""Root""
{
    def Mesh ""Quad"" (
        prepend apiSchemas = [""MaterialBindingAPI""]
    )
    {
        int[] faceVertexCounts = [4]
        int[] faceVertexIndices = [0, 1, 2, 3]
        point3f[] points = [(0, 0, 0), (1, 0, 0), (1, 1, 0), (0, 1, 0)]
        texCoord2f[] primvars:st = [(0, 0), (1, 0), (1, 1), (0, 1)] (
            interpolation = ""vertex""
        )
        rel material:binding = </Root/Looks/Red>
    }

    def Scope ""Looks""
    {
        def Material ""Red""
        {
            token outputs:surface.connect = </Root/Looks/Red/Surface.outputs:surface>

            def Shader ""Surface""
            {
                uniform token info:id = ""UsdPreviewSurface""
                color3f inputs:diffuseColor.connect = </Root/Looks/Red/Texture.outputs:rgb>
                token outputs:surface
            }

            def Shader ""Texture""
            {
                uniform token info:id = ""UsdUVTexture""
                asset inputs:file = @./tex.png@
                float3 outputs:rgb
            }
        }
    }
}
";

        private const string VariantStage = @"#usda 1.0
(
    defaultPrim = ""Root""
)

def Xform ""Root"" (
    variants = {
        string shape = ""a""
    }
    prepend variantSets = ""shape""
)
{
    variantSet ""shape"" = {
        ""a"" {
            def Cube ""A""
            {
            }
        }
        ""b"" {
            def Sphere ""B""
            {
            }
        }
    }
}
";
    }
}
