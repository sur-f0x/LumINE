$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
function Method([string] $path,[string] $signature) {
    $text=Get-Content (Join-Path $repoRoot $path) -Raw; $start=$text.IndexOf($signature)
    if($start -lt 0) { throw "Missing $signature" }; $end=$text.IndexOf('{',$start)+1; $depth=1
    while($depth -gt 0) { if($text[$end] -eq '{'){$depth++}; if($text[$end] -eq '}'){$depth--}; $end++ }
    $text.Substring($start,$end-$start)
}
$transfer=@('public static async Task<LumINE.Models.Message> SendImageAsync(', 'private static async Task<string> UploadImageObjectAsync(', 'private static async Task CacheSentPhotoAsync(', 'private static async Task<LumINE.Models.Message> SendPlainImageAsync(', 'private static async Task<byte[]> DownloadMessageImageAsync(', 'private static string Metadata(', 'private static void Append(', 'private static string Sanitize(') | ForEach-Object { Method 'LumINE/Services/Vyline/MessageMediaCache.cs' $_ }
$crypto=@('public static byte[] EncryptMediaKeyMaterial(', 'public static byte[] DecryptMediaKeyMaterial(', 'private static bool BytesEqual(', 'public static void EncryptTextMessage(') | ForEach-Object { Method 'LumINE/Utils/E2ee.cs' $_ }
$fixture=@'
public class JsonValue {
    public object Value;
    public static JsonValue CreateStringValue(string value) { return new JsonValue { Value=value }; }
    public static JsonValue CreateNumberValue(double value) { return new JsonValue { Value=value }; }
    public static JsonValue CreateBooleanValue(bool value) { return new JsonValue { Value=value }; }
}
public class JsonObject : Dictionary<string,JsonValue> { public string Stringify() { return System.Text.Json.JsonSerializer.Serialize(this.ToDictionary(p=>p.Key,p=>p.Value.Value)); } }
public class BufferValue { public byte[] Bytes; public uint Length { get { return (uint)Bytes.Length; } } }
public static class Adapters {
    public static BufferValue AsBuffer(this byte[] bytes) { return new BufferValue { Bytes=bytes }; }
    public static Task<T> AsTask<T>(this Task<T> task,CancellationToken cancellation) { return task; }
}
public class DataReader { byte[] Bytes; public static DataReader FromBuffer(BufferValue b) { return new DataReader { Bytes=b.Bytes }; } public void ReadBytes(byte[] b) { Bytes.CopyTo(b,0); } }
public class Headers : Dictionary<string,string> {
    public HttpMediaTypeHeaderValue ContentType;
    public bool TryAppendWithoutValidation(string name,string value) { this[name]=value; return true; }
    public Headers() : base(StringComparer.OrdinalIgnoreCase) {}
}
public class HttpMediaTypeHeaderValue { public HttpMediaTypeHeaderValue(string value) {} }
public class HttpBufferContent {
    public Headers Headers=new Headers(); public byte[] Bytes;
    public HttpBufferContent(BufferValue bytes) { Bytes=bytes.Bytes; }
    public Task<string> ReadAsStringAsync() { return Task.FromResult("test error"); }
    public Task<BufferValue> ReadAsBufferAsync() { return Task.FromResult(Bytes.AsBuffer()); }
}
public enum HttpMethod { Get,Post }
public class HttpRequestMessage : IDisposable {
    public Uri Uri; public HttpMethod Method; public Headers Headers=new Headers(); public HttpBufferContent Content;
    public HttpRequestMessage(HttpMethod method,Uri uri) { Method=method; Uri=uri; } public void Dispose() {}
}
public class HttpResponse : IDisposable {
    public int StatusCode=200; public Headers Headers=new Headers(); public HttpBufferContent Content=new HttpBufferContent(new byte[0].AsBuffer()); public void Dispose() {}
}
public class HttpClient : IDisposable {
    public static Queue<HttpResponse> Responses=new Queue<HttpResponse>(); public static List<HttpRequestMessage> Requests=new List<HttpRequestMessage>();
    public Task<HttpResponse> SendRequestAsync(HttpRequestMessage request) { Requests.Add(request); return Task.FromResult(Responses.Dequeue()); } public void Dispose() {}
}
public enum CreationCollisionOption { ReplaceExisting }
public class StorageFile { public byte[] Bytes; }
public class StorageFolder {
    public Dictionary<string,StorageFile> Files=new Dictionary<string,StorageFile>();
    public Task<StorageFile> CreateFileAsync(string name,CreationCollisionOption option) { var f=new StorageFile(); Files[name]=f; return Task.FromResult(f); }
}
public static class FileIO { public static Task WriteBytesAsync(StorageFile file,byte[] bytes) { file.Bytes=bytes; return Task.CompletedTask; } }
namespace LumINE.Models {
    public class Message {
        public string Id,From,To,MediaKeyMaterial; public int ContentType; public long? CreatedTime;
        public Dictionary<string,string> ContentMetadata; public List<byte[]> Chunks;
    }
}
namespace LumINE.Utils {
    public static class CryptoHelper {
        public static byte[] LastAad,LastPayload;
        public static byte[] RandomBytes(int n) { return RandomNumberGenerator.GetBytes(n); }
        public static byte[] HkdfSha256(byte[] ikm,byte[] salt,byte[] info,int n) { return HKDF.DeriveKey(HashAlgorithmName.SHA256,ikm,n,salt??new byte[0],info); }
        public static byte[] HmacSha256(byte[] key,byte[] data) { return HMACSHA256.HashData(key,data); }
        public static byte[] Sha256(params byte[][] data) { return SHA256.HashData(data.SelectMany(b=>b).ToArray()); }
        public static byte[] X25519SharedSecret(byte[] priv,byte[] pub) { return new byte[32]; }
        public static byte[] I32Be(int n) { return new byte[]{(byte)(n>>24),(byte)(n>>16),(byte)(n>>8),(byte)n}; }
        public static void AesGcmEncrypt(byte[] key,byte[] nonce,byte[] aad,byte[] data,out byte[] encrypted,out byte[] tag) { LastAad=aad; LastPayload=data; encrypted=data; tag=new byte[16]; }
        public static byte[] AesCtrEncryptDecrypt(byte[] key,byte[] nonce,byte[] data) {
            var counter=new byte[16]; nonce.CopyTo(counter,0); var output=new byte[data.Length];
            using(var aes=Aes.Create()) { aes.Key=key; for(int at=0;at<data.Length;at+=16) {
                var block=aes.EncryptEcb(counter,PaddingMode.None);
                for(int i=0;i<16 && at+i<data.Length;i++) output[at+i]=(byte)(data[at+i]^block[i]);
                for(int i=15;i>=12;i--) if(++counter[i]!=0) break;
            }} return output;
        }
    }
    public static class E2ee { __CRYPTO__ }
    public class ThriftField { public ThriftField(int t,int id,object value) {} }
    public static class ThriftTypes { public const int String=11,List=15,Struct=12; }
    public class ThriftListValue { public ThriftListValue(int t,List<object> values) {} }
    public static class ThriftBinary { public static byte[] PackBinaryStruct(List<ThriftField> fields) { return new byte[]{1,2,3}; } }
}
namespace LumINE.Services.Vyline {
    public class Peer {}
    public static class E2eeKeyStore { public static Peer ExtractPeerKey(Dictionary<int,object> raw) { return new Peer(); } public static Task SavePeerKeyAsync(string account,string mid,Peer key) { return Task.CompletedTask; } }
    public static class DesktopIdentity { public const string ApplicationHeader="test",UserAgent="test",Lal="ja_JP"; }
    public class Talk {
        public int ObsCalls; public bool Plain;
        public Task<string> GetObsAccessTokenAsync() { ObsCalls++; return Task.FromResult("obs-token"); }
        public Task<Dictionary<int,object>> NegotiateE2eePublicKeyAsync(string mid) { return Task.FromResult(new Dictionary<int,object>{{1,Plain?-1:2}}); }
    }
    public class Line {
        public int Sent,Decrypts; public string Payload,ResolveKey; public Dictionary<string,string> Metadata; public bool MissingAck;
        public Task<Dictionary<int,object>> SendMediaMessageAsync(string mid,string payload,Dictionary<string,string> meta) { Sent++; Payload=payload; Metadata=meta; return Task.FromResult(MissingAck?new Dictionary<int,object>():new Dictionary<int,object>{{4,"server-message"}}); }
        public Task DecryptMessagesAsync(List<LumINE.Models.Message> messages) { Decrypts++; if(ResolveKey!=null) foreach(var m in messages) m.MediaKeyMaterial=ResolveKey; return Task.CompletedTask; }
    }
    public class ChatStore {
        public Dictionary<string,LumINE.Models.Message> Messages=new Dictionary<string,LumINE.Models.Message>(); public int Saves;
        public static LumINE.Models.Message MapMessage(Dictionary<int,object> result,string chat) { object id; return new LumINE.Models.Message { Id=result.TryGetValue(4,out id)?id.ToString():null }; }
        public void UpsertMessage(string chat,LumINE.Models.Message message) { Messages[message.Id]=message; }
        public Task SaveMessagesAsync(string chat) { Saves++; return Task.CompletedTask; } public Task SaveChatActivityAsync() { return Task.CompletedTask; }
    }
    public class LineSession { public string AccountId="test",Mid="umyself",AuthToken="account-token"; public Talk Talk=new Talk(); public Line Line=new Line(); public ChatStore Store=new ChatStore(); public int GetReqseq() { return 123; } }
    public static class MessageMediaCache {
        const string Gateway="https://gwz.line.naver.jp"; public static StorageFolder Folder=new StorageFolder();
        static Task<StorageFolder> GetMediaFolderAsync(string account) { return Task.FromResult(Folder); }
        public static Task<byte[]> Download(LineSession session,LumINE.Models.Message message,int variant) { return DownloadMessageImageAsync(session,message,variant); }
__TRANSFER__
    }
}
public static class MediaChecks {
    static int Count;
    static void Check(bool okay,string message) { if(!okay) throw new Exception(message); Count++; }
    static HttpResponse Reply(string oid=null,int status=200,byte[] bytes=null) { var r=new HttpResponse { StatusCode=status }; if(oid!=null) r.Headers["x-obs-oid"]=oid; if(bytes!=null) r.Content=new HttpBufferContent(bytes.AsBuffer()); return r; }
    static void Reset() { HttpClient.Requests.Clear(); HttpClient.Responses.Clear(); MessageMediaCache.Folder=new StorageFolder(); }
    public static void Run() {
        byte[] photo=Enumerable.Range(0,101).Select(n=>(byte)n).ToArray(), key=Enumerable.Range(0,32).Select(n=>(byte)n).ToArray();
        var encrypted=E2ee.EncryptMediaKeyMaterial(photo,key);
        Check(encrypted.Length==photo.Length+32 && E2ee.DecryptMediaKeyMaterial(encrypted,key).SequenceEqual(photo),"Media encrypt/decrypt changed the image bytes.");
        foreach(int index in new[]{0,encrypted.Length-1}) { var bad=(byte[])encrypted.Clone(); bad[index]^=1; bool rejected=false; try { E2ee.DecryptMediaKeyMaterial(bad,key); } catch(InvalidOperationException) { rejected=true; } Check(rejected,"Corrupt ciphertext/HMAC was accepted."); }
        bool shortRejected=false; try { E2ee.DecryptMediaKeyMaterial(new byte[3],key); } catch(InvalidOperationException) { shortRejected=true; } Check(shortRejected,"Truncated encrypted media was accepted.");
        List<byte[]> chunks; int spec;
        E2ee.EncryptTextMessage("cgroup","umyself","{\"keyMaterial\":\"test\"}",1,new byte[32],new byte[32],1,2,out chunks,out spec,2,true);
        using(var json=System.Text.Json.JsonDocument.Parse(CryptoHelper.LastPayload)) Check(json.RootElement.TryGetProperty("keyMaterial",out var material) && !json.RootElement.TryGetProperty("text",out var text) && CryptoHelper.LastAad.Last()==1,"Media JSON or content type was wrapped as a text message.");
        Reset(); var session=new LineSession(); HttpClient.Responses.Enqueue(Reply("object")); HttpClient.Responses.Enqueue(Reply("object"));
        var sent=MessageMediaCache.SendImageAsync(session,"cgroup",photo,"photo.jpg").GetAwaiter().GetResult();
        Check(session.Line.Sent==1 && sent.Id=="server-message" && sent.ContentType==1 && session.Store.Messages.ContainsKey(sent.Id),"Uploaded image was never sent or added to the local conversation.");
        Check(HttpClient.Requests.Count==2 && HttpClient.Requests[0].Uri.AbsolutePath.Contains("/emi/reqid-") && HttpClient.Requests[1].Uri.AbsolutePath.EndsWith("object__ud-preview"),"Encrypted original/preview routes are incorrect.");
        using(var json=System.Text.Json.JsonDocument.Parse(session.Line.Payload)) {
            var material=Convert.FromBase64String(json.RootElement.GetProperty("keyMaterial").GetString());
            Check(E2ee.DecryptMediaKeyMaterial(HttpClient.Requests[0].Content.Bytes,material).SequenceEqual(photo) && HttpClient.Requests[0].Content.Bytes.SequenceEqual(HttpClient.Requests[1].Content.Bytes),"Uploaded objects cannot be decrypted with the message's keyMaterial.");
        }
        Check(session.Line.Metadata["SID"]=="emi" && session.Line.Metadata["OID"]=="object" && MessageMediaCache.Folder.Files["message-original-v1-server-message.img"].Bytes.SequenceEqual(photo),"Media object metadata or original cache is missing.");
        using(var info=System.Text.Json.JsonDocument.Parse(session.Line.Metadata["MEDIA_CONTENT_INFO"]))
            Check(info.RootElement.GetProperty("extension").GetString()=="jpg" && info.RootElement.GetProperty("fileSize").GetInt32()==encrypted.Length && !info.RootElement.GetProperty("animated").GetBoolean(),"Image content metadata was not advertised to other LINE clients.");
        foreach(var failure in new[]{Reply(null),Reply(null,403)}) {
            Reset(); session=new LineSession(); HttpClient.Responses.Enqueue(failure); bool rejected=false;
            try { MessageMediaCache.SendImageAsync(session,"cgroup",photo,"photo.jpg").GetAwaiter().GetResult(); } catch(InvalidOperationException) { rejected=true; }
            Check(rejected && session.Line.Sent==0 && session.Store.Messages.Count==0,"Failed/unacknowledged upload was reported as a sent message.");
        }
        Reset(); session=new LineSession(); session.Line.MissingAck=true; HttpClient.Responses.Enqueue(Reply("object")); HttpClient.Responses.Enqueue(Reply("object"));
        bool noAck=false; try { MessageMediaCache.SendImageAsync(session,"cgroup",photo,"photo.jpg").GetAwaiter().GetResult(); } catch(InvalidOperationException) { noAck=true; }
        Check(noAck && session.Store.Messages.Count==0,"An unacknowledged Talk send fabricated a local message.");
        Reset(); session=new LineSession(); session.Line.ResolveKey=Convert.ToBase64String(key); HttpClient.Responses.Enqueue(Reply(bytes:encrypted));
        var incoming=new LumINE.Models.Message { Id="message",ContentType=1,Chunks=new List<byte[]>{new byte[1]},ContentMetadata=new Dictionary<string,string>{{"SID","emi"},{"OID","object"}} };
        Check(MessageMediaCache.Download(session,incoming,0).GetAwaiter().GetResult().SequenceEqual(photo) && session.Line.Decrypts==1,"Cached encrypted image was downloaded before recovering its keyMaterial.");
        Check(HttpClient.Requests[0].Uri.AbsolutePath.EndsWith("/emi/object__ud-preview") && HttpClient.Requests[0].Headers.ContainsKey("X-Talk-Meta"),"Sealed image authorization or preview route was lost.");
        Reset(); HttpClient.Responses.Enqueue(Reply(bytes:encrypted));
        Check(MessageMediaCache.Download(session,incoming,4).GetAwaiter().GetResult().SequenceEqual(photo) && HttpClient.Requests[0].Uri.Host=="obs.line-apps.com" && HttpClient.Requests[0].Headers["X-Line-Access"]==session.AuthToken,"Direct OBS fallback used the wrong credentials or route.");
        Reset(); session=new LineSession(); incoming.MediaKeyMaterial=null;
        bool missingKey=false; try { MessageMediaCache.Download(session,incoming,0).GetAwaiter().GetResult(); } catch(InvalidOperationException) { missingKey=true; }
        Check(missingKey && HttpClient.Requests.Count==0,"Missing E2EE keys were treated as decodable photo bytes.");
        Reset(); session=new LineSession(); HttpClient.Responses.Enqueue(Reply(bytes:photo));
        var official=new LumINE.Models.Message { Id="official",ContentMetadata=new Dictionary<string,string>{{"PREVIEW_URL","https://example.com/photo.jpg"}} };
        Check(MessageMediaCache.Download(session,official,0).GetAwaiter().GetResult().SequenceEqual(photo) && !HttpClient.Requests[0].Headers.ContainsKey("X-Line-Access"),"Account credentials leaked to an external image CDN.");
        Reset(); session=new LineSession(); session.Talk.Plain=true; HttpClient.Responses.Enqueue(Reply("plain-message"));
        Check(MessageMediaCache.SendImageAsync(session,"uofficial",photo,"photo.jpg").GetAwaiter().GetResult().Id=="plain-message" && session.Line.Sent==0,"Non-E2EE official-account upload stopped working.");
        Console.WriteLine("PASS: "+Count+" encrypted-media, receive recovery, upload acknowledgement, cache and credential-routing checks.");
    }
}
'@
$fixture=$fixture.Replace('__TRANSFER__',($transfer -join "`n")).Replace('__CRYPTO__',($crypto -join "`n"))
Add-Type -TypeDefinition ('using System; using System.IO; using System.Text; using System.Linq; using System.Threading; using System.Threading.Tasks; using System.Collections.Generic; using System.Security.Cryptography; using LumINE.Utils; using LumINE.Services.Vyline;'+$fixture)
[MediaChecks]::Run()
