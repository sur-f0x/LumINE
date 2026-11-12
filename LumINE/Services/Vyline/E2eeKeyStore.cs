using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Data.Json;
using Windows.Security.Cryptography;
using Windows.Security.Cryptography.DataProtection;
using Windows.Storage.Streams;
using LumINE.Utils;

namespace LumINE.Services.Vyline
{
    public class E2eeSelfKey
    {
        public bool IsActive { get; set; }
        public int KeyId { get; set; }
        public byte[] PrivateKey { get; set; }
        public byte[] PublicKey { get; set; }
        public long CreatedTime { get; set; }
    }

    public class E2eePeerKey
    {
        public string Mid { get; set; }
        public int KeyId { get; set; }
        public byte[] PublicKey { get; set; }
        public int Version { get; set; }
        public long CreatedTime { get; set; }
    }

    public class E2eeGroupKey
    {
        public string Mid { get; set; }
        public int KeyId { get; set; }
        public byte[] PrivateKey { get; set; }
    }

    /// <summary>
    /// e2eeKeys persistence (vyline ensureE2EE / pubCacheKeys).
    /// Keys: e2eeKeys:{mid} self, e2eePublicKeys:peer:{mid}:{keyId}.
    /// Never auto-register new sender key unless allowRegisterNewKey.
    /// </summary>
    public static class E2eeKeyStore
    {
        private const string ProtectedSelfFilePrefix = "LUMINE-E2EE-DPAPI-V1\n";

        private static async Task<StorageFolder> AccountFolderAsync(string accountId)
        {
            var folder = await ApplicationData.Current.LocalFolder.CreateFolderAsync(
                "accounts", CreationCollisionOption.OpenIfExists);
            return await folder.CreateFolderAsync(accountId, CreationCollisionOption.OpenIfExists);
        }

        public static async Task<List<E2eeSelfKey>> LoadSelfKeysAsync(string accountId)
        {
            var sub = await AccountFolderAsync(accountId);
            StorageFile file;
            try { file = await sub.GetFileAsync("e2ee-self.json"); }
            catch (FileNotFoundException) { return null; }

            var text = await FileIO.ReadTextAsync(file);
            bool migrateLegacyFile = !text.StartsWith(ProtectedSelfFilePrefix, StringComparison.Ordinal);
            if (!migrateLegacyFile)
                text = await UnprotectAsync(text.Substring(ProtectedSelfFilePrefix.Length));

            var arr = JsonArray.Parse(text);
            var list = new List<E2eeSelfKey>();
            foreach (var t in arr)
            {
                var o = t.GetObject();
                var k = new E2eeSelfKey();
                k.KeyId = (int)GetNum(o, "keyId");
                k.CreatedTime = (long)GetNum(o, "createdTime");
                k.IsActive = o.ContainsKey("active") && o.GetNamedBoolean("active");
                k.PrivateKey = GetB64(o, "priv");
                k.PublicKey = GetB64(o, "pub");
                if (k.PrivateKey != null && k.PublicKey != null) list.Add(k);
            }
            // Existing callers use the last entry for sending. The login's
            // selected generation wins even when its ID is below a history key.
            list.Sort((a, b) => a.IsActive == b.IsActive ? a.KeyId.CompareTo(b.KeyId) : a.IsActive.CompareTo(b.IsActive));
            if (migrateLegacyFile && list.Count > 0)
                await SaveSelfKeysAsync(accountId, list).ConfigureAwait(false);
            return list;
        }

        public static async Task SaveSelfKeysAsync(string accountId, List<E2eeSelfKey> keys)
        {
            var sub = await AccountFolderAsync(accountId);
            var arr = new JsonArray();
            foreach (var k in keys)
            {
                var o = new JsonObject();
                o["active"] = JsonValue.CreateBooleanValue(k.IsActive);
                o["keyId"] = JsonValue.CreateNumberValue(k.KeyId);
                o["createdTime"] = JsonValue.CreateNumberValue(k.CreatedTime);
                o["priv"] = JsonValue.CreateStringValue(Convert.ToBase64String(k.PrivateKey));
                o["pub"] = JsonValue.CreateStringValue(Convert.ToBase64String(k.PublicKey));
                arr.Add(o);
            }
            var protectedText = ProtectedSelfFilePrefix +
                await ProtectAsync(arr.Stringify()).ConfigureAwait(false);
            var file = await sub.CreateFileAsync("e2ee-self.json", CreationCollisionOption.ReplaceExisting);
            await FileIO.WriteTextAsync(file, protectedText);
        }

