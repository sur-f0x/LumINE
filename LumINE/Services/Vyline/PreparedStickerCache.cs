using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using LumINE.Utils;

namespace LumINE.Services.Vyline
{
    public sealed class PreparedSticker
    {
        public int Width, Height, DurationMs;
        public uint Plays;
        public readonly List<byte[]> Frames = new List<byte[]>();
        public readonly List<int> Ends = new List<int>();
        public long Bytes { get { return (long)Width * Height * 4 * Frames.Count; } }
    }

    public static class PreparedStickerCache
    {
        private const long MemoryLimit = 24 * 1024 * 1024;
        private static readonly object Gate = new object();
        private static readonly SemaphoreSlim DecodeGate = new SemaphoreSlim(1, 1);
        private static readonly Dictionary<string, PreparedSticker> Cache = new Dictionary<string, PreparedSticker>();
        private static readonly Dictionary<string, Task<PreparedSticker>> Pending = new Dictionary<string, Task<PreparedSticker>>();
        private static readonly LinkedList<string> Recent = new LinkedList<string>();
        public static void Clear() { lock (Gate) { Cache.Clear(); Recent.Clear(); } }

        public static Task<PreparedSticker> GetAsync(string account, string id)
        {
            string key = account + ":" + id;
            lock (Gate)
            {
                PreparedSticker image;
                if (Cache.TryGetValue(key, out image)) { Recent.Remove(key); Recent.AddLast(key); return Task.FromResult(image); }
                Task<PreparedSticker> task;
                if (Pending.TryGetValue(key, out task)) return task;
                task = Task.Run(() => PrepareAsync(account, id));
                Pending[key] = task;
                // Observe faults even if all corresponding views were unloaded.
                var finish = task.ContinueWith(done =>
                {
                    lock (Gate)
                    {
                        Pending.Remove(key);
                        if (done.Status != TaskStatus.RanToCompletion) { var error = done.Exception; return; }
                        if (done.Result == null || !AppSettings.AnimateStickers) return;
                        Cache[key] = done.Result; Recent.AddLast(key);
                        while (Cache.Values.Sum(value => value.Bytes) > MemoryLimit && Recent.Count > 1)
                        { string oldest = Recent.First.Value; Recent.RemoveFirst(); Cache.Remove(oldest); }
                    }
                });
                return task;
            }
        }

        private static async Task<PreparedSticker> PrepareAsync(string account, string id)
        {
            await DecodeGate.WaitAsync();
            try
            {
                if (!AppSettings.AnimateStickers) return null;
                var bytes = await StickerAnimationCache.GetAsync(account, id, CancellationToken.None);
                if (bytes == null) return null;
                var animation = ApngAnimation.Parse(bytes);
                if (animation == null) return null;
                // Bound each complete animation to 8 MiB, independently of its
                // frame count; the shared LRU retains at most 24 MiB.
                double scale = Math.Min(1, Math.Min(360.0 / Math.Max(animation.Width, animation.Height),
                    Math.Sqrt(8.0 * 1024 * 1024 / (animation.Width * (double)animation.Height * 4 * animation.Frames.Count))));
                var prepared = new PreparedSticker { Width = Math.Max(1, (int)(animation.Width * scale)), Height = Math.Max(1, (int)(animation.Height * scale)), Plays = animation.Plays };
                var canvas = new ApngCanvas(animation.Width, animation.Height);
                foreach (var frame in animation.Frames)
                {
                    if (!AppSettings.AnimateStickers) return null;
                    using (var stream = new InMemoryRandomAccessStream())
                    {
                        await stream.WriteAsync(animation.FramePng(frame).AsBuffer()); stream.Seek(0);
                        var decoder = await BitmapDecoder.CreateAsync(BitmapDecoder.PngDecoderId, stream);
                        var pixels = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, new BitmapTransform(), ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
                        canvas.Render(frame, pixels.DetachPixelData());
                    }
                    var resized = new byte[prepared.Width * prepared.Height * 4];
                    for (int y = 0; y < prepared.Height; y++)
                    {
                        int sy = y * animation.Height / prepared.Height;
                        for (int x = 0; x < prepared.Width; x++)
                        {
                            int source = (sy * animation.Width + x * animation.Width / prepared.Width) * 4, target = (y * prepared.Width + x) * 4;
                            resized[target] = canvas.Pixels[source]; resized[target + 1] = canvas.Pixels[source + 1];
                            resized[target + 2] = canvas.Pixels[source + 2]; resized[target + 3] = canvas.Pixels[source + 3];
                        }
                    }
                    prepared.Frames.Add(resized); prepared.DurationMs += Math.Min(12000, frame.DelayMs); prepared.Ends.Add(prepared.DurationMs);
                }
                return prepared;
            }
            finally { DecodeGate.Release(); }
        }
    }
}
