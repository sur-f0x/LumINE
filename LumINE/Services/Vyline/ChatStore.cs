using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using LumINE.Models;
using LumINE.Utils;

namespace LumINE.Services.Vyline
{
    /// <summary>
    /// Chat roster / message helpers over TalkService + local JSON store (ApplicationData).
    /// Local-first: read disk, refresh remote in background when stale (CHATS_CACHE_MS).
    /// </summary>
    public partial class ChatStore
    {
        public const int ChatsCacheMs = 120000; // vyline CHATS_CACHE_MS default 2min scale
        private const int HistoryPageSize = 100;
        private const int HistoryMaxMessages = 5000;
        private static readonly TimeSpan HistoryRefreshTtl = TimeSpan.FromMinutes(5);

        private readonly LineSession _session;
        private readonly object _lock = new object();
        private List<Chat> _chats = new List<Chat>();
        private DateTime _chatsSyncedAt = DateTime.MinValue;
        private readonly Dictionary<string, List<Message>> _messages =
            new Dictionary<string, List<Message>>(StringComparer.Ordinal);
        private readonly HashSet<string> _fullCacheLoaded = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, DateTime> _historySyncedAt =
            new Dictionary<string, DateTime>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _unreadCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        private bool _unreadLoaded;

        public ChatStore(LineSession session)
        {
            _session = session;
        }

        public string AccountId { get { return _session.AccountId; } }

        // ── Chats ─────────────────────────────────────────────

        public async Task<List<Chat>> GetChatsAsync(bool force = false, bool refresh = false)
        {
            await LoadUnreadCountsAsync().ConfigureAwait(false);
            await LoadChatsFromDiskAsync().ConfigureAwait(false);
            List<Chat> local;
            lock (_lock) local = _chats.ToList();

            bool stale = refresh ||
                (DateTime.UtcNow - _chatsSyncedAt).TotalMilliseconds > ChatsCacheMs;

            if (!force && local.Count > 0 && !stale)
                return local;

            try
            {
                if (refresh && local.Count > 0)
                {
                    // race 3s then fall back to local (vyline refresh=true)
                    var fetchTask = FetchChatsInnerAsync();
                    var done = await Task.WhenAny(fetchTask, Task.Delay(3000)).ConfigureAwait(false);
                    if (done == fetchTask)
                    {
                        var fresh = await fetchTask.ConfigureAwait(false);
                        if (fresh.Count > 0)
                        {
                            lock (_lock)
                            {
                                _chats = fresh;
                                _chatsSyncedAt = DateTime.UtcNow;
                            }
                            await SaveChatsToDiskAsync(fresh).ConfigureAwait(false);
                            return fresh;
                        }
                    }
                    return local;
                }

                var chats = await FetchChatsInnerAsync().ConfigureAwait(false);
                if (chats.Count > 0 || local.Count == 0)
                {
                    lock (_lock)
                    {
                        _chats = chats;
                        _chatsSyncedAt = DateTime.UtcNow;
                    }
                    await SaveChatsToDiskAsync(chats).ConfigureAwait(false);
                    return chats;
                }
                return local;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[ChatStore] fetch failed: " + ex.Message);
                if (local.Count > 0) return local;
                throw;
            }
        }

