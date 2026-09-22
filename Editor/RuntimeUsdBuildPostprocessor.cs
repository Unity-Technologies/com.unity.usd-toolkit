using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using PackageManagerPackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace Unity.USDToolkit.Editor
{
    internal sealed class RuntimeUsdBuildPostprocessor : IPostprocessBuildWithReport
    {
        public int callbackOrder => 1000;

        public void OnPostprocessBuild(BuildReport report)
        {
            if (report.summary.platform != BuildTarget.StandaloneWindows64 &&
                report.summary.platform != BuildTarget.StandaloneOSX &&
                report.summary.platform != BuildTarget.StandaloneLinux64)
            {
                return;
            }

            bool isMacOS = report.summary.platform == BuildTarget.StandaloneOSX;
            bool isLinux = report.summary.platform == BuildTarget.StandaloneLinux64;
            string sourceRoot = GetPackagePluginRoot(isMacOS, isLinux);
            if (!Directory.Exists(sourceRoot))
            {
                Debug.LogWarning("Unity USD Toolkit native plugin folder was not found: " + sourceRoot);
                return;
            }

            string destinationRoot = isMacOS
                ? GetMacOSPluginPayloadPath(report.summary.outputPath)
                : isLinux
                    ? GetLinuxPluginPayloadPath(report.summary.outputPath)
                    : GetWindowsPluginPayloadPath(report.summary.outputPath);
            if (string.IsNullOrEmpty(destinationRoot))
            {
                Debug.LogWarning("Unity USD Toolkit could not resolve the player native plugin folder.");
                return;
            }

            // Windows만 평탄 레이아웃이라 형제 파일 정리. Linux/macOS는 self-contained 트리 그대로 복사.
            if (!isMacOS && !isLinux)
            {
                string windowsPluginParent = Path.GetDirectoryName(destinationRoot);
                if (!string.IsNullOrEmpty(windowsPluginParent))
                {
                    CleanOwnedRuntimeFiles(windowsPluginParent);
                }
            }

            CopyRuntimeTree(sourceRoot, destinationRoot);
        }

        private static string GetPackagePluginRoot(bool isMacOS, bool isLinux)
        {
            PackageManagerPackageInfo packageInfo =
                PackageManagerPackageInfo.FindForAssembly(typeof(RuntimeUsdBuildPostprocessor).Assembly);
            string packageRoot = packageInfo != null
                ? packageInfo.resolvedPath
                : Path.GetFullPath("Packages/com.unity.usd-toolkit");

            if (isMacOS)
            {
                return Path.Combine(packageRoot, "Runtime", "Plugins", "macOS");
            }

            if (isLinux)
            {
                return Path.Combine(packageRoot, "Runtime", "Plugins", "x86_64", "Linux");
            }

            return Path.Combine(packageRoot, "Runtime", "Plugins", "x86_64", "Windows");
        }

        private static string GetLinuxPluginPayloadPath(string outputPath)
        {
            string dataPath = GetBuildDataPath(outputPath);
            if (string.IsNullOrEmpty(dataPath))
            {
                return string.Empty;
            }

            return Path.Combine(dataPath, "Plugins", "x86_64", "Linux");
        }

        private static string GetWindowsPluginPayloadPath(string outputPath)
        {
            string dataPath = GetBuildDataPath(outputPath);
            if (string.IsNullOrEmpty(dataPath))
            {
                return string.Empty;
            }

            return Path.Combine(dataPath, "Plugins", "x86_64", "Windows");
        }

        private static string GetMacOSPluginPayloadPath(string outputPath)
        {
            if (string.IsNullOrEmpty(outputPath))
            {
                return string.Empty;
            }

            string appContents = Path.Combine(outputPath, "Contents");
            if (!Directory.Exists(appContents))
            {
                return string.Empty;
            }

            return Path.Combine(appContents, "PlugIns");
        }

        private static string GetBuildDataPath(string outputPath)
        {
            if (string.IsNullOrEmpty(outputPath))
            {
                return string.Empty;
            }

            string outputDirectory = Path.GetDirectoryName(outputPath);
            string playerName = Path.GetFileNameWithoutExtension(outputPath);
            if (string.IsNullOrEmpty(outputDirectory) || string.IsNullOrEmpty(playerName))
            {
                return string.Empty;
            }

            return Path.Combine(outputDirectory, playerName + "_Data");
        }

        private static void CopyRuntimeTree(string sourceRoot, string destinationRoot)
        {
            Directory.CreateDirectory(destinationRoot);
            CleanOwnedRuntimeFiles(destinationRoot);

            foreach (string file in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
            {
                string fileName = Path.GetFileName(file);
                if (file.EndsWith(".meta", StringComparison.OrdinalIgnoreCase) ||
                    file.EndsWith(".lib", StringComparison.OrdinalIgnoreCase) ||
                    file.EndsWith(".exp", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(fileName, ".gitkeep", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(fileName, "README.md", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string relativePath = MakeRelativePath(sourceRoot, file);
                string destination = Path.Combine(destinationRoot, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(destination));
                File.Copy(file, destination, true);
            }
        }

        private static void CleanOwnedRuntimeFiles(string destinationRoot)
        {
            DeleteMatchingFiles(destinationRoot, "UnityUSDToolkitNative.dll");
            DeleteMatchingFiles(destinationRoot, "UnityUSDToolkitNative.dylib");
            DeleteMatchingFiles(destinationRoot, "libUnityUSDToolkitNative.dylib");
            DeleteMatchingFiles(destinationRoot, "RobotUsdNative.dll");
            DeleteMatchingFiles(destinationRoot, "usd*.dll");
            DeleteMatchingFiles(destinationRoot, "libusd*.dylib");
            DeleteMatchingFiles(destinationRoot, "usd*.dylib");
            DeleteMatchingFiles(destinationRoot, "tbb*.dll");
            DeleteMatchingFiles(destinationRoot, "libtbb*.dylib");
            DeleteMatchingFiles(destinationRoot, "zlib*.dll");
            DeleteMatchingFiles(destinationRoot, "libz*.dylib");
            DeleteMatchingFiles(destinationRoot, "boost*.dll");
            DeleteMatchingFiles(destinationRoot, "libboost*.dylib");
            DeleteMatchingFiles(destinationRoot, "*.lib");
            DeleteMatchingFiles(destinationRoot, "*.exp");
            DeleteMatchingFiles(destinationRoot, "*.pdb");

            DeleteDirectory(Path.Combine(destinationRoot, "lib", "usd"));
            DeleteDirectory(Path.Combine(destinationRoot, "plugin", "usd"));
            DeleteDirectory(Path.Combine(destinationRoot, "share", "usd"));
            DeleteDirectory(Path.Combine(destinationRoot, "resources"));
        }

        private static void DeleteMatchingFiles(string root, string pattern)
        {
            foreach (string file in Directory.EnumerateFiles(root, pattern, SearchOption.TopDirectoryOnly))
            {
                File.Delete(file);
            }
        }

        private static void DeleteDirectory(string path)
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }

        private static string MakeRelativePath(string root, string path)
        {
            Uri rootUri = new Uri(AppendDirectorySeparator(Path.GetFullPath(root)));
            Uri pathUri = new Uri(Path.GetFullPath(path));
            return Uri.UnescapeDataString(rootUri.MakeRelativeUri(pathUri).ToString())
                .Replace('/', Path.DirectorySeparatorChar);
        }

        private static string AppendDirectorySeparator(string path)
        {
            if (path.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
            {
                return path;
            }

            return path + Path.DirectorySeparatorChar;
        }
    }
}
