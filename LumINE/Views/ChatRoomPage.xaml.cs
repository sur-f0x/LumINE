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

        public ChatRoomPage()
        {
            this.InitializeComponent();
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
        }

        private void OnSystemBackRequested(object sender, BackRequestedEventArgs e)
        {
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
            }
            catch (Exception ex) { LumINE.Utils.CrashDiagnostics.Record("ChatRoom.Resume", ex); }
        }

        private void ScrollForLatestReaction(IReadOnlyList<TalkPollEvent> events)
        {
            foreach (var ev in events)
                if (ev.ChatMid == _chatMid && ev.Kind == TalkEventKind.Reaction && ev.ReactionType != 0 && ViewModel.IsLatestMessage(ev.MessageId))
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

        private async void ScrollToLatest()
        {
            try
            {
                if (!_active || !BackgroundNotifications.IsForeground || _searchOpen || _searchPinned) return;
                await Task.Delay(40);
                if (!_active || !BackgroundNotifications.IsForeground || _searchOpen || _searchPinned || ViewModel == null || ViewModel.Messages.Count == 0 || MessagesList.ActualHeight <= 0) return;
                MessagesList.UpdateLayout();
                var last = ViewModel.Messages[ViewModel.Messages.Count - 1];
                if (MessagesList.Items.Contains(last)) MessagesList.ScrollIntoView(last);
            }
            catch (Exception ex) { LumINE.Utils.CrashDiagnostics.Record("ChatRoom.Scroll", ex); }
        }

        private async void ReactionMenu_Click(object sender, RoutedEventArgs e)
        {
            var menu = sender as MenuFlyoutItem;
            var message = menu == null ? null : menu.CommandParameter as Message;
            if (_reactionOpen || _searchOpen || _stickerPickerOpen || message == null || message.IsRevoked || ViewModel == null) return;
            _reactionOpen = true;
            try
            {
                var list = new ListView { SelectionMode = ListViewSelectionMode.Single, MaxHeight = 330 };
                for (int type = 2; type <= 7; type++) list.Items.Add(MessageReaction.Label(type));
                list.Items.Add("自分のリアクションを削除");
                var dialog = new ContentDialog { Title = "リアクション", Content = list, PrimaryButtonText = "送信", CloseButtonText = "キャンセル", IsPrimaryButtonEnabled = false };
                list.SelectionChanged += (s, args) => dialog.IsPrimaryButtonEnabled = list.SelectedIndex >= 0;
                if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                {
                    await ViewModel.ReactAsync(message, list.SelectedIndex == 6 ? 0 : list.SelectedIndex + 2);
                    if (list.SelectedIndex != 6 && string.IsNullOrEmpty(ViewModel.StatusMessage) && ViewModel.IsLatestMessage(message.Id)) { _searchPinned = false; ScrollToLatest(); }
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
                if (sent != null && !string.IsNullOrEmpty(sent.Id)) session.Store.UpsertMessage(_chatMid, sent);
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
