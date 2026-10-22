using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace Unity.USDToolkit.Tests
{
    /// <summary>
    /// The C# stage API (UsdStage, UsdPrim, UsdAttribute): stages made, opened, edited and saved;
    /// typed values both ways, with precision converting and shapes never; time samples; edit
    /// targets and variants; and the refusals.
    /// </summary>
    public sealed class UsdStageApiTests
    {
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
            Directory.Delete(directory, true);
        }

        [Test]
        public void AStageIsMadeEditedSavedAndOpenedAgain()
        {
            string path = Path.Combine(directory, "made.usda");
            using (UsdStage stage = UsdStage.CreateNew(path))
            {
                UsdPrim world = stage.DefinePrim("/World", "Xform");
                stage.DefaultPrim = world;
                world.SetKind("assembly");
                UsdPrim mesh = stage.DefinePrim("/World/Tri", "Mesh");
                mesh.GetAttribute("points").Set(new[] { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0) });
                mesh.GetAttribute("faceVertexCounts").Set(new[] { 3 });
                mesh.GetAttribute("faceVertexIndices").Set(new[] { 0, 1, 2 });
                mesh.GetAttribute("primvars:displayColor").Set(new[] { Color.red }, typeName: "color3f[]");
                stage.MetersPerUnit = 0.01;
                stage.UpAxis = UsdUpAxis.Z;
                Assert.That(stage.HasUnsavedChanges, Is.True);
                stage.Save();
                Assert.That(stage.HasUnsavedChanges, Is.False);
            }

            using (UsdStage stage = UsdStage.Open(path))
            {
                Assert.That(stage.IsReadOnly, Is.False);
                Assert.That(stage.DefaultPrim.Path, Is.EqualTo("/World"));
                Assert.That(stage.MetersPerUnit, Is.EqualTo(0.01));
                Assert.That(stage.UpAxis, Is.EqualTo(UsdUpAxis.Z));
                UsdPrim mesh = stage.GetPrimAtPath("/World/Tri");
                Assert.That(mesh.IsValid && mesh.TypeName == "Mesh", Is.True);
                Assert.That(mesh.Parent.Kind, Is.EqualTo("assembly"));
                Vector3[] points = mesh.GetAttribute("points").Get<Vector3[]>();
                Assert.That(points, Is.EqualTo(new[] { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0) }));
                Assert.That(mesh.GetAttribute("points").TypeName, Is.EqualTo("point3f[]"), "the schema's role is kept");
                Color color = mesh.GetAttribute("primvars:displayColor").Get<Color[]>().Single();
                Assert.That(color, Is.EqualTo(new Color(1, 0, 0, 1)), "color3f reads with alpha 1");
                Assert.That(stage.Traverse().Select(prim => prim.Path), Is.EqualTo(new[] { "/World", "/World/Tri" }));
            }
        }

        [Test]
        public void ValuesCrossAsTheirOwnTypes()
        {
            using (UsdStage stage = UsdStage.CreateInMemory())
            {
                UsdPrim prim = stage.DefinePrim("/Values", "Xform");
                prim.GetAttribute("flag").Set(true);
                prim.GetAttribute("count").Set(-7);
                prim.GetAttribute("big").Set(9000000000L);
                prim.GetAttribute("weight").Set(0.25f);
                prim.GetAttribute("precise").Set(1.0 / 3.0);
                prim.GetAttribute("label").Set("héllo wörld");
                prim.GetAttribute("tags").Set(new[] { "a", "", "c" }, typeName: "token[]");
                prim.GetAttribute("turn").Set(Quaternion.Euler(10, 20, 30));
                prim.GetAttribute("cells").Set(new[] { new Vector2Int(1, 2), new Vector2Int(3, 4) });
                Matrix4x4 matrix = Matrix4x4.TRS(new Vector3(1, 2, 3), Quaternion.Euler(0, 90, 0), Vector3.one * 2f);
                prim.GetAttribute("matrix").Set(matrix);
                prim.GetAttribute("half").Set(0.5f, typeName: "half");
                prim.GetAttribute("position").Set(new Vector3(1.5f, 2.5f, 3.5f), typeName: "double3");

                Assert.That(prim.GetAttribute("flag").Get<bool>(), Is.True);
                Assert.That(prim.GetAttribute("count").Get<int>(), Is.EqualTo(-7));
                Assert.That(prim.GetAttribute("big").Get<long>(), Is.EqualTo(9000000000L));
                Assert.That(prim.GetAttribute("weight").Get<float>(), Is.EqualTo(0.25f));
                Assert.That(prim.GetAttribute("precise").Get<double>(), Is.EqualTo(1.0 / 3.0), "double keeps its precision");
                Assert.That(prim.GetAttribute("label").Get<string>(), Is.EqualTo("héllo wörld"), "UTF-8 both ways");
                Assert.That(prim.GetAttribute("tags").Get<string[]>(), Is.EqualTo(new[] { "a", "", "c" }));
                Assert.That(prim.GetAttribute("tags").ValueType, Is.EqualTo(UsdValueType.Token));
                Assert.That(Quaternion.Angle(prim.GetAttribute("turn").Get<Quaternion>(), Quaternion.Euler(10, 20, 30)), Is.LessThan(1e-3f));
                Assert.That(prim.GetAttribute("cells").Get<Vector2Int[]>(), Is.EqualTo(new[] { new Vector2Int(1, 2), new Vector2Int(3, 4) }));
                Matrix4x4 back = prim.GetAttribute("matrix").Get<Matrix4x4>();
                for (int i = 0; i < 16; i++)
                {
                    Assert.That(back[i], Is.EqualTo(matrix[i]).Within(1e-5f), $"matrix element {i}");
                }

                Assert.That(prim.GetAttribute("half").ValueType, Is.EqualTo(UsdValueType.Half));
                Assert.That(prim.GetAttribute("half").Get<float>(), Is.EqualTo(0.5f), "a half reads as a float");
                Assert.That(prim.GetAttribute("position").ValueType, Is.EqualTo(UsdValueType.Double3));
                Assert.That(prim.GetAttribute("position").Get<Vector3>(), Is.EqualTo(new Vector3(1.5f, 2.5f, 3.5f)), "a double3 reads as a Vector3");
                Assert.That(prim.GetAttribute("position").GetComponents(), Is.EqualTo(new[] { 1.5, 2.5, 3.5 }), "and at full precision");
                prim.GetAttribute("position").SetComponents(new[] { 1e-9, 2.0, 3.0 });
                Assert.That(prim.GetAttribute("position").GetComponents()[0], Is.EqualTo(1e-9), "written at full precision");
            }
        }

        [Test]
        public void ShapesAndKindsNeverConvert()
        {
            using (UsdStage stage = UsdStage.CreateInMemory())
            {
                UsdPrim prim = stage.DefinePrim("/Values");
                prim.GetAttribute("points").Set(new[] { Vector3.one }, typeName: "point3f[]");
                prim.GetAttribute("count").Set(3);
                Assert.Throws<UsdStageException>(() => prim.GetAttribute("points").Get<Vector3>(), "an array is not one value");
                Assert.Throws<UsdStageException>(() => prim.GetAttribute("points").Get<Vector4[]>(), "a float3 is not a float4");
                Assert.Throws<UsdStageException>(() => prim.GetAttribute("count").Get<float>(), "an int is not a float");
                Assert.Throws<UsdStageException>(() => prim.GetAttribute("count").Set("three"), "nor written as a string");
                Assert.Throws<UsdStageException>(() => prim.GetAttribute("missing").Get<int>(), "no attribute");
                Assert.Throws<ArgumentException>(() => prim.GetAttribute("count").Get<Rect>(), "a type the API has no form for");
                Assert.Throws<UsdStageException>(() => prim.GetAttribute("fresh").Set(1f, typeName: "int"), "a type name that does not take the value");
                Assert.That(prim.GetAttribute("missing").Exists, Is.False);
            }
        }

        [Test]
        public void TimeSamplesBlocksAndClears()
        {
            using (UsdStage stage = UsdStage.CreateInMemory())
            {
                UsdAttribute height = stage.DefinePrim("/Ball", "Sphere").GetAttribute("radius");
                height.Set(1.0, 0);
                height.Set(3.0, 10);
                Assert.That(height.GetTimeSamples(), Is.EqualTo(new[] { 0.0, 10.0 }));
                Assert.That(height.Get<double>(5), Is.EqualTo(2.0), "interpolated between samples");
                Assert.That(height.Get<double>(UsdTimeCode.EarliestTime), Is.EqualTo(1.0));
                Assert.That(height.TryGet(out double fallback), Is.True, "the default is the schema fallback");
                Assert.That(fallback, Is.EqualTo(1.0));

                height.Block(10);
                Assert.That(height.TryGet(out double _, 10), Is.False, "a blocked sample has no value");
                height.ClearAt(10);
                Assert.That(height.GetTimeSamples(), Is.EqualTo(new[] { 0.0 }));
                height.Block();
                Assert.That(height.GetTimeSamples(), Is.Empty, "a block at the default takes the samples too");
                Assert.That(height.Get<double>(0), Is.EqualTo(1.0), "a blocked attribute reads its schema fallback");
                UsdAttribute custom = stage.GetPrimAtPath("/Ball").GetAttribute("bounce");
                custom.Set(0.5f);
                custom.Block();
                Assert.That(custom.TryGet(out float _), Is.False, "and one with no fallback has no value");
                height.Clear();
                Assert.That(height.HasAuthoredValue, Is.False);
                Assert.That(height.Get<double>(), Is.EqualTo(1.0), "cleared, the fallback is back");
            }
        }

        [Test]
        public void VariantsArcsAndEditTargets()
        {
            string part = Path.Combine(directory, "part.usda");
            using (UsdStage partStage = UsdStage.CreateNew(part))
            {
                partStage.DefaultPrim = partStage.DefinePrim("/Part", "Xform");
                partStage.DefinePrim("/Part/Box", "Cube");
                partStage.Save();
            }

            using (UsdStage stage = UsdStage.CreateInMemory())
            {
                UsdPrim prop = stage.DefinePrim("/Prop", "Xform");
                prop.AddVariant("look", "red");
                prop.AddVariant("look", "blue");
                prop.SetVariantSelection("look", "red");
                stage.EditTarget = UsdEditTarget.RootLayer.InVariant("/Prop", "look", "red");
                prop.GetAttribute("tint").Set(Color.red, typeName: "color3f");
                stage.EditTarget = UsdEditTarget.RootLayer;
                prop.SetVariantSelection("look", "blue");
                stage.EditTarget = UsdEditTarget.RootLayer.InVariant("/Prop", "look", "blue");
                prop.GetAttribute("tint").Set(Color.blue, typeName: "color3f");
                Assert.Throws<UsdStageException>(
                    () => stage.EditTarget = UsdEditTarget.RootLayer.InVariant("/Prop", "look", "red"),
                    "only the selected variant can be edited");
                stage.EditTarget = UsdEditTarget.RootLayer;

                Assert.That(prop.GetAttribute("tint").Get<Color>(), Is.EqualTo(Color.blue));
                UsdVariantSet look = prop.GetVariantSet("look");
                Assert.That(look.Selection, Is.EqualTo("blue"));
                Assert.That(look.Variants, Is.EquivalentTo(new[] { "red", "blue" }));

                stage.EditTarget = UsdEditTarget.SessionLayer;
                look.Select("red");
                Assert.That(prop.GetAttribute("tint").Get<Color>(), Is.EqualTo(Color.red), "a session selection is stronger");
                stage.EditTarget = UsdEditTarget.RootLayer;

                UsdPrim instance = stage.DefinePrim("/Instance", "Xform");
                instance.AddReference(part);
                Assert.That(instance.HasReferences, Is.True);
                Assert.That(stage.GetPrimAtPath("/Instance/Box").TypeName, Is.EqualTo("Cube"), "the reference composes");
                UsdPrim lazy = stage.DefinePrim("/Lazy", "Xform");
                lazy.AddPayload(part);
                Assert.That(stage.GetPrimAtPath("/Lazy/Box").IsValid, Is.True, "loaded");
                lazy.Unload();
                Assert.That(stage.GetPrimAtPath("/Lazy/Box").IsValid, Is.False, "unloaded");
                Assert.That(lazy.IsLoaded, Is.False);
                lazy.Load();
                Assert.That(stage.GetPrimAtPath("/Lazy/Box").IsValid, Is.True);

                instance.GetRelationship("material:binding").SetTargets(new[] { "/Looks/Wood" });
                Assert.That(instance.GetRelationship("material:binding").Targets, Is.EqualTo(new[] { "/Looks/Wood" }));
                instance.SetActive(false);
                Assert.That(stage.GetPrimAtPath("/Instance").IsActive, Is.False);
                Assert.That(stage.PseudoRoot.Children.Select(p => p.Name), Is.EquivalentTo(new[] { "Prop", "Lazy" }));
                Assert.That(stage.PseudoRoot.AllChildren.Select(p => p.Name), Is.EquivalentTo(new[] { "Prop", "Instance", "Lazy" }));

                string flat = Path.Combine(directory, "flat.usda");
                stage.Export(flat, flatten: true);
                Assert.That(File.ReadAllText(flat), Does.Not.Contain("variantSet").And.Not.Contain("references"));
            }
        }

        [Test]
        public void TransformsAndCoordinates()
        {
            using (UsdStage stage = UsdStage.CreateInMemory())
            {
                UsdPrim parent = stage.DefinePrim("/Parent", "Xform");
                UsdPrim child = stage.DefinePrim("/Parent/Child", "Xform");
                parent.SetLocalTransform(Matrix4x4.Translate(new Vector3(1, 0, 0)));
                child.SetLocalTransform(Matrix4x4.Translate(new Vector3(0, 2, 0)), 5);
                Assert.That(child.GetWorldTransform(5).GetColumn(3), Is.EqualTo(new Vector4(1, 2, 0, 1)), "world = parent x local");
                Assert.That(child.GetLocalTransform(5, out bool resets).GetColumn(3), Is.EqualTo(new Vector4(0, 2, 0, 1)));
                Assert.That(resets, Is.False);

                // The importer's basis: USD (x, y, z) is Unity (-x, y, z).
                Assert.That(UsdCoordinates.ToUnity(new Vector3(1, 2, 3)), Is.EqualTo(new Vector3(-1, 2, 3)));
                Matrix4x4 unity = UsdCoordinates.ToUnity(parent.GetLocalTransform());
                Assert.That(unity.MultiplyPoint(Vector3.zero), Is.EqualTo(new Vector3(-1, 0, 0)));
                Quaternion turn = Quaternion.Euler(0, 30, 0);
                Assert.That(Quaternion.Angle(UsdCoordinates.FromUnity(UsdCoordinates.ToUnity(turn)), turn), Is.LessThan(1e-4f));
                Matrix4x4 rotation = Matrix4x4.Rotate(turn);
                Vector3 point = new Vector3(1, 2, 3);
                Assert.That(Vector3.Distance(UsdCoordinates.ToUnity(rotation).MultiplyPoint(UsdCoordinates.ToUnity(point)),
                                             UsdCoordinates.ToUnity(rotation.MultiplyPoint(point))), Is.LessThan(1e-5f),
                            "a matrix and a point convert alike");
            }
        }

        [Test]
        public void ADisposedStageAndBadPathsAreRefused()
        {
            UsdStage stage = UsdStage.CreateInMemory();
            UsdPrim prim = stage.DefinePrim("/Thing");
            stage.Dispose();
            Assert.That(stage.IsOpen, Is.False);
            Assert.That(prim.IsValid, Is.False, "a prim of a closed stage is not valid");
            Assert.Throws<ObjectDisposedException>(() => _ = prim.TypeName);
            Assert.Throws<FileNotFoundException>(() => UsdStage.Open(Path.Combine(directory, "nope.usda")));

            using (UsdStage open = UsdStage.CreateInMemory())
            {
                Assert.Throws<ArgumentException>(() => open.GetPrimAtPath("relative"));
                Assert.That(open.GetPrimAtPath("/Nothing").IsValid, Is.False);
                Assert.That(open.DefaultPrim.IsValid, Is.False, "no defaultPrim");
                Assert.Throws<UsdStageException>(() => open.Save(), "an in-memory stage has no file");
                Assert.Throws<ArgumentException>(() => open.GetPrimAtPath("/A").GetAttribute("x").Set(1, UsdTimeCode.EarliestTime));
            }
        }

        [Test]
        public void AnImportedStageReadsThroughTheApi()
        {
            string path = UsdFixtures.PathOf("Skel", "skel_arm.usda");
            using (ImportedStage imported = ImportedStage.Import(path))
            {
                UsdStage stage = imported.Result.Source.Stage;
                Assert.That(stage.IsReadOnly, Is.True);
                UsdPrim[] skeletons = stage.Traverse().Where(prim => prim.TypeName == "Skeleton").ToArray();
                Assert.That(skeletons, Is.Not.Empty);
                string[] joints = skeletons[0].GetAttribute("joints").Get<string[]>();
                Assert.That(joints, Is.Not.Empty);
                Assert.Throws<InvalidOperationException>(() => skeletons[0].SetActive(false), "read-only");
            }
        }
    }
}
