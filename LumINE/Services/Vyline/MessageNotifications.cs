using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Windows.Data.Json;
using Windows.Data.Xml.Dom;
using Windows.Storage;
using Windows.UI.Notifications;
using LumINE.Models;
using LumINE.Utils;

namespace LumINE.Services.Vyline
{
    public static partial class BackgroundNotifications
    {
        private sealed class Inbox
        {
            public readonly NotificationLedger Ledger = new NotificationLedger();
            public readonly Dictionary<string, string[]> Contacts = new Dictionary<string, string[]>(StringComparer.Ordinal);
        }
        private static readonly SemaphoreSlim InboxGate = new SemaphoreSlim(1, 1);
        private static readonly Dictionary<string, Inbox> Inboxes = new Dictionary<string, Inbox>(StringComparer.Ordinal);

        private static async Task<Inbox> LoadInboxAsync(string accountId)
        {
            Inbox inbox;
            if (Inboxes.TryGetValue(accountId, out inbox)) return inbox;
            inbox = new Inbox();
            try
            {
                var file = await ApplicationData.Current.LocalFolder.GetFileAsync("accounts/" + accountId + "/notifications.json");
                var json = JsonObject.Parse(await FileIO.ReadTextAsync(file));
                foreach (var pair in json.GetNamedObject("unread")) inbox.Ledger.Unread[pair.Key] = (int)pair.Value.GetNumber();
                foreach (var id in json.GetNamedArray("seen")) inbox.Ledger.Seen.Add(id.GetString());
                foreach (var pair in json.GetNamedObject("contacts"))
                {
                    var contact = pair.Value.GetObject();
                    inbox.Contacts[pair.Key] = new[] { contact.GetNamedString("name", ""), contact.GetNamedString("picture", "") };
                }
            }
            catch (System.IO.FileNotFoundException)
            {
                try { foreach (var id in JsonArray.Parse(ApplicationData.Current.LocalSettings.Values[Key(accountId) + ".seen"] as string ?? "[]")) inbox.Ledger.Seen.Add(id.GetString()); } catch { }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Notifications] inbox load: " + ex.Message); }
            Inboxes[accountId] = inbox;
            return inbox;
        }
        private static async Task SaveInboxAsync(string accountId, Inbox inbox)
        {
            try
            {
                var unread = new JsonObject();
                foreach (var pair in inbox.Ledger.Unread) unread[pair.Key] = JsonValue.CreateNumberValue(pair.Value);
                var seen = new JsonArray();
                foreach (var id in inbox.Ledger.Seen) seen.Add(JsonValue.CreateStringValue(id));
                var contacts = new JsonObject();
                foreach (var pair in inbox.Contacts)
                {
                    var contact = new JsonObject(); contact["name"] = JsonValue.CreateStringValue(pair.Value[0]); contact["picture"] = JsonValue.CreateStringValue(pair.Value[1]); contacts[pair.Key] = contact;
                }
                var json = new JsonObject(); json["unread"] = unread; json["seen"] = seen; json["contacts"] = contacts;
                var accounts = await ApplicationData.Current.LocalFolder.CreateFolderAsync("accounts", CreationCollisionOption.OpenIfExists);
                var account = await accounts.CreateFolderAsync(accountId, CreationCollisionOption.OpenIfExists);
                await FileIO.WriteTextAsync(await account.CreateFileAsync("notifications.json", CreationCollisionOption.ReplaceExisting), json.Stringify());
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Notifications] inbox save: " + ex.Message); }
        }
        private static XmlDocument NotificationXml(string xml) { var document = new XmlDocument(); document.LoadXml(xml); return document; }
        private static string NotificationGroup(string accountId, string chat)
        {
            ulong hash = 14695981039346656037UL;
            foreach (char c in accountId + "|" + chat) { hash ^= c; hash = unchecked(hash * 1099511628211UL); }
            return hash.ToString("x16");
        }
        // Shared by arrivals, reads and settings changes under InboxGate.
        private static void PublishShell(string accountId, Inbox inbox)
        {
            var values = ApplicationData.Current.LocalSettings.Values;
            values[Key(accountId) + ".pending"] = IsEnabled(accountId) ? inbox.Ledger.Total : 0;
            long total = 0;
            foreach (var item in values)
                if (item.Key.StartsWith("notifications.", StringComparison.Ordinal) && item.Key.EndsWith(".pending", StringComparison.Ordinal) && item.Value is int) total += Math.Max(0, (int)item.Value);
            int count = (int)Math.Min(int.MaxValue, total);
            try
            {
                var badge = BadgeUpdateManager.CreateBadgeUpdaterForApplication();
                if (count == 0) badge.Clear(); else badge.Update(new BadgeNotification(NotificationXml(NotificationContent.Badge(count))));
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Notifications] badge: " + ex.Message); }
            try
            {
                var tile = TileUpdateManager.CreateTileUpdaterForApplication(); tile.EnableNotificationQueue(false);
                if (count == 0) { tile.Clear(); values.Remove("notifications.tile"); return; }
                var latest = values["notifications.tile"] as ApplicationDataCompositeValue;
                string name = latest == null ? "LumINE" : latest["name"] as string;
                string body = AppSettings.HideMessageContent ? "新規メッセージ" : latest == null ? "新着メッセージ " + count + "件" : latest["body"] as string;
                string icon = latest == null ? null : latest["icon"] as string;
                tile.Update(new TileNotification(NotificationXml(NotificationContent.Tile(name, body, icon, count))));
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Notifications] tile: " + ex.Message); }
        }
        public static async Task RestoreNotificationStateAsync(string accountId)
        {
            await InboxGate.WaitAsync();
            try { PublishShell(accountId, await LoadInboxAsync(accountId)); }
            finally { InboxGate.Release(); }
        }
        public static async Task ApplyContentPrivacyAsync()
        {
            await InboxGate.WaitAsync();
            try
            {
                var values = ApplicationData.Current.LocalSettings.Values;
                if (AppSettings.HideMessageContent)
                {
                    var latest = values["notifications.tile"] as ApplicationDataCompositeValue;
                    if (latest != null) { latest["body"] = "新規メッセージ"; values["notifications.tile"] = latest; }
                    ToastNotificationManager.History.Clear();
                }
                var session = SessionManager.Current;
                if (session != null) PublishShell(session.AccountId, await LoadInboxAsync(session.AccountId));
            }
            finally { InboxGate.Release(); }
        }
        public static async Task ClearNotificationStateAsync(string accountId)
        {
            await InboxGate.WaitAsync();
            try
            {
                var inbox = await LoadInboxAsync(accountId);
                foreach (var chat in inbox.Ledger.Unread.Keys)
                    try { ToastNotificationManager.History.RemoveGroup(NotificationGroup(accountId, chat)); } catch { }
                inbox.Ledger.Clear();
                var values = ApplicationData.Current.LocalSettings.Values;
                var latest = values["notifications.tile"] as ApplicationDataCompositeValue;
                if (latest != null && latest["account"] as string == accountId) values.Remove("notifications.tile");
                await SaveInboxAsync(accountId, inbox); PublishShell(accountId, inbox);
            }
            finally { InboxGate.Release(); }
        }
        public static async Task MarkNotificationReadAsync(string accountId, string chat)
        {
            await InboxGate.WaitAsync();
            try
            {
                var inbox = await LoadInboxAsync(accountId); inbox.Ledger.Read(chat);
                var values = ApplicationData.Current.LocalSettings.Values;
                var latest = values["notifications.tile"] as ApplicationDataCompositeValue;
                if (latest != null && latest["account"] as string == accountId && latest["chat"] as string == chat) values.Remove("notifications.tile");
                try { ToastNotificationManager.History.RemoveGroup(NotificationGroup(accountId, chat)); } catch { }
                await SaveInboxAsync(accountId, inbox); PublishShell(accountId, inbox);
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Notifications] read state: " + ex.Message); }
            finally { InboxGate.Release(); }
        }
        private static async Task WaitForContentAsync(Task task, int milliseconds, CancellationToken cancellation)
        {
            if (await Task.WhenAny(task, Task.Delay(milliseconds, cancellation)) == task) { await task; return; }
            var observe = task.ContinueWith(t => { var exception = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
            cancellation.ThrowIfCancellationRequested();
            throw new TimeoutException("Notification content lookup timed out.");
        }
        public static async Task NotifyAsync(LineSession session, Message message, CancellationToken cancellation = default(CancellationToken))
        {
            if (session == null || session.Disabled || !IsEnabled(session.AccountId) || message == null || string.IsNullOrEmpty(message.Id) || message.From == session.Mid || message.From == "me" || message.IsRevoked) return;
            string accountId = session.AccountId;
            string chat = message.To == session.Mid ? message.From : message.To;
            if (string.IsNullOrEmpty(chat)) return;
            long timestamp = ChatActivityProtocol.Timestamp(message.CreatedTime ?? 0);
            var values = ApplicationData.Current.LocalSettings.Values;
            long since = values[Key(accountId) + ".since"] is long ? (long)values[Key(accountId) + ".since"] : long.MaxValue;
            if (timestamp > 0 && timestamp < since) return;
            string[] contact = null;
            int readVersion = 0;
            await InboxGate.WaitAsync(cancellation);
            try
            {
                var inbox = await LoadInboxAsync(accountId);
                bool open = IsForeground && object.ReferenceEquals(session, SessionManager.Current) && session.Sync.ActiveChatId == chat;
                if (!inbox.Ledger.Observe(chat, message.Id, open)) return;
                readVersion = inbox.Ledger.ReadVersion(chat);
                await SaveInboxAsync(accountId, inbox); PublishShell(accountId, inbox);
                inbox.Contacts.TryGetValue(message.From ?? "", out contact);
                if (open) return;
            }
            finally { InboxGate.Release(); }
            DeliveryStatus = "最終受信: " + DateTime.Now.ToString("HH:mm:ss");
            if (!AppSettings.HideMessageContent && message.ContentType == 0 && string.IsNullOrEmpty(message.Text) && message.Chunks != null && message.Chunks.Count > 0)
                try { await WaitForContentAsync(session.Line.DecryptMessagesAsync(new List<Message> { message }), 5000, cancellation); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Notifications] decrypt: " + ex.Message); }
            string name = contact == null ? message.SenderName : contact[0];
            string picture = contact == null ? null : contact[1];
            bool resolvedContact = contact != null;
            if (string.IsNullOrWhiteSpace(name))
            {
                foreach (var cached in session.Store.GetCachedChats())
                    if (cached.ChatMid == message.From) { name = cached.ChatName; picture = cached.PicturePath; resolvedContact = !string.IsNullOrWhiteSpace(name); break; }
                try
                {
                    var task = session.Talk.GetContactAsync(message.From); await WaitForContentAsync(task, 3000, cancellation);
                    var fields = await task; object value;
                    string alias = fields.TryGetValue(27, out value) && value != null ? value.ToString() : null;
                    string display = fields.TryGetValue(22, out value) && value != null ? value.ToString() : null;
                    name = !string.IsNullOrWhiteSpace(alias) ? alias : !string.IsNullOrWhiteSpace(display) ? display : name;
                    resolvedContact = resolvedContact || !string.IsNullOrWhiteSpace(alias) || !string.IsNullOrWhiteSpace(display);
                    if (fields.TryGetValue(37, out value) && value != null) picture = value.ToString();
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Notifications] sender lookup: " + ex.Message); }
            }
            name = string.IsNullOrWhiteSpace(name) ? "LINE" : name;
            string body = AppSettings.HideMessageContent ? "新規メッセージ" : message.ContentType == 0 && string.IsNullOrEmpty(message.Text) ? "暗号化されたメッセージ（アプリで確認してください）" : message.ContentType == 0 ? message.Text : message.MediaFallbackText;
            if (name.Length > 120) name = name.Substring(0, 120);
            if (body.Length > 500) body = body.Substring(0, 500);
            string icon = null;
            if (!cancellation.IsCancellationRequested && !string.IsNullOrEmpty(picture))
                try { icon = await AvatarCache.GetNotificationUriAsync(accountId, picture); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Notifications] avatar: " + ex.Message); }
            // Once counted, finish publishing this message even if the background
            // budget expires during enrichment; the next poll must not lose it.
            await InboxGate.WaitAsync();
            try
            {
                var inbox = await LoadInboxAsync(accountId);
                if (!IsEnabled(accountId) || session.Disabled || !inbox.Ledger.Unread.ContainsKey(chat) || inbox.Ledger.ReadVersion(chat) != readVersion) return;
                if (AppSettings.HideMessageContent) body = "新規メッセージ";
                if (resolvedContact && !string.IsNullOrEmpty(message.From)) inbox.Contacts[message.From] = new[] { name, picture ?? "" };
                var previous = values["notifications.tile"] as ApplicationDataCompositeValue;
                if (previous == null || ChatActivityProtocol.Compare(timestamp, message.Id, (long)previous["timestamp"], previous.ContainsKey("id") ? previous["id"] as string : "") > 0)
                {
                    var preview = new ApplicationDataCompositeValue(); preview["account"] = accountId; preview["chat"] = chat; preview["id"] = message.Id; preview["name"] = name; preview["body"] = body; preview["icon"] = icon ?? ""; preview["timestamp"] = timestamp;
                    values["notifications.tile"] = preview;
                }
                await SaveInboxAsync(accountId, inbox); PublishShell(accountId, inbox);
                if (!IsForeground)
                {
                    try
                    {
                        var notifier = ToastNotificationManager.CreateToastNotifier();
                        if (notifier.Setting != NotificationSetting.Enabled) { DeliveryStatus += " / Windows通知: " + notifier.Setting; return; }
                        var toast = new ToastNotification(NotificationXml(NotificationContent.Toast(name, AppSettings.HideMessageContent ? "新規メッセージ" : body, icon, NotificationRoute.Encode(accountId, chat)))) { ExpirationTime = DateTimeOffset.Now.AddHours(1), Group = NotificationGroup(accountId, chat) };
                        toast.Tag = message.Id.Length > 16 ? message.Id.Substring(message.Id.Length - 16) : message.Id; notifier.Show(toast);
                        DeliveryStatus += " / 名前・本文付き通知をWindowsへ渡しました。";
                    }
                    catch (Exception ex) { DeliveryStatus += " / 通知エラー: " + ex.Message; System.Diagnostics.Debug.WriteLine("[Notifications] toast failed: " + ex.Message); }
                }
            }
            finally { InboxGate.Release(); }
        }
    }
}
