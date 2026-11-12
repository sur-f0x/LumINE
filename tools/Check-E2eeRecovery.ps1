$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
function Read-Source([string] $path) { [regex]::Replace((Get-Content -LiteralPath (Join-Path $repoRoot $path) -Raw), '(?m)^using [^\r\n]+;\r?\n', '') }
function Method([string] $path,[string] $signature) {
    $text=Get-Content (Join-Path $repoRoot $path) -Raw
    $start=$text.IndexOf($signature); if($start -lt 0) { throw "Missing $signature" }
    $end=$text.IndexOf('{',$start)+1; $depth=1
    while($depth -gt 0) { if($text[$end] -eq '{'){$depth++}; if($text[$end] -eq '}'){$depth--}; $end++ }
    $text.Substring($start,$end-$start)
}
$crypto=Get-Content (Join-Path $repoRoot 'LumINE/Utils/CryptoHelper.cs') -Raw
$start=$crypto.IndexOf('private static readonly BigInteger X25519_P')
$end=$crypto.IndexOf('// ==================================================================', $crypto.IndexOf('public static byte[] X25519SharedSecret'))
$crypto=$crypto.Substring($start,$end-$start)
$keychain=@('public static void DecryptKeyChain(', 'public static List<E2eeKeyChainEntry> DecryptKeyChainEntries(', 'private static byte[] AsBytes(') | ForEach-Object { Method 'LumINE/Utils/E2ee.cs' $_ }
$service=@('public Task<Dictionary<int, object>> SendMessageAsync(', 'private async Task<Dictionary<int, object>> SendMessageInnerAsync(', 'public Task<Dictionary<int, object>> SendMediaMessageAsync(', 'private async Task<Dictionary<int, object>> SendMediaInnerAsync(', 'private async Task<bool> TryEncryptE2eeAsync(', 'private async Task<E2eeGroupKey> LoadGroupKeyAsync(', 'private async Task<E2eeGroupKey> RecreateGroupKeyAsync(', 'private async Task<E2eeGroupKey> UnwrapGroupKeyAsync(', 'private static bool SameBytes(', 'private static int GetFieldInt(', 'private static string GetFieldString(', 'private static byte[] GetFieldBytes(') | ForEach-Object { Method 'LumINE/Services/Vyline/LineService.cs' $_ }
$fixture=@'
namespace LumINE.Utils {
    public class LineServiceError : Exception { public LineServiceError(int code,string msg) : base(msg) {} }
    public class E2eeKeyChainEntry { public int KeyId; public byte[] PrivateKey,PublicKey; }
    public static class CryptoHelper {
__CRYPTO__
        public static byte[] RandomBytes(int n) { return RandomNumberGenerator.GetBytes(n); }
        public static byte[] Sha256(params byte[][] parts) { return SHA256.HashData(parts.SelectMany(p=>p).ToArray()); }
        public static byte[] XorHalf(byte[] bytes) { return bytes.Take(bytes.Length/2).Select((b,i)=>(byte)(b^bytes[i+bytes.Length/2])).ToArray(); }
        public static byte[] AesCbcEncrypt(byte[] key,byte[] iv,byte[] bytes) { using(var aes=Aes.Create()) { aes.Key=key; return aes.EncryptCbc(bytes,iv,PaddingMode.PKCS7); } }
        public static byte[] AesCbcDecryptNoPadding(byte[] key,byte[] iv,byte[] bytes) { using(var aes=Aes.Create()) { aes.Key=key; return aes.DecryptCbc(bytes,iv,PaddingMode.None); } }
    }
    public static class E2ee {
__KEYCHAIN__
        public static int LastType; public static bool LastJson; public static string LastPayload;
        public static void EncryptTextMessage(string to,string from,string text,int type,byte[] priv,byte[] pub,int sender,int receiver,out List<byte[]> chunks,out int spec,int version,bool json=false) {
            LastType=type; LastJson=json; LastPayload=text;
            chunks=new List<byte[]> { BitConverter.GetBytes(sender),BitConverter.GetBytes(receiver) }; spec=2;
        }
    }
}
namespace LumINE.Services.Vyline {
    public class CancellationTokenEx {}
    public class ThriftRpcException : Exception { public int Code; public ThriftRpcException(int code) { Code=code; } }
    public class E2eeSelfKey { public int KeyId; public byte[] PrivateKey,PublicKey; }
    public class E2eePeerKey { public int KeyId,Version; public long CreatedTime; public byte[] PublicKey; }
    public class E2eeGroupKey { public string Mid; public int KeyId; public byte[] PrivateKey; }
    public static class E2eeKeyStore {
        public static List<E2eeSelfKey> Self;
        public static Dictionary<int,E2eeGroupKey> Groups=new Dictionary<int,E2eeGroupKey>();
        public static Task<List<E2eeSelfKey>> LoadSelfKeysAsync(string account) { return Task.FromResult(Self); }
        public static Task<E2eeGroupKey> LoadGroupKeyAsync(string account,string mid,int id) { E2eeGroupKey key; Groups.TryGetValue(id,out key); return Task.FromResult(key); }
        public static Task SaveGroupKeyAsync(string account,E2eeGroupKey key) { Groups[key.KeyId]=key; return Task.CompletedTask; }
        public static Task<E2eePeerKey> LoadPeerKeyAsync(string account,string mid) { return Task.FromResult<E2eePeerKey>(null); }
        public static Task SavePeerKeyAsync(string account,string mid,E2eePeerKey key) { return Task.CompletedTask; }
__EXTRACT__
    }
    public class LineSession { public string Mid="umyself", AccountId="test"; public TalkService Talk=new TalkService(); }
    public class Sent { public Dictionary<string,string> Metadata; public string Reply; public List<byte[]> Chunks; }
    public class TalkService {
        public Queue<int> Errors=new Queue<int>(); public List<Sent> Sent=new List<Sent>();
        public Dictionary<object,object> Recipients; public Dictionary<int,object> Last;
        public int LastError,Registrations,Keyed,LastReads; public List<string> Members; public List<int> Ids; public List<byte[]> Wrapped;
        public Task<Dictionary<int,object>> SendMessageAsync(string chat,string text,int contentType,Dictionary<string,string> metadata,string reply,int relation,List<byte[]> chunks,CancellationToken token) {
            Sent.Add(new Sent { Metadata=new Dictionary<string,string>(metadata),Reply=reply,Chunks=chunks });
            int code=Errors.Count>0?Errors.Dequeue():0; if(code!=0) throw new ThriftRpcException(code);
            return Task.FromResult(new Dictionary<int,object>{{4,"sent"}});
        }
        public Task<Dictionary<int,object>> NegotiateE2eePublicKeyAsync(string mid) { throw new NotSupportedException(); }
        public Task<Dictionary<int,object>> GetLastE2eeGroupSharedKeyAsync(int version,string mid) { LastReads++; if(LastError!=0) throw new ThriftRpcException(LastError); return Task.FromResult(Last); }
        public Task<Dictionary<int,object>> GetE2eeGroupSharedKeyAsync(int version,string mid,int id) { Keyed++; if(LastError!=0) throw new ThriftRpcException(LastError); return Task.FromResult(Last); }
        public Task<Dictionary<object,object>> GetLastE2eePublicKeysAsync(string mid) { return Task.FromResult(Recipients); }
        public Task<Dictionary<int,object>> RegisterE2eeGroupKeyAsync(int version,string mid,List<string> members,List<int> ids,List<byte[]> wrapped) {
            if(version!=1) throw new Exception("Wrong registration key version");
            Registrations++; Members=members; Ids=ids; Wrapped=wrapped;
            int own=members.IndexOf("umyself");
            Last=new Dictionary<int,object>{{2,100+Registrations},{3,"umyself"},{4,ids[own]},{6,ids[own]},{7,wrapped[own]}};
            return Task.FromResult(Last);
        }
        public Task<Dictionary<int,object>> GetE2eePublicKeyAsync(string mid,int version,int id) { return Task.FromResult((Dictionary<int,object>)Recipients[mid]); }
    }
    public class LineService {
        readonly LineSession _session; readonly object _sendLock=new object(); Task _sendChain=Task.CompletedTask; List<byte[]> _pendingChunks;
        public const int SendTimeoutMs=15000;
        public LineService(LineSession session) { _session=session; }
        static bool IsGroupMid(string mid) { return mid.StartsWith("c")||mid.StartsWith("r"); }
        public Task<E2eeGroupKey> History(string mid,int id) { return LoadGroupKeyAsync(mid,id); }
__SERVICE__
    }
}
public static class RecoveryChecks {
    static int Count; static byte[] PeerPriv;
    static void Check(bool okay,string message) { if(!okay) throw new Exception(message); Count++; }
    static Dictionary<int,object> Public(int id,byte[] pub) { return new Dictionary<int,object>{{1,1},{2,id},{4,pub}}; }
    static LineSession Setup() {
        byte[] priv,pub,peer; CryptoHelper.X25519GenerateKeyPair(out priv,out pub); CryptoHelper.X25519GenerateKeyPair(out PeerPriv,out peer);
        E2eeKeyStore.Self=new List<E2eeSelfKey>{new E2eeSelfKey { KeyId=5,PrivateKey=priv,PublicKey=pub }};
        E2eeKeyStore.Groups.Clear();
        var session=new LineSession(); session.Talk.Recipients=new Dictionary<object,object>{{"umyself",Public(5,pub)},{"upeer",Public(7,peer)}};
        return session;
    }
    static byte[] Open(byte[] priv,byte[] pub,byte[] encrypted) {
        var shared=CryptoHelper.X25519SharedSecret(priv,pub);
        var key=CryptoHelper.Sha256(shared,Encoding.ASCII.GetBytes("Key")); var iv=CryptoHelper.XorHalf(CryptoHelper.Sha256(shared,Encoding.ASCII.GetBytes("IV")));
        using(var aes=Aes.Create()) { aes.Key=key; return aes.DecryptCbc(encrypted,iv,PaddingMode.PKCS7); }
    }
    public static void Run() {
        var session=Setup(); session.Talk.Errors.Enqueue(99);
        new LineService(session).SendMessageAsync("cgroup","hello","replyId",new Dictionary<string,string>{{"MENTION","kept"}}).GetAwaiter().GetResult();
        Check(session.Talk.Registrations==1 && session.Talk.Sent.Count==2,"Plain code 99 did not recreate and retry exactly once.");
        var sent=session.Talk.Sent[1]; Check(sent.Chunks!=null && sent.Reply=="replyId" && sent.Metadata["MENTION"]=="kept" && sent.Metadata["e2eeVersion"]=="2","Sealed retry lost reply or metadata.");
        Check(session.Talk.Ids.SequenceEqual(new[]{5,7}) && session.Talk.Members.SequenceEqual(new[]{"umyself","upeer"}),"Recipients and key IDs became misaligned.");
        var own=E2eeKeyStore.Self[0]; var secret=E2eeKeyStore.Groups[101].PrivateKey;
        Check(secret.Length==32 && session.Talk.Wrapped.All(b=>b.Length==48),"Group key generation/padding is invalid.");
        Check(Open(PeerPriv,own.PublicKey,session.Talk.Wrapped[1]).SequenceEqual(secret),"The other member cannot unwrap the registered group key.");

        session=Setup(); session.Talk.LastError=5; session.Talk.Errors.Enqueue(82);
        new LineService(session).SendMessageAsync("cgroup","hello").GetAwaiter().GetResult();
        Check(session.Talk.Registrations==1 && session.Talk.Sent.Count==2,"Missing current group key was not recreated on send.");
        session=Setup(); own=E2eeKeyStore.Self[0];
        var initial=CryptoHelper.RandomBytes(32); var initialShared=CryptoHelper.X25519SharedSecret(own.PrivateKey,own.PublicKey);
        var initialWrapped=CryptoHelper.AesCbcEncrypt(CryptoHelper.Sha256(initialShared,Encoding.ASCII.GetBytes("Key")),CryptoHelper.XorHalf(CryptoHelper.Sha256(initialShared,Encoding.ASCII.GetBytes("IV"))),initial);
        session.Talk.Last=new Dictionary<int,object>{{2,50},{3,"umyself"},{4,5},{6,5},{7,initialWrapped}};
        session.Talk.Errors.Enqueue(82); session.Talk.Errors.Enqueue(99);
        var service=new LineService(session);
        service.SendMessageAsync("cgroup","hello","reply",new Dictionary<string,string>{{"MENTION","preserved"}}).GetAwaiter().GetResult();
        Check(session.Talk.Registrations==1 && session.Talk.Sent.Count==3 && session.Talk.Sent[2].Metadata["MENTION"]=="preserved" && session.Talk.Sent[2].Reply=="reply","Code 99 on the encrypted send did not recreate or lost reply/mention metadata.");
        int keyed=session.Talk.Keyed;
        Check(service.History("cgroup",50).GetAwaiter().GetResult().PrivateKey.SequenceEqual(initial) && session.Talk.Keyed==keyed,"Recreation discarded the cached historical group key.");
        session=Setup(); session.Talk.LastError=5;
        Check(new LineService(session).History("cgroup",77).GetAwaiter().GetResult()==null && session.Talk.Registrations==0 && session.Talk.Keyed==1 && session.Talk.LastReads==0,"Reading old history rotated today's key or fetched the wrong generation.");
        session=Setup(); session.Talk.Errors.Enqueue(99); session.Talk.Errors.Enqueue(99); session.Talk.Errors.Enqueue(99);
        bool bounded=false; try { new LineService(session).SendMessageAsync("cgroup","hello").GetAwaiter().GetResult(); } catch(ThriftRpcException ex) { bounded=ex.Code==99; }
        Check(bounded && session.Talk.Sent.Count==3 && session.Talk.Registrations==2,"Code 99 retry is unbounded or error is swallowed.");
        session=Setup(); session.Talk.LastError=403; session.Talk.Errors.Enqueue(82);
        bool denied=false; try { new LineService(session).SendMessageAsync("cgroup","hello").GetAwaiter().GetResult(); } catch(ThriftRpcException ex) { denied=ex.Code==403; }
        Check(denied && session.Talk.Registrations==0,"Permission failure triggered key registration.");
        session=Setup(); session.Talk.Errors.Enqueue(99); E2eeKeyStore.Self[0].PublicKey=new byte[32];
        bool mismatch=false; try { new LineService(session).SendMessageAsync("cgroup","hello").GetAwaiter().GetResult(); } catch(InvalidOperationException) { mismatch=true; }
        Check(mismatch && session.Talk.Registrations==0,"A mismatched self identity was used to distribute a group key.");
        session=Setup(); session.Talk.LastError=5; session.Talk.Errors.Enqueue(99);
        var media=new Dictionary<string,string>{{"SID","emi"},{"OID","uploaded-object"}};
        new LineService(session).SendMediaMessageAsync("cgroup","{\"keyMaterial\":\"secret\"}",media).GetAwaiter().GetResult();
        Check(E2ee.LastType==1 && E2ee.LastJson && E2ee.LastPayload.Contains("keyMaterial") && session.Talk.Sent.All(s=>s.Chunks!=null),"Media envelope was sent as plain text or wrapped as a text payload.");
        Check(session.Talk.Sent.Count==2 && session.Talk.Sent[1].Metadata["OID"]=="uploaded-object" && session.Talk.Sent[1].Metadata["contentType"]=="1","Media recovery lost its uploaded object or content type.");

        // Exercise the actual login keychain parser using encrypted multi-key
        // fixtures and real production X25519, plus portable AES/SHA adapters.
        session=Setup(); var old=E2eeKeyStore.Self[0]; byte[] newerPriv,newerPub; CryptoHelper.X25519GenerateKeyPair(out newerPriv,out newerPub);
        var entries=new List<object>{new List<ThriftField>{new ThriftField(8,2,5),new ThriftField(11,4,old.PublicKey),new ThriftField(11,5,old.PrivateKey)},new List<ThriftField>{new ThriftField(8,2,9),new ThriftField(11,4,newerPub),new ThriftField(11,5,newerPriv)}};
        var packed=ThriftCompact.PackCompact("k",new List<ThriftField>{new ThriftField(15,1,new ThriftListValue(12,entries))});
        var plain=packed.Skip(5).ToArray(); // 82 21 seq nameLength 'k'
        var shared=CryptoHelper.X25519SharedSecret(PeerPriv,old.PublicKey);
        var aesKey=CryptoHelper.Sha256(shared,Encoding.ASCII.GetBytes("Key")); var ivKey=CryptoHelper.XorHalf(CryptoHelper.Sha256(shared,Encoding.ASCII.GetBytes("IV")));
        var encrypted=CryptoHelper.AesCbcEncrypt(aesKey,ivKey,plain);
        var chain=E2ee.DecryptKeyChainEntries(old.PublicKey,PeerPriv,encrypted);
        Check(chain.Count==2 && chain[0].KeyId==5 && chain[1].KeyId==9,"Historical key IDs were relabelled or dropped during login.");
        byte[] selectedPriv,selectedPub;
        E2ee.DecryptKeyChain(old.PublicKey,PeerPriv,encrypted,out selectedPriv,out selectedPub,9);
        Check(selectedPriv.SequenceEqual(newerPriv) && selectedPub.SequenceEqual(newerPub),"Login selected the first key instead of the primary device's key ID.");
        bool ambiguous=false; try { E2ee.DecryptKeyChain(old.PublicKey,PeerPriv,encrypted,out selectedPriv,out selectedPub); } catch(InvalidOperationException) { ambiguous=true; }
        Check(ambiguous,"An ambiguous multi-key chain silently selected its first entry.");
        bool absent=false; try { E2ee.DecryptKeyChain(old.PublicKey,PeerPriv,encrypted,out selectedPriv,out selectedPub,100); } catch(InvalidOperationException) { absent=true; }
        Check(absent,"A missing login key ID silently selected another generation.");
        var malformed=(List<ThriftField>)entries[1]; malformed[0].Value=5;
        packed=ThriftCompact.PackCompact("k",new List<ThriftField>{new ThriftField(15,1,new ThriftListValue(12,entries))});
        encrypted=CryptoHelper.AesCbcEncrypt(aesKey,ivKey,packed.Skip(5).ToArray());
        bool duplicate=false; try { E2ee.DecryptKeyChainEntries(old.PublicKey,PeerPriv,encrypted); } catch(InvalidOperationException) { duplicate=true; }
        Check(duplicate,"Duplicate key-chain IDs were accepted.");
        malformed[0].Value=9; malformed[1].Value=old.PublicKey;
        packed=ThriftCompact.PackCompact("k",new List<ThriftField>{new ThriftField(15,1,new ThriftListValue(12,entries))});
        encrypted=CryptoHelper.AesCbcEncrypt(aesKey,ivKey,packed.Skip(5).ToArray());
        bool invalidPair=false; try { E2ee.DecryptKeyChainEntries(old.PublicKey,PeerPriv,encrypted); } catch(InvalidOperationException) { invalidPair=true; }
        Check(invalidPair,"Mismatched login public/private keys were accepted.");
        Console.WriteLine("PASS: "+Count+" E2EE device-change, group recreation, recipient wrapping, bounded retry and login keychain checks.");
    }
}
'@
$fixture=$fixture.Replace('__CRYPTO__',$crypto).Replace('__KEYCHAIN__',($keychain -join "`n")).Replace('__SERVICE__',($service -join "`n")).Replace('__EXTRACT__',(Method 'LumINE/Services/Vyline/E2eeKeyStore.cs' 'public static E2eePeerKey ExtractPeerKey('))
$source='using System; using System.IO; using System.Text; using System.Linq; using System.Numerics; using System.Threading; using System.Threading.Tasks; using System.Security.Cryptography; using System.Collections.Generic; using LumINE.Utils; using LumINE.Services.Vyline;' + (Read-Source 'LumINE/Utils/ThriftTypes.cs') + (Read-Source 'LumINE/Utils/ThriftCompact.cs') + $fixture
Add-Type -TypeDefinition $source
[RecoveryChecks]::Run()
