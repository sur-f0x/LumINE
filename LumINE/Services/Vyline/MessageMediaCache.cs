using System;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading.Tasks;
using Windows.Data.Json;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.UI.Xaml.Media.Imaging;
using Windows.Web.Http;
using Windows.Web.Http.Headers;
using Windows.Graphics.Imaging;

namespace LumINE.Services.Vyline
{
    /// <summary>Per-account disk cache and transfer helpers for chat photos and sticker art.</summary>
    public static class MessageMediaCache
    {
        // OBS media is served from LINE's gateway host, not the Talk RPC host.
        private const string Gateway = "https://gwz.line.naver.jp";

        public sealed class CachedPhoto
        {
            public StorageFile File { get; set; }
            public bool IsOriginal { get; set; }
            public string Extension { get; set; }
            public uint Width { get; set; }
            public uint Height { get; set; }
        }

        public static async Task<CachedPhoto> GetPhotoAsync(LineSession session, LumINE.Models.Message message)
        {
            if (session == null || message == null || !message.IsImage || message.IsRevoked) throw new InvalidOperationException("表示できる写真がありません。");
            var folder = await GetMediaFolderAsync(session.AccountId);
            string originalName = "message-original-v1-" + Sanitize(message.Id) + ".img";
            StorageFile original = null;
            try { original = await folder.GetFileAsync(originalName); } catch (FileNotFoundException) { }
            if (original != null)
            {
                try { return await ReadPhotoInfoAsync(original, true); }
                catch { try { await original.DeleteAsync(); } catch { } }
            }
            Exception failure;
            try
            {
                if (string.IsNullOrEmpty(message.MediaKeyMaterial) && message.Chunks != null && message.Chunks.Count > 0)
                    await session.Line.DecryptMessagesAsync(new System.Collections.Generic.List<LumINE.Models.Message> { message });
                var bytes = await DownloadMessageImageAsync(session, message, 1);
                using (var stream = new InMemoryRandomAccessStream())
                {
                    await stream.WriteAsync(bytes.AsBuffer()); stream.Seek(0);
                    await BitmapDecoder.CreateAsync(stream);
                }
                original = await folder.CreateFileAsync(originalName, CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteBytesAsync(original, bytes);
                return await ReadPhotoInfoAsync(original, true);
            }
            catch (Exception ex) { failure = ex; System.Diagnostics.Debug.WriteLine("[PhotoPreview] original unavailable: " + ex.Message); }
            try { return await ReadPhotoInfoAsync(await folder.GetFileAsync("message-v2-" + Sanitize(message.Id) + ".img"), false); }
            catch (Exception) { throw new InvalidOperationException("元画像とキャッシュ画像を取得できませんでした。", failure); }
        }

        private static async Task<CachedPhoto> ReadPhotoInfoAsync(StorageFile file, bool original)
        {
            using (var stream = await file.OpenAsync(FileAccessMode.Read))
            {
                var decoder = await BitmapDecoder.CreateAsync(stream);
                Guid codec = decoder.DecoderInformation.CodecId;
                string extension = codec == BitmapDecoder.JpegDecoderId ? ".jpg" : codec == BitmapDecoder.GifDecoderId ? ".gif" :
                    codec == BitmapDecoder.BmpDecoderId ? ".bmp" : codec == BitmapDecoder.TiffDecoderId ? ".tif" : codec == BitmapDecoder.JpegXRDecoderId ? ".jxr" : ".png";
                return new CachedPhoto { File = file, IsOriginal = original, Extension = extension, Width = decoder.PixelWidth, Height = decoder.PixelHeight };
            }
        }

        public static async Task<BitmapImage> GetImageAsync(LineSession session, LumINE.Models.Message message)
        {
            if (session == null || message == null) return null;
            bool sticker = message.ContentType == 7;
            if (!sticker && message.ContentType != 1) return null;
            string stickerId = Metadata(message, "STKID");
            if (sticker && string.IsNullOrEmpty(stickerId)) return null;
            string cacheName = sticker ? "sticker-v2-" + Sanitize(stickerId) + ".png" : "message-v2-" + Sanitize(message.Id) + ".img";
            // BitmapImage is a XAML/COM object. Keep this continuation on the
            // caller's UI dispatcher through SetSourceAsync, otherwise UWP can
            // report "The application called an interface that was marshaled...".
            var folder = await GetMediaFolderAsync(session.AccountId);
            StorageFile file = null;
            try { file = await folder.GetFileAsync(cacheName); } catch (FileNotFoundException) { }
            Exception failure = null;
            for (int attempt = 0; attempt < (sticker ? 2 : 6); attempt++)
            {
                try
                {
                    if (file == null)
                    {
                        byte[] bytes = sticker ? await DownloadStickerAsync(stickerId, attempt != 0)
                            : await DownloadMessageImageAsync(session, message, attempt);
                        if (bytes == null || bytes.Length < 12) throw new InvalidDataException("画像データが空です。");
                        file = await folder.CreateFileAsync(cacheName, CreationCollisionOption.ReplaceExisting);
                        await FileIO.WriteBytesAsync(file, bytes);
                    }
                    var image = new BitmapImage { DecodePixelWidth = 640 };
                    using (IRandomAccessStream stream = await file.OpenAsync(FileAccessMode.Read))
                        await image.SetSourceAsync(stream);
                    return image;
                }
                catch (Exception ex)
                {
                    failure = ex;
                    System.Diagnostics.Debug.WriteLine("[Media] decode/download attempt=" + attempt + " type=" + message.ContentType + " error=" + ex.Message);
                    if (file != null) try { await file.DeleteAsync(); } catch { }
                    file = null;
                }
            }
            throw new InvalidOperationException("この画像を表示できませんでした。" + (failure == null ? "" : " " + failure.Message), failure);
        }

        public static async Task<LumINE.Models.Message> SendImageAsync(LineSession session, string chatMid, byte[] bytes, string fileName)
        {
            if (session == null || bytes == null || bytes.Length == 0) throw new ArgumentException("写真データがありません。");
            if (chatMid.StartsWith("u", StringComparison.Ordinal))
            {
                var negotiation = await session.Talk.NegotiateE2eePublicKeyAsync(chatMid).ConfigureAwait(false);
                object spec;
                if (negotiation.TryGetValue(1, out spec) && spec != null && spec.ToString() == "-1")
                    return await SendPlainImageAsync(session, chatMid, bytes, fileName).ConfigureAwait(false);
                var peer = E2eeKeyStore.ExtractPeerKey(negotiation);
                if (peer == null) throw new InvalidOperationException("写真送信先のE2EE公開鍵を取得できませんでした。");
                await E2eeKeyStore.SavePeerKeyAsync(session.AccountId, chatMid, peer).ConfigureAwait(false);
            }
            byte[] keyMaterial = LumINE.Utils.CryptoHelper.RandomBytes(32);
            byte[] encrypted = LumINE.Utils.E2ee.EncryptMediaKeyMaterial(bytes, keyMaterial);
            string name = string.IsNullOrWhiteSpace(fileName) ? "image.jpg" : Path.GetFileName(fileName);
            var parameters = new JsonObject();
            parameters["type"] = JsonValue.CreateStringValue("file");
            parameters["ver"] = JsonValue.CreateStringValue("2.0");
            parameters["name"] = JsonValue.CreateStringValue(name);
            string oid = await UploadImageObjectAsync(session, "emi/reqid-" + Guid.NewGuid().ToString("N"), encrypted, parameters).ConfigureAwait(false);
            string previewOid = await UploadImageObjectAsync(session, "emi/" + Uri.EscapeDataString(oid) + "__ud-preview", encrypted, parameters).ConfigureAwait(false);
            if (previewOid != oid && previewOid != oid + "__ud-preview") throw new InvalidOperationException("画像プレビューの送信確認が元画像と一致しません。");
            var payload = new JsonObject();
            payload["keyMaterial"] = JsonValue.CreateStringValue(Convert.ToBase64String(keyMaterial));
            payload["fileName"] = JsonValue.CreateStringValue(name);
            var contentInfo = new JsonObject();
            contentInfo["category"] = JsonValue.CreateStringValue("original");
            contentInfo["fileSize"] = JsonValue.CreateNumberValue(encrypted.Length);
            contentInfo["extension"] = JsonValue.CreateStringValue(Path.GetExtension(name).TrimStart('.').ToLowerInvariant());
            contentInfo["animated"] = JsonValue.CreateBooleanValue(Path.GetExtension(name).Equals(".gif", StringComparison.OrdinalIgnoreCase));
            var metadata = new System.Collections.Generic.Dictionary<string, string>
            {
                { "SID", "emi" }, { "OID", oid }, { "FILE_SIZE", encrypted.Length.ToString() }, { "MEDIA_CONTENT_INFO", contentInfo.Stringify() }
            };
            var response = await session.Line.SendMediaMessageAsync(chatMid, payload.Stringify(), metadata).ConfigureAwait(false);
            var sent = ChatStore.MapMessage(response, chatMid);
            if (sent == null || string.IsNullOrEmpty(sent.Id)) throw new InvalidOperationException("画像メッセージの送信確認を取得できませんでした。");
            sent.From = session.Mid; sent.To = chatMid; sent.ContentType = 1;
            sent.CreatedTime = sent.CreatedTime ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            sent.ContentMetadata = sent.ContentMetadata ?? new System.Collections.Generic.Dictionary<string, string>();
            foreach (var pair in metadata) sent.ContentMetadata[pair.Key] = pair.Value;
            sent.MediaKeyMaterial = Convert.ToBase64String(keyMaterial);
            await CacheSentPhotoAsync(session, chatMid, sent, bytes).ConfigureAwait(false);
            return sent;
        }

        private static async Task<string> UploadImageObjectAsync(LineSession session, string path, byte[] bytes, JsonObject parameters)
        {
            using (var request = new HttpRequestMessage(HttpMethod.Post, new Uri(Gateway + "/oa/r/talk/" + path)))
            {
                Append(request, "X-LHM", "POST"); Append(request, "X-LPV", "1");
                Append(request, "X-Line-Application", DesktopIdentity.ApplicationHeader); Append(request, "User-Agent", DesktopIdentity.UserAgent);
                Append(request, "X-Line-Access", await session.Talk.GetObsAccessTokenAsync().ConfigureAwait(false));
                Append(request, "X-Line-Mid", session.Mid); Append(request, "x-lal", DesktopIdentity.Lal);
                Append(request, "X-Obs-Params", Convert.ToBase64String(Encoding.UTF8.GetBytes(parameters.Stringify())));
                request.Content = new HttpBufferContent(bytes.AsBuffer());
                request.Content.Headers.ContentType = new HttpMediaTypeHeaderValue("application/octet-stream");
                using (var client = new HttpClient())
                using (var timeout = new System.Threading.CancellationTokenSource(30000))
                using (var response = await client.SendRequestAsync(request).AsTask(timeout.Token).ConfigureAwait(false))
                {
                    if ((int)response.StatusCode != 200 && (int)response.StatusCode != 201)
                        throw new InvalidOperationException("画像アップロードが拒否されました (HTTP " + (int)response.StatusCode + ")。");
                    string oid;
                    if (!response.Headers.TryGetValue("x-obs-oid", out oid) || string.IsNullOrEmpty(oid))
                        throw new InvalidOperationException("画像アップロードのオブジェクトIDを取得できませんでした。");
                    return oid;
                }
            }
        }

        private static async Task CacheSentPhotoAsync(LineSession session, string chatMid, LumINE.Models.Message sent, byte[] bytes)
        {
            // An acknowledged send remains successful if disk caching fails.
            session.Store.UpsertMessage(chatMid, sent);
            try
            {
                var folder = await GetMediaFolderAsync(session.AccountId);
                var file = await folder.CreateFileAsync("message-v2-" + Sanitize(sent.Id) + ".img", CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteBytesAsync(file, bytes);
                var original = await folder.CreateFileAsync("message-original-v1-" + Sanitize(sent.Id) + ".img", CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteBytesAsync(original, bytes);
                await session.Store.SaveMessagesAsync(chatMid).ConfigureAwait(false);
                await session.Store.SaveChatActivityAsync().ConfigureAwait(false);
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Media] sent cache: " + ex.Message); }
        }

        private static async Task<LumINE.Models.Message> SendPlainImageAsync(LineSession session, string chatMid, byte[] bytes, string fileName)
        {
            if (session == null || bytes == null || bytes.Length == 0) throw new ArgumentException("写真データがありません。");
            string token = await session.Talk.GetObsAccessTokenAsync().ConfigureAwait(false);
            int reqseq = session.GetReqseq();
            var parameters = new JsonObject();
            parameters["type"] = JsonValue.CreateStringValue("image");
            parameters["ver"] = JsonValue.CreateStringValue("2.0");
            parameters["name"] = JsonValue.CreateStringValue(string.IsNullOrWhiteSpace(fileName) ? "image.jpg" : Path.GetFileName(fileName));
            parameters["oid"] = JsonValue.CreateStringValue("reqseq");
            parameters["reqseq"] = JsonValue.CreateStringValue(reqseq.ToString());
            parameters["tomid"] = JsonValue.CreateStringValue(chatMid);
            parameters["cat"] = JsonValue.CreateStringValue("original");
            string obsHeader = Convert.ToBase64String(Encoding.UTF8.GetBytes(parameters.Stringify()));

            var request = new HttpRequestMessage(HttpMethod.Post, new Uri(Gateway + "/oa/r/talk/m/reqseq"));
            Append(request, "X-LHM", "POST");
            Append(request, "X-LPV", "1");
            Append(request, "X-Line-Application", DesktopIdentity.ApplicationHeader);
            Append(request, "User-Agent", DesktopIdentity.UserAgent);
            Append(request, "X-Line-Access", token);
            Append(request, "X-Line-Mid", session.Mid ?? "");
            Append(request, "x-lal", DesktopIdentity.Lal);
            Append(request, "X-Obs-Params", obsHeader);
            request.Content = new HttpBufferContent(bytes.AsBuffer());
            request.Content.Headers.ContentType = new HttpMediaTypeHeaderValue("application/octet-stream");
            using (var client = new HttpClient())
            using (var response = await client.SendRequestAsync(request))
            {
                int status = (int)response.StatusCode;
                if (status != 200 && status != 201)
                {
                    string detail = "";
                    try { detail = await response.Content.ReadAsStringAsync(); } catch { }
                    throw new InvalidOperationException("写真のアップロードが拒否されました (HTTP " + status + "): " + detail);
                }
                string oid;
                if (!response.Headers.TryGetValue("x-obs-oid", out oid) || string.IsNullOrEmpty(oid))
                    throw new InvalidOperationException("画像の送信確認を取得できませんでした。");
                var sent = new LumINE.Models.Message { Id = oid, From = session.Mid, To = chatMid, ContentType = 1,
                    CreatedTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
                await CacheSentPhotoAsync(session, chatMid, sent, bytes).ConfigureAwait(false);
                return sent;
            }
        }

        private static async Task<byte[]> DownloadMessageImageAsync(LineSession session, LumINE.Models.Message message, int variant)
        {
            bool directObs = variant >= 3;
            variant %= 3;
            bool original = variant != 0;
            if (string.IsNullOrEmpty(message.Id)) throw new InvalidOperationException("画像メッセージ ID がありません。");
            bool encryptedMessage = message.Chunks != null && message.Chunks.Count > 0;
            if (encryptedMessage && string.IsNullOrEmpty(message.MediaKeyMaterial))
                await session.Line.DecryptMessagesAsync(new System.Collections.Generic.List<LumINE.Models.Message> { message }).ConfigureAwait(false);
            if (encryptedMessage && string.IsNullOrEmpty(message.MediaKeyMaterial))
                throw new InvalidOperationException("画像メッセージのE2EE鍵を取得できませんでした。鍵が失われた過去の画像は表示できません。");
            string token = directObs ? session.AuthToken : await session.Talk.GetObsAccessTokenAsync().ConfigureAwait(false);
            string oid = Metadata(message, "OID");
            string sid = Metadata(message, "SID");
            bool sealedMedia = message.Chunks != null && message.Chunks.Count > 0 && !string.IsNullOrEmpty(oid) && !string.IsNullOrEmpty(sid);
            string root = directObs ? "https://obs.line-apps.com/r/talk/" : Gateway + "/oa/r/talk/";
            string url = sealedMedia
                ? root + Uri.EscapeDataString(sid) + "/" + Uri.EscapeDataString(original ? oid : oid + "__ud-preview")
                : root + "m/" + Uri.EscapeDataString(message.Id) + (variant == 2 ? "/w800" : original ? "" : "/preview");
            // Official accounts can supply their media on a separate HTTPS CDN.
            // Never forward the account's authentication headers to that CDN.
            Uri supplied;
            string metadataUrl = Metadata(message, original ? "DOWNLOAD_URL" : "PREVIEW_URL");
            if (!sealedMedia && variant < 2 && Uri.TryCreate(metadataUrl, UriKind.Absolute, out supplied) && supplied.Scheme == "https") url = supplied.AbsoluteUri;
            var request = new HttpRequestMessage(HttpMethod.Get, new Uri(url));
            Append(request, "Accept", "image/jpeg,image/png,image/gif;q=0.9,*/*;q=0.1");
            Append(request, "X-LHM", "GET"); Append(request, "X-LPV", "1");
            Append(request, "X-Line-Application", DesktopIdentity.ApplicationHeader);
            Append(request, "User-Agent", DesktopIdentity.UserAgent);
            if (new Uri(url).Host.Equals(new Uri(Gateway).Host, StringComparison.OrdinalIgnoreCase) || new Uri(url).Host.Equals("obs.line-apps.com", StringComparison.OrdinalIgnoreCase))
            {
                Append(request, "X-Line-Access", new Uri(url).Host.Equals("obs.line-apps.com", StringComparison.OrdinalIgnoreCase) ? session.AuthToken : token);
                Append(request, "X-Line-Mid", session.Mid ?? "");
            }
            Append(request, "x-lal", DesktopIdentity.Lal);
            if (sealedMedia)
            {
                var talkFields = new System.Collections.Generic.List<LumINE.Utils.ThriftField>
                {
                    new LumINE.Utils.ThriftField(LumINE.Utils.ThriftTypes.String, 4, message.Id),
                    new LumINE.Utils.ThriftField(LumINE.Utils.ThriftTypes.List, 27,
                        new LumINE.Utils.ThriftListValue(LumINE.Utils.ThriftTypes.Struct, new System.Collections.Generic.List<object>()))
                };
                string inner = Convert.ToBase64String(LumINE.Utils.ThriftBinary.PackBinaryStruct(talkFields));
                string json = "{\"message\":\"" + inner + "\"}";
                Append(request, "X-Talk-Meta", Convert.ToBase64String(Encoding.UTF8.GetBytes(json)));
            }
            using (var client = new HttpClient())
            using (var timeout = new System.Threading.CancellationTokenSource(20000))
            using (var response = await client.SendRequestAsync(request).AsTask(timeout.Token))
            {
                if ((int)response.StatusCode != 200)
                {
                    string detail = "";
                    try { detail = await response.Content.ReadAsStringAsync(); } catch { }
                    throw new InvalidOperationException("写真を取得できませんでした (HTTP " + (int)response.StatusCode + "): " + detail);
                }
                var buffer = await response.Content.ReadAsBufferAsync().AsTask(timeout.Token);
                var data = new byte[(int)buffer.Length];
                DataReader.FromBuffer(buffer).ReadBytes(data);
                System.Diagnostics.Debug.WriteLine("[Media] response original=" + original + " bytes=" + data.Length + " type=" + response.Content.Headers.ContentType);
                if (encryptedMessage)
                {
                    if (string.IsNullOrEmpty(message.MediaKeyMaterial))
                        throw new InvalidOperationException("暗号化された写真の復号キーを取得できませんでした。");
                    return LumINE.Utils.E2ee.DecryptMediaKeyMaterial(data, message.MediaKeyMaterial);
                }
                return data;
            }
        }

        private static async Task<byte[]> DownloadStickerAsync(string stickerId, bool alternate)
        {
            string url = "https://stickershop.line-scdn.net/stickershop/v1/sticker/" +
                Uri.EscapeDataString(stickerId) + (alternate ? "/iphone/sticker@2x.png" : "/android/sticker.png");
            using (var client = new HttpClient())
            using (var response = await client.GetAsync(new Uri(url)))
            {
                response.EnsureSuccessStatusCode();
                var buffer = await response.Content.ReadAsBufferAsync();
                var data = new byte[(int)buffer.Length];
                DataReader.FromBuffer(buffer).ReadBytes(data);
                return data;
            }
        }

        private static async Task<StorageFolder> GetMediaFolderAsync(string accountId)
        {
            var accounts = await ApplicationData.Current.LocalFolder.CreateFolderAsync("accounts", CreationCollisionOption.OpenIfExists);
            var account = await accounts.CreateFolderAsync(accountId, CreationCollisionOption.OpenIfExists);
            return await account.CreateFolderAsync("media", CreationCollisionOption.OpenIfExists);
        }

        private static string Metadata(LumINE.Models.Message message, string name)
        {
            string value;
            return message.ContentMetadata != null && message.ContentMetadata.TryGetValue(name, out value) ? value : null;
        }

        private static void Append(HttpRequestMessage request, string name, string value)
        {
            request.Headers.TryAppendWithoutValidation(name, value ?? "");
        }

        private static string Sanitize(string value)
        {
            if (string.IsNullOrEmpty(value)) return "unknown";
            foreach (char c in Path.GetInvalidFileNameChars()) value = value.Replace(c, '_');
            return value.Replace(':', '_').Replace('/', '_').Replace('\\', '_');
        }
    }
}
