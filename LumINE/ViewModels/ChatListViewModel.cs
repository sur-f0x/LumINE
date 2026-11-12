using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Linq;
using System.Collections.Generic;
using LumINE.Models;
using LumINE.Services.Vyline;

namespace LumINE.ViewModels
{
    public class ChatListViewModel : BaseViewModel
    {
        public ObservableCollection<ChatSummary> FriendChats { get; } = new ObservableCollection<ChatSummary>();
        public ObservableCollection<ChatSummary> GroupChats { get; } = new ObservableCollection<ChatSummary>();
        public ObservableCollection<ChatSummary> Chats { get { return _showFriends ? FriendChats : GroupChats; } }
        private string _searchQuery = "";
        public string SearchQuery { get { return _searchQuery; } set { _searchQuery = value ?? ""; OnPropertyChanged("SearchQuery"); RefreshVisibleChats(); } }
        public string SearchSummary { get { return "友だち " + FriendChats.Count + "件・グループ " + GroupChats.Count + "件"; } }
        private readonly System.Collections.Generic.List<ChatSummary> _friends = new System.Collections.Generic.List<ChatSummary>();
        private readonly System.Collections.Generic.List<ChatSummary> _groups = new System.Collections.Generic.List<ChatSummary>();
        private bool _showFriends = true;
        private string _statusMessage;
        private int _loadGeneration;
        public event Action LatestMessagesUpdated;

        public bool ShowFriends { get { return _showFriends; } }

        public string StatusMessage
        {
            get { return _statusMessage; }
            set { _statusMessage = value; OnPropertyChanged("StatusMessage"); }
        }

        /// <summary>Must be called on the UI thread.</summary>
        public Task LoadChatsAsync() { return RunBusyAsync(LoadChatsCoreAsync); }

        private async Task LoadChatsCoreAsync()
        {
            var session = SessionManager.Current;
            if (session == null)
            {
                StatusMessage = "ログインセッションがありません。アプリを再起動してログインしてください。";
                return;
            }
            try
            {
                int generation = ++_loadGeneration;
                int foreground = LumINE.Utils.ForegroundUi.Generation;
                await session.Store.LoadChatActivityAsync();
                // vyline: local-first then refresh (refresh=true races 3s, falls back local)
                var chats = await session.Line.FetchChatsAsync(false, true);
                if (generation != _loadGeneration || !LumINE.Utils.ForegroundUi.IsCurrent(foreground)) return;
                var existing = _friends.Concat(_groups).ToDictionary(item => item.ChatMid, StringComparer.Ordinal);
                _friends.Clear();
                _groups.Clear();
                foreach (var chat in chats)
                {
                    ChatSummary summary;
                    if (!existing.TryGetValue(chat.ChatMid, out summary)) summary = new ChatSummary();
                    summary.ChatMid = chat.ChatMid; summary.ChatType = chat.ChatType;
                    summary.Name = string.IsNullOrEmpty(chat.ChatName) ? chat.ChatMid : chat.ChatName;
                    summary.PicturePath = chat.PicturePath;
                    summary.LastMessage = session.Store.GetLastMessage(chat.ChatMid);
                    summary.IsFriend = chat.ChatType == 2;
                    summary.UnreadCount = session.Store.GetUnreadCount(chat.ChatMid);
                    if (summary.IsFriend) _friends.Add(summary);
                    else _groups.Add(summary);
                    if (!string.IsNullOrEmpty(chat.PicturePath))
                    {
                        var ignored = LoadAvatarAsync(session.AccountId, chat.PicturePath, summary);
                    }
                }
                RefreshVisibleChats();
                _ = RunBusyAsync(() => RefreshLatestAsync(session, generation));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[ChatList] load failed: " + ex.Message);
                StatusMessage = "トークを取得できませんでした。通信状態を確認してください。";
            }
        }

