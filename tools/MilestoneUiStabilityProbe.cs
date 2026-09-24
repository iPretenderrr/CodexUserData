using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CodexUserData
{
    internal static class MilestoneUiStabilityProbe
    {
        private static MilestoneSnapshot Fixture(string scope,long count)
        {
            long now=LocalCodexUsage.Unix(DateTime.UtcNow),time=now-7200;var marks=new List<MilestoneMark>();
            for(long i=count;i>=1;i--){marks.Insert(0,new MilestoneMark{First=i,Last=i,From=time,To=time,Batch=i+1});time-=(2+i*7%13)*3600;}
            // The oldest page includes inferred and day-granularity timing.
            if(count>5){marks[1].Precision=1;marks[3].Precision=2;marks[3].To+=3600;}
            if(count>30){marks[marks.Count-3].Batch=marks[marks.Count-4].Batch;marks[marks.Count-1].Precision=2;marks[marks.Count-1].To+=3600;}
            return new MilestoneSnapshot{Scope=scope,Source="演示用量",Signature=scope+count,TotalTokens=count*MilestoneEngine.Base+MilestoneEngine.Base/2,ObservedAt=now,Origin=new MilestoneBoundary{From=time,To=time,Batch=1},Marks=marks};
        }
        private static async Task Settled(MilestonePanel panel)
        {await StabilityProbe.Until(()=>!StabilityProbe.Field<bool>(panel,"loading"),"milestone panel did not settle",5000);}
        private static void Click(MilestonePanel panel,string name)
        {StabilityProbe.Field<Button>(panel,name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));}
        private static void Capture(Window window,string path)
        {
            // Capture client content; Window.ActualHeight also contains the native title bar.
            var content=(FrameworkElement)window.Content;var bitmap=new RenderTargetBitmap((int)content.ActualWidth,(int)content.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(content);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using(var output=File.Create(path))png.Save(output);
        }
        internal static async Task Run(string root)
        {
            string scope="a";long remembered=0;bool failure=false;MilestoneSnapshot data=Fixture(scope,65);TaskCompletionSource<MilestoneSnapshot> delayed=null;
            var panel=new MilestonePanel(cancel=>failure?Task.FromException<MilestoneSnapshot>(new IOException("fixture unavailable")):delayed==null?Task.FromResult(data):delayed.Task,()=>scope,MilestoneEngine.Base,value=>remembered=value){Margin=new Thickness(16)};
            var window=new Window{Width=860,Height=710,Content=new ScrollViewer{Content=panel,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled},Background=Theme.Background,ShowActivated=false,ShowInTaskbar=false};
            try
            {
                window.Show();await Settled(panel);window.UpdateLayout();
                var rows=StabilityProbe.Field<StackPanel>(panel,"rows");var detail=StabilityProbe.Field<Border>(panel,"detail");var chart=StabilityProbe.Field<MilestoneDurationChart>(panel,"chart");
                var records=StabilityProbe.Field<Expander>(panel,"records");
                StabilityProbe.Check(rows.Children.Count==0&&!records.IsExpanded&&detail.Visibility==Visibility.Collapsed&&chart.Height>=220,"milestone opens with a large chart and no record wall or empty detail height");
                StabilityProbe.Check(Math.Abs(StabilityProbe.Field<double>(StabilityProbe.Field<MilestoneProgressRing>(panel,"progress"),"fraction")-.5)<.001,"current progress ring reflects the remaining stage fraction");
                StabilityProbe.Check(StabilityProbe.Field<Border>(panel,"explanation").Visibility==Visibility.Collapsed,"statistical explanations are collapsed by default");
                records.IsExpanded=true;StabilityProbe.Check(rows.Children.Count==30,"optional records are allocated only when expanded");
                ((Button)rows.Children[0]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                StabilityProbe.Check(detail.Visibility==Visibility.Visible&&StabilityProbe.Field<long>(panel,"selected")==65&&StabilityProbe.Field<long>(chart,"selected")==65,"record selection shares the chart highlight and expanded detail");
                Click(panel,"clear");StabilityProbe.Check(detail.Visibility==Visibility.Collapsed&&StabilityProbe.Field<long>(chart,"selected")==0,"clear removes both selection and detail height");
                window.UpdateLayout();await Task.Delay(40);
                double height=chart.ActualHeight;int builds=chart.DrawingBuilds;for(int i=0;i<100;i++)chart.SetSelected(i%2==0?65:0);await Task.Delay(30);
                StabilityProbe.Check(chart.ActualHeight==height&&chart.DrawingBuilds==builds,"selection overlays do not resize or rebuild the cached milestone plot");
                Click(panel,"older");long anchor=StabilityProbe.Field<long>(panel,"pageEnd");StabilityProbe.Check(anchor==35&&(long)((Button)rows.Children[0]).Tag==35,"older page shows the prior thirty stages in descending order");
                data=Fixture(scope,67);panel.Refresh();await Settled(panel);StabilityProbe.Check(StabilityProbe.Field<long>(panel,"pageEnd")==anchor&&(long)((Button)rows.Children[0]).Tag==35,"new usage does not jump an older reading page to the latest records");
                Click(panel,"newer");Click(panel,"newer");StabilityProbe.Check(StabilityProbe.Field<long>(panel,"pageEnd")==0,"newer pagination returns to the latest stages");
                window.Width=350;await Task.Delay(30);window.UpdateLayout();
                StabilityProbe.Check(StabilityProbe.Field<bool>(panel,"narrow")&&((Grid)((Button)rows.Children[0]).Content).ColumnDefinitions.Count==2&&panel.ActualWidth<350,"narrow milestone pages wrap step buttons and simplify row date columns");
                ((Button)rows.Children[0]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));StabilityProbe.Check(detail.Visibility==Visibility.Visible,"narrow records retain complete start/end details on selection");Click(panel,"clear");
                delayed=new TaskCompletionSource<MilestoneSnapshot>();var retained=rows.Children[0];panel.Refresh();
                StabilityProbe.Check(Object.ReferenceEquals(rows.Children[0],retained)&&StabilityProbe.Field<MilestoneSnapshot>(panel,"snapshot")!=null,"ordinary pending refresh preserves the cached screen without a loading flash");
                delayed.SetResult(data);delayed=null;await Settled(panel);
                failure=true;panel.Refresh();await Settled(panel);var current=StabilityProbe.Field<TextBlock>(panel,"currentText");current.Text="outdated";
                var clock=StabilityProbe.Field<System.Windows.Threading.DispatcherTimer>(panel,"timer");clock.Interval=TimeSpan.FromMilliseconds(15);
                await StabilityProbe.Until(()=>current.Text!="outdated","elapsed time froze after read failure",1000);
                StabilityProbe.Check((string)StabilityProbe.Field<TextBlock>(panel,"status").ToolTip=="fixture unavailable"&&Object.ReferenceEquals(rows.Children[0],retained),"elapsed clock advances during a source outage while keeping a compact warning and history");
                clock.Interval=TimeSpan.FromMinutes(1);failure=false;panel.Refresh();await Settled(panel);
                var choices=StabilityProbe.Field<Dictionary<long,Button>>(panel,"steps");choices[500000000].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                StabilityProbe.Check(remembered==500000000&&rows.Children.Count==13,"milestone step changes use the shared totals and persist through the callback");choices[MilestoneEngine.Base].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                records.IsExpanded=false;StabilityProbe.Check(rows.Children.Count==0,"collapsing optional records releases their controls");
                foreach(string mode in new[]{"dark","light"})
                {
                    Theme.Apply(new Preferences{ThemeMode=mode,LiveQuota=false,OrbAnimation="off"});window.Width=860;await Task.Delay(30);window.UpdateLayout();
                    Capture(window,Path.Combine(root,"milestone-"+mode+".png"));
                }
                window.Width=350;await Task.Delay(30);window.UpdateLayout();Capture(window,Path.Combine(root,"milestone-narrow.png"));
                chart.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,PresentationSource.FromVisual(chart),0,System.Windows.Input.Key.Right){RoutedEvent=System.Windows.Input.Keyboard.KeyDownEvent});
                StabilityProbe.Check(detail.Visibility==Visibility.Visible&&StabilityProbe.Field<long>(panel,"selected")>0,"chart keyboard navigation reveals a single stage without opening the list");
                chart.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,PresentationSource.FromVisual(chart),0,System.Windows.Input.Key.Escape){RoutedEvent=System.Windows.Input.Keyboard.KeyDownEvent});
                StabilityProbe.Check(detail.Visibility==Visibility.Collapsed,"Escape clears chart selection and its detail");
                StabilityProbe.Check(MilestonePanel.Duration(59)=="<1分钟"&&MilestonePanel.Duration(90060).Contains("1天")&&MilestonePanel.DurationText(data.Stage(MilestoneEngine.Base,2,LocalCodexUsage.Unix(DateTime.UtcNow))).Contains("未知"),"duration labels preserve natural days and unknown inferred timing");
            }
            finally{panel.Dispose();window.Close();}

            var pending=new List<TaskCompletionSource<MilestoneSnapshot>>();scope="first";
            panel=new MilestonePanel(cancel=>{var task=new TaskCompletionSource<MilestoneSnapshot>();pending.Add(task);return task.Task;},()=>scope,MilestoneEngine.Base,null);
            window=new Window{Width=450,Height=480,Content=panel,ShowActivated=false,ShowInTaskbar=false};
            try
            {
                window.Show();await StabilityProbe.Until(()=>pending.Count==1,"initial milestone request missing",5000);
                // Ignore cancellation intentionally: stale-result guards must still protect the UI.
                scope="second";panel.SourceChanged();scope="third";panel.SourceChanged();
                StabilityProbe.Check(StabilityProbe.Field<MilestoneSnapshot>(panel,"snapshot")==null&&StabilityProbe.Field<StackPanel>(panel,"rows").Children.Count==0,"source switches immediately remove prior milestone records");
                pending[0].SetResult(Fixture("first",4));await StabilityProbe.Until(()=>pending.Count==2,"coalesced milestone request missing",5000);
                StabilityProbe.Check(StabilityProbe.Field<MilestoneSnapshot>(panel,"snapshot")==null,"late first-source completion cannot populate a newer source");
                pending[1].SetResult(Fixture("third",8));await Settled(panel);StabilityProbe.Check(StabilityProbe.Field<MilestoneSnapshot>(panel,"snapshot").Scope=="third"&&pending.Count==2,"rapid source changes coalesce into a single latest-source reload");
                window.WindowState=WindowState.Minimized;panel.Refresh();StabilityProbe.Check(pending.Count==2&&!StabilityProbe.Field<System.Windows.Threading.DispatcherTimer>(panel,"timer").IsEnabled,"minimized milestone windows stop periodic refresh work");
                window.WindowState=WindowState.Normal;await StabilityProbe.Until(()=>pending.Count==3,"hidden request missing",5000);panel.Visibility=Visibility.Collapsed;panel.Refresh();
                pending[2].SetResult(Fixture("third",9));await Settled(panel);StabilityProbe.Check(pending.Count==3&&StabilityProbe.Field<MilestoneSnapshot>(panel,"snapshot").Completed(MilestoneEngine.Base)==8,"hidden panel cancels work and rejects a late result without starting a refresh");
                panel.Visibility=Visibility.Visible;await StabilityProbe.Until(()=>pending.Count==4,"restored request missing",5000);pending[3].SetResult(Fixture("third",10));await Settled(panel);
                StabilityProbe.Check(StabilityProbe.Field<MilestoneSnapshot>(panel,"snapshot").Completed(MilestoneEngine.Base)==10,"restoring the milestone panel reloads current data");
                scope="fourth";panel.SourceChanged();await StabilityProbe.Until(()=>pending.Count==5,"new source request missing",5000);
                StabilityProbe.Check(StabilityProbe.Field<MilestoneSnapshot>(panel,"snapshot")==null&&StabilityProbe.Field<StackPanel>(panel,"rows").Children.Count==0,"changing a populated source removes its cached records immediately");
                pending[4].SetResult(Fixture("fourth",11));await Settled(panel);
                panel.Refresh();await StabilityProbe.Until(()=>pending.Count==6,"closing request missing",5000);panel.Dispose();window.Close();pending[5].SetResult(Fixture("fourth",12));await Task.Delay(30);
                StabilityProbe.Check(StabilityProbe.Field<MilestoneSnapshot>(panel,"snapshot")==null&&!StabilityProbe.Field<System.Windows.Threading.DispatcherTimer>(panel,"timer").IsEnabled,"disposed milestone panel ignores late completion and stops its timer");
            }
            finally{panel.Dispose();window.Close();}
        }
    }
}
