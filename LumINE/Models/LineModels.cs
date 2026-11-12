using System;
using System.Collections.Generic;
using System.Text;

namespace LumINE.Models
{
    public sealed class MessageReaction
    {
        public string FromMid { get; set; }
        public long AtMillis { get; set; }
        public int Type { get; set; }
        public static string Label(int type)
        {
            switch (type) { case 2: return "👍"; case 3: return "😍"; case 4: return "😆"; case 5: return "🙏"; case 6: return "😢"; case 7: return "😯"; default: return "?"; }
        }
    }
    public class Profile
    {
        public string Mid { get; set; }
        public string DisplayName { get; set; }
        public string StatusMessage { get; set; }
        public string PicturePath { get; set; }
        public string UserId { get; set; }
    }

    public class Contact
    {
        public string Mid { get; set; }
        public string DisplayName { get; set; }
        public string DisplayNameOverride { get; set; }
        public string PicturePath { get; set; }
        public string Name { get { return DisplayNameOverride ?? DisplayName ?? Mid; } }
    }

    public class Chat
    {
        public string ChatMid { get; set; }
        public int? ChatType { get; set; }
        public int? MemberCount { get; set; }
        public string ChatName { get; set; }
        public string PicturePath { get; set; }
        public bool NotificationDisabled { get; set; }
    }

