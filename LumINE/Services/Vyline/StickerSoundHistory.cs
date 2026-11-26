using System;
using System.Collections.Generic;

namespace LumINE.Services.Vyline
{
    // Persist message identities, not sticker IDs: two messages containing the
    // same sticker may each play once; recycled rows must not play it again.
    public sealed class StickerSoundHistory
    {
        public readonly HashSet<string> Seen = new HashSet<string>(StringComparer.Ordinal);
        public bool Observe(string chat, string message)
        {
            if (string.IsNullOrEmpty(chat) || string.IsNullOrEmpty(message)) return false;
            return Seen.Add(chat + ":" + message);
        }
        public static string SoundUrl(string stickerId)
        {
            long id;
            if (!long.TryParse(stickerId, out id) || id <= 0) return null;
            return "https://stickershop.line-scdn.net/stickershop/v1/sticker/" + id.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/android/sticker_sound.m4a";
        }
        public static bool IsM4a(byte[] bytes)
        {
            return bytes != null && bytes.Length >= 12 && bytes[4] == 'f' && bytes[5] == 't' && bytes[6] == 'y' && bytes[7] == 'p';
        }
    }
}
