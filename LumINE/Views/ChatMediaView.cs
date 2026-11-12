using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;
using LumINE.Models;
using LumINE.Services.Vyline;
using LumINE.Utils;
using LumINE.Services;

namespace LumINE.Views
{
    public sealed class ChatMediaView : UserControl
    {
        public event RoutedEventHandler PhotoTapped;
        public static readonly DependencyProperty SourceProperty = DependencyProperty.Register("Source", typeof(ImageSource), typeof(ChatMediaView), new PropertyMetadata(null, OnSourceChanged));
        public static readonly DependencyProperty MessageProperty = DependencyProperty.Register("Message", typeof(Message), typeof(ChatMediaView), new PropertyMetadata(null, OnMessageChanged));
        public ImageSource Source { get { return (ImageSource)GetValue(SourceProperty); } set { SetValue(SourceProperty, value); } }
        public Message Message { get { return (Message)GetValue(MessageProperty); } set { SetValue(MessageProperty, value); } }
        private readonly Image _image = new Image { Stretch = Stretch.Uniform };
        private CancellationTokenSource _play;
        private bool _loaded, _animating, _completed;
        private Window _window;

        public ChatMediaView()
        {
            Content = _image;
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
            Tapped += OnTapped;
            RegisterPropertyChangedCallback(VisibilityProperty, (s, p) => { if (Visibility == Visibility.Visible) Start(); else Stop(); });
        }
        private static void OnSourceChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
        {
            var view = (ChatMediaView)sender;
            if (!view._animating) view._image.Source = args.NewValue as ImageSource;
        }
        private static void OnMessageChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
        {
            var view = (ChatMediaView)sender;
            view.Stop(); view.Start();
        }
        private void OnLoaded(object sender, RoutedEventArgs args)
        {
            _loaded = true;
            AppSettings.Changed += OnSettingsChanged;
            _window = Window.Current;
            if (_window != null) _window.VisibilityChanged += OnWindowVisibilityChanged;
            Start();
        }
        private void OnUnloaded(object sender, RoutedEventArgs args)
        {
            _loaded = false;
            AppSettings.Changed -= OnSettingsChanged;
            if (_window != null) _window.VisibilityChanged -= OnWindowVisibilityChanged;
            _window = null;
            Stop();
        }
        private void OnWindowVisibilityChanged(object sender, Windows.UI.Core.VisibilityChangedEventArgs args)
        {
            if (args.Visible) Start(); else Stop();
        }
        private void OnSettingsChanged(string key) { if (key == "AnimateStickers") { Stop(); if (AppSettings.AnimateStickers) Start(); } }
        private void OnTapped(object sender, TappedRoutedEventArgs args)
        {
            if (Message != null && Message.IsImage && !Message.IsRevoked)
            {
                args.Handled = true;
                PhotoTapped?.Invoke(this, new RoutedEventArgs());
                return;
            }
            if (Message != null && Message.IsSticker && !Message.IsRevoked) { Stop(); Start(); }
        }
        private void Stop()
        {
            var previous = _play; _play = null;
            if (previous != null) previous.Cancel();
            _animating = false; _completed = false;
            _image.Source = Source;
        }
        private async void Start()
        {
            var message = Message;
            if (!AppSettings.AnimateStickers || !_loaded || Visibility != Visibility.Visible || _play != null || _completed || message == null || !message.IsSticker || message.IsRevoked || (_window != null && !_window.Visible)) return;
            var session = SessionManager.Current;
            string id;
            if (session == null || message.ContentMetadata == null || !message.ContentMetadata.TryGetValue("STKID", out id)) return;
            var play = new CancellationTokenSource(); _play = play;
            try
            {
                var preparation = PreparedStickerCache.GetAsync(session.AccountId, id);
                if (await Task.WhenAny(preparation, Task.Delay(-1, play.Token)) != preparation) return;
                var animation = await preparation;
                play.Token.ThrowIfCancellationRequested();
                if (animation == null) return;
                var animator = new XamlAnimatedPng.ApngAnimator(_image, animation.Width, animation.Height, animation.Frames, animation.Ends, animation.Plays);
                _animating = true;
                await animator.PlayAsync(play.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Debug.WriteLine("[StickerAnimation] static fallback: " + ex.Message); }
            finally
            {
                if (object.ReferenceEquals(_play, play)) { _play = null; _completed = true; _animating = false; _image.Source = Source; }
                play.Cancel(); play.Dispose();
            }
        }
    }
}
