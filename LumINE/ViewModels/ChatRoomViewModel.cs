using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Windows.UI.Xaml.Media.Imaging;
using LumINE.Models;
using LumINE.Services.Vyline;

namespace LumINE.ViewModels
{
    public class ChatRoomViewModel : BaseViewModel
    {
        private readonly string _chatMid;
        private string _messageText;
        private string _statusMessage;
        private string _replyTargetId;
        private string _replyTargetPreview;
        public string ReplyTargetPreview { get { return _replyTargetPreview; } }
        private Task _historyTask;
        private bool _historyLoaded;
        public bool HasReplyTarget { get { return !string.IsNullOrEmpty(_replyTargetId); } }
        public event Action MessagesChanged;
        private readonly Dictionary<string, ChatStoreSenderInfo> _senderCache = new Dictionary<string, ChatStoreSenderInfo>(StringComparer.Ordinal);

        public ObservableCollection<Message> Messages { get; set; } = new ObservableCollection<Message>();

        public string MessageText
        {
            get { return _messageText; }
            set { _messageText = value; OnPropertyChanged("MessageText"); }
        }

        public string StatusMessage
        {
            get { return _statusMessage; }
            set { _statusMessage = value; OnPropertyChanged("StatusMessage"); OnPropertyChanged("HasStatusMessage"); }
        }
        public bool HasStatusMessage { get { return !string.IsNullOrWhiteSpace(_statusMessage); } }

        public string ChatMid { get { return _chatMid; } }
        public bool IsLatestMessage(string id)
        {
            for (int i = Messages.Count - 1; i >= 0; i--)
                if (!Messages[i].IsDateSeparator) return !string.IsNullOrEmpty(id) && Messages[i].Id == id;
            return false;
        }
        public void SetReplyTarget(Message message)
        {
            _replyTargetId = message == null ? null : message.Id;
            _replyTargetPreview = message == null ? "" : "返信先: " + (message.IsRevoked ? "取り消されたメッセージ" : !string.IsNullOrWhiteSpace(message.Text) ? message.Text : message.MediaFallbackText);
            OnPropertyChanged("HasReplyTarget");
            OnPropertyChanged("ReplyTargetPreview");
        }

        public Task UnsendAsync(Message message) { return RunBusyAsync(() => UnsendCoreAsync(message)); }

        private async Task UnsendCoreAsync(Message message)
        {
            var session = SessionManager.Current;
            if (session == null || message == null || string.IsNullOrEmpty(message.Id)) return;
            if (!message.CanUnsend) { StatusMessage = "送信から1時間を過ぎたメッセージは取り消せません。"; return; }
            try
            {
                await session.Talk.UnsendMessageAsync(message.Id);
                session.Store.MarkRevoked(_chatMid, message.Id);
                await session.Store.SaveMessagesAsync(_chatMid);
                message.Text = null;
                DisplayMessages(session, session.Store.GetCachedMessages(_chatMid));
                StatusMessage = "";
            }
            catch (Exception ex) { StatusMessage = "送信を取り消せませんでした: " + ex.Message; }
        }

        public Task LoadOlderHistoryAsync()
        {
            return _historyTask ?? (_historyTask = RunBusyAsync(LoadHistoryCoreAsync));
        }

