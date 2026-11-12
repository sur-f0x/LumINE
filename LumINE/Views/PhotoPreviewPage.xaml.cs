using System;
using System.Collections.Generic;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Provider;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;
using LumINE.Models;
using LumINE.Services.Vyline;
using LumINE.ViewModels;

namespace LumINE.Views
{
    public sealed partial class PhotoPreviewPage : Page
    {
        private readonly BaseViewModel _progress = new BaseViewModel();
        private MessageMediaCache.CachedPhoto _photo;
        private Message _message;
        private bool _active;
        public PhotoPreviewPage() { InitializeComponent(); DataContext = _progress; }

        protected override async void OnNavigatedTo(NavigationEventArgs args)
        {
            base.OnNavigatedTo(args);
            _active = true; _message = args.Parameter as Message;
            SystemNavigationManager.GetForCurrentView().BackRequested += OnBackRequested;
            SystemNavigationManager.GetForCurrentView().AppViewBackButtonVisibility = AppViewBackButtonVisibility.Visible;
            if (_message == null) { StatusText.Text = "写真がありません。"; return; }
            PreviewImage.Source = _message.MediaImage;
            using (_progress.BeginBusy())
            {
                StatusText.Text = "写真を読み込み中…";
                try
                {
                    var photo = await MessageMediaCache.GetPhotoAsync(SessionManager.Current, _message);
                    if (!_active) return;
                    var image = new BitmapImage();
                    double scale = Math.Min(1.0, 2048.0 / Math.Max(photo.Width, photo.Height));
                    image.DecodePixelWidth = Math.Max(1, (int)(photo.Width * scale));
                    image.DecodePixelHeight = Math.Max(1, (int)(photo.Height * scale));
                    using (var stream = await photo.File.OpenAsync(FileAccessMode.Read)) await image.SetSourceAsync(stream);
                    if (!_active) return;
                    _photo = photo; PreviewImage.Source = image;
                    StatusText.Text = photo.IsOriginal ? "" : "元画像を取得できなかったため、キャッシュ画像を表示しています。保存される画像も同じ画質です。";
                    DownloadButton.IsEnabled = true;
                }
                catch (Exception ex) { if (_active) StatusText.Text = "写真を表示できませんでした: " + ex.Message; }
            }
        }
        protected override void OnNavigatedFrom(NavigationEventArgs args)
        {
            _active = false; _photo = null; PreviewImage.Source = null;
            SystemNavigationManager.GetForCurrentView().BackRequested -= OnBackRequested;
            base.OnNavigatedFrom(args);
        }
        private void BackButton_Click(object sender, RoutedEventArgs args) { if (Frame.CanGoBack) Frame.GoBack(); }
        private void OnBackRequested(object sender, BackRequestedEventArgs args)
        {
            if (Frame.CanGoBack) { args.Handled = true; Frame.GoBack(); }
        }
        private async void DownloadButton_Click(object sender, RoutedEventArgs args)
        {
            var photo = _photo;
            if (photo == null || !DownloadButton.IsEnabled) return;
            DownloadButton.IsEnabled = false;
            try
            {
                long id;
                string name = _message != null && long.TryParse(_message.Id, out id) ? id.ToString() : DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary, SuggestedFileName = "LINE_" + name };
                picker.FileTypeChoices.Add("画像", new List<string> { photo.Extension });
                var destination = await picker.PickSaveFileAsync();
                if (destination == null) return;
                using (_progress.BeginBusy())
                {
                    CachedFileManager.DeferUpdates(destination);
                    await photo.File.CopyAndReplaceAsync(destination);
                    var status = await CachedFileManager.CompleteUpdatesAsync(destination);
                    if (_active) StatusText.Text = status == FileUpdateStatus.Complete || status == FileUpdateStatus.CompleteAndRenamed ? "画像を保存しました。" : "画像の保存を完了できませんでした（" + status + "）。";
                }
            }
            catch (Exception ex) { if (_active) StatusText.Text = "画像を保存できませんでした: " + ex.Message; }
            finally { if (_active) DownloadButton.IsEnabled = true; }
        }
    }
}
