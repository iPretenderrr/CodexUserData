using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace CodexUserData
{
    internal static class MilestoneCurveStabilityProbe
    {
        private const long M=MilestoneEngine.Base;
        private static void Check(bool value,string message){StabilityProbe.Check(value,"Milestone curve: "+message);}
        private static MilestoneSnapshot Fixture(string root,string scope)
        {
            long now=LocalCodexUsage.Unix(DateTime.UtcNow),start=now-241*3600;var input=new MilestoneInput();
            for(int i=0;i<240;i++)input.Events.Add(new MilestoneEvent{From=start+i*3600,To=start+i*3600+(i==30?1800:0),Tokens=i==0?M/4:(i%7==0?22*M/10:8*M/10),Precision=i==15?1:i==30?2:0});
            return new MilestoneEngine(Path.Combine(root,"curve-fixtures")).Get(input,scope,"演示用量",now,CancellationToken.None);
        }
        private static async Task Settled(MilestoneCurvePanel panel)
        {await StabilityProbe.Until(()=>!StabilityProbe.Field<bool>(panel,"loading")&&!StabilityProbe.Field<bool>(panel,"pumpQueued"),"curve panel did not settle",5000);}
        private static void PressKey(MilestoneCurveChart chart,Key key)
        {chart.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(chart),0,key){RoutedEvent=Keyboard.KeyDownEvent});}
        private static void Capture(Window window,string path)
        {
            var content=(FrameworkElement)window.Content;var bitmap=new RenderTargetBitmap((int)content.ActualWidth,(int)content.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(content);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using(var stream=File.Create(path))png.Save(stream);
        }
        internal static async Task Run(string root)
        {
            string scope="curve-a";var value=Fixture(root,scope);int calls=0;long remembered=0;
            var panel=new MilestoneCurvePanel(cancel=>{calls++;return Task.FromResult(value);},()=>scope,M,choice=>remembered=choice);
            long start=value.Origin.From,end=value.ObservedAt+3600;panel.SetRange(start,end);
            var window=new Window{Width=880,Height=420,Content=new Border{Padding=new Thickness(16),Background=Theme.Background,Child=panel},Background=Theme.Background,ShowActivated=false,ShowInTaskbar=false};
            try
            {
                window.Show();await Settled(panel);window.UpdateLayout();await Task.Delay(30);
                var chart=StabilityProbe.Field<MilestoneCurveChart>(panel,"chart");var detail=StabilityProbe.Field<Border>(panel,"detail");
                Check(chart.DrawingBuilds>0&&chart.Baseline==0&&chart.ShowsEndpoint&&chart.Height>=260&&detail.Visibility==Visibility.Collapsed,"initial curve has the full-history baseline, current endpoint and no selected detail");
                var nodes=StabilityProbe.Field<List<MilestoneCurveNode>>(chart,"nodes");
                Check(nodes.Count>1&&nodes.Count<=64&&chart.SampleCount<=2*768+66,"time-bucket geometry and compressed nodes have bounded allocation");
                foreach(var node in nodes)Check(node.Time==value.Stage(M,node.Last,value.ObservedAt).End.From,"marker uses the report timestamp for the threshold, not a calendar bucket midpoint");
                PressKey(chart,Key.Right);long number=StabilityProbe.Field<long>(panel,"selected");
                Check(number>0&&StabilityProbe.Field<long>(chart,"selected")==number&&detail.Visibility==Visibility.Visible&&StabilityProbe.Field<TextBlock>(panel,"detailText").Text.Contains("个1亿"),"keyboard node selection highlights the matching preceding stage with one compact detail");
                int builds=chart.DrawingBuilds;double height=chart.ActualHeight;var drawing=StabilityProbe.Field<DrawingGroup>(chart,"drawing");
                for(int i=0;i<40;i++)chart.SetSelected(i%2==0?number:0);chart.SetSelected(number);await Task.Delay(30);
                Check(chart.DrawingBuilds==builds&&chart.ActualHeight==height&&drawing.IsFrozen,"selection overlays reuse the frozen drawing without resizing the curve");
                PressKey(chart,Key.Escape);Check(detail.Visibility==Visibility.Collapsed&&StabilityProbe.Field<long>(panel,"selected")==0,"Escape clears selection and the optional detail row");
                panel.SetRange(start,value.ObservedAt-2,true);window.UpdateLayout();await Task.Delay(30);PressKey(chart,Key.Right);number=StabilityProbe.Field<long>(panel,"selected");panel.SetRange(start,value.ObservedAt+3,true);window.UpdateLayout();await Task.Delay(30);
                Check(chart.ShowsEndpoint&&StabilityProbe.Field<long>(panel,"selected")==number&&number>0,"live endpoint follows a later snapshot observation and an advancing clock preserves selection");
                int before=calls;long windowStart=value.Curve.Times[120]+1;panel.SetRange(windowStart,value.Curve.Times[180]);window.UpdateLayout();await Task.Delay(30);
                Check(chart.Baseline==value.Curve.Totals[120]&&!chart.ShowsEndpoint&&calls==before,"historical viewport keeps the prior cumulative total and performs no data read");
                foreach(var node in StabilityProbe.Field<List<MilestoneCurveNode>>(chart,"nodes"))Check(node.Time>=windowStart&&node.Time<=value.Curve.Times[180],"historical viewport has only visible report nodes");
                panel.Refresh();await Settled(panel);window.UpdateLayout();await Task.Delay(30);builds=chart.DrawingBuilds;panel.Refresh();await Settled(panel);window.UpdateLayout();await Task.Delay(30);
                Check(chart.DrawingBuilds==builds,"unchanged provider results do not rebuild geometry");
                panel.SetRange(start,end);window.UpdateLayout();await Task.Delay(30);
                var choices=StabilityProbe.Field<Dictionary<long,Button>>(panel,"steps");Check(choices.Count==5,"all five milestone intervals are available");choices[5*M].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));window.UpdateLayout();await Task.Delay(30);
                Check(remembered==5*M&&calls==before+2&&StabilityProbe.Field<long>(panel,"selected")==0,"interval changes reuse the snapshot and persist the preference");panel.SetStep(10*M);Check(remembered==5*M&&StabilityProbe.Field<long>(panel,"step")==10*M,"external interval synchronization updates the view without a callback loop");choices[M].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                foreach(string mode in new[]{"dark","light"})
                {
                    Theme.Apply(new Preferences{ThemeMode=mode,LiveQuota=false,OrbAnimation="off"});window.Width=880;window.UpdateLayout();await Task.Delay(30);Capture(window,Path.Combine(root,"milestone-curve-"+mode+".png"));
                }
                PressKey(chart,Key.Right);long pinned=StabilityProbe.Field<long>(panel,"selected");window.Width=312;window.UpdateLayout();await Task.Delay(30);var group=chart.GroupFor(pinned);
                Check(group!=null&&StabilityProbe.Field<long>(panel,"selectedFirst")==group.First&&StabilityProbe.Field<long>(panel,"selectedLast")==group.Last,"resize preserves the stage while synchronizing grouped navigation with the visible nodes");
                window.UpdateLayout();Capture(window,Path.Combine(root,"milestone-curve-narrow.png"));
                Check(panel.ActualWidth<=280&&chart.ActualWidth==panel.ActualWidth&&detail.Visibility==Visibility.Visible,"280px content retains the curve and compact keyboard detail");

                // One report crosses billions of base thresholds, but remains one
                // mark and one node; Home/End provide bounded navigation in its group.
                var input=new MilestoneInput();input.Events.Add(new MilestoneEvent{From=start,To=start,Tokens=Int64.MaxValue});value=new MilestoneEngine(Path.Combine(root,"curve-fixtures")).Get(input,scope,"演示大批记录",end-1,CancellationToken.None);
                panel.Refresh();await Settled(panel);window.UpdateLayout();await Task.Delay(30);nodes=StabilityProbe.Field<List<MilestoneCurveNode>>(chart,"nodes");
                Check(nodes.Count==1&&nodes[0].First==1&&nodes[0].Last==Int64.MaxValue/M,"massive same-report crossings stay compressed to one visual node");
                PressKey(chart,Key.Right);PressKey(chart,Key.Home);Check(StabilityProbe.Field<long>(panel,"selected")==1&&StabilityProbe.Field<TextBlock>(panel,"detailText").Text.Contains("未知"),"same-report duration stays unknown, including its first threshold");
                PressKey(chart,Key.End);Check(StabilityProbe.Field<long>(panel,"selected")==Int64.MaxValue/M,"group keyboard navigation reaches the last stage without allocating a stage list");
                StabilityProbe.Field<Button>(panel,"previous").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Check(StabilityProbe.Field<long>(panel,"selected")==Int64.MaxValue/M-1,"compact group controls inspect an adjacent exact stage");
                panel.ClearSelection();Check(detail.Visibility==Visibility.Collapsed,"external clear resets the single detail row");
                input=new MilestoneInput();input.Events.Add(new MilestoneEvent{From=start,To=start,Tokens=Int64.MaxValue-8192});input.Events.Add(new MilestoneEvent{From=start+100,To=start+100,Tokens=4096});input.Events.Add(new MilestoneEvent{From=start+200,To=start+200,Tokens=4096});
                value=new MilestoneEngine(Path.Combine(root,"curve-fixtures")).Get(input,scope,"演示极大累计",start+300,CancellationToken.None);panel.SetRange(start+1,start+300);panel.Refresh();await Settled(panel);window.UpdateLayout();await Task.Delay(30);
                Check(chart.Baseline==Int64.MaxValue-8192&&chart.SampleCount<10&&StabilityProbe.Field<DrawingGroup>(chart,"drawing").IsFrozen,"tiny increments near Int64.MaxValue produce bounded representable ticks and a frozen drawing");
            }
            finally{panel.Dispose();window.Close();}

            var pending=new List<TaskCompletionSource<MilestoneSnapshot>>();scope="old";var old=Fixture(root,scope);var latest=Fixture(root,"latest");
            panel=new MilestoneCurvePanel(cancel=>{var task=new TaskCompletionSource<MilestoneSnapshot>();pending.Add(task);return task.Task;},()=>scope,M,null);panel.SetRange(old.Origin.From,old.ObservedAt+1);
            window=new Window{Width=600,Height=420,Content=panel,ShowActivated=false,ShowInTaskbar=false};
            try
            {
                window.Show();await StabilityProbe.Until(()=>pending.Count==1,"initial curve request missing",5000);
                scope="middle";panel.Refresh();scope="latest";panel.Refresh();long newStart=latest.Curve.Times[100]+1;panel.SetRange(newStart,latest.Curve.Times[190]);
                pending[0].SetResult(old);await StabilityProbe.Until(()=>pending.Count==2,"coalesced latest curve request missing",5000);
                Check(StabilityProbe.Field<MilestoneSnapshot>(panel,"snapshot")==null,"late old-source completion cannot repopulate the curve");pending[1].SetResult(latest);await Settled(panel);window.UpdateLayout();await Task.Delay(30);
                var chart=StabilityProbe.Field<MilestoneCurveChart>(panel,"chart");Check(pending.Count==2&&chart.Baseline==latest.Curve.Totals[100]&&!chart.ShowsEndpoint,"latest source result uses the current viewport and coalesces intermediate sources");
                panel.Refresh();await StabilityProbe.Until(()=>pending.Count==3,"hidden curve request missing",5000);panel.Visibility=Visibility.Collapsed;pending[2].SetResult(latest.WithStatus("hidden","",latest.ObservedAt+1,Int64.MaxValue));await Settled(panel);
                Check(StabilityProbe.Field<MilestoneSnapshot>(panel,"snapshot").Source!="hidden"&&!StabilityProbe.Field<DispatcherTimer>(panel,"timer").IsEnabled,"hidden curve rejects late completion and stops its timer");
                panel.Visibility=Visibility.Visible;await StabilityProbe.Until(()=>pending.Count==4,"restored curve request missing",5000);pending[3].SetResult(latest);await Settled(panel);
                window.WindowState=WindowState.Minimized;panel.Refresh();Check(!StabilityProbe.Field<DispatcherTimer>(panel,"timer").IsEnabled&&pending.Count==4,"minimization suspends curve refresh work");window.WindowState=WindowState.Normal;await StabilityProbe.Until(()=>pending.Count==5,"resumed curve request missing",5000);
                panel.Dispose();pending[4].SetResult(latest);await Settled(panel);Check(StabilityProbe.Field<bool>(panel,"disposed")&&StabilityProbe.Field<MilestoneSnapshot>(panel,"snapshot")==null&&!StabilityProbe.Field<DispatcherTimer>(panel,"timer").IsEnabled,"disposed curve ignores an uncancelable late result and releases its snapshot");
            }
            finally{panel.Dispose();window.Close();}
        }
    }
}