        public static async Task<E2eePeerKey> LoadPeerKeyAsync(string accountId, string mid)
        {
            return await LoadPeerKeyAsync(accountId, mid, 0).ConfigureAwait(false);
        }

        public static async Task<E2eePeerKey> LoadPeerKeyAsync(string accountId, string mid, int keyId)
        {
            try
            {
                var sub = await AccountFolderAsync(accountId);
                string fileName = keyId > 0
                    ? "e2ee-peer-" + Sanitize(mid) + "-" + keyId + ".json"
                    : "e2ee-peer-" + Sanitize(mid) + ".json";
                StorageFile file;
                try { file = await sub.GetFileAsync(fileName); }
                catch (FileNotFoundException)
                {
                    if (keyId <= 0) return null;
                    file = await sub.GetFileAsync("e2ee-peer-" + Sanitize(mid) + ".json");
                }
                var text = await FileIO.ReadTextAsync(file);
                var o = JsonObject.Parse(text);
                var k = new E2eePeerKey();
                k.Mid = mid;
                k.KeyId = (int)GetNum(o, "keyId");
                k.Version = (int)GetNum(o, "version");
                k.CreatedTime = (long)GetNum(o, "createdTime");
                k.PublicKey = GetB64(o, "pub");
                if (keyId > 0 && k.KeyId != keyId) return null;
                return k.PublicKey != null ? k : null;
            }
            catch
            {
                return null;
            }
        }

