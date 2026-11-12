using System;
using System.Text;

namespace LumINE.Utils
{
    public static class NotificationContent
    {
        public static string Escape(string value)
        {
            var clean = new StringBuilder();
            value = value ?? "";
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (char.IsHighSurrogate(c)) { if (i + 1 < value.Length && char.IsLowSurrogate(value[i + 1])) { clean.Append(c); clean.Append(value[++i]); } }
                else if (!char.IsLowSurrogate(c) && (c == '\t' || c == '\n' || c == '\r' || c >= 32) && c != '\ufffe' && c != '\uffff') clean.Append(c);
            }
            return clean.ToString().Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&apos;");
        }
        public static string Toast(string name, string body, string icon, string arguments = null)
        {
            return "<toast" + (string.IsNullOrEmpty(arguments) ? "" : " launch='" + Escape(arguments) + "'") + "><visual><binding template='ToastGeneric'><text>" + Escape(name) + "</text><text>" + Escape(body) + "</text>" +
                (string.IsNullOrEmpty(icon) ? "" : "<image placement='appLogoOverride' hint-crop='circle' src='" + Escape(icon) + "'/>") + "</binding></visual></toast>";
        }
        public static string Tile(string name, string body, string icon, int count)
        {
            string text = "<text hint-style='caption' hint-wrap='true'>" + Escape(name) + "</text><text hint-wrap='true' hint-maxLines='3'>" + Escape(body) +
                "</text><text hint-style='captionSubtle'>未読 " + Math.Max(0, count) + "件</text>";
            string content = string.IsNullOrEmpty(icon) ? text : "<group><subgroup hint-weight='1'><image src='" + Escape(icon) +
                "' hint-crop='circle'/></subgroup><subgroup hint-weight='4'>" + text + "</subgroup></group>";
            return "<tile><visual branding='nameAndLogo'><binding template='TileMedium'>" + content + "</binding><binding template='TileWide'>" + content + "</binding></visual></tile>";
        }
        public static string Badge(int count) { return "<badge value='" + Math.Min(100, Math.Max(0, count)) + "'/>"; }
    }
}
