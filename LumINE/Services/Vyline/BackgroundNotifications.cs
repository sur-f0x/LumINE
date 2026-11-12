using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.Background;
using Windows.ApplicationModel.ExtendedExecution.Foreground;
using Windows.Foundation.Metadata;
using Windows.Data.Json;
using Windows.Data.Xml.Dom;
using Windows.Storage;
using Windows.UI.Notifications;
using LumINE.Models;

namespace LumINE.Services.Vyline
{
    public static partial class BackgroundNotifications
    {
        public const string TaskName = "LumINE.MessageNotifications";
        public static volatile bool IsForeground;
        private static readonly object Gate = new object();
        private static readonly SemaphoreSlim PollGate = new SemaphoreSlim(1, 1);
        private static readonly SemaphoreSlim LifetimeGate = new SemaphoreSlim(1, 1);
        private static ExtendedExecutionForegroundSession _execution;
        private static volatile bool _realtimeActive;
        private static string _realtimeStatus = "常駐待受は停止しています。";
        public static string RealtimeStatus { get { return _realtimeStatus; } }
        public static string DeliveryStatus { get; private set; } = "新着の受信はまだ確認できていません。";

        public static void ShowTestNotification()
        {
            var notifier = ToastNotificationManager.CreateToastNotifier();
            if (notifier.Setting != NotificationSetting.Enabled) throw new InvalidOperationException("Windowsで通知が無効です: " + notifier.Setting);
            var xml = new XmlDocument();
            xml.LoadXml(LumINE.Utils.NotificationContent.Toast("LumINE", AppSettings.HideMessageContent ? "新規メッセージ" : "通知テストです。", null));
            notifier.Show(new ToastNotification(xml));
        }

        // Keep the existing vyline-style OpsPoller alive, rather than creating
        // a second /SYNC4 reader with a competing revision cursor.
        public static async Task EnsureRealtimeAsync()
        {
            await LifetimeGate.WaitAsync();
            try
            {
                var current = SessionManager.Current;
                if (current == null || current.Disabled || !IsEnabled(current.AccountId))
                {
                    ReleaseRealtime();
                    return;
                }
                await EnsureBackgroundRegistrationAsync();
                await RestoreNotificationStateAsync(current.AccountId);
                if (_execution != null || !IsForeground) return;
                if (!ApiInformation.IsTypePresent("Windows.ApplicationModel.ExtendedExecution.Foreground.ExtendedExecutionForegroundSession"))
                {
                    _realtimeStatus = "このOSでは常駐待受を利用できません。定期確認を使用します。";
                    return;
                }
                var extension = new ExtendedExecutionForegroundSession
                {
                    Reason = ExtendedExecutionForegroundReason.Unconstrained,
                    Description = "LINEの新着メッセージを待ち受けます"
                };
                extension.Revoked += OnRealtimeRevoked;
                _execution = extension;
                try
                {
                    var result = await extension.RequestExtensionAsync();
                    if (result == ExtendedExecutionForegroundResult.Allowed && object.ReferenceEquals(_execution, extension) &&
                        object.ReferenceEquals(current, SessionManager.Current) && !current.Disabled && IsEnabled(current.AccountId))
                    {
                        _realtimeActive = true;
                        _realtimeStatus = "常駐待受中です。ホーム画面やロック中にも新着を通知します。";
                        current.StartOpsListener();
                        System.Diagnostics.Debug.WriteLine("[Notifications] unconstrained execution allowed");
                    }
                    else
                    {
                        ReleaseRealtime();
                        _realtimeStatus = "常駐が許可されなかったため、定期確認を使用します。";
                    }
                }
                catch (Exception ex)
                {
                    ReleaseRealtime();
                    _realtimeStatus = "常駐待受を開始できませんでした。定期確認を使用します。";
                    System.Diagnostics.Debug.WriteLine("[Notifications] extended execution failed: " + ex.Message);
                }
            }
            catch (Exception ex)
            {
                ReleaseRealtime();
                _realtimeStatus = "常駐待受を開始できませんでした。定期確認を使用します。";
                System.Diagnostics.Debug.WriteLine("[Notifications] realtime initialization failed: " + ex.Message);
            }
            finally { LifetimeGate.Release(); }
        }

        private static void OnRealtimeRevoked(object sender, ExtendedExecutionForegroundRevokedEventArgs args)
        {
            var extension = sender as ExtendedExecutionForegroundSession;
            if (!object.ReferenceEquals(extension, Interlocked.CompareExchange(ref _execution, null, extension))) return;
            _realtimeActive = false;
            _realtimeStatus = "Windowsが常駐待受を終了しました。定期確認を使用します。";
            DisposeExtension(extension);
            System.Diagnostics.Debug.WriteLine("[Notifications] extended execution revoked: " + args.Reason);
        }

        private static void ReleaseRealtime()
        {
            _realtimeActive = false;
            var extension = Interlocked.Exchange(ref _execution, null);
            DisposeExtension(extension);
            _realtimeStatus = "常駐待受は停止しています。";
        }

