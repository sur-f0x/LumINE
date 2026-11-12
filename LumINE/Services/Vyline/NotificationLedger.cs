using System;
using System.Collections.Generic;
using System.Linq;

namespace LumINE.Services.Vyline
{
    public sealed class NotificationLedger
    {
        public readonly Dictionary<string, int> Unread = new Dictionary<string, int>(StringComparer.Ordinal);
        public readonly List<string> Seen = new List<string>();
        private readonly Dictionary<string, int> _readVersions = new Dictionary<string, int>(StringComparer.Ordinal);
        public int ReadVersion(string chat) { int version; return _readVersions.TryGetValue(chat, out version) ? version : 0; }
        public int Total { get { return (int)Math.Min(int.MaxValue, Unread.Values.Sum(n => (long)Math.Max(0, n))); } }
        public bool Observe(string chat, string id, bool isOpen)
        {
            if (string.IsNullOrEmpty(chat) || string.IsNullOrEmpty(id) || Seen.Contains(id)) return false;
            Seen.Add(id);
            if (Seen.Count > 2000) Seen.RemoveAt(0);
            if (!isOpen) { int count; Unread.TryGetValue(chat, out count); Unread[chat] = Math.Min(9999, count + 1); }
            return true;
        }
        public void Read(string chat) { Unread.Remove(chat); _readVersions[chat] = ReadVersion(chat) + 1; }
        public void Clear() { foreach (var chat in Unread.Keys.ToList()) Read(chat); }
    }
}
