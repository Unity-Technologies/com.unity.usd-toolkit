using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace Unity.USDToolkit.Samples.SimReadyCentral.Tests
{
    /// <summary>
    /// The dependency walk over Tests/Fixtures~/walk, served by <see cref="FakeHubTransport"/>:
    /// a root with a sublayer, a reference, a payload per variant (selected and not), textures,
    /// and asset paths that leave the folder or name another location.
    /// </summary>
    public sealed class SimReadyDownloaderTests
    {
        private const string Root = "walkpkg/simready_usd/root.usda";

        private string cache;
        private FakeHubTransport hub;

        [SetUp]
        public void SetUp()
        {
            cache = SimReadyFixtures.TemporaryFolder();
            hub = new FakeHubTransport(SimReadyFixtures.PathOf("walk"));
            SimReadyLayerScanner.PreferNative = true;
        }

        [TearDown]
        public void TearDown()
        {
            SimReadyLayerScanner.PreferNative = true;
            if (Directory.Exists(cache))
            {
                Directory.Delete(cache, true);
            }
        }

        private Task<SimReadyDownloadResult> Download(SimReadyHostPolicy policy = null, string link = null, CancellationToken cancellation = default) =>
            new SimReadyDownloader(policy ?? SimReadyHostPolicy.CreateDefault(), cache, hub).DownloadAsync(link ?? hub.Link(Root), null, cancellation);

        [Test]
        public async Task TheWalkFetchesEveryLayerAndTextureEveryVariantNames()
        {
            SimReadyDownloadResult result = await Download();
            Assert.That(result.Files, Is.SupersetOf(new[]
            {
                Root,
                "walkpkg/simready_usd/layers/sub.usda",
                "walkpkg/simready_usd/payloads/base.usda",
                // A payload of the selected variant and one of the variant that is not selected.
                "walkpkg/simready_usd/payloads/Physics/a.usda",
                "walkpkg/simready_usd/payloads/Physics/b.usda",
                // Named by the sublayer, relative to the sublayer's folder.
                "walkpkg/simready_usd/payloads/shared.usda",
                "walkpkg/simready_usd/textures/t.png",
                // Named by the unselected variant's payload.
                "walkpkg/simready_usd/textures/b.png",
            }));
            Assert.That(result.Files, Does.Not.Contain("walkpkg/simready_usd/textures/unused.png"), "nothing names it");
            Assert.That(result.UnreferencedFiles, Is.EqualTo(1));
            Assert.That(File.Exists(result.RootLocalPath), Is.True);
            Assert.That(result.Commit, Is.EqualTo(FakeHubTransport.Sha));
            Assert.That(result.ValidationLocalPath, Does.EndWith("walkpkg_validation.json"));
        }

        [Test]
        public async Task PathsThatLeaveTheFolderOrNameAnotherLocationAreNotFetched()
        {
            SimReadyDownloadResult result = await Download();
            string warnings = string.Join("\n", result.Warnings);
            Assert.That(warnings, Does.Contain("@../../outside.usda@ (leaves the asset's folder"));
            Assert.That(warnings, Does.Contain("@/absolute.usda@ (absolute"));
            Assert.That(warnings, Does.Contain("@https://example.com/x.usda@ (absolute"));
            Assert.That(warnings, Does.Contain("<UDIM>"));
            Assert.That(warnings, Does.Contain("missing.png is referenced but not in the repo"));
            Assert.That(warnings, Does.Not.Contain("not_a_dependency"), "a comment is not an asset path");
            Assert.That(hub.Requests.Keys.All(path => path.StartsWith("walkpkg/", System.StringComparison.Ordinal)), Is.True);
        }

        [Test]
        public async Task TheTextScanFindsTheSameFilesWithoutTheNativeReader()
        {
            SimReadyLayerScanner.PreferNative = false;
            SimReadyDownloadResult result = await Download();
            Assert.That(result.Files, Does.Contain("walkpkg/simready_usd/payloads/Physics/b.usda"));
            Assert.That(result.Files, Does.Contain("walkpkg/simready_usd/payloads/shared.usda"));
            Assert.That(result.UsedFolderFallback, Is.False, "every layer is text");
        }

        [Test]
        public void TheNativeReaderAndTheTextScanAgree()
        {
            Assume.That(UsdLayerInfo.IsSupported, "needs the 0.8.2 native plugin");
            string root = SimReadyFixtures.PathOf("walk/" + Root);
            UsdLayerInfo native = UsdLayerInfo.Read(root);
            SimReadyLayerScan text = SimReadyLayerScanner.ScanText(File.ReadAllText(root));
            Assert.That(text.AssetPaths, Is.EquivalentTo(native.AllAssetPaths));
            Assert.That(native.Payloads, Is.EquivalentTo(new[] { "./payloads/Physics/a.usda", "./payloads/Physics/b.usda" }));
            Assert.That(native.SubLayers, Is.EquivalentTo(new[] { "./layers/sub.usda" }));
            Assert.That(native.UpAxis, Is.EqualTo("Z"));
            Assert.That(native.CustomLayerData["SimReady_Metadata:asset_name"], Is.EqualTo("walk"));
            Assert.That(text.CustomLayerData["SimReady_Metadata:asset_name"], Is.EqualTo("walk"));
        }

        [Test]
        public async Task LfsFilesAreCheckedAgainstTheirSha256()
        {
            hub.Lfs.Add("walkpkg/simready_usd/textures/t.png");
            SimReadyDownloadResult result = await Download();
            Assert.That(result.Files, Does.Contain("walkpkg/simready_usd/textures/t.png"));
        }

        [Test]
        public void AFileThatDoesNotMatchItsListedHashIsDeleted()
        {
            hub.Corrupt.Add("walkpkg/simready_usd/payloads/base.usda");
            var exception = Assert.ThrowsAsync<SimReadyDownloadException>(() => Download());
            Assert.That(exception.Message, Does.Contain("does not match the size and hash"));
            Assert.That(Directory.GetFiles(cache, "base.usda*", SearchOption.AllDirectories), Is.Empty);
        }

        [Test]
        public async Task AnInterruptedFileResumesFromItsPart()
        {
            hub.InterruptAfter["walkpkg/simready_usd/root.usda"] = 100;
            Assert.ThrowsAsync<SimReadyDownloadException>(() => Download());
            SimReadyDownloadResult result = await Download();
            Assert.That(hub.Requests["walkpkg/simready_usd/root.usda"], Is.EqualTo(new[] { 0L, 100L }), "the second request asks for the rest");
            Assert.That(File.ReadAllText(result.RootLocalPath), Is.EqualTo(File.ReadAllText(SimReadyFixtures.PathOf("walk/" + Root))));
        }

        [Test]
        public async Task AServerThatIgnoresTheRangeStartsTheFileOver()
        {
            hub.HonourRanges = false;
            hub.InterruptAfter["walkpkg/simready_usd/root.usda"] = 100;
            Assert.ThrowsAsync<SimReadyDownloadException>(() => Download());
            SimReadyDownloadResult result = await Download();
            Assert.That(File.ReadAllText(result.RootLocalPath), Is.EqualTo(File.ReadAllText(SimReadyFixtures.PathOf("walk/" + Root))));
        }

        [Test]
        public void TheSizeCapsStopTheWalk()
        {
            SimReadyHostPolicy small = SimReadyHostPolicy.CreateDefault();
            small.MaxFileBytes = 200;
            Assert.That(Assert.ThrowsAsync<SimReadyDownloadException>(() => Download(small)).Message, Does.Contain("per-file cap"));

            SimReadyHostPolicy total = SimReadyHostPolicy.CreateDefault();
            total.MaxTotalBytes = 1200;
            Assert.That(Assert.ThrowsAsync<SimReadyDownloadException>(() => Download(total)).Message, Does.Contain("total cap"));
        }

        [Test]
        public async Task APinnedLinkLoadsFromTheCacheTheSecondTime()
        {
            await Download();
            int listings = hub.TreeRequests;
            SimReadyDownloadResult again = await Download();
            Assert.That(again.FromCache, Is.True);
            Assert.That(hub.TreeRequests, Is.EqualTo(listings), "no listing, no download");
        }

        [Test]
        public async Task ABranchLinkIsResolvedToItsCommit()
        {
            SimReadyDownloadResult result = await Download(link: hub.Link(Root, "resolve", "main"));
            Assert.That(result.Commit, Is.EqualTo(FakeHubTransport.Sha));
            Assert.That(result.CacheFolder, Does.EndWith(FakeHubTransport.Sha));
        }

        [Test]
        public async Task AFolderLinkFindsTheRootLayer()
        {
            SimReadyDownloadResult result = await Download(link: hub.Link("walkpkg", "tree"));
            Assert.That(result.RootRepoPath, Is.EqualTo(Root));
        }

        [Test]
        public void CancellingStopsTheWalk()
        {
            using (var cancel = new CancellationTokenSource())
            {
                cancel.Cancel();
                Assert.CatchAsync<System.OperationCanceledException>(() => Download(cancellation: cancel.Token));
            }
        }

        [Test]
        public void TheTreeListingParses()
        {
            SimReadyTreeEntry[] entries = SimReadyTreeEntry.ParseList(
                "[{\"type\":\"file\",\"oid\":\"aa\",\"size\":3,\"path\":\"a/b.usda\"}," +
                "{\"type\":\"file\",\"oid\":\"bb\",\"size\":9,\"path\":\"a/c.usd\",\"lfs\":{\"oid\":\"cc\",\"size\":9,\"pointerSize\":130},\"xetHash\":\"dd\"}," +
                "{\"type\":\"directory\",\"oid\":\"ee\",\"size\":0,\"path\":\"a/d\"}]");
            Assert.That(entries.Length, Is.EqualTo(3));
            Assert.That(entries[0].lfs, Is.Null);
            Assert.That(entries[1].lfs.oid, Is.EqualTo("cc"));
            Assert.That(entries[2].type, Is.EqualTo("directory"));
        }

        [Test]
        public void MetadataReadsTheValidationProfile()
        {
            SimReadyMetadata.ReadValidation(File.ReadAllText(SimReadyFixtures.PathOf("walk/walkpkg/.metadata/walkpkg_validation.json")),
                                            out string profile, out string version);
            Assert.That(profile, Is.EqualTo("Test-Profile"));
            Assert.That(version, Is.EqualTo("1.2.3"));
        }
    }
}
