using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Unity.USDToolkit.Samples.SimReadyCentral.Tests
{
    /// <summary>The sample's own synthetic fixtures (Tests/Fixtures~); no downloaded asset is used in a test.</summary>
    public static class SimReadyFixtures
    {
        public static string Folder([CallerFilePath] string file = "") =>
            Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file) ?? ".", "..", "Fixtures~"));

        public static string PathOf(string relative) => Path.Combine(Folder(), relative.Replace('/', Path.DirectorySeparatorChar));

        public static string TemporaryFolder()
        {
            string path = Path.Combine(Path.GetTempPath(), "SimReadyTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }
    }

    /// <summary>
    /// Serves a local folder the way the Hugging Face Hub does for one repo: the revision API, the
    /// recursive tree API (with git blob SHA-1s, and SHA-256 for files marked LFS) and /resolve/
    /// downloads with range requests, so the downloader's walk runs without a network.
    /// </summary>
    public sealed class FakeHubTransport : ISimReadyTransport
    {
        public const string Sha = "0123456789abcdef0123456789abcdef01234567";

        private readonly string repoRoot;
        private readonly string repo;

        public FakeHubTransport(string repoRoot, string repo = "datasets/test/walk")
        {
            this.repoRoot = repoRoot;
            this.repo = repo;
        }

        /// <summary>Repo paths listed as LFS files (their tree entry carries a SHA-256).</summary>
        public readonly HashSet<string> Lfs = new HashSet<string>();

        /// <summary>Repo paths served with different bytes than listed.</summary>
        public readonly HashSet<string> Corrupt = new HashSet<string>();

        /// <summary>Repo paths whose download stops after this many bytes (once), as an interrupted connection does.</summary>
        public readonly Dictionary<string, long> InterruptAfter = new Dictionary<string, long>();

        /// <summary>Repo path -> how many times it was requested, and with which range start.</summary>
        public readonly Dictionary<string, List<long>> Requests = new Dictionary<string, List<long>>();

        public bool HonourRanges = true;
        public int TreeRequests;

        public string Link(string path, string kind = "blob", string revision = Sha) =>
            $"https://huggingface.co/{repo}/{kind}/{revision}/{path}";

        public Task<SimReadyResponse> GetTextAsync(string url, long maxBytes, CancellationToken cancellation)
        {
            if (url.Contains("/revision/"))
            {
                return Task.FromResult(new SimReadyResponse { StatusCode = 200, Text = "{\"sha\":\"" + Sha + "\",\"id\":\"test/walk\"}" });
            }

            if (url.Contains("/tree/"))
            {
                TreeRequests++;
                return Task.FromResult(new SimReadyResponse { StatusCode = 200, Text = Tree() });
            }

            return Task.FromResult(new SimReadyResponse { StatusCode = 404, Error = "HTTP 404" });
        }

        public Task<SimReadyResponse> DownloadAsync(string url, string partPath, long resumeFrom, long maxBytes, Action<long> bytesReceived,
                                                    CancellationToken cancellation)
        {
            const string marker = "/resolve/" + Sha + "/";
            int at = url.IndexOf(marker, StringComparison.Ordinal);
            string path = Uri.UnescapeDataString(url.Substring(at + marker.Length));
            if (!Requests.TryGetValue(path, out List<long> starts))
            {
                Requests[path] = starts = new List<long>();
            }

            starts.Add(resumeFrom);
            string local = Path.Combine(repoRoot, path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(local))
            {
                return Task.FromResult(new SimReadyResponse { StatusCode = 404, Error = "HTTP 404" });
            }

            byte[] bytes = File.ReadAllBytes(local);
            if (Corrupt.Contains(path))
            {
                bytes[bytes.Length - 1] ^= 0xff;
            }

            if (bytes.Length > maxBytes)
            {
                return Task.FromResult(new SimReadyResponse { StatusCode = 200, Error = "larger than the cap" });
            }

            bool ranged = resumeFrom > 0 && HonourRanges;
            long start = ranged ? resumeFrom : 0;
            long end = bytes.Length;
            bool interrupted = InterruptAfter.TryGetValue(path, out long cut);
            if (interrupted)
            {
                InterruptAfter.Remove(path);
                end = Math.Min(end, start + cut);
            }

            using (var stream = new FileStream(partPath, ranged ? FileMode.Append : FileMode.Create))
            {
                stream.Write(bytes, (int)start, (int)(end - start));
            }

            bytesReceived?.Invoke(end);
            return Task.FromResult(interrupted
                ? new SimReadyResponse { StatusCode = 206, Error = "connection reset" }
                : new SimReadyResponse { StatusCode = ranged ? 206 : 200, Resumed = ranged });
        }

        private string Tree()
        {
            var json = new StringBuilder("[");
            bool first = true;
            foreach (string file in Directory.GetFiles(repoRoot, "*", SearchOption.AllDirectories))
            {
                string path = file.Substring(repoRoot.Length + 1).Replace(Path.DirectorySeparatorChar, '/');
                byte[] bytes = File.ReadAllBytes(file);
                json.Append(first ? string.Empty : ",");
                first = false;
                json.Append("{\"type\":\"file\",\"oid\":\"").Append(GitBlobSha1(bytes)).Append("\",\"size\":").Append(bytes.Length)
                    .Append(",\"path\":\"").Append(path).Append('"');
                if (Lfs.Contains(path))
                {
                    json.Append(",\"lfs\":{\"oid\":\"").Append(Sha256(bytes)).Append("\",\"size\":").Append(bytes.Length).Append(",\"pointerSize\":131}");
                }

                json.Append('}');
            }

            return json.Append(']').ToString();
        }

        public static string GitBlobSha1(byte[] bytes)
        {
            using (var sha = SHA1.Create())
            {
                byte[] header = Encoding.ASCII.GetBytes($"blob {bytes.Length}\0");
                var all = new byte[header.Length + bytes.Length];
                Buffer.BlockCopy(header, 0, all, 0, header.Length);
                Buffer.BlockCopy(bytes, 0, all, header.Length, bytes.Length);
                return Hex(sha.ComputeHash(all));
            }
        }

        private static string Sha256(byte[] bytes)
        {
            using (var sha = SHA256.Create())
            {
                return Hex(sha.ComputeHash(bytes));
            }
        }

        private static string Hex(byte[] bytes)
        {
            var builder = new StringBuilder();
            foreach (byte b in bytes)
            {
                builder.Append(b.ToString("x2"));
            }

            return builder.ToString();
        }
    }
}