        private async Task<List<Chat>> FetchChatsInnerAsync()
        {
            // LINE's message boxes are not the canonical chat roster. The
            // roster is group/room MIDs plus contacts (direct chats), followed
            // by getChats in bounded batches.
            var result = await _session.Talk.GetChatsAsync(false, 200).ConfigureAwait(false);

            var groupMids = new List<string>();
            foreach (var chat in result)
                if (chat != null && !string.IsNullOrEmpty(chat.ChatMid) && chat.ChatMid.StartsWith("c", StringComparison.Ordinal))
                    groupMids.Add(chat.ChatMid);
            if (groupMids.Count > 0)
            {
                try
                {
                    var groups = await _session.Talk.GetGroupsAsync(groupMids).ConfigureAwait(false);
                    foreach (var group in groups)
                    {
                        string mid = GetString(group, 1);
                        if (string.IsNullOrEmpty(mid)) continue;
                        object rawMembers;
                        int memberCount = group.TryGetValue(20, out rawMembers) && rawMembers is List<object> members
                            ? members.Count : 0;
                        if (memberCount == 0 && group.TryGetValue(40, out rawMembers) && rawMembers is List<object> memberMids)
                            memberCount = memberMids.Count;
                        string groupName = GetString(group, 10);
                        var chat = result.Find(item => item.ChatMid == mid);
                        if (chat == null) continue;
                        if (memberCount == 0) memberCount = chat.MemberCount ?? 0;
                        chat.MemberCount = memberCount;
                        if (!string.IsNullOrEmpty(groupName)) chat.ChatName = groupName;
                        if (!string.IsNullOrEmpty(chat.ChatName) && memberCount > 0)
                            chat.ChatName = chat.ChatName + " (" + memberCount + ")";
                    }
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[ChatStore] getGroups failed: " + ex.Message); }
            }

            // Direct chats are peers, not server-side rooms. Build the Friends
            // tab from contacts independently of the group/room getChats RPC.
            var friendMids = await _session.Talk.GetAllContactIdsAsync().ConfigureAwait(false);
            if (friendMids.Count > 0)
            {
                try
                {
                    var contacts = await _session.Talk.GetContactsAsync(friendMids).ConfigureAwait(false);
                    var contactMids = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var cobj in contacts)
                    {
                        var cd = cobj as Dictionary<int, object>;
                        if (cd == null) continue;
                        string mid = GetString(cd, 1);
                        string name = GetString(cd, 27) ?? GetString(cd, 22);
                        string picturePath = GetString(cd, 37);
                        if (string.IsNullOrEmpty(mid) || !contactMids.Add(mid)) continue;
                        result.Add(new Chat
                        {
                            ChatMid = mid,
                            ChatType = 2, // ChatType.PEER
                            ChatName = string.IsNullOrEmpty(name) ? mid : name,
                            PicturePath = picturePath
                        });
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("[ChatStore] getContacts failed: " + ex.Message);
                }
            }

            return result;
        }

        private static string GetString(Dictionary<int, object> fields, int id)
        {
            object value;
            return fields.TryGetValue(id, out value) && value != null ? value.ToString() : null;
        }

        private static List<object> FindList(Dictionary<int, object> d)
        {
            if (d == null) return null;
            foreach (var key in new[] { 1, 2, 0, 3, 4 })
            {
                object v;
                if (d.TryGetValue(key, out v))
                {
                    var list = v as List<object>;
                    if (list != null && list.Count > 0) return list;
                }
            }
            // deep scan one level
            foreach (var kv in d)
            {
                var nested = kv.Value as Dictionary<int, object>;
                if (nested == null) continue;
                foreach (var nk in nested.Values)
                {
                    var list = nk as List<object>;
                    if (list != null && list.Count > 0) return list;
                }
            }
            return null;
        }

        // ── Messages ──────────────────────────────────────────

        public async Task<List<Message>> GetMessagesAsync(string chatMid, int count = 50)
        {
            await LoadMessagesFromDiskAsync(chatMid).ConfigureAwait(false);
            List<Message> local;
            lock (_lock)
            {
                List<Message> list;
                if (!_messages.TryGetValue(chatMid, out list)) list = new List<Message>();
                local = list.ToList();
            }
            await _session.Line.DecryptMessagesAsync(local).ConfigureAwait(false);

            try
            {
                DateTime lastSync;
                bool stale;
                lock (_lock) stale = !_historySyncedAt.TryGetValue(chatMid, out lastSync) ||
                    DateTime.UtcNow - lastSync >= HistoryRefreshTtl;
                if (stale)
                {
                    var fetched = await FetchHistoryAsync(chatMid).ConfigureAwait(false);
                    await _session.Line.DecryptMessagesAsync(fetched).ConfigureAwait(false);
                    MergeMessages(chatMid, fetched);
                    await SaveMessagesToDiskAsync(chatMid).ConfigureAwait(false);
                    await SaveHistoryCompleteMarkerAsync(chatMid).ConfigureAwait(false);
                    lock (_lock) _historySyncedAt[chatMid] = DateTime.UtcNow;
                }
                return GetCachedMessages(chatMid);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[ChatStore] messages fetch failed: " + ex.Message);
                if (local.Count == 0) throw;
                return local;
            }
        }

        /// <summary>Fast chat entry path: show cached tail, refresh only the newest page, never wait for full history pagination.</summary>
        public async Task<List<Message>> GetLatestMessagesAsync(string chatMid, int count = 50)
        {
            var localAll = GetCachedMessages(chatMid);
            var local = localAll.Skip(Math.Max(0, localAll.Count - Math.Max(1, count))).ToList();
            try
            {
                await RefreshLatestMessagesAsync(chatMid, Math.Max(30, count)).ConfigureAwait(false);
                var all = GetCachedMessages(chatMid);
                var latest = all.Skip(Math.Max(0, all.Count - Math.Max(1, count))).ToList();
                await _session.Line.DecryptMessagesAsync(latest).ConfigureAwait(false);
                return latest;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[ChatStore] latest message refresh failed: " + ex.Message);
                return local;
            }
        }

        private async Task<List<Message>> RefreshLatestMessagesAsync(string chatMid, int count)
        {
            var raw = await _session.Talk.GetRecentMessagesV2Async(chatMid, count).ConfigureAwait(false);
            var fetched = MapMessageList(raw, chatMid);
            if (fetched.Count == 0) return new List<Message>();
            await _session.Line.DecryptMessagesAsync(fetched).ConfigureAwait(false);
            MergeMessages(chatMid, fetched);
            var all = GetCachedMessages(chatMid);
            await SaveLatestMessagesToDiskAsync(chatMid, all.Skip(Math.Max(0, all.Count - 50)).ToList()).ConfigureAwait(false);
            await SaveChatActivityAsync().ConfigureAwait(false);
            return fetched;
        }

        public async Task<List<Message>> GetCachedLatestMessagesAsync(string chatMid, int count = 50)
        {
            try
            {
                var accounts = await ApplicationData.Current.LocalFolder.GetFolderAsync("accounts");
                var account = await accounts.GetFolderAsync(AccountId);
                var latestFile = await account.GetFileAsync("msg-latest-" + Sanitize(chatMid) + ".json");
                var latestArray = Windows.Data.Json.JsonArray.Parse(await FileIO.ReadTextAsync(latestFile));
                var cachedTail = new List<Message>();
                foreach (var item in latestArray) cachedTail.Add(DeserializeMessage(item.GetObject()));
                MergeMessages(chatMid, cachedTail);
                return cachedTail;
            }
            catch { }

            await LoadMessagesFromDiskAsync(chatMid).ConfigureAwait(false);
            var all = GetCachedMessages(chatMid);
            var latest = all.Skip(Math.Max(0, all.Count - Math.Max(1, count))).ToList();
            return latest;
        }

        /// <summary>Lightweight newest-message poll used while a chat is open.</summary>
        public async Task<List<Message>> RefreshRecentMessagesAsync(string chatMid, int count = 30)
        {
            var raw = await _session.Talk.GetRecentMessagesV2Async(chatMid, count).ConfigureAwait(false);
            var fetched = MapMessageList(raw, chatMid);
            if (fetched.Count == 0) return new List<Message>();
            await _session.Line.DecryptMessagesAsync(fetched).ConfigureAwait(false);
            MergeMessages(chatMid, fetched);
            DateTime loadedAt;
            bool fullHistoryLoaded;
            lock (_lock) fullHistoryLoaded = _historySyncedAt.TryGetValue(chatMid, out loadedAt);
            if (fullHistoryLoaded) await SaveMessagesToDiskAsync(chatMid).ConfigureAwait(false);
            else
            {
                var all = GetCachedMessages(chatMid);
                await SaveLatestMessagesToDiskAsync(chatMid, all.Skip(Math.Max(0, all.Count - 50)).ToList()).ConfigureAwait(false);
            }
            return fetched;
        }

        private async Task<List<Message>> FetchHistoryAsync(string chatMid)
        {
            var result = new List<Message>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            string endMessageId = "";
            long cutoff = DateTimeOffset.UtcNow.AddDays(-14).ToUnixTimeMilliseconds();
            bool receivedAny = false;
            try
            {
                var recent = await _session.Talk.GetRecentMessagesV2Async(chatMid, HistoryPageSize).ConfigureAwait(false);
                AddHistoryPage(recent, result, seen, cutoff, out endMessageId, out bool recentReachedCutoff);
                receivedAny = recent != null && recent.Count > 0;
                if (recentReachedCutoff || result.Count >= HistoryMaxMessages) return result;
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[ChatStore] recent history failed: " + ex.Message); }
            while (result.Count < HistoryMaxMessages)
            {
                List<object> raw;
                try
                {
                    raw = await _session.Talk.GetPreviousMessagesV2Async(
                        chatMid, endMessageId, HistoryPageSize, false, false, 0, 0).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("[ChatStore] history page failed: " + ex.Message);
                    if (!receivedAny)
                    {
                        raw = await _session.Talk.GetRecentMessagesV2Async(chatMid, HistoryPageSize).ConfigureAwait(false);
                    }
                    else break;
                }
                if (raw == null || raw.Count == 0) break;
                receivedAny = true;
                string oldestId; bool reachedCutoff;
                AddHistoryPage(raw, result, seen, cutoff, out oldestId, out reachedCutoff);
                if (reachedCutoff || string.IsNullOrEmpty(oldestId) || oldestId == endMessageId) break;
                endMessageId = oldestId;
            }
            return result;
        }

        private void AddHistoryPage(List<object> raw, List<Message> output, HashSet<string> seen,
            long cutoff, out string oldestId, out bool reachedCutoff)
        {
            oldestId = null;
            reachedCutoff = false;
            var page = MapMessageList(raw, null);
            long oldestTime = long.MaxValue;
            foreach (var message in page)
            {
                long ts = NormalizeTimestamp(message.CreatedTime ?? 0);
                if (ts > 0 && (oldestId == null || ChatActivityProtocol.Compare(ts, message.Id, oldestTime, oldestId) < 0)) { oldestTime = ts; oldestId = message.Id; }
                if (ts > 0 && ts < cutoff) reachedCutoff = true;
                if ((ts == 0 || ts >= cutoff) && message.Id != null && seen.Add(message.Id)) output.Add(message);
            }
        }

        private static long NormalizeTimestamp(long value)
        {
            // LINE timestamps are normally milliseconds; tolerate seconds.
            return value > 0 && value < 100000000000L ? value * 1000L : value;
        }

        public void MergeMessages(string chatMid, List<Message> incoming)
        {
            if (incoming == null || incoming.Count == 0) return;
            lock (_lock)
            {
                List<Message> list;
                if (!_messages.TryGetValue(chatMid, out list))
                {
                    list = new List<Message>();
                    _messages[chatMid] = list;
                }
                var byId = new Dictionary<string, Message>(StringComparer.Ordinal);
                foreach (var m in list) if (m != null && !string.IsNullOrEmpty(m.Id)) byId[m.Id] = m;
                foreach (var m in incoming)
                {
                    if (m == null || string.IsNullOrEmpty(m.Id)) continue;
                    Message previous;
                    byId.TryGetValue(m.Id, out previous);
                    if (previous != null)
                    {
                        if (!m.CreatedTime.HasValue || m.CreatedTime <= 0) m.CreatedTime = previous.CreatedTime;
                        if (string.IsNullOrEmpty(m.From)) m.From = previous.From;
                        if (string.IsNullOrEmpty(m.To)) m.To = previous.To;
                    }
                    if (previous != null && previous.ContentMetadata != null)
                    {
                        string oldRead, oldCount, newCount;
                        if (previous.ContentMetadata.TryGetValue("read", out oldRead) && oldRead == "1")
                        {
                            m.ContentMetadata = m.ContentMetadata ?? new Dictionary<string, string>();
                            m.ContentMetadata["read"] = "1";
                            int a = 0, b = 0;
                            previous.ContentMetadata.TryGetValue("readCount", out oldCount);
                            m.ContentMetadata.TryGetValue("readCount", out newCount);
                            int.TryParse(oldCount, out a); int.TryParse(newCount, out b);
                            m.ContentMetadata["readCount"] = Math.Max(a, b).ToString();
                        }
                        string state;
                        if (previous.ContentMetadata.TryGetValue("messageState", out state) && state == "revoked")
                        {
                            m.ContentMetadata = m.ContentMetadata ?? new Dictionary<string, string>();
                            m.ContentMetadata["messageState"] = "revoked";
                            m.Text = null;
                        }
                    }
                    if (previous != null)
                    {
                        if (!m.ReactionsKnown && previous.ReactionsKnown) { m.Reactions = previous.Reactions; m.ReactionsKnown = true; }
                        if (string.IsNullOrEmpty(m.ReplyPreview)) m.ReplyPreview = previous.ReplyPreview;
                        if (string.IsNullOrEmpty(m.RelatedMessageId)) { m.RelatedMessageId = previous.RelatedMessageId; m.RelationType = previous.RelationType; }
                    }
                    if (previous != null && previous.CreatedTime == m.CreatedTime && previous.From == m.From && previous.To == m.To && previous.Text == m.Text && previous.ContentType == m.ContentType &&
                        previous.RelatedMessageId == m.RelatedMessageId && previous.ReactionsKnown == m.ReactionsKnown &&
                        previous.Reactions.Count == m.Reactions.Count && m.Reactions.All(r => previous.Reactions.Any(p => p.FromMid == r.FromMid && p.Key == r.Key && p.ResourceType == r.ResourceType && p.Version == r.Version && p.AtMillis == r.AtMillis)) &&
                        ((previous.ContentMetadata == null && m.ContentMetadata == null) ||
                         (previous.ContentMetadata != null && m.ContentMetadata != null && previous.ContentMetadata.Count == m.ContentMetadata.Count &&
                          previous.ContentMetadata.All(pair => m.ContentMetadata.ContainsKey(pair.Key) && m.ContentMetadata[pair.Key] == pair.Value))))
                    {
                        if (!string.IsNullOrEmpty(m.MediaKeyMaterial)) previous.MediaKeyMaterial = m.MediaKeyMaterial;
                        byId[m.Id] = previous;
                    }
                    else byId[m.Id] = m;
                }
                list.Clear();
                list.AddRange(byId.Values);
                list.Sort(MessageTimeline.Compare);
                if (list.Count > 0) UpdateActivityLocked(chatMid, list[list.Count - 1]);
                foreach (var message in list)
                {
                    Message target;
                    if (!string.IsNullOrEmpty(message.RelatedMessageId) && byId.TryGetValue(message.RelatedMessageId, out target))
                        message.ReplyPreview = target.IsRevoked ? "取り消されたメッセージ" : !string.IsNullOrEmpty(target.Text) ? target.Text : target.MediaFallbackText;
                }
            }
        }

        public void UpsertMessage(string chatMid, Message message)
        {
            if (message == null || message.Id == null) return;
            MergeMessages(chatMid, new List<Message> { message });
        }

        public void MarkRevoked(string chatMid, string messageId)
        {
            lock (_lock)
            {
                List<Message> list;
                if (!_messages.TryGetValue(chatMid, out list)) return;
                for (int i = 0; i < list.Count; i++)
                {
                    if (list[i].Id == messageId)
                    {
                        var m = list[i];
                        m.ContentMetadata = m.ContentMetadata ?? new Dictionary<string, string>();
                        m.ContentMetadata["messageState"] = "revoked";
                        m.Text = null;
                        m.MediaImage = null;
                        m.RefreshState();
                    }
                }
            }
        }

        public Task SaveMessagesAsync(string chatMid)
        {
            return SaveMessagesToDiskAsync(chatMid);
        }

        public async Task ApplyRemoteRevokeAsync(string chatMid, string messageId)
        {
            if (string.IsNullOrEmpty(chatMid) || string.IsNullOrEmpty(messageId)) return;
            await LoadMessagesFromDiskAsync(chatMid).ConfigureAwait(false);
            MarkRevoked(chatMid, messageId);
            await SaveMessagesToDiskAsync(chatMid).ConfigureAwait(false);
        }

        public List<Message> GetCachedMessages(string chatMid)
        {
            lock (_lock)
            {
                List<Message> list;
                if (!_messages.TryGetValue(chatMid, out list)) return new List<Message>();
                return list.ToList();
            }
        }

        public List<Chat> GetCachedChats()
        {
            lock (_lock) return _chats.ToList();
        }

        public async Task LoadUnreadCountsAsync()
        {
            lock (_lock) if (_unreadLoaded) return;
            try
            {
                var folder = await ApplicationData.Current.LocalFolder.GetFolderAsync("accounts");
                var account = await folder.GetFolderAsync(AccountId);
                var file = await account.GetFileAsync("unread.json");
                var obj = Windows.Data.Json.JsonObject.Parse(await FileIO.ReadTextAsync(file));
                lock (_lock)
                {
                    foreach (var pair in obj)
                    {
                        if (pair.Value.ValueType == Windows.Data.Json.JsonValueType.Number)
                            _unreadCounts[pair.Key] = (int)pair.Value.GetNumber();
                    }
                    _unreadLoaded = true;
                }
            }
            catch { lock (_lock) _unreadLoaded = true; }
        }

        public int GetUnreadCount(string chatMid)
        {
            lock (_lock) { int value; return _unreadCounts.TryGetValue(chatMid, out value) ? value : 0; }
        }

        public async Task IncrementUnreadAsync(string chatMid)
        {
            if (string.IsNullOrEmpty(chatMid)) return;
            await LoadUnreadCountsAsync().ConfigureAwait(false);
            lock (_lock)
            {
                int count; _unreadCounts.TryGetValue(chatMid, out count);
                _unreadCounts[chatMid] = Math.Min(999, count + 1);
            }
            await SaveUnreadCountsAsync().ConfigureAwait(false);
        }

        public async Task ClearUnreadAsync(string chatMid)
        {
            await LoadUnreadCountsAsync().ConfigureAwait(false);
            lock (_lock) _unreadCounts.Remove(chatMid);
            await SaveUnreadCountsAsync().ConfigureAwait(false);
            await BackgroundNotifications.MarkNotificationReadAsync(AccountId, chatMid).ConfigureAwait(false);
        }

        public async Task ApplyReadRangeAsync(string chatMid, Dictionary<string, long> lastReadByMember)
        {
            if (lastReadByMember == null || lastReadByMember.Count == 0) return;
            var messages = GetCachedMessages(chatMid);
            bool changed = false;
            foreach (var message in messages)
            {
                if (message == null || message.Id == null ||
                    (message.From != _session.Mid && message.From != "me")) continue;
                long messageId;
                if (!long.TryParse(message.Id, out messageId)) continue;
                int readCount = 0;
                foreach (var readId in lastReadByMember.Values) if (messageId <= readId) readCount++;
                if (readCount > 0)
                {
                    message.ContentMetadata = message.ContentMetadata ?? new Dictionary<string, string>();
                    message.ContentMetadata["read"] = "1";
                    string priorCount;
                    int prior = 0;
                    if (message.ContentMetadata.TryGetValue("readCount", out priorCount)) int.TryParse(priorCount, out prior);
                    readCount = Math.Max(readCount, prior);
                    message.ContentMetadata["readCount"] = readCount.ToString();
                    message.ReadLabel = chatMid.StartsWith("c") || chatMid.StartsWith("r") ? "既読" + readCount : "既読";
                    changed = true;
                }
            }
            if (changed)
            {
                DateTime loadedAt; bool full;
                lock (_lock) full = _historySyncedAt.TryGetValue(chatMid, out loadedAt);
                if (full) await SaveMessagesToDiskAsync(chatMid).ConfigureAwait(false);
                else await SaveLatestMessagesToDiskAsync(chatMid, messages.Skip(Math.Max(0, messages.Count - 50)).ToList()).ConfigureAwait(false);
            }
        }

        private async Task SaveUnreadCountsAsync()
        {
            try
            {
                var folder = await ApplicationData.Current.LocalFolder.CreateFolderAsync("accounts", CreationCollisionOption.OpenIfExists);
                var account = await folder.CreateFolderAsync(AccountId, CreationCollisionOption.OpenIfExists);
                var obj = new Windows.Data.Json.JsonObject();
                lock (_lock)
                    foreach (var pair in _unreadCounts) obj[pair.Key] = Windows.Data.Json.JsonValue.CreateNumberValue(pair.Value);
                var file = await account.CreateFileAsync("unread.json", CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteTextAsync(file, obj.Stringify());
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[ChatStore] save unread failed: " + ex.Message); }
        }

        public List<Message> MapMessageList(List<object> rawList, string chatMid)
        {
            var result = new List<Message>();
            if (rawList == null) return result;
            foreach (var item in rawList)
            {
                var md = item as Dictionary<int, object>;
                if (md == null) continue;
                var msg = MapMessage(md, chatMid);
                if (msg != null) result.Add(msg);
            }
            return result;
        }

        public static Message MapMessage(Dictionary<int, object> md, string chatMid)
        {
            if (md == null) return null;
            var msg = new Message();
            object v;
            if (md.TryGetValue(4, out v) && v != null) msg.Id = v.ToString();
            if (md.TryGetValue(1, out v) && v != null) msg.From = v.ToString();
            if (md.TryGetValue(2, out v) && v != null) msg.To = v.ToString();
            if (md.TryGetValue(21, out v) && v != null) msg.RelatedMessageId = v.ToString();
            if (md.TryGetValue(22, out v) && v != null)
            {
                int relationType;
                if (int.TryParse(v.ToString(), out relationType)) msg.RelationType = relationType;
            }
            if (md.TryGetValue(10, out v) && v != null) msg.Text = v.ToString();
            if (md.TryGetValue(15, out v) && v != null)
            {
                int ct; if (int.TryParse(v.ToString(), out ct)) msg.ContentType = ct;
            }
            if (md.TryGetValue(5, out v) && v != null)
            {
                long t; if (long.TryParse(v.ToString(), out t)) msg.CreatedTime = t;
            }
            if (md.TryGetValue(18, out v) && v is Dictionary<object, object> meta)
            {
                msg.ContentMetadata = new Dictionary<string, string>();
                foreach (var kv in meta)
                {
                    if (kv.Key != null && kv.Value != null)
                        msg.ContentMetadata[kv.Key.ToString()] = kv.Value.ToString();
                }
            }
            else if (md.TryGetValue(18, out v) && v is Dictionary<int, object> meta2)
            {
                msg.ContentMetadata = new Dictionary<string, string>();
                foreach (var kv in meta2)
                    if (kv.Value != null) msg.ContentMetadata[kv.Key.ToString()] = kv.Value.ToString();
            }
            if (md.TryGetValue(20, out v) && v is List<object> chunks)
            {
                msg.Chunks = new List<byte[]>();
                foreach (var c in chunks)
                {
                    if (c is byte[] b) msg.Chunks.Add(b);
                    else if (c is string s)
                        msg.Chunks.Add(System.Text.Encoding.UTF8.GetBytes(s));
                }
            }
            if (md.TryGetValue(27, out v)) { msg.ReactionsKnown = true; msg.Reactions = ReactionProtocol.Read(v); }
            return msg;
        }

        // ── Disk persistence (ApplicationData) ────────────────

        private async Task LoadChatsFromDiskAsync()
        {
            try
            {
                var path = "accounts/" + AccountId + "/chats.json";
                var file = await ApplicationData.Current.LocalFolder.GetFileAsync(path);
                var text = await FileIO.ReadTextAsync(file);
                var arr = Windows.Data.Json.JsonArray.Parse(text);
                var list = new List<Chat>();
                foreach (var t in arr)
                {
                    var o = t.GetObject();
                    var c = new Chat();
                    c.ChatMid = GetJsonStr(o, "mid");
                    c.ChatName = GetJsonStr(o, "name");
                    c.PicturePath = GetJsonStr(o, "picturePath");
                    c.ChatType = (int?)GetJsonLong(o, "type");
                    c.MemberCount = o.ContainsKey("members") ? (int?)GetJsonLong(o, "members") : null;
                    if (!string.IsNullOrEmpty(c.ChatMid)) list.Add(c);
                }
                lock (_lock)
                {
                    if (list.Count > 0)
                    {
                        _chats = list;
                        _chatsSyncedAt = DateTime.UtcNow.AddMilliseconds(-ChatsCacheMs - 1); // force refresh
                    }
                }
            }
            catch { }
        }

        private async Task SaveChatsToDiskAsync(List<Chat> chats)
        {
            try
            {
                var folder = await ApplicationData.Current.LocalFolder.CreateFolderAsync(
                    "accounts", CreationCollisionOption.OpenIfExists);
                var sub = await folder.CreateFolderAsync(AccountId, CreationCollisionOption.OpenIfExists);
                var arr = new Windows.Data.Json.JsonArray();
                foreach (var c in chats)
                {
                    var o = new Windows.Data.Json.JsonObject();
                    o["mid"] = Windows.Data.Json.JsonValue.CreateStringValue(c.ChatMid ?? "");
                    o["name"] = Windows.Data.Json.JsonValue.CreateStringValue(c.ChatName ?? "");
                    o["picturePath"] = Windows.Data.Json.JsonValue.CreateStringValue(c.PicturePath ?? "");
                    o["type"] = Windows.Data.Json.JsonValue.CreateNumberValue(c.ChatType ?? 0);
                    o["members"] = Windows.Data.Json.JsonValue.CreateNumberValue(c.MemberCount ?? 0);
                    arr.Add(o);
                }
                var file = await sub.CreateFileAsync("chats.json", CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteTextAsync(file, arr.Stringify());
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[ChatStore] save chats failed: " + ex.Message);
            }
        }

        private async Task LoadMessagesFromDiskAsync(string chatMid)
        {
            lock (_lock)
            {
                if (_fullCacheLoaded.Contains(chatMid)) return;
            }
            try
            {
                var path = "accounts/" + AccountId + "/msg-" + Sanitize(chatMid) + ".json";
                var file = await ApplicationData.Current.LocalFolder.GetFileAsync(path);
                var text = await FileIO.ReadTextAsync(file);
                var arr = Windows.Data.Json.JsonArray.Parse(text);
                var list = new List<Message>();
                foreach (var t in arr)
                {
                    var o = t.GetObject();
                    var m = DeserializeMessage(o);
                    if (m.Id != null) list.Add(m);
                }
                MergeMessages(chatMid, list);
                lock (_lock) _fullCacheLoaded.Add(chatMid);
                try
                {
                    var folder = await ApplicationData.Current.LocalFolder.GetFolderAsync("accounts");
                    var account = await folder.GetFolderAsync(AccountId);
                    await account.GetFileAsync("history-complete-" + Sanitize(chatMid) + ".txt");
                    lock (_lock) _historySyncedAt[chatMid] = DateTime.UtcNow;
                }
                catch { }
            }
            catch { }
        }

        private async Task SaveHistoryCompleteMarkerAsync(string chatMid)
        {
            try
            {
                var folder = await ApplicationData.Current.LocalFolder.CreateFolderAsync("accounts", CreationCollisionOption.OpenIfExists);
                var account = await folder.CreateFolderAsync(AccountId, CreationCollisionOption.OpenIfExists);
                var marker = await account.CreateFileAsync("history-complete-" + Sanitize(chatMid) + ".txt", CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteTextAsync(marker, DateTime.UtcNow.ToString("o"));
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[ChatStore] history marker failed: " + ex.Message); }
        }

        private async Task SaveMessagesToDiskAsync(string chatMid)
        {
            try
            {
                List<Message> snapshot;
                lock (_lock)
                {
                    List<Message> list;
                    if (!_messages.TryGetValue(chatMid, out list)) return;
                    snapshot = list.ToList();
                }
                var folder = await ApplicationData.Current.LocalFolder.CreateFolderAsync(
                    "accounts", CreationCollisionOption.OpenIfExists);
                var sub = await folder.CreateFolderAsync(AccountId, CreationCollisionOption.OpenIfExists);
                var arr = new Windows.Data.Json.JsonArray();
                foreach (var m in snapshot)
                {
                    var o = SerializeMessage(m);
                    o["id"] = Windows.Data.Json.JsonValue.CreateStringValue(m.Id ?? "");
                    o["from"] = Windows.Data.Json.JsonValue.CreateStringValue(m.From ?? "");
                    o["to"] = Windows.Data.Json.JsonValue.CreateStringValue(m.To ?? "");
                    o["text"] = Windows.Data.Json.JsonValue.CreateStringValue(m.Text ?? "");
                    o["ct"] = Windows.Data.Json.JsonValue.CreateNumberValue(m.ContentType);
                    o["ts"] = Windows.Data.Json.JsonValue.CreateNumberValue(m.CreatedTime ?? 0);
                    var metadata = new Windows.Data.Json.JsonObject();
                    if (m.ContentMetadata != null)
                        foreach (var pair in m.ContentMetadata)
                            metadata[pair.Key] = Windows.Data.Json.JsonValue.CreateStringValue(pair.Value ?? "");
                    o["meta"] = metadata;
                    var chunks = new Windows.Data.Json.JsonArray();
                    if (m.Chunks != null)
                        foreach (var chunk in m.Chunks)
                            chunks.Add(Windows.Data.Json.JsonValue.CreateStringValue(Convert.ToBase64String(chunk ?? new byte[0])));
                    o["chunks"] = chunks;
                    arr.Add(o);
                }
                var file = await sub.CreateFileAsync("msg-" + Sanitize(chatMid) + ".json",
                    CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteTextAsync(file, arr.Stringify());
                await SaveLatestMessagesToDiskAsync(chatMid, snapshot.Skip(Math.Max(0, snapshot.Count - 50)).ToList()).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[ChatStore] save msgs failed: " + ex.Message);
            }
        }

        private async Task SaveLatestMessagesToDiskAsync(string chatMid, List<Message> latest)
        {
            try
            {
                var accounts = await ApplicationData.Current.LocalFolder.CreateFolderAsync("accounts", CreationCollisionOption.OpenIfExists);
                var account = await accounts.CreateFolderAsync(AccountId, CreationCollisionOption.OpenIfExists);
                var array = new Windows.Data.Json.JsonArray();
                foreach (var m in latest) array.Add(SerializeMessage(m));
                var file = await account.CreateFileAsync("msg-latest-" + Sanitize(chatMid) + ".json", CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteTextAsync(file, array.Stringify());
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[ChatStore] save latest cache failed: " + ex.Message); }
        }

        private static Windows.Data.Json.JsonObject SerializeMessage(Message m)
        {
            var o = new Windows.Data.Json.JsonObject();
            o["id"] = Windows.Data.Json.JsonValue.CreateStringValue(m.Id ?? "");
            o["from"] = Windows.Data.Json.JsonValue.CreateStringValue(m.From ?? "");
            o["to"] = Windows.Data.Json.JsonValue.CreateStringValue(m.To ?? "");
            o["text"] = Windows.Data.Json.JsonValue.CreateStringValue(m.Text ?? "");
            o["ct"] = Windows.Data.Json.JsonValue.CreateNumberValue(m.ContentType);
            o["ts"] = Windows.Data.Json.JsonValue.CreateNumberValue(m.CreatedTime ?? 0);
            o["replyId"] = Windows.Data.Json.JsonValue.CreateStringValue(m.RelatedMessageId ?? "");
            o["relation"] = Windows.Data.Json.JsonValue.CreateNumberValue(m.RelationType ?? 0);
            o["replyPreview"] = Windows.Data.Json.JsonValue.CreateStringValue(m.ReplyPreview ?? "");
            o["reactionsKnown"] = Windows.Data.Json.JsonValue.CreateBooleanValue(m.ReactionsKnown);
            var reactions = new Windows.Data.Json.JsonArray();
            foreach (var reaction in m.Reactions)
            {
                var item = new Windows.Data.Json.JsonObject();
                item["from"] = Windows.Data.Json.JsonValue.CreateStringValue(reaction.FromMid ?? "");
                item["at"] = Windows.Data.Json.JsonValue.CreateNumberValue(reaction.AtMillis);
                item["type"] = Windows.Data.Json.JsonValue.CreateNumberValue(reaction.Type);
                item["product"] = Windows.Data.Json.JsonValue.CreateStringValue(reaction.ProductId ?? "");
                item["emoji"] = Windows.Data.Json.JsonValue.CreateStringValue(reaction.EmojiId ?? "");
                if (reaction.ResourceType.HasValue) item["resource"] = Windows.Data.Json.JsonValue.CreateNumberValue(reaction.ResourceType.Value);
                // Preserve I64 exactly; JSON numbers lose integer precision above 2^53.
                if (reaction.Version.HasValue) item["version"] = Windows.Data.Json.JsonValue.CreateStringValue(reaction.Version.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
                reactions.Add(item);
            }
            o["reactions"] = reactions;
            var metadata = new Windows.Data.Json.JsonObject();
            if (m.ContentMetadata != null)
                foreach (var pair in m.ContentMetadata) metadata[pair.Key] = Windows.Data.Json.JsonValue.CreateStringValue(pair.Value ?? "");
            o["meta"] = metadata;
            var chunks = new Windows.Data.Json.JsonArray();
            if (m.Chunks != null)
                foreach (var chunk in m.Chunks) chunks.Add(Windows.Data.Json.JsonValue.CreateStringValue(Convert.ToBase64String(chunk ?? new byte[0])));
            o["chunks"] = chunks;
            return o;
        }

        private static Message DeserializeMessage(Windows.Data.Json.JsonObject o)
        {
            var m = new Message
            {
                Id = GetJsonStr(o, "id"), From = GetJsonStr(o, "from"), To = GetJsonStr(o, "to"),
                Text = GetJsonStr(o, "text"), ContentType = (int)GetJsonLong(o, "ct"), CreatedTime = GetJsonLong(o, "ts"),
                RelatedMessageId = GetJsonStr(o, "replyId"), RelationType = (int)GetJsonLong(o, "relation"), ReplyPreview = GetJsonStr(o, "replyPreview")
            };
            if (o.ContainsKey("reactions"))
            {
                var reactions = new List<MessageReaction>();
                foreach (var value in o.GetNamedArray("reactions"))
                {
                    var item = value.GetObject();
                    long version;
                    reactions.Add(new MessageReaction { FromMid = GetJsonStr(item, "from"), AtMillis = GetJsonLong(item, "at"), Type = (int)GetJsonLong(item, "type"), ProductId = GetJsonStr(item, "product"), EmojiId = GetJsonStr(item, "emoji"), ResourceType = item.ContainsKey("resource") ? (int?)GetJsonLong(item, "resource") : null, Version = long.TryParse(GetJsonStr(item, "version"), out version) ? (long?)version : null });
                }
                m.Reactions = reactions;
                m.ReactionsKnown = o.ContainsKey("reactionsKnown") && o.GetNamedBoolean("reactionsKnown");
            }
            try
            {
                if (o.ContainsKey("meta"))
                {
                    m.ContentMetadata = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var pair in o.GetNamedObject("meta"))
                        if (pair.Value.ValueType == Windows.Data.Json.JsonValueType.String) m.ContentMetadata[pair.Key] = pair.Value.GetString();
                }
            }
            catch { }
            try
            {
                if (o.ContainsKey("chunks"))
                {
                    m.Chunks = new List<byte[]>();
                    foreach (var chunk in o.GetNamedArray("chunks")) m.Chunks.Add(Convert.FromBase64String(chunk.GetString()));
                }
            }
            catch { m.Chunks = null; }
            return m;
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

        private static string GetJsonStr(Windows.Data.Json.JsonObject o, string k)
        {
            try { return o.ContainsKey(k) ? o.GetNamedString(k) : null; }
            catch { return null; }
        }

        private static long GetJsonLong(Windows.Data.Json.JsonObject o, string k)
        {
            try
            {
                if (!o.ContainsKey(k)) return 0;
                var t = o.GetNamedValue(k);
                if (t.ValueType == Windows.Data.Json.JsonValueType.Number) return (long)t.GetNumber();
                if (t.ValueType == Windows.Data.Json.JsonValueType.String)
                {
                    long v; long.TryParse(t.GetString(), out v); return v;
                }
            }
            catch { }
            return 0;
        }
    }

    /// <summary>Read receipts (vyline refreshReadReceipts + markAsRead).</summary>
    public class ReadEngine
    {
        private readonly LineSession _session;
        private readonly ChatStore _store;
        private readonly Dictionary<string, DateTime> _watermarkAt =
            new Dictionary<string, DateTime>(StringComparer.Ordinal);
        private readonly object _lock = new object();
        private static readonly TimeSpan WatermarkTtl = TimeSpan.FromMinutes(2);
        public event Action<string> ReadReceiptsUpdated;

        public ReadEngine(LineSession session, ChatStore store)
        {
            _session = session;
            _store = store;
        }

        /// <summary>markAsRead: tryRefreshToken → resolve lastMessageId → sendChatChecked.</summary>
        public async Task<string> MarkAsReadAsync(string chatMid, string lastMessageId = null)
        {
            await _session.Auth.TryRefreshTokenAsync().ConfigureAwait(false);

            var messageId = (lastMessageId ?? "").Trim();
            if (messageId.StartsWith("pending_")) messageId = "";

            if (string.IsNullOrEmpty(messageId))
            {
                var boxes = await _session.Talk.GetMessageBoxesAsync().ConfigureAwait(false);
                var boxList = FindListBox(boxes);
                if (boxList != null)
                {
                    foreach (var item in boxList)
                    {
                        var bd = item as Dictionary<int, object>;
                        if (bd == null) continue;
                        string id = null;
                        foreach (var key in new[] { 1, 2, 3 })
                        {
                            object v;
                            if (bd.TryGetValue(key, out v) && v is string s &&
                                (s.StartsWith("u") || s.StartsWith("c") || s.StartsWith("r")))
                            {
                                id = s; break;
                            }
                        }
                        if (id != chatMid) continue;
                        // lastDeliveredMessageId.messageId — scan nested
                        messageId = FindLastMessageId(bd);
                        break;
                    }
                }
            }

            if (string.IsNullOrEmpty(messageId))
            {
                System.Diagnostics.Debug.WriteLine("[Read] markAsRead skipped: no lastMessageId chat=" + chatMid);
                return null;
            }

            await _session.Talk.SendChatCheckedAsync(chatMid, messageId).ConfigureAwait(false);

            lock (_lock) _watermarkAt[chatMid] = DateTime.UtcNow;

            return messageId;
        }

        public async Task RefreshReadReceiptsAsync(string chatMid, bool force = false)
        {
            if (string.IsNullOrEmpty(chatMid)) return;
            lock (_lock)
            {
                DateTime at;
                if (!force && _watermarkAt.TryGetValue(chatMid, out at) &&
                    (DateTime.UtcNow - at) < WatermarkTtl)
                    return;
            }

            var msgs = _store.GetCachedMessages(chatMid);
            var myIds = new List<string>();
            bool needsPoll = false;
            foreach (var m in msgs)
            {
                bool isMine = m.From == _session.Mid || m.From == "me";
                if (!isMine) continue;
                if (m.Id == null || m.Id.StartsWith("pending_")) continue;
                if (m.ContentMetadata != null &&
                    (m.ContentMetadata.ContainsKey("messageState") &&
                     m.ContentMetadata["messageState"].StartsWith("revoked")))
                    continue;
                myIds.Add(m.Id);
                string readValue;
                bool read = m.ContentMetadata != null && m.ContentMetadata.TryGetValue("read", out readValue) && readValue == "1";
                if (chatMid.StartsWith("c") || chatMid.StartsWith("r") || !read) needsPoll = true;
            }
            if (!needsPoll || myIds.Count == 0) return;

            try
            {
                var range = await _session.Talk.GetMessageReadRangeAsync(
                    new List<string> { chatMid }).ConfigureAwait(false);
                var readByMember = ParseReadRanges(range, chatMid);
                await _store.ApplyReadRangeAsync(chatMid, readByMember).ConfigureAwait(false);
                if (readByMember.Count > 0)
                {
                    var handler = ReadReceiptsUpdated;
                    if (handler != null) handler(chatMid);
                }
                lock (_lock) _watermarkAt[chatMid] = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Read] refresh failed: " + ex.Message);
            }
        }

        private Dictionary<string, long> ParseReadRanges(List<object> response, string chatMid)
        {
            var output = new Dictionary<string, long>(StringComparer.Ordinal);
            if (response == null) return output;
            foreach (var item in response)
            {
                var entry = item as Dictionary<int, object>;
                if (entry == null || !string.Equals(GetString(entry, 1), chatMid, StringComparison.Ordinal)) continue;
                object membersObj;
                if (!entry.TryGetValue(2, out membersObj)) continue;
                var members = membersObj as Dictionary<object, object>;
                if (members == null) continue;
                foreach (var pair in members)
                {
                    string memberMid = pair.Key == null ? null : pair.Key.ToString();
                    if (string.IsNullOrEmpty(memberMid) || memberMid == _session.Mid) continue;
                    var ranges = pair.Value as List<object>;
                    if (ranges == null) continue;
                    long maxRead = 0;
                    foreach (var range in ranges)
                    {
                        var fields = range as Dictionary<int, object>;
                        object value; long id;
                        if (fields != null && fields.TryGetValue(2, out value) && value != null && long.TryParse(value.ToString(), out id))
                            maxRead = Math.Max(maxRead, id);
                    }
                    if (maxRead > 0) output[memberMid] = maxRead;
                }
            }
            return output;
        }

        private static string GetString(Dictionary<int, object> fields, int id)
        {
            object value;
            return fields != null && fields.TryGetValue(id, out value) && value != null ? value.ToString() : null;
        }

        private static List<object> FindListBox(Dictionary<int, object> d)
        {
            if (d == null) return null;
            foreach (var key in new[] { 1, 2, 0, 3 })
            {
                object v;
                if (d.TryGetValue(key, out v))
                {
                    var list = v as List<object>;
                    if (list != null) return list;
                }
            }
            return null;
        }

        private static string FindLastMessageId(Dictionary<int, object> box)
        {
            // recurse for lastDeliveredMessageId.messageId
            foreach (var kv in box)
            {
                var d = kv.Value as Dictionary<int, object>;
                if (d == null)
                {
                    // maybe messageId directly as string field
                    continue;
                }
                // look for messageId-like string in nested struct
                foreach (var nk in d)
                {
                    if (nk.Value is string s && s.Length > 0 && char.IsDigit(s[0]))
                        return s;
                }
            }
            return null;
        }
    }
}
