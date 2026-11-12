$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
function Read-Source([string] $path) {
    $source = Get-Content -LiteralPath (Join-Path $repoRoot $path) -Raw
    $source = [regex]::Replace($source, '(?m)^using [^\r\n]+;\r?\n', '')
    [regex]::Replace($source, '(?m)^#define WINRT\r?\n', '')
}
$fixtures = @'
namespace Windows.UI.Xaml.Media.Animation {
    public struct RepeatBehavior {
        public bool HasDuration,HasCount; public TimeSpan Duration; public double Count;
        public RepeatBehavior(TimeSpan duration) { Duration=duration; HasDuration=true; HasCount=false; Count=0; }
        public RepeatBehavior(double count) { Count=count; HasCount=true; HasDuration=false; Duration=TimeSpan.Zero; }
    }
}
namespace Windows.UI.Xaml.Controls { public class Image { public object Source; } }
namespace Windows.UI.Xaml.Media.Imaging {
    public class FakeBuffer { public byte[] Pixels; }
    public class WriteableBitmap {
        public FakeBuffer PixelBuffer; public readonly List<byte[]> Draws=new List<byte[]>(); public static Action Invalidated;
        public WriteableBitmap(int width,int height) { PixelBuffer=new FakeBuffer{Pixels=new byte[width*height*4]}; }
        public void Invalidate() { Draws.Add((byte[])PixelBuffer.Pixels.Clone()); Invalidated?.Invoke(); }
    }
}
namespace System.Runtime.InteropServices.WindowsRuntime {
    public static class Copies { public static void CopyTo(this byte[] source,int index,global::Windows.UI.Xaml.Media.Imaging.FakeBuffer target,uint offset,int length) { System.Buffer.BlockCopy(source,index,target.Pixels,(int)offset,length); } }
}
public static class ApngLibraryChecks {
    static void Check(bool condition,string message) { if(!condition) throw new Exception(message); }
    public static async Task Run() {
        var timing=new XamlAnimatedGif.TimingManager(new RepeatBehavior(2.0)); timing.Add(TimeSpan.FromMilliseconds(1)); timing.Add(TimeSpan.FromMilliseconds(1));
        int completed=0; timing.Completed += (s,e)=>completed++;
        Check(await timing.NextAsync(CancellationToken.None) && await timing.NextAsync(CancellationToken.None) && await timing.NextAsync(CancellationToken.None) && !await timing.NextAsync(CancellationToken.None) && timing.IsComplete && completed==1,"Upstream repeat-count or completion behavior failed.");
        timing.Reset(); Check(!timing.IsComplete && await timing.NextAsync(CancellationToken.None),"Upstream rewind/reset behavior failed.");
        var limited=new XamlAnimatedGif.TimingManager(new RepeatBehavior(TimeSpan.FromMilliseconds(10))); limited.Add(TimeSpan.FromSeconds(10));
        using(var stop=new CancellationTokenSource(1000)) {
            var wait=limited.NextAsync(stop.Token); Check(!await wait && limited.IsComplete,"The last wait exceeded the requested animation duration.");
        }
        var paused=new XamlAnimatedGif.TimingManager(new RepeatBehavior(1.0)); paused.Add(TimeSpan.FromMilliseconds(1)); paused.Pause();
        using(var stop=new CancellationTokenSource()) {
            var wait=paused.NextAsync(stop.Token); Check(!wait.IsCompleted && paused.IsPaused,"Pause did not block frame advancement.");
            stop.Cancel(); bool canceled=false; try { await wait; } catch(OperationCanceledException) { canceled=true; } Check(canceled,"Paused playback ignored cancellation.");
        }
        paused.Reset(); paused.Resume(); Check(!await paused.NextAsync(CancellationToken.None) && !paused.IsPaused,"Resume did not restore playback.");
        var image=new Windows.UI.Xaml.Controls.Image(); var frames=new List<byte[]>{new byte[]{1,2,3,4},new byte[]{5,6,7,8}}; var ends=new List<int>{1,2};
        await new XamlAnimatedPng.ApngAnimator(image,1,1,frames,ends,1).PlayAsync(CancellationToken.None);
        var bitmap=(Windows.UI.Xaml.Media.Imaging.WriteableBitmap)image.Source;
        Check(bitmap.Draws.Count==2 && bitmap.Draws[0].SequenceEqual(frames[0]) && bitmap.Draws[1].SequenceEqual(frames[1]),"UWP adapter did not render the supplied APNG frames in order.");
        using(var stop=new CancellationTokenSource()) {
            Windows.UI.Xaml.Media.Imaging.WriteableBitmap.Invalidated=()=>stop.Cancel();
            bool canceled=false; try { await new XamlAnimatedPng.ApngAnimator(image,1,1,frames,new List<int>{20,40},1).PlayAsync(stop.Token); } catch(OperationCanceledException) { canceled=true; }
            finally { Windows.UI.Xaml.Media.Imaging.WriteableBitmap.Invalidated=null; }
            Check(canceled && ((Windows.UI.Xaml.Media.Imaging.WriteableBitmap)image.Source).Draws.Count==1,"Unloading/disabled playback did not stop frame rendering.");
        }
        bool bad=false; try { new XamlAnimatedPng.ApngAnimator(image,1,1,frames,new List<int>{1,1},1); } catch(ArgumentException) { bad=true; }
        Check(bad,"Invalid APNG timing was accepted.");
        await new XamlAnimatedPng.ApngAnimator(image,1,1,frames,ends,0).PlayAsync(CancellationToken.None);
        Check(((Windows.UI.Xaml.Media.Imaging.WriteableBitmap)image.Source).Draws.Count==6,"Infinite source animation did not honor the three-loop cap.");
    }
}
'@
$source = "#define WINRT`nusing System; using System.Collections.Generic; using System.Linq; using System.Threading; using System.Threading.Tasks; using System.Runtime.InteropServices.WindowsRuntime; using Windows.UI.Xaml.Controls; using Windows.UI.Xaml.Media.Animation; using Windows.UI.Xaml.Media.Imaging; using TaskEx = System.Threading.Tasks.Task;" + (Read-Source 'LumINE/ThirdParty/XamlAnimatedPng/TimingManager.cs') + (Read-Source 'LumINE/ThirdParty/XamlAnimatedPng/ApngAnimator.Uwp.cs') + $fixtures
Add-Type -TypeDefinition $source
[ApngLibraryChecks]::Run().GetAwaiter().GetResult() | Out-Null
Write-Output 'PASS: 10 upstream timing and UWP APNG adapter checks (repeat, reset, duration, pause/resume, cancellation, pixels, validation and loop cap).'
