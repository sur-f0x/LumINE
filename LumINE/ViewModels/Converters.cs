using System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Media;
using LumINE.Models;
using LumINE.Services.Vyline;

namespace LumINE.ViewModels
{
    public class BoolVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            return value is bool && (bool)value ? Visibility.Visible : Visibility.Collapsed;
        }
        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            return (value is Visibility) && (Visibility)value == Visibility.Visible;
        }
    }

    public class MessageAlignmentConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            var msg = value as Message;
            if (msg == null) return HorizontalAlignment.Right;
            if (msg.IsMine || msg.From == "me") return HorizontalAlignment.Right;
            var session = SessionManager.Current;
            if (session == null || string.IsNullOrEmpty(session.Mid)) return HorizontalAlignment.Right;
            return msg.From == session.Mid ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }

    public class MessageBackgroundConverter : IValueConverter
    {
        private static readonly SolidColorBrush MineBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x06, 0xC7, 0x55));
        private static readonly SolidColorBrush TheirsBrush = new SolidColorBrush(Windows.UI.Colors.White);

        public object Convert(object value, Type targetType, object parameter, string language)
        {
            var msg = value as Message;
            if (msg == null) return MineBrush;
            if (msg.IsRevoked || msg.ContentType == 1 || msg.ContentType == 7) return new SolidColorBrush(Windows.UI.Colors.Transparent);
            if (msg.IsMine || msg.From == "me") return MineBrush;
            var session = SessionManager.Current;
            if (session == null || string.IsNullOrEmpty(session.Mid)) return MineBrush;
            return msg.From == session.Mid ? MineBrush : TheirsBrush;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }

    public class MessageForegroundConverter : IValueConverter
    {
        private static readonly SolidColorBrush MineBrush = new SolidColorBrush(Windows.UI.Colors.White);
        private static readonly SolidColorBrush TheirsBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x22, 0x22, 0x22));

        public object Convert(object value, Type targetType, object parameter, string language)
        {
            var msg = value as Message;
            if (msg != null && msg.IsRevoked) return new SolidColorBrush(Windows.UI.Color.FromArgb(255, 136, 136, 136));
            if (msg != null && (msg.IsMine || msg.From == "me")) return MineBrush;
            var session = SessionManager.Current;
            return msg != null && session != null && msg.From == session.Mid ? MineBrush : TheirsBrush;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
