$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
function Read-Source([string] $path) {
    [regex]::Replace((Get-Content -LiteralPath (Join-Path $repoRoot $path) -Raw), '(?m)^using [^\r\n]+;\r?\n', '')
}
$fixtures = @'
public static class NotificationChecks {
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    static System.Xml.XmlDocument Xml(string text) { var doc = new System.Xml.XmlDocument(); doc.LoadXml(text); return doc; }
    public static void Run() {
        var ledger = new LumINE.Services.Vyline.NotificationLedger();
        Check(ledger.Observe("upeer","1",false) && ledger.Total==1,"First incoming notification must count once.");
        Check(!ledger.Observe("upeer","1",false) && ledger.Total==1,"Realtime and UI drain counted the same message twice.");
        ledger.Observe("cgroup","2",false); ledger.Observe("cgroup","3",false);
        Check(ledger.Total==3 && ledger.Unread["cgroup"]==2,"Unread totals must sum messages across chats.");
        int before = ledger.ReadVersion("upeer"); ledger.Read("upeer");
        Check(ledger.Total==2 && ledger.ReadVersion("upeer")!=before,"Reading a chat must invalidate in-flight notification work.");
        Check(!ledger.Observe("upeer","1",false) && ledger.Total==2,"A read notification reappeared after replay.");
        Check(ledger.Observe("uopen","4",true) && ledger.Total==2 && !ledger.Observe("uopen","4",false),"An open foreground chat must not generate a later unread notification.");
        ledger.Clear(); Check(ledger.Total==0 && ledger.Seen.Count==4,"Clearing pending notifications must preserve deduplication.");
        var restored = new LumINE.Services.Vyline.NotificationLedger(); restored.Seen.AddRange(ledger.Seen);
        Check(!restored.Observe("cgroup","2",false),"Restored seen IDs did not prevent duplicate notifications.");
        for(int i=0;i<2100;i++) restored.Observe("bulk", "new"+i, false);
        Check(restored.Seen.Count==2000 && restored.Total==2100,"Seen-message storage must be bounded without capping unread at that bound.");
        string name="A&B <名前> 😍", body="一行目\nURL: https://example.com/?x=1&y=2 <text> \"quoted\"";
        string icon="ms-appdata:///local/accounts/test/avatars/icon.png";
        var toast = Xml(LumINE.Utils.NotificationContent.Toast(name,body,icon));
        var texts=toast.SelectNodes("/toast/visual/binding/text");
        Check(texts.Count==2 && texts[0].InnerText==name && texts[1].InnerText==body,"Toast text was lost or interpreted as XML markup.");
        Check(toast.SelectSingleNode("/toast/visual/binding").Attributes["template"].Value=="ToastGeneric" && toast.SelectSingleNode("//image").Attributes["placement"].Value=="appLogoOverride" && toast.SelectSingleNode("//image").Attributes["src"].Value==icon,"Sender icon is not an app-logo override.");
        Check(Xml(LumINE.Utils.NotificationContent.Toast(name,body,null)).SelectNodes("//image").Count==0,"Missing avatars must still yield a valid toast.");
        var safe=Xml(LumINE.Utils.NotificationContent.Toast("bad\u0001\ud800name",body,icon));
        Check(safe.SelectSingleNode("//text").InnerText=="badname","Invalid XML Unicode was not removed.");
        var tile=Xml(LumINE.Utils.NotificationContent.Tile(name,body,icon,12));
        Check(tile.SelectNodes("//binding").Count==2 && tile.SelectSingleNode("//binding[@template='TileMedium']")!=null && tile.SelectSingleNode("//binding[@template='TileWide']")!=null && tile.InnerText.Contains("未読 12件"),"Medium/wide tile content or unread count is missing.");
        Check(Xml(LumINE.Utils.NotificationContent.Badge(123)).DocumentElement.GetAttribute("value")=="100" && Xml(LumINE.Utils.NotificationContent.Badge(-1)).DocumentElement.GetAttribute("value")=="0","Badge must allow Windows' 99+ state and clamp negative values.");
    }
}
'@
$source = 'using System; using System.Text; using System.Linq; using System.Collections.Generic;' + (Read-Source 'LumINE/Utils/NotificationContent.cs') + (Read-Source 'LumINE/Services/Vyline/NotificationLedger.cs') + $fixtures
Add-Type -TypeDefinition $source
[NotificationChecks]::Run()
$manifest = [xml](Get-Content -LiteralPath (Join-Path $repoRoot 'LumINE/Package.appxmanifest') -Raw)
$ns = [Xml.XmlNamespaceManager]::new($manifest.NameTable)
$ns.AddNamespace('uap','http://schemas.microsoft.com/appx/manifest/uap/windows10')
$lock = $manifest.SelectSingleNode('//uap:LockScreen',$ns)
if ($null -eq $lock -or $lock.Notification -ne 'badgeAndTileText') { throw 'Lock screen eligibility is not declared.' }
Write-Output 'PASS: 16 checks covering toast/tile XML, sender icons, Unicode, unread deduplication/read races, persistence inputs, badge ranges and lock-screen registration.'