        private static void DisposeExtension(ExtendedExecutionForegroundSession extension)
        {
            if (extension == null) return;
            // Revocation and foreground activation can overlap. A revoked COM
            // session may already be closed; cleanup must not escape its event.
            try { extension.Revoked -= OnRealtimeRevoked; }
            catch (Exception ex) { LumINE.Utils.CrashDiagnostics.Record("Notifications.Unsubscribe", ex); }
            try { extension.Dispose(); }
            catch (Exception ex) { LumINE.Utils.CrashDiagnostics.Record("Notifications.Dispose", ex); }
        }
        private static string Key(string accountId) { return "notifications." + accountId; }
        public static bool IsEnabled(string accountId) { return ApplicationData.Current.LocalSettings.Values[Key(accountId)] as bool? == true; }

        public static async Task SetEnabledAsync(string accountId, bool enabled)
        {
            var settings = ApplicationData.Current.LocalSettings.Values;
            if (enabled)
            {
                bool wasEnabled = IsEnabled(accountId);
                if (!wasEnabled)
                {
                    var cursor = await OpsRevisionCursor.LoadAsync(accountId);
                    SaveCursor(accountId, cursor);
                    settings[Key(accountId) + ".since"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                }
                settings[Key(accountId)] = true;
                try
                {
                    await EnsureBackgroundRegistrationAsync(true);
                    await RestoreNotificationStateAsync(accountId);
                    await EnsureRealtimeAsync();
                }
                catch { settings[Key(accountId)] = wasEnabled; throw; }
            }
            else
            {
                settings[Key(accountId)] = false;
                await ClearNotificationStateAsync(accountId);
                await EnsureBackgroundRegistrationAsync();
                await EnsureRealtimeAsync();
            }
        }

        private static void SaveCursor(string accountId, OpsRevisionCursor cursor)
        {
            lock (Gate)
            {
                var values = ApplicationData.Current.LocalSettings.Values;
                string key = Key(accountId) + ".cursor";
                var old = values[key] as ApplicationDataCompositeValue;
                var data = new ApplicationDataCompositeValue();
                data["revision"] = Math.Max(cursor.Revision, old == null ? 0 : (long)old["revision"]);
                data["global"] = Math.Max(cursor.GlobalRev, old == null ? 0 : (long)old["global"]);
                data["individual"] = Math.Max(cursor.IndividualRev, old == null ? 0 : (long)old["individual"]);
                values[key] = data;
            }
        }

        public static void CommitRealtimeCursor(string accountId, OpsRevisionCursor cursor)
        {
            if (IsEnabled(accountId)) SaveCursor(accountId, cursor);
        }

        public static async Task PollAsync(CancellationToken cancellationToken)
        {
            if (IsForeground || _realtimeActive || !await PollGate.WaitAsync(0)) return;
            try
            {
                foreach (var account in await TokenStore.ListAsync())
                {
                    if (!IsEnabled(account.AccountId) || string.IsNullOrEmpty(account.AuthToken) || cancellationToken.IsCancellationRequested) continue;
                    var data = ApplicationData.Current.LocalSettings.Values[Key(account.AccountId) + ".cursor"] as ApplicationDataCompositeValue;
                    if (data == null) continue;
                    var cursor = new OpsRevisionCursor { Revision = (long)data["revision"], GlobalRev = (long)data["global"], IndividualRev = (long)data["individual"] };
                    // Do not start the foreground sync loop or warm contacts,
                    // E2EE keys, avatars, or history for this short background job.
                    var current = SessionManager.Current;
                    var session = current != null && current.AccountId == account.AccountId ? current : new LineSession(account.AccountId, account.AuthToken, account.RefreshToken, account.Mid);
                    try
                    {
                        var processor = new OperationProcessor(session);
                        for (int batch = 0; batch < 2 && !cancellationToken.IsCancellationRequested && !IsForeground && IsEnabled(account.AccountId); batch++)
                        {
                            var response = await session.Talk.SyncAsync(cursor.Revision, cursor.GlobalRev, cursor.IndividualRev, 100, 7000, cancellationToken);
                            foreach (var op in OpsPoller.ExtractOperations(response))
                            {
                                var ev = await processor.ProcessSingleAsync(op);
                                if (ev != null && ev.Kind == TalkEventKind.Message && ev.RawMessage != null)
                                    await NotifyAsync(session, ChatStore.MapMessage(ev.RawMessage, ev.ChatMid), cancellationToken);
                            }
                            var next = OpsRevisionCursor.Next(cursor, response);
                            SaveCursor(account.AccountId, next);
                            if (next.Revision == cursor.Revision && next.GlobalRev == cursor.GlobalRev && next.IndividualRev == cursor.IndividualRev) break;
                            cursor = next;
                        }
                    }
                    finally
                    {
                        var foregroundSession = SessionManager.Current;
                        if (!object.ReferenceEquals(session, current) && (foregroundSession == null || foregroundSession.AccountId != account.AccountId)) session.Dispose();
                    }
                }
            }
            finally { PollGate.Release(); }
        }
    }
}
