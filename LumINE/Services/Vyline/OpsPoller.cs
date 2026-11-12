using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LumINE.Utils;

namespace LumINE.Services.Vyline
{
    /// <summary>
    /// fetchOps loop (vyline clientManager.startFetchOpsLoop).
    /// POLL=2000ms, IDLE=8000ms, TIMEOUT=60000ms,
    /// error backoff 5s * 2^n max 60s, timeout retry 5s without streak++.
    /// </summary>
    public class OpsPoller
    {
        public const int PollIntervalMs = 2000;
        public const int IdleIntervalMs = 8000;
        public const int PollTimeoutMs = 60000;
        public const int ListenerStartDelayMs = 5000;
        public const int TimeoutRetryMs = 5000;

        private readonly TalkService _talk;
        private readonly string _accountId;
        private readonly Func<OpsRevisionCursor> _getCursor;
        private readonly Action<OpsRevisionCursor> _setCursor;
        private readonly Func<OpsRevisionCursor, Task> _persistCursor;
        private readonly Func<List<Dictionary<int, object>>, Task> _processOps;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private Task _loop;
        private int _started;

        public event Action Stopped;

        public OpsPoller(
            TalkService talk,
            string accountId,
            Func<OpsRevisionCursor> getCursor,
            Action<OpsRevisionCursor> setCursor,
            Func<OpsRevisionCursor, Task> persistCursor,
            Func<List<Dictionary<int, object>>, Task> processOps)
        {
            _talk = talk;
            _accountId = accountId;
            _getCursor = getCursor;
            _setCursor = setCursor;
            _persistCursor = persistCursor;
            _processOps = processOps;
        }

        public bool IsRunning { get { return _loop != null && !_loop.IsCompleted; } }

        /// <summary>Start after ListenerStartDelayMs (vyline startTalkListeners).</summary>
        public void StartDelayed()
        {
            if (Interlocked.Exchange(ref _started, 1) == 1) return;
            _loop = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(ListenerStartDelayMs, _cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { return; }
                await RunLoopAsync().ConfigureAwait(false);
            });
        }

        public void StartNow()
        {
            if (Interlocked.Exchange(ref _started, 1) == 1) return;
            _loop = Task.Run(() => RunLoopAsync());
        }

        public void Stop()
        {
            try { _cts.Cancel(); } catch { }
            var handler = Stopped;
            if (handler != null) handler();
        }

        private async Task RunLoopAsync()
        {
            int errorStreak = 0;
            var token = _cts.Token;
            while (!token.IsCancellationRequested)
            {
                OpsRevisionCursor cursor;
                try
                {
                    cursor = _getCursor();
                    var resp = await _talk.SyncAsync(
                        cursor.Revision, cursor.GlobalRev, cursor.IndividualRev,
                        100, PollTimeoutMs, token).ConfigureAwait(false);

                    if (token.IsCancellationRequested) break;

                    // Extract operations list
                    var ops = ExtractOperations(resp);
                    var next = OpsRevisionCursor.Next(cursor, resp);

                    // process first, then commit cursor (processAndCommitOpsRevision)
                    if (ops.Count > 0)
                    {
                        try
                        {
                            await _processOps(ops).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine("[Ops] process failed; cursor not advanced: " + ex.Message);
                            // do not advance cursor
                            ops = new List<Dictionary<int, object>>(); // still sleep as idle? vyline rethrows and does not commit — but loop continues after catch
                            // fall through to sleep with error handling
                            throw;
                        }
                    }

                    if (token.IsCancellationRequested) break;

                    _setCursor(next);
                    try
                    {
                        await _persistCursor(next).ConfigureAwait(false);
                        BackgroundNotifications.CommitRealtimeCursor(_accountId, next);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine("[Ops] persist failed: " + ex.Message);
                    }

                    int delay = ops.Count > 0 ? PollIntervalMs : IdleIntervalMs;
                    try { await Task.Delay(delay, token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                    errorStreak = 0;
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (token.IsCancellationRequested) break;
                    var msg = ex.Message ?? "";
                    bool isTimeout =
                        ex is OperationCanceledException ||
                        msg.IndexOf("timed out", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        msg.IndexOf("canceled", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        msg.IndexOf("cancelled", StringComparison.OrdinalIgnoreCase) >= 0;

                    if (isTimeout)
                    {
                        // long poll natural end — retry in 5s, do not increment streak
                        errorStreak = 0;
                        try { await Task.Delay(TimeoutRetryMs, token).ConfigureAwait(false); }
                        catch (OperationCanceledException) { break; }
                    }
                    else
                    {
                        errorStreak++;
                        int retryMs = Math.Min(5000 * (1 << Math.Min(errorStreak - 1, 4)), 60000);
                        // 5s,10s,20s,40s,60s,60s...
                        if (errorStreak == 1) retryMs = 5000;
                        else if (errorStreak == 2) retryMs = 10000;
                        else if (errorStreak == 3) retryMs = 20000;
                        else if (errorStreak == 4) retryMs = 40000;
                        else retryMs = 60000;
                        System.Diagnostics.Debug.WriteLine("[Ops] error streak=" + errorStreak + " retry in " + retryMs + "ms: " + msg);
                        try { await Task.Delay(retryMs, token).ConfigureAwait(false); }
                        catch (OperationCanceledException) { break; }
                    }
                }
            }
            System.Diagnostics.Debug.WriteLine("[Ops] loop stopped account=" + _accountId);
        }

        /// <summary>
        /// Extract operations array from sync_result.
        /// Layout discovery: operationResponse typically field containing operations list.
        /// </summary>
        public static List<Dictionary<int, object>> ExtractOperations(Dictionary<int, object> syncResult)
        {
            var ops = new List<Dictionary<int, object>>();
            if (syncResult == null) return ops;
            object response, entries;
            if (syncResult.TryGetValue(1, out response) && response is Dictionary<int, object> operation &&
                operation.TryGetValue(1, out entries) && entries is List<object> wireOperations)
            {
                foreach (var item in wireOperations) if (item is Dictionary<int, object> fields) ops.Add(fields);
                return ops;
            }

            // Common: field 1 or 3 = OperationResponse with operations field
            foreach (var rootKey in new[] { 1, 3, 2, 4 })
            {
                object v;
                if (!syncResult.TryGetValue(rootKey, out v)) continue;
                var d = v as Dictionary<int, object>;
                if (d == null) continue;

                // operations list field: try 3, 6, 1, 2
                foreach (var listKey in new[] { 3, 6, 1, 2, 4, 5 })
                {
                    object listObj;
                    if (!d.TryGetValue(listKey, out listObj)) continue;
                    var list = listObj as List<object>;
                    if (list == null || list.Count == 0) continue;
                    // Heuristic: list of structs with a type-like field
                    int structCount = 0;
                    foreach (var item in list)
                        if (item is Dictionary<int, object>) structCount++;
                    if (structCount == 0) continue;
                    if (structCount >= Math.Max(1, list.Count / 2))
                    {
                        foreach (var item in list)
                        {
                            var od = item as Dictionary<int, object>;
                            if (od != null) ops.Add(od);
                        }
                        return ops;
                    }
                }
            }
            return ops;
        }
    }
}
