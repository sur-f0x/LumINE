using System;
using Windows.Storage;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace LumINE.Views
{
    public sealed partial class AttributionsPage : Page
    {
        public AttributionsPage() { InitializeComponent(); }
        protected override async void OnNavigatedTo(NavigationEventArgs args)
        {
            base.OnNavigatedTo(args);
            SystemNavigationManager.GetForCurrentView().BackRequested += OnBack;
            try
            {
                var file = await StorageFile.GetFileFromApplicationUriAsync(new Uri("ms-appx:///Assets/ThirdPartyNotices.txt"));
                LicenseText.Text = await FileIO.ReadTextAsync(file);
            }
            catch (Exception ex) { LicenseText.Text = "ライセンス表示を読み込めませんでした: " + ex.Message; }
        }
        protected override void OnNavigatedFrom(NavigationEventArgs args)
        {
            SystemNavigationManager.GetForCurrentView().BackRequested -= OnBack;
            base.OnNavigatedFrom(args);
        }
        private void Back_Click(object sender, RoutedEventArgs args) { if (Frame.CanGoBack) Frame.GoBack(); }
        private void OnBack(object sender, BackRequestedEventArgs args) { if (!args.Handled && Frame != null && Frame.CanGoBack) { args.Handled = true; Frame.GoBack(); } }
    }
}
