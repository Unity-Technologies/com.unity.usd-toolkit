using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Unity.USDToolkit.Tests
{
    /// <summary>
    /// Switching a variant or the loaded payloads at runtime (UsdStageSource.SwitchVariant,
    /// SwitchPayloads, LoadPayload, UnloadPayload and the async forms): the change is made on the
    /// stage the import keeps, and only the parts it recomposes are imported again, in place --
    /// the rest of the import, and the edits made to it, stay. A change that cannot be confined,
    /// or a stage changed on disk, imports the whole stage again in the import's place.
    /// </summary>
    public sealed class UsdCompositionSwitchTests
    {
        private readonly List<GameObject> roots = new List<GameObject>();
        private string directory;
        private GameObject level;

        [SetUp]
        public void SetUp()
        {
            directory = Path.Combine(Path.GetTempPath(), "com.unity.usd-toolkit-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            level = new GameObject("Level");
            new GameObject("Before").transform.SetParent(level.transform, false);
        }

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject root in roots)
            {
                ImportedStage.Release(root);
            }

            roots.Clear();
            Object.DestroyImmediate(level);
            Directory.Delete(directory, true);
        }

        [Test]
        public void AVariantSwitchRebuildsThePrimInPlace()
        {
            string path = Write("variants.usda", VariantStage);
            UsdImportResult first = Keep(UsdImporter.Import(path, new UsdImportOptions { Parent = level.transform, GenerateColliders = true }));
            GameObject root = first.RootObject;
            root.name = "MyStage";
            root.transform.localPosition = new Vector3(1f, 2f, 3f);
            new GameObject("After").transform.SetParent(level.transform, false);
            GameObject oldPrim = ImportedStage.FindPrim(root, "/Root");
            Mesh oldMesh = ImportedStage.FindPrim(root, "/Root/A").GetComponent<MeshFilter>().sharedMesh;

            UsdSwitchResult switched = first.Source.SwitchVariant("/Root", "shape", "b");

            Assert.That(switched.Reimported, Is.False, switched.ReimportReason);
            Assert.That(switched.RootObject, Is.SameAs(root), "the import stays");
            Assert.That(switched.RebuiltPrimPaths, Is.EqualTo(new[] { "/Root" }));
            Assert.That(root.transform.GetSiblingIndex(), Is.EqualTo(1), "between Before and After");
            Assert.That(root.name, Is.EqualTo("MyStage"));
            Assert.That(root.transform.localPosition, Is.EqualTo(new Vector3(1f, 2f, 3f)));
            Assert.That(oldPrim == null, Is.True, "the switched prim's object is rebuilt");
            Assert.That(oldMesh == null, Is.True, "with the mesh the import made for it");
            Assert.That(ImportedStage.FindPrim(root, "/Root/A"), Is.Null);
            GameObject sphere = ImportedStage.FindPrim(root, "/Root/B");
            Assert.That(sphere, Is.Not.Null, "the other variant");
            Assert.That(sphere.GetComponent<MeshCollider>(), Is.Not.Null, "the import's settings carry over");
            Assert.That(sphere.GetComponent<UsdPrimSource>().Stage, Is.SameAs(first.Source), "recorded for the same import");
            Assert.That(first.Source.VariantSelections.Single().Variant, Is.EqualTo("b"));
            Assert.That(first.Source.VariantSets.Single().Selection, Is.EqualTo("b"), "the composition the source reports");
            Assert.That(first.Source.PrimPaths, Does.Contain("/Root/B").And.Not.Contain("/Root/A"));

            UsdSwitchResult back = first.Source.SwitchVariant("/Root", "shape", "a");
            Assert.That(back.Reimported, Is.False);
            Assert.That(ImportedStage.FindPrim(root, "/Root/A"), Is.Not.Null, "and back again");
            Assert.That(first.Source.VariantSelections.Count, Is.EqualTo(1), "one selection per set");
            Assert.That(first.Source.SwitchVariant("/Root", "shape", "a").RebuiltPrimPaths, Is.Empty, "the same choice rebuilds nothing");
        }

        [Test]
        public void TheRestOfTheImportAndItsEditsStay()
        {
            string path = Write("prop.usda", PropStage);
            UsdImportResult result = Keep(UsdImporter.Import(path, new UsdImportOptions { Parent = level.transform }));
            GameObject root = result.RootObject;
            GameObject table = ImportedStage.FindPrim(root, "/Root/Table");
            table.transform.localPosition = new Vector3(4f, 0f, 0f);
            var marker = new GameObject("Marker");
            marker.transform.SetParent(table.transform, false);

            // By hand on the switched prim's own object: moved, renamed, a child added below it.
            GameObject prop = ImportedStage.FindPrim(root, "/Root/Prop");
            prop.transform.localPosition = new Vector3(0f, 7f, 0f);
            prop.name = "MyProp";
            var attached = new GameObject("Attached");
            attached.transform.SetParent(ImportedStage.FindPrim(root, "/Root/Prop/Base").transform, false);

            UsdSwitchResult switched = result.Source.SwitchVariant("/Root/Prop", "shape", "sphere");

            Assert.That(switched.Reimported, Is.False, switched.ReimportReason);
            Assert.That(switched.RebuiltPrimPaths, Is.EqualTo(new[] { "/Root/Prop" }));
            Assert.That(ImportedStage.FindPrim(root, "/Root/Table"), Is.SameAs(table), "a prim the switch did not touch keeps its object");
            Assert.That(table.transform.localPosition, Is.EqualTo(new Vector3(4f, 0f, 0f)), "and its edits");
            Assert.That(marker.transform.parent, Is.EqualTo(table.transform));

            GameObject rebuilt = ImportedStage.FindPrim(root, "/Root/Prop");
            Assert.That(rebuilt, Is.Not.SameAs(prop));
            Assert.That(rebuilt.transform.localPosition, Is.EqualTo(new Vector3(0f, 7f, 0f)), "a hand move on the prim carries over");
            Assert.That(rebuilt.name, Is.EqualTo("MyProp"), "so does a rename");
            Assert.That(ImportedStage.FindPrim(root, "/Root/Prop/Ball"), Is.Not.Null);
            Assert.That(attached != null, Is.True, "an object added by hand is kept");
            Assert.That(attached.transform.parent, Is.EqualTo(ImportedStage.FindPrim(root, "/Root/Prop/Base").transform),
                        "under the new object of the prim it was under");
        }

        [Test]
        public void PayloadsLoadAndUnloadInPlace()
        {
            Write("part.usda", PartStage);
            string path = Write("main.usda", PayloadStage);
            UsdImportResult result = Keep(UsdImporter.Import(path, new UsdImportOptions { Parent = level.transform, LoadPayloads = false }));
            GameObject root = result.RootObject;
            UsdStageSource source = result.Source;
            Assert.That(ImportedStage.FindPrim(root, "/Root/Part/Box"), Is.Null, "unloaded");
            Assert.That(ImportedStage.FindPrim(root, "/Root/Part"), Is.Not.Null, "its prim keeps its place");

            UsdSwitchResult switched = source.SwitchPayloads(true);
            Assert.That(switched.Reimported, Is.False, switched.ReimportReason);
            Assert.That(ImportedStage.FindPrim(root, "/Root/Part/Box"), Is.Not.Null, "all loaded");
            Assert.That(source.Payloads.Single().Loaded, Is.True);
            Assert.That(source.PayloadsLoaded, Is.True);

            source.SwitchPayloads(false);
            Assert.That(ImportedStage.FindPrim(root, "/Root/Part/Box"), Is.Null, "none loaded");
            Assert.That(source.PayloadsLoaded, Is.False);

            source.SwitchPayloads(false, new[] { "/Root/Part" });
            Assert.That(ImportedStage.FindPrim(root, "/Root/Part/Box"), Is.Not.Null, "that one loaded");

            source.UnloadPayload("/Root/Part");
            Assert.That(ImportedStage.FindPrim(root, "/Root/Part/Box"), Is.Null, "unloaded by itself");
            source.LoadPayload("/Root/Part");
            Assert.That(ImportedStage.FindPrim(root, "/Root/Part/Box"), Is.Not.Null, "and loaded again");
            Assert.That(level.transform.childCount, Is.EqualTo(2), "Before and the one import, every time");
        }

        [Test]
        public void TheRebuiltPartsAnimationReplacesItsOwn()
        {
            string path = Write("animated.usda", AnimatedStage);
            UsdImportResult result = Keep(UsdImporter.Import(path, new UsdImportOptions { Parent = level.transform }));
            UsdAnimationPlayer player = result.Animation;
            Assert.That(player.Data.TransformTracks.Count, Is.EqualTo(2), "Spinner and Mover");
            player.Evaluate(0.5); // time code 5

            UsdSwitchResult switched = result.Source.SwitchVariant("/Root/Prop", "shape", "b");
            Assert.That(switched.Reimported, Is.False, switched.ReimportReason);
            Assert.That(result.RootObject.GetComponent<UsdAnimationPlayer>(), Is.SameAs(player), "the same player plays on");
            string[] tracks = player.Data.TransformTracks.Select(track => track.Path).OrderBy(p => p).ToArray();
            Assert.That(tracks, Is.EqualTo(new[] { "Root/Prop/Lifter", "Root/Spinner" }), "Mover's track is gone, Lifter's came");
            Assert.That(player.CurrentTime, Is.EqualTo(0.5).Within(1e-9));
            Vector3 lifter = ImportedStage.FindPrim(result.RootObject, "/Root/Prop/Lifter").transform.localPosition;
            Assert.That(Vector3.Distance(lifter, new Vector3(0f, 0f, 5f)), Is.LessThan(1e-4f), "posed at the player's time");
            player.Evaluate(1.0);
            lifter = ImportedStage.FindPrim(result.RootObject, "/Root/Prop/Lifter").transform.localPosition;
            Assert.That(Vector3.Distance(lifter, new Vector3(0f, 0f, 10f)), Is.LessThan(1e-4f), "and animated by it");
        }

        [UnityTest]
        public IEnumerator AnAsyncSwitchKeepsTheOldPartUntilTheNewOneIsBuilt()
        {
            string path = Write("variants.usda", VariantStage);
            UsdImportResult first = Keep(UsdImporter.Import(path, new UsdImportOptions { Parent = level.transform }));
            GameObject oldPrim = ImportedStage.FindPrim(first.RootObject, "/Root");
            var task = first.Source.SwitchVariantAsync("/Root", "shape", "b");
            Assert.That(oldPrim != null, Is.True, "still there while the stage is read");
            float deadline = Time.realtimeSinceStartup + 60f;
            while (!task.IsCompleted && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
            }

            Assert.That(task.IsCompleted, Is.True, "the switch finished");
            Assert.That(task.Exception, Is.Null);
            Assert.That(task.Result.Reimported, Is.False, task.Result.ReimportReason);
            Assert.That(oldPrim == null, Is.True, "replaced once the new part was built");
            Assert.That(task.Result.RootObject, Is.SameAs(first.RootObject));
            Assert.That(ImportedStage.FindPrim(first.RootObject, "/Root/B"), Is.Not.Null);
        }

        [Test]
        public void AStageChangedOnDiskImportsAgain()
        {
            string path = Write("variants.usda", VariantStage);
            UsdImportResult first = Keep(UsdImporter.Import(path, new UsdImportOptions { Parent = level.transform }));
            GameObject root = first.RootObject;
            File.WriteAllText(path, VariantStage.Replace("def Sphere \"B\"", "def Cylinder \"B\""));
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));

            UsdSwitchResult switched = first.Source.SwitchVariant("/Root", "shape", "b");
            Keep(switched.RootObject);
            Assert.That(switched.Reimported, Is.True);
            Assert.That(switched.ReimportReason, Does.Contain("changed on disk"));
            Assert.That(root == null, Is.True, "the import is replaced");
            Assert.That(switched.Import.Source.VariantSelections.Single().Variant, Is.EqualTo("b"));
            Assert.That(ImportedStage.FindPrim(switched.RootObject, "/Root/B"), Is.Not.Null, "with the file as it is now");
        }

        [Test]
        public void AVariantTheStageDoesNotHaveIsRefused()
        {
            string path = Write("variants.usda", VariantStage);
            UsdImportResult result = Keep(UsdImporter.Import(path, new UsdImportOptions { Parent = level.transform }));
            Assert.Throws<ArgumentException>(() => result.Source.SwitchVariant("/Root", "shape", "zzz"));
            Assert.Throws<ArgumentException>(() => result.Source.SwitchVariant("/Root", "nope", "a"));
            Assert.That(ImportedStage.FindPrim(result.RootObject, "/Root/A"), Is.Not.Null, "nothing changed");
            Assert.That(result.Source.VariantSelections, Is.Empty);
        }

        [Test]
        public void TheKeptStageReadsAsTheImportComposes()
        {
            string path = Write("variants.usda", VariantStage);
            UsdImportResult result = Keep(UsdImporter.Import(path, new UsdImportOptions { Parent = level.transform, KeepStageOpen = true }));
            UsdStage stage = result.Source.Stage;
            Assert.That(stage.IsOpen && stage.IsReadOnly, Is.True);
            Assert.That(stage.GetPrimAtPath("/Root").GetVariantSet("shape").Selection, Is.EqualTo("a"));
            Assert.Throws<InvalidOperationException>(() => stage.DefinePrim("/Other"), "read-only to callers");

            result.Source.SwitchVariant("/Root", "shape", "b");
            Assert.That(result.Source.Stage, Is.SameAs(stage), "the same stage, switched");
            Assert.That(stage.GetPrimAtPath("/Root/B").IsValid, Is.True);
            Assert.That(stage.GetPrimAtPath("/Root").GetVariantSet("shape").Selection, Is.EqualTo("b"));

            GameObject root = result.RootObject;
            UsdImportResult again = result.Source.Reimport(result.Source.ImportOptions);
            Keep(again.RootObject);
            Assert.That(stage.IsOpen, Is.False, "a reimport closes the old import's stage");
            Assert.That(root == null, Is.True);
        }

        [Test]
        public void AMissingSourceIsRefused()
        {
            string path = Write("variants.usda", VariantStage);
            UsdImportResult result = Keep(UsdImporter.Import(path, new UsdImportOptions { Parent = level.transform }));
            File.Delete(path);
            Assert.Throws<UsdImportException>(() => result.Source.SwitchVariant("/Root", "shape", "b"));
            Assert.That(result.RootObject != null, Is.True, "nothing is destroyed when the switch cannot happen");
        }

        private UsdImportResult Keep(UsdImportResult result)
        {
            roots.Add(result.RootObject);
            return result;
        }

        private void Keep(GameObject root) => roots.Add(root);

        private string Write(string file, string text)
        {
            string path = Path.Combine(directory, file);
            File.WriteAllText(path, text);
            return path;
        }

        internal const string VariantStage = @"#usda 1.0
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

        private const string PropStage = @"#usda 1.0
