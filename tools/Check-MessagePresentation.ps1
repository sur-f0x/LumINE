$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
function Read-Source([string] $path) {
    [regex]::Replace((Get-Content -LiteralPath (Join-Path $repoRoot $path) -Raw), '(?m)^using [^\r\n]+;\r?\n', '')
}
$fixtures = @'
public static class PresentationChecks {
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    static void Be(Stream output, uint value) { output.WriteByte((byte)(value>>24)); output.WriteByte((byte)(value>>16)); output.WriteByte((byte)(value>>8)); output.WriteByte((byte)value); }
    static void Chunk(Stream output, string name, byte[] data) {
        Be(output,(uint)data.Length);
        var body = Encoding.ASCII.GetBytes(name).Concat(data).ToArray();
        output.Write(body,0,body.Length);
        uint crc=0xffffffff;
        foreach(byte b in body) { crc ^= b; for(int i=0;i<8;i++) crc=(crc>>1)^((crc&1)!=0?0xedb88320u:0); }
        Be(output,crc^0xffffffff);
    }
    static byte[] Numbers(params uint[] values) { using(var s=new MemoryStream()) { foreach(uint n in values) Be(s,n); return s.ToArray(); } }
    static byte[] Compressed(byte[] raw) { using(var output=new MemoryStream()) { using(var zip=new System.IO.Compression.ZLibStream(output,System.IO.Compression.CompressionLevel.Optimal,true)) zip.Write(raw,0,raw.Length); return output.ToArray(); } }
    static byte[] Fixture(bool animated=true) {
        using(var output=new MemoryStream()) {
            output.Write(new byte[]{137,80,78,71,13,10,26,10});
            Chunk(output,"IHDR",Numbers(2,1).Concat(new byte[]{8,6,0,0,0}).ToArray());
            if(animated) {
                Chunk(output,"acTL",Numbers(2,3));
                Chunk(output,"fcTL",Numbers(0,2,1,0,0).Concat(new byte[]{0,1,0,10,0,0}).ToArray());
            }
            Chunk(output,"IDAT",Compressed(new byte[]{0,255,0,0,255,255,0,0,255}));
            if(animated) {
                Chunk(output,"fcTL",Numbers(1,1,1,1,0).Concat(new byte[]{0,1,0,0,2,1}).ToArray());
                Chunk(output,"fdAT",Numbers(2).Concat(Compressed(new byte[]{0,0,0,255,255})).ToArray());
            }
            Chunk(output,"IEND",new byte[0]); return output.ToArray();
        }
    }
    public static void Run() {
        string text="日本語 https://example.com/a_(b)).\nwww.example.org/path?q=1&x=2。 終わり";
        var parts=LumINE.Utils.MessageLinks.Split(text);
        Check(string.Concat(parts.Select(p=>p.Text))==text,"Link extraction changed copied message text.");
        var links=parts.Where(p=>p.Uri!=null).ToList();
        Check(links.Count==2 && links[0].Text=="https://example.com/a_(b)" && links[1].Uri.Scheme=="https", "Link punctuation, balanced brackets or www normalization failed.");
        Check(LumINE.Utils.MessageLinks.Split("javascript:alert(1) file:///private ftp://example.org").All(p=>p.Uri==null),"Unsupported URL schemes were linked.");
        Check(LumINE.Utils.MessageLinks.Split(null).Count==0,"Null text failed.");
        var animation=LumINE.Utils.ApngAnimation.Parse(Fixture());
        Check(animation.Width==2 && animation.Height==1 && animation.Plays==3 && animation.Frames.Count==2,"APNG canvas/frame count failed.");
        Check(animation.Frames[0].DelayMs==100 && animation.Frames[1].DelayMs==20 && animation.Frames[1].X==1 && animation.Frames[1].Dispose==2,"APNG delay, offset or disposal failed.");
        var png=animation.FramePng(animation.Frames[1]);
        Check(LumINE.Utils.ApngAnimation.Parse(png)==null,"Extracted frame is not a valid standalone PNG.");
        // Decompress the reconstructed PNG independently of the production parser.
        using(var compressed=new MemoryStream()) {
            for(int offset=8;offset<png.Length;) {
                int length=(png[offset]<<24)|(png[offset+1]<<16)|(png[offset+2]<<8)|png[offset+3];
                if(Encoding.ASCII.GetString(png,offset+4,4)=="IDAT") compressed.Write(png,offset+8,length);
                offset+=length+12;
            }
            compressed.Position=0;
            using(var zip=new System.IO.Compression.ZLibStream(compressed,System.IO.Compression.CompressionMode.Decompress))
            using(var raw=new MemoryStream()) { zip.CopyTo(raw); Check(raw.ToArray().SequenceEqual(new byte[]{0,0,0,255,255}),"APNG fdAT sequence number leaked into IDAT."); }
        }
        Check(LumINE.Utils.ApngAnimation.Parse(Fixture(false))==null,"Static PNG was treated as animated.");
        var corrupt=Fixture(); corrupt[30]^=1; bool rejected=false;
        try { LumINE.Utils.ApngAnimation.Parse(corrupt); } catch(InvalidDataException) { rejected=true; }
        Check(rejected,"Corrupt PNG checksum accepted.");
        rejected=false;
        try { LumINE.Utils.ApngAnimation.Parse(Fixture().Take(35).ToArray()); } catch(InvalidDataException) { rejected=true; }
        Check(rejected,"Truncated PNG accepted.");
        var canvas=new LumINE.Utils.ApngCanvas(2,1);
        canvas.Render(new LumINE.Utils.ApngFrame{Width=2,Height=1},new byte[]{0,0,255,255,0,0,255,255});
        canvas.Render(new LumINE.Utils.ApngFrame{Width=1,Height=1,X=1,Dispose=2,Blend=1},new byte[]{128,0,0,128});
        Check(canvas.Pixels[4]==128 && canvas.Pixels[6]==127 && canvas.Pixels[7]==255,"Premultiplied OVER blend failed.");
        canvas.Render(new LumINE.Utils.ApngFrame{Width=1,Height=1,Dispose=1},new byte[]{0,255,0,255});
        Check(canvas.Pixels[4]==0 && canvas.Pixels[6]==255,"PREVIOUS disposal did not restore the prior frame.");
        canvas.Render(new LumINE.Utils.ApngFrame{Width=1,Height=1,X=1},new byte[]{255,0,0,255});
        Check(canvas.Pixels[3]==0 && canvas.Pixels[4]==255,"BACKGROUND disposal did not clear the frame rectangle.");
    }
}
'@
$source = 'using System; using System.IO; using System.Linq; using System.Collections.Generic; using System.Text; using System.Text.RegularExpressions;' + (Read-Source 'LumINE/Utils/MessageLinks.cs') + (Read-Source 'LumINE/Utils/ApngAnimation.cs') + $fixtures
Add-Type -TypeDefinition $source
[PresentationChecks]::Run()
Write-Output 'PASS: 14 checks covering URL preservation and schemes, APNG frame reconstruction, CRC/truncation rejection, timing, alpha blending and disposal.'
