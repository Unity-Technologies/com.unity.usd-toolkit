using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Unity.USDToolkit.Samples.SimReadyCentral
{
    /// <summary>Where a download is: files and bytes so far, of those known so far.</summary>
    public struct SimReadyProgress
    {
        public string Stage;
        public string CurrentFile;
        public int FilesDone;
        public int FilesKnown;
        public long BytesDone;
        public long BytesKnown;

        public float Fraction => BytesKnown > 0 ? Mathf.Clamp01((float)BytesDone / BytesKnown) : 0f;

        public override string ToString() =>
            $"{Stage}: {FilesDone}/{FilesKnown} files, {SimReadyWebTransport.Megabytes(BytesDone)} of {SimReadyWebTransport.Megabytes(BytesKnown)}" +
            (string.IsNullOrEmpty(CurrentFile) ? string.Empty : $" ({CurrentFile})");
    }

    /// <summary>What a download produced: the root layer on disk and everything it pulled in.</summary>
    public sealed class SimReadyDownloadResult
    {
        public SimReadyLink Link;
        public string Commit;
        public string RootRepoPath;
        public string RootLocalPath;
        public string CacheFolder;
        public readonly List<string> Files = new List<string>();
        public readonly List<string> Warnings = new List<string>();
        public long TotalBytes;
        public bool FromCache;

        /// <summary>Files in the asset's folder no layer referenced (the tree API cross-check); not downloaded.</summary>
        public int UnreferencedFiles;

        /// <summary>The dependency walk could not read a binary layer and fell back to the folder listing.</summary>
        public bool UsedFolderFallback;

        /// <summary>The package's <c>.metadata/*_validation.json</c>, when it has one (the SimReady profile).</summary>
        public string ValidationLocalPath;

        /// <summary>The root layer's thumbnail (<c>.thumbs/256x256/&lt;root&gt;.png</c>), when the package has one.</summary>
        public string ThumbnailLocalPath;

        internal void Note(string repoPath)
        {
            if (repoPath.EndsWith("_validation.json", StringComparison.Ordinal))
            {
                ValidationLocalPath = LocalPathOf(repoPath);
            }
            else if (repoPath.Contains("/.thumbs/") && repoPath.EndsWith(".png", StringComparison.Ordinal))
            {
                ThumbnailLocalPath = LocalPathOf(repoPath);
            }
        }

        public string LocalPathOf(string repoPath) => SimReadyAssetPaths.LocalPath(CacheFolder, repoPath);
    }

    public sealed class SimReadyDownloadException : Exception
    {
        public SimReadyDownloadException(string message)
            : base(message)
        {
        }
    }

    /// <summary>
    /// Downloads a SimReady asset and everything it composes from: resolves the link's revision
    /// to a commit, lists the asset's folder with the Hub's tree API (for sizes, hashes and a
    /// cross-check), then walks the layers from the root, fetching each sublayer, reference,
    /// payload (of every variant) and texture they name, relative to the layer that names it.
    /// Every file is checked against the hash the Hub lists, every path against the asset's
    /// folder, every request against the <see cref="SimReadyHostPolicy"/>. A cancelled or
    /// interrupted file resumes from its <c>.part</c> on the next attempt.
    /// </summary>
    public sealed class SimReadyDownloader
    {
        private const long MaxApiBytes = 32L * 1024 * 1024;
        private const string PartSuffix = ".part";
        private const string ManifestFolder = ".simready";

        private static readonly Regex Sha = new Regex("^[0-9a-f]{40}$", RegexOptions.CultureInvariant);

        private readonly SimReadyHostPolicy policy;
        private readonly ISimReadyTransport transport;
        private readonly string cacheRoot;

        public SimReadyDownloader(SimReadyHostPolicy policy, string cacheRoot, ISimReadyTransport transport = null)
        {
            this.policy = policy ?? throw new ArgumentNullException(nameof(policy));
            this.cacheRoot = cacheRoot ?? throw new ArgumentNullException(nameof(cacheRoot));
            this.transport = transport ?? new SimReadyWebTransport(policy);
        }

        /// <summary>The cache: Library/SimReadyCache in the Editor (outside Assets/), persistentDataPath/SimReadyCache in a player.</summary>
        public static string DefaultCacheRoot
        {
            get
            {
#if UNITY_EDITOR
                return Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Library", "SimReadyCache"));
#else
                return Path.Combine(Application.persistentDataPath, "SimReadyCache");
#endif
            }
        }

        /// <summary>The folder a commit of a repo caches into.</summary>
        public string CommitFolder(SimReadyLink link, string commit) =>
            Path.Combine(cacheRoot, link.RepoType, link.Owner, link.Repo, commit);

        public async Task<SimReadyDownloadResult> DownloadAsync(string text, IProgress<SimReadyProgress> progress, CancellationToken cancellation)
        {
            if (!SimReadyLink.TryParse(text, out SimReadyLink link, out string error))
            {
                throw new SimReadyDownloadException(error);
            }

            return await DownloadAsync(link, progress, cancellation);
        }

        public async Task<SimReadyDownloadResult> DownloadAsync(SimReadyLink link, IProgress<SimReadyProgress> progress, CancellationToken cancellation)
        {
            var report = new SimReadyProgress { Stage = "Resolving revision" };
            progress?.Report(report);
            string commit = link.IsCommit ? link.Revision : await ResolveCommitAsync(link, cancellation);
            SimReadyLink pinned = link.AtCommit(commit);
            string folder = CommitFolder(link, commit);

            var result = new SimReadyDownloadResult { Link = pinned, Commit = commit, CacheFolder = folder };
            if (!pinned.IsFolder && TryUseManifest(result, pinned.Path))
            {
                report.Stage = "Cached";
                report.FilesDone = report.FilesKnown = result.Files.Count;
                report.BytesDone = report.BytesKnown = result.TotalBytes;
                progress?.Report(report);
                return result;
            }

            report.Stage = "Listing the asset";
            progress?.Report(report);
            Dictionary<string, SimReadyTreeEntry> tree = await ListTreeAsync(pinned, pinned.PackageFolder, cancellation);
            string root = pinned.IsFolder ? await FindRootAsync(pinned, tree, folder, cancellation) : pinned.Path;
            if (!tree.ContainsKey(root))
            {
                throw new SimReadyDownloadException($"{root} is not in {pinned.RepoId} at {commit.Substring(0, 8)}.");
            }

            result.RootRepoPath = root;
            await WalkAsync(pinned, tree, root, result, report, progress, cancellation);
            result.RootLocalPath = result.LocalPathOf(root);
            WriteManifest(result);
            return result;
        }

        // ---- Revision and listing ----

        private async Task<string> ResolveCommitAsync(SimReadyLink link, CancellationToken cancellation)
        {
            SimReadyResponse response = await transport.GetTextAsync(link.RevisionApiUrl, MaxApiBytes, cancellation);
            if (response.StatusCode == 401 || response.StatusCode == 403)
            {
                throw new SimReadyDownloadException($"{link.RepoId} is gated or private; this sample loads public assets only.");
            }

            if (!response.Ok)
            {
                throw new SimReadyDownloadException($"Could not resolve {link.Revision}: {response.Error}");
            }

            var revision = JsonUtility.FromJson<RevisionJson>(response.Text ?? "{}");
            if (revision == null || !Sha.IsMatch(revision.sha ?? string.Empty))
            {
                throw new SimReadyDownloadException($"The Hub did not name a commit for {link.Revision}.");
            }

            return revision.sha;
        }

        private async Task<Dictionary<string, SimReadyTreeEntry>> ListTreeAsync(SimReadyLink link, string folder, CancellationToken cancellation)
        {
            var tree = new Dictionary<string, SimReadyTreeEntry>(StringComparer.Ordinal);
            string url = link.TreeApiUrl(folder);
            for (int page = 0; url != null && page < 64; page++)
            {
                SimReadyResponse response = await transport.GetTextAsync(url, MaxApiBytes, cancellation);
                if (!response.Ok)
                {
                    throw new SimReadyDownloadException($"Could not list {folder} in {link.RepoId}: {response.Error}");
                }

                foreach (SimReadyTreeEntry entry in SimReadyTreeEntry.ParseList(response.Text))
                {
                    if (entry.type == "file" && SimReadyAssetPaths.IsInside(entry.path, link.PackageFolder))
                    {
                        tree[entry.path] = entry;
                    }
                }

                url = response.NextPage;
            }

            return tree;
        }

        // A /tree/ link: the root the package's metadata names, else the one layer at the shallowest depth.
        private async Task<string> FindRootAsync(SimReadyLink link, Dictionary<string, SimReadyTreeEntry> tree, string folder, CancellationToken cancellation)
        {
            string rootsFile = link.PackageFolder + "/.metadata/com.nvidia.simready.root_usds.json";
            if (tree.TryGetValue(rootsFile, out SimReadyTreeEntry roots))
            {
                await EnsureFileAsync(link, rootsFile, roots, folder, _ => { }, cancellation);
                var listed = JsonUtility.FromJson<RootsJson>(File.ReadAllText(SimReadyAssetPaths.LocalPath(folder, rootsFile)));
                foreach (string entry in listed?.entries ?? Array.Empty<string>())
                {
                    if (SimReadyAssetPaths.TryResolve(link.PackageFolder + "/x", entry, link.PackageFolder, out string resolved, out _) &&
                        SimReadyAssetPaths.IsUsdLayer(resolved) && tree.ContainsKey(resolved) && SimReadyAssetPaths.IsInside(resolved, link.Path))
                    {
                        return resolved;
                    }
                }
            }

            var candidates = new List<string>();
            int best = int.MaxValue;
            foreach (string path in tree.Keys)
            {
                if (!SimReadyAssetPaths.IsUsdLayer(path) || !SimReadyAssetPaths.IsInside(path, link.Path) || path.Contains("/payloads/"))
                {
                    continue;
                }

                int depth = path.Split('/').Length;
                if (depth < best)
                {
                    best = depth;
                    candidates.Clear();
                }

                if (depth == best)
                {
                    candidates.Add(path);
                }
            }

            if (candidates.Count == 1)
            {
                return candidates[0];
            }

            throw new SimReadyDownloadException(candidates.Count == 0
                ? $"No USD layer under {link.Path}."
                : $"{link.Path} holds several root layers ({string.Join(", ", candidates)}); paste the link of the one to load.");
        }

        // ---- The walk ----

        private async Task WalkAsync(SimReadyLink link, Dictionary<string, SimReadyTreeEntry> tree, string root, SimReadyDownloadResult result,
                                     SimReadyProgress report, IProgress<SimReadyProgress> progress, CancellationToken cancellation)
        {
            var queue = new Queue<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            long planned = 0;

            void Enqueue(string path)
            {
                if (seen.Add(path))
                {
                    queue.Enqueue(path);
                    if (tree.TryGetValue(path, out SimReadyTreeEntry known))
                    {
                        report.FilesKnown++;
                        report.BytesKnown += known.size;
                    }
                }
            }

            Enqueue(root);
            foreach (string extra in MetadataFiles(link, tree, root))
            {
                Enqueue(extra);
            }

            while (queue.Count > 0)
            {
                cancellation.ThrowIfCancellationRequested();
                string path = queue.Dequeue();
                if (!tree.TryGetValue(path, out SimReadyTreeEntry entry))
                {
                    result.Warnings.Add($"{path} is referenced but not in the repo; the import will report it missing.");
                    continue;
                }

                if (entry.size > policy.MaxFileBytes)
                {
                    throw new SimReadyDownloadException(
                        $"{path} is {SimReadyWebTransport.Megabytes(entry.size)}, over the {SimReadyWebTransport.Megabytes(policy.MaxFileBytes)} per-file cap (Download settings).");
                }

                planned += entry.size;
                if (planned > policy.MaxTotalBytes)
                {
                    throw new SimReadyDownloadException(
                        $"The asset needs more than the {SimReadyWebTransport.Megabytes(policy.MaxTotalBytes)} total cap (Download settings).");
                }

                report.Stage = "Downloading";
                report.CurrentFile = path;
                progress?.Report(report);
                long before = report.BytesDone;
                await EnsureFileAsync(link, path, entry, result.CacheFolder, received =>
                {
                    report.BytesDone = before + received;
                    progress?.Report(report);
                }, cancellation);

                report.BytesDone = before + entry.size;
                report.FilesDone++;
                result.Files.Add(path);
                result.TotalBytes += entry.size;
                result.Note(path);

                if (!SimReadyAssetPaths.IsUsdLayer(path))
                {
                    continue;
                }

                SimReadyLayerScan scan = SimReadyLayerScanner.Scan(result.LocalPathOf(path));
                foreach (string authored in scan.AssetPaths)
                {
                    if (SimReadyAssetPaths.TryResolve(path, authored, link.PackageFolder, out string dependency, out string why))
                    {
                        Enqueue(dependency);
                    }
                    else
                    {
                        result.Warnings.Add($"{path}: not fetching @{authored}@ ({why}).");
                    }
                }

                if (!scan.Complete && !result.UsedFolderFallback)
                {
                    // A binary layer this plugin cannot read on its own: take the folder the root is in.
                    result.UsedFolderFallback = true;
                    result.Warnings.Add(scan.Reason + " Fetching every file in the root layer's folder instead.");
                    string rootFolder = root.Substring(0, Math.Max(0, root.LastIndexOf('/')));
                    foreach (string candidate in tree.Keys)
                    {
                        if (SimReadyAssetPaths.IsInside(candidate, rootFolder) && !candidate.Contains("/.thumbs/") &&
                            SimReadyAssetPaths.IsAllowedData(candidate))
                        {
                            Enqueue(candidate);
                        }
                    }
                }
            }

            // The cross-check: what the folder holds that nothing referenced.
            string assetFolder = root.Substring(0, Math.Max(0, root.LastIndexOf('/')));
            foreach (string candidate in tree.Keys)
            {
                if (SimReadyAssetPaths.IsInside(candidate, assetFolder) && !seen.Contains(candidate) && !candidate.Contains("/.thumbs/"))
                {
                    result.UnreferencedFiles++;
                }
            }

            report.Stage = "Downloaded";
            report.CurrentFile = null;
            progress?.Report(report);
        }

        // The package's licence, validation report (the SimReady profile) and the root layer's
        // thumbnail, when it has them: small data files the info panel shows.
        private static IEnumerable<string> MetadataFiles(SimReadyLink link, Dictionary<string, SimReadyTreeEntry> tree, string root)
        {
            string rootFolder = root.Substring(0, Math.Max(0, root.LastIndexOf('/')));
            string thumbnail = rootFolder + "/.thumbs/256x256/" + root.Substring(root.LastIndexOf('/') + 1) + ".png";
            if (tree.TryGetValue(thumbnail, out SimReadyTreeEntry thumb) && thumb.size < 4 * 1024 * 1024)
            {
                yield return thumbnail;
            }

            foreach (string path in tree.Keys)
            {
                bool validation = path.StartsWith(link.PackageFolder + "/.metadata/", StringComparison.Ordinal) &&
                                  path.EndsWith("_validation.json", StringComparison.Ordinal) && tree[path].size < 4 * 1024 * 1024;
                bool licence = path == rootFolder + "/LICENSE.txt" || path == link.PackageFolder + "/LICENSE.txt";
                if (validation || licence)
                {
                    yield return path;
                }
            }
        }

        // ---- One file ----

        private async Task EnsureFileAsync(SimReadyLink link, string repoPath, SimReadyTreeEntry entry, string folder,
                                           Action<long> received, CancellationToken cancellation)
        {
            string local = SimReadyAssetPaths.LocalPath(folder, repoPath);
            if (File.Exists(local) && new FileInfo(local).Length == entry.size && Verify(local, entry))
            {
                received(entry.size);
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(local));
            string part = local + PartSuffix;
            long resumeFrom = File.Exists(part) ? new FileInfo(part).Length : 0;
            if (resumeFrom >= entry.size)
            {
                // A complete or overlong part from an earlier run: check it rather than refetch.
                if (resumeFrom == entry.size && Verify(part, entry))
                {
                    File.Move(part, local);
                    received(entry.size);
                    return;
                }

                File.Delete(part);
                resumeFrom = 0;
            }

            SimReadyResponse response = await transport.DownloadAsync(link.ResolveUrl(repoPath), part, resumeFrom,
                                                                      Math.Min(policy.MaxFileBytes, entry.size), received, cancellation);
            if (!response.Ok)
            {
                // Keep a part that grew: the next attempt resumes it. Drop one that did not.
                if (File.Exists(part) && new FileInfo(part).Length == 0)
                {
                    File.Delete(part);
                }

                throw new SimReadyDownloadException($"Downloading {repoPath} failed: {response.Error}");
            }

            long length = new FileInfo(part).Length;
            if (length != entry.size || !Verify(part, entry))
            {
                File.Delete(part);
                throw new SimReadyDownloadException($"{repoPath} does not match the size and hash the Hub lists for it; deleted.");
            }

            if (LooksExecutable(part))
            {
                File.Delete(part);
                throw new SimReadyDownloadException($"{repoPath} looks like a program, not data; deleted.");
            }

            if (File.Exists(local))
            {
                File.Delete(local);
            }

            File.Move(part, local);
        }

        // An LFS file carries its SHA-256; any other file its git blob SHA-1.
        internal static bool Verify(string path, SimReadyTreeEntry entry)
        {
            if (entry.lfs != null && !string.IsNullOrEmpty(entry.lfs.oid))
            {
                using (var sha = SHA256.Create())
                using (FileStream stream = File.OpenRead(path))
                {
                    return Hex(sha.ComputeHash(stream)) == entry.lfs.oid.ToLowerInvariant();
                }
            }

            if (string.IsNullOrEmpty(entry.oid))
            {
                return false;
            }

            return GitBlobSha1(path) == entry.oid.ToLowerInvariant();
        }

        internal static string GitBlobSha1(string path)
        {
            using (var sha = SHA1.Create())
            using (FileStream stream = File.OpenRead(path))
            {
                byte[] header = Encoding.ASCII.GetBytes($"blob {stream.Length}\0");
                sha.TransformBlock(header, 0, header.Length, null, 0);
                var buffer = new byte[81920];
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    sha.TransformBlock(buffer, 0, read, null, 0);
                }

                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                return Hex(sha.Hash);
            }
        }

        private static bool LooksExecutable(string path)
        {
            var head = new byte[8];
            int read;
            using (FileStream stream = File.OpenRead(path))
            {
                read = stream.Read(head, 0, head.Length);
            }

            return SimReadyAssetPaths.LooksExecutable(head, read);
        }

        private static string Hex(byte[] bytes)
        {
            var builder = new StringBuilder(bytes.Length * 2);
            foreach (byte b in bytes)
            {
                builder.Append(b.ToString("x2"));
            }

            return builder.ToString();
        }

        // ---- The manifest a finished walk leaves, so a pinned link loads offline ----

        private static string ManifestPath(string folder, string root) =>
            Path.Combine(folder, ManifestFolder, Regex.Replace(root, "[^A-Za-z0-9._-]", "_") + ".json");

        private static void WriteManifest(SimReadyDownloadResult result)
        {
            var manifest = new ManifestJson
            {
                root = result.RootRepoPath,
                files = result.Files.ToArray(),
                totalBytes = result.TotalBytes,
                warnings = result.Warnings.ToArray(),
                unreferenced = result.UnreferencedFiles,
                folderFallback = result.UsedFolderFallback,
            };
            string path = ManifestPath(result.CacheFolder, result.RootRepoPath);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, JsonUtility.ToJson(manifest, true));
        }

        private static bool TryUseManifest(SimReadyDownloadResult result, string root)
        {
            string path = ManifestPath(result.CacheFolder, root);
            if (!File.Exists(path))
            {
                return false;
            }

            ManifestJson manifest;
            try
            {
                manifest = JsonUtility.FromJson<ManifestJson>(File.ReadAllText(path));
            }
            catch (ArgumentException)
            {
                return false;
            }

            if (manifest == null || manifest.root != root || manifest.files == null)
            {
                return false;
            }

            foreach (string file in manifest.files)
            {
                if (!SimReadyAssetPaths.IsInside(file, result.Link.PackageFolder) || !File.Exists(result.LocalPathOf(file)))
                {
                    return false;
                }
            }

            result.RootRepoPath = root;
            result.RootLocalPath = result.LocalPathOf(root);
            result.Files.AddRange(manifest.files);
            result.Warnings.AddRange(manifest.warnings ?? Array.Empty<string>());
            result.TotalBytes = manifest.totalBytes;
            result.UnreferencedFiles = manifest.unreferenced;
            result.UsedFolderFallback = manifest.folderFallback;
            result.FromCache = true;
            foreach (string file in manifest.files)
            {
                result.Note(file);
            }

            return true;
        }

        /// <summary>Deletes everything this sample cached (Library/SimReadyCache or the player's).</summary>
        public static void ClearCache(string root)
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }

        [Serializable]
        private sealed class RevisionJson
        {
            public string sha;
        }

        [Serializable]
        private sealed class RootsJson
        {
            public string[] entries;
        }

        [Serializable]
        private sealed class ManifestJson
        {
            public string root;
            public string[] files;
            public long totalBytes;
            public string[] warnings;
            public int unreferenced;
            public bool folderFallback;
        }
    }

    /// <summary>One file of the Hub's tree API listing.</summary>
    [Serializable]
    public sealed class SimReadyTreeEntry
    {
        public string type;
        public string oid;
        public long size;
        public string path;
        public LfsJson lfs;

        [Serializable]
        public sealed class LfsJson
        {
            public string oid;
            public long size;
        }

        [Serializable]
        private sealed class ListJson
        {
            public SimReadyTreeEntry[] items;
        }

        /// <summary>The API's JSON array (JsonUtility reads no top-level arrays, so it is wrapped).</summary>
        public static SimReadyTreeEntry[] ParseList(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return Array.Empty<SimReadyTreeEntry>();
            }

            ListJson list = JsonUtility.FromJson<ListJson>("{\"items\":" + json + "}");
            if (list?.items == null)
            {
                return Array.Empty<SimReadyTreeEntry>();
            }

            foreach (SimReadyTreeEntry entry in list.items)
            {
                // JsonUtility makes an empty object for a missing "lfs".
                if (entry.lfs != null && string.IsNullOrEmpty(entry.lfs.oid))
                {
                    entry.lfs = null;
                }
            }

            return list.items;
        }
    }
}
