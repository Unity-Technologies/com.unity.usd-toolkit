using System.IO;
using System.Text;

namespace Unity.USDToolkit.Samples.AGVFleet.LiveSyncDemo
{
    /// <summary>Text files shared with Isaac Sim: replaced atomically, and left alone when unchanged.</summary>
    public static class AtomicFile
    {
        /// <returns>true when the file was written.</returns>
        public static bool WriteIfChanged(string path, string text)
        {
            try
            {
                if (File.Exists(path) && File.ReadAllText(path) == text) return false;
            }
            catch (IOException)
            {
                // unreadable: write it
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, text, new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
            return true;
        }
    }
}
