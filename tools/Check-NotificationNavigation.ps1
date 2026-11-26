$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
function Read-Source([string] $path) { [regex]::Replace((Get-Content -LiteralPath (Join-Path $repoRoot $path) -Raw), '(?m)^using [^\r\n]+;\r?\n', '') }
$app = Get-Content (Join-Path $repoRoot 'LumINE/App.xaml.cs') -Raw
$start = $app.IndexOf('private async void BootstrapAsync(')
$open = $app.IndexOf('{', $start); $end = $open + 1; $depth = 1
while ($depth -gt 0) { if ($app[$end] -eq '{') { $depth++ }; if ($app[$end] -eq '}') { $depth-- }; $end++ }
$bootstrap = $app.Substring($start, $end - $start)
$fixture = @'
namespace Windows.UI.Xaml.Navigation {
    public class PageStackEntry { public Type SourcePageType; public PageStackEntry(Type type,object parameter,object transition) { SourcePageType=type; } }
}
namespace Windows.UI.Xaml.Controls {
    public class DispatcherAdapter { public Task RunAsync(Windows.UI.Core.CoreDispatcherPriority priority, Action action) { action(); return Task.CompletedTask; } }
    public class Frame {
        public object Content,Parameter; public int Navigations;
        public DispatcherAdapter Dispatcher=new DispatcherAdapter();
        public List<PageStackEntry> BackStack=new List<PageStackEntry>();
        public bool Navigate(Type type,object parameter) { Content=Activator.CreateInstance(type); Parameter=parameter; Navigations++; return true; }
    }
}
namespace Windows.UI.Core { public enum CoreDispatcherPriority { Normal } }
namespace LumINE.Views { public class ChatRoomPage {} public class ChatListPage { public string ChatId; public void OpenChat(string id) { ChatId=id; } } public class UnlockPage {} public class LoginPage {} }
namespace LumINE.Utils { public static class CrashDiagnostics { public static int Errors; public static void Record(string stage,Exception error) { Errors++; } } }
namespace LumINE {
    public class BootstrapHarness {
        bool _bootstrapRunning;
        public bool Running { get { return _bootstrapRunning; } }
        public void Launch(Frame frame,string arguments) { BootstrapAsync(frame,arguments); }
__BOOTSTRAP__
    }
}
namespace LumINE.Services.Vyline {
    public class Session { public string AccountId; }
    public static class SessionManager {
        public static bool IsLoggedIn; public static Session Current; public static int Restores;
        public static TaskCompletionSource<bool> RestoreCompletion=new TaskCompletionSource<bool>();
        public static async Task<bool> TryRestoreAsync(string account) {
            Restores++; await RestoreCompletion.Task;
            Current=new Session { AccountId=account ?? "acc.test:slot" }; IsLoggedIn=true; return true;
        }
    }
}
public static class NavigationChecks {
    static int Count;
    static void Check(bool condition,string message) { if(!condition) throw new Exception(message); Count++; }
    static void Login(string account) { SessionManager.Current=new Session { AccountId=account }; SessionManager.IsLoggedIn=true; }
    public static void Run() {
        string encoded=NotificationRoute.Encode("acc.test:slot", "upeer123");
        var parsed=NotificationRoute.Parse(encoded);
        Check(parsed.AccountId=="acc.test:slot" && parsed.ChatId=="upeer123","Notification arguments did not round-trip.");
        var xml=new System.Xml.XmlDocument();
        xml.LoadXml(NotificationContent.Toast("名前", "本文", null, encoded));
        Check(xml.DocumentElement.GetAttribute("launch")==encoded && NotificationRoute.Parse(xml.DocumentElement.GetAttribute("launch")).ChatId=="upeer123","Toast XML escaped or lost launch arguments.");
        xml.LoadXml(NotificationContent.Toast("test", "test", null));
        Check(!xml.DocumentElement.HasAttribute("launch"),"Test/legacy notification unexpectedly selected a conversation.");
        foreach(string value in new[]{null,"","unknown","action=other&account=acc&chat=upeer","action=openChat&account=acc&chat=../bad","action=openChat&account=acc&chat=upeer&chat=cother","action=openChat&account=&chat=upeer","action=openChat&account=acc%2Funsafe&chat=upeer"})
            Check(NotificationRoute.Parse(value)==null,"Malformed notification was accepted: "+value);
        foreach(string chat in new[]{"upeer123","cgroup456","rroom789"}) {
            Login("acc.test:slot"); var frame=new Frame();
            NotificationNavigation.Receive(NotificationRoute.Encode(SessionManager.Current.AccountId,chat));
            Check(NotificationNavigation.OpenPending(frame) && frame.Content is ChatListPage && ((ChatListPage)frame.Content).ChatId==chat,"Cold notification did not open the selected conversation.");
            Check(frame.BackStack.Count==0,"Cold notification has no back path to the list.");
            Check(!NotificationNavigation.OpenPending(frame) && frame.Navigations==1,"Pending notification was opened twice.");
        }
        var warm=new Frame { Content=new ChatRoomPage() }; warm.BackStack.Add(new PageStackEntry(typeof(ChatRoomPage),null,null));
        NotificationNavigation.Receive(encoded);
        Check(NotificationNavigation.OpenPending(warm) && ((ChatListPage)warm.Content).ChatId=="upeer123" && warm.BackStack.Count==0,"A live different conversation did not switch cleanly.");
        var locked=new Frame { Content=new UnlockPage() };
        NotificationNavigation.Receive(encoded);
        Check(!NotificationNavigation.OpenPending(locked) && NotificationNavigation.PendingAccountId=="acc.test:slot","Notification bypassed Windows Hello or lost its pending target.");
        locked.Content=new ChatListPage();
        Check(NotificationNavigation.OpenPending(locked) && locked.Content is ChatListPage && ((ChatListPage)locked.Content).ChatId=="upeer123","Unlock did not consume the pending conversation.");
        SessionManager.IsLoggedIn=false; var loggedOut=new Frame(); NotificationNavigation.Receive(encoded);
        Check(!NotificationNavigation.OpenPending(loggedOut) && loggedOut.Navigations==0,"Notification bypassed session restore/login.");
        Login("acc.test:slot"); Check(NotificationNavigation.OpenPending(loggedOut),"Login lost the notification target.");
        Login("another"); NotificationNavigation.Receive(encoded);
        Check(!NotificationNavigation.OpenPending(new Frame()) && NotificationNavigation.PendingAccountId==null,"Notification opened a chat under the wrong account.");
        Login("acc.test:slot"); NotificationNavigation.Receive(encoded);
        NotificationNavigation.Receive(NotificationRoute.Encode("acc.test:slot","clatest"));
        var latest=new Frame(); Check(NotificationNavigation.OpenPending(latest) && ((ChatListPage)latest.Content).ChatId=="clatest","A later notification tap did not replace the pending target.");
        NotificationNavigation.Receive(null); SessionManager.Current=null; SessionManager.IsLoggedIn=false;
        var restoring=new Frame(); var app=new LumINE.BootstrapHarness();
        app.Launch(restoring,""); NotificationNavigation.Receive(encoded); app.Launch(restoring,encoded);
        Check(SessionManager.Restores==1 && app.Running,"Tapping a notification during cold restore created a competing session.");
        SessionManager.RestoreCompletion.SetResult(true);
        for(int i=0;i<200 && app.Running;i++) Task.Delay(5).GetAwaiter().GetResult();
        Check(!app.Running && restoring.Content is ChatListPage && ((ChatListPage)restoring.Content).ChatId=="upeer123","Cold session restore lost the latest notification tap.");
        Check(SessionManager.Restores==1 && restoring.Navigations==1 && LumINE.Utils.CrashDiagnostics.Errors==0,"Cold activation duplicated navigation or faulted.");
        Console.WriteLine("PASS: "+Count+" notification routing, XML, cold/warm launch, authentication and back-navigation checks.");
    }
}
'@
$fixture = $fixture.Replace('__BOOTSTRAP__', $bootstrap)
$source = 'using System; using System.Threading.Tasks; using System.Collections.Generic; using System.Text; using Windows.UI.Xaml.Controls; using Windows.UI.Xaml.Navigation; using LumINE.Services.Vyline; using LumINE.Views; using LumINE.Utils;' + (Read-Source 'LumINE/Utils/NotificationNavigation.cs') + (Read-Source 'LumINE/Utils/NotificationContent.cs') + $fixture
Add-Type -TypeDefinition $source
[NavigationChecks]::Run()
