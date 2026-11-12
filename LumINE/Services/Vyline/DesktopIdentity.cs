using System;

namespace LumINE.Services.Vyline
{
    /// <summary>
    /// LINE Desktop (DESKTOPWIN) identity — mirrors vyline stack/base/core/utils/devices.ts.
    /// X-Line-Application uses TAB (0x09) separators.
    /// </summary>
    public static class DesktopIdentity
    {
        public const string Device = "DESKTOPWIN";
        public const string AppVersion = "9.7.0.3556";
        public const string SystemName = "WINDOWS";
        public const string SystemVersion = "10.0.0-NT-x64";

        /// <summary>x-line-application: DESKTOPWIN\t{ver}\tWINDOWS\t{os-nt}</summary>
        public static string ApplicationHeader
        {
            get { return Device + "\t" + AppVersion + "\t" + SystemName + "\t" + SystemVersion; }
        }

        /// <summary>User-Agent: DESKTOP:WINDOWS:{os-nt}({ver})</summary>
        public static string UserAgent
        {
            get { return "DESKTOP:" + SystemName + ":" + SystemVersion + "(" + AppVersion + ")"; }
        }

        public const string Endpoint = "legy.line-apps.com";
        public const string Lal = "ja_JP";
        public const string Lpv = "1";
        public const string Lap = "5";
    }
}
