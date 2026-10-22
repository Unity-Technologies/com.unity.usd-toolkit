using NUnit.Framework;

namespace Unity.USDToolkit.Tests
{
    /// <summary>
    /// The UsdSkel fixtures, their expected values and the import harness the skinning and
    /// animation tests rely on.
    /// </summary>
    public sealed class UsdSkelFixtureTests
    {
        private static readonly string[] Fixtures = { "skel_arm", "blender_arm" };

        [TestCaseSource(nameof(Fixtures))]
        public void ExpectedValuesMatchTheirFixture(string fixture)
        {
            ExpectedStage expected = UsdFixtures.LoadExpected("Skel", fixture);

            Assert.That(expected.source, Is.EqualTo(fixture + ".usda"));
            Assert.That(expected.stage.timeCodesPerSecond, Is.GreaterThan(0));
            Assert.That(expected.times, Is.Not.Empty);
            Assert.That(expected.skinned, Is.Not.Empty);
            foreach (ExpectedSkinnedMesh mesh in expected.skinned)
            {
                Assert.That(mesh.restPoints.Length % 3, Is.Zero, mesh.path);
                Assert.That(mesh.frames.Length, Is.EqualTo(expected.times.Length), mesh.path);
                foreach (ExpectedPointsFrame frame in mesh.frames)
                {
                    Assert.That(frame.points.Length, Is.EqualTo(mesh.restPoints.Length), $"{mesh.path} at {frame.time}");
                }
            }
        }

        [TestCaseSource(nameof(Fixtures))]
        public void FixtureImportsEveryBoundMeshAndItsSkeleton(string fixture)
        {
            ExpectedStage expected = UsdFixtures.LoadExpected("Skel", fixture);

            using (ImportedStage stage = ImportedStage.Import(UsdFixtures.PathOf("Skel", fixture + ".usda")))
            {
                foreach (ExpectedSkinnedMesh mesh in expected.skinned)
                {
                    Assert.That(stage.Find(mesh.path), Is.Not.Null, mesh.path);
                    Assert.That(stage.Find(mesh.skeleton), Is.Not.Null, mesh.skeleton);
                }
            }
        }
    }
}
