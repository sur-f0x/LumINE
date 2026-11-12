using System;
using Windows.ApplicationModel.Background;
using Windows.Data.Xml.Dom;
using Windows.Storage;
using Windows.UI.Notifications;

namespace LumINE.Background
{
    // A real, exported WinRT entry point for Mobile's lock-screen catalog.
    // Reads persisted counts only; never starts another LINE polling connection.
    public sealed class LockScreenStatusTask : IBackgroundTask
    {
        public void Run(IBackgroundTaskInstance taskInstance)
        {
            var values = ApplicationData.Current.LocalSettings.Values;
            try
            {
                long count = 0;
                foreach (var item in values)
                {
                    if (!item.Key.StartsWith("notifications.", StringComparison.Ordinal) || !item.Key.EndsWith(".pending", StringComparison.Ordinal) || !(item.Value is int)) continue;
                    string setting = item.Key.Substring(0, item.Key.Length - ".pending".Length);
                    if (values[setting] as bool? == true) count += Math.Max(0, (int)item.Value);
                }
                var updater = BadgeUpdateManager.CreateBadgeUpdaterForApplication();
                if (count == 0) updater.Clear();
                else
                {
                    var xml = new XmlDocument(); xml.LoadXml("<badge value='" + Math.Min(100L, count) + "'/>");
                    updater.Update(new BadgeNotification(xml));
                }
                values["notifications.status.lastRun"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            }
            catch (Exception ex) { values["notifications.status.error"] = ex.Message; }
        }
    }
}