(
    defaultPrim = ""Root""
)

def Xform ""Root""
{
    def Cube ""Table""
    {
    }

    def Xform ""Prop"" (
        variants = {
            string shape = ""box""
        }
        prepend variantSets = ""shape""
    )
    {
        def Xform ""Base""
        {
        }

        variantSet ""shape"" = {
            ""box"" {
                def Cube ""Box""
                {
                }
            }
            ""sphere"" {
                def Sphere ""Ball""
                {
                }
            }
        }
    }
}
";

        private const string AnimatedStage = @"#usda 1.0
(
    defaultPrim = ""Root""
    startTimeCode = 0
    endTimeCode = 10
    timeCodesPerSecond = 10
)

def Xform ""Root""
{
    def Xform ""Spinner""
    {
        double3 xformOp:translate.timeSamples = {
            0: (0, 0, 0),
            10: (0, 10, 0),
        }
        uniform token[] xformOpOrder = [""xformOp:translate""]
    }

    def Xform ""Prop"" (
        variants = {
            string shape = ""a""
        }
        prepend variantSets = ""shape""
    )
    {
        variantSet ""shape"" = {
            ""a"" {
                def Xform ""Mover""
                {
                    double3 xformOp:translate.timeSamples = {
                        0: (0, 0, 0),
                        10: (0, 10, 0),
                    }
                    uniform token[] xformOpOrder = [""xformOp:translate""]
                }
            }
            ""b"" {
                def Xform ""Lifter""
                {
                    double3 xformOp:translate.timeSamples = {
                        0: (0, 0, 0),
                        10: (0, 0, 10),
                    }
                    uniform token[] xformOpOrder = [""xformOp:translate""]
                }
            }
        }
    }
}
";

        private const string PayloadStage = @"#usda 1.0
