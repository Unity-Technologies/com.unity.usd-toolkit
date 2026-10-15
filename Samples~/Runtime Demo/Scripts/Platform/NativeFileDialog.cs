using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

// System.Diagnostics (for Process) also defines Debug; pin it to Unity's.
using Debug = UnityEngine.Debug;

namespace Unity.USDToolkit.RuntimeDemo
{
    /// <summary>
    /// Native OS open/save panels, coroutine driven.
    /// </summary>
    /// <remarks>
    /// macOS runs the panel out of process via osascript. That costs a process spawn, but it buys
    /// two things that matter on stage: the player keeps rendering while the panel is up (no
    /// beachball behind the dialog), and a misbehaving panel can never take the player down with
    /// it. Windows uses comdlg32 directly, which is modal on the calling thread.
    /// </remarks>
    public static class NativeFileDialog
    {
        public static bool IsSupported
        {
            get
            {
#if UNITY_STANDALONE_OSX || UNITY_EDITOR_OSX || UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
                return true;
#else
                return false;
#endif
            }
        }

        /// <summary>
        /// Shows an open panel. <paramref name="onComplete"/> receives an empty array when the
        /// user cancels; cancelling is a normal outcome, never an error.
        /// </summary>
        public static IEnumerator OpenFiles(
            string title,
            string startDirectory,
            IReadOnlyList<string> extensions,
            bool allowMultiple,
            Action<string[]> onComplete)
        {
#if UNITY_STANDALONE_OSX || UNITY_EDITOR_OSX
            string script = BuildOpenScript(title, startDirectory, extensions, allowMultiple);
            string stdout = null;
            yield return RunOsaScript(script, value => stdout = value);
            onComplete?.Invoke(SplitPaths(stdout));
#elif UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            onComplete?.Invoke(WindowsOpen(title, startDirectory, extensions, allowMultiple));
            yield break;
#else
            Debug.LogWarning("NativeFileDialog: no native panel on this platform.");
            onComplete?.Invoke(Array.Empty<string>());
            yield break;
#endif
        }

        /// <summary>
        /// Shows a save panel. <paramref name="onComplete"/> receives null when the user cancels.
        /// </summary>
        public static IEnumerator SaveFile(
            string title,
            string startDirectory,
            string defaultName,
            string requiredExtension,
            Action<string> onComplete)
        {
#if UNITY_STANDALONE_OSX || UNITY_EDITOR_OSX
            string script = BuildSaveScript(title, startDirectory, defaultName);
            string stdout = null;
            yield return RunOsaScript(script, value => stdout = value);
            string[] paths = SplitPaths(stdout);
            onComplete?.Invoke(paths.Length > 0 ? ForceExtension(paths[0], requiredExtension) : null);
#elif UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            string path = WindowsSave(title, startDirectory, defaultName, requiredExtension);
            onComplete?.Invoke(string.IsNullOrEmpty(path) ? null : ForceExtension(path, requiredExtension));
            yield break;
#else
            Debug.LogWarning("NativeFileDialog: no native panel on this platform.");
            onComplete?.Invoke(null);
            yield break;
#endif
        }

        private static string ForceExtension(string path, string extension)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(extension))
            {
                return path;
            }

            // The macOS save panel happily returns a name with no suffix, or with the wrong one
            // when the user retypes it. The export format is chosen in our UI, not in the panel,
            // so our choice is the authority.
            return string.Equals(Path.GetExtension(path), extension, StringComparison.OrdinalIgnoreCase)
                ? path
                : Path.ChangeExtension(path, extension);
        }

        private static string[] SplitPaths(string stdout)
        {
            if (string.IsNullOrWhiteSpace(stdout))
            {
                return Array.Empty<string>();
            }

            string[] lines = stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var result = new List<string>(lines.Length);
            foreach (string line in lines)
            {
                string trimmed = line.Trim();
                if (trimmed.Length > 0)
                {
                    result.Add(trimmed);
                }
            }

            return result.ToArray();
        }