    public class Message : System.ComponentModel.INotifyPropertyChanged
    {
        public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;
        private void Notify(string name)
        {
            var handler = PropertyChanged;
            if (handler == null) return;
            int generation = LumINE.Utils.ForegroundUi.Generation;
            if (!LumINE.Utils.ForegroundUi.IsCurrent(generation)) return;
            try
            {
                var dispatcher = Windows.ApplicationModel.Core.CoreApplication.MainView.Dispatcher;
                if (!dispatcher.HasThreadAccess)
                {
                    var pending = dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal,
                        () =>
                        {
                            if (!LumINE.Utils.ForegroundUi.IsCurrent(generation)) return;
                            try { handler(this, new System.ComponentModel.PropertyChangedEventArgs(name)); }
                            catch (Exception ex) { LumINE.Utils.CrashDiagnostics.Record("Message.Binding", ex); }
                        });
                    return;
                }
                handler(this, new System.ComponentModel.PropertyChangedEventArgs(name));
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Message] binding notification unavailable: " + ex.Message); }
        }
        private Windows.UI.Xaml.Media.ImageSource _mediaImage;
        private Windows.UI.Xaml.Media.ImageSource _senderAvatar;
        private string _senderName;
        private string _timeLabel;
        private string _dateLabel;
        private string _readLabel;
        private bool _isMine;
        private bool _isDateSeparator;
        private string _replyPreview;
        private string _mediaLoadError;
        private List<MessageReaction> _reactions = new List<MessageReaction>();
        public bool ReactionsKnown { get; set; }
        public List<MessageReaction> Reactions { get { return _reactions; } set { _reactions = value ?? new List<MessageReaction>(); Notify("Reactions"); Notify("ReactionSummary"); Notify("HasReactions"); } }
        public bool HasReactions { get { return !IsRevoked && Reactions.Count > 0; } }
        public string ReactionSummary
        {
            get
            {
                if (IsRevoked) return "";
                var counts = new SortedDictionary<int, int>();
                foreach (var reaction in Reactions) { int count; counts.TryGetValue(reaction.Type, out count); counts[reaction.Type] = count + 1; }
                var labels = new List<string>();
                foreach (var pair in counts) labels.Add(MessageReaction.Label(pair.Key) + " " + pair.Value);
                return string.Join("  ", labels);
            }
        }
        public string Id { get; set; }
        public string From { get; set; }
        public string To { get; set; }
        public string Text { get; set; }
        public int ContentType { get; set; }
        public Dictionary<string, string> ContentMetadata { get; set; }
        public long? CreatedTime { get; set; }
        public List<byte[]> Chunks { get; set; }
        public string MediaKeyMaterial { get; set; }
        public string MediaLoadError { get { return _mediaLoadError; } set { _mediaLoadError = value; Notify("MediaLoadError"); Notify("MediaFallbackText"); } }
        public string RelatedMessageId { get; set; }
        public int? RelationType { get; set; }
        public string ReplyPreview { get { return _replyPreview; } set { _replyPreview = value; Notify("ReplyPreview"); Notify("HasReply"); } }
        public bool HasReply { get { return !string.IsNullOrEmpty(ReplyPreview); } }
        public void RefreshState()
        {
            Notify(""); Notify("DisplayText"); Notify("IsText"); Notify("HasMediaImage"); Notify("ShowMediaFallback"); Notify("CanUnsend"); Notify("BodyFontSize"); Notify("HasReactions"); Notify("ReactionSummary");
        }
        public bool IsMine { get { return _isMine; } set { _isMine = value; Notify("IsMine"); Notify("ShowPeerInfo"); Notify("ShowReadLabel"); } }
        public bool IsDateSeparator { get { return _isDateSeparator; } set { _isDateSeparator = value; Notify("IsDateSeparator"); Notify("IsText"); Notify("IsImage"); Notify("IsSticker"); Notify("ShowPeerInfo"); } }
        public string DateLabel { get { return _dateLabel; } set { _dateLabel = value; Notify("DateLabel"); } }
        public string TimeLabel { get { return _timeLabel; } set { _timeLabel = value; Notify("TimeLabel"); } }
        public string ReadLabel { get { return _readLabel; } set { _readLabel = value; Notify("ReadLabel"); Notify("ShowReadLabel"); } }
        public string SenderName { get { return _senderName; } set { _senderName = value; Notify("SenderName"); } }
        public Windows.UI.Xaml.Media.ImageSource SenderAvatar { get { return _senderAvatar; } set { _senderAvatar = value; Notify("SenderAvatar"); } }
        public Windows.UI.Xaml.Media.ImageSource MediaImage { get { return _mediaImage; } set { _mediaImage = value; Notify("MediaImage"); Notify("HasMediaImage"); Notify("ShowMediaFallback"); } }
        public bool IsImage { get { return ContentType == 1 && !IsDateSeparator; } }
        public bool IsSticker { get { return ContentType == 7 && !IsDateSeparator; } }
        public bool IsRevoked
        {
            get
            {
                string state;
                if (ContentMetadata != null)
                {
                    if (ContentMetadata.TryGetValue("messageState", out state) && state != null && state.StartsWith("revoked", StringComparison.OrdinalIgnoreCase)) return true;
                    foreach (var key in new[] { "UNSENT", "UNSEND", "UNSENT_MESSAGE", "SILENTLY_UNSENT" })
                        if (ContentMetadata.TryGetValue(key, out state) && !string.IsNullOrEmpty(state) && state != "0" && !state.Equals("false", StringComparison.OrdinalIgnoreCase)) return true;
                    if (ContentMetadata.TryGetValue("REPLACE", out state) && state != null &&
                        (state.IndexOf("UNSEND", StringComparison.OrdinalIgnoreCase) >= 0 || state.IndexOf("UNSENT", StringComparison.OrdinalIgnoreCase) >= 0)) return true;
                }
                // Match vyline's empty, unencrypted NONE tombstone handling.
                // Retain encrypted empty bodies as encrypted messages, never as unsent.
                return !IsDateSeparator && ContentType == 0 && string.IsNullOrEmpty(Text) &&
                    (Chunks == null || Chunks.Count == 0) && (ContentMetadata == null ||
                    (!ContentMetadata.ContainsKey("STKID") && !ContentMetadata.ContainsKey("OID") &&
                     !ContentMetadata.ContainsKey("SID") && !ContentMetadata.ContainsKey("DOWNLOAD_URL")));
            }
        }
        public string DisplayText { get { return IsRevoked ? "メッセージの送信を取り消しました" : Text; } }
        public bool IsText { get { return (ContentType == 0 || IsRevoked) && !IsDateSeparator; } }
        public bool IsMessageItem { get { return !IsDateSeparator; } }
        public bool HasMediaImage { get { return MediaImage != null && !IsRevoked; } }
        public double BodyFontSize { get { return IsRevoked ? 10 : 15; } }
        public double MediaMaxWidth { get { return IsSticker ? Math.Max(100, Windows.UI.Xaml.Window.Current.Bounds.Width * 0.5) : 230; } }
        public bool CanUnsend { get { long t = CreatedTime ?? 0; if (t > 0 && t < 100000000000L) t *= 1000; long age = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - t; return IsMine && !IsRevoked && t > 0 && age >= 0 && age < 3600000; } }
        public bool ShowMediaFallback { get { return IsMessageItem && !IsRevoked && ContentType != 0 && MediaImage == null; } }
        public string MediaFallbackText
        {
            get
            {
                if (!string.IsNullOrEmpty(MediaLoadError)) return "画像を読み込めませんでした: " + MediaLoadError;
                switch (ContentType)
                {
                    case 1: return "写真";
                    case 2: return "動画";
                    case 3: return "音声";
                    case 7: return "スタンプ";
                    case 14: return "ファイル";
                    default: return "メッセージ";
                }
            }
        }
        public bool ShowPeerInfo { get { return !IsMine && !IsDateSeparator; } }
        public bool ShowReadLabel { get { return IsMine && !string.IsNullOrEmpty(ReadLabel); } }
    }

    public class ChatSummary : System.ComponentModel.INotifyPropertyChanged
    {
        private string _chatMid;
        private int? _chatType;
        private string _name;
        private Message _lastMessage;
        private string _senderName;
        private string _picturePath;
        private Windows.UI.Xaml.Media.ImageSource _avatar;
        private bool _isFriend;
        private int _unreadCount;

        public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;

        private void Notify(string name)
        {
            var h = PropertyChanged;
            if (h != null) h(this, new System.ComponentModel.PropertyChangedEventArgs(name));
        }

        public string ChatMid
        {
            get { return _chatMid; }
            set { _chatMid = value; Notify("ChatMid"); }
        }

        public int? ChatType
        {
            get { return _chatType; }
            set { _chatType = value; Notify("ChatType"); }
        }

        public string Name
        {
            get { return _name; }
            set { _name = value; Notify("Name"); }
        }

        public Message LastMessage
        {
            get { return _lastMessage; }
            set { _lastMessage = value; Notify("LastMessage"); Notify("LastMessagePreview"); Notify("LastMessageTimeLabel"); }
        }

        public string LastMessagePreview
        {
            get
            {
                if (_lastMessage == null) return "";
                if (_lastMessage.IsRevoked) return "メッセージの送信を取り消しました";
                if (_lastMessage.ContentType == 0 && string.IsNullOrEmpty(_lastMessage.Text) && _lastMessage.Chunks != null && _lastMessage.Chunks.Count > 0) return "暗号化されたメッセージ";
                if (!string.IsNullOrEmpty(_lastMessage.Text)) return _lastMessage.Text;
                switch (_lastMessage.ContentType)
                {
                    case 1: return "写真";
                    case 2: return "動画";
                    case 3: return "音声";
                    case 7: return "スタンプ";
                    case 14: return "ファイル";
                    default: return "";
                }
            }
        }

        public long LastActivityTime
        {
            get { long time = LastMessage == null ? 0 : LastMessage.CreatedTime ?? 0; return time > 0 && time < 100000000000L ? time * 1000L : time; }
        }

        public string LastMessageTimeLabel
        {
            get
            {
                if (_lastMessage == null || !_lastMessage.CreatedTime.HasValue) return "";
                long timestamp = _lastMessage.CreatedTime.Value;
                if (timestamp < 100000000000L) timestamp *= 1000L;
                var date = DateTimeOffset.FromUnixTimeMilliseconds(timestamp).ToLocalTime();
                var now = DateTime.Now;
                if (date.Date == now.Date) return date.ToString("HH:mm");
                if (date.Date == now.Date.AddDays(-1)) return "昨日";
                return date.ToString("M月d日");
            }
        }

        public string SenderName
        {
            get { return _senderName; }
            set { _senderName = value; Notify("SenderName"); }
        }

        public string PicturePath
        {
            get { return _picturePath; }
            set { _picturePath = value; Notify("PicturePath"); }
        }

        public Windows.UI.Xaml.Media.ImageSource Avatar
        {
            get { return _avatar; }
            set { _avatar = value; Notify("Avatar"); }
        }

        public bool IsFriend
        {
            get { return _isFriend; }
            set { _isFriend = value; Notify("IsFriend"); }
        }

        public int UnreadCount { get { return _unreadCount; } set { _unreadCount = value; Notify("UnreadCount"); Notify("UnreadBadge"); Notify("HasUnread"); } }
        public string UnreadBadge { get { return _unreadCount > 99 ? "99+" : _unreadCount.ToString(); } }
        public bool HasUnread { get { return _unreadCount > 0; } }
    }
}
