$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$source = Get-Content (Join-Path $root 'LumINE/Views/ChatRoomPage.xaml.cs') -Raw
function Method($name) {
    $match = [regex]::Match($source, '(?m)^        private .*\b' + $name + '\(')
    if (!$match.Success) { throw "Missing scroll method: $name" }
    $brace = $source.IndexOf('{', $match.Index); $depth = 1; $end = $brace + 1
    while ($depth -gt 0) { if ($source[$end] -eq '{') { $depth++ }; if ($source[$end] -eq '}') { $depth-- }; $end++ }
    $source.Substring($match.Index, $end - $match.Index)
}
$methods = @('ScrollToLatest','QueueBottomScroll','CanFollowLatest','AlignLatestMessage','FindMessageScrollViewer') | ForEach-Object { Method $_ }
$fixture = @'
using System;using System.Collections.Generic;using Windows.UI.Xaml;using Windows.UI.Xaml.Controls;using Windows.UI.Core;
namespace Windows.UI.Core {public enum CoreDispatcherPriority{Low}}
namespace Windows.UI.Xaml {public class DependencyObject{public List<DependencyObject> Children=new List<DependencyObject>();}}
namespace Windows.UI.Xaml.Media {public static class VisualTreeHelper{
 public static int GetChildrenCount(DependencyObject d){return d.Children.Count;}
 public static DependencyObject GetChild(DependencyObject d,int i){return d.Children[i];}
}}
namespace Windows.UI.Xaml.Controls {
 public class ScrollViewer:DependencyObject {
  public double ScrollableHeight,VerticalOffset;public int Changes;
  public bool ChangeView(double? x,double? y,float? zoom,bool noAnimation){Changes++;VerticalOffset=Math.Min(y.Value,ScrollableHeight);return true;}
 }
}
public static class BackgroundNotifications {public static bool IsForeground=true;}
namespace LumINE.Utils {public static class CrashDiagnostics{public static void Record(string stage,Exception e){throw new Exception(stage,e);}}}
public class TestDispatcher {
 public Queue<Action> Pending=new Queue<Action>();
 public object RunAsync(CoreDispatcherPriority priority,Action action){Pending.Enqueue(action);return null;}
 public void Drain(){int limit=100;while(Pending.Count>0){if(--limit==0)throw new Exception("Scroll loop did not settle.");Pending.Dequeue()();}}
}
public class TestList:DependencyObject {
 public double ActualWidth,ActualHeight,LastItemHeight;public int Realizations;
 public ScrollViewer Scroll=new ScrollViewer();public List<object> Items=new List<object>();object realized;
 public TestList(){Children.Add(Scroll);}
 public object ContainerFromItem(object item){return object.ReferenceEquals(realized,item)?item:null;}
 public void ScrollIntoView(object item){realized=item;Realizations++;Scroll.VerticalOffset=Math.Max(0,Scroll.ScrollableHeight-LastItemHeight);}
 public void UpdateLayout(){}
}
public class TestViewModel {public List<object> Messages;}
public class ChatScrollHost {
 bool _active=true,_followLatest,_bottomScrollQueued,_searchOpen,_searchPinned;
 ScrollViewer _messagesScroll;object _bottomScrollTarget;
 public TestList MessagesList=new TestList();public TestViewModel ViewModel;
 public TestDispatcher Dispatcher=new TestDispatcher();
 public ChatScrollHost(){ViewModel=new TestViewModel{Messages=MessagesList.Items};}
 public void Request(){ScrollToLatest();}public void Layout(){QueueBottomScroll();}
 public void UserScroll(){_followLatest=false;}
 public void Leave(){_active=false;_followLatest=false;}
 public void Search(bool open,bool pinned){_searchOpen=open;_searchPinned=pinned;}
__METHODS__
}
public static class ScrollChecks {
 static int count;static void Check(bool value,string message){if(!value)throw new Exception(message);count++;}
 static ChatScrollHost Ready(double width=360){var h=new ChatScrollHost();h.MessagesList.ActualWidth=width;h.MessagesList.ActualHeight=600;h.MessagesList.LastItemHeight=900;h.MessagesList.Scroll.ScrollableHeight=2400;h.MessagesList.Items.Add(new object());return h;}
 public static void Run(){
  var narrow=Ready();narrow.MessagesList.ActualWidth=0;narrow.Request();narrow.Dispatcher.Drain();
  Check(narrow.MessagesList.Scroll.Changes==0,"A hidden SplitView conversation was scrolled before it had width.");
  narrow.MessagesList.ActualWidth=360;narrow.Layout();narrow.Dispatcher.Drain();
  Check(narrow.MessagesList.Scroll.VerticalOffset==2400,"A scroll request was lost during the narrow-pane transition.");
  Check(narrow.MessagesList.Realizations==1,"The last virtualized row was not realized.");
  Check(narrow.MessagesList.Scroll.VerticalOffset>2400-900,"A message taller than the viewport was aligned at its top rather than its bottom.");
  narrow.MessagesList.Scroll.ScrollableHeight=3300;narrow.Layout();narrow.Dispatcher.Drain();
  Check(narrow.MessagesList.Scroll.VerticalOffset==3300,"Late image sizing or wrapped-row height left the view above the bottom.");
  int changes=narrow.MessagesList.Scroll.Changes;narrow.Layout();narrow.Layout();narrow.Layout();narrow.Dispatcher.Drain();
  Check(narrow.MessagesList.Scroll.Changes==changes,"Stable layout repeatedly changed the view.");
  narrow.UserScroll();narrow.MessagesList.Scroll.VerticalOffset=1200;narrow.MessagesList.Scroll.ScrollableHeight=3900;narrow.Layout();narrow.Dispatcher.Drain();
  Check(narrow.MessagesList.Scroll.VerticalOffset==1200,"Layout fought manual scrolling/text selection.");
  narrow.Request();narrow.UserScroll();narrow.Dispatcher.Drain();Check(narrow.MessagesList.Scroll.VerticalOffset==1200,"A queued callback ignored later user interaction.");
  narrow.Request();narrow.Dispatcher.Drain();Check(narrow.MessagesList.Scroll.VerticalOffset==3900,"An explicit latest-message request did not re-enable scrolling.");
  var hidden=Ready();hidden.MessagesList.ActualHeight=0;hidden.Request();hidden.Dispatcher.Drain();hidden.MessagesList.ActualHeight=600;hidden.Layout();hidden.Dispatcher.Drain();
  Check(hidden.MessagesList.Scroll.VerticalOffset==2400,"A request was lost while the list had zero height.");
  var empty=Ready();empty.MessagesList.Items.Clear();empty.Request();empty.Dispatcher.Drain();empty.MessagesList.Items.Add(new object());empty.Layout();empty.Dispatcher.Drain();
  Check(empty.MessagesList.Scroll.VerticalOffset==2400,"An initially empty cache discarded the initial scroll request.");
  var coalesced=Ready();for(int i=0;i<100;i++)coalesced.Request();
  Check(coalesced.Dispatcher.Pending.Count==1,"Message and layout callbacks queued duplicate scroll operations.");coalesced.Dispatcher.Drain();
  var leaving=Ready();leaving.Request();leaving.Leave();leaving.Dispatcher.Drain();Check(leaving.MessagesList.Scroll.Changes==0,"A deferred scroll ran after navigation away.");
  var searching=Ready();searching.Request();searching.Search(false,true);searching.Dispatcher.Drain();Check(searching.MessagesList.Scroll.Changes==0,"Deferred scrolling overrode a pinned search result.");
  searching.Search(true,false);searching.Request();searching.Layout();searching.Dispatcher.Drain();Check(searching.MessagesList.Scroll.Changes==0,"Open search was moved to the bottom.");
  var foreground=Ready();BackgroundNotifications.IsForeground=false;foreground.Request();foreground.Dispatcher.Drain();Check(foreground.MessagesList.Scroll.Changes==0,"A hidden application scrolled.");
  BackgroundNotifications.IsForeground=true;foreground.Layout();foreground.Dispatcher.Drain();Check(foreground.MessagesList.Scroll.VerticalOffset==2400,"Foreground recovery lost the requested bottom position.");
  foreach(double width in new[]{320d,360d,540d,899d,900d,1280d}){var h=Ready(width);h.Request();h.Dispatcher.Drain();Check(h.MessagesList.Scroll.VerticalOffset==2400,"Bottom alignment differs by width: "+width);}
  Console.WriteLine("PASS: "+count+" narrow-pane readiness, tall rows, late media, user interaction, coalescing, navigation/search and width checks.");
 }
}
'@
Add-Type -TypeDefinition $fixture.Replace('__METHODS__',($methods -join "`n"))
[ScrollChecks]::Run()
