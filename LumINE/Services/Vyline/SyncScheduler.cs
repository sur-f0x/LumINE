using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LumINE.Models;

namespace LumINE.Services.Vyline
{
    /// <summary>
    /// Frontend sync timers (vyline useVylineSync):
    /// events poll 2s focused / 8s unfocused / 60s hidden,
    /// chats poll 120s + 0-25s rand, read receipts 5s.
    /// UWP: "hidden" ≈ app not activated; "unfocused" ≈ no core window focus.
    /// </summary>
    public class SyncScheduler : IDisposable
    {
        public const int EventsFocusedMs = 2000;
        public const int EventsUnfocusedMs = 8000;
        public const int EventsHiddenMs = 60000;
        public const int ChatsPollMs = 120000;
        public const int ChatsJitterMs = 25000;
        public const int ReadPollMs = 5000;

        private readonly LineSession _session;
        private readonly ChatStore _store;
        private readonly ReadEngine _readEngine;
        private readonly object _timerGate = new object();
        private int _pollInflight;
        private long _eventCursor;
        private Timer _eventsTimer;
        private Timer _chatsTimer;
        private Timer _readTimer;
        private string _activeChatId;
        private volatile bool _disposed;
        private bool _started;
        private Windows.UI.Core.CoreWindow _window;
        private Windows.UI.Core.CoreDispatcher _dispatcher;
        private volatile bool _windowHidden;
        private volatile bool _windowDeactivated;

        public event Action<IReadOnlyList<TalkPollEvent>> EventsReceived;
        public event Action ChatsUpdated;

        public SyncScheduler(LineSession session, ChatStore store, ReadEngine readEngine)
        {
            _session = session;
            _store = store;
            _readEngine = readEngine;
            _eventCursor = 0;
        }

        public string ActiveChatId
        {
            get { return _activeChatId; }
            set { _activeChatId = value; }
        }

        public void Start()
        {
            lock (_timerGate)
            {
                if (_disposed || _started) return;
                _started = true;
                AttachWindow();
                // Initial poll immediately
                Task.Run(() => PollIncomingAsync());
                ScheduleEvents();
                ScheduleChats();
                _readTimer = new Timer(_ =>
                {
                    if (_disposed) return;
                    var chat = _activeChatId;
                    if (BackgroundNotifications.IsForeground && !_windowHidden && !string.IsNullOrEmpty(chat))
                        Task.Run(async () =>
                        {
                            try { await _readEngine.RefreshReadReceiptsAsync(chat, false); }
                            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Sync] read refresh failed: " + ex.Message); }
                        });
                }, null, ReadPollMs, ReadPollMs);
            }
        }