#if UNITY_STANDALONE_OSX || UNITY_EDITOR_OSX
        private static string EscapeForAppleScript(string value)
        {
            return (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private static string BuildLocationClause(string startDirectory)
        {
            if (string.IsNullOrEmpty(startDirectory) || !Directory.Exists(startDirectory))
            {
                return string.Empty;
            }

            return " default location (POSIX file \"" + EscapeForAppleScript(startDirectory) + "\")";
        }

        private static string BuildOpenScript(
            string title,
            string startDirectory,
            IReadOnlyList<string> extensions,
            bool allowMultiple)
        {
            var typeClause = new StringBuilder();
            if (extensions != null && extensions.Count > 0)
            {
                typeClause.Append(" of type {");
                for (int i = 0; i < extensions.Count; i++)
                {
                    if (i > 0)
                    {
                        typeClause.Append(", ");
                    }

                    typeClause.Append('"').Append(EscapeForAppleScript(extensions[i].TrimStart('.'))).Append('"');
                }

                typeClause.Append('}');
            }

            var script = new StringBuilder();
            script.Append("set theSelection to choose file with prompt \"")
                  .Append(EscapeForAppleScript(title))
                  .Append('"')
                  .Append(typeClause)
                  .Append(BuildLocationClause(startDirectory));

            if (allowMultiple)
            {
                script.Append(" with multiple selections allowed");
            }

            script.AppendLine();
            // `choose file` returns a bare alias for a single pick and a list for a multi pick;
            // normalise to a list so one loop handles both.
            script.AppendLine("if class of theSelection is not list then set theSelection to {theSelection}");
            script.AppendLine("set output to \"\"");
            script.AppendLine("repeat with anItem in theSelection");
            script.AppendLine("    set output to output & POSIX path of anItem & linefeed");
            script.AppendLine("end repeat");
            script.Append("return output");
            return script.ToString();
        }

        private static string BuildSaveScript(string title, string startDirectory, string defaultName)
        {
            var script = new StringBuilder();
            script.Append("set theFile to choose file name with prompt \"")
                  .Append(EscapeForAppleScript(title))
                  .Append('"');

            if (!string.IsNullOrEmpty(defaultName))
            {
                script.Append(" default name \"").Append(EscapeForAppleScript(defaultName)).Append('"');
            }

            script.Append(BuildLocationClause(startDirectory));
            script.AppendLine();
            script.Append("return POSIX path of theFile");
            return script.ToString();
        }

        private static IEnumerator RunOsaScript(string script, Action<string> onComplete)
        {
            Process process = null;
            string stdout = string.Empty;
            string stderr = string.Empty;

            try
            {
                var startInfo = new ProcessStartInfo("/usr/bin/osascript")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                startInfo.ArgumentList.Add("-e");
                startInfo.ArgumentList.Add(script);

                process = Process.Start(startInfo);
            }
            catch (Exception exception)
            {
                Debug.LogWarning("NativeFileDialog: could not launch osascript. " + exception.Message);
                onComplete?.Invoke(null);
                yield break;
            }

            if (process == null)
            {
                onComplete?.Invoke(null);
                yield break;
            }

            // Drain both pipes on worker threads. Reading them inline after WaitForExit can
            // deadlock once a pipe buffer fills, and polling HasExited keeps the player rendering
            // while the panel is open.
            var outBuilder = new StringBuilder();
            var errBuilder = new StringBuilder();
            process.OutputDataReceived += (_, args) => { if (args.Data != null) { lock (outBuilder) { outBuilder.AppendLine(args.Data); } } };
            process.ErrorDataReceived += (_, args) => { if (args.Data != null) { lock (errBuilder) { errBuilder.AppendLine(args.Data); } } };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            while (!process.HasExited)
            {
                yield return null;
            }

            // Give the async readers a frame to flush whatever landed with the final write.
            yield return null;

            lock (outBuilder)
            {
                stdout = outBuilder.ToString();
            }

            lock (errBuilder)
            {
                stderr = errBuilder.ToString();
            }

            int exitCode = process.ExitCode;
            process.Dispose();

            if (exitCode != 0)
            {
                // -128 is "user cancelled", which is an ordinary outcome and must stay silent.
                if (stderr.IndexOf("-128", StringComparison.Ordinal) < 0)
                {
                    Debug.LogWarning("NativeFileDialog: osascript exited " + exitCode + ". " + stderr.Trim());
                }

                onComplete?.Invoke(null);
                yield break;
            }

            onComplete?.Invoke(stdout);
        }
#endif

#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct OpenFileName
        {
            public int lStructSize;
            public IntPtr hwndOwner;
            public IntPtr hInstance;
            public string lpstrFilter;
            public string lpstrCustomFilter;
            public int nMaxCustFilter;
            public int nFilterIndex;
            public IntPtr lpstrFile;
            public int nMaxFile;
            public string lpstrFileTitle;
            public int nMaxFileTitle;
            public string lpstrInitialDir;
            public string lpstrTitle;
            public int Flags;
            public short nFileOffset;
            public short nFileExtension;
            public string lpstrDefExt;
            public IntPtr lCustData;
            public IntPtr lpfnHook;
            public string lpTemplateName;
            public IntPtr pvReserved;
            public int dwReserved;
            public int flagsEx;
        }

        private const int OfnFileMustExist = 0x00001000;
        private const int OfnPathMustExist = 0x00000800;
        private const int OfnAllowMultiSelect = 0x00000200;
        private const int OfnExplorer = 0x00080000;
        private const int OfnOverwritePrompt = 0x00000002;
        private const int OfnNoChangeDir = 0x00000008;
        private const int FileBufferChars = 16384;

        [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool GetOpenFileNameW(ref OpenFileName ofn);

        [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool GetSaveFileNameW(ref OpenFileName ofn);

        private static string BuildWindowsFilter(IReadOnlyList<string> extensions)
        {
            var patterns = new StringBuilder();
            if (extensions != null)
            {
                foreach (string extension in extensions)
                {
                    if (patterns.Length > 0)
                    {
                        patterns.Append(';');
                    }

                    patterns.Append("*.").Append(extension.TrimStart('.'));
                }
            }

            if (patterns.Length == 0)
            {
                patterns.Append("*.*");
            }

            // comdlg32 wants a double-null terminated set of null separated pairs.
            return "USD files (" + patterns + ")\0" + patterns + "\0All files (*.*)\0*.*\0\0";
        }

        private static OpenFileName CreateOfn(
            string title,
            string startDirectory,
            IReadOnlyList<string> extensions,
            IntPtr buffer)
        {
            return new OpenFileName
            {
                lStructSize = Marshal.SizeOf<OpenFileName>(),
                lpstrFilter = BuildWindowsFilter(extensions),
                nFilterIndex = 1,
                lpstrFile = buffer,
                nMaxFile = FileBufferChars,
                lpstrInitialDir = Directory.Exists(startDirectory) ? startDirectory : null,
                lpstrTitle = title,
                Flags = OfnExplorer | OfnNoChangeDir
            };
        }

        private static string[] WindowsOpen(
            string title,
            string startDirectory,
            IReadOnlyList<string> extensions,
            bool allowMultiple)
        {
            IntPtr buffer = Marshal.AllocHGlobal(FileBufferChars * sizeof(char));
            try
            {
                for (int i = 0; i < 4; i++)
                {
                    Marshal.WriteByte(buffer, i, 0);
                }

                OpenFileName ofn = CreateOfn(title, startDirectory, extensions, buffer);
                ofn.Flags |= OfnFileMustExist | OfnPathMustExist;
                if (allowMultiple)
                {
                    ofn.Flags |= OfnAllowMultiSelect;
                }

                if (!GetOpenFileNameW(ref ofn))
                {
                    return Array.Empty<string>();
                }

                return ParseWindowsResult(buffer, allowMultiple);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private static string[] ParseWindowsResult(IntPtr buffer, bool multiSelect)
        {
            // Multi-select returns "dir\0name1\0name2\0\0"; single select returns "path\0".
            // A single-select result has only one terminator, and whatever follows it is
            // uninitialised memory or leftovers of the seeded default name, so stop there.
            if (!multiSelect)
            {
                string path = Marshal.PtrToStringUni(buffer);
                return string.IsNullOrEmpty(path) ? Array.Empty<string>() : new[] { path };
            }

            var segments = new List<string>();
            var current = new StringBuilder();
            for (int i = 0; i < FileBufferChars; i++)
            {
                char c = (char)Marshal.ReadInt16(buffer, i * sizeof(char));
                if (c == '\0')
                {
                    if (current.Length == 0)
                    {
                        break;
                    }

                    segments.Add(current.ToString());
                    current.Clear();
                    continue;
                }

                current.Append(c);
            }

            if (segments.Count == 0)
            {
                return Array.Empty<string>();
            }

            if (segments.Count == 1)
            {
                return new[] { segments[0] };
            }

            var files = new string[segments.Count - 1];
            for (int i = 1; i < segments.Count; i++)
            {
                files[i - 1] = Path.Combine(segments[0], segments[i]);
            }

            return files;
        }

        private static string WindowsSave(
            string title,
            string startDirectory,
            string defaultName,
            string requiredExtension)
        {
            IntPtr buffer = Marshal.AllocHGlobal(FileBufferChars * sizeof(char));
            try
            {
                string seed = defaultName ?? string.Empty;
                for (int i = 0; i < FileBufferChars; i++)
                {
                    char c = i < seed.Length ? seed[i] : '\0';
                    Marshal.WriteInt16(buffer, i * sizeof(char), c);
                    if (c == '\0' && i > seed.Length)
                    {
                        break;
                    }
                }

                OpenFileName ofn = CreateOfn(title, startDirectory, new[] { requiredExtension }, buffer);
                ofn.Flags |= OfnOverwritePrompt | OfnPathMustExist;
                ofn.lpstrDefExt = (requiredExtension ?? string.Empty).TrimStart('.');

                if (!GetSaveFileNameW(ref ofn))
                {
                    return null;
                }

                string[] result = ParseWindowsResult(buffer, false);
                return result.Length > 0 ? result[0] : null;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
#endif
    }
}
