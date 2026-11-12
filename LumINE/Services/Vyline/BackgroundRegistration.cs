using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Background;
using Windows.Foundation.Metadata;
using Windows.Storage;

namespace LumINE.Services.Vyline
{
    public static partial class BackgroundNotifications
    {
        public const string StatusTaskName = "LumINE.LockScreenStatus";
        private const string RecoveryPrefix = "LumINE.MessageNotifications.Recovery.";
        private const int RegistrationSchema = 2;
        private static readonly SemaphoreSlim RegistrationGate = new SemaphoreSlim(1, 1);

        public static bool IsNotificationTask(string name) { return name == TaskName || (name != null && name.StartsWith(RecoveryPrefix, StringComparison.Ordinal)); }
        private static bool IsManagedTask(string name) { return name == StatusTaskName || IsNotificationTask(name); }
        private static bool AccessAllowed(BackgroundAccessStatus access) { return access != BackgroundAccessStatus.Unspecified && !access.ToString().StartsWith("Denied", StringComparison.Ordinal); }
        private static bool AnyEnabled()
        {
            foreach (var item in ApplicationData.Current.LocalSettings.Values)
                if (item.Key.StartsWith("notifications.", StringComparison.Ordinal) && item.Value is bool && (bool)item.Value) return true;
            return false;
        }
        public static string RegistrationStatus
        {
            get
            {
                var values = ApplicationData.Current.LocalSettings.Values;
                string text = values["notifications.registration.status"] as string ?? "バックグラウンド登録を確認していません。";
                if (values["notifications.worker.lastWake"] is long)
                    text += "\n最終バックグラウンド起動: " + DateTimeOffset.FromUnixTimeMilliseconds((long)values["notifications.worker.lastWake"]).ToLocalTime().ToString("M/d HH:mm:ss");
                string error = values["notifications.worker.error"] as string;
                if (!string.IsNullOrEmpty(error)) text += "\n最終確認エラー: " + error;
                return text;
            }
        }

        // Called on foreground entry as well as when the toggle is saved. Never
        // resets .since or the /SYNC4 cursor when merely repairing registration.
        public static async Task EnsureBackgroundRegistrationAsync(bool explicitRequest = false)
        {
            await RegistrationGate.WaitAsync();
            try
            {
                var values = ApplicationData.Current.LocalSettings.Values;
                if (!AnyEnabled())
                {
                    foreach (var task in BackgroundTaskRegistration.AllTasks.Values.Where(t => IsManagedTask(t.Name)).ToList()) task.Unregister(true);
                    values["notifications.registration.status"] = "通知のバックグラウンド登録は停止しています。";
                    return;
                }
                var access = BackgroundExecutionManager.GetAccessStatus();
                if (!AccessAllowed(access) && access != BackgroundAccessStatus.Unspecified && !explicitRequest)
                {
                    values["notifications.registration.status"] = "Windowsのバックグラウンド実行許可: " + access;
                    return;
                }
                var version = Package.Current.Id.Version;
                string signature = RegistrationSchema + ":" + version.Major + "." + version.Minor + "." + version.Build + "." + version.Revision;
                bool changed = values["notifications.registration.version"] as string != signature;
                if (changed && AccessAllowed(access)) BackgroundExecutionManager.RemoveAccess();
                if (changed || !AccessAllowed(access)) access = await BackgroundExecutionManager.RequestAccessAsync();
                if (!AccessAllowed(access))
                {
                    values["notifications.registration.status"] = "Windowsのバックグラウンド実行許可: " + access;
                    throw new InvalidOperationException("Windowsの設定でLumINEのバックグラウンド実行を許可してください（" + access + "）。");
                }
                if (changed)
                    foreach (var task in BackgroundTaskRegistration.AllTasks.Values.Where(t => IsManagedTask(t.Name)).ToList()) task.Unregister(true);

                RegisterTask(TaskName, new TimeTrigger(15, false), null, true);
                string errors = "";
                foreach (var trigger in new[] { SystemTriggerType.SessionConnected, SystemTriggerType.InternetAvailable, SystemTriggerType.UserPresent, SystemTriggerType.ServicingComplete })
                {
                    try { RegisterTask(RecoveryPrefix + trigger, new SystemTrigger(trigger, false), null, true); }
                    catch (Exception ex) { errors += "\n" + trigger + ": " + ex.Message; }
                }
                RegisterTask(StatusTaskName, new SystemTrigger(SystemTriggerType.UserPresent, false), "LumINE.Background.LockScreenStatusTask", false);
                values["notifications.registration.version"] = signature;
                int count = BackgroundTaskRegistration.AllTasks.Values.Count(t => IsManagedTask(t.Name));
                values["notifications.registration.status"] = "バックグラウンド登録: " + count + "件。再起動後は定期・ロック解除・接続復旧時に確認します。" + errors;
            }
            finally { RegistrationGate.Release(); }
        }

        private static void RegisterTask(string name, IBackgroundTrigger trigger, string entryPoint, bool network)
        {
            if (BackgroundTaskRegistration.AllTasks.Values.Any(task => task.Name == name)) return;
            var builder = new BackgroundTaskBuilder { Name = name };
            if (!string.IsNullOrEmpty(entryPoint)) builder.TaskEntryPoint = entryPoint;
            builder.SetTrigger(trigger);
            if (network)
            {
                builder.AddCondition(new SystemCondition(SystemConditionType.InternetAvailable));
                if (ApiInformation.IsPropertyPresent("Windows.ApplicationModel.Background.BackgroundTaskBuilder", "IsNetworkRequested")) builder.IsNetworkRequested = true;
            }
            builder.Register();
        }

        public static async Task RunNotificationTaskAsync(string taskName, CancellationToken cancellation)
        {
            if (!IsNotificationTask(taskName)) return;
            var values = ApplicationData.Current.LocalSettings.Values;
            values["notifications.worker.lastWake"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            values["notifications.worker.trigger"] = taskName;
            try
            {
                await PollAsync(cancellation);
                values.Remove("notifications.worker.error");
            }
            catch (Exception ex)
            {
                values["notifications.worker.error"] = ex.Message;
                System.Diagnostics.Debug.WriteLine("[Notifications] " + taskName + ": " + ex.Message);
            }
        }
    }
}
