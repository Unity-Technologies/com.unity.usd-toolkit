using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Unity.USDToolkit.RobotPhysics
{
    /// <summary>
    /// Fetches a robot asset from NVIDIA's Isaac Sim asset server into a local folder, file by file
    /// as a manifest lists them, and checks each against the SHA-256 the manifest pins before the
    /// import reads anything. A file already there with the right digest is not fetched again.
    /// </summary>
    /// <remarks>
    /// OpenUSD resolves the robot's sublayers, references and payloads relative to its root layer,
    /// so the files keep the folder layout they have on the server. The manifest (see
    /// Robots/franka_panda_isaac_6.0.files.txt) has a <c>base</c> URL, the <c>root</c> layer and one
    /// <c>sha256  size  relative/path</c> line per file.
    /// </remarks>
    public sealed class RobotAssetDownload
    {
        public string BaseUrl { get; private set; }
        public string RootLayer { get; private set; }
        public IReadOnlyList<Entry> Files => files;

        private readonly List<Entry> files = new List<Entry>();

        public readonly struct Entry
        {
            public readonly string Sha256;
            public readonly long Size;
            public readonly string Path;

            public Entry(string sha256, long size, string path)
            {
                Sha256 = sha256;
                Size = size;
                Path = path;
            }
        }

        public long TotalBytes
        {
            get
            {
                long total = 0;
                foreach (Entry entry in files)
                {
                    total += entry.Size;
                }

                return total;
            }
        }

        public static RobotAssetDownload Parse(string manifest)
        {
            var download = new RobotAssetDownload();
            foreach (string raw in manifest.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                {
                    continue;
                }

                if (line.StartsWith("base ", StringComparison.Ordinal))
                {
                    download.BaseUrl = line.Substring(5).Trim();
                    continue;
                }

                if (line.StartsWith("root ", StringComparison.Ordinal))
                {
                    download.RootLayer = line.Substring(5).Trim();
                    continue;
                }

                string[] parts = line.Split(new[] { ' ' }, 3, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 3 || parts[0].Length != 64 || !long.TryParse(parts[1], out long size))
                {
                    throw new FormatException($"Robot manifest: cannot read line '{line}'.");
                }

                string path = parts[2].Trim().Replace('\\', '/');
                if (path.StartsWith("/", StringComparison.Ordinal) || path.Contains("..") || path.Contains(":"))
                {
                    throw new FormatException($"Robot manifest: '{path}' is not a relative path inside the asset.");
                }

                download.files.Add(new Entry(parts[0].ToLowerInvariant(), size, path));
            }

            if (string.IsNullOrEmpty(download.BaseUrl) || !download.BaseUrl.StartsWith("https://", StringComparison.Ordinal))
            {
                throw new FormatException("Robot manifest: no https base URL.");
            }

            if (string.IsNullOrEmpty(download.RootLayer) || !download.files.Exists(f => f.Path == download.RootLayer))
            {
                throw new FormatException("Robot manifest: the root layer is not among its files.");
            }

            return download;
        }

        /// <summary>The folder the robot is kept in: under <see cref="RobotPhysicsImport.DownloadRoot"/>.</summary>
        public static string DefaultFolder(string name) =>
            System.IO.Path.Combine(RobotPhysicsImport.DownloadRoot, name);

        /// <summary>
        /// True when every file is in <paramref name="folder"/> with its listed size. Cheaper than
        /// <see cref="IsComplete"/>, for showing whether a download is there; the fetch still checks digests.
        /// </summary>
        public bool HasFiles(string folder)
        {
            foreach (Entry entry in files)
            {
                var file = new FileInfo(System.IO.Path.Combine(folder, entry.Path));
                if (!file.Exists || file.Length != entry.Size)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>True when every file is in <paramref name="folder"/> with its pinned digest.</summary>
        public bool IsComplete(string folder)
        {
            foreach (Entry entry in files)
            {
                if (!IsValid(System.IO.Path.Combine(folder, entry.Path), entry))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Fetches what is missing or wrong into <paramref name="folder"/> and returns the root
        /// layer's path. A file whose digest does not match is deleted and the download fails.
        /// </summary>
        public async Task<string> FetchAsync(string folder, Action<float, string> progress = null, CancellationToken cancel = default)
        {
            Directory.CreateDirectory(folder);
            string fullFolder = System.IO.Path.GetFullPath(folder);
            long total = Math.Max(1, TotalBytes);
            long done = 0;
            using (var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
            {
                foreach (Entry entry in files)
                {
                    string target = System.IO.Path.GetFullPath(System.IO.Path.Combine(fullFolder, entry.Path));
                    if (!target.StartsWith(fullFolder, StringComparison.Ordinal))
                    {
                        throw new InvalidDataException($"Robot manifest: '{entry.Path}' leaves the download folder.");
                    }

                    cancel.ThrowIfCancellationRequested();
                    progress?.Invoke((float)done / total, entry.Path);

                    // Hashing runs on a worker thread; the await comes back to the caller's
                    // context (Unity's main thread), so `progress` is always called there.
                    if (!await Task.Run(() => IsValid(target, entry), cancel))
                    {
                        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target));
                        string temporary = target + ".part";
                        using (HttpResponseMessage response = await client.GetAsync(BaseUrl + Escape(entry.Path), HttpCompletionOption.ResponseHeadersRead, cancel))
                        {
                            response.EnsureSuccessStatusCode();
                            using (Stream source = await response.Content.ReadAsStreamAsync())
                            using (FileStream destination = File.Create(temporary))
                            {
                                await source.CopyToAsync(destination, 81920, cancel);
                            }
                        }

                        if (!await Task.Run(() => IsValid(temporary, entry), cancel))
                        {
                            File.Delete(temporary);
                            throw new InvalidDataException(
                                $"{entry.Path} from {BaseUrl} does not match the SHA-256 the manifest pins; the asset on the server changed. Nothing was imported.");
                        }

                        if (File.Exists(target))
                        {
                            File.Delete(target);
                        }

                        File.Move(temporary, target);
                    }

                    done += entry.Size;
                }
            }

            progress?.Invoke(1f, "Done");
            return System.IO.Path.Combine(fullFolder, RootLayer);
        }

        private static string Escape(string path)
        {
            string[] segments = path.Split('/');
            for (int i = 0; i < segments.Length; i++)
            {
                segments[i] = Uri.EscapeDataString(segments[i]);
            }

            return string.Join("/", segments);
        }

        private static bool IsValid(string path, Entry entry)
        {
            if (!File.Exists(path) || new FileInfo(path).Length != entry.Size)
            {
                return false;
            }

            using (SHA256 sha = SHA256.Create())
            using (FileStream stream = File.OpenRead(path))
            {
                byte[] hash = sha.ComputeHash(stream);
                return string.Equals(BitConverter.ToString(hash).Replace("-", string.Empty), entry.Sha256, StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}
