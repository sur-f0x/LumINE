$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
function Read-Method([string] $path, [string] $signature) {
    $source = Get-Content -LiteralPath (Join-Path $repoRoot $path) -Raw
    $start = $source.IndexOf($signature, [StringComparison]::Ordinal)
    if ($start -lt 0) { throw "Method missing: $signature" }
    $open = $source.IndexOf('{', $start)
    $depth = 1; $end = $open + 1
    while ($depth -gt 0) {
        if ($source[$end] -eq '{') { $depth++ }
        if ($source[$end] -eq '}') { $depth-- }
        $end++
    }
    return $source.Substring($start, $end - $start)
}
$ui = [regex]::Replace((Get-Content (Join-Path $repoRoot 'LumINE/Utils/UiDispatcher.cs') -Raw), '(?m)^using [^\r\n]+;\r?\n', '')
$foreground = [regex]::Replace((Get-Content (Join-Path $repoRoot 'LumINE/Utils/ForegroundUi.cs') -Raw), '(?m)^using [^\r\n]+;\r?\n', '')
$models = [regex]::Replace((Get-Content (Join-Path $repoRoot 'LumINE/Models/LineModels.cs') -Raw), '(?m)^using [^\r\n]+;\r?\n', '')
$ensure = Read-Method 'LumINE/Services/Vyline/SessionManager.cs' 'private void EnsureServices()'
$cleanup = Read-Method 'LumINE/Services/Vyline/BackgroundNotifications.cs' 'private static void DisposeExtension('
$scheduling = @('private void ScheduleEvents()', 'private void ScheduleChats()', 'public void Dispose()') | ForEach-Object { Read-Method 'LumINE/Services/Vyline/SyncScheduler.cs' $_ }
$fixture = @'
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using System.Collections.Generic;
using Windows.UI.Core;
public class ExtendedExecutionForegroundSession {
    public bool Closed, FailRemove; public int Disposals;
    public event Action<object,object> Revoked { add {} remove { if(FailRemove) throw new ObjectDisposedException("Session"); } }
    public void Dispose() { Disposals++; if(Closed) throw new ObjectDisposedException("Session"); Closed=true; }
}
namespace LumINE.Utils { public static class CrashDiagnostics { public static int Errors; public static void Record(string stage,Exception error) { Errors++; } } }
public static class NotificationLifetime {
    static void OnRealtimeRevoked(object sender,object args) {}
    public static void Release(ExtendedExecutionForegroundSession session) { DisposeExtension(session); }
__CLEANUP__
}
namespace Windows.UI.Core {
    public enum CoreDispatcherPriority { Normal }
    public class CoreDispatcher {
        public bool HasThreadAccess, Fail, Deferred;
        public Queue<Action> Pending=new Queue<Action>();
        public Task RunAsync(CoreDispatcherPriority priority, Action work) {
            if (Fail) throw new InvalidOperationException("dispatcher closed");
            if(Deferred) Pending.Enqueue(work); else work(); return Task.CompletedTask;
        }
    }
}
namespace Windows.ApplicationModel.Core {
    public class View { public CoreDispatcher Dispatcher=new CoreDispatcher(); }
    public static class CoreApplication { public static View MainView=new View(); }
}
namespace Windows.UI.Xaml.Media { public class ImageSource {} }
namespace Windows.UI.Xaml {
    public class BoundsValue { public double Width=360; }
    public class Window { public static Window Current=new Window(); public BoundsValue Bounds=new BoundsValue(); }
}
public class ChatStore { public ChatStore(LineSession session) {} }
public class ReadEngine {
    public static ManualResetEventSlim Entered=new ManualResetEventSlim(), Release=new ManualResetEventSlim();
    public ReadEngine(LineSession session, ChatStore store) { Entered.Set(); if(!Release.Wait(5000)) throw new TimeoutException(); }
}
public class LineService { public LineService(LineSession session, ChatStore store, ReadEngine read) {} }
public class SyncScheduler { public SyncScheduler(LineSession session, ChatStore store, ReadEngine read) {} }
public class LineSession {
    readonly object _svcLock=new object(); ChatStore _store; ReadEngine _read; LineService _line; SyncScheduler _sync;
    public ChatStore Store { get { EnsureServices(); return _store; } }
    public LineService Line { get { EnsureServices(); return _line; } }
__ENSURE__
}
namespace SchedulerFixture {
    // Block Timer.Change while another thread tries to dispose the scheduler.
    public class Timer {
        public static ManualResetEventSlim Entered=new ManualResetEventSlim(), Release=new ManualResetEventSlim();
        public static bool Pause; public static int Created;
        bool Disposed;
        public Timer(Action<object> callback, object state, int due, int period) { Created++; }
        public void Change(int due,int period) { if(Pause) { Entered.Set(); if(!Release.Wait(5000)) throw new TimeoutException(); } if(Disposed) throw new ObjectDisposedException("Timer"); }
        public void Dispose() { Disposed=true; }
    }
    public class Scheduler {
        readonly object _timerGate=new object(); volatile bool _disposed;
        Timer _eventsTimer, _chatsTimer, _readTimer=null;
        const int ChatsPollMs=120000, ChatsJitterMs=25000;
        int CurrentIntervalMs() { return 2000; }
        void OnEventsTick(object state) {} void OnChatsTick(object state) {} void DetachWindow() {}
        public void Schedule(bool chats) { if(chats) ScheduleChats(); else ScheduleEvents(); }
__SCHEDULING__
    }
}
public static class ResumeChecks {
    static int Count;
    static void Check(bool condition,string message) { if(!condition) throw new Exception(message); Count++; }
    public static void Run() { RunAsync().GetAwaiter().GetResult(); Console.WriteLine("PASS: " + Count + " resume safety checks."); }
    static async Task RunAsync() {
        Check(!LumINE.Utils.ForegroundUi.IsReady,"Background-only startup enabled presentation before a window existed.");
        int resumed=0; Action onReady=()=>resumed++;
        LumINE.Utils.ForegroundUi.Ready+=onReady;
        int first=LumINE.Utils.ForegroundUi.Generation;
        Check(LumINE.Utils.ForegroundUi.Resume(first) && resumed==1,"Foreground entry did not request cache catch-up.");
        Check(!LumINE.Utils.ForegroundUi.Resume(first) && resumed==1,"Duplicate launch/visibility events reentered foreground initialization.");
        // A callback posted before hiding must not mutate a reused page later.
        Action stale=()=>{if(LumINE.Utils.ForegroundUi.IsCurrent(first)) throw new Exception("Stale UI callback ran.");};
        LumINE.Utils.ForegroundUi.Pause(); stale();
        Check(!LumINE.Utils.ForegroundUi.IsReady && !LumINE.Utils.ForegroundUi.Resume(first),"Old activation reopened a hidden UI.");
        int second=LumINE.Utils.ForegroundUi.Generation;
        Check(LumINE.Utils.ForegroundUi.Resume(second) && resumed==2,"Live-process reactivation did not resume presentation exactly once.");
        stale();
        Check(!LumINE.Utils.ForegroundUi.IsCurrent(first),"Work from the previous foreground generation remained valid.");
        LumINE.Utils.ForegroundUi.Ready-=onReady;
        var bindingDispatcher=Windows.ApplicationModel.Core.CoreApplication.MainView.Dispatcher;
        bindingDispatcher.Deferred=true;
        var message=new LumINE.Models.Message(); int bindings=0;
        message.PropertyChanged+=(s,e)=>bindings++;
        message.ReadLabel="既読";
        Check(bindingDispatcher.Pending.Count>0,"The binding race fixture did not post UI work.");
        LumINE.Utils.ForegroundUi.Pause();
        int queued=bindingDispatcher.Pending.Count;
        message.MediaLoadError="cached error";
        Check(bindingDispatcher.Pending.Count==queued,"Hidden-window model changes continued to queue binding updates.");
        LumINE.Utils.ForegroundUi.Resume(LumINE.Utils.ForegroundUi.Generation);
        while(bindingDispatcher.Pending.Count>0) bindingDispatcher.Pending.Dequeue()();
        Check(bindings==0,"An old binding callback executed against the reactivated window.");
        bindingDispatcher.HasThreadAccess=true;
        message.RefreshState();
        Check(bindings>0 && message.ReadLabel=="既読" && message.MediaLoadError=="cached error","Foreground catch-up lost cached model state.");
        int errors=LumINE.Utils.CrashDiagnostics.Errors;
        bindingDispatcher.HasThreadAccess=false; bindingDispatcher.Deferred=false;
        message.PropertyChanged+=(s,e)=>{throw new InvalidOperationException("binding handler failed");};
        message.ReadLabel="既読2";
        Check(LumINE.Utils.CrashDiagnostics.Errors>errors,"Dispatched binding faults escaped diagnosis.");
        var dispatcher=new CoreDispatcher();
        var release=new TaskCompletionSource<bool>(); bool finished=false;
        var run=LumINE.Utils.UiDispatcher.RunAsync(dispatcher,async()=>{await release.Task; finished=true;});
        Check(!run.IsCompleted,"Dispatch must wait for awaited work, not only async-void invocation.");
        release.SetResult(true); await run; Check(finished,"Dispatched work was lost.");
        bool caught=false;
        try { await LumINE.Utils.UiDispatcher.RunAsync(dispatcher,async()=>{await Task.Yield(); throw new InvalidOperationException("after await");}); }
        catch(InvalidOperationException ex) { caught=ex.Message=="after await"; }
        Check(caught,"Exception after await escaped the observed Task.");
        caught=false;
        try { await LumINE.Utils.UiDispatcher.RunAsync(dispatcher,()=>Task.FromCanceled(new CancellationToken(true))); }
        catch(OperationCanceledException) { caught=true; }
        Check(caught,"Canceled UI work did not propagate cancellation.");
        dispatcher.HasThreadAccess=true; int calls=0;
        await LumINE.Utils.UiDispatcher.RunAsync(dispatcher,()=>{calls++; return Task.CompletedTask;});
        Check(calls==1,"UI-thread work must execute exactly once.");
        dispatcher.HasThreadAccess=false; dispatcher.Fail=true; caught=false;
        try { await LumINE.Utils.UiDispatcher.RunAsync(dispatcher,()=>Task.CompletedTask); } catch(InvalidOperationException) { caught=true; }
        Check(caught,"Dispatch failure must not leave an incomplete Task.");
        NotificationLifetime.Release(null);
        var extension=new ExtendedExecutionForegroundSession { FailRemove=true };
        NotificationLifetime.Release(extension);
        Check(extension.Disposals==1,"Failed event unsubscribe skipped COM session disposal.");
        NotificationLifetime.Release(extension);
        Check(extension.Disposals==2 && LumINE.Utils.CrashDiagnostics.Errors==errors+5,"Already-closed session cleanup escaped or was not diagnosed.");

        var session=new LineSession();
        var initialize=Task.Run(()=>session.Store);
        Check(ReadEngine.Entered.Wait(5000),"Service initialization never entered the controlled constructor.");
        var reader=Task.Run(()=>session.Line);
        try { await Task.Delay(100); Check(!reader.IsCompleted,"Another thread observed partially initialized services."); }
        finally { ReadEngine.Release.Set(); }
        await initialize; Check(await reader!=null,"Concurrent service accessor returned null.");
        Check(object.ReferenceEquals(session.Store,await initialize),"Service accessor replaced the shared store.");

        foreach(bool chats in new[]{false,true}) {
            SchedulerFixture.Timer.Pause=false;
            SchedulerFixture.Timer.Entered.Reset(); SchedulerFixture.Timer.Release.Reset();
            var scheduler=new SchedulerFixture.Scheduler(); scheduler.Schedule(chats);
            SchedulerFixture.Timer.Pause=true;
            var change=Task.Run(()=>scheduler.Schedule(chats));
            Check(SchedulerFixture.Timer.Entered.Wait(5000),"Timer.Change did not enter controlled race.");
            var dispose=Task.Run(()=>scheduler.Dispose());
            try { await Task.Delay(100); Check(!dispose.IsCompleted,"Timer was disposed while Change was still in flight."); }
            finally { SchedulerFixture.Timer.Release.Set(); }
            await change; await dispose;
            int created=SchedulerFixture.Timer.Created;
            scheduler.Schedule(chats); scheduler.Dispose();
            Check(SchedulerFixture.Timer.Created==created,"Disposed scheduler restarted its timers.");
        }
    }
}
'@
$fixture = $fixture.Replace('__ENSURE__', $ensure).Replace('__SCHEDULING__', ($scheduling -join "`n")).Replace('__CLEANUP__', $cleanup)
Add-Type -TypeDefinition ($fixture + $ui + $foreground + $models)
[ResumeChecks]::Run()
