using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Unity.USDToolkit.Tests
{
    /// <summary>
    /// Where a live switch draws the line: a prototype of a point instancer rebuilds the whole
    /// instancer, a change to a material imports the whole stage again, a variant set inside a
    /// variant appears and switches with it; and the kept stage stays usable -- reopened when
    /// closed, safe to read from several threads.
    /// </summary>
    public sealed class UsdLiveSwitchTests
    {
        private readonly List<GameObject> roots = new List<GameObject>();
        private string directory;

        [SetUp]
        public void SetUp()
        {
            directory = Path.Combine(Path.GetTempPath(), "com.unity.usd-toolkit-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
        }

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject root in roots)
            {
                ImportedStage.Release(root);
            }

            roots.Clear();
            Directory.Delete(directory, true);
        }

        [Test]
        public void APrototypeSwitchRebuildsTheWholeInstancer()
        {
            UsdImportResult result = Import("scatter.usda", ScatterStage);
            UsdSwitchResult switched = result.Source.SwitchVariant("/Root/Scatter/Protos/Pebble", "shape", "round");
            Assert.That(switched.Reimported, Is.False, switched.ReimportReason);
            Assert.That(switched.RebuiltPrimPaths, Is.EqualTo(new[] { "/Root/Scatter" }), "widened to the instancer");
            UsdPointInstancer instancer = result.RootObject.GetComponentInChildren<UsdPointInstancer>(true);
            Assert.That(instancer, Is.Not.Null);
            Assert.That(instancer.InstanceCount, Is.EqualTo(3), "every placement is drawn again");
            // Prototypes are imported once under the instancer (prototype_<n>_<name>), not at their prim paths.
            Transform scatter = ImportedStage.FindPrim(result.RootObject, "/Root/Scatter").transform;
            string[] names = scatter.GetComponentsInChildren<Transform>(true).Select(t => t.name).ToArray();
            Assert.That(names, Does.Contain("Ball").And.Not.Contain("Slab"), "the prototype is the other variant now");
        }

        [Test]
        public void AMaterialSwitchImportsTheWholeStageAgain()
        {
            UsdImportResult result = Import("looks.usda", LooksStage);
            GameObject root = result.RootObject;
            UsdSwitchResult switched = result.Source.SwitchVariant("/Root/Looks/Paint", "tone", "blue");
            roots.Add(switched.RootObject);
            Assert.That(switched.Reimported, Is.True);
            Assert.That(switched.ReimportReason, Does.Contain("reaches past"));
            Assert.That(root == null, Is.True);
            Assert.That(switched.Import.Source.VariantSelections.Single().Variant, Is.EqualTo("blue"));
            Material material = ImportedStage.FindPrim(switched.RootObject, "/Root/Box").GetComponent<MeshRenderer>().sharedMaterial;
            Assert.That(material.color.b, Is.GreaterThan(0.9f), "the box shows the other look");
        }

        [Test]
        public void AVariantSetInsideAVariantSwitchesToo()
        {
            UsdImportResult result = Import("nested.usda", NestedStage);
            UsdStageSource source = result.Source;
            Assert.That(source.VariantSets.Select(set => set.Name), Is.EquivalentTo(new[] { "shape" }));

            source.SwitchVariant("/Root", "shape", "fancy");
            Assert.That(source.VariantSets.Select(set => set.Name), Is.EquivalentTo(new[] { "shape", "detail" }),
                        "the inner set comes with its variant");
            Assert.That(ImportedStage.FindPrim(result.RootObject, "/Root/Low"), Is.Not.Null);

            UsdSwitchResult inner = source.SwitchVariant("/Root", "detail", "high");
            Assert.That(inner.Reimported, Is.False, inner.ReimportReason);
            Assert.That(ImportedStage.FindPrim(result.RootObject, "/Root/High"), Is.Not.Null);
            Assert.That(ImportedStage.FindPrim(result.RootObject, "/Root/Low"), Is.Null);
            Assert.That(source.VariantSelections.Count, Is.EqualTo(2));
        }

        [Test]
        public void TheKeptStageReopensOnceClosedAndReadsFromManyThreads()
        {
            UsdImportResult result = Import("nested.usda", NestedStage);
            UsdStage stage = result.Source.Stage;
            stage.Dispose();
            UsdStage reopened = result.Source.Stage;
            Assert.That(reopened, Is.Not.SameAs(stage));
            Assert.That(reopened.IsOpen, Is.True);

            var readers = new Task<int>[4];
            for (int t = 0; t < readers.Length; t++)
            {
                readers[t] = Task.Run(() =>
                {
                    int found = 0;
                    for (int i = 0; i < 200; i++)
                    {
                        found += reopened.GetPrimAtPath("/Root").VariantSets.Count;
                        found += reopened.GetPrimAtPath("/Root/Base").GetAttribute("size").Get<double>() > 0 ? 1 : 0;
                    }

                    return found;
                });
            }

            Task.WaitAll(readers);
            Assert.That(readers.All(task => task.Result == 400), Is.True, "every read answered, one at a time");
            Assert.That(result.Source.SwitchVariant("/Root", "shape", "fancy").Reimported, Is.False, "and the switch uses it");
        }

        private UsdImportResult Import(string file, string text)
        {
            string path = Path.Combine(directory, file);
            File.WriteAllText(path, text);
            UsdImportResult result = UsdImporter.Import(path);
            roots.Add(result.RootObject);
            return result;
        }

        private const string ScatterStage = @"#usda 1.0
(
    defaultPrim = ""Root""
)

def Xform ""Root""
{
    def PointInstancer ""Scatter""
    {
        rel prototypes = </Root/Scatter/Protos/Pebble>
        int[] protoIndices = [0, 0, 0]
        point3f[] positions = [(0, 0, 0), (2, 0, 0), (4, 0, 0)]

        def Xform ""Protos""
        {
            def Xform ""Pebble"" (
                variants = {
                    string shape = ""flat""
                }
                prepend variantSets = ""shape""
            )
            {
                variantSet ""shape"" = {
                    ""flat"" {
                        def Cube ""Slab""
                        {
                        }
                    }
                    ""round"" {
                        def Sphere ""Ball""
                        {
                        }
                    }
                }
            }
        }
    }
}
";

        private const string LooksStage = @"#usda 1.0
(
    defaultPrim = ""Root""
)

def Xform ""Root""
{
    def Cube ""Box"" (
        prepend apiSchemas = [""MaterialBindingAPI""]
    )
    {
        rel material:binding = </Root/Looks/Paint>
    }

    def Scope ""Looks""
    {
        def Material ""Paint"" (
            variants = {
                string tone = ""red""
            }
            prepend variantSets = ""tone""
        )
        {
            token outputs:surface.connect = </Root/Looks/Paint/Surface.outputs:surface>

            def Shader ""Surface""
            {
                uniform token info:id = ""UsdPreviewSurface""
                token outputs:surface
            }

            variantSet ""tone"" = {
                ""blue"" {
                    over ""Surface""
                    {
                        color3f inputs:diffuseColor = (0, 0, 1)
                    }
                }
                ""red"" {
                    over ""Surface""
                    {
                        color3f inputs:diffuseColor = (1, 0, 0)
                    }
                }
            }
        }
    }
}
";

        private const string NestedStage = @"#usda 1.0
(
    defaultPrim = ""Root""
)

def Xform ""Root"" (
    variants = {
        string shape = ""plain""
    }
    prepend variantSets = ""shape""
)
{
    def Cube ""Base""
    {
        double size = 2
    }

    variantSet ""shape"" = {
        ""fancy"" (
            variants = {
                string detail = ""low""
            }
            prepend variantSets = ""detail""
        ) {
            variantSet ""detail"" = {
                ""high"" {
                    def Sphere ""High""
                    {
                    }
                }
                ""low"" {
                    def Cube ""Low""
                    {
                    }
                }
            }
        }
        ""plain"" {
        }
    }
}
";
    }
}
