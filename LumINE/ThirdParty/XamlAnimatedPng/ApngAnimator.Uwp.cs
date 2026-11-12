// UWP APNG adapter for xupefei/XamlAnimatedPng (Apache-2.0).
// Uses the upstream TimingManager and Animator.cs WinRT buffer-copy rendering
// path. APNG decoding/compositing is supplied by LumINE's prepared-frame cache
// because the pinned upstream WIP still only contains a GIF decoder.
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media.Animation;
using Windows.UI.Xaml.Media.Imaging;

namespace XamlAnimatedPng
{
    public sealed class ApngAnimator
    {
        private readonly Image _image;
        private readonly int _width, _height;
        private readonly IReadOnlyList<byte[]> _frames;
        private readonly XamlAnimatedGif.TimingManager _timing;

        public ApngAnimator(Image image, int width, int height, IReadOnlyList<byte[]> frames, IReadOnlyList<int> ends, uint plays)
        {
            if (image == null || width <= 0 || height <= 0 || frames == null || ends == null || frames.Count == 0 || frames.Count != ends.Count)
                throw new ArgumentException("Invalid APNG frame sequence.");
            _image = image; _width = width; _height = height; _frames = frames;
            long duration = Math.Min(12000L, ends[ends.Count - 1] * (long)(plays == 0 ? 3 : Math.Min(plays, 3u)));
            if (duration <= 0) throw new ArgumentException("Invalid APNG frame duration.");
            _timing = new XamlAnimatedGif.TimingManager(new RepeatBehavior(TimeSpan.FromMilliseconds(duration)));
            int previous = 0;
            for (int i = 0; i < frames.Count; i++)
            {
                if (frames[i] == null || frames[i].Length != checked(width * height * 4) || ends[i] <= previous)
                    throw new ArgumentException("Invalid APNG pixels or timing.");
                _timing.Add(TimeSpan.FromMilliseconds(ends[i] - previous)); previous = ends[i];
            }
        }

        // Called on the UI dispatcher. Timing begins before drawing, as in the
        // upstream Animator.RunAsync, so drawing time is not added to the delay.
        public async Task PlayAsync(CancellationToken cancellation)
        {
            var bitmap = new WriteableBitmap(_width, _height);
            _image.Source = bitmap;
            _timing.Reset();
            int frame = 0;
            while (true)
            {
                cancellation.ThrowIfCancellationRequested();
                var next = _timing.NextAsync(cancellation);
                var pixels = _frames[frame];
                pixels.CopyTo(0, bitmap.PixelBuffer, 0, pixels.Length);
                bitmap.Invalidate();
                if (!await next) break;
                frame = (frame + 1) % _frames.Count;
            }
        }
    }
}