        private async Task RefreshLatestAsync(LineSession session, int generation)
        {
            int foreground = LumINE.Utils.ForegroundUi.Generation;
            try
            {
                await session.Store.RefreshChatActivityAsync();
                if (generation != _loadGeneration || !LumINE.Utils.ForegroundUi.IsCurrent(foreground) || !object.ReferenceEquals(session, SessionManager.Current)) return;
                foreach (var item in _friends.Concat(_groups))
                {
                    item.LastMessage = session.Store.GetLastMessage(item.ChatMid);
                    item.UnreadCount = session.Store.GetUnreadCount(item.ChatMid);
                }
                RefreshVisibleChats();
                LatestMessagesUpdated?.Invoke();
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[ChatList] latest refresh: " + ex.Message); }
        }

        public void SelectFriends()
        {
            _showFriends = true;
            OnPropertyChanged("ShowFriends");
            OnPropertyChanged("Chats");
            RefreshVisibleChats();
        }

        public void SelectGroups()
        {
            _showFriends = false;
            OnPropertyChanged("ShowFriends");
            OnPropertyChanged("Chats");
            RefreshVisibleChats();
        }

        private void RefreshVisibleChats()
        {
            Reconcile(FriendChats, _friends);
            Reconcile(GroupChats, _groups);
            OnPropertyChanged("SearchSummary");
            StatusMessage = Chats.Count == 0 ? (!string.IsNullOrWhiteSpace(_searchQuery) ? "該当するトークがありません。" : _showFriends ? "友だちがいません。" : "グループがありません。") : "";
        }

        private void Reconcile(ObservableCollection<ChatSummary> target, List<ChatSummary> source)
        {
            string query = _searchQuery.Trim();
            var ordered = source.Where(item => query.Length == 0 || (item.Name ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderByDescending(item => item.LastActivityTime)
                .ThenByDescending(item => item.LastMessage == null ? "" : item.LastMessage.Id, StringComparer.Ordinal)
                .ThenBy(item => item.Name, StringComparer.CurrentCulture).ToList();
            for (int i = 0; i < ordered.Count; i++)
            {
                int index = target.IndexOf(ordered[i]);
                if (index < 0) target.Insert(i, ordered[i]); else if (index != i) target.Move(index, i);
            }
            while (target.Count > ordered.Count) target.RemoveAt(target.Count - 1);
        }

        private async Task LoadAvatarAsync(string accountId, string picturePath, ChatSummary summary)
        {
            try { summary.Avatar = await AvatarCache.GetAsync(accountId, picturePath); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Avatar] load failed: " + ex.Message); }
        }

        /// <summary>
        /// Apply drained talk events to the visible list (UI thread).
        /// Message events update the last-message preview in place;
        /// anything else triggers a full refresh.
        /// </summary>
        public void ApplyEvents(System.Collections.Generic.IReadOnlyList<TalkPollEvent> events)
        {
            if (events == null || events.Count == 0) return;
            var session = SessionManager.Current;
            if (session == null) return;

            bool needReload = false;
            bool receivedMessage = false;
            foreach (var ev in events)
            {
                if (ev.Kind == TalkEventKind.Message) receivedMessage = true;
                if (string.IsNullOrEmpty(ev.ChatMid) || (ev.Kind != TalkEventKind.Message && ev.Kind != TalkEventKind.Revoke && ev.Kind != TalkEventKind.Read && ev.Kind != TalkEventKind.Reaction))
                {
                    needReload = true;
                    continue;
                }

                var item = _friends.Concat(_groups).FirstOrDefault(chat => chat.ChatMid == ev.ChatMid);
                if (item == null) { needReload = true; continue; }
                item.LastMessage = session.Store.GetLastMessage(item.ChatMid);
                item.UnreadCount = session.Store.GetUnreadCount(item.ChatMid);
            }

            if (needReload)
            {
                var _ = LoadChatsAsync();
            }
            else
            {
                RefreshVisibleChats();
                if (receivedMessage) LatestMessagesUpdated?.Invoke();
            }
        }
    }
}
