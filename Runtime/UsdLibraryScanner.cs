using System;
using System.Collections.Generic;
using System.IO;

namespace Unity.USDToolkit
{
    public static class UsdLibraryScanner
    {
        private static readonly HashSet<string> UsdExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".usd",
            ".usda",
            ".usdc",
            ".usdz"
        };

        private static readonly string[] ThumbnailExtensions =
        {
            ".png",
            ".jpg",
            ".jpeg"
        };

        public static IReadOnlyList<UsdLibraryItem> ScanFolder(string folderPath, UsdLibraryScanOptions options = null)
        {
            if (string.IsNullOrWhiteSpace(folderPath))
            {
                throw new ArgumentException("Folder path is empty.", nameof(folderPath));
            }

            if (!Directory.Exists(folderPath))
            {
                throw new DirectoryNotFoundException(folderPath);
            }

            options ??= new UsdLibraryScanOptions();
            var result = new List<UsdLibraryItem>();
            SearchOption searchOption = options.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            foreach (string file in Directory.EnumerateFiles(folderPath, "*", searchOption))
            {
                if (!UsdExtensions.Contains(Path.GetExtension(file)))
                {
                    continue;
                }

                var fileInfo = new FileInfo(file);
                string thumbnailPath = options.IncludeCompanionThumbnails ? FindCompanionThumbnail(file) : string.Empty;
                result.Add(new UsdLibraryItem(
                    fileInfo.FullName,
                    fileInfo.Name,
                    fileInfo.Length,
                    fileInfo.LastWriteTimeUtc,
                    thumbnailPath));
            }

            result.Sort((left, right) => string.Compare(left.FileName, right.FileName, StringComparison.OrdinalIgnoreCase));
            return result;
        }

        private static string FindCompanionThumbnail(string usdPath)
        {
            string directory = Path.GetDirectoryName(usdPath);
            string nameWithoutExtension = Path.GetFileNameWithoutExtension(usdPath);
            if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(nameWithoutExtension))
            {
                return string.Empty;
            }

            foreach (string extension in ThumbnailExtensions)
            {
                string candidate = Path.Combine(directory, nameWithoutExtension + extension);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return string.Empty;
        }
    }

    public sealed class UsdLibraryScanOptions
    {
        public bool Recursive = true;
        public bool IncludeCompanionThumbnails = true;
    }

    public sealed class UsdLibraryItem
    {
        internal UsdLibraryItem(
            string path,
            string fileName,
            long fileSizeBytes,
            DateTime lastWriteTimeUtc,
            string thumbnailPath)
        {
            Path = path;
            FileName = fileName;
            FileSizeBytes = fileSizeBytes;
            LastWriteTimeUtc = lastWriteTimeUtc;
            ThumbnailPath = thumbnailPath;
        }

        public string Path { get; }
        public string FileName { get; }
        public long FileSizeBytes { get; }
        public DateTime LastWriteTimeUtc { get; }
        public string ThumbnailPath { get; }
    }
}
