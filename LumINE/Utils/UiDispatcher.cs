using System;
using System.Threading.Tasks;
using Windows.UI.Core;

namespace LumINE.Utils
{
    public static class UiDispatcher
    {
        // CoreDispatcher only waits for an async-void delegate to return, not
        // for its awaited work. Transfer completion/faults to an observed Task.
        public static async Task RunAsync(CoreDispatcher dispatcher, Func<Task> work)
        {
            if (dispatcher.HasThreadAccess) { await work(); return; }
            var completion = new TaskCompletionSource<bool>();
            await dispatcher.RunAsync(CoreDispatcherPriority.Normal, async () =>
            {
                try { await work(); completion.TrySetResult(true); }
                catch (OperationCanceledException) { completion.TrySetCanceled(); }
                catch (Exception ex) { completion.TrySetException(ex); }
            });
            await completion.Task;
        }
    }
}
