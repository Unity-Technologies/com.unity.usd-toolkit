using System.IO;
using System.Text;
using NUnit.Framework;

namespace Unity.USDToolkit.Tests
{
    /// <summary>
    /// A stage's time metadata read from files (Tests/Fixtures~/Time, written by make_time.py):
    /// the same values from a text .usda and a binary .usdc of the same layer, the rates USD falls
    /// back to when a layer does not author them, and no time range unless both ends are authored.
    /// </summary>
    public sealed class UsdStageTimeMetadataTests
    {
        private const string Folder = "Time";

        [TestCase("range_24", true, 0.0, 48.0, 24.0, 24.0)]
        [TestCase("fps12_only", true, -6.0, 30.0, 12.0, 12.0)]
        [TestCase("start_only", false, 10.0, 0.0, 30.0, 24.0)]
        [TestCase("no_time", false, 0.0, 0.0, 24.0, 24.0)]
        public void TimeMetadataIsTheSameInTextAndBinaryFiles(string fixture, bool hasTimeRange, double start, double end,
                                                               double timeCodesPerSecond, double framesPerSecond)
        {
            foreach (string extension in new[] { ".usda", ".usdc" })
            {
                string path = UsdFixtures.PathOf(Folder, fixture + extension);
                string magic = extension == ".usda" ? "#usda 1" : "PXR-USDC";
                Assert.That(Encoding.ASCII.GetString(File.ReadAllBytes(path), 0, magic.Length), Is.EqualTo(magic),
                            $"{fixture}{extension} is not in the format its extension names");

                using (UsdStage stage = UsdStage.Open(path, loadPayloads: false))
                {
                    string file = fixture + extension;
                    Assert.That(stage.HasTimeRange, Is.EqualTo(hasTimeRange), file);
                    Assert.That(stage.StartTimeCode, Is.EqualTo(start), file);
                    Assert.That(stage.EndTimeCode, Is.EqualTo(end), file);
                    Assert.That(stage.TimeCodesPerSecond, Is.EqualTo(timeCodesPerSecond), file);
                    Assert.That(stage.FramesPerSecond, Is.EqualTo(framesPerSecond), file);
                }
            }
        }
    }
}
