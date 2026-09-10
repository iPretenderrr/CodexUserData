using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CodexUserData
{
    // Documentation and visual checks share deterministic, generated data only.
    internal static class GuideStabilityProbe
    {
        internal static UsageSnapshot Snapshot()
        {
            var today=DateTime.Today;var days=DailyUsage.Empty(today,180);
            for(int i=0;i<days.Length;i++)
            {
                long weight=i%17==0?0:1+(i%11)*(i%11);
                long input=(180000+(i%9)*19000)*weight/60,output=(32000+(i%7)*7000)*weight/60,cache=(720000+(i%13)*43000)*weight/60;
                days[i].Add(input,output,cache,0,35+i%12,0,0);
                ModelUsage.Accumulate(days[i].Models,"gpt-5.5","high",input,output,cache,0,35+i%12);
                if(i%5!=0){days[i].Add(input/2,output/2,cache/3,0,12,0,0);ModelUsage.Accumulate(days[i].Models,"gpt-5.3-codex","medium",input/2,output/2,cache/3,0,12);}
            }
            var last=days.Last();long now=LocalCodexUsage.Unix(DateTime.Now);
            var snapshot=new UsageSnapshot{Daily=days,Hourly=DailyUsage.Hours(today),HourlyThrough=Math.Max(1,DateTime.Now.Hour+1),SourceName="本地 Codex",CountLabel="用量记录",CostAvailable=false,TotalTokens=last.Tokens,Requests=last.Requests,InputTokens=last.Input,OutputTokens=last.Output,CacheReadTokens=last.CacheRead,CacheHitRate=76.4,Models=last.Models.ToList(),KnownModels=new[]{"gpt-5.5","gpt-5.3-codex"},EquivalentUsd=last.Models.Sum(m=>m.EquivalentUsd),LatestRecord="演示记录",LatestUsageUnix=now};
            for(int i=0;i<snapshot.HourlyThrough;i++){long input=last.Input/snapshot.HourlyThrough,output=last.Output/snapshot.HourlyThrough,cache=last.CacheRead/snapshot.HourlyThrough;snapshot.Hourly[i].Add(input,output,cache,0,3,0,0);ModelUsage.Accumulate(snapshot.Hourly[i].Models,"gpt-5.5","high",input,output,cache,0,3);}
            snapshot.Quotas.Add(new QuotaBucket{Id="codex",Name="Codex",Origin="在线查询",ObservedAt=now,Primary=new QuotaWindow{Minutes=300,UsedPercent=28,ResetsAt=now+7200},Secondary=new QuotaWindow{Minutes=10080,UsedPercent=43,ResetsAt=now+172800}});
            return snapshot;
        }
        internal static FrameworkElement Find(DependencyObject root,string id)
        {var element=root as FrameworkElement;if(element!=null&&AutomationProperties.GetAutomationId(element)==id)return element;for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){var found=Find(VisualTreeHelper.GetChild(root,i),id);if(found!=null)return found;}return null;}
        private static void Save(FrameworkElement element,string path)
        {
            element.UpdateLayout();int width=(int)Math.Ceiling(element.ActualWidth),height=(int)Math.Ceiling(element.ActualHeight);
            // Render at 2x directly so the user guide stays sharp when printed or zoomed.
            var image=new RenderTargetBitmap(width*2,height*2,192,192,PixelFormats.Pbgra32);image.Render(element);
            var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(image));using(var file=File.Create(path))png.Save(file);
        }
        internal static async Task Run(string root)
        {
            string output=Path.Combine(Path.GetDirectoryName(root),"guide-images");Directory.CreateDirectory(output);var data=Snapshot();
            foreach(string mode in new[]{"dark","light"})
            {
                var p=new Preferences{Source="local",CodexHome=Path.Combine(root,"codex"),LiveQuota=false,ShowQuota=true,QuotaCli="",ThemeMode=mode,OrbAnimation="off",Width=380,Height=700,Collapsed=false,Left=120,Top=90,ShowModels=true};
                var widget=new WidgetWindow(p,true){ShowActivated=false};widget.Show();widget.ApplySnapshot(data);widget.ApplyActivityReport(new ActivityReport{ActiveTasks=1,ObservedAt=LocalCodexUsage.Unix(DateTime.Now),Until=LocalCodexUsage.Unix(DateTime.Now)+60});await Task.Delay(70);
                var state=Find(widget,"ActivityStatus") as TextBlock;StabilityProbe.Check(state!=null&&state.Text.Contains("运行"),mode+" main view exposes the actual task state");
                ((TextBlock)Find(widget,"RefreshStatus")).Text="演示数据 · 刚刚更新";
                Save((FrameworkElement)widget.Content,Path.Combine(output,"main-"+mode+".png"));
                var settings=new SettingsWindow(p,v=>{}){ShowActivated=false};settings.Show();StabilityProbe.Call(settings,"SelectPage","floating");settings.UpdateLayout();await Task.Delay(50);
                StabilityProbe.Check(Find(settings,"BallPositionLocked") is CheckBox,"settings expose position locking in "+mode+" mode");
                Save((FrameworkElement)settings.Content,Path.Combine(output,"settings-"+mode+".png"));StabilityProbe.Call(settings,"SelectPage","appearance");await Task.Delay(30);
                Save((FrameworkElement)settings.Content,Path.Combine(output,"appearance-"+mode+".png"));settings.Close();
                var panel=new HistoryPanel{Margin=new Thickness(16,0,16,16)};panel.Configure(false,true,30);panel.Apply(data,"演示数据");var chartWindow=new StyledWindow{Width=850,Height=920,ShowActivated=false,ShowInTaskbar=false};chartWindow.SetBody(new ScrollViewer{Content=panel,VerticalScrollBarVisibility=ScrollBarVisibility.Auto},"用量趋势","DEMO",true);chartWindow.Show();await Task.Delay(50);
                Save((FrameworkElement)chartWindow.Content,Path.Combine(output,"charts-"+mode+".png"));panel.Configure(true,false,30);chartWindow.Height=650;await Task.Delay(30);
                Save((FrameworkElement)chartWindow.Content,Path.Combine(output,"heatmap-"+mode+".png"));chartWindow.Close();
                data.Warning="2 个日志尚未完成读取，1 条异常记录已跳过。";data.CoverageWarnings=3;widget.ApplySnapshot(data);
                var toggle=Find(widget,"CoverageToggle") as Button;StabilityProbe.Check(toggle!=null&&toggle.Visibility==Visibility.Visible,"coverage issues have a visible action in "+mode+" mode");
                toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Task.Delay(40);
                var explanation=Application.Current.Windows.Cast<Window>().Select(w=>Find(w,"CoverageExplanation") as TextBlock).FirstOrDefault(x=>x!=null);
                StabilityProbe.Check(explanation!=null&&explanation.Text.Contains("影响")&&explanation.Text.Contains("操作"),"coverage dialog explains impact and next steps in "+mode+" mode");
                if(explanation!=null)Window.GetWindow(explanation).Close();data.Warning="";data.CoverageWarnings=0;
                p.Collapsed=true;widget.Height=305;StabilityProbe.Call(widget,"UpdateButtons");data.Warning="1 条演示记录待检查。";data.CoverageWarnings=1;widget.ApplySnapshot(data);await Task.Delay(40);
                Save((FrameworkElement)widget.Content,Path.Combine(output,"collapsed-"+mode+".png"));p.Collapsed=false;StabilityProbe.Call(widget,"UpdateButtons");data.Warning="";data.CoverageWarnings=0;
                widget.Width=300;widget.Height=500;widget.ApplySnapshot(data);await Task.Delay(40);Save((FrameworkElement)widget.Content,Path.Combine(output,"compact-"+mode+".png"));
                foreach(string form in new[]{"small","large","orb"})
                {
                    p.BallStyle=form=="orb"?"orb":"capsule";p.BallExpanded=form=="large";p.BallDock="";p.BallLeft=400;p.BallTop=200;
                    var ball=new FloatingBall(()=>p,()=>{},()=>{},()=>{}){ShowActivated=false};ball.Apply(data,data.Quotas[0],"演示数据","");ball.Show();await Task.Delay(40);
                    Save((FrameworkElement)ball.Content,Path.Combine(output,form+"-"+mode+".png"));ball.Dispose();
                }
                widget.Close();
            }
            Console.WriteLine("GUIDE IMAGES: "+output);
        }
    }
}
