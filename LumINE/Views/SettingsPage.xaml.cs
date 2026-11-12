using System;
using Windows.Security.Credentials.UI;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;
using LumINE.Services;
using LumINE.Services.Vyline;
using LumINE.ViewModels;

namespace LumINE.Views
{
    public sealed partial class SettingsPage : Page
    {
        private readonly BaseViewModel _progress = new BaseViewModel();
        private bool _saving;
        private void Attributions_Click(object sender, RoutedEventArgs args) { if (!_saving) Frame.Navigate(typeof(AttributionsPage)); }
        public SettingsPage() { InitializeComponent(); DataContext = _progress; }
        protected override void OnNavigatedTo(NavigationEventArgs args)
        {
            base.OnNavigatedTo(args);
            SystemNavigationManager.GetForCurrentView().BackRequested += OnBackRequested;
            var session = SessionManager.Current;
            NotificationsToggle.IsEnabled = session != null;
            NotificationsToggle.IsOn = session != null && BackgroundNotifications.IsEnabled(session.AccountId);
            HelloToggle.IsOn = AppSettings.RequireHello; HideContentToggle.IsOn = AppSettings.HideMessageContent; AnimateToggle.IsOn = AppSettings.AnimateStickers;
            RefreshStatus();
        }
        protected override void OnNavigatedFrom(NavigationEventArgs args) { SystemNavigationManager.GetForCurrentView().BackRequested -= OnBackRequested; base.OnNavigatedFrom(args); }
        private void OnBackRequested(object sender, BackRequestedEventArgs args) { if (_saving) { args.Handled = true; return; } if (Frame.CanGoBack) { args.Handled = true; Frame.GoBack(); } }
        private void BackButton_Click(object sender, RoutedEventArgs args) { if (!_saving && Frame.CanGoBack) Frame.GoBack(); }
        private void RefreshStatus() { NotificationStatusText.Text = BackgroundNotifications.RegistrationStatus + "\n" + BackgroundNotifications.RealtimeStatus + "\n" + BackgroundNotifications.DeliveryStatus; }
        private async void Save_Click(object sender, RoutedEventArgs args)
        {
            if (_saving) return; _saving = true; SaveButton.IsEnabled = false;
            bool hello = HelloToggle.IsOn, hide = HideContentToggle.IsOn, animate = AnimateToggle.IsOn, notifications = NotificationsToggle.IsOn;
            using (_progress.BeginBusy())
            try
            {
                if (hello != AppSettings.RequireHello)
                {
                    if (!await AppAuthentication.VerifyAsync("LumINEの起動時認証の設定を変更します。")) { StatusText.Text = "本人確認が完了しなかったため、設定を保存しませんでした。"; return; }
                }
                var session = SessionManager.Current;
                if (session != null && notifications != BackgroundNotifications.IsEnabled(session.AccountId)) await BackgroundNotifications.SetEnabledAsync(session.AccountId, notifications);
                AppSettings.RequireHello = hello; AppSettings.HideMessageContent = hide; AppSettings.AnimateStickers = animate;
                if (!animate) PreparedStickerCache.Clear();
                await BackgroundNotifications.ApplyContentPrivacyAsync();
                StatusText.Text = "設定を保存しました。"; RefreshStatus();
            }
            catch (Exception ex) { StatusText.Text = "設定を保存できませんでした: " + ex.Message; }
            finally { _saving = false; SaveButton.IsEnabled = true; }
        }
        private void TestNotification_Click(object sender, RoutedEventArgs args) { try { BackgroundNotifications.ShowTestNotification(); RefreshStatus(); StatusText.Text = "保存済みの設定でテスト通知を送信しました。"; } catch (Exception ex) { StatusText.Text = ex.Message; } }
        private async void LockScreen_Click(object sender, RoutedEventArgs args) { await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:lockscreen")); }
        private async void Logout_Click(object sender, RoutedEventArgs args)
        {
            if (_saving) return;
            if (await new ContentDialog { Title = "ログアウトしますか？", PrimaryButtonText = "ログアウト", CloseButtonText = "キャンセル" }.ShowAsync() != ContentDialogResult.Primary) return;
            _saving = true;
            try { using (_progress.BeginBusy()) await SessionManager.LogoutAsync(); Frame.Navigate(typeof(LoginPage)); Frame.BackStack.Clear(); }
            catch (Exception ex) { StatusText.Text = "ログアウトできませんでした: " + ex.Message; }
            finally { _saving = false; }
        }
    }
}
