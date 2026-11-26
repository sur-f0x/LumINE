using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using System.Runtime.InteropServices.WindowsRuntime;
using LumINE.Models;
using LumINE.Services.Vyline;
using LumINE.ViewModels;

namespace LumINE.Views
{
    public sealed partial class ChatRoomPage : Page
    {
        public ChatRoomViewModel ViewModel { get; set; }
        private string _chatMid;
        private bool _subscribed;
        private bool _stickerPickerOpen;
        private bool _searchOpen;
        private bool _searchPinned;
        private bool _reactionOpen;
        private bool _openingPhoto;
        private bool _active;
        private bool _followLatest;
        private bool _bottomScrollQueued;
        private ScrollViewer _messagesScroll;
        private object _bottomScrollTarget;

        public ChatRoomPage()
        {
            this.InitializeComponent();
            MessagesList.Loaded += (s, e) => QueueBottomScroll();
            MessagesList.SizeChanged += (s, e) => QueueBottomScroll();
            MessagesList.LayoutUpdated += (s, e) => QueueBottomScroll();
            // Respect touch scrolling, scrollbar interaction, wheel scrolling
            // and text selection even if a child already handled the pointer.
            MessagesList.AddHandler(UIElement.PointerPressedEvent, new Windows.UI.Xaml.Input.PointerEventHandler((s, e) => _followLatest = false), true);
            MessagesList.AddHandler(UIElement.PointerWheelChangedEvent, new Windows.UI.Xaml.Input.PointerEventHandler((s, e) => _followLatest = false), true);
            MessagesList.AddHandler(UIElement.KeyDownEvent, new Windows.UI.Xaml.Input.KeyEventHandler((s, e) =>
            {
                if (e.Key == Windows.System.VirtualKey.End) ScrollToLatest();
                else if (e.Key == Windows.System.VirtualKey.Up || e.Key == Windows.System.VirtualKey.Down ||
                         e.Key == Windows.System.VirtualKey.PageUp || e.Key == Windows.System.VirtualKey.PageDown ||
                         e.Key == Windows.System.VirtualKey.Home || e.Key == Windows.System.VirtualKey.Space) _followLatest = false;
            }), true);
        }

        private void MessagesList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (!args.InRecycleQueue && ViewModel != null) ViewModel.PrepareMedia(args.Item as Message);
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            _active = true;
            LumINE.Utils.ForegroundUi.Ready += OnForegroundReady;
            SystemNavigationManager.GetForCurrentView().BackRequested += OnSystemBackRequested;
            SystemNavigationManager.GetForCurrentView().AppViewBackButtonVisibility =
                Frame.CanGoBack ? AppViewBackButtonVisibility.Visible : AppViewBackButtonVisibility.Collapsed;
            if (e.Parameter is string chatMid)
            {
                _chatMid = chatMid;
                _searchPinned = false;
                _openingPhoto = false;
                ViewModel = new ChatRoomViewModel(chatMid);
                ViewModel.MessagesChanged += ScrollToLatest;
                this.DataContext = ViewModel;

                var session = SessionManager.Current;
                if (session != null)
                {
                    session.EnsureSyncStarted();
                    session.Sync.ActiveChatId = chatMid;
                    session.Read.ReadReceiptsUpdated += OnReadReceiptsUpdated;
                    if (!_subscribed)
                    {
                        _subscribed = true;
                        session.Sync.EventsReceived += OnEventsReceived;
                    }
                    ChatNameText.Text = ResolveChatName(session, chatMid);
                    _ = ViewModel.RunBusyAsync(() => UpdateGroupHeaderAsync(session, chatMid));
                }

                await ViewModel.LoadMessagesAsync();
                ScrollToLatest();
                await ViewModel.MarkAsReadAsync();
                _ = ViewModel.LoadOlderHistoryAsync();
            }
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            _active = false;
            StickerSoundPlayer.Stop();
            _followLatest = false;
            _bottomScrollTarget = null;
            LumINE.Utils.ForegroundUi.Ready -= OnForegroundReady;
            if (ViewModel != null) ViewModel.MessagesChanged -= ScrollToLatest;
            SystemNavigationManager.GetForCurrentView().BackRequested -= OnSystemBackRequested;
            var session = SessionManager.Current;
            if (session != null)
            {
                session.Read.ReadReceiptsUpdated -= OnReadReceiptsUpdated;
                if (string.Equals(session.Sync.ActiveChatId, _chatMid))
                    session.Sync.ActiveChatId = null;
                if (_subscribed)
                {
                    _subscribed = false;
                    session.Sync.EventsReceived -= OnEventsReceived;
                }
            }
        }

