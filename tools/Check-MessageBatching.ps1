$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
function Read-Source($path) { [regex]::Replace([IO.File]::ReadAllText((Join-Path $root $path)), '(?m)^using [^\r\n]+;\r?\n', '') }
function Method($source, $name) {
    $match = [regex]::Match($source, '(?m)^        (?:public|private).*\b' + $name + '\(')
    if (!$match.Success) { throw "Missing production method: $name" }
    $brace = $source.IndexOf('{', $match.Index); $depth = 1; $end = $brace + 1
    while ($depth -gt 0) { if ($source[$end] -eq '{') { $depth++ }; if ($source[$end] -eq '}') { $depth-- }; $end++ }
    $source.Substring($match.Index, $end - $match.Index)
}
$store = Read-Source 'LumINE/Services/Vyline/ChatStore.cs'
$view = Read-Source 'LumINE/ViewModels/ChatRoomViewModel.cs'
$model = Read-Source 'LumINE/Models/LineModels.cs'
$reaction = $model.Substring($model.IndexOf('    public sealed class MessageReaction'), $model.IndexOf('    public class Profile') - $model.IndexOf('    public sealed class MessageReaction'))
# Execute the real merge, comparison, batch publication and date-boundary code.
# Only WinRT/model presentation is replaced by a portable fixture.
$fixture = @'
namespace Windows.Data.Json {
 public enum JsonValueType{Null,String,Number,Boolean,Object,Array}
 public interface IJsonValue{JsonValueType ValueType{get;}string GetString();double GetNumber();JsonObject GetObject();}
 public class JsonValue:IJsonValue {
  internal System.Text.Json.Nodes.JsonNode Node;
  public JsonValue(System.Text.Json.Nodes.JsonNode node){Node=node;}
  public JsonValueType ValueType {get{if(Node==null)return JsonValueType.Null;if(Node is System.Text.Json.Nodes.JsonObject)return JsonValueType.Object;if(Node is System.Text.Json.Nodes.JsonArray)return JsonValueType.Array;string s;bool b;return ((System.Text.Json.Nodes.JsonValue)Node).TryGetValue<string>(out s)?JsonValueType.String:((System.Text.Json.Nodes.JsonValue)Node).TryGetValue<bool>(out b)?JsonValueType.Boolean:JsonValueType.Number;}}
  public string GetString(){return Node.GetValue<string>();}public double GetNumber(){return Node.GetValue<double>();}public JsonObject GetObject(){return new JsonObject((System.Text.Json.Nodes.JsonObject)Node);}
  public static JsonValue CreateStringValue(string s){return new JsonValue(System.Text.Json.Nodes.JsonValue.Create(s));}
  public static JsonValue CreateNumberValue(double n){return new JsonValue(System.Text.Json.Nodes.JsonValue.Create(n));}
  public static JsonValue CreateBooleanValue(bool b){return new JsonValue(System.Text.Json.Nodes.JsonValue.Create(b));}
  public string Stringify(){return Node.ToJsonString();}
 }
 public class JsonObject:JsonValue,IEnumerable<KeyValuePair<string,IJsonValue>> {
  public JsonObject():base(new System.Text.Json.Nodes.JsonObject()){}internal JsonObject(System.Text.Json.Nodes.JsonObject node):base(node){}
  public IJsonValue this[string key]{get{return new JsonValue(Node[key]);}set{Node[key]=((JsonValue)value).Node?.DeepClone();}}
  public bool ContainsKey(string key){return ((System.Text.Json.Nodes.JsonObject)Node).ContainsKey(key);}
  public string GetNamedString(string key){return Node[key].GetValue<string>();}public bool GetNamedBoolean(string key){return Node[key].GetValue<bool>();}
  public IJsonValue GetNamedValue(string key){return this[key];}public JsonArray GetNamedArray(string key){return new JsonArray((System.Text.Json.Nodes.JsonArray)Node[key]);}
  public JsonObject GetNamedObject(string key){return new JsonObject((System.Text.Json.Nodes.JsonObject)Node[key]);}
  public static JsonObject Parse(string json){return new JsonObject((System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse(json));}
  public IEnumerator<KeyValuePair<string,IJsonValue>> GetEnumerator(){foreach(var p in (System.Text.Json.Nodes.JsonObject)Node)yield return new KeyValuePair<string,IJsonValue>(p.Key,new JsonValue(p.Value));}
  System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator(){return GetEnumerator();}
 }
 public class JsonArray:JsonValue,IEnumerable<IJsonValue>{
  public JsonArray():base(new System.Text.Json.Nodes.JsonArray()){}internal JsonArray(System.Text.Json.Nodes.JsonArray node):base(node){}
  public void Add(IJsonValue item){((System.Text.Json.Nodes.JsonArray)Node).Add(((JsonValue)item).Node.DeepClone());}
  public IEnumerator<IJsonValue> GetEnumerator(){foreach(var n in (System.Text.Json.Nodes.JsonArray)Node)yield return new JsonValue(n);}
  System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator(){return GetEnumerator();}
 }
}
namespace LumINE.Models {
__REACTION__
 public class Message {
  public string Id,From,To,Text,RelatedMessageId,ReplyPreview,MediaKeyMaterial,DateLabel,TimeLabel,ReadLabel;
  public int ContentType;public int? RelationType;public long? CreatedTime;
  public bool ReactionsKnown,IsDateSeparator,IsMine;
  public object MediaImage;
  public string SenderName;public Windows.UI.Xaml.Media.ImageSource SenderAvatar;
  public List<byte[]> Chunks;
  public Dictionary<string,string> ContentMetadata;
  public List<MessageReaction> Reactions=new List<MessageReaction>();
  public bool IsRevoked {get{string state;return ContentMetadata!=null&&ContentMetadata.TryGetValue("messageState",out state)&&state=="revoked";}}
  public string MediaFallbackText {get{return "media";}}
 }
}
namespace LumINE.Utils {public static class ForegroundUi{public static bool IsReady=true;}}
namespace Windows.UI.Xaml.Media {public class ImageSource{}}
namespace LumINE.Services.Vyline {
 public static class AvatarCache {public static Task<Windows.UI.Xaml.Media.ImageSource> GetAsync(string account,string path){return Task.FromResult(new Windows.UI.Xaml.Media.ImageSource());}}
 public class BatchTalk {
  public int Calls,LastDistinctSenders;
  public Task<List<object>> GetContactsAsync(List<string> mids){Calls++;LastDistinctSenders=mids.Distinct().Count();return Task.FromResult(mids.Select(mid=>(object)new Dictionary<int,object>{{1,mid},{27,"Name:"+mid}}).ToList());}
 }
 public class LineSession {public string Mid="me",AccountId="test";public ChatStore Store=new ChatStore();public BatchTalk Talk=new BatchTalk();}
 public class ChatStore {
  object _lock=new object();Dictionary<string,List<Message>> _messages=new Dictionary<string,List<Message>>(StringComparer.Ordinal);
  void UpdateActivityLocked(string chat,Message latest){}
  public List<Message> GetCachedMessages(string chat){lock(_lock){List<Message> items;return _messages.TryGetValue(chat,out items)?items.ToList():new List<Message>();}}
__MERGE__
__CACHE__
  public static Message RoundTrip(Message message){return DeserializeMessage(Windows.Data.Json.JsonObject.Parse(SerializeMessage(message).Stringify()));}
 }
}
public class BatchView {
 string _chatMid="cgroup";bool _historyLoaded=true;
 HashSet<Message> _mediaLoads=new HashSet<Message>();
 SemaphoreSlim _senderGate=new SemaphoreSlim(1,1),_mediaGate=new SemaphoreSlim(4,4);
 Dictionary<string,ChatStoreSenderInfo> _senderCache=new Dictionary<string,ChatStoreSenderInfo>();
 Task _senderTask=Task.CompletedTask;bool _senderRefreshPending;
 class ChatStoreSenderInfo {public string Name,PicturePath;public bool AvatarLoaded;public Windows.UI.Xaml.Media.ImageSource Avatar;}
 public LumINE.ViewModels.MessageCollection Messages=new LumINE.ViewModels.MessageCollection();
 public event Action MessagesChanged;
 Task LoadMessageImageAsync(LineSession session,Message message){return Task.CompletedTask;}
 void PrepareMedia(Message message){}
__SENDERS__
 public void Publish(LineSession session,List<Message> snapshot){DisplayMessages(session,snapshot);}
 public void LatestOnly(){_historyLoaded=false;}
__DISPLAY__
}
public static class BatchChecks {
 static int checks;
 static void Check(bool value,string message){if(!value)throw new Exception(message);checks++;}
 static Message Make(string id,long? time,string text="text"){return new Message{Id=id,CreatedTime=time,Text=text,From="upeer",To="me"};}
 static long Time(Message m){long value=m.CreatedTime??0;return value>0&&value<100000000000L?value*1000:value;}
 static int Oracle(Message a,Message b){
  int time=Time(a).CompareTo(Time(b));if(time!=0)return time;
  bool an=!string.IsNullOrEmpty(a.Id)&&a.Id.All(c=>c>='0'&&c<='9'),bn=!string.IsNullOrEmpty(b.Id)&&b.Id.All(c=>c>='0'&&c<='9');
  if(an!=bn)return an?-1:1;
  if(an){int num=System.Numerics.BigInteger.Parse(a.Id).CompareTo(System.Numerics.BigInteger.Parse(b.Id));if(num!=0)return num;}
  return string.CompareOrdinal(a.Id,b.Id);
 }
 static void Same(IList<Message> actual,IList<Message> expected,string context){
  Check(actual.Count==expected.Count,context+": count/duplicates differ");
  Check(actual.Select(m=>m.Id).SequenceEqual(expected.Select(m=>m.Id)),context+": chronological order differs");
 }
 public static void Run(){
  var random=new Random(57231);var all=new List<Message>();
  for(int i=0;i<1000;i++){
   string id=(System.Numerics.BigInteger.Parse("9223372036854775808")+i).ToString();
   long time=1700000000000L+(i%23)*1000;all.Add(Make(id,i%3==0?time/1000:time));
  }
  all.Add(Make("9",1700000000000));all.Add(Make("10",1700000000));all.Add(Make("0009",1700000000000));all.Add(Make("pending-a",1700000000000));all.Add(Make("a",1700000000000));
  all.Add(Make("missing-time",null));all.Add(Make("zero-time",0));
  var expected=all.ToList();expected.Sort(Oracle);
  for(int trial=0;trial<200;trial++){
   var store=new ChatStore();var shuffled=all.OrderBy(_=>random.Next()).ToList();
   for(int index=0;index<shuffled.Count;index+=37){
    var page=shuffled.Skip(index).Take(37).ToList();page.AddRange(page.Take(5));page.Add(null);page.Add(Make("",1));
    store.MergeMessages("cgroup",page);
   }
   Same(store.GetCachedMessages("cgroup"),expected,"Random page arrival "+trial);
   Same(MessageTimeline.Normalize(shuffled.Concat(shuffled).Concat(new Message[]{null,Make("",1)})),expected,"Normalize shuffled overlap "+trial);
  }
  var concurrent=new ChatStore();
  var workers=Enumerable.Range(0,8).Select(worker=>Task.Run(()=>{
   for(int start=worker;start<all.Count;start+=8){concurrent.MergeMessages("cgroup",new List<Message>{all[start]});if(start%2==0)concurrent.MergeMessages("cgroup",new List<Message>{all[start]});}
  })).ToArray();Task.WaitAll(workers);
  Same(concurrent.GetCachedMessages("cgroup"),expected,"Concurrent history/live merge");
  var stable=concurrent.GetCachedMessages("cgroup");var first=stable.First(m=>m.Id==all[0].Id);
  concurrent.MergeMessages("cgroup",new List<Message>{Make(first.Id,null,first.Text)});
  Check(concurrent.GetCachedMessages("cgroup").First(m=>m.Id==first.Id).CreatedTime==first.CreatedTime,"Partial refresh lost the cached timestamp.");
  Same(concurrent.GetCachedMessages("cgroup"),expected,"Missing timestamp update");
  var ids=new[]{"2","10","1a","0010","9","a","92233720368547758080","000","0"};
  foreach(var a in ids)foreach(var b in ids)foreach(var c in ids){
   int ab=ChatActivityProtocol.Compare(1,a,1,b),bc=ChatActivityProtocol.Compare(1,b,1,c),ac=ChatActivityProtocol.Compare(1,a,1,c);
   Check(!(ab<=0&&bc<=0)||ac<=0,"Mixed numeric/non-numeric ID comparison is not transitive.");
  }
  var collection=new LumINE.ViewModels.MessageCollection();int notifications=0;collection.CollectionChanged+=(s,e)=>{notifications++;Check(e.NewItems==null||e.NewItems.Count==1,"UWP cannot consume multi-item Add events.");};
  var many=Enumerable.Range(0,5000).Select(i=>Make(i.ToString(),i)).ToList();
  Check(collection.ApplySnapshot(many)&&notifications==1,"Initial 5000-message load must publish one batch.");
  Check(!collection.ApplySnapshot(many)&&notifications==1,"An unchanged snapshot must not rebuild the list.");
  many.Add(Make("5000",5000));collection.ApplySnapshot(many);
  Check(notifications==2&&object.ReferenceEquals(collection[0],many[0]),"Single live append lost item identity or produced a reset sequence.");
  many.InsertRange(0,Enumerable.Range(0,1000).Select(i=>Make("older"+i,-i)).ToList());collection.ApplySnapshot(many);
  Check(notifications==3,"Prepending a history page emitted per-message notifications.");
  Same(collection,many,"Batch collection after prepend");
  var replacement=Make(many[100].Id,many[100].CreatedTime,"edited");many[100]=replacement;collection.ApplySnapshot(many);
  Check(notifications==4&&object.ReferenceEquals(collection[100],replacement),"An isolated change must use one replacement notification.");
  var session=new LineSession();session.Store.MergeMessages("cgroup",new List<Message>{Make("100",1700000000000),Make("101",1700086400000)});
  var ui=new BatchView();int renders=0,scrolls=0;ui.Messages.CollectionChanged+=(s,e)=>renders++;ui.MessagesChanged+=()=>scrolls++;
  var stale=session.Store.GetCachedMessages("cgroup");ui.Publish(session,stale);
  Check(ui.Messages.Count(m=>m.IsDateSeparator)==2&&renders==1,"Date boundaries or initial batching are incorrect.");
  ui.Publish(session,stale);Check(renders==1&&scrolls==1,"Equivalent date separators triggered a full reload/scroll.");
  session.Store.MergeMessages("cgroup",new List<Message>{Make("102",1700086401000)});
  ui.Publish(session,stale);
  Check(ui.Messages.Last().Id=="102"&&ui.Messages.Count(m=>!m.IsDateSeparator)==3,"Late history completion erased a new live message.");
  Same(ui.Messages.Where(m=>!m.IsDateSeparator).ToList(),session.Store.GetCachedMessages("cgroup"),"Published live/history snapshot");
  var tail=new LineSession();tail.Store.MergeMessages("cgroup",all);var tailUi=new BatchView();tailUi.LatestOnly();tailUi.Publish(tail,new List<Message>());
  Same(tailUi.Messages.Where(m=>!m.IsDateSeparator).ToList(),expected.Skip(expected.Count-50).ToList(),"Fast initial tail");
  // All culture-sensitive ordering paths must still produce the same sequence.
  foreach(string culture in new[]{"ja-JP","en-US","tr-TR"}){CultureInfo.CurrentCulture=new CultureInfo(culture);Same(MessageTimeline.Normalize(all),expected,"Culture "+culture);}
  var stored=Make("cache",1700000000000);stored.RelatedMessageId="100";stored.ReplyPreview="quoted";stored.ReactionsKnown=true;
  stored.ContentMetadata=new Dictionary<string,string>{{"read","1"},{"readCount","3"}};
  stored.Chunks=new List<byte[]>{new byte[]{0,255,128}};
  stored.Reactions.Add(new MessageReaction { FromMid="upeer",ProductId="670e0cce840a8236ddd4ee4c",EmojiId="143",ResourceType=2,Version=9007199254740993L,AtMillis=1700000000001 });
  var restored=ChatStore.RoundTrip(stored);
  Check(restored.ReactionsKnown&&restored.Reactions[0].Key==stored.Reactions[0].Key&&restored.Reactions[0].Version==9007199254740993L&&restored.Reactions[0].ResourceType==2,"Custom reaction cache dropped fields/precision.");
  Check(restored.RelatedMessageId=="100"&&restored.ReplyPreview=="quoted"&&restored.ContentMetadata["readCount"]=="3"&&restored.Chunks[0].SequenceEqual(stored.Chunks[0]),"Message caching regressed quote/read/encrypted data.");
  var grouped=new LineSession();var groupMessages=Enumerable.Range(0,5000).Select(i=>{var m=Make(i.ToString(),1700000000000L+i);m.From="user"+(i%100);return m;}).ToList();
  grouped.Store.MergeMessages("cgroup",groupMessages);var groupUi=new BatchView();groupUi.Publish(grouped,groupMessages);
  Check(grouped.Talk.Calls==1&&grouped.Talk.LastDistinctSenders==100,"5000 messages must batch contacts by 100 unique senders, not one request per message.");
  Check(groupUi.Messages.Where(m=>!m.IsDateSeparator).All(m=>m.SenderName=="Name:"+m.From),"Batched sender data was assigned to the wrong message.");
  groupUi.Publish(grouped,groupMessages);Check(grouped.Talk.Calls==1,"Unchanged history fetched sender profiles again.");
  Console.WriteLine("PASS: "+checks+" assertions; 200 randomized history/duplicate schedules, concurrent live merge, comparator transitivity, 5000-message batching and date/publication checks.");
 }
}
'@
$display = @('DisplayMessages','GetLocalDate','GetTimeLabel','GetReadLabel') | ForEach-Object { Method $view $_ }
$cache = @('SerializeMessage','DeserializeMessage','GetJsonStr','GetJsonLong') | ForEach-Object { Method $store $_ }
$senders = @('QueueSenderInfo','LoadSenderInfosAsync','LoadSenderAvatarAsync','GetField') | ForEach-Object { Method $view $_ }
$fixture = $fixture.Replace('__REACTION__',$reaction).Replace('__MERGE__',(Method $store 'MergeMessages')).Replace('__DISPLAY__',($display -join "`n")).Replace('__CACHE__',($cache -join "`n")).Replace('__SENDERS__',($senders -join "`n"))
$source = 'using System;using System.IO;using System.Text;using System.Linq;using System.Threading;using System.Threading.Tasks;using System.Collections.Generic;using System.Collections.ObjectModel;using System.Collections.Specialized;using System.ComponentModel;using System.Globalization;using LumINE.Models;using LumINE.Services.Vyline;'
foreach($p in @('LumINE/Services/Vyline/ChatActivityProtocol.cs','LumINE/Services/Vyline/MessageTimeline.cs','LumINE/ViewModels/MessageCollection.cs')) { $source += Read-Source $p }
Add-Type -TypeDefinition ($source+$fixture)
[BatchChecks]::Run()
