using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Data.Json;
using LumINE.Models;

namespace LumINE.Services.Vyline
{
    public partial class ChatStore
    {
        private readonly Dictionary<string, Message> _activity = new Dictionary<string, Message>(StringComparer.Ordinal);
        private readonly SemaphoreSlim _activityGate = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _activityLoadGate = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _activitySaveGate = new SemaphoreSlim(1, 1);
        private bool _activityLoaded;
        private DateTime _activityRefreshedAt = DateTime.MinValue;

        private void UpdateActivityLocked(string chatMid, Message message)
        {
            if (message == null || string.IsNullOrEmpty(message.Id)) return;
            Message previous;
            if (!_activity.TryGetValue(chatMid, out previous) || previous.Id == message.Id ||
                ChatActivityProtocol.Compare(message.CreatedTime ?? 0, message.Id, previous.CreatedTime ?? 0, previous.Id) > 0) _activity[chatMid] = message;
        }

        public Message GetLastMessage(string chatMid)
        {
            lock (_lock)
            {
                List<Message> messages;
                if (_messages.TryGetValue(chatMid, out messages) && messages.Count > 0) UpdateActivityLocked(chatMid, messages[messages.Count - 1]);
                Message message;
                return _activity.TryGetValue(chatMid, out message) ? message : null;
            }
        }

        public async Task LoadChatActivityAsync()
        {
            await _activityLoadGate.WaitAsync();
            try
            {
                if (_activityLoaded) return;
                try
                {
                    var file = await ApplicationData.Current.LocalFolder.GetFileAsync("accounts/" + AccountId + "/chat-activity.json");
                    foreach (var item in JsonArray.Parse(await FileIO.ReadTextAsync(file)))
                    {
                        var obj = item.GetObject();
                        string mid = obj.GetNamedString("chat");
                        var message = DeserializeMessage(obj.GetNamedObject("message"));
                        lock (_lock)
                        {
                            Message current;
                            if (message != null && (!_activity.TryGetValue(mid, out current) ||
                                ChatActivityProtocol.Compare(message.CreatedTime ?? 0, message.Id, current.CreatedTime ?? 0, current.Id) > 0))
                                UpdateActivityLocked(mid, message);
                        }
                    }
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Activity] cache load: " + ex.Message); }
                _activityLoaded = true;
            }
            finally { _activityLoadGate.Release(); }
        }

        public async Task SaveChatActivityAsync()
        {
            await LoadChatActivityAsync();
            await _activitySaveGate.WaitAsync();
            try
            {
                var array = new JsonArray();
                lock (_lock) foreach (var pair in _activity)
                {
                    var item = new JsonObject();
                    item["chat"] = JsonValue.CreateStringValue(pair.Key);
                    item["message"] = SerializeMessage(pair.Value);
                    array.Add(item);
                }
                var accounts = await ApplicationData.Current.LocalFolder.CreateFolderAsync("accounts", CreationCollisionOption.OpenIfExists);
                var account = await accounts.CreateFolderAsync(AccountId, CreationCollisionOption.OpenIfExists);
                await FileIO.WriteTextAsync(await account.CreateFileAsync("chat-activity.json", CreationCollisionOption.ReplaceExisting), array.Stringify());
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Activity] cache save: " + ex.Message); }
            finally { _activitySaveGate.Release(); }
        }

        public async Task RefreshChatActivityAsync()
        {
            await LoadChatActivityAsync();
            await _activityGate.WaitAsync();
            try
            {
                if (DateTime.UtcNow - _activityRefreshedAt < TimeSpan.FromSeconds(20)) return;
                bool succeeded = false;
                foreach (string prefix in new[] { "u", "c", "r" })
                {
                    string cursor = prefix;
                    for (int page = 0; page < 20; page++)
                    {
                        Dictionary<int, object> response;
                        try { response = await _session.Talk.GetMessageBoxesAsync(cursor, prefix + new string('f', 32), false, 100, true, 1); }
                        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Activity] boxes fetch: " + ex.Message); break; }
                        object raw;
                        if (!response.TryGetValue(1, out raw) || !(raw is List<object>)) break;
                        succeeded = true;
                        var boxes = (List<object>)raw;
                        var latest = new Dictionary<string, Message>(StringComparer.Ordinal);
                        string last = null;
                        foreach (var item in boxes)
                        {
                            var box = item as Dictionary<int, object>;
                            if (box == null) continue;
                            string mid = GetString(box, 1);
                            if (string.IsNullOrEmpty(mid) || !mid.StartsWith(prefix, StringComparison.Ordinal)) continue;
                            last = mid;
                            var fields = ChatActivityProtocol.LatestMessage(box);
                            if (fields != null) latest[mid] = MapMessage(fields, mid);
                        }
                        await _session.Line.DecryptMessagesAsync(latest.Values.ToList());
                        foreach (var pair in latest) MergeMessages(pair.Key, new List<Message> { pair.Value });
                        object more;
                        if (!response.TryGetValue(2, out more) || !(more is bool) || !(bool)more || boxes.Count == 0 || string.IsNullOrEmpty(last) || last == cursor) break;
                        cursor = last;
                    }
                }
                if (succeeded) { _activityRefreshedAt = DateTime.UtcNow; await SaveChatActivityAsync(); }
            }
            finally { _activityGate.Release(); }
        }

        public async Task ApplyReactionAsync(string chatMid, string messageId, string actor, int type, long atMillis)
        {
            await ApplyCustomReactionAsync(chatMid, messageId, actor, type == 0 ? null : new MessageReaction { Type = type }, atMillis);
        }

        public async Task ApplyCustomReactionAsync(string chatMid, string messageId, string actor, MessageReaction reaction, long atMillis)
        {
            if (string.IsNullOrEmpty(actor) || string.IsNullOrEmpty(messageId)) return;
            await LoadMessagesFromDiskAsync(chatMid);
            Message target;
            lock (_lock)
            {
                List<Message> list;
                target = _messages.TryGetValue(chatMid, out list) ? list.Find(m => m.Id == messageId) : null;
                if (target == null || target.IsRevoked) return;
                var previous = target.Reactions.Find(r => r.FromMid == actor);
                if (previous != null && previous.AtMillis > atMillis) return;
                var reactions = target.Reactions.Where(r => r.FromMid != actor).ToList();
                if (reaction != null && (reaction.IsCustom || (reaction.Type >= 2 && reaction.Type <= 7)))
                    reactions.Add(new MessageReaction { FromMid = actor, Type = reaction.Type, AtMillis = atMillis, ProductId = reaction.ProductId, EmojiId = reaction.EmojiId, ResourceType = reaction.ResourceType, Version = reaction.Version });
                target.ReactionsKnown = true;
                target.Reactions = reactions;
            }
            await SaveMessagesToDiskAsync(chatMid);
        }
    }
}
