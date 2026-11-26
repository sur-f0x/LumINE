using System;
using System.Collections.Generic;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;
using LumINE.Services.Vyline;
using LumINE.Views;

namespace LumINE.Utils
{
    public sealed class NotificationRoute
    {
        public string AccountId { get; private set; }
        public string ChatId { get; private set; }
        public static string Encode(string accountId, string chatId)
        {
            return "action=openChat&account=" + Uri.EscapeDataString(accountId) + "&chat=" + Uri.EscapeDataString(chatId);
        }
        public static NotificationRoute Parse(string arguments)
        {
            if (string.IsNullOrEmpty(arguments) || arguments.Length > 2048) return null;
            try
            {
                var fields = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var part in arguments.Split('&'))
                {
                    int equals = part.IndexOf('=');
                    if (equals <= 0) return null;
                    fields.Add(Uri.UnescapeDataString(part.Substring(0, equals)), Uri.UnescapeDataString(part.Substring(equals + 1)));
                }
                string action, account, chat;
                if (!fields.TryGetValue("action", out action) || action != "openChat" ||
                    !fields.TryGetValue("account", out account) || string.IsNullOrWhiteSpace(account) || account.Length > 256 ||
                    !fields.TryGetValue("chat", out chat) || chat.Length < 2 || chat.Length > 128 ||
                    (chat[0] != 'u' && chat[0] != 'c' && chat[0] != 'r')) return null;
                foreach (char c in account) if (char.IsControl(c) || c == '/' || c == '\\') return null;
                foreach (char c in chat) if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9'))) return null;
                return new NotificationRoute { AccountId = account, ChatId = chat };
            }
            catch (ArgumentException) { return null; }
            catch (UriFormatException) { return null; }
        }
    }

    // Pending navigation survives session restore / Windows Hello. Only the UI
    // thread consumes it, after authentication; no tokens are in toast arguments.
    public static class NotificationNavigation
    {
        private static NotificationRoute _pending;
        public static string PendingAccountId { get { return _pending == null ? null : _pending.AccountId; } }
        public static void Receive(string arguments) { _pending = NotificationRoute.Parse(arguments); }
        public static bool OpenPending(Frame frame)
        {
            var route = _pending;
            if (route == null || !SessionManager.IsLoggedIn || frame.Content is UnlockPage) return false;
            _pending = null;
            if (!string.Equals(SessionManager.Current.AccountId, route.AccountId, StringComparison.Ordinal)) return false;
            if (!(frame.Content is ChatListPage) && !frame.Navigate(typeof(ChatListPage), null)) return false;
            // Back always leads to the list, including a cold launch or a tap
            // while a different conversation/settings page was already open.
            frame.BackStack.Clear();
            ((ChatListPage)frame.Content).OpenChat(route.ChatId);
            return true;
        }
    }
}
