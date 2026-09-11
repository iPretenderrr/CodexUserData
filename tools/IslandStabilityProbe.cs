using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CodexUserData
{
    // Exercise the real native host with generated data; never connect to the user's CLI.
    internal static class IslandStabilityProbe
    {
        private static bool Flag(object target,string name)
        {return (bool)target.GetType().GetProperty(name,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic).GetValue(target,null);}
        private static ActivityReport State(int tasks)
        {long now=LocalCodexUsage.Unix(DateTime.Now);return new ActivityReport{ActiveTasks=tasks,ObservedAt=now,Until=tasks>0?now+60:0};}
        private static FrameworkElement Island(FloatingBall ball)
        {return GuideStabilityProbe.Find(ball,"DynamicIsland");}
        private static string Summary(FloatingBall ball)
        {return AutomationProperties.GetName(Island(ball));}
        private static void Click(FloatingBall ball,int count)
        {
            // Routed input exercises the production handlers without moving the user's cursor.
            foreach(var routed in new[]{UIElement.PreviewMouseLeftButtonDownEvent,UIElement.PreviewMouseLeftButtonUpEvent})
            {
                var args=new MouseButtonEventArgs(Mouse.PrimaryDevice,Environment.TickCount,MouseButton.Left){RoutedEvent=routed,Source=ball};
                typeof(MouseButtonEventArgs).GetProperty("ClickCount").SetValue(args,count,null);ball.RaiseEvent(args);
            }
        }
        private static async Task Settled(FloatingBall ball,int delay=650)
        {await Task.Delay(delay);ball.UpdateLayout();}
        private static void Save(FloatingBall ball,string path)
        {
            var content=(FrameworkElement)ball.Content;content.UpdateLayout();
            int width=(int)Math.Ceiling(content.ActualWidth*2),height=(int)Math.Ceiling(content.ActualHeight*2);
            var bitmap=new RenderTargetBitmap(width,height,192,192,PixelFormats.Pbgra32);bitmap.Render(content);
            var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using(var file=File.Create(path))encoder.Save(file);
        }
        private static bool ClearCorners(FloatingBall ball)
        {
            var content=(FrameworkElement)ball.Content;content.UpdateLayout();
            int width=(int)Math.Ceiling(ball.ActualWidth),height=(int)Math.Ceiling(ball.ActualHeight);
            var drawing=new DrawingVisual();using(var dc=drawing.RenderOpen())
            {dc.DrawRectangle(ball.Background,null,new Rect(0,0,width,height));dc.DrawRectangle(new VisualBrush(content),null,new Rect(0,0,width,height));}
            var bitmap=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);bitmap.Render(drawing);
            foreach(var point in new[]{new[]{0,0},new[]{width-1,0},new[]{0,height-1},new[]{width-1,height-1}})
            {var pixel=new byte[4];bitmap.CopyPixels(new Int32Rect(point[0],point[1],1,1),pixel,4,0);if(pixel[3]!=0)return false;}
            return true;
        }
        internal static async Task Run(string root)
        {
            string output=Path.Combine(Path.GetDirectoryName(root),"island-images");Directory.CreateDirectory(output);
            var data=GuideStabilityProbe.Snapshot();
            StabilityProbe.Check(new Preferences().IslandMaterial=="glass","new island settings default to the glass capsule");
            var p=new Preferences{Source="local",CodexHome=Path.Combine(root,"codex"),Database=Path.Combine(root,"missing.db"),LiveQuota=false,QuotaCli="",BallStyle="island",IslandMaterial="classic",BallDock="",BallLeft=350,BallTop=220,OrbAnimation="smooth",ThemeMode="dark",BallOpacity=.8};
            p.Validate();StabilityProbe.Check(p.BallStyle=="island","island preference survives validation instead of falling back to the ring");
            var cloned=Program.Json.Deserialize<Preferences>(Program.Json.Serialize(p));cloned.Validate();
            StabilityProbe.Check(cloned.BallStyle=="island","island preference survives settings serialization");
            p.IslandFollowTheme=false;p.IslandColors=new[]{"#3976FF","#35C8E2","#87F3BE","#F4D78D","#FF83A4"};
            var colorsCopy=p.Clone();colorsCopy.IslandColors[0]="#123456";
            StabilityProbe.Check(!colorsCopy.IslandFollowTheme&&colorsCopy.IslandColors.Length==5&&p.IslandColors[0]=="#3976FF"&&p.OrbShortColors[0]!="#123456","island custom colors persist independently of ring colors and settings drafts");
            p.IslandFollowTheme=true;
            Theme.Apply(p);int saves=0,restores=0;
            var ball=new FloatingBall(()=>p,()=>saves++,()=>restores++,()=>{}){ShowActivated=false};
            try
            {
                ball.Apply(data,data.Quotas[0],"演示数据","");ball.ApplyActivity(State(0));ball.Show();await Settled(ball);
                var original=Island(ball);double idleWidth=ball.Width,idleHeight=ball.Height,left=ball.Left,top=ball.Top;
                StabilityProbe.Check(original!=null&&Flag(ball,"IsIsland"),"island opens as its own native form");
                StabilityProbe.Check(idleWidth<=200&&idleHeight<=52,"idle island stays compact");
                StabilityProbe.Check(!original.HasAnimatedProperties,"idle island does not keep decorative frame clocks running");
                var shell=StabilityProbe.Field<Border>(ball,"shell");
                StabilityProbe.Check(shell.ToolTip==null&&!ToolTipService.GetIsEnabled(shell),"island hover has no tooltip");
                StabilityProbe.Check(shell.ContextMenu.Items.OfType<MenuItem>().Any(i=>Convert.ToString(i.Header).Contains("返回完整窗口")),"island keeps the host-owned route back to the main UI");
                Save(ball,Path.Combine(output,"island-idle-dark.png"));
                Click(ball,1);await Task.Delay(40);Click(ball,2);
                await Settled(ball,System.Windows.Forms.SystemInformation.DoubleClickTime+150);
                StabilityProbe.Check(restores==1&&!Flag(ball,"IslandExpanded"),"a double click returns once and rolls back the first tap preference");
                StabilityProbe.Call(ball,"SetIslandExpanded",false);await Settled(ball);
                Click(ball,1);await Settled(ball,System.Windows.Forms.SystemInformation.DoubleClickTime+650);
                StabilityProbe.Check(Flag(ball,"IslandExpanded"),"a single routed click expands the island");
                StabilityProbe.Call(ball,"SetIslandExpanded",false);await Settled(ball);
                Click(ball,1);ball.Hide();await Task.Delay(System.Windows.Forms.SystemInformation.DoubleClickTime+80);ball.Show();await Settled(ball,100);
                StabilityProbe.Check(Flag(ball,"IslandExpanded"),"hiding and restoring cannot dispatch an extra delayed click");
                StabilityProbe.Call(ball,"SetIslandExpanded",false);await Settled(ball);
                int savesBeforeActivity=saves;
                ball.ApplyActivity(State(2));await Settled(ball);
                StabilityProbe.Check(saves==savesBeforeActivity,"automatic activity expansion does not flush settings to disk");
                StabilityProbe.Check(ball.Width>idleWidth&&ball.Height<=60,"confirmed activity stretches the island without opening details");
                StabilityProbe.Check(Math.Abs(ball.Left-left)<2&&Math.Abs(ball.Top-top)<2,"activity expansion preserves the user's window position");
                StabilityProbe.Check(Object.ReferenceEquals(original,Island(ball)),"activity changes retain the same island visual instance");
                var textLayout=StabilityProbe.Field<FormattedText>(original,"value");
                for(int i=0;i<20;i++)ball.Apply(data,data.Quotas[0],"演示数据","");
                StabilityProbe.Check(Object.ReferenceEquals(textLayout,StabilityProbe.Field<FormattedText>(original,"value")),"unchanged island snapshots reuse text layouts");
                await Settled(ball,100);
                StabilityProbe.Check(Object.ReferenceEquals(original,Island(ball))&&Math.Abs(ball.Opacity-.8)<.01,"repeated snapshots preserve the island and configured opacity");
                Save(ball,Path.Combine(output,"island-running-dark.png"));
                if(Theme.MotionAllowed)
                {
                    await Settled(ball,180);Save(ball,Path.Combine(output,"island-running-frame2.png"));
                    StabilityProbe.Check(!File.ReadAllBytes(Path.Combine(output,"island-running-dark.png")).SequenceEqual(File.ReadAllBytes(Path.Combine(output,"island-running-frame2.png"))),"running island produces visibly different rendered animation frames");
                }
                StabilityProbe.Call(ball,"SetIslandExpanded",true);await Settled(ball);
                StabilityProbe.Check(Flag(ball,"IslandExpanded")&&ball.Width==280&&ball.Height==76,"classic island uses a compact two-quota layout instead of a tall details panel");
                StabilityProbe.Check(Object.ReferenceEquals(original,Island(ball)),"expanding details keeps the same visual and window");
                Save(ball,Path.Combine(output,"island-expanded-dark.png"));
                StabilityProbe.Call(ball,"SetIslandExpanded",false);StabilityProbe.Call(ball,"SetIslandExpanded",true);StabilityProbe.Call(ball,"SetIslandExpanded",false);await Settled(ball);
                StabilityProbe.Check(!Flag(ball,"IslandExpanded")&&ball.Height<80,"latest rapid island expansion request wins");
                if(Theme.MotionAllowed)
                {
                    StabilityProbe.Call(ball,"SetIslandExpanded",true);await Settled(ball,100);double during=((DynamicIsland)Island(ball)).RenderHeight;
                    StabilityProbe.Call(ball,"SetIslandExpanded",false);
                    StabilityProbe.Check(Math.Abs(((DynamicIsland)Island(ball)).RenderHeight-during)<3,"reversing an island resize starts at the displayed height without a jump");
                    await Settled(ball);
                    StabilityProbe.Check(Math.Abs(ball.Height-48)<1,"an interrupted island expansion still reaches the requested compact target");
                }
                ball.ApplyActivity(State(0));await Settled(ball);
                StabilityProbe.Check(Math.Abs(ball.Width-idleWidth)<1,"idle activity returns an unexpanded island to its compact size");
                ball.SetCompletionPending(true);await Settled(ball,100);
                StabilityProbe.Check(Summary(ball).Contains("完成"),"island exposes a pending completion through the shared state bridge");
                ball.SetOrb();StabilityProbe.Call(ball,"SetIsland");await Settled(ball);
                StabilityProbe.Check(Summary(ball).Contains("完成"),"changing away and back retains unacknowledged island completion");
                ball.SetCompletionPending(false);ball.ApplyActivity(State(1));await Settled(ball);
                StabilityProbe.Check(!Summary(ball).Contains("完成")&&Summary(ball).Contains("运行"),"a new task replaces the acknowledged completion presentation");
                var runningVisual=Island(ball);ball.Hide();await Task.Delay(50);
                StabilityProbe.Check(!runningVisual.HasAnimatedProperties,"hidden island stops decorative animation clocks");
                ball.Show();await Settled(ball,100);
                StabilityProbe.Check(!Theme.MotionAllowed||runningVisual.HasAnimatedProperties,"visible running island resumes animation under the selected policy");
                StabilityProbe.Call(runningVisual,"Stop");await Settled(ball,1150);
                StabilityProbe.Check(!runningVisual.HasAnimatedProperties,"telemetry aging cannot restart a transition-suspended island");
                StabilityProbe.Call(runningVisual,"Start",60);
                var stale=State(1);stale.ObservedAt-=30;ball.ApplyActivity(stale);await Settled(ball);
                StabilityProbe.Check(!Island(ball).HasAnimatedProperties&&!Summary(ball).Contains("正在运行"),"expired activity does not keep an island animation alive");
                ball.ApplyActivity(State(0));await Settled(ball);
                var work=SystemParameters.WorkArea;
                for(int edge=0;edge<4;edge++)
                {
                    ball.Left=edge==0?work.Left+1:edge==1?work.Right-ball.Width-1:work.Left+work.Width/2;
                    ball.Top=edge==2?work.Top+1:edge==3?work.Bottom-ball.Height-1:work.Top+work.Height/2;
                    ball.SnapToEdge();await Settled(ball,950);
                    StabilityProbe.Check(ball.IsPillar&&p.BallStyle=="island","island docks on edge "+edge+" without losing its selected form");
                    ball.Left=work.Left+work.Width/2;ball.Top=work.Top+work.Height/2;ball.SnapToEdge();await Settled(ball,950);
                    StabilityProbe.Check(!ball.IsPillar&&Flag(ball,"IsIsland")&&!Flag(ball,"IslandExpanded"),"edge "+edge+" restores the compact island");
                }
                ball.SetExpanded(true);ball.SetOrb();StabilityProbe.Call(ball,"SetIsland");await Settled(ball,1100);
                StabilityProbe.Check(Flag(ball,"IsIsland")&&!ball.IsOrb&&!ball.Expanded,"rapid cross-form requests settle on the island");
                foreach(string mode in new[]{"dark","light"})
                {
                    p.ThemeMode=mode;p.OrbAnimation="off";Theme.Apply(p);ball.Apply(data,data.Quotas[0],"演示数据","");ball.ApplyActivity(State(1));
                    StabilityProbe.Call(ball,"SetIslandExpanded",true);await Settled(ball,100);
                    StabilityProbe.Check(ClearCorners(ball),mode+" island leaves its outside corners fully transparent");
                    Save(ball,Path.Combine(output,"island-expanded-"+mode+".png"));
                }
                var single=new QuotaBucket{Id="fixture",Name="Codex",Origin="在线查询",ObservedAt=LocalCodexUsage.Unix(DateTime.Now),Secondary=data.Quotas[0].Secondary};
                ball.Apply(data,single,"演示数据","");await Settled(ball,50);
                await Settled(ball);
                StabilityProbe.Check(ball.Width==228&&ball.Height==76,"classic single-quota layout removes the unused column and footer height");
                StabilityProbe.Check(!Summary(ball).Contains("5h")&&!Summary(ball).Contains("5小时")&&Summary(ball).Contains("57%"),"single-period island reports the actual quota without inventing a short window");
                Save(ball,Path.Combine(output,"island-single-light.png"));
                StabilityProbe.Call(ball,"SetIslandExpanded",false);ball.Apply(data,null,"演示数据","");ball.ApplyActivity(new ActivityReport{MonitoringUnavailable=true,ObservedAt=LocalCodexUsage.Unix(DateTime.Now)});await Settled(ball,100);
                StabilityProbe.Check(!Summary(ball).Contains("%")&&(Summary(ball).Contains("未知")||Summary(ball).Contains("待确认")),"missing quota and unreadable activity stay unknown instead of showing numeric quota or idle");
                Save(ball,Path.Combine(output,"island-unknown-light.png"));
                p.IslandMaterial="glass";p.OrbAnimation="smooth";p.ThemeMode="dark";Theme.Apply(p);ball.Apply(data,data.Quotas[0],"演示数据","");ball.ApplyActivity(State(0));await Settled(ball);
                StabilityProbe.Check(ball.Height==56&&ball.Width==224,"glass idle capsule keeps its intended readable proportions");
                Save(ball,Path.Combine(output,"glass-idle-dark.png"));
                var compactRings=StabilityProbe.Field<TranslateTransform[]>(Island(ball),"ringPositions");
                StabilityProbe.Check(Math.Abs(compactRings[1].Y-ball.Height/2)<.1,"compact glass quota ring is vertically centred in the capsule");
                double glassHeight=ball.Height;ball.ApplyActivity(State(2));await Settled(ball);
                StabilityProbe.Check(ball.Height==glassHeight&&ball.Width==252,"starting a task widens glass without changing its height");
                Save(ball,Path.Combine(output,"glass-running-dark.png"));
                int nativeResizes=0;SizeChangedEventHandler countSizes=(s,e)=>nativeResizes++;ball.SizeChanged+=countSizes;
                Click(ball,1);
                StabilityProbe.Check(Flag(ball,"IslandExpanded"),"glass responds to a click immediately without the system double-click delay");
                double envelope=ball.Width;await Settled(ball,60);double early=((DynamicIsland)Island(ball)).RenderWidth;
                Save(ball,Path.Combine(output,"glass-morph-early.png"));
                StabilityProbe.Check(!DependencyPropertyHelper.GetValueSource(ball,Window.WidthProperty).IsAnimated,"glass expansion never animates native window width");
                await Settled(ball,65);double later=((DynamicIsland)Island(ball)).RenderWidth;
                Save(ball,Path.Combine(output,"glass-morph-late.png"));
                StabilityProbe.Check(ball.Width==envelope&&later>early,"glass expands its painted surface inside stable native bounds");
                await Settled(ball);ball.SizeChanged-=countSizes;
                StabilityProbe.Check(nativeResizes<=3&&ball.Height==72&&ball.Width==312,"glass expansion changes native size only at its boundaries");
                Save(ball,Path.Combine(output,"glass-expanded-dark.png"));
                p.ThemeMode="light";Theme.Apply(p);ball.Apply(data,data.Quotas[0],"演示数据","");await Settled(ball,80);
                StabilityProbe.Check(ClearCorners(ball),"glass reflections leave outside corners fully transparent");
                Save(ball,Path.Combine(output,"glass-expanded-light.png"));
                ball.Apply(data,single,"演示数据","");await Settled(ball);Save(ball,Path.Combine(output,"glass-single-light.png"));
                StabilityProbe.Check(ball.Width==252,"single-quota glass removes unused width instead of keeping an empty second column");
                StabilityProbe.Check(Summary(ball).Contains("57%")&&!Summary(ball).Contains("5小时"),"glass single-quota layout never adds a fictional second ring");
                ball.Hide();StabilityProbe.Call(ball,"SetIslandExpanded",true);ball.Show();await Settled(ball,150);
                StabilityProbe.Check(Flag(ball,"IsIsland")&&ball.IsVisible,"hidden island changes restore to a visible valid form");
            }
            finally{ball.Dispose();}
            StabilityProbe.Call(ball,"SetIsland");StabilityProbe.Call(ball,"SetIslandExpanded",true);await Task.Delay(80);
            StabilityProbe.Check(!ball.IsVisible,"late island requests after disposal cannot reopen the window");
            Console.WriteLine("ISLAND IMAGES: "+output);
        }
    }
}
