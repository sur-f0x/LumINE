param([switch]$LiveRsa)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
function Read-Source($path) {
    [regex]::Replace([IO.File]::ReadAllText((Join-Path $root $path)), '(?m)^using [^\r\n]+;\r?\n', '')
}
$legy = Read-Source 'LumINE/Services/LegyClient.cs'
function Read-Method($name) {
    $start = [regex]::Match($legy, '(?m)^        (?:public|private).*\b' + $name + '\(').Index
    $brace = $legy.IndexOf('{', $start); $depth = 1; $end = $brace + 1
    while ($depth -gt 0) { if ($legy[$end] -eq '{') { $depth++ }; if ($legy[$end] -eq '}') { $depth-- }; $end++ }
    $legy.Substring($start, $end - $start)
}
# Compile production HTTP methods and codecs. The WinRT adapter below captures
# requests for offline tests and can optionally forward the public RSA request.
$methods = @('PostLegyAsync', 'PostLegyRawAsync', 'ServiceHeaders') | ForEach-Object { Read-Method $_ }
$fixture = @'
namespace System.Runtime.InteropServices.WindowsRuntime {
 public static class Buffers { public static byte[] AsBuffer(this byte[] value){return value;} }
}
namespace Windows.Storage.Streams {
 public class DataReader {
  byte[] value; public static DataReader FromBuffer(byte[] data){return new DataReader{value=data};}
  public void ReadBytes(byte[] target){Array.Copy(value,target,value.Length);}
 }
}
namespace Windows.Web.Http.Headers {
 public class HttpMediaTypeHeaderValue { public string Value; public HttpMediaTypeHeaderValue(string value){Value=value;} }
}
namespace Windows.Web.Http {
 public class HeaderMap:Dictionary<string,string> {
  public HeaderMap():base(StringComparer.OrdinalIgnoreCase){}
  public bool TryAppendWithoutValidation(string name,string value){this[name]=value;return true;}
 }
 public class ContentHeaders {public Headers.HttpMediaTypeHeaderValue ContentType;}
 public class BufferOperation {
  public byte[] Value; public Task<byte[]> AsTask(CancellationToken token){token.ThrowIfCancellationRequested();return Task.FromResult(Value);}
 }
 public class HttpBufferContent {
  public byte[] Bytes; public ContentHeaders Headers=new ContentHeaders();
  public HttpBufferContent(byte[] bytes){Bytes=bytes;}
  public BufferOperation ReadAsBufferAsync(){return new BufferOperation{Value=Bytes};}
 }
 public class HttpMethod {public static HttpMethod Post=new HttpMethod();}
 public class HttpRequestMessage:IDisposable {
  public Uri RequestUri; public HeaderMap Headers=new HeaderMap(); public HttpBufferContent Content;
  public HttpRequestMessage(HttpMethod method,Uri uri){RequestUri=uri;}
  public void Dispose(){}
 }
 public class HttpResponseMessage:IDisposable {
  public int StatusCode=200;public HeaderMap Headers=new HeaderMap();public HttpBufferContent Content;
  public bool IsSuccessStatusCode {get{return StatusCode>=200&&StatusCode<300;}}
  public void Dispose(){}
 }
 public class HttpOperation {
  public HttpRequestMessage Request;
  public Task<HttpResponseMessage> AsTask(CancellationToken token){return HttpClient.Send(Request,token);}
 }
 public class HttpClient {
  public static Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> Send;
  public HttpOperation SendRequestAsync(HttpRequestMessage request){return new HttpOperation{Request=request};}
 }
}
namespace LumINE.Services {
 public class LegyClient {
  Windows.Web.Http.HttpClient _httpClient=new Windows.Web.Http.HttpClient();
  public class LegyRawResult {public byte[] Body;public string NextAccess;public int Status;}
__METHODS__
 }
}
public static class LoginTransportChecks {
 static int count;
 static void Check(bool value,string message){if(!value)throw new Exception(message);count++;}
 static Windows.Web.Http.HttpResponseMessage Response(int status,byte[] body){return new Windows.Web.Http.HttpResponseMessage{StatusCode=status,Content=new Windows.Web.Http.HttpBufferContent(body)};}
 public static async Task Run(bool live){
  var client=new LumINE.Services.LegyClient();
  var fields=new List<LumINE.Utils.ThriftField>{new LumINE.Utils.ThriftField(8,2,1)};
  var request=LumINE.Utils.ThriftBinary.PackBinary("getRSAKeyInfo",fields);
  var key=new List<LumINE.Utils.ThriftField>{new LumINE.Utils.ThriftField(11,1,"key"),new LumINE.Utils.ThriftField(11,2,"modulus"),new LumINE.Utils.ThriftField(11,3,"010001"),new LumINE.Utils.ThriftField(11,4,"test-session")};
  var reply=LumINE.Utils.ThriftBinary.PackBinary("getRSAKeyInfo",new List<LumINE.Utils.ThriftField>{new LumINE.Utils.ThriftField(12,0,key)});reply[3]=2;
  Windows.Web.Http.HttpRequestMessage captured=null;
  Windows.Web.Http.HttpClient.Send=(r,t)=>{captured=r;return Task.FromResult(Response(200,reply));};
  var rsa=await client.PostLegyAsync(LumINE.Services.LineApiConfig.EpLegyTalkDo,request);
  Check(captured.RequestUri.AbsoluteUri=="https://legy-jp.line-apps.com/api/v3/TalkService.do","RSA used the retired gateway.");
  Check(captured.Content.Bytes.SequenceEqual(request),"Binary request was wrapped or changed.");
  Check((string)rsa[4]=="test-session","RSA reply no longer unwraps the success struct.");
  Check(captured.Headers["x-line-application"]==LumINE.Services.Vyline.DesktopIdentity.ApplicationHeader,"Desktop TAB identity was dropped.");
  Check(!captured.Headers.ContainsKey("x-lcs")&&!captured.Headers.ContainsKey("x-le"),"Retired gateway headers were sent.");
  var extra=new Dictionary<string,string>{{"Host","obsolete.invalid"},{"Content-Type","wrong"},{"connection","close"},{"X-LCS","obsolete"},{"X-LE","18"},{"x-line-access","old-token"},{"x-test","preserved"}};
  var compact=new byte[]{0x82,0x21,0,0};
  Windows.Web.Http.HttpClient.Send=(r,t)=>{captured=r;var res=Response(200,compact);res.Headers["x-line-next-access"]="rotated-token";return Task.FromResult(res);};
  var raw=await client.PostLegyRawAsync("/S4",compact,"current-token",extra,15000,CancellationToken.None);
  Check(captured.RequestUri.AbsolutePath=="/S4"&&raw.Body.SequenceEqual(compact),"Talk request/reply must remain raw Thrift.");
  Check(captured.Headers["x-line-access"]=="current-token","Access token was lost or overwritten.");
  Check(raw.NextAccess=="rotated-token","Token rotation was lost.");
  Check(!captured.Headers.ContainsKey("Host")&&!captured.Headers.ContainsKey("connection")&&!captured.Headers.ContainsKey("Content-Type")&&!captured.Headers.ContainsKey("X-LCS")&&!captured.Headers.ContainsKey("X-LE"),"Reserved headers leaked into request headers.");
  Check(captured.Headers["x-test"]=="preserved"&&captured.Content.Headers.ContentType.Value=="application/x-thrift","Extra or content headers were lost.");
  var denied=LumINE.Utils.ThriftBinary.PackBinary("loginV2",new List<LumINE.Utils.ThriftField>{new LumINE.Utils.ThriftField(12,1,new List<LumINE.Utils.ThriftField>{new LumINE.Utils.ThriftField(8,1,1),new LumINE.Utils.ThriftField(11,2,"authentication denied")})});denied[3]=2;
  Windows.Web.Http.HttpClient.Send=(r,t)=>Task.FromResult(Response(200,denied));
  try{await client.PostLegyAsync("/api/v3p/rs",request);throw new Exception("Service rejection was accepted.");}catch(LumINE.Utils.LineServiceError ex){Check(ex.Code==1&&ex.ErrorMessage=="authentication denied","Service authentication errors were replaced or swallowed.");}
  Windows.Web.Http.HttpClient.Send=(r,t)=>Task.FromResult(Response(404,new byte[0]));
  try{await client.PostLegyAsync("/api/v3p/rs",request);throw new Exception("HTTP error was accepted.");}catch(Exception ex){Check(ex.Message.Contains("HTTP 404")&&ex.Message.Contains("/api/v3p/rs")&&ex.Message.Contains("legy-jp.line-apps.com"),"HTTP diagnostic omitted the route.");}
  Windows.Web.Http.HttpClient.Send=(r,t)=>Task.FromResult(Response(200,new byte[0]));
  try{await client.PostLegyAsync("/api/v3p/rs",request);throw new Exception("Empty response was accepted.");}catch(InvalidDataException){count++;}
  Windows.Web.Http.HttpClient.Send=async(r,t)=>{await Task.Delay(Timeout.Infinite,t);return Response(200,reply);};
  try{await client.PostLegyRawAsync("/S4",request,null,null,10,CancellationToken.None);throw new Exception("Timeout was ignored.");}catch(OperationCanceledException){count++;}
  using(var cancelled=new CancellationTokenSource()){
   cancelled.Cancel();try{await client.PostLegyRawAsync("/S4",request,null,null,15000,cancelled.Token);throw new Exception("Cancellation was ignored.");}catch(OperationCanceledException){count++;}
  }
  Console.WriteLine("PASS: "+count+" desktop HTTPS, RSA decoding, headers, token rotation, errors and cancellation checks.");
  if(!live)return;
  using(var handler=new System.Net.Http.HttpClientHandler{AutomaticDecompression=System.Net.DecompressionMethods.GZip|System.Net.DecompressionMethods.Deflate})
  using(var network=new System.Net.Http.HttpClient(handler)){
   Windows.Web.Http.HttpClient.Send=async(r,t)=>{
    using(var req=new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Post,r.RequestUri)){
     foreach(var h in r.Headers)req.Headers.TryAddWithoutValidation(h.Key,h.Value);
     req.Content=new System.Net.Http.ByteArrayContent(r.Content.Bytes);req.Content.Headers.ContentType=new System.Net.Http.Headers.MediaTypeHeaderValue(r.Content.Headers.ContentType.Value);
     using(var res=await network.SendAsync(req,t)){return Response((int)res.StatusCode,await res.Content.ReadAsByteArrayAsync());}
    }
   };
   var liveRsa=await client.PostLegyAsync(LumINE.Services.LineApiConfig.EpLegyTalkDo,request);
   for(int i=1;i<=4;i++)Check(liveRsa.ContainsKey(i)&&!string.IsNullOrEmpty(liveRsa[i] as string),"Live RSA response field missing: "+i);
   Console.WriteLine("PASS: Live public RSA request returned key name, modulus, exponent and session. No credentials sent or response values logged.");
  }
 }
}
'@
$source = 'using System;using System.IO;using System.Linq;using System.Text;using System.Collections.Generic;using System.Threading;using System.Threading.Tasks;using Windows.Web.Http;using Windows.Web.Http.Headers;using Windows.Storage.Streams;using System.Runtime.InteropServices.WindowsRuntime;using LumINE.Services.Vyline;'
foreach ($path in @('LumINE/Utils/ThriftTypes.cs','LumINE/Utils/LineServiceError.cs','LumINE/Utils/ThriftBinary.cs','LumINE/Services/LineApiConfig.cs','LumINE/Services/Vyline/DesktopIdentity.cs')) { $source += Read-Source $path }
Add-Type -TypeDefinition ($source + $fixture.Replace('__METHODS__', ($methods -join "`n"))) -IgnoreWarnings -WarningAction SilentlyContinue
[LoginTransportChecks]::Run([bool]$LiveRsa).GetAwaiter().GetResult() | Out-Null
