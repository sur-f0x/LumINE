$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$text=Get-Content (Join-Path $root 'LumINE/Views/ChatListPage.xaml.cs') -Raw
function Method([string] $signature) {
 $start=$text.IndexOf($signature); if($start -lt 0){throw "Missing $signature"}; $end=$text.IndexOf('{',$start)+1;$depth=1
 while($depth -gt 0){if($text[$end] -eq '{'){$depth++};if($text[$end] -eq '}'){$depth--};$end++}
 $text.Substring($start,$end-$start)
}
$methods=@('public void OpenChat(', 'public void ShowChatList()', 'private void UpdateWorkspace()', 'private void WorkspaceBackRequested(')|ForEach-Object{Method $_}
$fixture=@'
namespace Windows.UI.Xaml { public enum Visibility { Visible,Collapsed } }
namespace Windows.UI.Core {
 public enum AppViewBackButtonVisibility { Visible,Collapsed }
 public class BackRequestedEventArgs { public bool Handled; }
 public class SystemNavigationManager { static SystemNavigationManager Instance=new SystemNavigationManager(); public AppViewBackButtonVisibility AppViewBackButtonVisibility; public static SystemNavigationManager GetForCurrentView(){return Instance;} }
}
public class Page {}
public class ChatRoomPage : Page { public string Draft="unsent draft"; }
public class SettingsPage : Page { public bool IsSaving; }
public class PhotoPage : Page {}
public class Frame {
 public object Content; public string Parameter;
 public Action Changed;
 public List<object> BackStack=new List<object>();
 public bool CanGoBack { get { return BackStack.Count>0; } }
 public bool Navigate(Type type,string parameter=null){if(Content!=null)BackStack.Add(Content);Content=Activator.CreateInstance(type);Parameter=parameter;if(Changed!=null)Changed();return true;}
 public void GoBack(){Content=BackStack[BackStack.Count-1];BackStack.RemoveAt(BackStack.Count-1);if(Changed!=null)Changed();}
}
public class SplitView { public bool IsPaneOpen;public double OpenPaneLength; }
public class Placeholder { public Windows.UI.Xaml.Visibility Visibility; }
public class Workspace {
 string _selectedChat;bool _active=true;
 public double ActualWidth;
 bool IsWide { get { return ActualWidth>=900; } }
 public Frame ConversationFrame=new Frame();public SplitView WorkspaceSplit=new SplitView();public Placeholder EmptyConversation=new Placeholder();
 public Workspace(){ConversationFrame.Changed=UpdateWorkspace;}
 public void Resize(double width){ActualWidth=width;UpdateWorkspace();}
 public void Back(Windows.UI.Core.BackRequestedEventArgs args){WorkspaceBackRequested(null,args);}
__METHODS__
}
public static class AdaptiveChecks {
 static int Count;
 static void Check(bool okay,string message){if(!okay)throw new Exception(message);Count++;}
 public static void Run(){
  var host=new Workspace();host.Resize(360);
  Check(host.WorkspaceSplit.IsPaneOpen && host.WorkspaceSplit.OpenPaneLength==360,"Phone list does not use the available width.");
  host.OpenChat("cgroup");var room=host.ConversationFrame.Content;
  Check(!host.WorkspaceSplit.IsPaneOpen && host.ConversationFrame.Parameter=="cgroup","Phone selection did not switch to the conversation.");
  host.Resize(1280);
  Check(host.WorkspaceSplit.IsPaneOpen && host.WorkspaceSplit.OpenPaneLength==380,"Desktop list/detail pane dimensions are wrong.");
  Check(object.ReferenceEquals(room,host.ConversationFrame.Content) && ((ChatRoomPage)room).Draft=="unsent draft","Resize recreated the conversation or lost its draft.");
  host.Resize(899);Check(!host.WorkspaceSplit.IsPaneOpen,"Narrowing a selected conversation returned to the list.");
  host.Resize(900);Check(host.WorkspaceSplit.IsPaneOpen,"Wide layout did not activate at the breakpoint.");
  host.ConversationFrame.Navigate(typeof(PhotoPage));var back=new Windows.UI.Core.BackRequestedEventArgs();host.Back(back);
  Check(back.Handled && object.ReferenceEquals(host.ConversationFrame.Content,room),"Back from preview did not return to the same conversation.");
  host.Back(back);Check(object.ReferenceEquals(host.ConversationFrame.Content,room),"An already-handled Back event navigated a second time.");
  host.Resize(360);host.Back(new Windows.UI.Core.BackRequestedEventArgs());
  Check(host.WorkspaceSplit.IsPaneOpen && host.EmptyConversation.Visibility==Windows.UI.Xaml.Visibility.Visible && !host.ConversationFrame.CanGoBack,"Back from a phone conversation did not restore the list.");
  host.OpenChat("ufriend");host.OpenChat("cother");
  Check(host.ConversationFrame.Parameter=="cother" && !host.ConversationFrame.CanGoBack,"Changing selection accumulated stale conversation history.");
  host.ShowChatList();host.Resize(1920);
  Check(host.WorkspaceSplit.IsPaneOpen && host.EmptyConversation.Visibility==Windows.UI.Xaml.Visibility.Visible,"Empty desktop workspace has no placeholder/list.");
  host.ConversationFrame.Navigate(typeof(SettingsPage));host.Resize(360);
  var settings=(SettingsPage)host.ConversationFrame.Content;settings.IsSaving=true;
  var savingBack=new Windows.UI.Core.BackRequestedEventArgs();host.Back(savingBack);
  Check(savingBack.Handled && object.ReferenceEquals(host.ConversationFrame.Content,settings),"Back interrupted settings persistence.");
  settings.IsSaving=false;host.Back(new Windows.UI.Core.BackRequestedEventArgs());
  Check(host.WorkspaceSplit.IsPaneOpen && host.ConversationFrame.Content.GetType()==typeof(Page),"Standalone settings did not return to the phone list.");
  Console.WriteLine("PASS: "+Count+" adaptive resize, draft preservation, nested preview, selection and back-navigation checks.");
 }
}
'@
Add-Type -TypeDefinition ('using System;using System.Collections.Generic;'+$fixture.Replace('__METHODS__',($methods -join "`n")))
[AdaptiveChecks]::Run()
[xml]$xaml=Get-Content (Join-Path $root 'LumINE/Views/ChatListPage.xaml') -Raw
$ns=[Xml.XmlNamespaceManager]::new($xaml.NameTable);$ns.AddNamespace('u','http://schemas.microsoft.com/winfx/2006/xaml/presentation')
foreach($control in @('SplitView','Frame','AdaptiveTrigger','ListView','CommandBar')) { if(!$xaml.SelectSingleNode('//u:'+$control,$ns)){throw "Required UWP control missing: $control"} }
Write-Output 'PASS: UWP SplitView/Frame, adaptive triggers, ListView navigation and CommandBar declarations.'
