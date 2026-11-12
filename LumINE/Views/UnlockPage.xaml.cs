using System;
using System.Threading.Tasks;
using Windows.Security.Credentials.UI;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;
using LumINE.Services.Vyline;
using LumINE.Services;

namespace LumINE.Views
{
    public sealed partial class UnlockPage : Page
    {
        private string _arguments;
        private bool _verifying, _active;
        public UnlockPage() { InitializeComponent(); Loaded += async (s, e) => { await Task.Delay(80); if (_active) await VerifyAsync(); }; }
        protected override void OnNavigatedTo(NavigationEventArgs args) { base.OnNavigatedTo(args); _active = true; _arguments = args.Parameter as string; SystemNavigationManager.GetForCurrentView().BackRequested += OnBack; }
        protected override void OnNavigatedFrom(NavigationEventArgs args) { _active = false; SystemNavigationManager.GetForCurrentView().BackRequested -= OnBack; base.OnNavigatedFrom(args); }
        private void OnBack(object sender, BackRequestedEventArgs args) { args.Handled = true; }
        private async void Unlock_Click(object sender, RoutedEventArgs args) { await VerifyAsync(); }
        private async Task VerifyAsync()
        {
            if (_verifying) return; _verifying = true; UnlockButton.IsEnabled = false; Progress.Visibility = Visibility.Visible; Progress.IsIndeterminate = true;
            try
            {
                bool verified = await AppAuthentication.VerifyAsync("LumINEを開くために本人確認してください。");
                if (!_active) return;
                if (!verified) { StatusText.Text = "本人確認できませんでした。ロックを解除するには再試行してください。"; return; }
                if (Frame.CanGoBack) { var frame = Frame; frame.GoBack(); frame.ForwardStack.Clear(); LumINE.Utils.NotificationNavigation.OpenPending(frame); return; }
                bool restored = await SessionManager.TryRestoreAsync(LumINE.Utils.NotificationNavigation.PendingAccountId);
                if (!_active) return;
                if (restored)
                {
                    // Leave the lock page before consuming the pending route.
                    var frame = Frame;
                    frame.Navigate(typeof(ChatListPage));
                    frame.BackStack.Clear();
                    LumINE.Utils.NotificationNavigation.OpenPending(frame);
                    return;
                }
                Frame.Navigate(restored ? typeof(ChatListPage) : typeof(LoginPage), _arguments); Frame.BackStack.Clear();
            }
            catch (Exception ex) { if (_active) StatusText.Text = "ロックを解除できませんでした: " + ex.Message; }
            finally { _verifying = false; UnlockButton.IsEnabled = true; Progress.IsIndeterminate = false; Progress.Visibility = Visibility.Collapsed; }
        }
    }
}
