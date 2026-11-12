$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
function Method([string] $signature) {
    $text=Get-Content (Join-Path $repoRoot 'LumINE/Services/Vyline/E2eeKeyStore.cs') -Raw
    $start=$text.IndexOf($signature); if($start -lt 0) { throw "Missing $signature" }
    $end=$text.IndexOf('{',$start)+1; $depth=1
    while($depth -gt 0) { if($text[$end] -eq '{'){$depth++}; if($text[$end] -eq '}'){$depth--}; $end++ }
    $text.Substring($start,$end-$start)
}
$methods=@('public static async Task<List<E2eeSelfKey>> LoadSelfKeysAsync(', 'public static async Task SaveSelfKeysAsync(', 'public static async Task<E2eeGroupKey> LoadGroupKeyAsync(', 'public static async Task SaveGroupKeyAsync(', 'private static double GetNum(', 'private static byte[] GetB64(') | ForEach-Object { Method $_ }
$fixture=@'
public enum CreationCollisionOption { ReplaceExisting }
public class StorageFile { public string Text; }
public class StorageFolder {
    public Dictionary<string,StorageFile> Files=new Dictionary<string,StorageFile>();
    public Task<StorageFile> GetFileAsync(string name) { StorageFile f; if(!Files.TryGetValue(name,out f)) throw new FileNotFoundException(); return Task.FromResult(f); }
    public Task<StorageFile> CreateFileAsync(string name,CreationCollisionOption option) { var f=new StorageFile(); Files[name]=f; return Task.FromResult(f); }
}
public static class FileIO { public static Task<string> ReadTextAsync(StorageFile f) { return Task.FromResult(f.Text); } public static Task WriteTextAsync(StorageFile f,string text) { f.Text=text; return Task.CompletedTask; } }
public enum JsonValueType { String,Number,Boolean }
public class JsonValue {
    public object Value; public JsonValueType ValueType;
    public static JsonValue CreateStringValue(string v) { return new JsonValue { Value=v,ValueType=JsonValueType.String }; }
    public static JsonValue CreateNumberValue(double v) { return new JsonValue { Value=v,ValueType=JsonValueType.Number }; }
    public static JsonValue CreateBooleanValue(bool v) { return new JsonValue { Value=v,ValueType=JsonValueType.Boolean }; }
    public string GetString() { return (string)Value; } public double GetNumber() { return Convert.ToDouble(Value); }
}
public class JsonObject : Dictionary<string,JsonValue> {
    public JsonObject GetObject() { return this; }
    public JsonValue GetNamedValue(string k) { return this[k]; }
    public string GetNamedString(string k) { return this[k].GetString(); }
    public bool GetNamedBoolean(string k) { return (bool)this[k].Value; }
    public string Stringify() { return System.Text.Json.JsonSerializer.Serialize(this.ToDictionary(p=>p.Key,p=>p.Value.Value)); }
    public static JsonObject From(System.Text.Json.JsonElement element) {
        var obj=new JsonObject(); foreach(var p in element.EnumerateObject()) {
            obj[p.Name]=p.Value.ValueKind==System.Text.Json.JsonValueKind.String ? JsonValue.CreateStringValue(p.Value.GetString()) : p.Value.ValueKind==System.Text.Json.JsonValueKind.Number ? JsonValue.CreateNumberValue(p.Value.GetDouble()) : JsonValue.CreateBooleanValue(p.Value.GetBoolean());
        } return obj;
    }
    public static JsonObject Parse(string text) { using(var doc=System.Text.Json.JsonDocument.Parse(text)) { return From(doc.RootElement); } }
}
public class JsonArray : List<JsonObject> {
    public string Stringify() { return "["+string.Join(",",this.Select(o=>o.Stringify()))+"]"; }
    public static JsonArray Parse(string text) { using(var doc=System.Text.Json.JsonDocument.Parse(text)) { var array=new JsonArray(); foreach(var value in doc.RootElement.EnumerateArray()) array.Add(JsonObject.From(value)); return array; } }
}
__SELF__
__GROUP__
public static class Store {
    const string ProtectedSelfFilePrefix="LUMINE-E2EE-DPAPI-V1\n";
    public static StorageFolder Folder=new StorageFolder();
    static Task<StorageFolder> AccountFolderAsync(string account) { return Task.FromResult(Folder); }
    static Task<string> ProtectAsync(string text) { return Task.FromResult(Convert.ToBase64String(Encoding.UTF8.GetBytes(text))); }
    static Task<string> UnprotectAsync(string text) { return Task.FromResult(Encoding.UTF8.GetString(Convert.FromBase64String(text))); }
    static string Sanitize(string text) { return text; }
__METHODS__
}
public static class StorageChecks {
    static int Count;
    static void Check(bool okay,string message) { if(!okay) throw new Exception(message); Count++; }
    public static void Run() {
        var keys=new List<E2eeSelfKey> {
            new E2eeSelfKey { KeyId=9,PrivateKey=new byte[]{9},PublicKey=new byte[]{19} },
            new E2eeSelfKey { KeyId=2,PrivateKey=new byte[]{2},PublicKey=new byte[]{12},IsActive=true }
        };
        Store.SaveSelfKeysAsync("test",keys).GetAwaiter().GetResult();
        var restored=Store.LoadSelfKeysAsync("test").GetAwaiter().GetResult();
        Check(restored.Count==2 && restored[1].KeyId==2 && restored[1].IsActive,"Login-selected sender generation was replaced by the largest historical key ID.");
        Check(restored[0].KeyId==9 && restored[0].PrivateKey[0]==9 && restored[1].PrivateKey[0]==2,"Self key IDs or private keys were relabelled.");
        Check(Store.Folder.Files["e2ee-self.json"].Text.StartsWith("LUMINE-E2EE-DPAPI-V1\n"),"Self keys bypassed the existing protected storage envelope.");
        Store.Folder.Files["e2ee-self.json"].Text="[{\"keyId\":9,\"createdTime\":0,\"priv\":\"CQ==\",\"pub\":\"Ew==\"},{\"keyId\":2,\"createdTime\":0,\"priv\":\"Ag==\",\"pub\":\"DA==\"}]";
        restored=Store.LoadSelfKeysAsync("test").GetAwaiter().GetResult();
        Check(restored.Count==2 && restored[1].KeyId==9,"Legacy self key ordering/migration changed unexpectedly.");
        var a=new E2eeGroupKey { Mid="cgroup",KeyId=10,PrivateKey=Enumerable.Repeat((byte)10,32).ToArray() };
        var b=new E2eeGroupKey { Mid="cgroup",KeyId=20,PrivateKey=Enumerable.Repeat((byte)20,32).ToArray() };
        Store.SaveGroupKeyAsync("test",a).GetAwaiter().GetResult(); Store.SaveGroupKeyAsync("test",b).GetAwaiter().GetResult();
        Check(Store.LoadGroupKeyAsync("test","cgroup",10).GetAwaiter().GetResult().PrivateKey.SequenceEqual(a.PrivateKey),"Saving a new group generation evicted the old one.");
        Check(Store.LoadGroupKeyAsync("test","cgroup",20).GetAwaiter().GetResult().PrivateKey.SequenceEqual(b.PrivateKey),"The current group generation was not restored.");
        Check(Store.LoadGroupKeyAsync("test","cgroup").GetAwaiter().GetResult().KeyId==20,"Legacy current-key lookup stopped working.");
        Check(Store.LoadGroupKeyAsync("test","cgroup",30).GetAwaiter().GetResult()==null,"A different generation was substituted for missing historical keys.");
        Store.Folder.Files.Remove("e2ee-group-cgroup-20.json");
        Check(Store.LoadGroupKeyAsync("test","cgroup",20).GetAwaiter().GetResult().KeyId==20,"Old unsuffixed group caches did not migrate on lookup.");
        Console.WriteLine("PASS: "+Count+" active identity, protected storage, historical generations and legacy-cache checks.");
    }
}
'@
$fixture=$fixture.Replace('__SELF__',(Method 'public class E2eeSelfKey')).Replace('__GROUP__',(Method 'public class E2eeGroupKey')).Replace('__METHODS__',($methods -join "`n"))
Add-Type -TypeDefinition ('using System; using System.IO; using System.Text; using System.Linq; using System.Threading.Tasks; using System.Collections.Generic;'+$fixture)
[StorageChecks]::Run()
