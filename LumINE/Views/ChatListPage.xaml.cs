using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.UI.Xaml.Controls;
using LumINE.Models;
using LumINE.Services.Vyline;
using LumINE.ViewModels;

namespace LumINE.Views
{
    public sealed partial class ChatListPage : Page
    {
        private static bool _lastShowFriends = true;
        public ChatListViewModel ViewModel { get; set; }
        private bool _subscribed;
        private bool _active;
        private string _selectedChat;
        private string _accountId;
        private bool IsWide { get { return ActualWidth >= 900; } }

        public ChatListPage()
        {
            ViewModel = new ChatListViewModel();
            this.InitializeComponent();
            NavigationCacheMode = Windows.UI.Xaml.Navigation.NavigationCacheMode.Required;
            ChatsFlip.RegisterPropertyChangedCallback(Windows.UI.Xaml.Controls.Primitives.Selector.SelectedIndexProperty, OnTabSelectionChanged);
            this.DataContext = ViewModel;
            ConversationFrame.Tag = this;
            SizeChanged += (s, e) => { UpdateWorkspace(); UpdateTabButtons(); };
        }

        protected override async void OnNavigatedTo(Windows.UI.Xaml.Navigation.NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            var account = SessionManager.Current == null ? null : SessionManager.Current.AccountId;
            if (_accountId != account)
            {
                _accountId = account; _selectedChat = null;
                ViewModel = new ChatListViewModel(); DataContext = ViewModel;
            }
            _active = true;
            Windows.UI.Core.SystemNavigationManager.GetForCurrentView().BackRequested += WorkspaceBackRequested;
            if (!string.IsNullOrEmpty(_selectedChat)) OpenChat(_selectedChat);
            UpdateWorkspace();
            LumINE.Utils.ForegroundUi.Ready += OnChatsUpdated;
            ViewModel.LatestMessagesUpdated += ScrollToTop;
            if (_lastShowFriends) ViewModel.SelectFriends(); else ViewModel.SelectGroups();
            UpdateTabButtons();

            var session = SessionManager.Current;
            if (session != null && !_subscribed)
            {
                _subscribed = true;
                session.EnsureSyncStarted();
                session.Sync.ChatsUpdated += OnChatsUpdated;
                session.Sync.EventsReceived += OnEventsReceived;
            }

            using (ViewModel.BeginBusy())
            {
                await BackgroundNotifications.EnsureRealtimeAsync();
                await ViewModel.LoadChatsAsync();
            }
            UpdateTabButtons();
        }

        protected override void OnNavigatedFrom(Windows.UI.Xaml.Navigation.NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            _active = false;
            Windows.UI.Core.SystemNavigationManager.GetForCurrentView().BackRequested -= WorkspaceBackRequested;
            // Child Frame pages must receive OnNavigatedFrom when the root is
            // replaced (Hello/login), otherwise hidden conversations keep polling.
            if (ConversationFrame.Content != null) ConversationFrame.Navigate(typeof(Page));
            ConversationFrame.BackStack.Clear();
            LumINE.Utils.ForegroundUi.Ready -= OnChatsUpdated;
            ViewModel.LatestMessagesUpdated -= ScrollToTop;
            var session = SessionManager.Current;
            if (session != null && _subscribed)
            {
                _subscribed = false;
                session.Sync.ChatsUpdated -= OnChatsUpdated;
                session.Sync.EventsReceived -= OnEventsReceived;
            }
        }

        private async void ScrollToTop()
        {
            try
            {
                if (!_active || !BackgroundNotifications.IsForeground || !IsNearTop()) return;
                await Task.Delay(40);
                if (!_active || !BackgroundNotifications.IsForeground || ViewModel.Chats.Count == 0 || !IsNearTop()) return;
                var list = ChatsList;
                if (list.ActualWidth <= 0 || list.ActualHeight <= 0) return;
                list.UpdateLayout();
                var first = ViewModel.Chats[0];
                if (list.Items.Contains(first)) list.ScrollIntoView(first, ScrollIntoViewAlignment.Leading);
            }
            catch (Exception ex) { LumINE.Utils.CrashDiagnostics.Record("ChatList.Scroll", ex); }
        }

