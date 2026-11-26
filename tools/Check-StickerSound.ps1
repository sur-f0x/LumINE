param([switch]$LiveCdn)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
function Source($p){[regex]::Replace([IO.File]::ReadAllText((Join-Path $root $p)),'(?m)^using [^\r\n]+;\r?\n','')}
function Body($source,$name){
 $m=[regex]::Match($source,'(?m)^        private static .*\b'+$name+'\(')
 if(!$m.Success){throw "Missing method $name"}
 $b=$source.IndexOf('{',$m.Index);$d=1;$end=$b+1
 while($d -gt 0){if($source[$end] -eq '{'){$d++};if($source[$end] -eq '}'){$d--};$end++}
 $source.Substring($m.Index,$end-$m.Index)
}
$player=Source 'LumINE/Services/Vyline/StickerSoundPlayer.cs'
# Execute the real playback state machine/settings with portable media and
# storage adapters. No audio output or user account is needed for these checks.
$player=$player.Replace((Body $player 'FolderAsync'),'')
$player=$player.Replace((Body $player 'MarkSeenAsync'),@'
        private static async Task<bool> MarkSeenAsync(string account,string chat,string id){
            if(TestAudio.MarkDelay!=null)await TestAudio.MarkDelay.Task;
            StickerSoundHistory history;if(!Histories.TryGetValue(account,out history))Histories[account]=history=new StickerSoundHistory();
            return history.Observe(chat,id);
        }
'@)
$player=$player.Replace((Body $player 'GetSoundAsync'),@'
        private static async Task<StorageFile> GetSoundAsync(string account,string stickerId,CancellationToken token){
            TestAudio.Downloads++;if(TestAudio.DownloadDelay!=null)await TestAudio.DownloadDelay.Task;
            token.ThrowIfCancellationRequested();return TestAudio.Absent?null:new StorageFile();
        }
'@)
$pack=Source 'LumINE/Services/Vyline/StickerCatalog.cs'
$pack=$pack.Substring($pack.IndexOf('    public sealed class StickerPack'),$pack.IndexOf('    public static class StickerCatalog')-$pack.IndexOf('    public sealed class StickerPack'))
$fixture=@'
public static class TestAudio {public static TaskCompletionSource<bool> MarkDelay,DownloadDelay;public static int Downloads;public static bool Absent;}
namespace Windows.Storage {
 public class StorageFile{}
 public class SettingsValues:Dictionary<string,object>{public new object this[string key]{get{object v;return TryGetValue(key,out v)?v:null;}set{base[key]=value;}}}
 public class Settings {public SettingsValues Values=new SettingsValues();}
 public class ApplicationData {public static ApplicationData Current=new ApplicationData();public Settings LocalSettings=new Settings();}
}
namespace Windows.UI.Xaml {
 public class VisibilityArgs:EventArgs{public bool Visible;}
 public class Window {public static Window Current=new Window();public bool Visible=true;public event EventHandler<VisibilityArgs> VisibilityChanged;public void Hide(){Visible=false;VisibilityChanged?.Invoke(this,new VisibilityArgs{Visible=false});}}
}
namespace Windows.Media.Core {public class MediaSource:IDisposable {public static MediaSource CreateFromStorageFile(Windows.Storage.StorageFile file){return new MediaSource();}public void Dispose(){}}}
namespace Windows.Media.Playback {
 public enum MediaPlayerAudioCategory{SoundEffects}
 public class Manager{public bool IsEnabled=true;}
 public class MediaError:EventArgs{public string Error="test";}
 public class MediaPlayer {
  public static int Plays,Pauses;public bool AutoPlay,IsLoopingEnabled;public MediaPlayerAudioCategory AudioCategory;public object Source;public Manager CommandManager=new Manager();public event EventHandler<MediaError> MediaFailed;
  public void Play(){Plays++;}public void Pause(){Pauses++;}
 }
}
namespace LumINE.Models {public class Message {public bool IsSticker=true,IsRevoked;public string Id,To="cgroup",From="upeer";public Dictionary<string,string> ContentMetadata=new Dictionary<string,string>{{"STKID","7115479"}};}}
namespace LumINE.Services.Vyline {
__PACK__
 public class LineSession{public string AccountId="test";}
 public static class SessionManager{public static LineSession Current=new LineSession();}
 public static class BackgroundNotifications{public static bool IsForeground=true;}
}
public static class StickerChecks {
 static int checks;static void Check(bool ok,string message){if(!ok)throw new Exception(message);checks++;}
 static LumINE.Models.Message M(string id){return new LumINE.Models.Message{Id=id};}
 public static async Task Run(){
  var session=LumINE.Services.Vyline.SessionManager.Current;var owner=new object();
  Check(LumINE.Services.AppSettings.PlayStickerSounds,"Sticker sounds should default to enabled.");
  await LumINE.Services.Vyline.StickerSoundPlayer.PlayAsync(session,M("received"),false,owner,()=>true);
  Check(MediaPlayer.Plays==1,"First visible received sticker did not play.");
  await LumINE.Services.Vyline.StickerSoundPlayer.PlayAsync(session,M("received"),false,owner,()=>true);
  Check(MediaPlayer.Plays==1,"Recycled/reopened rows replayed automatically.");
  await LumINE.Services.Vyline.StickerSoundPlayer.PlayAsync(session,M("sent"),false,null);
  await LumINE.Services.Vyline.StickerSoundPlayer.PlayAsync(session,M("sent"),false,owner,()=>true);
  Check(MediaPlayer.Plays==2,"Send plus first display produced duplicate audio.");
  await LumINE.Services.Vyline.StickerSoundPlayer.PlayAsync(session,M("sent"),true,owner,()=>true);
  await LumINE.Services.Vyline.StickerSoundPlayer.PlayAsync(session,M("sent"),true,owner,()=>true);
  Check(MediaPlayer.Plays==4,"Tapping must replay a previously heard sticker.");
  LumINE.Services.AppSettings.PlayStickerSounds=false;
  int downloads=TestAudio.Downloads;
  await LumINE.Services.Vyline.StickerSoundPlayer.PlayAsync(session,M("muted"),false,owner,()=>true);
  await LumINE.Services.Vyline.StickerSoundPlayer.PlayAsync(session,M("sent"),true,owner,()=>true);
  Check(MediaPlayer.Plays==4&&TestAudio.Downloads==downloads&&MediaPlayer.Pauses>0,"Mute did not stop/suppress automatic and tapped sounds.");
  LumINE.Services.AppSettings.PlayStickerSounds=true;
  await LumINE.Services.Vyline.StickerSoundPlayer.PlayAsync(session,M("muted"),false,owner,()=>true);
  Check(MediaPlayer.Plays==4,"Enabling audio replayed messages already viewed while muted.");
  await LumINE.Services.Vyline.StickerSoundPlayer.PlayAsync(session,M("offscreen"),false,owner,()=>false);
  Check(MediaPlayer.Plays==4,"A pre-realized offscreen/collapsed row played.");
  await LumINE.Services.Vyline.StickerSoundPlayer.PlayAsync(session,M("offscreen"),false,owner,()=>true);
  Check(MediaPlayer.Plays==5,"Offscreen preload consumed the first-visible claim.");
  TestAudio.DownloadDelay=new TaskCompletionSource<bool>();var pending=LumINE.Services.Vyline.StickerSoundPlayer.PlayAsync(session,M("download"),true,owner,()=>true);
  LumINE.Services.AppSettings.PlayStickerSounds=false;TestAudio.DownloadDelay.SetResult(true);await pending;TestAudio.DownloadDelay=null;
  Check(MediaPlayer.Plays==5,"Audio began after mute during download.");LumINE.Services.AppSettings.PlayStickerSounds=true;
  TestAudio.MarkDelay=new TaskCompletionSource<bool>();pending=LumINE.Services.Vyline.StickerSoundPlayer.PlayAsync(session,M("left"),false,null);
  LumINE.Services.Vyline.StickerSoundPlayer.Stop();TestAudio.MarkDelay.SetResult(true);await pending;TestAudio.MarkDelay=null;
  Check(MediaPlayer.Plays==5,"A delayed send/visibility request played after leaving the chat.");
  await LumINE.Services.Vyline.StickerSoundPlayer.PlayAsync(session,M("revoked"),true,owner,()=>true);
  int before=MediaPlayer.Plays;var revoked=M("revoked2");revoked.IsRevoked=true;await LumINE.Services.Vyline.StickerSoundPlayer.PlayAsync(session,revoked,true,owner,()=>true);
  Check(MediaPlayer.Plays==before,"Revoked stickers played audio.");
  BackgroundNotifications.IsForeground=false;await LumINE.Services.Vyline.StickerSoundPlayer.PlayAsync(session,M("hidden"),false,owner);
  Check(MediaPlayer.Plays==before,"Background receive played sticker sound.");BackgroundNotifications.IsForeground=true;
  await LumINE.Services.Vyline.StickerSoundPlayer.PlayAsync(new LineSession(),M("wrong-account"),true,owner);Check(MediaPlayer.Plays==before,"Inactive account played audio.");
  TestAudio.DownloadDelay=new TaskCompletionSource<bool>();var other=new object();pending=LumINE.Services.Vyline.StickerSoundPlayer.PlayAsync(session,M("new-owner"),true,other,()=>true);
  LumINE.Services.Vyline.StickerSoundPlayer.Stop(owner);TestAudio.DownloadDelay.SetResult(true);await pending;TestAudio.DownloadDelay=null;
  Check(MediaPlayer.Plays==before+1,"Recycling an older row canceled another row's playback.");
  Window.Current.Hide();Check(MediaPlayer.Pauses>0,"Hiding the window did not stop audio.");Window.Current.Visible=true;
  TestAudio.Absent=true;await LumINE.Services.Vyline.StickerSoundPlayer.PlayAsync(session,M("silent"),false,owner);Check(MediaPlayer.Plays==before+1,"A silent/no-file sticker attempted playback.");TestAudio.Absent=false;
  var history=new StickerSoundHistory();Check(history.Observe("c1","1")&&!history.Observe("c1","1")&&history.Observe("c1","2")&&history.Observe("c2","1"),"First-display tracking must use chat/message identity.");
  var restored=new StickerSoundHistory();foreach(var value in history.Seen)restored.Seen.Add(value);Check(!restored.Observe("c1","1"),"Restored seen records replayed an old sticker.");
  Check(StickerSoundHistory.SoundUrl("../bad")==null&&StickerSoundHistory.SoundUrl("0")==null&&StickerSoundHistory.SoundUrl("7115479").EndsWith("/7115479/android/sticker_sound.m4a"),"Audio URL validation failed.");
  Check(StickerSoundHistory.IsM4a(new byte[]{0,0,0,20,102,116,121,112,77,52,65,32})&&!StickerSoundHistory.IsM4a(new byte[]{1,2,3})&&!StickerSoundHistory.IsM4a(new byte[20]),"Invalid/HTML audio was accepted into cache.");
  var args=StickerCatalogProtocol.OwnedArgs(100);Check(args[0].Type==12&&args[0].FieldId==1&&((List<ThriftField>)args[0].Value).Count==0&&args[1].Value.ToString()=="stickershop"&&(int)args[2].Value==100&&(int)args[3].Value==100,"Current owned-summary request schema differs from Vyline.");
  var pack=StickerCatalogProtocol.ReadPack(new Dictionary<int,object>{{1,"11537"},{11,"Pack"},{21,23L},{94,1700000000L}});
  Check(pack.Id=="11537"&&pack.Name=="Pack"&&pack.Version=="23"&&pack.ExpiresAt==1700000000000L&&pack.IsExpired,"Modern pack version/validUntil was read from legacy fields.");
  Check(StickerCatalogProtocol.ReadPack(new Dictionary<int,object>{{1,"bad"}})==null,"Invalid owned package was offered.");
  Console.WriteLine("PASS: "+checks+" sound triggers, mute/cancellation, ownership, first-display persistence inputs, CDN validation and owned-catalog schema checks.");
 }
}
'@
$code='using System;using System.IO;using System.Collections.Generic;using System.Threading;using System.Threading.Tasks;using Windows.Storage;using Windows.UI.Xaml;using Windows.Media.Core;using Windows.Media.Playback;using LumINE.Services.Vyline;using LumINE.Models;using LumINE.Utils;'
foreach($p in @('LumINE/Services/AppSettings.cs','LumINE/Services/Vyline/StickerSoundHistory.cs','LumINE/Services/Vyline/StickerCatalogProtocol.cs','LumINE/Services/Vyline/ChatActivityProtocol.cs','LumINE/Utils/ThriftTypes.cs')){$code+=Source $p}
Add-Type -TypeDefinition ($code+$player+$fixture.Replace('__PACK__',$pack)) -IgnoreWarnings -WarningAction SilentlyContinue
[StickerChecks]::Run().GetAwaiter().GetResult() | Out-Null
if($LiveCdn){
 $client=[Net.Http.HttpClient]::new()
 try{$bytes=$client.GetByteArrayAsync([LumINE.Services.Vyline.StickerSoundHistory]::SoundUrl('7115479')).GetAwaiter().GetResult();if(![LumINE.Services.Vyline.StickerSoundHistory]::IsM4a($bytes)){throw 'CDN did not return M4A'};Write-Output "PASS: Public sticker audio CDN returned valid M4A ($($bytes.Length) bytes). No playback performed."}finally{$client.Dispose()}
}
