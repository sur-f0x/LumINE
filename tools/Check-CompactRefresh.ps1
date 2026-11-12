$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
function Read-Source([string] $path) { [regex]::Replace((Get-Content -LiteralPath (Join-Path $repoRoot $path) -Raw), '(?m)^using [^\r\n]+;\r?\n', '') }
$fixtures = @'
namespace LumINE.Utils {
    public class LineServiceError : Exception { public LineServiceError(int code,string message) : base(message) {} }
}
namespace LumINE.Services.Vyline {
    public class ThriftRpcException : Exception { public ThriftRpcException(int code,string message,Dictionary<int,object> fields) : base(message) {} }
    public class ThriftResult { public object Success; public Dictionary<int,object> Fields; }
    public static class ThriftStructs { public static List<ThriftField> RefreshArgs(string token) { return new List<ThriftField>(); } public static List<ThriftField> NoopArgs() { return new List<ThriftField>(); } }
    public class RequestClient {
        public byte[] Reply; public int Requests;
        public Task<ThriftResult> RequestCoreAsync(string path,List<ThriftField> args,string method,object headers,bool retry,int timeout,CancellationToken cancellation) {
            Requests++; var fields=ThriftCompact.UnpackCompactMessage(Reply);
            return Task.FromResult(new ThriftResult { Success=fields[0], Fields=fields });
        }
        public Task RequestAsync(string path,List<ThriftField> args,string method) { return Task.CompletedTask; }
    }
}
public static class CompactRefreshChecks {
    static int Count;
    static void Check(bool condition,string message) { if(!condition) throw new Exception(message); Count++; }
    static byte[] Hex(string value) { value=value.Replace(" ",""); var bytes=new byte[value.Length/2]; for(int i=0;i<bytes.Length;i++) bytes[i]=Convert.ToByte(value.Substring(i*2,2),16); return bytes; }
    static byte[] Reply(byte[] body) {
        // Independently encoded REPLY header: compact v1, seq 0, method refresh.
        return Hex("82 41 00 07 72 65 66 72 65 73 68").Concat(body).ToArray();
    }
    public static void Run() {
        // Apache compact protocol: field 1 DOUBLE, IEEE 754 1.5 LE;
        // field 2 I32 = 42. The latter detects incorrect byte order/length.
        var values=ThriftCompact.UnpackCompactStruct(Hex("17 000000000000F83F 15 54 00"));
        Check((double)values[1]==1.5 && (int)values[2]==42,"Double decoding misread its value or consumed the following field.");
        var packed=ThriftCompact.PackCompact("m",new List<ThriftField>{new ThriftField(ThriftTypes.Double,1,1.5),new ThriftField(ThriftTypes.I32,2,42)});
        Check(packed.SequenceEqual(Hex("82 21 00 01 6D 17 000000000000F83F 15 54 00")),"Double encoding differs from the compact wire format.");
        var containers=ThriftCompact.UnpackCompactStruct(Hex("19 27 000000000000E03F 00000000000002C0 1B 01 87 01 78 000000000000F83F 1A 17 000000000000E03F 00"));
        var list=(List<object>)containers[1]; var map=(Dictionary<object,object>)containers[2]; var set=(List<object>)containers[3];
        Check((double)list[0]==0.5 && (double)list[1]==-2.25,"Doubles in lists did not decode.");
        Check((double)map["x"]==1.5 && (double)set[0]==0.5,"Doubles in maps/sets did not decode.");
        foreach(double number in new[]{0.0,BitConverter.Int64BitsToDouble(long.MinValue),double.PositiveInfinity,double.NegativeInfinity,double.Epsilon,double.MaxValue,double.NaN}) {
            var fields=ThriftCompact.UnpackCompactMessage(ThriftCompact.PackCompact("m",new List<ThriftField>{new ThriftField(ThriftTypes.Double,1,number)}));
            Check(BitConverter.DoubleToInt64Bits((double)fields[1])==BitConverter.DoubleToInt64Bits(number),"IEEE 754 bits were changed.");
        }
        bool truncated=false;
        try { ThriftCompact.UnpackCompactStruct(Hex("17 00000000")); } catch(EndOfStreamException) { truncated=true; }
        Check(truncated,"A truncated double was accepted.");
        // Synthetic refresh response with access/refresh token strings and
        // issue-time/duration DOUBLE fields. No real credentials are used.
        var reply=Reply(Hex("0C 00 18 06 61 63 63 65 73 73 18 07 72 65 66 72 65 73 68 17 000000000000F83F 17 0000000000000240 00 15 02 00"));
        var result=ThriftCompact.UnpackCompactMessage(reply); var tokenInfo=(Dictionary<int,object>)result[0];
        Check((string)tokenInfo[1]=="access" && (string)tokenInfo[2]=="refresh" && (double)tokenInfo[3]==1.5 && (double)tokenInfo[4]==2.25 && (int)result[1]==1,"A refresh-shaped reply with doubles did not fully decode.");
        // Exercise the real AuthService against a response with integral double
        // timestamps, as accepted by its existing expiry parser.
        var client=new RequestClient { Reply=Reply(Hex("0C 00 18 06 61 63 63 65 73 73 18 07 72 65 66 72 65 73 68 17 0000000000408F40 17 000000000020AC40 00 00")) };
        string access=null,refresh=null; long expiry=0; int updated=0;
        var auth=new AuthService(client,()=>"old-refresh",v=>refresh=v,v=>access=v,v=>expiry=v);
        auth.AccessTokenUpdated+=v=>updated++;
        auth.TryRefreshTokenAsync().GetAwaiter().GetResult();
        Check(access=="access" && refresh=="refresh" && updated==1,"AuthService failed to apply tokens from a decoded refresh reply.");
        Check(expiry==4600 && client.Requests==1,"Refresh expiry fields were lost or refresh was retried unnecessarily.");
        Console.WriteLine("PASS: "+Count+" compact DOUBLE, wire vectors, container, truncation and token-refresh checks.");
    }
}
'@
$source = 'using System; using System.IO; using System.Linq; using System.Text; using System.Threading; using System.Threading.Tasks; using System.Collections.Generic; using LumINE.Utils; using LumINE.Services.Vyline;' + (Read-Source 'LumINE/Utils/ThriftTypes.cs') + (Read-Source 'LumINE/Utils/ThriftCompact.cs') + (Read-Source 'LumINE/Services/Vyline/AuthService.cs') + $fixtures
Add-Type -TypeDefinition $source
[CompactRefreshChecks]::Run()
