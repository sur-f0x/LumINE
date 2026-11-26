using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Windows.Data.Json;
using Windows.Storage;
using Windows.Web.Http;
using LumINE.Models;
using LumINE.Utils;

namespace LumINE.Services.Vyline
{
    public sealed class EmojiReactionPack
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public long? Version { get; set; }
        public int? ResourceType { get; set; }
        public long ExpiresAt { get; set; }
        public bool IsExpired { get { return ExpiresAt > 0 && ExpiresAt <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); } }
    }

    public static class EmojiReactionCatalog
    {
        private static async Task<StorageFolder> FolderAsync(string account)
        {
            var accounts = await ApplicationData.Current.LocalFolder.CreateFolderAsync("accounts", CreationCollisionOption.OpenIfExists);
            return await (await accounts.CreateFolderAsync(account, CreationCollisionOption.OpenIfExists)).CreateFolderAsync("reaction-emoji", CreationCollisionOption.OpenIfExists);
        }

        public static async Task<List<EmojiReactionPack>> LoadCachedAsync(string account)
        {
            var packs = new List<EmojiReactionPack>();
            try
            {
                foreach (var raw in JsonArray.Parse(await FileIO.ReadTextAsync(await (await FolderAsync(account)).GetFileAsync("owned.json"))))
                {
                    var item = raw.GetObject(); long version;
                    var pack = new EmojiReactionPack { Id = item.GetNamedString("id"), Name = item.GetNamedString("name"), Version = long.TryParse(item.GetNamedString("version", ""), out version) ? (long?)version : null, ExpiresAt = (long)item.GetNamedNumber("expires", 0) };
                    if (item.ContainsKey("resource")) pack.ResourceType = (int)item.GetNamedNumber("resource");
                    if (!pack.IsExpired) packs.Add(pack);
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[ReactionEmoji] cache: " + ex.Message); }
            return packs;
        }

        public static async Task<List<EmojiReactionPack>> RefreshOwnedAsync(LineSession session)
        {
            var packs = new List<EmojiReactionPack>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int offset = 0; offset < 10000; offset += 100)
            {
                var args = new List<ThriftField> {
                    new ThriftField(ThriftTypes.Struct, 1, new List<ThriftField>()),
                    new ThriftField(ThriftTypes.String, 2, "sticonshop"), new ThriftField(ThriftTypes.I32, 3, offset), new ThriftField(ThriftTypes.I32, 4, 100),
                    new ThriftField(ThriftTypes.Struct, 5, new List<ThriftField> { new ThriftField(ThriftTypes.String, 1, "ja"), new ThriftField(ThriftTypes.String, 2, "JP") })
                };
                var response = await session.Request.RequestCoreAsync("/TSHOP4", args, "getOwnedProductSummaries", null, false, 15000, CancellationToken.None);
                var fields = response.Success as Dictionary<int, object>; object value;
                if (fields == null || !fields.TryGetValue(1, out value) || !(value is List<object>)) throw new InvalidOperationException("所持絵文字の一覧を取得できませんでした。");
                var page = (List<object>)value; int added = 0;
                foreach (var raw in page)
                {
                    var item = raw as Dictionary<int, object>; long version, expiry; int resource;
                    string id = Text(item, 1);
                    if (string.IsNullOrEmpty(id) || !System.Text.RegularExpressions.Regex.IsMatch(id, "^[a-zA-Z0-9]{1,128}$") || !seen.Add(id)) continue;
                    added++;
                    var pack = new EmojiReactionPack { Id = id, Name = Text(item, 11) ?? id };
                    if (long.TryParse(Text(item, 21), out version) && version > 0) pack.Version = version;
                    if (long.TryParse(Text(item, 94), out expiry)) pack.ExpiresAt = ChatActivityProtocol.Timestamp(expiry);
                    var summary = Struct(Struct(item, 93), 3);
                    if (int.TryParse(Text(summary, 3), out resource)) pack.ResourceType = resource;
                    if (!pack.IsExpired) packs.Add(pack);
                }
                int total; int.TryParse(Text(fields, 3), out total);
                if (page.Count == 0 || (total > 0 && offset + page.Count >= total) || page.Count < 100) break;
                if (added == 0 || offset == 9900) throw new InvalidOperationException("所持絵文字一覧の続きが取得できませんでした。");
            }
            var cache = new JsonArray();
            foreach (var pack in packs)
            {
                var item = new JsonObject();
                item["id"] = JsonValue.CreateStringValue(pack.Id); item["name"] = JsonValue.CreateStringValue(pack.Name);
                item["version"] = JsonValue.CreateStringValue(pack.Version.HasValue ? pack.Version.Value.ToString(CultureInfo.InvariantCulture) : "");
                item["expires"] = JsonValue.CreateNumberValue(pack.ExpiresAt);
                if (pack.ResourceType.HasValue) item["resource"] = JsonValue.CreateNumberValue(pack.ResourceType.Value);
                cache.Add(item);
            }
            await FileIO.WriteTextAsync(await (await FolderAsync(session.AccountId)).CreateFileAsync("owned.json", CreationCollisionOption.ReplaceExisting), cache.Stringify());
            return packs;
        }

        public static async Task<List<MessageReaction>> LoadChoicesAsync(string account, EmojiReactionPack pack)
        {
            if (pack.IsExpired || string.IsNullOrEmpty(pack.Id) || !System.Text.RegularExpressions.Regex.IsMatch(pack.Id, "^[a-zA-Z0-9]{1,128}$")) throw new InvalidOperationException("この絵文字は利用できません。");
            var folder = await FolderAsync(account);
            string file = pack.Id + "-" + (pack.Version ?? 0).ToString(CultureInfo.InvariantCulture) + ".json", json = null;
            try { json = await FileIO.ReadTextAsync(await folder.GetFileAsync(file)); } catch { }
            if (json == null)
            {
                using (var client = new HttpClient())
                using (var response = await client.GetAsync(new Uri("https://stickershop.line-scdn.net/sticonshop/v1/sticon/" + pack.Id + "/ANDROID/meta.json")))
                { response.EnsureSuccessStatusCode(); json = await response.Content.ReadAsStringAsync(); }
                // Parse before saving so an error response cannot poison the cache.
                JsonObject.Parse(json).GetNamedArray("orders");
                await FileIO.WriteTextAsync(await folder.CreateFileAsync(file, CreationCollisionOption.ReplaceExisting), json);
            }
            var choices = new List<MessageReaction>(); var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var value in JsonObject.Parse(json).GetNamedArray("orders"))
            {
                string id = value.ValueType == JsonValueType.String ? value.GetString() : ((long)value.GetNumber()).ToString(CultureInfo.InvariantCulture);
                if (!System.Text.RegularExpressions.Regex.IsMatch(id, "^[a-zA-Z0-9]{1,128}$") || !seen.Add(id)) continue;
                choices.Add(new MessageReaction { ProductId = pack.Id, EmojiId = id, ResourceType = pack.ResourceType, Version = pack.Version });
            }
            return choices;
        }
        private static string Text(Dictionary<int, object> fields, int key) { object value; return fields != null && fields.TryGetValue(key, out value) && value != null ? value.ToString() : null; }
        private static Dictionary<int, object> Struct(Dictionary<int, object> fields, int key) { object value; return fields != null && fields.TryGetValue(key, out value) ? value as Dictionary<int, object> : null; }
    }
}
