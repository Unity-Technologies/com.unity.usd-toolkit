using System;
using NUnit.Framework;
using UnityEngine;

namespace Unity.USDToolkit.Samples.SimReadyCentral.Tests
{
    public sealed class SimReadyLinkTests
    {
        private const string Rg2 =
            "https://huggingface.co/datasets/onrobot/simready-grippers/blob/507c28d73ba3082a221e906e9800e22d1d00de3d/onrobot_rg2/simready_usd/onrobot_rg2.usda";

        [Test]
        public void ParsesTheSpacesCopyUrlForm()
        {
            Assert.That(SimReadyLink.TryParse(Rg2, out SimReadyLink link, out string error), Is.True, error);
            Assert.That(link.RepoType, Is.EqualTo("datasets"));
            Assert.That(link.RepoId, Is.EqualTo("onrobot/simready-grippers"));
            Assert.That(link.Kind, Is.EqualTo("blob"));
            Assert.That(link.Revision, Is.EqualTo("507c28d73ba3082a221e906e9800e22d1d00de3d"));
            Assert.That(link.IsCommit, Is.True);
            Assert.That(link.Path, Is.EqualTo("onrobot_rg2/simready_usd/onrobot_rg2.usda"));
            Assert.That(link.PackageFolder, Is.EqualTo("onrobot_rg2"));
        }

        [TestCase("blob")]
        [TestCase("resolve")]
        [TestCase("raw")]
        public void EveryFileFormRewritesToResolve(string kind)
        {
            Assert.That(SimReadyLink.TryParse(Rg2.Replace("/blob/", "/" + kind + "/"), out SimReadyLink link, out _), Is.True);
            Assert.That(link.DownloadUrl, Is.EqualTo(
                "https://huggingface.co/datasets/onrobot/simready-grippers/resolve/507c28d73ba3082a221e906e9800e22d1d00de3d/onrobot_rg2/simready_usd/onrobot_rg2.usda"));
        }

        [Test]
        public void ModelsReposAndBranchesAreAccepted()
        {
            Assert.That(SimReadyLink.TryParse("https://huggingface.co/models/acme/robots/resolve/main/arm/arm.usd", out SimReadyLink link, out _), Is.True);
            Assert.That(link.RepoType, Is.EqualTo("models"));
            Assert.That(link.IsCommit, Is.False);
            Assert.That(link.RevisionApiUrl, Is.EqualTo("https://huggingface.co/api/models/acme/robots/revision/main"));
        }

        [Test]
        public void TreeLinksNameAFolder()
        {
            Assert.That(SimReadyLink.TryParse("https://huggingface.co/datasets/onrobot/simready-grippers/tree/main/onrobot_2fg14", out SimReadyLink link, out _), Is.True);
            Assert.That(link.IsFolder, Is.True);
            Assert.That(link.PackageFolder, Is.EqualTo("onrobot_2fg14"));
            Assert.That(link.TreeApiUrl("onrobot_2fg14"), Is.EqualTo("https://huggingface.co/api/datasets/onrobot/simready-grippers/tree/main/onrobot_2fg14?recursive=true"));
        }

        [Test]
        public void PinningKeepsTheRestOfTheLink()
        {
            SimReadyLink.TryParse("https://huggingface.co/datasets/a/b/blob/main/x/y.usda", out SimReadyLink link, out _);
            SimReadyLink pinned = link.AtCommit(new string('a', 40));
            Assert.That(pinned.IsCommit, Is.True);
            Assert.That(pinned.DownloadUrl, Is.EqualTo("https://huggingface.co/datasets/a/b/resolve/" + new string('a', 40) + "/x/y.usda"));
            Assert.Throws<ArgumentException>(() => link.AtCommit("main"));
        }

        [Test]
        public void QueryAndFragmentAreIgnoredAndEscapesDecoded()
        {
            Assert.That(SimReadyLink.TryParse("https://www.huggingface.co/datasets/a/b/blob/main/my%20asset/a.usd?download=true#x", out SimReadyLink link, out _), Is.True);
            Assert.That(link.Path, Is.EqualTo("my asset/a.usd"));
            Assert.That(link.DownloadUrl, Does.EndWith("/resolve/main/my%20asset/a.usd"));
        }

