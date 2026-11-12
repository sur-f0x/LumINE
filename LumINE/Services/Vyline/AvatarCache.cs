using System;
using System.Text;
using System.Threading.Tasks;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Security.Cryptography;
using Windows.Security.Cryptography.Core;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.UI.Xaml.Media.Imaging;
using Windows.Web.Http;

namespace LumINE.Services.Vyline
{
    /// <summary>Downloads profile images once and keeps them in the per-account local cache.</summary>
    public static class AvatarCache
    {
        private const string Cdn = "https://profile.line-scdn.net/";

        public static async Task<BitmapImage> GetAsync(string accountId, string picturePath)
        {
            var file = await GetFileAsync(accountId, picturePath);
            if (file == null) return null;
            var image = new BitmapImage();
            using (IRandomAccessStream stream = await file.OpenAsync(FileAccessMode.Read).AsTask())
                await image.SetSourceAsync(stream).AsTask();
            return image;
        }

        // No XAML objects: safe to call from the notification background worker.
        public static async Task<StorageFile> GetFileAsync(string accountId, string picturePath)
        {
            if (string.IsNullOrWhiteSpace(picturePath)) return null;
            string url = picturePath.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                         picturePath.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                ? picturePath
                : Cdn + picturePath.TrimStart('/');
            if (!url.EndsWith("/preview", StringComparison.OrdinalIgnoreCase))
                url += "/preview";
            string fileName = HashName(url) + ".img";
            StorageFolder accountFolder = await GetAccountFolderAsync(accountId);
            StorageFolder avatarFolder = await accountFolder.CreateFolderAsync(
                "avatars", CreationCollisionOption.OpenIfExists).AsTask();

            StorageFile file = null;
            try { file = await avatarFolder.GetFileAsync(fileName).AsTask(); }
            catch { }

            if (file == null)
            {
                using (var client = new HttpClient())
                {
                    using (var timeout = new System.Threading.CancellationTokenSource(5000))
                    using (var response = await client.GetAsync(new Uri(url)).AsTask(timeout.Token))
                    {
                    response.EnsureSuccessStatusCode();
                    var buffer = await response.Content.ReadAsBufferAsync().AsTask(timeout.Token);
                    file = await avatarFolder.CreateFileAsync(fileName, CreationCollisionOption.ReplaceExisting)
                        .AsTask();
                    await FileIO.WriteBufferAsync(file, buffer).AsTask();
                    }
                }
            }

            return file;
        }

        public static async Task<string> GetNotificationUriAsync(string accountId, string picturePath)
        {
            var source = await GetFileAsync(accountId, picturePath);
            if (source == null) return null;
            var folder = await source.GetParentAsync();
            string name = System.IO.Path.GetFileNameWithoutExtension(source.Name) + ".png";
            StorageFile png = null;
            try { png = await folder.GetFileAsync(name); } catch (System.IO.FileNotFoundException) { }
            if (png == null)
            {
                using (var input = await source.OpenAsync(FileAccessMode.Read))
                {
                    var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(input);
                    var pixels = await decoder.GetPixelDataAsync(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
                        new Windows.Graphics.Imaging.BitmapTransform { ScaledWidth = 96, ScaledHeight = 96 }, Windows.Graphics.Imaging.ExifOrientationMode.IgnoreExifOrientation, Windows.Graphics.Imaging.ColorManagementMode.DoNotColorManage);
                    png = await folder.CreateFileAsync(name, CreationCollisionOption.ReplaceExisting);
                    using (var output = await png.OpenAsync(FileAccessMode.ReadWrite))
                    {
                        var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, output);
                        encoder.SetPixelData(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied, 96, 96, 96, 96, pixels.DetachPixelData());
                        await encoder.FlushAsync();
                    }
                }
            }
            return "ms-appdata:///local/accounts/" + Uri.EscapeDataString(accountId) + "/avatars/" + png.Name;
        }

        private static async Task<StorageFolder> GetAccountFolderAsync(string accountId)
        {
            StorageFolder accounts = await ApplicationData.Current.LocalFolder.CreateFolderAsync(
                "accounts", CreationCollisionOption.OpenIfExists).AsTask();
            return await accounts.CreateFolderAsync(accountId, CreationCollisionOption.OpenIfExists)
                .AsTask();
        }

        private static string HashName(string value)
        {
            var provider = HashAlgorithmProvider.OpenAlgorithm(HashAlgorithmNames.Sha256);
            var hash = provider.HashData(CryptographicBuffer.ConvertStringToBinary(value, BinaryStringEncoding.Utf8));
            byte[] bytes = new byte[(int)hash.Length];
            DataReader.FromBuffer(hash).ReadBytes(bytes);
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (byte b in bytes) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
    }
}
