$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
function Read-Source([string] $path) { [regex]::Replace((Get-Content -LiteralPath (Join-Path $repoRoot $path) -Raw), '(?m)^using [^\r\n]+;\r?\n', '') }
$prepared = Read-Source 'LumINE/Services/Vyline/PreparedStickerCache.cs'
$prepared = $prepared.Substring(0, $prepared.IndexOf('    public static class PreparedStickerCache')) + '}'
$fixtures = @'
namespace Windows.Storage {
    public class Values : Dictionary<string,object> { public new object this[string key] { get { object value; return TryGetValue(key,out value)?value:null; } set { base[key]=value; } } }
    public class Settings { public Values Values=new Values(); }
    public class ApplicationData { public static ApplicationData Current=new ApplicationData(); public Settings LocalSettings=new Settings(); }
}
namespace Windows.Security.Credentials.UI {
    public enum UserConsentVerifierAvailability { Available, NotConfiguredForUser, DisabledByPolicy }
    public enum UserConsentVerificationResult { Verified, Canceled, RetriesExhausted }
    public static class UserConsentVerifier {
        public static UserConsentVerifierAvailability Available=UserConsentVerifierAvailability.Available;
        public static UserConsentVerificationResult Result=UserConsentVerificationResult.Verified;
        public static int Requests;
        public static Task<UserConsentVerifierAvailability> CheckAvailabilityAsync() { return Task.FromResult(Available); }
        public static Task<UserConsentVerificationResult> RequestVerificationAsync(string text) { Requests++; return Task.FromResult(Result); }
    }
}
namespace LumINE.Models {
    public class Message { public string Id; public long CreatedTime; }
    public class Chat { public string ChatMid, ChatName, PicturePath; public int ChatType; }
    public class ChatSummary { public string ChatMid,Name,PicturePath; public int ChatType,UnreadCount; public bool IsFriend; public object Avatar; public Message LastMessage; public long LastActivityTime { get { return LastMessage==null?0:LastMessage.CreatedTime; } } }
}
namespace LumINE.Services.Vyline {
    public enum TalkEventKind { Message,Revoke,Read,Reaction,Membership }
    public class TalkPollEvent { public string ChatMid; public TalkEventKind Kind; }
    public static class SessionManager { public static LineSession Current; }
    public class LineSession { public string AccountId="test"; public TestStore Store=new TestStore(); public TestLine Line=new TestLine(); }
    public class TestStore {
        public Dictionary<string,LumINE.Models.Message> Messages=new Dictionary<string,LumINE.Models.Message>();
        public Task LoadChatActivityAsync() { return Task.CompletedTask; } public Task RefreshChatActivityAsync() { return Task.CompletedTask; }
        public LumINE.Models.Message GetLastMessage(string mid) { LumINE.Models.Message message; return Messages.TryGetValue(mid,out message)?message:null; }
        public int GetUnreadCount(string mid) { return 0; }
    }
    public class TestLine { public List<LumINE.Models.Chat> Chats=new List<LumINE.Models.Chat>(); public Task<List<LumINE.Models.Chat>> FetchChatsAsync(bool force,bool refresh) { return Task.FromResult(Chats); } }
    public static class AvatarCache { public static Task<object> GetAsync(string account,string path) { return Task.FromResult<object>(null); } }
}
public static class PreferenceChecks {
    static void Check(bool condition,string message) { if(!condition) throw new Exception(message); }
    public static async Task Run() {
        Check(!LumINE.Services.AppSettings.RequireHello && !LumINE.Services.AppSettings.HideMessageContent && LumINE.Services.AppSettings.AnimateStickers,"Default settings changed existing behavior.");
        int events=0; LumINE.Services.AppSettings.Changed += key=>events++;
        LumINE.Services.AppSettings.AnimateStickers=false; LumINE.Services.AppSettings.HideMessageContent=true; LumINE.Services.AppSettings.RequireHello=true;
        Check(events==3 && !LumINE.Services.AppSettings.AnimateStickers && LumINE.Services.AppSettings.HideMessageContent && LumINE.Services.AppSettings.RequireHello,"Settings persistence or change events failed.");
        Check(await LumINE.Services.AppAuthentication.VerifyAsync("test"),"Verified Windows Hello result was not accepted.");
        Windows.Security.Credentials.UI.UserConsentVerifier.Result=Windows.Security.Credentials.UI.UserConsentVerificationResult.Canceled;
        Check(!await LumINE.Services.AppAuthentication.VerifyAsync("test"),"Canceled Windows Hello bypassed the gate.");
        Windows.Security.Credentials.UI.UserConsentVerifier.Result=Windows.Security.Credentials.UI.UserConsentVerificationResult.RetriesExhausted;
        Check(!await LumINE.Services.AppAuthentication.VerifyAsync("test"),"Failed Windows Hello bypassed the gate.");
        int calls=Windows.Security.Credentials.UI.UserConsentVerifier.Requests; bool denied=false;
        Windows.Security.Credentials.UI.UserConsentVerifier.Available=Windows.Security.Credentials.UI.UserConsentVerifierAvailability.NotConfiguredForUser;
        try { await LumINE.Services.AppAuthentication.VerifyAsync("test"); } catch(InvalidOperationException) { denied=true; }
        Check(denied && calls==Windows.Security.Credentials.UI.UserConsentVerifier.Requests,"Missing device authentication must fail closed before requesting verification.");
        var session=new LumINE.Services.Vyline.LineSession(); LumINE.Services.Vyline.SessionManager.Current=session;
        session.Line.Chats.AddRange(new[]{
            new LumINE.Models.Chat{ChatMid="u1",ChatName="Alpha",ChatType=2},new LumINE.Models.Chat{ChatMid="u2",ChatName="ALPHA north",ChatType=2},new LumINE.Models.Chat{ChatMid="u3",ChatName="Beta",ChatType=2},
            new LumINE.Models.Chat{ChatMid="c1",ChatName="Alpha group",ChatType=1},new LumINE.Models.Chat{ChatMid="c2",ChatName="Gamma",ChatType=1}});
        var vm=new LumINE.ViewModels.ChatListViewModel(); await vm.LoadChatsAsync();
        Check(vm.FriendChats.Count==3 && vm.GroupChats.Count==2,"Both swipe pages must have independent collections.");
        var friends=vm.FriendChats; vm.SearchQuery=" alpha ";
        Check(vm.FriendChats.Count==2 && vm.GroupChats.Count==1,"Search must list every case-insensitive matching chat on both tabs.");
        vm.SelectGroups(); Check(vm.Chats.Count==1 && vm.Chats[0].ChatMid=="c1","Switching tabs lost the search filter.");
        vm.SearchQuery=""; Check(vm.FriendChats.Count==3 && vm.GroupChats.Count==2 && object.ReferenceEquals(friends,vm.FriendChats),"Clearing search did not restore stable collections.");
        session.Store.Messages["u3"]=new LumINE.Models.Message{Id="100",CreatedTime=500};
        vm.ApplyEvents(new[]{new LumINE.Services.Vyline.TalkPollEvent{ChatMid="u3",Kind=LumINE.Services.Vyline.TalkEventKind.Message}});
        Check(vm.FriendChats[0].ChatMid=="u3" && vm.GroupChats.Count==2,"Latest ordering was not retained with two swipe pages.");
        vm.SearchQuery="no match"; Check(vm.FriendChats.Count==0 && vm.GroupChats.Count==0 && vm.StatusMessage.Contains("該当"),"Empty filtered results were not represented.");
    }
}
'@
$source = 'using System; using System.Collections.Generic; using System.Collections.ObjectModel; using System.Linq; using System.Threading.Tasks; using LumINE.Models; using LumINE.Services.Vyline; using Windows.Storage; using Windows.Security.Credentials.UI;' + (Read-Source 'LumINE/Services/AppSettings.cs') + (Read-Source 'LumINE/Services/AppAuthentication.cs') + (Read-Source 'LumINE/ViewModels/BaseViewModel.cs') + (Read-Source 'LumINE/ViewModels/ChatListViewModel.cs') + $prepared + $fixtures
Add-Type -TypeDefinition ($source + (Read-Source 'LumINE/Utils/ForegroundUi.cs'))
[LumINE.Utils.ForegroundUi]::Resume([LumINE.Utils.ForegroundUi]::Generation) | Out-Null
[PreferenceChecks]::Run().GetAwaiter().GetResult() | Out-Null
Write-Output 'PASS: 12 settings, Windows Hello fail-closed, complete search and independent-tab collection checks. Playback is checked by Check-XamlAnimatedPng.ps1.'
