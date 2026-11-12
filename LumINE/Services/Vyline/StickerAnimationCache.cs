using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.Web.Http;

namespace LumINE.Services.Vyline
{
    public static class StickerAnimationCache
    {
        private static readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);
        public static async Task<byte[]> GetAsync(string accountId, string stickerId, CancellationToken cancellation)
        {
            long id;
            if (!long.TryParse(stickerId, out id) || id <= 0) return null;
            await Gate.WaitAsync(cancellation);
            try
            {
                var accounts = await ApplicationData.Current.LocalFolder.CreateFolderAsync("accounts", CreationCollisionOption.OpenIfExists);
                var account = await accounts.CreateFolderAsync(accountId, CreationCollisionOption.OpenIfExists);
                var folder = await account.CreateFolderAsync("media", CreationCollisionOption.OpenIfExists);
                string name = "sticker-animation-v1-" + id;
                StorageFile file = null;
                try { file = await folder.GetFileAsync(name + ".png"); } catch (FileNotFoundException) { }
                if (file != null) return (await FileIO.ReadBufferAsync(file)).ToArray();
                try
                {
                    var absent = await folder.GetFileAsync(name + ".none");
                    if (DateTimeOffset.UtcNow - absent.DateCreated < TimeSpan.FromDays(1)) return null;
                }
                catch (FileNotFoundException) { }
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
                using (var client = new HttpClient())
                {
                    timeout.CancelAfter(15000);
                    var uri = new Uri("https://stickershop.line-scdn.net/stickershop/v1/sticker/" + id + "/iphone/sticker_animation@2x.png");
                    using (var response = await client.GetAsync(uri).AsTask(timeout.Token))
                    {
                        if (response.StatusCode == HttpStatusCode.NotFound)
                        {
                            await folder.CreateFileAsync(name + ".none", CreationCollisionOption.ReplaceExisting);
                            return null;
                        }
                        response.EnsureSuccessStatusCode();
                        if (response.Content.Headers.ContentLength > 8 * 1024 * 1024) throw new InvalidDataException("Sticker animation is too large.");
                        var buffer = await response.Content.ReadAsBufferAsync().AsTask(timeout.Token);
                        if (buffer.Length > 8 * 1024 * 1024) throw new InvalidDataException("Sticker animation is too large.");
                        byte[] bytes = buffer.ToArray();
                        // Validate before persisting, so an invalid CDN response is not cached.
                        if (await Task.Run(() => LumINE.Utils.ApngAnimation.Parse(bytes)) == null) return null;
                        cancellation.ThrowIfCancellationRequested();
                        file = await folder.CreateFileAsync(name + ".png", CreationCollisionOption.ReplaceExisting);
                        await FileIO.WriteBytesAsync(file, bytes);
                        return bytes;
                    }
                }
            }
            finally { Gate.Release(); }
        }
    }
}
