$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
function Read-Source([string] $relativePath) {
    $source = Get-Content -LiteralPath (Join-Path $repoRoot $relativePath) -Raw
    return [regex]::Replace($source, '(?m)^using [^\r\n]+;\r?\n', '')
}
$processor = Read-Source 'LumINE/Services/Vyline/OperationProcessor.cs'
$buffer = Read-Source 'LumINE/Services/Vyline/TalkEventBuffer.cs'
$models = Read-Source 'LumINE/Models/LineModels.cs'
$foreground = Read-Source 'LumINE/Utils/ForegroundUi.cs'
$reaction = Read-Source 'LumINE/Services/Vyline/ReactionProtocol.cs'
$reactionEvents = Read-Source 'LumINE/Services/Vyline/ReactionEventDecoder.cs'
$activity = Read-Source 'LumINE/Services/Vyline/ChatActivityProtocol.cs'
$thrift = Read-Source 'LumINE/Utils/ThriftTypes.cs'
$cursor = Read-Source 'LumINE/Services/Vyline/OpsRevisionCursor.cs'
# Compile the actual protocol methods, excluding only UWP disk persistence.
$cursor = $cursor.Substring(0, $cursor.IndexOf('        public static string CursorPath')) + "`n    }`n}"
$poller = Read-Source 'LumINE/Services/Vyline/OpsPoller.cs'
$poller = 'namespace LumINE.Services.Vyline { public class OpsPoller { ' + $poller.Substring($poller.IndexOf('        public static List<Dictionary<int, object>> ExtractOperations'))
$fixtures = @'
namespace LumINE.Utils { public static class CrashDiagnostics { public static void Record(string stage,Exception error) {} } }
// Portable adapter for the WinRT JSON API used by the production decoder.
namespace Windows.Data.Json {
    public enum JsonValueType { Null, String, Object, Number }
    public interface IJsonValue { JsonValueType ValueType { get; } string GetString(); string Stringify(); JsonObject GetObject(); }
    public class JsonValue : IJsonValue {
        protected System.Text.Json.JsonElement Element;
        public JsonValue(System.Text.Json.JsonElement element) { Element = element.Clone(); }
        public JsonValueType ValueType { get { switch (Element.ValueKind) {
            case System.Text.Json.JsonValueKind.Object: return JsonValueType.Object;
            case System.Text.Json.JsonValueKind.String: return JsonValueType.String;
            case System.Text.Json.JsonValueKind.Number: return JsonValueType.Number;
            default: return JsonValueType.Null; } } }
        public string GetString() { return Element.GetString(); }
        public string Stringify() { return Element.GetRawText(); }
        public JsonObject GetObject() { return new JsonObject(Element); }
    }
    public class JsonObject : JsonValue {
        public JsonObject(System.Text.Json.JsonElement element) : base(element) { }
        public static bool TryParse(string value, out JsonObject result) {
            try { using (var doc = System.Text.Json.JsonDocument.Parse(value)) { result = new JsonObject(doc.RootElement); return doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object; } }
            catch { result = null; return false; }
        }
        public bool ContainsKey(string key) { System.Text.Json.JsonElement value; return Element.TryGetProperty(key, out value); }
        public bool TryGetValue(string key, out IJsonValue value) { System.Text.Json.JsonElement raw; bool found = Element.TryGetProperty(key, out raw); value = found ? new JsonValue(raw) : null; return found; }
        public IJsonValue GetNamedValue(string key) { return new JsonValue(Element.GetProperty(key)); }
        public string GetNamedString(string key, string fallback) { System.Text.Json.JsonElement value; return Element.TryGetProperty(key, out value) ? value.GetString() : fallback; }
    }
}
namespace Windows.UI.Xaml.Media { public class ImageSource { } }
namespace Windows.UI.Core {
    public enum CoreDispatcherPriority { Normal }
    public class TestDispatcher { public bool HasThreadAccess = true; public object RunAsync(CoreDispatcherPriority priority, Action action) { action(); return null; } }
}
namespace Windows.ApplicationModel.Core {
    public static class CoreApplication { public static TestMainView MainView = new TestMainView(); }
    public class TestMainView { public Windows.UI.Core.TestDispatcher Dispatcher = new Windows.UI.Core.TestDispatcher(); }
}
namespace Windows.UI.Xaml {
    public class Window { public static Window Current = new Window(); public BoundsValue Bounds = new BoundsValue(); }
    public class BoundsValue { public double Width = 360; }
}
namespace LumINE.Services.Vyline {
    public class LineSession {
        public string Mid = "umyself"; public string AccountId = "test";
        public TalkEventBuffer Events = new TalkEventBuffer(); public ChatStore Store = new ChatStore();
    }
    public class ChatStore {
        public int Revokes;
        public Task ApplyRemoteRevokeAsync(string chat, string id) { Revokes++; return Task.CompletedTask; }
        public Task ApplyReactionAsync(string chat, string id, string actor, int type, long at) { return Task.CompletedTask; }
        public Task ApplyCustomReactionAsync(string chat, string id, string actor, LumINE.Models.MessageReaction reaction, long at) { return Task.CompletedTask; }
        public static object MapMessage(Dictionary<int, object> raw, string chat) { return raw; }
    }
    public static class BackgroundNotifications {
        public static int Observed;
        public static Task NotifyAsync(LineSession session, object message) { Observed++; return Task.CompletedTask; }
    }
}
public static class MessageProtocolChecks {
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    public static void Run() {
        var session = new LumINE.Services.Vyline.LineSession();
        var processor = new LumINE.Services.Vyline.OperationProcessor(session);
        var rawMessage = new Dictionary<int, object> { {1,"upeer"}, {2,"umyself"}, {4,"10001"}, {5,1700000000000L}, {10,"hello"}, {15,0} };
        var incoming = new Dictionary<int, object> { {1,123456789L}, {2,1700000000000L}, {3,26}, {20,rawMessage} };
        var message = processor.ProcessSingleAsync(incoming).GetAwaiter().GetResult();
        Check(message != null && message.Kind == LumINE.Services.Vyline.TalkEventKind.Message && message.ChatMid == "upeer" && message.MessageId == "10001", "Wire RECEIVE_MESSAGE was not decoded.");
        var revoke = new Dictionary<int, object> { {1,123456790L}, {2,1700000000001L}, {3,65}, {10,"upeer"}, {11,"10001"} };
        var revoked = processor.ProcessSingleAsync(revoke).GetAwaiter().GetResult();
        Check(revoked != null && revoked.Kind == LumINE.Services.Vyline.TalkEventKind.Revoke && revoked.ChatMid == "upeer" && revoked.MessageId == "10001", "Wire remote revoke was not decoded.");
        var ownRevoke = new Dictionary<int, object> { {1,123456791L}, {3,64}, {10,"10001"}, {11,"upeer"} };
        Check(processor.ProcessSingleAsync(ownRevoke).GetAwaiter().GetResult().Kind == LumINE.Services.Vyline.TalkEventKind.Revoke, "Own revoke layout was not decoded.");
        var read = new Dictionary<int, object> { {1,123456792L}, {3,55}, {10,"upeer"}, {11,"upeer"} };
        Check(processor.ProcessSingleAsync(read).GetAwaiter().GetResult().Kind == LumINE.Services.Vyline.TalkEventKind.Read, "Read operation was not decoded.");
        var profile = new Dictionary<int, object> { {1,26L}, {2,1700000000000L}, {3,1} };
        Check(processor.ProcessSingleAsync(profile).GetAwaiter().GetResult() == null, "Revision 26 was incorrectly decoded as a message type.");
        processor.ProcessAsync(new List<Dictionary<int, object>> { incoming, revoke }).GetAwaiter().GetResult();
        Check(LumINE.Services.Vyline.BackgroundNotifications.Observed == 1 && session.Store.Revokes == 1 && session.Events.CurrentSeq == 2, "Notification/revoke commit pipeline failed.");
        var operations = new List<object> { incoming, revoke };
        var response = new Dictionary<int, object> { {1,new Dictionary<int, object> {
            {1,operations}, {2,false}, {3,new Dictionary<int, object> {{1,new Dictionary<object,object>()},{2,900L}}},
            {4,new Dictionary<int, object> {{1,new List<object>()},{2,700L}}}
        }} };
        var next = LumINE.Services.Vyline.OpsRevisionCursor.Next(new LumINE.Services.Vyline.OpsRevisionCursor(), response);
        Check(next.Revision == 123456790L && next.GlobalRev == 900 && next.IndividualRev == 700, "Wire sync cursor fields were not decoded.");
        Check(LumINE.Services.Vyline.OpsPoller.ExtractOperations(response).Count == 2, "Operations array extraction failed.");
        var full = new Dictionary<int, object> { {2,new Dictionary<int, object> {{1,new List<object>{1}},{2,1000L}}} };
        Check(LumINE.Services.Vyline.OpsRevisionCursor.Next(new LumINE.Services.Vyline.OpsRevisionCursor(), full).Revision == 1000, "Full sync nextRevision decoded as reasons instead.");
        var empty = new LumINE.Models.Message { Text = null, ContentType = 0 };
        Check(empty.IsRevoked && empty.IsText, "Empty unencrypted NONE tombstone was not rendered.");
        var encrypted = new LumINE.Models.Message { ContentType = 0, Chunks = new List<byte[]> { new byte[16] } };
        Check(!encrypted.IsRevoked, "Encrypted body was incorrectly marked as unsent.");
        var sticker = new LumINE.Models.Message { ContentType = 7, ContentMetadata = new Dictionary<string,string> {{"UNSENT","1"}}, MediaImage = new Windows.UI.Xaml.Media.ImageSource() };
        Check(sticker.IsRevoked && !sticker.HasMediaImage && sticker.IsText && sticker.BodyFontSize == 10, "Unsent sticker still displays its image.");
        var args = LumINE.Services.Vyline.ReactionProtocol.BuildArgs(9, "123456789012345", 3);
        var request = (List<LumINE.Utils.ThriftField>)args[0].Value;
        Check(args[0].Type == 12 && args[0].FieldId == 1 && request.Count == 3 && request[1].Type == 10 && (long)request[1].Value == 123456789012345L, "Reaction target must be a nested I64.");
        Check((int)((List<LumINE.Utils.ThriftField>)request[2].Value)[0].Value == 3 && ((List<LumINE.Utils.ThriftField>)LumINE.Services.Vyline.ReactionProtocol.BuildArgs(10, "1", 0)[0].Value).Count == 2, "Reaction and cancellation schemas differ.");
        bool rejected = false;
        try { LumINE.Services.Vyline.ReactionProtocol.BuildArgs(1, "pending-id", 2); } catch (ArgumentException) { rejected = true; }
        Check(rejected, "Pending messages cannot be reacted to.");
        var reactions = LumINE.Services.Vyline.ReactionProtocol.Read(new List<object> {
            new Dictionary<int,object>{{1,"upeer"},{2,2L},{3,new Dictionary<int,object>{{1,3}}}},
            new Dictionary<int,object>{{1,"upeer"},{2,1L},{3,new Dictionary<int,object>{{1,2}}}},
            new Dictionary<int,object>{{1,"uother"},{2,3L},{3,new Dictionary<int,object>{{1,3}}}}
        });
        var reacted = new LumINE.Models.Message { Text = "test", ReactionsKnown = true, Reactions = reactions };
        Check(reactions.Count == 2 && reacted.ReactionSummary.Contains("2") && reacted.HasReactions, "Duplicate actors inflated reaction totals.");
        var reactionOp = new Dictionary<int,object>{{1,123456793L},{2,1700000000020L},{3,140},{10,"10001"},{11,"{\"chatMid\":\"upeer\",\"curr\":{\"predefinedReactionType\":3}}"},{12,"upeer"}};
        var reactionEvent = processor.ProcessSingleAsync(reactionOp).GetAwaiter().GetResult();
        Check(reactionEvent.ChatMid == "upeer" && reactionEvent.ReactionType == 3 && reactionEvent.ReactionActor == "upeer" && reactionEvent.ReactionAtMillis == 1700000000020L, "Wire JSON reaction event decoding failed.");
        var cancelled = LumINE.Services.Vyline.ReactionEventDecoder.Parse("139", "{\"chatMid\":\"cgroup\",\"curr\":null}", "", "umyself");
        Check(cancelled.Type == 0 && cancelled.ActorMid == "umyself", "Own reaction cancellation failed.");
        Check(LumINE.Services.Vyline.ReactionEventDecoder.Parse("154", "{\"chatMid\":\"cgroup\",\"curr\":{\"predefinedReactionType\":2}}", "", "umyself").ActorMid == "", "Missing group actor must not be guessed.");
        var newer = new Dictionary<int,object>{{1,"upeer"},{2,"umyself"},{4,"10002"},{5,1700000010000L}};
        var box = new Dictionary<int,object>{{1,"upeer"},{10,new List<object>{newer,rawMessage}},{6,new List<object>{"not a message"}}};
        Check(object.ReferenceEquals(LumINE.Services.Vyline.ChatActivityProtocol.LatestMessage(box), newer), "Message-box latest selection depends on array ordering.");
        Check(LumINE.Services.Vyline.ChatActivityProtocol.Compare(1700000000L,"100",1700000000000L,"99") > 0, "Timestamp normalization or numeric ID tie-break failed.");
        var custom = new LumINE.Models.MessageReaction { ProductId = "670e0cce840a8236ddd4ee4c", EmojiId = "143", ResourceType = 2, Version = 9007199254740993L };
        var customRequest = (List<LumINE.Utils.ThriftField>)LumINE.Services.Vyline.ReactionProtocol.BuildArgs(11, "123", custom)[0].Value;
        var union = (List<LumINE.Utils.ThriftField>)customRequest[2].Value;
        var paid = (List<LumINE.Utils.ThriftField>)union[0].Value;
        Check(union[0].FieldId == 2 && union[0].Type == 12 && paid[0].FieldId == 1 && paid[1].FieldId == 2 && (int)paid[2].Value == 2 && paid[3].Type == 10 && (long)paid[3].Value == 9007199254740993L, "Owned emoji must use the paid union and preserve I64 version.");
        var customFields = new Dictionary<int,object>{{2,new Dictionary<int,object>{{1,custom.ProductId},{2,custom.EmojiId},{3,2},{4,custom.Version.Value}}}};
        var customHistory = LumINE.Services.Vyline.ReactionProtocol.Read(new List<object>{new Dictionary<int,object>{{1,"upeer"},{2,4L},{3,customFields}},new Dictionary<int,object>{{1,"uother"},{2,5L},{3,customFields}}});
        var customMessage = new LumINE.Models.Message { Text="paid emoji", Reactions=customHistory };
        Check(customHistory.Count == 2 && customMessage.ReactionBadges.Count == 1 && customMessage.ReactionBadges[0].Count == 2 && customMessage.ReactionBadges[0].EmojiId == "143", "Custom emoji grouping merged different formats or lost counts.");
        Check(customMessage.ReactionBadges[0].ImageUrl.EndsWith("/670e0cce840a8236ddd4ee4c/android/143.png") && customHistory[0].Version == custom.Version, "Custom emoji display or history metadata was lost.");
        var customEvent = LumINE.Services.Vyline.ReactionEventDecoder.Parse("140", "{\"chatMid\":\"cgroup\",\"curr\":{\"paidReactionType\":{\"productId\":\"670e0cce840a8236ddd4ee4c\",\"emojiId\":\"143\",\"resourceType\":2,\"version\":\"9007199254740993\"}}}", "upeer", "umyself");
        Check(customEvent.Type == null && customEvent.CustomReaction != null && customEvent.CustomReaction.Version == custom.Version && customEvent.CustomReaction.Key == custom.Key, "Custom realtime JSON event lost its union or precise version.");
        var malformedCustom = LumINE.Services.Vyline.ReactionEventDecoder.Parse("140", "{\"chatMid\":\"cgroup\",\"curr\":{\"paidReactionType\":{\"productId\":\"../bad\",\"emojiId\":\"143\"}}}", "upeer", "umyself");
        Check(malformedCustom.CustomReaction == null && malformedCustom.Type == null, "Malformed custom events must request refresh rather than cancel a reaction.");
        customMessage.ContentMetadata = new Dictionary<string,string>{{"messageState","revoked"}};
        Check(customMessage.ReactionBadges.Count == 0, "Revoked messages still display paid reactions.");
        customHistory.Add(new LumINE.Models.MessageReaction { Type=3,FromMid="uthird" });
        Check(new LumINE.Models.Message { Text="mixed",Reactions=customHistory }.ReactionBadges.Count == 2, "Custom and predefined reactions must remain separate.");
        bool customRejected=false;
        try { LumINE.Services.Vyline.ReactionProtocol.BuildArgs(1,"123",new LumINE.Models.MessageReaction { ProductId="../bad",EmojiId="1" }); } catch(ArgumentException) { customRejected=true; }
        Check(customRejected,"Invalid emoji IDs may not enter URLs or the send RPC.");
        LumINE.Utils.ForegroundUi.Resume(LumINE.Utils.ForegroundUi.Generation);
        var binding=new LumINE.Models.Message { Text="before" };int changes=0;
        binding.PropertyChanged+=(s,e)=>{if(e.PropertyName=="DisplayText")changes++;};
        binding.Text="after";binding.Text="after";
        Check(changes==1&&binding.DisplayText=="after","Batched/no-op refresh must still notify when decryption changes text in place.");
    }
}
'@
$source = 'using System; using System.Collections.Generic; using System.Threading; using System.Threading.Tasks; using System.IO; using System.Text; using LumINE.Models; using LumINE.Utils; using Windows.Data.Json;' + $models + $buffer + $processor + $cursor + $poller + $reaction + $reactionEvents + $activity + $thrift + $fixtures
Add-Type -TypeDefinition ($source + $foreground) -IgnoreWarnings
[MessageProtocolChecks]::Run()
Write-Output 'PASS: 30 checks covering wire operations, custom/predefined reactions, latest-message ordering, notification dispatch, sync cursors and unsent messages.'