(
    defaultPrim = ""Root""
)

def Xform ""Root""
{
    def Xform ""Part"" (
        prepend payload = @./part.usda@</Part>
    )
    {
    }
}
";

        private const string PartStage = @"#usda 1.0
(
    defaultPrim = ""Part""
)

def Xform ""Part""
{
    def Cube ""Box""
    {
    }
}
";
    }

    /// <summary>
    /// The same switch in Play Mode, as a game makes it: asynchronous, with the old part playing
    /// until the new one is built and destroyed at the end of that frame.
    /// </summary>
    public sealed class UsdCompositionSwitchPlayModeTests
    {
        // Everything happens after EnterPlayMode: the domain reload there resets what came before.
        [UnityTest]
        public IEnumerator AGameSwitchesAVariantWhilePlaying()
        {
            yield return new EnterPlayMode();

            string directory = Path.Combine(Path.GetTempPath(), "com.unity.usd-toolkit-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "variants.usda");
            File.WriteAllText(path, UsdCompositionSwitchTests.VariantStage);
            var level = new GameObject("Level");
            UsdImportResult first = null;
            try
            {
                first = UsdImporter.Import(path, new UsdImportOptions { Parent = level.transform });
                GameObject oldPrim = ImportedStage.FindPrim(first.RootObject, "/Root");
                var task = first.Source.SwitchVariantAsync("/Root", "shape", "b");
                float deadline = Time.realtimeSinceStartup + 60f;
                while (!task.IsCompleted && Time.realtimeSinceStartup < deadline)
                {
                    Assert.That(oldPrim != null, Is.True, "the old part stays until the new one is built");
                    yield return null;
                }

                Assert.That(task.IsCompleted && task.Exception == null, Is.True, task.Exception?.ToString());
                Assert.That(task.Result.Reimported, Is.False, task.Result.ReimportReason);
                yield return null; // Destroy takes effect at the end of the frame
                Assert.That(oldPrim == null, Is.True, "then it is gone");
                Assert.That(ImportedStage.FindPrim(first.RootObject, "/Root/B"), Is.Not.Null);
                Assert.That(level.transform.childCount, Is.EqualTo(1));
            }
            finally
            {
                if (first != null && first.RootObject != null)
                {
                    ImportedStage.Of(first).Dispose();
                }

                Object.Destroy(level);
                Directory.Delete(directory, true);
            }

            yield return new ExitPlayMode();
        }
    }
}
