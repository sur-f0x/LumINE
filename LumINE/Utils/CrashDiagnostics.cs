using System;
using System.IO;
using Windows.Storage;

namespace LumINE.Utils
{
    public static class CrashDiagnostics
    {
        private static readonly object Gate = new object();
        public static void Record(string stage, Exception error)
        {
            string text = DateTimeOffset.UtcNow.ToString("o") + " [" + stage + "]\n" + error;
            System.Diagnostics.Debug.WriteLine(text);
            try
            {
                lock (Gate)
                {
                    var directory = Path.Combine(ApplicationData.Current.LocalFolder.Path, "diagnostics");
                    Directory.CreateDirectory(directory);
                    File.WriteAllText(Path.Combine(directory, "last-error.txt"), text);
                }
            }
            catch { }
        }
    }
}
