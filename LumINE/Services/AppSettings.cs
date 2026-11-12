using System;
using Windows.Storage;

namespace LumINE.Services
{
    public static class AppSettings
    {
        public static event Action<string> Changed;
        private static bool Read(string key, bool fallback) { var value = ApplicationData.Current.LocalSettings.Values["preferences." + key]; return value is bool ? (bool)value : fallback; }
        private static void Write(string key, bool value) { ApplicationData.Current.LocalSettings.Values["preferences." + key] = value; Changed?.Invoke(key); }
        public static bool RequireHello { get { return Read("RequireHello", false); } set { Write("RequireHello", value); } }
        public static bool HideMessageContent { get { return Read("HideMessageContent", false); } set { Write("HideMessageContent", value); } }
        public static bool AnimateStickers { get { return Read("AnimateStickers", true); } set { Write("AnimateStickers", value); } }
    }
}
