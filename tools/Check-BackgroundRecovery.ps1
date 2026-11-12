$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
function Read-Source([string] $path) { [regex]::Replace((Get-Content -LiteralPath (Join-Path $repoRoot $path) -Raw), '(?m)^using [^\r\n]+;\r?\n', '') }
$fixtures = @'
namespace Windows.Storage {
    public class SettingsMap : Dictionary<string,object> { public new object this[string key] { get { object value; return TryGetValue(key,out value)?value:null; } set { base[key]=value; } } }
    public class Settings { public SettingsMap Values=new SettingsMap(); }
    public class ApplicationData { public static ApplicationData Current=new ApplicationData(); public Settings LocalSettings=new Settings(); }
}
namespace Windows.ApplicationModel {
    public struct PackageVersion { public ushort Major,Minor,Build,Revision; }
    public class Identity { public PackageVersion Version=new PackageVersion{Major=1,Build=1}; }
    public class Package { public static Package Current=new Package(); public Identity Id=new Identity(); }
}
namespace Windows.Foundation.Metadata { public static class ApiInformation { public static bool IsPropertyPresent(string type,string property) { return true; } } }
namespace Windows.ApplicationModel.Background {
    public enum BackgroundAccessStatus { Unspecified,AllowedSubjectToSystemPolicy,DeniedByUser }
    public enum SystemTriggerType { SessionConnected,InternetAvailable,UserPresent,ServicingComplete }
    public enum SystemConditionType { InternetAvailable }
    public interface IBackgroundTrigger { }
    public class TimeTrigger : IBackgroundTrigger { public int Minutes; public bool Once; public TimeTrigger(int minutes,bool once) { Minutes=minutes; Once=once; } }
    public class SystemTrigger : IBackgroundTrigger { public SystemTriggerType Type; public bool Once; public SystemTrigger(SystemTriggerType type,bool once) { Type=type; Once=once; } }
    public class SystemCondition { public SystemConditionType Type; public SystemCondition(SystemConditionType type) { Type=type; } }
    public interface IBackgroundTaskInstance { }
    public interface IBackgroundTask { void Run(IBackgroundTaskInstance instance); }
    public class BackgroundExecutionManager {
        public static BackgroundAccessStatus Status=BackgroundAccessStatus.AllowedSubjectToSystemPolicy; public static int Removes,Requests;
        public static BackgroundAccessStatus GetAccessStatus() { return Status; }
        public static void RemoveAccess() { Removes++; Status=BackgroundAccessStatus.Unspecified; }
        public static Task<BackgroundAccessStatus> RequestAccessAsync() { Requests++; if(Status!=BackgroundAccessStatus.DeniedByUser) Status=BackgroundAccessStatus.AllowedSubjectToSystemPolicy; return Task.FromResult(Status); }
    }
    public class BackgroundTaskRegistration {
        public static Dictionary<Guid,BackgroundTaskRegistration> AllTasks=new Dictionary<Guid,BackgroundTaskRegistration>();
        public Guid Id=Guid.NewGuid(); public string Name,Entry; public IBackgroundTrigger Trigger; public bool Network; public List<SystemCondition> Conditions;
        public void Unregister(bool cancel) { AllTasks.Remove(Id); }
    }
    public class BackgroundTaskBuilder {
        public string Name,TaskEntryPoint; public bool IsNetworkRequested; IBackgroundTrigger Trigger; List<SystemCondition> Conditions=new List<SystemCondition>();
        public void SetTrigger(IBackgroundTrigger trigger) { Trigger=trigger; } public void AddCondition(SystemCondition condition) { Conditions.Add(condition); }
        public BackgroundTaskRegistration Register() { var task=new BackgroundTaskRegistration{Name=Name,Entry=TaskEntryPoint,Network=IsNetworkRequested,Trigger=Trigger,Conditions=Conditions}; BackgroundTaskRegistration.AllTasks.Add(task.Id,task); return task; }
    }
}
namespace Windows.Data.Xml.Dom { public class XmlDocument { public string Value; public void LoadXml(string value) { var parsed=new System.Xml.XmlDocument(); parsed.LoadXml(value); Value=value; } } }
namespace Windows.UI.Notifications {
    public class BadgeNotification { public string Xml; public BadgeNotification(Windows.Data.Xml.Dom.XmlDocument doc) { Xml=doc.Value; } }
    public class Updater { public string Last; public void Clear() { Last="clear"; } public void Update(BadgeNotification badge) { Last=badge.Xml; } }
    public static class BadgeUpdateManager { public static Updater Updater=new Updater(); public static Updater CreateBadgeUpdaterForApplication() { return Updater; } }
}
namespace LumINE.Services.Vyline {
    public static partial class BackgroundNotifications { public const string TaskName="LumINE.MessageNotifications"; public static int Polls; public static Task PollAsync(CancellationToken token) { Polls++; return Task.CompletedTask; } }
}
public static class RecoveryChecks {
    static void Check(bool condition,string message) { if(!condition) throw new Exception(message); }
    public static void Run() {
        var values=Windows.Storage.ApplicationData.Current.LocalSettings.Values;
        values["notifications.account"]=true; values["notifications.account.since"]=123L; values["notifications.account.cursor"]="preserved";
        LumINE.Services.Vyline.BackgroundNotifications.EnsureBackgroundRegistrationAsync().GetAwaiter().GetResult();
        var tasks=Windows.ApplicationModel.Background.BackgroundTaskRegistration.AllTasks;
        Check(tasks.Count==6,"Timer, four recovery triggers and real lock-screen task must all register.");
        var timer=tasks.Values.Single(t=>t.Name=="LumINE.MessageNotifications");
        Check(((Windows.ApplicationModel.Background.TimeTrigger)timer.Trigger).Minutes==15 && !((Windows.ApplicationModel.Background.TimeTrigger)timer.Trigger).Once,"Periodic polling must persist.");
        Check(tasks.Values.Where(t=>t.Name!="LumINE.LockScreenStatus").All(t=>t.Network && t.Conditions.Count==1 && string.IsNullOrEmpty(t.Entry)),"Network polling must request connected-standby access and use in-process activation.");
        Check(tasks.Values.Single(t=>t.Name=="LumINE.LockScreenStatus").Entry=="LumINE.Background.LockScreenStatusTask","Lock-screen catalog entry point is missing.");
        Check((long)values["notifications.account.since"]==123L && (string)values["notifications.account.cursor"]=="preserved","Registration repair reset notification history.");
        Check(Windows.ApplicationModel.Background.BackgroundExecutionManager.Removes==1,"Upgrade must renew background access once.");
        LumINE.Services.Vyline.BackgroundNotifications.EnsureBackgroundRegistrationAsync().GetAwaiter().GetResult();
        Check(tasks.Count==6 && Windows.ApplicationModel.Background.BackgroundExecutionManager.Removes==1,"Repeated launch duplicated tasks or reset access.");
        timer.Unregister(true); LumINE.Services.Vyline.BackgroundNotifications.EnsureBackgroundRegistrationAsync().GetAwaiter().GetResult();
        Check(tasks.Count==6 && tasks.Values.Single(t=>t.Name=="LumINE.MessageNotifications").Network,"A missing periodic registration was not repaired.");
        LumINE.Services.Vyline.BackgroundNotifications.RunNotificationTaskAsync("LumINE.MessageNotifications.Recovery.UserPresent",CancellationToken.None).GetAwaiter().GetResult();
        Check(LumINE.Services.Vyline.BackgroundNotifications.Polls==1 && values["notifications.worker.lastWake"] is long,"Recovery activation did not dispatch polling or persist diagnostics.");
        LumINE.Services.Vyline.BackgroundNotifications.RunNotificationTaskAsync("unrelated",CancellationToken.None).GetAwaiter().GetResult();
        Check(LumINE.Services.Vyline.BackgroundNotifications.Polls==1,"An unrelated background activation was treated as notification polling.");
        var unrelated=new Windows.ApplicationModel.Background.BackgroundTaskRegistration{Name="unrelated"}; tasks.Add(unrelated.Id,unrelated);
        values["notifications.account"]=false; LumINE.Services.Vyline.BackgroundNotifications.EnsureBackgroundRegistrationAsync().GetAwaiter().GetResult();
        Check(tasks.Count==1 && tasks.Values.Single().Name=="unrelated","Disabling notifications failed to remove all owned tasks or removed another task.");
        values["notifications.account"]=true; Windows.ApplicationModel.Background.BackgroundExecutionManager.Status=Windows.ApplicationModel.Background.BackgroundAccessStatus.DeniedByUser;
        LumINE.Services.Vyline.BackgroundNotifications.EnsureBackgroundRegistrationAsync().GetAwaiter().GetResult();
        Check(tasks.Count==1 && Windows.ApplicationModel.Background.BackgroundExecutionManager.Removes==1,"User-denied background access must not be reset automatically.");
        values["notifications.account.pending"]=123; values["notifications.disabled"]=false; values["notifications.disabled.pending"]=100;
        new LumINE.Background.LockScreenStatusTask().Run(null);
        Check(Windows.UI.Notifications.BadgeUpdateManager.Updater.Last=="<badge value='100'/>","Status task must use persisted enabled-account counts and the 99+ badge state.");
        values["notifications.account.pending"]=0; new LumINE.Background.LockScreenStatusTask().Run(null);
        Check(Windows.UI.Notifications.BadgeUpdateManager.Updater.Last=="clear","Status task did not clear an empty badge.");
    }
}
'@
$source = 'using System; using System.Collections.Generic; using System.Linq; using System.Threading; using System.Threading.Tasks; using Windows.ApplicationModel; using Windows.ApplicationModel.Background; using Windows.Foundation.Metadata; using Windows.Storage; using Windows.UI.Notifications; using Windows.Data.Xml.Dom;' + (Read-Source 'LumINE/Services/Vyline/BackgroundRegistration.cs') + (Read-Source 'LumINE.Background/LockScreenStatusTask.cs') + $fixtures
Add-Type -TypeDefinition $source
[RecoveryChecks]::Run()
$manifest=[xml](Get-Content -LiteralPath (Join-Path $repoRoot 'LumINE/Package.appxmanifest') -Raw)
$ns=[Xml.XmlNamespaceManager]::new($manifest.NameTable); $ns.AddNamespace('m','http://schemas.microsoft.com/appx/manifest/foundation/windows10')
if($null -eq $manifest.SelectSingleNode('//m:Extension[@Category="windows.backgroundTasks" and @EntryPoint="LumINE.Background.LockScreenStatusTask"]/m:BackgroundTasks/m:Task[@Type="systemEvent"]',$ns)) { throw 'Exported lock-screen task declaration is missing.' }
Write-Output 'PASS: 15 background recovery, migration, idempotence, network, permission, badge and manifest checks.'
