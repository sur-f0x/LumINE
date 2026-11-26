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
        private Message _soundCheckedFor;

        public ChatMediaView()
        {
            Content = _image;
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
            Tapped += OnTapped;
            LayoutUpdated += (s, e) => TryAutoSound();
            RegisterPropertyChangedCallback(VisibilityProperty, (s, p) => { if (Visibility == Visibility.Visible) { Start(); TryAutoSound(); } else { Stop(); StickerSoundPlayer.Stop(this); } });
        }
        private static void OnSourceChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
        {
            var view = (ChatMediaView)sender;
            if (!view._animating) view._image.Source = args.NewValue as ImageSource;
            view.TryAutoSound();
        }
        private static void OnMessageChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
        {
            var view = (ChatMediaView)sender;
            view._soundCheckedFor = null;
            view.Stop(); view.Start();
            view.TryAutoSound();
        }
        private void OnLoaded(object sender, RoutedEventArgs args)
        {
            _loaded = true;
            AppSettings.Changed += OnSettingsChanged;
            _window = Window.Current;
            if (_window != null) _window.VisibilityChanged += OnWindowVisibilityChanged;
            Start();
            TryAutoSound();
        }
        private void OnUnloaded(object sender, RoutedEventArgs args)
        {
            _loaded = false;
            AppSettings.Changed -= OnSettingsChanged;
            if (_window != null) _window.VisibilityChanged -= OnWindowVisibilityChanged;
            _window = null;
            Stop();
            StickerSoundPlayer.Stop(this);
        }
        private void OnWindowVisibilityChanged(object sender, Windows.UI.Core.VisibilityChangedEventArgs args)
        {
            if (args.Visible) { Start(); TryAutoSound(); } else { Stop(); StickerSoundPlayer.Stop(this); }
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
            if (Message != null && Message.IsSticker && !Message.IsRevoked)
            {
                args.Handled = true;
                Stop(); Start();
                var message = Message;
                _soundCheckedFor = message;
                _ = StickerSoundPlayer.PlayAsync(SessionManager.Current, message, true, this, () => object.ReferenceEquals(Message, message) && IsVisibleInViewport());
            }
        }
        private void TryAutoSound()
        {
            var message = Message;
            if (message == null || !message.IsSticker || message.IsRevoked || object.ReferenceEquals(_soundCheckedFor, message) || _image.Source == null || !IsVisibleInViewport()) return;
            _soundCheckedFor = message;
            _ = StickerSoundPlayer.PlayAsync(SessionManager.Current, message, false, this, () => object.ReferenceEquals(Message, message) && IsVisibleInViewport());
        }
        private bool IsVisibleInViewport()
        {
            if (!_loaded || ActualWidth <= 0 || ActualHeight <= 0 || (_window != null && !_window.Visible)) return false;
            DependencyObject ancestor = this;
            ScrollViewer viewport = null;
            while (ancestor != null)
            {
                var element = ancestor as UIElement;
                if (element != null && element.Visibility != Visibility.Visible) return false;
                if (viewport == null) viewport = ancestor as ScrollViewer;
                ancestor = VisualTreeHelper.GetParent(ancestor);
            }
            if (viewport == null || viewport.ActualWidth <= 0 || viewport.ActualHeight <= 0) return false;
            try
            {
                var bounds = TransformToVisual(viewport).TransformBounds(new Windows.Foundation.Rect(0, 0, ActualWidth, ActualHeight));
                return bounds.Right > 0 && bounds.Bottom > 0 && bounds.X < viewport.ActualWidth && bounds.Y < viewport.ActualHeight;
            }
            catch (InvalidOperationException) { return false; }
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
