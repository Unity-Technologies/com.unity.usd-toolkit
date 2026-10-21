using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Unity.USDToolkit
{
    // File paths recorded on imported objects. A file inside the Unity project is recorded by its
    // project path -- "Assets/..." or "Packages/<name>/...", as the AssetDatabase names it -- so a
    // prefab or a scene that records it holds no user name or checkout location and works the
    // same for everyone who opens the project. Anything else stays an absolute path. Project
    // paths only mean something in the Editor; a player resolves nothing and gets them back as
    // recorded.
    internal static class UsdProjectPaths
    {
        public static string ToRecorded(string path) => ToRecorded(path, null);

        // `roots` caches the project's roots (Assets and every package) across many calls; pass
        // null for one.
        public static string ToRecorded(string path, List<KeyValuePair<string, string>> roots)
        {
#if UNITY_EDITOR
            if (string.IsNullOrEmpty(path) || !Path.IsPathRooted(path))
            {
                return path;
            }

            string full;
            try
            {
                full = Path.GetFullPath(path).Replace('\\', '/');
            }
            catch (Exception)
            {
                return path;
            }

            foreach (KeyValuePair<string, string> root in roots ?? ProjectRoots())
            {
                if (full.StartsWith(root.Key, PathComparison))
                {
                    return root.Value + full.Substring(root.Key.Length);
                }
            }

            return path;
#else
            return path;
#endif
        }

        // Each folder the project names files in, as (absolute folder with a trailing slash,
        // project path prefix): Assets, then every registered package.
        public static List<KeyValuePair<string, string>> ProjectRoots()
        {
            var roots = new List<KeyValuePair<string, string>>();
#if UNITY_EDITOR
            roots.Add(new KeyValuePair<string, string>(
                Path.GetFullPath(Application.dataPath).Replace('\\', '/').TrimEnd('/') + "/", "Assets/"));
            foreach (UnityEditor.PackageManager.PackageInfo package in UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages())
            {
                if (!string.IsNullOrEmpty(package.resolvedPath) && !string.IsNullOrEmpty(package.assetPath))
                {
                    roots.Add(new KeyValuePair<string, string>(
                        Path.GetFullPath(package.resolvedPath).Replace('\\', '/').TrimEnd('/') + "/",
                        package.assetPath.TrimEnd('/') + "/"));
                }
            }
#endif
            return roots;
        }

        public static string Resolve(string recorded)
        {
            if (!IsProjectPath(recorded))
            {
                return recorded;
            }

#if UNITY_EDITOR
            return Path.GetFullPath(UnityEditor.FileUtil.GetPhysicalPath(recorded));
#else
            return recorded;
#endif
        }

        public static bool IsProjectPath(string path) =>
            !string.IsNullOrEmpty(path) && !Path.IsPathRooted(path) &&
            (path.StartsWith("Assets/", StringComparison.Ordinal) || path.StartsWith("Packages/", StringComparison.Ordinal));

        private static StringComparison PathComparison =>
            Application.platform == RuntimePlatform.LinuxEditor ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
    }
}