        private bool IsNearTop()
        {
            var scroll = FindScrollViewer(ChatsList);
            if (scroll == null) return true;
            var first = ChatsList.ContainerFromIndex(0) as Windows.UI.Xaml.FrameworkElement;
            if (first == null)
            {
                var panel = ChatsList.ItemsPanelRoot as ItemsStackPanel;
                if (panel == null || panel.FirstVisibleIndex > 1) return false;
                first = ChatsList.ContainerFromIndex(Math.Max(0, panel.FirstVisibleIndex)) as Windows.UI.Xaml.FrameworkElement;
            }
            double rowHeight = first == null ? 0 : first.ActualHeight;
            return scroll.VerticalOffset <= rowHeight + 0.5;
        }

        private static ScrollViewer FindScrollViewer(Windows.UI.Xaml.DependencyObject parent)
        {
            var scroll = parent as ScrollViewer;
            if (scroll != null) return scroll;
            for (int i = 0; i < Windows.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                scroll = FindScrollViewer(Windows.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, i));
                if (scroll != null) return scroll;
            }
            return null;
        }

        private async void OnChatsUpdated()
        {
            int generation = LumINE.Utils.ForegroundUi.Generation;
            if (!LumINE.Utils.ForegroundUi.IsCurrent(generation)) return;
            try
            {
                await LumINE.Utils.UiDispatcher.RunAsync(Dispatcher, async () => { if (_active && LumINE.Utils.ForegroundUi.IsCurrent(generation)) await ViewModel.LoadChatsAsync(); });
            }
            catch (Exception ex) { LumINE.Utils.CrashDiagnostics.Record("ChatList.Refresh", ex); }
        }

        private async void OnEventsReceived(IReadOnlyList<TalkPollEvent> events)
        {
            int generation = LumINE.Utils.ForegroundUi.Generation;
            if (!LumINE.Utils.ForegroundUi.IsCurrent(generation)) return;
            try
            {
                await LumINE.Utils.UiDispatcher.RunAsync(Dispatcher, () => { if (_active && LumINE.Utils.ForegroundUi.IsCurrent(generation)) ViewModel.ApplyEvents(events); return Task.CompletedTask; });
            }
            catch (Exception ex) { LumINE.Utils.CrashDiagnostics.Record("ChatList.Events", ex); }
        }

        private void ChatList_ItemClick(object sender, ItemClickEventArgs e)
        {
            var summary = e.ClickedItem as ChatSummary;
            if (summary == null || string.IsNullOrEmpty(summary.ChatMid)) return;
            OpenChat(summary.ChatMid);
        }

        private void FriendsTab_Click(object sender, Windows.UI.Xaml.RoutedEventArgs e)
        {
            _lastShowFriends = true;
            ViewModel.SelectFriends();
            UpdateTabButtons();
        }

        private void GroupsTab_Click(object sender, Windows.UI.Xaml.RoutedEventArgs e)
        {
            _lastShowFriends = false;
            ViewModel.SelectGroups();
            UpdateTabButtons();
        }