        [TestCase("http://huggingface.co/datasets/a/b/blob/main/x/y.usda", "https://")]
        [TestCase("https://evil.example/datasets/a/b/blob/main/x/y.usda", "huggingface.co")]
        [TestCase("https://huggingface.co.evil.example/datasets/a/b/blob/main/x/y.usda", "huggingface.co")]
        [TestCase("https://huggingface.co:8443/datasets/a/b/blob/main/x/y.usda", "port")]
        [TestCase("https://user@huggingface.co/datasets/a/b/blob/main/x/y.usda", "user")]
        [TestCase("https://huggingface.co/spaces/nvidia/simready-central/blob/main/app.usda", "Expected")]
        [TestCase("https://huggingface.co/datasets/a/b/commits/main/x/y.usda", "blob, resolve, raw or tree")]
        [TestCase("https://huggingface.co/datasets/a/b/blob/main/x/../../y.usda", "not allowed")]
        [TestCase("https://huggingface.co/datasets/a/b/blob/main/x/%2e%2e/y.usda", "not allowed")]
        [TestCase("https://huggingface.co/datasets/a/b/blob/main/x/y.py", ".usd")]
        [TestCase("https://huggingface.co/datasets/a/b/blob/main", "no file")]
        [TestCase("hf://huggingface.co/datasets/a/b/x.usda", "https://")]
        [TestCase("not a link", "Not a URL")]
        [TestCase("", "Paste")]
        public void AnythingElseIsRejectedWithTheReason(string text, string reason)
        {
            Assert.That(SimReadyLink.TryParse(text, out SimReadyLink link, out string error), Is.False);
            Assert.That(link, Is.Null);
            Assert.That(error, Does.Contain(reason));
        }
    }

    public sealed class SimReadyAssetPathsTests
    {
        [TestCase("./payloads/base.usda", "pkg/simready_usd/payloads/base.usda")]
        [TestCase("payloads/base.usda", "pkg/simready_usd/payloads/base.usda")]
        [TestCase("../Textures/a.png", "pkg/Textures/a.png")]
        [TestCase("./x/../y/./z.mdl", "pkg/simready_usd/y/z.mdl")]
        public void ResolvesRelativeToTheLayer(string authored, string expected)
        {
            Assert.That(SimReadyAssetPaths.TryResolve("pkg/simready_usd/root.usda", authored, "pkg", out string resolved, out string error), Is.True, error);
            Assert.That(resolved, Is.EqualTo(expected));
        }

        [TestCase("../../outside.usda", "leaves the asset's folder")]
        [TestCase("../../../../etc/passwd.usda", "climbs above")]
        [TestCase("/absolute.usda", "absolute")]
        [TestCase("C:/Windows/x.usda", "absolute")]
        [TestCase("\\\\server\\share\\x.usda", "absolute")]
        [TestCase("https://example.com/x.usda", "absolute")]
        [TestCase("omniverse://server/x.usda", "absolute")]
        [TestCase("./t.<UDIM>.png", "template")]
        [TestCase("a.usdz[inner.usda]", "package-relative")]
        [TestCase("./tool.exe", "not a data type")]
        [TestCase("./script.py", "not a data type")]
        [TestCase("./lib.dll", "not a data type")]
        [TestCase("sub\\file.usda", "backslash")]
        [TestCase("./trailing.usda.", "not allowed")]
        public void RefusesWhatLeavesTheFolderOrIsNotData(string authored, string reason)
        {
            Assert.That(SimReadyAssetPaths.TryResolve("pkg/simready_usd/root.usda", authored, "pkg", out string resolved, out string error), Is.False);
            Assert.That(resolved, Is.Null);
            Assert.That(error, Does.Contain(reason));
        }

        [Test]
        public void LocalPathsStayInTheCache()
        {
            string root = SimReadyFixtures.TemporaryFolder();
            Assert.That(SimReadyAssetPaths.LocalPath(root, "a/b.usda"), Does.StartWith(System.IO.Path.GetFullPath(root)));
            Assert.Throws<InvalidOperationException>(() => SimReadyAssetPaths.LocalPath(root, "../escape.usda"));
        }

