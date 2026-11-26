using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Data.Json;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.UI.Xaml;
using Windows.Web.Http;
using LumINE.Models;

namespace LumINE.Services.Vyline
{
    public static class StickerSoundPlayer
    {
        private static readonly SemaphoreSlim HistoryGate = new SemaphoreSlim(1, 1);
        private static readonly SemaphoreSlim DownloadGate = new SemaphoreSlim(1, 1);
        private static readonly Dictionary<string, StickerSoundHistory> Histories = new Dictionary<string, StickerSoundHistory>(StringComparer.Ordinal);
        private static MediaPlayer _player;
        private static MediaSource _source;
        private static CancellationTokenSource _pending;
        private static object _owner;
        private static Window _window;
        private static int _epoch;

        static StickerSoundPlayer()
        {
            AppSettings.Changed += key => { if (key == "PlayStickerSounds" && !AppSettings.PlayStickerSounds) Stop(); };
        }

        public static async Task PlayAsync(LineSession session, Message message, bool replay, object owner, Func<bool> visible = null)
        {
            int epoch = _epoch;
            try
            {
                string stickerId;
                if (session == null || message == null || !message.IsSticker || message.IsRevoked ||
                    message.ContentMetadata == null || !message.ContentMetadata.TryGetValue("STKID", out stickerId) || StickerSoundHistory.SoundUrl(stickerId) == null) return;
                if (!IsAllowed(session, visible)) return;
                // Also mark first visibility while muted, so enabling the setting
                // later doesn't unexpectedly replay an already viewed message.
                bool first = await MarkSeenAsync(session.AccountId, message.To ?? message.From, message.Id);
                if (epoch != _epoch || (!first && !replay) || !AppSettings.PlayStickerSounds || !IsAllowed(session, visible)) return;
                StopPlayback();
                var play = new CancellationTokenSource(); _pending = play; _owner = owner;
                try
                {
                    if (_window == null && Window.Current != null)
                    {
                        _window = Window.Current;
                        _window.VisibilityChanged += (s, e) => { if (!e.Visible) Stop(); };
                    }
                    var file = await GetSoundAsync(session.AccountId, stickerId, play.Token);
                    if (file == null || play.IsCancellationRequested || !object.ReferenceEquals(_pending, play) || !AppSettings.PlayStickerSounds || !IsAllowed(session, visible) || message.IsRevoked) return;
                    if (_player == null)
                    {
                        _player = new MediaPlayer { AutoPlay = false, IsLoopingEnabled = false, AudioCategory = MediaPlayerAudioCategory.SoundEffects };
                        _player.CommandManager.IsEnabled = false;
                        _player.MediaFailed += (s, e) => System.Diagnostics.Debug.WriteLine("[StickerSound] media failure: " + e.Error);
                    }
                    _source = MediaSource.CreateFromStorageFile(file);
                    _player.Source = _source;
                    _player.Play();
                }
                finally
                {
                    // Keep ownership after playback starts so unload/mute can
                    // stop it. A later play disposes this completed request.
                    if (!object.ReferenceEquals(_pending, play)) play.Dispose();
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[StickerSound] unavailable: " + ex.Message); }
        }

        private static bool IsAllowed(LineSession session, Func<bool> visible)
        {
            return object.ReferenceEquals(session, SessionManager.Current) && BackgroundNotifications.IsForeground &&
                (Window.Current == null || Window.Current.Visible) && (visible == null || visible());
        }

        public static void Stop(object owner = null)
        {
            if (owner != null && !object.ReferenceEquals(owner, _owner)) return;
            if (owner == null) _epoch++;
            StopPlayback();
        }

        private static void StopPlayback()
        {
            var previous = _pending; _pending = null; _owner = null;
            if (previous != null) { previous.Cancel(); previous.Dispose(); }
            if (_player != null) { _player.Pause(); _player.Source = null; }
            if (_source != null) { _source.Dispose(); _source = null; }
        }

        private static async Task<StorageFolder> FolderAsync(string account)
        {
            var accounts = await ApplicationData.Current.LocalFolder.CreateFolderAsync("accounts", CreationCollisionOption.OpenIfExists);
            return await (await accounts.CreateFolderAsync(account, CreationCollisionOption.OpenIfExists)).CreateFolderAsync("media", CreationCollisionOption.OpenIfExists);
        }

        private static async Task<bool> MarkSeenAsync(string account, string chat, string id)
        {
            await HistoryGate.WaitAsync();
            try
            {
                StickerSoundHistory history;
                var folder = await FolderAsync(account);
                if (!Histories.TryGetValue(account, out history))
                {
                    history = new StickerSoundHistory();
                    try
                    {
                        foreach (var value in JsonArray.Parse(await FileIO.ReadTextAsync(await folder.GetFileAsync("sticker-sound-seen.json")))) history.Seen.Add(value.GetString());
                    }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[StickerSound] seen load: " + ex.Message); }
                    Histories[account] = history;
                }
                if (!history.Observe(chat, id)) return false;
                var array = new JsonArray();
                foreach (string key in history.Seen) array.Add(JsonValue.CreateStringValue(key));
                try { await FileIO.WriteTextAsync(await folder.CreateFileAsync("sticker-sound-seen.json", CreationCollisionOption.ReplaceExisting), array.Stringify()); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[StickerSound] seen cache: " + ex.Message); }
                return true;
            }
            finally { HistoryGate.Release(); }
        }

        private static async Task<StorageFile> GetSoundAsync(string account, string stickerId, CancellationToken cancellation)
        {
            await DownloadGate.WaitAsync(cancellation);
            try
            {
                var folder = await FolderAsync(account);
                string name = "sticker-sound-v1-" + long.Parse(stickerId).ToString(System.Globalization.CultureInfo.InvariantCulture);
                try { return await folder.GetFileAsync(name + ".m4a"); } catch (FileNotFoundException) { }
                try
                {
                    var absent = await folder.GetFileAsync(name + ".none");
                    if (DateTimeOffset.UtcNow - absent.DateCreated < TimeSpan.FromDays(1)) return null;
                }
                catch (FileNotFoundException) { }
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
                using (var http = new HttpClient())
                {
                    timeout.CancelAfter(15000);
                    using (var response = await http.GetAsync(new Uri(StickerSoundHistory.SoundUrl(stickerId))).AsTask(timeout.Token))
                    {
                        if (response.StatusCode == HttpStatusCode.NotFound)
                        {
                            await folder.CreateFileAsync(name + ".none", CreationCollisionOption.ReplaceExisting);
                            return null;
                        }
                        response.EnsureSuccessStatusCode();
                        if (response.Content.Headers.ContentLength > 2 * 1024 * 1024) throw new InvalidDataException("Sticker sound is too large.");
                        var buffer = await response.Content.ReadAsBufferAsync().AsTask(timeout.Token);
                        if (buffer.Length > 2 * 1024 * 1024 || !StickerSoundHistory.IsM4a(buffer.ToArray())) throw new InvalidDataException("Invalid sticker sound.");
                        cancellation.ThrowIfCancellationRequested();
                        var file = await folder.CreateFileAsync(name + ".m4a", CreationCollisionOption.ReplaceExisting);
                        await FileIO.WriteBufferAsync(file, buffer);
                        return file;
                    }
                }
            }
            finally { DownloadGate.Release(); }
        }
    }
}