        private async Task LoadHistoryCoreAsync()
        {
            var session = SessionManager.Current;
            if (session == null) return;
            try
            {
                var history = await session.Line.FetchMessagesAsync(_chatMid, 50);
                _historyLoaded = true;
                DisplayMessages(session, history);
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[ChatRoom] history load failed: " + ex.Message); }
        }

        public ChatRoomViewModel(string chatMid)
        {
            _chatMid = chatMid;
        }

        /// <summary>Must be called on the UI thread.</summary>
        public Task LoadMessagesAsync() { return RunBusyAsync(LoadMessagesCoreAsync); }

        private async Task LoadMessagesCoreAsync()
        {
            var session = SessionManager.Current;
            if (session == null) return;
            try
            {
                var cached = await session.Line.FetchCachedLatestMessagesAsync(_chatMid, 50);
                if (cached.Count > 0) DisplayMessages(session, _historyLoaded ? session.Store.GetCachedMessages(_chatMid) : cached);
                var messages = await session.Line.FetchLatestMessagesAsync(_chatMid, 50);
                DisplayMessages(session, _historyLoaded ? session.Store.GetCachedMessages(_chatMid) : messages);
                StatusMessage = "";
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[ChatRoom] load failed: " + ex.Message);
                StatusMessage = "メッセージを取得できませんでした。通信状態を確認してください。";
            }
        }

        public void RefreshCachedMessages()
        {
            var session = SessionManager.Current;
            if (session != null) DisplayMessages(session, session.Store.GetCachedMessages(_chatMid), false);
        }

        public async Task<List<Message>> SearchMessagesAsync(string query)
        {
            await LoadOlderHistoryAsync();
            var found = new List<Message>();
            if (string.IsNullOrWhiteSpace(query)) return found;
            foreach (var message in Messages)
                if (!message.IsDateSeparator && !message.IsRevoked && !string.IsNullOrEmpty(message.Text) &&
                    message.Text.IndexOf(query.Trim(), StringComparison.OrdinalIgnoreCase) >= 0) found.Add(message);
            found.Reverse();
            return found;
        }

        public Task ReactAsync(Message message, int type) { return RunBusyAsync(() => ReactCoreAsync(message, type)); }

        private async Task ReactCoreAsync(Message message, int type)
        {
            var session = SessionManager.Current;
            if (session == null || message == null || message.IsRevoked) return;
            try
            {
                await session.Line.ReactAsync(message.Id, type);
                await session.Store.ApplyReactionAsync(_chatMid, message.Id, session.Mid, type, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                RefreshCachedMessages();
                StatusMessage = "";
            }
            catch (Exception ex) { StatusMessage = "リアクションを送信できませんでした: " + ex.Message; }
        }

        private void DisplayMessages(LineSession session, List<Message> messages, bool scroll = true)
        {
            if (!LumINE.Utils.ForegroundUi.IsReady) return;
            try
            {
                var byId = new Dictionary<string, Message>(StringComparer.Ordinal);
                foreach (var item in messages) if (item != null && !string.IsNullOrEmpty(item.Id)) byId[item.Id] = item;
                foreach (var item in messages)
                {
                    Message replied;
                    if (item != null && !string.IsNullOrEmpty(item.RelatedMessageId) && byId.TryGetValue(item.RelatedMessageId, out replied))
                        item.ReplyPreview = !string.IsNullOrEmpty(replied.Text) ? replied.Text : replied.ContentType == 1 ? "写真" : replied.ContentType == 7 ? "スタンプ" : "メッセージ";
                    else if (item != null && !string.IsNullOrEmpty(item.RelatedMessageId) && string.IsNullOrEmpty(item.ReplyPreview)) item.ReplyPreview = "返信したメッセージ";
                }
                var desired = new List<Message>();
                DateTime? lastDate = null;
                foreach (var msg in messages)
                {
                    msg.IsMine = msg.From == session.Mid || msg.From == "me";
                    var date = GetLocalDate(msg.CreatedTime);
                    if (date.HasValue && (!lastDate.HasValue || date.Value.Date != lastDate.Value.Date))
                    {
                        desired.Add(new Message { IsDateSeparator = true, DateLabel = date.Value.ToString("M月d日 (ddd)", new CultureInfo("ja-JP")) });
                        lastDate = date.Value.Date;
                    }
                    msg.TimeLabel = GetTimeLabel(msg.CreatedTime);
                    msg.ReadLabel = GetReadLabel(msg);
                    if (!msg.IsMine) _ = LoadSenderInfoAsync(session, msg);
                    if (!msg.IsRevoked && msg.MediaImage == null && (msg.ContentType == 1 || msg.ContentType == 7)) _ = LoadMessageImageAsync(session, msg);
                    desired.Add(msg);
                }
                for (int i = 0; i < desired.Count; i++)
                {
                    if (i < Messages.Count && Messages[i].IsDateSeparator && desired[i].IsDateSeparator && Messages[i].DateLabel == desired[i].DateLabel) continue;
                    if (i < Messages.Count && object.ReferenceEquals(Messages[i], desired[i])) continue;
                    int found = -1;
                    if (!desired[i].IsDateSeparator)
                        for (int j = i + 1; j < Messages.Count; j++) if (Messages[j].Id == desired[i].Id) { found = j; break; }
                    if (found >= 0) { Messages.Move(found, i); Messages[i] = desired[i]; }
                    else if (i < Messages.Count && Messages[i].Id == desired[i].Id) Messages[i] = desired[i];
                    else Messages.Insert(i, desired[i]);
                }
                while (Messages.Count > desired.Count) Messages.RemoveAt(Messages.Count - 1);
                var changed = MessagesChanged;
                if (scroll && changed != null) changed();
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[ChatRoom] render messages failed: " + ex.Message); }
        }

        public Task SendPhotoAsync(byte[] bytes, string fileName) { return RunBusyAsync(() => SendPhotoCoreAsync(bytes, fileName)); }

        private async Task SendPhotoCoreAsync(byte[] bytes, string fileName)
        {
            var session = SessionManager.Current;
            if (session == null || bytes == null || bytes.Length == 0) return;
            StatusMessage = "写真を送信中...";
            try
            {
                var before = new HashSet<string>(StringComparer.Ordinal);
                foreach (var message in session.Store.GetCachedMessages(_chatMid))
                    if (message != null && message.Id != null) before.Add(message.Id);
                await MessageMediaCache.SendImageAsync(session, _chatMid, bytes, fileName);
                RefreshCachedMessages();
                StatusMessage = "";
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    await Task.Delay(700);
                    var recent = await session.Line.RefreshRecentMessagesAsync(_chatMid, 30);
                    bool appeared = false;
                    foreach (var message in recent)
                        if (message != null && message.Id != null && !before.Contains(message.Id)) appeared = true;
                    if (appeared) break;
                }
                await LoadMessagesAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[ChatRoom] photo send failed: " + ex.Message);
                StatusMessage = "写真を送信できませんでした: " + ex.Message;
            }
        }

        private async Task LoadMessageImageAsync(LineSession session, Message message)
        {
            try { message.MediaImage = await MessageMediaCache.GetImageAsync(session, message); message.MediaLoadError = null; }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Media] load failed: " + ex); message.MediaLoadError = ex.Message; }
        }

        private async Task LoadSenderInfoAsync(LineSession session, Message message)
        {
            try
            {
                string name = message.From;
                string picture = null;
                ChatStoreSenderInfo cached;
                if (!_senderCache.TryGetValue(message.From ?? "", out cached))
                {
                    var contact = await session.Talk.GetContactAsync(message.From);
                    cached = new ChatStoreSenderInfo
                    {
                        Name = GetField(contact, 27) ?? GetField(contact, 22) ?? message.From,
                        PicturePath = GetField(contact, 37)
                    };
                    _senderCache[message.From ?? ""] = cached;
                }
                name = cached.Name;
                picture = cached.PicturePath;
                message.SenderName = name;
                if (!string.IsNullOrEmpty(picture)) message.SenderAvatar = await AvatarCache.GetAsync(session.AccountId, picture);
            }
            catch { message.SenderName = message.From; }
        }

        private static string GetField(Dictionary<int, object> fields, int id)
        {
            object value;
            return fields != null && fields.TryGetValue(id, out value) && value != null ? value.ToString() : null;
        }

        private static DateTime? GetLocalDate(long? timestamp)
        {
            if (!timestamp.HasValue || timestamp.Value <= 0) return null;
            long value = timestamp.Value < 100000000000L ? timestamp.Value * 1000L : timestamp.Value;
            try { return DateTimeOffset.FromUnixTimeMilliseconds(value).ToLocalTime().DateTime.Date; }
            catch { return null; }
        }

        private static string GetTimeLabel(long? timestamp)
        {
            if (!timestamp.HasValue || timestamp.Value <= 0) return "";
            long value = timestamp.Value < 100000000000L ? timestamp.Value * 1000L : timestamp.Value;
            try { return DateTimeOffset.FromUnixTimeMilliseconds(value).ToLocalTime().ToString("HH:mm"); }
            catch { return ""; }
        }

        private static string GetReadLabel(Message message)
        {
            string read, count;
            if (message.ContentMetadata == null || !message.ContentMetadata.TryGetValue("read", out read) || read != "1") return "";
            return message.ContentMetadata.TryGetValue("readCount", out count) && count != "1" ? "既読" + count : "既読";
        }

        public void RefreshReadLabels()
        {
            foreach (var message in Messages)
                if (!message.IsDateSeparator) message.ReadLabel = GetReadLabel(message);
        }

        private class ChatStoreSenderInfo
        {
            public string Name;
            public string PicturePath;
        }

        public Task SendMessageAsync() { return RunBusyAsync(SendMessageCoreAsync); }

        private async Task SendMessageCoreAsync()
        {
            var text = MessageText;
            if (string.IsNullOrEmpty(text)) return;
            var session = SessionManager.Current;
            if (session == null) return;

            MessageText = "";
            StatusMessage = "送信中...";
            try
            {
                await session.Line.SendMessageAsync(_chatMid, text, _replyTargetId);
                SetReplyTarget(null);
                StatusMessage = "";
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[ChatRoom] send failed: " + ex.Message);
                MessageText = text;
                var rpc = ex as LumINE.Services.Vyline.ThriftRpcException;
                StatusMessage = rpc != null && rpc.Code != 0
                    ? "送信できませんでした (LINE code=" + rpc.Code + ")。"
                    : "送信できませんでした。通信状態を確認してください。";
                return;
            }
            await LoadMessagesAsync();
        }

        public Task MarkAsReadAsync() { return RunBusyAsync(MarkAsReadCoreAsync); }

        private async Task MarkAsReadCoreAsync()
        {
            var session = SessionManager.Current;
            if (session == null) return;
            try
            {
                await session.Line.MarkAsReadAsync(_chatMid);
                await session.Read.RefreshReadReceiptsAsync(_chatMid, true);
                foreach (var message in Messages)
                    if (!message.IsDateSeparator) message.ReadLabel = GetReadLabel(message);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[ChatRoom] markAsRead failed: " + ex.Message);
            }
        }
    }
}