        [Test]
        public void ExecutableHeadersAreRecognised()
        {
            Assert.That(SimReadyAssetPaths.LooksExecutable(new byte[] { (byte)'M', (byte)'Z', 0, 0 }, 4), Is.True, "PE");
            Assert.That(SimReadyAssetPaths.LooksExecutable(new byte[] { 0x7f, (byte)'E', (byte)'L', (byte)'F' }, 4), Is.True, "ELF");
            Assert.That(SimReadyAssetPaths.LooksExecutable(new byte[] { 0xcf, 0xfa, 0xed, 0xfe }, 4), Is.True, "Mach-O 64");
            Assert.That(SimReadyAssetPaths.LooksExecutable(new byte[] { (byte)'#', (byte)'!', (byte)'/', (byte)'b' }, 4), Is.True, "script");
            Assert.That(SimReadyAssetPaths.LooksExecutable(System.Text.Encoding.ASCII.GetBytes("#usda 1.0"), 8), Is.False);
            Assert.That(SimReadyAssetPaths.LooksExecutable(System.Text.Encoding.ASCII.GetBytes("PXR-USDC"), 8), Is.False);
            Assert.That(SimReadyAssetPaths.LooksExecutable(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' }, 4), Is.False);
        }
    }

    public sealed class SimReadyHostPolicyTests
    {
        [Test]
        public void TheDefaultsAreTheObservedHosts()
        {
            SimReadyHostPolicy policy = SimReadyHostPolicy.CreateDefault();
            Assert.That(policy.Hosts, Is.EquivalentTo(new[] { "huggingface.co", "us.aws.cdn.hf.co" }));
            Assert.That(policy.MaxFileBytes, Is.EqualTo(SimReadyHostPolicy.DefaultMaxFileBytes));
            Assert.That(policy.MaxTotalBytes, Is.EqualTo(SimReadyHostPolicy.DefaultMaxTotalBytes));
        }

        [TestCase("https://huggingface.co/api/x", true)]
        [TestCase("https://us.aws.cdn.hf.co/xet-bridge-us/abc", true)]
        [TestCase("https://HuggingFace.CO/x", true)]
        [TestCase("http://huggingface.co/x", false)]
        [TestCase("https://huggingface.co:444/x", false)]
        [TestCase("https://user:pass@huggingface.co/x", false)]
        [TestCase("https://eu.aws.cdn.hf.co/x", false)]
        [TestCase("https://cdn.hf.co.evil.example/x", false)]
        [TestCase("ftp://huggingface.co/x", false)]
        public void OnlyHttpsToListedHosts(string url, bool permitted)
        {
            Assert.That(SimReadyHostPolicy.CreateDefault().Permits(new Uri(url), out string error), Is.EqualTo(permitted), error);
        }

        [Test]
        public void TheUserCanAddAndRemoveHosts()
        {
            SimReadyHostPolicy policy = SimReadyHostPolicy.CreateDefault();
            Assert.That(policy.TryAddHost(" EU.aws.cdn.hf.co. ", out string error), Is.True, error);
            Assert.That(policy.Permits(new Uri("https://eu.aws.cdn.hf.co/x"), out _), Is.True);
            Assert.That(policy.TryAddHost("eu.aws.cdn.hf.co", out error), Is.False);
            Assert.That(error, Does.Contain("already"));
            Assert.That(policy.RemoveHost("eu.aws.cdn.hf.co"), Is.True);
            Assert.That(policy.Permits(new Uri("https://eu.aws.cdn.hf.co/x"), out _), Is.False);
        }

        [TestCase("*.hf.co", "Wildcards")]
        [TestCase("https://x.hf.co", "host name only")]
        [TestCase("x.hf.co:443", "host name only")]
        [TestCase("10.0.0.1", "IP addresses")]
        [TestCase("::1", "host name only")]
        [TestCase("localhost", "Local")]
        [TestCase("printer.local", "Local")]
        [TestCase("no_underscores.example", "valid DNS")]
        [TestCase("single", "valid DNS")]
        [TestCase("", "Enter")]
        public void UnsafeHostsCannotBeAdded(string host, string reason)
        {
            Assert.That(SimReadyHostPolicy.CreateDefault().TryAddHost(host, out string error), Is.False);
            Assert.That(error, Does.Contain(reason));
        }

        [Test]
        public void CapsAreBoundedAndSettingsPersist()
        {
            SimReadyHostPolicy.ClearSaved();
            try
            {
                SimReadyHostPolicy policy = SimReadyHostPolicy.Load();
                policy.MaxFileBytes = long.MaxValue;
                Assert.That(policy.MaxFileBytes, Is.EqualTo(SimReadyHostPolicy.CeilingFileBytes));
                policy.MaxFileBytes = 10L * 1024 * 1024;
                policy.MaxTotalBytes = 100L * 1024 * 1024;
                policy.TryAddHost("cdn-lfs.hf.co", out _);
                policy.Save();

                SimReadyHostPolicy loaded = SimReadyHostPolicy.Load();
                Assert.That(loaded.Hosts, Does.Contain("cdn-lfs.hf.co"));
                Assert.That(loaded.MaxFileBytes, Is.EqualTo(10L * 1024 * 1024));
                Assert.That(loaded.MaxTotalBytes, Is.EqualTo(100L * 1024 * 1024));

                // A stored host that does not validate (hand-edited prefs) is dropped, not trusted.
                PlayerPrefs.SetString("Unity.USDToolkit.SimReadyCentral.AllowedHosts", "huggingface.co\n*.evil\n127.0.0.1");
                Assert.That(SimReadyHostPolicy.Load().Hosts, Is.EquivalentTo(new[] { "huggingface.co" }));
            }
            finally
            {
                SimReadyHostPolicy.ClearSaved();
            }
        }
    }
}