        public static async Task SavePeerKeyAsync(string accountId, string mid, E2eePeerKey key)
        {
            try
            {
                var sub = await AccountFolderAsync(accountId);
                var o = new JsonObject();
                o["mid"] = JsonValue.CreateStringValue(mid);
                o["keyId"] = JsonValue.CreateNumberValue(key.KeyId);
                o["version"] = JsonValue.CreateNumberValue(key.Version);
                o["createdTime"] = JsonValue.CreateNumberValue(key.CreatedTime);
                o["pub"] = JsonValue.CreateStringValue(Convert.ToBase64String(key.PublicKey));
                string fileName = key.KeyId > 0
                    ? "e2ee-peer-" + Sanitize(mid) + "-" + key.KeyId + ".json"
                    : "e2ee-peer-" + Sanitize(mid) + ".json";
                var file = await sub.CreateFileAsync(fileName,
                    CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteTextAsync(file, o.Stringify());
                if (key.KeyId > 0)
                {
                    var latestFile = await sub.CreateFileAsync(
                        "e2ee-peer-" + Sanitize(mid) + ".json", CreationCollisionOption.ReplaceExisting);
                    await FileIO.WriteTextAsync(latestFile, o.Stringify());
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[E2eeStore] save peer failed: " + ex.Message);
            }
        }

        public static async Task<E2eeGroupKey> LoadGroupKeyAsync(string accountId, string mid, int keyId = 0)
        {
            try
            {
                var folder = await AccountFolderAsync(accountId);
                StorageFile file;
                try { file = await folder.GetFileAsync("e2ee-group-" + Sanitize(mid) + (keyId > 0 ? "-" + keyId : "") + ".json"); }
                catch (FileNotFoundException) { file = await folder.GetFileAsync("e2ee-group-" + Sanitize(mid) + ".json"); }
                var text = await FileIO.ReadTextAsync(file);
                var obj = JsonObject.Parse(text);
                var protectedValue = obj.GetNamedString("priv");
                var key = new E2eeGroupKey { Mid = mid, KeyId = (int)GetNum(obj, "keyId"),
                    PrivateKey = Convert.FromBase64String(await UnprotectAsync(protectedValue).ConfigureAwait(false)) };
                return key.PrivateKey.Length == 32 && (keyId <= 0 || key.KeyId == keyId) ? key : null;
            }
            catch { return null; }
        }

        public static async Task SaveGroupKeyAsync(string accountId, E2eeGroupKey key)
        {
            var folder = await AccountFolderAsync(accountId);
            var obj = new JsonObject();
            obj["keyId"] = JsonValue.CreateNumberValue(key.KeyId);
            obj["priv"] = JsonValue.CreateStringValue(await ProtectAsync(Convert.ToBase64String(key.PrivateKey)).ConfigureAwait(false));
            var file = await folder.CreateFileAsync("e2ee-group-" + Sanitize(key.Mid) + ".json", CreationCollisionOption.ReplaceExisting);
            await FileIO.WriteTextAsync(file, obj.Stringify());
            var versioned = await folder.CreateFileAsync("e2ee-group-" + Sanitize(key.Mid) + "-" + key.KeyId + ".json", CreationCollisionOption.ReplaceExisting);
            await FileIO.WriteTextAsync(versioned, obj.Stringify());
        }

        /// <summary>Extract peer public key from getE2EEPublicKey / negotiate result.</summary>
        public static E2eePeerKey ExtractPeerKey(Dictionary<int, object> result)
        {
            if (result == null) return null;
            // getE2EEPublicKey_result.success → PublicKey { version, keyId, keyData, createdTime }
            // Common: field 1 = success struct, or success IS the key struct
            Dictionary<int, object> keyStruct = result as Dictionary<int, object>;
            // If result has field 1 as struct, unwrap
            object v;
            if (result.TryGetValue(1, out v) && v is Dictionary<int, object> nested)
                keyStruct = nested;
            else if (result.TryGetValue(2, out v) && v is Dictionary<int, object> negotiated)
                keyStruct = negotiated;

            if (keyStruct == null) return null;

            var k = new E2eePeerKey();
            // Pb1_C13097n4: 1=version, 2=keyId, 4=keyData, 5=createdTime
            int ver;
            if (keyStruct.TryGetValue(1, out v) && v != null && int.TryParse(v.ToString(), out ver)) { }
            else ver = 1;
            k.Version = ver;
            int kid;
            if (keyStruct.TryGetValue(2, out v) && v != null && int.TryParse(v.ToString(), out kid)) { }
            else kid = 0;
            k.KeyId = kid;
            if (keyStruct.TryGetValue(4, out v) && v != null)
            {
                if (v is byte[] b) k.PublicKey = b;
                else
                {
                    try { k.PublicKey = Convert.FromBase64String(v.ToString()); }
                    catch { k.PublicKey = System.Text.Encoding.UTF8.GetBytes(v.ToString()); }
                }
            }
            long ct;
            if (keyStruct.TryGetValue(5, out v) && v != null && long.TryParse(v.ToString(), out ct)) { }
            else ct = 0;
            k.CreatedTime = ct;
            return k.PublicKey != null && k.PublicKey.Length > 0 ? k : null;
        }

        private static double GetNum(JsonObject o, string k)
        {
            try
            {
                if (!o.ContainsKey(k)) return 0;
                var t = o.GetNamedValue(k);
                if (t.ValueType == JsonValueType.Number) return t.GetNumber();
                if (t.ValueType == JsonValueType.String)
                {
                    double d; double.TryParse(t.GetString(), out d); return d;
                }
            }
            catch { }
            return 0;
        }

        private static byte[] GetB64(JsonObject o, string k)
        {
            try
            {
                if (!o.ContainsKey(k)) return null;
                var s = o.GetNamedString(k);
                return string.IsNullOrEmpty(s) ? null : Convert.FromBase64String(s);
            }
            catch { return null; }
        }

        private static string Sanitize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "unknown";
            var chars = s.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                char c = chars[i];
                if (!((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') ||
                      c == '.' || c == '_' || c == '-' || c == ':'))
                    chars[i] = '_';
            }
            return new string(chars);
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