        private void UpdateTabButtons()
        {
            ChatsFlip.SelectedIndex = ViewModel.ShowFriends ? 0 : 1;
            PageTitleText.Text = ViewModel.ShowFriends ? "友だち" : "グループ";
            FriendsTabButton.Background = new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Colors.Transparent);
            FriendsTabButton.Foreground = ViewModel.ShowFriends
                ? new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Colors.White)
                : new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x85, 0x90, 0xA8));
            GroupsTabButton.Background = new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Colors.Transparent);
            GroupsTabButton.Foreground = ViewModel.ShowFriends
                ? new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x85, 0x90, 0xA8))
                : new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Colors.White);
            FriendsTabIcon.Foreground = FriendsTabButton.Foreground;
            GroupsTabIcon.Foreground = GroupsTabButton.Foreground;
        }

        private void SearchButton_Click(object sender, Windows.UI.Xaml.RoutedEventArgs e)
        {
            SearchPanel.Visibility = Windows.UI.Xaml.Visibility.Visible;
            SearchInput.Focus(Windows.UI.Xaml.FocusState.Programmatic);
        }
        private void SearchInput_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (ViewModel == null) return;
            ViewModel.SearchQuery = SearchInput.Text;
            if (_active && ViewModel.Chats.Count > 0) { ChatsList.UpdateLayout(); ChatsList.ScrollIntoView(ViewModel.Chats[0], ScrollIntoViewAlignment.Leading); }
        }
        private void CloseSearch_Click(object sender, Windows.UI.Xaml.RoutedEventArgs e) { SearchInput.Text = ""; SearchPanel.Visibility = Windows.UI.Xaml.Visibility.Collapsed; }
        private ListView ChatsList { get { return IsWide ? WideChatsList : ViewModel.ShowFriends ? FriendsList : GroupsList; } }
        private void OnTabSelectionChanged(Windows.UI.Xaml.DependencyObject sender, Windows.UI.Xaml.DependencyProperty property)
        {
            // The desktop list is controlled by the navigation buttons. Focus
            // changes during child-frame navigation must not select a hidden
            // FlipView page and overwrite the active desktop tab.
            if (!_active || IsWide || ChatsFlip.SelectedIndex < 0) return;
            _lastShowFriends = ChatsFlip.SelectedIndex == 0;
            if (_lastShowFriends != ViewModel.ShowFriends) { if (_lastShowFriends) ViewModel.SelectFriends(); else ViewModel.SelectGroups(); }
            UpdateTabButtons();
        }
        private async void AddButton_Click(object sender, Windows.UI.Xaml.RoutedEventArgs e)
        {
            await new ContentDialog { Title = "新しいトーク", Content = "トークの作成機能は今後追加予定です。", CloseButtonText = "閉じる" }.ShowAsync();
        }

        private void MoreButton_Click(object sender, Windows.UI.Xaml.RoutedEventArgs e)
        {
            ConversationFrame.Navigate(typeof(SettingsPage));
            UpdateWorkspace();
        }

        public void OpenChat(string chatMid)
        {
            _selectedChat = chatMid;
            ConversationFrame.Navigate(typeof(ChatRoomPage), chatMid);
            ConversationFrame.BackStack.Clear();
            UpdateWorkspace();
        }

        public void ShowChatList()
        {
            _selectedChat = null;
            ConversationFrame.Navigate(typeof(Page));
            ConversationFrame.BackStack.Clear();
            UpdateWorkspace();
        }

        private void UpdateWorkspace()
        {
            if (WorkspaceSplit == null) return;
            bool detail = ConversationFrame.Content != null && ConversationFrame.Content.GetType() != typeof(Page);
            WorkspaceSplit.OpenPaneLength = IsWide ? 380 : Math.Max(0, ActualWidth);
            WorkspaceSplit.IsPaneOpen = IsWide || !detail;
            EmptyConversation.Visibility = detail ? Windows.UI.Xaml.Visibility.Collapsed : Windows.UI.Xaml.Visibility.Visible;
            if (_active)
                Windows.UI.Core.SystemNavigationManager.GetForCurrentView().AppViewBackButtonVisibility = detail
                    ? Windows.UI.Core.AppViewBackButtonVisibility.Visible : Windows.UI.Core.AppViewBackButtonVisibility.Collapsed;
        }

        private void ConversationFrame_Navigated(object sender, Windows.UI.Xaml.Navigation.NavigationEventArgs args) { UpdateWorkspace(); }
        private void WorkspaceBackRequested(object sender, Windows.UI.Core.BackRequestedEventArgs args)
        {
            if (args.Handled || !_active) return;
            var settings = ConversationFrame.Content as SettingsPage;
            if (settings != null && settings.IsSaving) { args.Handled = true; return; }
            if (ConversationFrame.CanGoBack) { args.Handled = true; ConversationFrame.GoBack(); }
            else if (!string.IsNullOrEmpty(_selectedChat) || ConversationFrame.Content is SettingsPage)
            { args.Handled = true; ShowChatList(); }
        }
    }
}
