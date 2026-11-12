using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Data.Json;
using Windows.Security.Cryptography;
using Windows.Security.Cryptography.DataProtection;
using Windows.Storage.Streams;

namespace LumINE.Services.Vyline
{
    public class StoredAccount
    {
        public string AccountId { get; set; }
        public string Mid { get; set; }
        public string AuthToken { get; set; }
        public string RefreshToken { get; set; }
        public long Expire { get; set; }
        public string DisplayName { get; set; }
        public DateTime? LoggedInAt { get; set; }
    }

    /// <summary>
    /// Token store (vyline tokenStore.ts) — user-protected data under LocalFolder/accounts.json.
    /// assertSafeAccountId: ^[A-Za-z0-9._:-]+$
    /// </summary>
    public static class TokenStore
    {
        private static readonly object _lock = new object();
        private static Dictionary<string, StoredAccount> _cache;
        private static readonly TaskCompletionSource<bool> _ready = new TaskCompletionSource<bool>();
        private const string ProtectedFilePrefix = "LUMINE-DPAPI-V1\n";

        public static Task Ready { get { return _ready.Task; } }

        public static bool AssertSafeAccountId(string accountId)
        {
            if (string.IsNullOrEmpty(accountId)) return false;
            foreach (var c in accountId)
            {
                bool ok = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') ||
                          c == '.' || c == '_' || c == ':' || c == '-';
                if (!ok) return false;
            }
            return true;
        }

        public static async Task EnsureLoadedAsync()
        {
            if (_cache != null) return;
            await LoadAsync().ConfigureAwait(false);
        }

        private static async Task LoadAsync()
        {
            lock (_lock)
            {
                if (_cache != null) { _ready.TrySetResult(true); return; }
            }
            var map = new Dictionary<string, StoredAccount>(StringComparer.Ordinal);
            string text = null;
            try
            {
                var file = await ApplicationData.Current.LocalFolder.GetFileAsync("accounts.json");
                text = await FileIO.ReadTextAsync(file);
            }
            catch (FileNotFoundException) { /* first launch */ }

            bool migrateLegacyFile = !string.IsNullOrEmpty(text) && !text.StartsWith(ProtectedFilePrefix, StringComparison.Ordinal);
            if (!string.IsNullOrEmpty(text))
            {
                if (!migrateLegacyFile)
                    text = await UnprotectAsync(text.Substring(ProtectedFilePrefix.Length));

                var arr = JsonArray.Parse(text);
                foreach (var token in arr)
                {
                    var o = token.GetObject();
                    var acc = new StoredAccount();
                    acc.AccountId = GetStr(o, "accountId");
                    acc.Mid = GetStr(o, "mid");
                    acc.AuthToken = GetStr(o, "authToken");
                    acc.RefreshToken = GetStr(o, "refreshToken");
                    acc.Expire = GetLong(o, "expire");
                    acc.DisplayName = GetStr(o, "displayName");
                    if (!string.IsNullOrEmpty(acc.AccountId) && AssertSafeAccountId(acc.AccountId))
                        map[acc.AccountId] = acc;
                }
            }
            lock (_lock)
            {
                _cache = map;
            }
            if (migrateLegacyFile) await PersistAsync().ConfigureAwait(false);
            _ready.TrySetResult(true);
        }

        private static string GetStr(JsonObject o, string k)
        {
            try { return o.ContainsKey(k) ? o.GetNamedString(k) : null; }
            catch { return null; }
        }

        private static long GetLong(JsonObject o, string k)
        {
            try
            {
                if (!o.ContainsKey(k)) return 0;
                var t = o.GetNamedValue(k);
                if (t.ValueType == JsonValueType.String)
                {
                    long v; long.TryParse(t.GetString(), out v); return v;
                }
                if (t.ValueType == JsonValueType.Number) return (long)t.GetNumber();
            }
            catch { }
            return 0;
        }

        public static async Task SaveAsync(StoredAccount account)
        {
            if (account == null || !AssertSafeAccountId(account.AccountId))
                throw new ArgumentException("invalid accountId");
            await EnsureLoadedAsync().ConfigureAwait(false);
            lock (_lock)
            {
                _cache[account.AccountId] = account;
            }
            await PersistAsync().ConfigureAwait(false);
        }

        public static async Task<StoredAccount> GetAsync(string accountId)
        {
            if (!AssertSafeAccountId(accountId)) return null;
            await EnsureLoadedAsync().ConfigureAwait(false);
            lock (_lock)
            {
                StoredAccount acc;
                if (_cache.TryGetValue(accountId, out acc)) return acc;
            }
            return null;
        }

        public static async Task<List<StoredAccount>> ListAsync()
        {
            await EnsureLoadedAsync().ConfigureAwait(false);
            lock (_lock)
            {
                return new List<StoredAccount>(_cache.Values);
            }
        }

        public static async Task DeleteAsync(string accountId)
        {
            if (!AssertSafeAccountId(accountId)) return;
            await EnsureLoadedAsync().ConfigureAwait(false);
            lock (_lock)
            {
                _cache.Remove(accountId);
            }
            await PersistAsync().ConfigureAwait(false);
        }

        public static async Task UpdateSessionMetaAsync(string accountId, Action<StoredAccount> mutate)
        {
            var acc = await GetAsync(accountId).ConfigureAwait(false);
            if (acc == null) return;
            mutate(acc);
            await SaveAsync(acc).ConfigureAwait(false);
        }

        private static async Task PersistAsync()
        {
            var arr = new JsonArray();
            List<StoredAccount> snapshot;
            lock (_lock)
            {
                snapshot = new List<StoredAccount>(_cache.Values);
            }
            foreach (var a in snapshot)
            {
                var o = new JsonObject();
                o["accountId"] = JsonValue.CreateStringValue(a.AccountId ?? "");
                o["mid"] = JsonValue.CreateStringValue(a.Mid ?? "");
                o["authToken"] = JsonValue.CreateStringValue(a.AuthToken ?? "");
                o["refreshToken"] = JsonValue.CreateStringValue(a.RefreshToken ?? "");
                o["expire"] = JsonValue.CreateStringValue(a.Expire.ToString());
                o["displayName"] = JsonValue.CreateStringValue(a.DisplayName ?? "");
                o["loggedInAt"] = JsonValue.CreateStringValue(
                    a.LoggedInAt.HasValue ? a.LoggedInAt.Value.ToString("o") : "");
                arr.Add(o);
            }
            string protectedText = ProtectedFilePrefix + await ProtectAsync(arr.Stringify()).ConfigureAwait(false);
            var file = await ApplicationData.Current.LocalFolder.CreateFileAsync(
                "accounts.json", CreationCollisionOption.ReplaceExisting);
            await FileIO.WriteTextAsync(file, protectedText);
        }

        private static async Task<string> ProtectAsync(string text)
        {
            var provider = new DataProtectionProvider("LOCAL=user");
            IBuffer clear = CryptographicBuffer.ConvertStringToBinary(text, BinaryStringEncoding.Utf8);
            IBuffer protectedBuffer = await provider.ProtectAsync(clear);
            return CryptographicBuffer.EncodeToBase64String(protectedBuffer);
        }

        private static async Task<string> UnprotectAsync(string encoded)
        {
            var provider = new DataProtectionProvider();
            IBuffer protectedBuffer = CryptographicBuffer.DecodeFromBase64String(encoded);
            IBuffer clear = await provider.UnprotectAsync(protectedBuffer);
            return CryptographicBuffer.ConvertBinaryToString(BinaryStringEncoding.Utf8, clear);
        }
    }
}