        private async void OnReadReceiptsUpdated(string chatMid)
        {
            int generation = LumINE.Utils.ForegroundUi.Generation;
            if (!LumINE.Utils.ForegroundUi.IsCurrent(generation)) return;
            if (!string.Equals(chatMid, _chatMid, StringComparison.Ordinal)) return;
            try
            {
                await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    if (_active && LumINE.Utils.ForegroundUi.IsCurrent(generation) && ViewModel != null) ViewModel.RefreshReadLabels();
                });
            }
            catch { }
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            if (Frame != null && Frame.CanGoBack) Frame.GoBack();
            else if (Frame != null && Frame.Tag is ChatListPage) ((ChatListPage)Frame.Tag).ShowChatList();
        }

        private void OnSystemBackRequested(object sender, BackRequestedEventArgs e)
        {
            if (e.Handled) return;
            if (Frame != null && Frame.CanGoBack)
            {
                e.Handled = true;
                Frame.GoBack();
            }
        }

        private static string ResolveChatName(LineSession session, string chatMid)
        {
            try
            {
                var chats = session.Store.GetCachedChats();
                foreach (var c in chats)
                {
                    if (c.ChatMid == chatMid && !string.IsNullOrEmpty(c.ChatName))
                        return c.ChatName;
                }
            }
            catch { }
            return chatMid;
        }

        private async Task UpdateGroupHeaderAsync(LineSession session, string chatMid)
        {
            if (session == null || string.IsNullOrEmpty(chatMid) ||
                !(chatMid.StartsWith("c", StringComparison.Ordinal) || chatMid.StartsWith("r", StringComparison.Ordinal))) return;
            try
            {
                var details = await session.Talk.GetChatsAsync(false, 1, new List<string> { chatMid });
                if (details.Count > 0 && details[0].MemberCount.HasValue && details[0].MemberCount.Value > 0)
                {
                    ChatNameText.Text = StripMemberSuffix(details[0].ChatName ?? ResolveChatName(session, chatMid)) + " (" + details[0].MemberCount.Value + ")";
                    return;
                }
                var groups = await session.Talk.GetGroupsAsync(new List<string> { chatMid });
                foreach (var group in groups)
                {
                    object idValue;
                    if (!group.TryGetValue(1, out idValue) || !string.Equals(idValue == null ? null : idValue.ToString(), chatMid, StringComparison.Ordinal)) continue;
                    object nameValue, membersValue;
                    string name = group.TryGetValue(10, out nameValue) && nameValue != null ? nameValue.ToString() : ResolveChatName(session, chatMid);
                    int count = 0;
                    if (group.TryGetValue(20, out membersValue))
                        count = CountGroupMembers(membersValue);
                    if (count == 0 && group.TryGetValue(40, out membersValue))
                        count = CountGroupMembers(membersValue);
                    if (count == 0)
                    {
                        foreach (var chat in session.Store.GetCachedChats())
                            if (chat.ChatMid == chatMid && chat.MemberCount.HasValue) { count = chat.MemberCount.Value; break; }
                    }
                    name = StripMemberSuffix(name);
                    ChatNameText.Text = count > 0 ? name + " (" + count + ")" : name;
                    return;
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[ChatRoom] group header lookup failed: " + ex.Message); }
        }

        private static int CountGroupMembers(object membersValue)
        {
            var list = membersValue as List<object>;
            if (list != null) return list.Count;
            var map = membersValue as Dictionary<object, object>;
            if (map != null) return map.Count;
            var fields = membersValue as Dictionary<int, object>;
            if (fields != null)
                foreach (var value in fields.Values)
                {
                    var nestedList = value as List<object>;
                    if (nestedList != null) return nestedList.Count;
                    var nestedMap = value as Dictionary<object, object>;
                    if (nestedMap != null) return nestedMap.Count;
                }
            return 0;
        }

        private static string StripMemberSuffix(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            int open = name.LastIndexOf(" (", StringComparison.Ordinal);
            return open >= 0 && name.EndsWith(")", StringComparison.Ordinal) ? name.Substring(0, open) : name;
        }

        private async void OnEventsReceived(IReadOnlyList<TalkPollEvent> events)
        {
            int generation = LumINE.Utils.ForegroundUi.Generation;
            if (!LumINE.Utils.ForegroundUi.IsCurrent(generation)) return;
            bool relevant = false;
            bool messagesChanged = false;
            foreach (var ev in events)
            {
                if (ev.ChatMid != _chatMid) continue;
                relevant = true;
                if (ev.Kind == TalkEventKind.Message || ev.Kind == TalkEventKind.Revoke) messagesChanged = true;
            }
            if (!relevant) return;
            try
            {
                await LumINE.Utils.UiDispatcher.RunAsync(Dispatcher, async () =>
                {
                    if (!_active || !LumINE.Utils.ForegroundUi.IsCurrent(generation) || ViewModel == null) return;
                    if (!messagesChanged)
                    {
                        ViewModel.RefreshCachedMessages(); ViewModel.RefreshReadLabels();
                        ScrollForLatestReaction(events);
                        return;
                    }
                    await ViewModel.LoadMessagesAsync();
                    ScrollForLatestReaction(events);
                    ScrollToLatest();
                    await ViewModel.MarkAsReadAsync();
                });
            }
            catch (Exception ex) { LumINE.Utils.CrashDiagnostics.Record("ChatRoom.Events", ex); }
        }

        private void OnForegroundReady()
        {
            if (!_active || ViewModel == null) return;
            try
            {
                ViewModel.RefreshCachedMessages(); ViewModel.RefreshReadLabels();
                foreach (var message in ViewModel.Messages) message.RefreshState();
                QueueBottomScroll();
            }
            catch (Exception ex) { LumINE.Utils.CrashDiagnostics.Record("ChatRoom.Resume", ex); }
        }

        private void ScrollForLatestReaction(IReadOnlyList<TalkPollEvent> events)
        {
            foreach (var ev in events)
                if (ev.ChatMid == _chatMid && ev.Kind == TalkEventKind.Reaction && (ev.CustomReaction != null || ev.ReactionType != 0) && ViewModel.IsLatestMessage(ev.MessageId))
                {
                    _searchPinned = false;
                    ScrollToLatest();
                    return;
                }
        }

        private void Photo_Tapped(object sender, RoutedEventArgs args)
        {
            var view = sender as ChatMediaView;
            var message = view == null ? null : view.Message;
            if (_openingPhoto || message == null || !message.IsImage || message.IsRevoked) return;
            _openingPhoto = true;
            if (!Frame.Navigate(typeof(PhotoPreviewPage), message)) _openingPhoto = false;
        }

        private async void SendButton_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel != null)
            {
                ViewModel.MessageText = MessageInput.Text;
                _searchPinned = false;
                await ViewModel.SendMessageAsync();
                MessageInput.Text = ViewModel.MessageText ?? "";
                ScrollToLatest();
            }
        }

        private async void PhotoButton_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var picker = new FileOpenPicker();
            picker.ViewMode = PickerViewMode.Thumbnail;
            picker.SuggestedStartLocation = PickerLocationId.PicturesLibrary;
            picker.FileTypeFilter.Add(".jpg"); picker.FileTypeFilter.Add(".jpeg");
            picker.FileTypeFilter.Add(".png"); picker.FileTypeFilter.Add(".gif");
            var file = await picker.PickSingleFileAsync();
            if (file == null) return;
            using (ViewModel.BeginBusy())
            {
            try
            {
                var buffer = await FileIO.ReadBufferAsync(file);
                var bytes = new byte[(int)buffer.Length];
                DataReader.FromBuffer(buffer).ReadBytes(bytes);
                await ViewModel.SendPhotoAsync(bytes, file.Name);
                ScrollToLatest();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[ChatRoom] photo picker failed: " + ex.Message);
                ViewModel.StatusMessage = "写真を読み込めませんでした。";
            }
            }
        }

        private void ScrollToLatest()
        {
            if (!_active || _searchOpen || _searchPinned) return;
            // Retain the request while SplitView closes its full-width pane.
            // The conversation may still have zero width at this point.
            _followLatest = true;
            QueueBottomScroll();
        }

        private void QueueBottomScroll()
        {
            if (_bottomScrollQueued || !CanFollowLatest()) return;
            _bottomScrollQueued = true;
            try
            {
                var pending = Dispatcher.RunAsync(CoreDispatcherPriority.Low, () =>
                {
                    try { if (CanFollowLatest()) AlignLatestMessage(); }
                    catch (Exception ex) { LumINE.Utils.CrashDiagnostics.Record("ChatRoom.Scroll", ex); }
                    finally { _bottomScrollQueued = false; }
                });
            }
            catch (Exception ex) { _bottomScrollQueued = false; LumINE.Utils.CrashDiagnostics.Record("ChatRoom.Scroll", ex); }
        }

        private bool CanFollowLatest()
        {
            return _active && _followLatest && BackgroundNotifications.IsForeground && !_searchOpen && !_searchPinned;
        }

        private void AlignLatestMessage()
        {
            if (ViewModel == null || ViewModel.Messages.Count == 0 || MessagesList.ActualWidth <= 0 || MessagesList.ActualHeight <= 0) return;
            var last = ViewModel.Messages[ViewModel.Messages.Count - 1];
            if (!MessagesList.Items.Contains(last)) return;
            // Realize the last virtualized row before reading the scroll extent.
            if (!object.ReferenceEquals(_bottomScrollTarget, last) || MessagesList.ContainerFromItem(last) == null)
            {
                _bottomScrollTarget = last;
                MessagesList.ScrollIntoView(last);
                MessagesList.UpdateLayout();
            }
            if (_messagesScroll == null) _messagesScroll = FindMessageScrollViewer(MessagesList);
            if (_messagesScroll == null) return;
            // ScrollIntoView only guarantees visibility; a tall wrapped message
            // can still extend below the viewport. Align to the actual bottom.
            // Later layout passes repeat this if images or row estimates grow.
            if (Math.Abs(_messagesScroll.ScrollableHeight - _messagesScroll.VerticalOffset) > 0.5)
                _messagesScroll.ChangeView(null, _messagesScroll.ScrollableHeight, null, true);
        }

        private static ScrollViewer FindMessageScrollViewer(DependencyObject parent)
        {
            var scroll = parent as ScrollViewer;
            if (scroll != null) return scroll;
            for (int i = 0; i < Windows.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                scroll = FindMessageScrollViewer(Windows.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, i));
                if (scroll != null) return scroll;
            }
            return null;
        }

        private async void ReactionMenu_Click(object sender, RoutedEventArgs e)
        {
            var menu = sender as MenuFlyoutItem;
            var message = menu == null ? null : menu.CommandParameter as Message;
            if (_reactionOpen || _searchOpen || _stickerPickerOpen || message == null || message.IsRevoked || ViewModel == null) return;
            _reactionOpen = true;
            try
            {
                var session = SessionManager.Current;
                if (session == null) return;
                var standard = new List<MessageReaction>();
                for (int type = 2; type <= 7; type++) standard.Add(new MessageReaction { Type = type });
                var owned = new System.Collections.ObjectModel.ObservableCollection<EmojiReactionPack> { new EmojiReactionPack { Name = "基本リアクション" } };
                var packs = new ComboBox { ItemsSource = owned, DisplayMemberPath = "Name", SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
                var list = new GridView { ItemsSource = standard, SelectionMode = ListViewSelectionMode.Single, MaxHeight = 280, ItemTemplate = (DataTemplate)Resources["ReactionChoiceTemplate"] };
                var status = new TextBlock { Text = "所持絵文字を確認しています…", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
                var panel = new StackPanel(); panel.Children.Add(packs); panel.Children.Add(list); panel.Children.Add(status);
                var dialog = new ContentDialog { Title = "リアクション", Content = panel, PrimaryButtonText = "送信", SecondaryButtonText = "削除", CloseButtonText = "キャンセル", IsPrimaryButtonEnabled = false };
                bool open = true; int version = 0;
                dialog.Closed += (s, args) => { open = false; version++; };
                list.SelectionChanged += (s, args) => dialog.IsPrimaryButtonEnabled = list.SelectedItem is MessageReaction;
                packs.SelectionChanged += async (s, args) =>
                {
                    int current = ++version;
                    var pack = packs.SelectedItem as EmojiReactionPack;
                    list.SelectedItem = null; list.ItemsSource = null; dialog.IsPrimaryButtonEnabled = false;
                    if (pack == null || pack.Id == null) { list.ItemsSource = standard; return; }
                    status.Text = "絵文字を読み込んでいます…";
                    try
                    {
                        var choices = await EmojiReactionCatalog.LoadChoicesAsync(session.AccountId, pack);
                        if (!open || current != version) return;
                        list.ItemsSource = choices; status.Text = choices.Count == 0 ? "このパックに絵文字がありません。" : "";
                    }
                    catch (Exception ex) { if (open && current == version) status.Text = "絵文字を取得できませんでした: " + ex.Message; }
                };
                dialog.Opened += async (s, args) =>
                {
                    try
                    {
                        foreach (var pack in await EmojiReactionCatalog.LoadCachedAsync(session.AccountId)) { if (!open) return; owned.Add(pack); }
                        var refreshed = await EmojiReactionCatalog.RefreshOwnedAsync(session);
                        if (!open) return;
                        // Replace stale ownership data; don't offer received or expired packs.
                        packs.SelectedIndex = 0;
                        while (owned.Count > 1) owned.RemoveAt(owned.Count - 1);
                        foreach (var pack in refreshed) owned.Add(pack);
                        status.Text = refreshed.Count == 0 ? "追加の所持絵文字はありません。" : "パックを選ぶと所持絵文字を使用できます。";
                    }
                    catch (Exception ex) { if (open) status.Text = "所持絵文字の更新に失敗しました。保存済みの一覧を表示しています。"; System.Diagnostics.Debug.WriteLine("[Reactions] catalog: " + ex.Message); }
                };
                var result = await dialog.ShowAsync();
                if (result == ContentDialogResult.Primary || result == ContentDialogResult.Secondary)
                {
                    var selected = result == ContentDialogResult.Primary ? list.SelectedItem as MessageReaction : null;
                    if (result == ContentDialogResult.Primary && selected == null) return;
                    await ViewModel.ReactAsync(message, selected);
                    if (selected != null && string.IsNullOrEmpty(ViewModel.StatusMessage) && ViewModel.IsLatestMessage(message.Id)) { _searchPinned = false; ScrollToLatest(); }
                }
            }
            catch (Exception ex) { ViewModel.StatusMessage = "リアクションを選択できませんでした: " + ex.Message; }
            finally { _reactionOpen = false; }
        }

        private async void SearchButton_Click(object sender, RoutedEventArgs e)
        {
            if (_searchOpen || _reactionOpen || _stickerPickerOpen || ViewModel == null) return;
            _searchOpen = true;
            int version = 0;
            try
            {
                var input = new TextBox { PlaceholderText = "メッセージを検索" };
                var status = new TextBlock { Text = "取得できた履歴を検索します。", Margin = new Thickness(0, 8, 0, 8), TextWrapping = TextWrapping.Wrap };
                var results = new ListView { MaxHeight = 300, SelectionMode = ListViewSelectionMode.Single };
                var panel = new StackPanel();
                panel.Children.Add(input); panel.Children.Add(status); panel.Children.Add(results);
                var dialog = new ContentDialog { Title = "トーク内検索", Content = panel, PrimaryButtonText = "移動", CloseButtonText = "閉じる", IsPrimaryButtonEnabled = false };
                results.SelectionChanged += (s, args) => dialog.IsPrimaryButtonEnabled = results.SelectedItem != null;
                input.TextChanged += async (s, args) =>
                {
                    int current = ++version;
                    string query = input.Text;
                    dialog.IsPrimaryButtonEnabled = false;
                    try
                    {
                        await Task.Delay(250);
                        if (!_searchOpen || current != version) return;
                        if (string.IsNullOrWhiteSpace(query)) { results.Items.Clear(); status.Text = "検索する文字を入力してください。"; return; }
                        status.Text = "履歴を検索中...";
                        var matches = await ViewModel.SearchMessagesAsync(query);
                        if (!_searchOpen || current != version) return;
                        results.Items.Clear();
                        foreach (var match in matches)
                        {
                            string text = match.Text.Replace('\r', ' ').Replace('\n', ' ');
                            if (text.Length > 90) text = text.Substring(0, 90) + "…";
                            long at = ChatActivityProtocol.Timestamp(match.CreatedTime ?? 0);
                            string date = at > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(at).ToLocalTime().ToString("M/d HH:mm") : "";
                            results.Items.Add(new ListViewItem { Content = new TextBlock { Text = date + "  " + text, TextWrapping = TextWrapping.Wrap, MaxLines = 3 }, Tag = match });
                        }
                        status.Text = matches.Count + "件（取得できた履歴内）";
                    }
                    catch (Exception ex) { if (current == version && _searchOpen) status.Text = "検索できませんでした: " + ex.Message; }
                };
                var choice = await dialog.ShowAsync();
                ++version;
                var hit = results.SelectedItem as ListViewItem;
                var target = hit == null ? null : hit.Tag as Message;
                if (choice == ContentDialogResult.Primary && target != null)
                {
                    // Live updates can replace a Message object while the dialog is open.
                    foreach (var current in ViewModel.Messages)
                        if (!current.IsDateSeparator && current.Id == target.Id) { target = current; break; }
                    _searchPinned = true;
                    MessagesList.UpdateLayout();
                    MessagesList.SelectedItem = target;
                    MessagesList.ScrollIntoView(target, ScrollIntoViewAlignment.Leading);
                }
            }
            catch (Exception ex) { if (ViewModel != null) ViewModel.StatusMessage = "検索画面を開けませんでした: " + ex.Message; }
            finally { _searchOpen = false; ++version; }
        }

        private void ReplyMenu_Click(object sender, RoutedEventArgs e)
        {
            var item = sender as MenuFlyoutItem;
            var message = item == null ? null : item.CommandParameter as Message;
            if (message == null || ViewModel == null) return;
            ViewModel.SetReplyTarget(message);
            MessageInput.Focus(FocusState.Programmatic);
        }

        private sealed class ReplySwipe
        {
            public string Id;
            public long Started;
        }
        private void MessageSwipe_Started(object sender, Windows.UI.Xaml.Input.ManipulationStartedRoutedEventArgs args)
        {
            var host = sender as FrameworkElement;
            var message = host == null ? null : host.DataContext as Message;
            if (host != null) host.Tag = null;
            if (message == null || message.IsRevoked || message.IsDateSeparator || HasSelectedText(host)) return;
            host.Tag = new ReplySwipe { Id = message.Id, Started = System.Diagnostics.Stopwatch.GetTimestamp() };
            host.RenderTransform = new Windows.UI.Xaml.Media.TranslateTransform();
        }
        private void MessageSwipe_Delta(object sender, Windows.UI.Xaml.Input.ManipulationDeltaRoutedEventArgs args)
        {
            var host = sender as FrameworkElement;
            if (host == null || !(host.Tag is ReplySwipe)) return;
            if (HasSelectedText(host))
            {
                host.Tag = null; host.Opacity = 1;
                var reset = host.RenderTransform as Windows.UI.Xaml.Media.TranslateTransform;
                if (reset != null) reset.X = 0;
                return;
            }
            var translation = host.RenderTransform as Windows.UI.Xaml.Media.TranslateTransform;
            if (translation != null) translation.X = Math.Max(-72, Math.Min(0, args.Cumulative.Translation.X));
            host.Opacity = args.Cumulative.Translation.X <= -56 ? 0.75 : 1;
        }
        private void MessageSwipe_Completed(object sender, Windows.UI.Xaml.Input.ManipulationCompletedRoutedEventArgs args)
        {
            var host = sender as FrameworkElement;
            var swipe = host == null ? null : host.Tag as ReplySwipe;
            if (host == null || swipe == null) return;
            host.Tag = null; host.Opacity = 1;
            var translation = host.RenderTransform as Windows.UI.Xaml.Media.TranslateTransform;
            if (translation != null) translation.X = 0;
            double seconds = (System.Diagnostics.Stopwatch.GetTimestamp() - swipe.Started) / (double)System.Diagnostics.Stopwatch.Frequency;
            var message = host.DataContext as Message;
            if (args.Cumulative.Translation.X <= -56 && seconds < 1 && message != null && !message.IsRevoked && message.Id == swipe.Id && ViewModel != null)
            { ViewModel.SetReplyTarget(message); MessageInput.Focus(FocusState.Programmatic); }
        }

        private static bool HasSelectedText(DependencyObject parent)
        {
            var text = parent as RichTextBlock;
            if (text != null && !string.IsNullOrEmpty(text.SelectedText)) return true;
            for (int i = 0; i < Windows.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
                if (HasSelectedText(Windows.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, i))) return true;
            return false;
        }

        private void CopyMenu_Click(object sender, RoutedEventArgs e)
        {
            var menu = sender as MenuFlyoutItem;
            var message = menu == null ? null : menu.CommandParameter as Message;
            if (message == null || string.IsNullOrEmpty(message.DisplayText)) return;
            try
            {
                var data = new Windows.ApplicationModel.DataTransfer.DataPackage();
                data.SetText(message.DisplayText);
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data);
            }
            catch (Exception ex) { if (ViewModel != null) ViewModel.StatusMessage = "コピーできませんでした: " + ex.Message; }
        }

        private async void UnsendMenu_Click(object sender, RoutedEventArgs e)
        {
            var item = sender as MenuFlyoutItem;
            var message = item == null ? null : item.CommandParameter as Message;
            if (message == null || ViewModel == null) return;
            if (!message.CanUnsend) { ViewModel.StatusMessage = "送信から1時間を過ぎたメッセージは取り消せません。"; return; }
            var dialog = new ContentDialog { Title = "送信を取り消しますか？", Content = "相手のトーク画面からもメッセージが削除されます。", PrimaryButtonText = "送信を取り消す", CloseButtonText = "キャンセル" };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary) await ViewModel.UnsendAsync(message);
        }

        private void CancelReply_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel != null) ViewModel.SetReplyTarget(null);
        }

        public sealed class StickerChoice
        {
            public string Id { get; set; }
            public string PackageId { get; set; }
            public string Version { get; set; }
            public string ImageUrl { get { return "https://stickershop.line-scdn.net/stickershop/v1/sticker/" + Id + "/android/sticker.png"; } }
        }

        private async void StickerButton_Click(object sender, RoutedEventArgs e)
        {
            var session = SessionManager.Current;
            if (session == null || ViewModel == null || _stickerPickerOpen || _searchOpen || _reactionOpen) return;
            _stickerPickerOpen = true;
            try
            {
            var choices = new List<StickerChoice>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var meta in await StickerCatalog.LoadRecentAsync(session.AccountId))
                {
                    string id, packageId, version;
                    if (!meta.TryGetValue("STKID", out id) || !meta.TryGetValue("STKPKGID", out packageId) || !seen.Add(id)) continue;
                    meta.TryGetValue("STKVER", out version);
                    choices.Add(new StickerChoice { Id = id, PackageId = packageId, Version = version ?? "1" });
                }
            var grid = new GridView { ItemsSource = choices, SelectionMode = ListViewSelectionMode.Single, MaxHeight = 360 };
            grid.ItemTemplate = (DataTemplate)Windows.UI.Xaml.Markup.XamlReader.Load("<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><Image Source='{Binding ImageUrl}' Width='80' Height='80' Margin='4' Stretch='Uniform'/></DataTemplate>");
            var packs = new System.Collections.ObjectModel.ObservableCollection<StickerPack>();
            var recentPack = new StickerPack { Id = "", Name = "最近使用したスタンプ" };
            packs.Add(recentPack);
            foreach (var pack in await StickerCatalog.LoadCachedPacksAsync(session.AccountId)) packs.Add(pack);
            var selector = new ComboBox { ItemsSource = packs, DisplayMemberPath = "Name", HorizontalAlignment = HorizontalAlignment.Stretch, SelectedIndex = 0 };
            var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 6), FontSize = 12 };
            var refresh = new Button { Content = "購入済み一覧を更新", HorizontalAlignment = HorizontalAlignment.Right };
            var panel = new StackPanel();
            panel.Children.Add(selector); panel.Children.Add(refresh); panel.Children.Add(status); panel.Children.Add(grid);
            var dialog = new ContentDialog { Title = "スタンプ", Content = panel, PrimaryButtonText = "送信", CloseButtonText = "閉じる", IsPrimaryButtonEnabled = false };
            bool closed = false;
            int selectionVersion = 0;
            selector.SelectionChanged += async (s, args) =>
            {
                int version = ++selectionVersion;
                var pack = selector.SelectedItem as StickerPack;
                dialog.IsPrimaryButtonEnabled = false;
                if (pack == null || string.IsNullOrEmpty(pack.Id)) { grid.ItemsSource = choices; status.Text = ""; return; }
                grid.ItemsSource = null;
                status.Text = "スタンプを読み込み中…";
                try
                {
                    var ids = await ViewModel.RunBusyAsync(() => StickerCatalog.LoadStickerIdsAsync(session.AccountId, pack));
                    if (closed || version != selectionVersion) return;
                    var items = new List<StickerChoice>();
                    foreach (var id in ids) items.Add(new StickerChoice { Id = id, PackageId = pack.Id, Version = pack.Version });
                    grid.ItemsSource = items;
                    status.Text = "";
                }
                catch (Exception ex) { if (!closed && version == selectionVersion) status.Text = "スタンプを取得できませんでした: " + ex.Message; }
            };
            Func<Task> syncPacks = async () =>
            {
                refresh.IsEnabled = false;
                status.Text = "購入済みスタンプを同期中…";
                try
                {
                    var owned = await ViewModel.RunBusyAsync(() => StickerCatalog.RefreshPacksAsync(session));
                    if (closed) return;
                    var selectedPack = selector.SelectedItem as StickerPack;
                    packs.Clear(); packs.Add(recentPack);
                    foreach (var pack in owned) packs.Add(pack);
                    selector.SelectedItem = recentPack;
                    if (selectedPack != null)
                        foreach (var pack in packs) if (pack.Id == selectedPack.Id) { selector.SelectedItem = pack; break; }
                    status.Text = owned.Count + " パックを同期しました。";
                }
                catch (Exception ex) { if (!closed) status.Text = "一覧を同期できませんでした。保存済みの一覧を表示しています: " + ex.Message; }
                finally { if (!closed) refresh.IsEnabled = true; }
            };
            refresh.Click += async (s, args) => await syncPacks();
            grid.SelectionChanged += (s, args) => dialog.IsPrimaryButtonEnabled = grid.SelectedItem != null;
            _ = syncPacks();
            var answer = await dialog.ShowAsync();
            closed = true;
            if (answer != ContentDialogResult.Primary) return;
            var selected = grid.SelectedItem as StickerChoice;
            if (selected == null) return;
            var activePack = selector.SelectedItem as StickerPack;
            if (activePack != null && activePack.IsExpired) { ViewModel.StatusMessage = "このスタンプの利用期限が切れています。"; return; }
            using (ViewModel.BeginBusy())
            {
            try
            {
                var meta = new Dictionary<string, string> { { "STKID", selected.Id }, { "STKPKGID", selected.PackageId }, { "STKVER", selected.Version } };
                var result = await session.Talk.SendMessageAsync(_chatMid, null, 7, meta, null, 0);
                try { await StickerCatalog.RecordSentAsync(session.AccountId, meta); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Stickers] recent save failed: " + ex.Message); }
                var sent = ChatStore.MapMessage(result, _chatMid);
                if (sent != null && !string.IsNullOrEmpty(sent.Id))
                {
                    session.Store.UpsertMessage(_chatMid, sent);
                    _ = StickerSoundPlayer.PlayAsync(session, sent, false, null, () => _active);
                }
                await session.Line.RefreshRecentMessagesAsync(_chatMid, 30);
                await ViewModel.LoadMessagesAsync();
            }
            catch (Exception ex) { ViewModel.StatusMessage = "スタンプを送信できませんでした: " + ex.Message; }
            }
            }
            finally { _stickerPickerOpen = false; }
        }
    }
}