        /// <summary>
        /// Register CoreWindow focus/visibility handlers (must run on UI thread).
        /// Fallback when not on UI thread: state stays "focused".
        /// </summary>
        private void AttachWindow()
        {
            try
            {
                var w = Windows.UI.Core.CoreWindow.GetForCurrentThread();
                if (w == null) return;
                _window = w;
                _dispatcher = w.Dispatcher;
                w.VisibilityChanged += OnWindowVisibilityChanged;
                w.Activated += OnWindowActivated;
                _windowHidden = !w.Visible;
                _windowDeactivated = false;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Sync] AttachWindow failed: " + ex.Message);
            }
        }

        private void OnWindowVisibilityChanged(Windows.UI.Core.CoreWindow sender, Windows.UI.Core.VisibilityChangedEventArgs args)
        {
            _windowHidden = !args.Visible;
        }

        private void OnWindowActivated(Windows.UI.Core.CoreWindow sender, Windows.UI.Core.WindowActivatedEventArgs args)
        {
            _windowDeactivated = args.WindowActivationState == Windows.UI.Core.CoreWindowActivationState.Deactivated;
        }

        private int CurrentIntervalMs()
        {
            if (_windowHidden) return EventsHiddenMs;
            if (_windowDeactivated) return EventsUnfocusedMs;
            return EventsFocusedMs;
        }

        private void ScheduleEvents()
        {
            lock (_timerGate)
            {
                if (_disposed) return;
                int ms = CurrentIntervalMs();
                if (_eventsTimer == null)
                    _eventsTimer = new Timer(OnEventsTick, null, ms, Timeout.Infinite);
                else
                    _eventsTimer.Change(ms, Timeout.Infinite);
            }
        }

        private void OnEventsTick(object state)
        {
            if (_disposed) return;
            ScheduleEvents();
            if (BackgroundNotifications.IsForeground && !_windowHidden)
                Task.Run(() => PollIncomingAsync());
        }

        private void ScheduleChats()
        {
            lock (_timerGate)
            {
                if (_disposed) return;
                var rnd = new Random();
                int ms = ChatsPollMs + rnd.Next(0, ChatsJitterMs + 1);
                if (_chatsTimer == null)
                    _chatsTimer = new Timer(OnChatsTick, null, ms, Timeout.Infinite);
                else
                    _chatsTimer.Change(ms, Timeout.Infinite);
            }
        }

        private void OnChatsTick(object state)
        {
            if (_disposed) return;
            ScheduleChats();
            if (_windowHidden || !BackgroundNotifications.IsForeground) return;
            Task.Run(async () =>
            {
                try
                {
                    await _store.GetChatsAsync(false, false).ConfigureAwait(false);
                    var h = ChatsUpdated;
                    if (h != null) h();
                }
                catch { }
            });
        }

        /// <summary>Single-flight pollIncoming (vyline pollIncomingInflight).</summary>
        public async Task PollIncomingAsync()
        {
            if (_disposed || !LumINE.Utils.ForegroundUi.IsReady) return;
            if (Interlocked.Exchange(ref _pollInflight, 1) == 1) return;
            try
            {
                var buf = _session.Events;
                var batch = buf.Drain(_eventCursor);
                if (batch.Reset)
                {
                    _eventCursor = batch.Cursor;
                    try { await _store.GetChatsAsync(false, true).ConfigureAwait(false); }
                    catch { }
                    var chat = _activeChatId;
                    if (!string.IsNullOrEmpty(chat))
                    {
                        try { await RefreshActiveChatAsync(chat).ConfigureAwait(false); }
                        catch { }
                    }
                    return;
                }

                if (batch.Events.Count > 0)
                {
                    _eventCursor = batch.Cursor;
                    await ApplyEventsAsync(batch.Events).ConfigureAwait(false);
                    await _store.SaveChatActivityAsync().ConfigureAwait(false);
                    var handler = EventsReceived;
                    if (handler != null) handler(batch.Events);
                }
                else
                {
                    _eventCursor = batch.Cursor;
                }

                // delta catch-up for active chat (vyline always calls pollMessagesDelta)
                var active = _activeChatId;
                if (!string.IsNullOrEmpty(active))
                {
                    try { await RefreshActiveChatAsync(active).ConfigureAwait(false); }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Sync] pollIncoming failed: " + ex.Message);
            }
            finally
            {
                Interlocked.Exchange(ref _pollInflight, 0);
            }
        }

        private async Task RefreshActiveChatAsync(string chatMid)
        {
            var before = _store.GetCachedMessages(chatMid);
            var knownIds = new HashSet<string>(StringComparer.Ordinal);
            var reactions = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var item in before) if (item != null && item.Id != null) knownIds.Add(item.Id);
            foreach (var item in before) if (item != null && item.Id != null) reactions[item.Id] = ReactionSignature(item);
            var fetched = await _session.Line.RefreshRecentMessagesAsync(chatMid, 30).ConfigureAwait(false);
            if (fetched.Count == 0) return;
            string newMessageId = null;
            foreach (var message in fetched)
                if (message != null && message.Id != null && !knownIds.Contains(message.Id)) newMessageId = message.Id;
            if (!string.IsNullOrEmpty(newMessageId))
            {
                var handler = EventsReceived;
                if (handler != null)
                    handler(new[] { new TalkPollEvent { Kind = TalkEventKind.Message, ChatMid = chatMid, MessageId = newMessageId } });
            }
            else if (fetched.Any(message => message.Id != null && message.ReactionsKnown && reactions.ContainsKey(message.Id) && reactions[message.Id] != ReactionSignature(message)))
            {
                var handler = EventsReceived;
                if (handler != null) handler(fetched.Where(message => message.Id != null && message.ReactionsKnown && reactions.ContainsKey(message.Id) && reactions[message.Id] != ReactionSignature(message))
                    .Select(message => new TalkPollEvent { Kind = TalkEventKind.Reaction, ChatMid = chatMid, MessageId = message.Id }).ToArray());
            }
        }

        private static string ReactionSignature(Message message)
        {
            return string.Join("|", message.Reactions.OrderBy(r => r.FromMid, StringComparer.Ordinal).Select(r => r.FromMid + ":" + r.Type));
        }

        private async Task ApplyEventsAsync(List<TalkPollEvent> events)
        {
            foreach (var ev in events)
            {
                switch (ev.Kind)
                {
                    case TalkEventKind.Message:
                        if (ev.RawMessage != null)
                        {
                            var msg = ChatStore.MapMessage(ev.RawMessage, ev.ChatMid);
                            if (msg != null)
                            {
                                msg.Id = ev.MessageId ?? msg.Id;
                                await _session.Line.DecryptMessagesAsync(new List<Message> { msg }).ConfigureAwait(false);
                                _store.UpsertMessage(ev.ChatMid, msg);
                                await BackgroundNotifications.NotifyAsync(_session, msg).ConfigureAwait(false);
                                await _store.SaveMessagesAsync(ev.ChatMid).ConfigureAwait(false);
                                if (msg.From != _session.Mid && ev.ChatMid != _activeChatId)
                                    await _store.IncrementUnreadAsync(ev.ChatMid).ConfigureAwait(false);
                            }
                        }
                        break;
                    case TalkEventKind.Revoke:
                        await _store.ApplyRemoteRevokeAsync(ev.ChatMid, ev.MessageId).ConfigureAwait(false);
                        break;
                    case TalkEventKind.Read:
                        // own unread → 0; refresh receipts
                        var chat = _activeChatId;
                        if (chat == ev.ChatMid)
                        {
                            try
                            {
                                await _readEngine.RefreshReadReceiptsAsync(ev.ChatMid, true).ConfigureAwait(false);
                            }
                            catch { }
                        }
                        try { await _store.GetChatsAsync(false, true).ConfigureAwait(false); }
                        catch { }
                        break;
                    case TalkEventKind.Reaction:
                        if (_activeChatId == ev.ChatMid)
                        {
                            try
                            {
                                if (!ev.ReactionType.HasValue || string.IsNullOrEmpty(ev.ReactionActor))
                                    await _store.RefreshRecentMessagesAsync(ev.ChatMid, 100).ConfigureAwait(false);
                            }
                            catch { }
                        }
                        break;
                    default:
                        // membership / chat:update / announce / calls → refresh chats
                        try { await _store.GetChatsAsync(false, true).ConfigureAwait(false); }
                        catch { }
                        break;
                }
            }
        }

        public void Dispose()
        {
            lock (_timerGate)
            {
                if (_disposed) return;
                _disposed = true;
                if (_eventsTimer != null) _eventsTimer.Dispose();
                if (_chatsTimer != null) _chatsTimer.Dispose();
                if (_readTimer != null) _readTimer.Dispose();
            }
            DetachWindow();
        }

        private async void DetachWindow()
        {
            try
            {
                var w = _window;
                var dispatcher = _dispatcher;
                _window = null;
                _dispatcher = null;
                if (w != null)
                {
                    await LumINE.Utils.UiDispatcher.RunAsync(dispatcher, () =>
                    {
                        w.VisibilityChanged -= OnWindowVisibilityChanged;
                        w.Activated -= OnWindowActivated;
                        return Task.CompletedTask;
                    });
                }
            }
            catch (Exception ex) { LumINE.Utils.CrashDiagnostics.Record("Sync.DetachWindow", ex); }
        }
    }
}
