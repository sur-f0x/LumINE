using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Windows.Data.Json;
using Windows.Storage;
using Windows.Web.Http;
using LumINE.Utils;

namespace LumINE.Services.Vyline
{
    public sealed class StickerPack
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Version { get; set; }
        public long ExpiresAt { get; set; }
        public bool IsExpired { get { return ExpiresAt > 0 && ExpiresAt <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); } }
    }

    public static class StickerCatalog
    {
        private static readonly SemaphoreSlim RecentLock = new SemaphoreSlim(1, 1);
        public static async Task<List<Dictionary<string, string>>> LoadRecentAsync(string accountId)
        {
            var result = new List<Dictionary<string, string>>();
            try
            {
                var file = await (await FolderAsync(accountId)).GetFileAsync("sent-by-lumine.json");
                foreach (var item in JsonArray.Parse(await FileIO.ReadTextAsync(file)))
                {
                    var o = item.GetObject();
                    result.Add(new Dictionary<string, string> { { "STKID", o.GetNamedString("id") }, { "STKPKGID", o.GetNamedString("package") }, { "STKVER", o.GetNamedString("version") } });
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Stickers] recent cache: " + ex.Message); }
            return result;
        }

        public static async Task RecordSentAsync(string accountId, Dictionary<string, string> metadata)
        {
            await RecentLock.WaitAsync();
            try
            {
                var recent = await LoadRecentAsync(accountId);
                recent.RemoveAll(item => item["STKID"] == metadata["STKID"] && item["STKPKGID"] == metadata["STKPKGID"]);
                recent.Insert(0, metadata);
                var array = new JsonArray();
                foreach (var item in recent.GetRange(0, Math.Min(60, recent.Count)))
                {
                    var o = new JsonObject();
                    o["id"] = JsonValue.CreateStringValue(item["STKID"]); o["package"] = JsonValue.CreateStringValue(item["STKPKGID"]); o["version"] = JsonValue.CreateStringValue(item["STKVER"]);
                    array.Add(o);
                }
                await FileIO.WriteTextAsync(await (await FolderAsync(accountId)).CreateFileAsync("sent-by-lumine.json", CreationCollisionOption.ReplaceExisting), array.Stringify());
            }
            finally { RecentLock.Release(); }
        }
        private static async Task<StorageFolder> FolderAsync(string accountId)
        {
            var accounts = await ApplicationData.Current.LocalFolder.CreateFolderAsync("accounts", CreationCollisionOption.OpenIfExists);
            var account = await accounts.CreateFolderAsync(accountId, CreationCollisionOption.OpenIfExists);
            return await account.CreateFolderAsync("stickers", CreationCollisionOption.OpenIfExists);
        }

        private static string Field(Dictionary<int, object> fields, int id)
        {
            object value;
            return fields.TryGetValue(id, out value) && value != null ? value.ToString() : null;
        }

        public static async Task<List<StickerPack>> LoadCachedPacksAsync(string accountId)
        {
            var packs = new List<StickerPack>();
            try
            {
                var folder = await FolderAsync(accountId);
                var file = await folder.GetFileAsync("owned.json");
                foreach (var item in JsonArray.Parse(await FileIO.ReadTextAsync(file)))
                {
                    var value = item.GetObject();
                    var pack = new StickerPack { Id = value.GetNamedString("id"), Name = value.GetNamedString("name"), Version = value.GetNamedString("version"), ExpiresAt = (long)value.GetNamedNumber("expires") };
                    if (!pack.IsExpired) packs.Add(pack);
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Stickers] cache: " + ex.Message); }
            return packs;
        }

        public static async Task<List<StickerPack>> RefreshPacksAsync(LineSession session)
        {
            var packs = new List<StickerPack>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            int offset = 0;
            while (true)
            {
                var locale = new List<ThriftField> { new ThriftField(ThriftTypes.String, 1, "ja_JP"), new ThriftField(ThriftTypes.String, 2, "JP") };
                var args = new List<ThriftField>
                {
                    new ThriftField(ThriftTypes.String, 2, "stickershop"),
                    new ThriftField(ThriftTypes.I32, 3, offset), new ThriftField(ThriftTypes.I32, 4, 50),
                    new ThriftField(ThriftTypes.Struct, 5, locale)
                };
                var response = await session.Request.RequestCoreAsync("/TSHOP4", args, "getOwnedProducts", null, false, 15000, CancellationToken.None, true);
                var fields = response.Success as Dictionary<int, object>;
                object raw;
                if (fields == null || !fields.TryGetValue(1, out raw) || !(raw is List<object>))
                    throw new InvalidOperationException("購入済みスタンプ一覧の応答形式が不正です。");
                var page = (List<object>)raw;
                int added = 0;
                foreach (var item in page)
                {
                    var product = item as Dictionary<int, object>;
                    long id;
                    if (product == null || !long.TryParse(Field(product, 1), out id) || id <= 0 || !seen.Add(id.ToString())) continue;
                    added++;
                    long expiry = 0;
                    long.TryParse(Field(product, 42), out expiry);
                    if (expiry > 0 && expiry < 100000000000L) expiry *= 1000;
                    long version = 0;
                    long.TryParse(Field(product, 23), out version);
                    if (version <= 0) long.TryParse(Field(product, 21), out version);
                    var pack = new StickerPack { Id = id.ToString(), Name = Field(product, 11) ?? id.ToString(), Version = Math.Max(1, version).ToString(), ExpiresAt = expiry };
                    if (!pack.IsExpired) packs.Add(pack);
                }
                offset += page.Count;
                int total = 0;
                int.TryParse(Field(fields, 3), out total);
                if (page.Count == 0 || (total > 0 && offset >= total) || (total == 0 && page.Count < 50)) break;
                if (added == 0 || offset >= 10000) throw new InvalidOperationException("スタンプ一覧の続きが取得できませんでした。");
            }
            var array = new JsonArray();
            foreach (var pack in packs)
            {
                var json = new JsonObject();
                json["id"] = JsonValue.CreateStringValue(pack.Id); json["name"] = JsonValue.CreateStringValue(pack.Name);
                json["version"] = JsonValue.CreateStringValue(pack.Version); json["expires"] = JsonValue.CreateNumberValue(pack.ExpiresAt);
                array.Add(json);
            }
            var folder = await FolderAsync(session.AccountId);
            var file = await folder.CreateFileAsync("owned.json", CreationCollisionOption.ReplaceExisting);
            await FileIO.WriteTextAsync(file, array.Stringify());
            return packs;
        }

        public static async Task<List<string>> LoadStickerIdsAsync(string accountId, StickerPack pack)
        {
            long numericId;
            if (!long.TryParse(pack.Id, out numericId) || numericId <= 0 || pack.IsExpired) throw new InvalidOperationException("このスタンプは利用できません。");
            var folder = await FolderAsync(accountId);
            string name = numericId + "-" + pack.Version.Replace('/', '_').Replace('\\', '_') + ".json";
            string text = null;
            try { text = await FileIO.ReadTextAsync(await folder.GetFileAsync(name)); } catch { }
            if (text != null)
            {
                try { return ParseStickerIds(text); }
                catch { text = null; }
            }
            using (var client = new HttpClient())
            using (var response = await client.GetAsync(new Uri("https://stickershop.line-scdn.net/stickershop/v1/product/" + numericId + "/iphone/productInfo.meta")))
            {
                response.EnsureSuccessStatusCode();
                text = await response.Content.ReadAsStringAsync();
            }
            var ids = ParseStickerIds(text);
            var file = await folder.CreateFileAsync(name, CreationCollisionOption.ReplaceExisting);
            await FileIO.WriteTextAsync(file, text);
            return ids;
        }

        private static List<string> ParseStickerIds(string text)
        {
            var ids = new List<string>();
            var seen = new HashSet<string>();
            foreach (var item in JsonObject.Parse(text).GetNamedArray("stickers"))
            {
                var raw = item.GetObject().GetNamedValue("id");
                string value = raw.ValueType == JsonValueType.String ? raw.GetString() : ((long)raw.GetNumber()).ToString();
                long id;
                if (long.TryParse(value, out id) && id > 0 && seen.Add(value)) ids.Add(value);
            }
            if (ids.Count == 0) throw new InvalidOperationException("このパックのスタンプ情報を取得できませんでした。");
            return ids;
        }
    }
}
