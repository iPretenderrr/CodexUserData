using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using Forms=System.Windows.Forms;

namespace CodexUserData
{
    internal sealed class TrayFlyout : Window
    {
        private readonly StackPanel body=new StackPanel();
        private readonly TextBlock activityText=Theme.Text("等待任务状态",10,Theme.Muted);
        private bool otherExpanded;
        private string contentKey;
        private bool dismissReversible;
        private int revealRevision;
        internal bool Dismissing {get;private set;}
        internal void Reveal(System.Drawing.Point anchor)
        {
            if(Dismissing&&!dismissReversible)return;
            bool opening=!IsVisible;Dismissing=false;int revision=++revealRevision;
            if(!opening){PositionAt(anchor);WindowInteraction.Reveal(this);return;}
            // Keep the native popup transparent until SizeToContent has produced its real first size.
            // Positioning it again at render priority avoids the initial wrong-monitor/wrong-edge jump.
            Opacity=0;WindowInteraction.PrepareReveal(this);Show();PositionAt(anchor);
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Render,new Action(()=>{if(revision!=revealRevision||!IsVisible)return;UpdateLayout();PositionAt(anchor);Opacity=1;WindowInteraction.Reveal(this);}));
        }
        internal void Dismiss(Action after=null,bool reversible=false)
        {
            revealRevision++;if(!IsVisible){Dismissing=false;if(after!=null)after();return;}Dismissing=true;dismissReversible=reversible;
            WindowInteraction.Hide(this,()=>{Dismissing=false;dismissReversible=false;if(after!=null)after();});
        }
        internal TrayFlyout(Action show,Action charts,Action refresh)
        {
            Title="Codex 额度速览";Width=370;SizeToContent=SizeToContent.Height;MaxHeight=720;WindowStyle=WindowStyle.None;AllowsTransparency=true;Background=Brushes.Transparent;ShowInTaskbar=false;ShowActivated=false;Topmost=true;ResizeMode=ResizeMode.NoResize;UseLayoutRounding=true;
            FontFamily=new FontFamily("Segoe UI, Microsoft YaHei UI");Theme.InstallStyles(this);WindowInteraction.EnableMotion(this,false);
            Closed+=delegate{Dismissing=false;dismissReversible=false;Opacity=1;};
            var shell=new Border{Background=Theme.WindowBackground,BorderBrush=Theme.Frame,BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(14),Padding=new Thickness(16)};Content=shell;
            var layout=new StackPanel();shell.Child=layout;
            var header=new DockPanel{Margin=new Thickness(0,0,0,12)};layout.Children.Add(header);var close=Theme.ToolbarButton("expand","收起额度速览");close.Click+=delegate{Dismiss();};DockPanel.SetDock(close,Dock.Right);header.Children.Add(close);
            var heading=new StackPanel();heading.Children.Add(Theme.Text("CODEX",9,Theme.Muted));var caption=Theme.Text("额度与今日用量",18,Theme.Ink);caption.FontWeight=FontWeights.SemiBold;caption.Margin=new Thickness(0,3,0,0);heading.Children.Add(caption);header.Children.Add(heading);
            activityText.Margin=new Thickness(0,0,0,10);layout.Children.Add(activityText);
            var scroll=new ScrollViewer{Content=body,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,MaxHeight=555};layout.Children.Add(scroll);
            var actions=new UniformGrid{Columns=3,Margin=new Thickness(-3,12,-3,0)};layout.Children.Add(actions);
            Action<string,Action> button=(label,action)=>{var b=Theme.Button(label,label,45);b.Margin=new Thickness(3,0,3,0);b.Background=Theme.Surface;b.Click+=delegate{if(action!=null)action();};actions.Children.Add(b);};
            button("打开悬浮窗",()=>Dismiss(show));button("用量图表",()=>Dismiss(charts));button("刷新额度",refresh);
        }
        internal void Apply(IEnumerable<QuotaBucket> quotas,UsageSnapshot snapshot,string scope,string status)
        {
            long now=LocalCodexUsage.Unix(DateTime.Now);var all=(quotas??new QuotaBucket[0]).OrderBy(q=>q.Id).ToArray();var general=all.FirstOrDefault(q=>q.Id=="codex");
            DailyUsage today=snapshot==null?null:snapshot.Daily.LastOrDefault(d=>d.Date==DateTime.Today.ToString("yyyy-MM-dd"));
            Func<QuotaWindow,string> windowKey=w=>w==null?"":w.Label+":"+w.Minutes+":"+w.UsedPercent+":"+w.ResetsAt;
            string nextKey=(scope??"")+"/"+(status??"")+"/"+(snapshot==null?"":snapshot.Warning)+"/"+String.Join("|",all.Select(q=>q.Id+":"+q.ObservedAt+":"+windowKey(q.Primary)+":"+windowKey(q.Secondary)))+"/"+(today==null?"none":today.Date+":"+today.Tokens+":"+today.Input+":"+today.CacheRead+":"+today.CacheWrite+":"+today.Output+":"+String.Join(",",today.Models.Select(m=>m.Model+":"+m.Effort+":"+m.Tokens+":"+m.EquivalentUsd+":"+m.UnpricedTokens)));
            // Quota and usage clocks often deliver identical snapshots; keep the existing visual tree
            // to avoid repeated layout, allocation and a visible twitch while the flyout is open.
            if(nextKey==contentKey)return;contentKey=nextKey;body.Children.Clear();
            var windows=general==null?new QuotaWindow[0]:new[]{general.Primary,general.Secondary}.Where(w=>w!=null).OrderBy(w=>w.Minutes).ToArray();
            var balances=new UniformGrid{Columns=Math.Max(1,windows.Length),Margin=new Thickness(-3,0,-3,0)};body.Children.Add(balances);
            if(windows.Length==0)balances.Children.Add(QuotaCard(null,null,now));else foreach(var window in windows)balances.Children.Add(QuotaCard(general,window,now));
            var additional=all.Where(b=>b!=general).OrderBy(b=>b.Id).ToArray();
            if(additional.Length>0)
            {
                var extra=new StackPanel();var expander=new Expander{Header="其他额度 · "+additional.Length,Foreground=Theme.Muted,FontSize=11,IsExpanded=otherExpanded,Margin=new Thickness(0,9,0,0),Content=extra};expander.Expanded+=delegate{otherExpanded=true;};expander.Collapsed+=delegate{otherExpanded=false;};body.Children.Add(expander);
                foreach(var bucket in additional)
                {
                    foreach(var window in new[]{bucket.Primary,bucket.Secondary}.Where(w=>w!=null))
                    {
                        var line=new DockPanel{Margin=new Thickness(3,6,3,0)};double? remaining=Remaining(bucket,window,now);
                        var value=Theme.Text(remaining.HasValue?remaining.Value.ToString("0.#")+"%":"待更新",11,remaining.HasValue?Theme.Ink:Theme.Muted);DockPanel.SetDock(value,Dock.Right);line.Children.Add(value);
                        var label=Theme.Text((bucket.Name??bucket.Id)+" · "+window.Label,10,Theme.Muted);label.TextTrimming=TextTrimming.CharacterEllipsis;line.Children.Add(label);line.ToolTip=Reset(window);extra.Children.Add(line);
                    }
                }
            }
            var updated=Theme.Text(status,10,Theme.Muted);updated.TextWrapping=TextWrapping.Wrap;updated.Margin=new Thickness(2,10,2,13);AutomationProperties.SetAutomationId(updated,"TrayQuotaUpdated");body.Children.Add(updated);
            var title=Theme.Text((String.IsNullOrEmpty(scope)?"用量":scope)+" · 今日",11,Theme.Ink);title.Margin=new Thickness(1,0,0,8);body.Children.Add(title);
            if(today==null){body.Children.Add(Theme.Text("当前来源的统计尚未准备好",11,Theme.Muted));return;}
            var totals=new UniformGrid{Columns=2,Margin=new Thickness(-3,0,-3,0)};body.Children.Add(totals);
            Stat(totals,"TOKENS",TokenText.Compact(today.Tokens),"TrayTodayTokens");Stat(totals,"API 等效 · USD",ModelColors.Money(today.Models.Sum(m=>m.EquivalentUsd),today.Models.Sum(m=>m.UnpricedTokens),today.Tokens),"TrayTodayValue");
            var parts=new UniformGrid{Columns=3,Margin=new Thickness(0,10,0,10)};body.Children.Add(parts);
            Small(parts,"未缓存",today.Input);Small(parts,"缓存",today.CacheRead+today.CacheWrite);Small(parts,"输出",today.Output);
            foreach(var model in today.Models.GroupBy(m=>m.Model).OrderByDescending(g=>g.Sum(m=>m.Tokens)).Take(3))
            {
                var line=new DockPanel{Margin=new Thickness(1,4,1,0)};var amount=Theme.Text(TokenText.Compact(model.Sum(m=>m.Tokens)),11,Theme.Ink);DockPanel.SetDock(amount,Dock.Right);line.Children.Add(amount);line.Children.Add(Theme.Text("● "+model.Key,11,ModelColors.For(model.Key)));body.Children.Add(line);
            }
            if(!String.IsNullOrEmpty(snapshot.Warning)){var warning=Theme.Text("部分记录待核对",10,Theme.Warning);warning.ToolTip=snapshot.Warning;warning.Margin=new Thickness(1,9,0,0);body.Children.Add(warning);}
        }
        internal void ApplyActivity(ActivityReport report,bool completed)
        {
            string text=completed?"✓  任务已完成":report==null?"等待任务状态":report.ActiveTasks>0?"●  正在运行 · "+report.ActiveTasks+" 项任务":report.UncertainTasks>0?"○  任务状态待确认":"○  当前空闲";
            if(activityText.Text==text)return;activityText.Text=text;activityText.Foreground=completed?Theme.B(Theme.IsLight?"#237D69":"#82D6BE"):report!=null&&report.UncertainTasks>0?Theme.Warning:Theme.Muted;
        }
        internal static double? Remaining(QuotaBucket bucket,QuotaWindow window,long now)
        {
            return window==null?null:window.RemainingPercent(bucket,now);
        }
        private static Border QuotaCard(QuotaBucket bucket,QuotaWindow window,long now)
        {
            double? remaining=Remaining(bucket,window,now);bool week=window!=null&&window.Minutes>=1440;
            var gradient=new LinearGradientBrush((Color)ColorConverter.ConvertFromString(week?"#548CD1":"#438FEB"),(Color)ColorConverter.ConvertFromString(week?"#7DC9B9":"#75D1DE"),0);gradient.Freeze();
            var panel=new StackPanel();panel.Children.Add(Theme.Text(window==null?"账号额度":window.Label+"窗口",11,Theme.Muted));
            var number=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(0,6,0,0)};panel.Children.Add(number);
            var amount=Theme.Text(remaining.HasValue?remaining.Value.ToString("0",CultureInfo.InvariantCulture)+"%":"—",32,Theme.Ink);amount.FontWeight=FontWeights.SemiBold;AutomationProperties.SetAutomationId(amount,"TrayQuotaRemaining");number.Children.Add(amount);
            var suffix=Theme.Text(remaining.HasValue?"剩余":"待更新",10,Theme.Muted);suffix.Margin=new Thickness(7,10,0,0);number.Children.Add(suffix);
            var track=new Grid{Height=6,Margin=new Thickness(0,9,0,10)};panel.Children.Add(track);track.Children.Add(new Border{Background=Theme.Line,CornerRadius=new CornerRadius(3)});
            var fill=new Grid();double fraction=(remaining??0)/100;fill.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(fraction,GridUnitType.Star)});fill.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1-fraction,GridUnitType.Star)});fill.Children.Add(new Border{Background=remaining<15?(Brush)Theme.Warning:gradient,CornerRadius=new CornerRadius(3)});track.Children.Add(fill);
            var reset=Theme.Text(window==null?"等待在线查询":Reset(window),9,Theme.Muted);reset.TextTrimming=TextTrimming.CharacterEllipsis;panel.Children.Add(reset);
            return new Border{Background=Theme.Surface,BorderBrush=Theme.Line,BorderThickness=new Thickness(.7),CornerRadius=new CornerRadius(13),Padding=new Thickness(12),Margin=new Thickness(3),Child=panel};
        }
        private static string Reset(QuotaWindow window)
        {
            return window.ResetsAt<=0?"重置时间未提供":"重置 "+new DateTime(1970,1,1,0,0,0,DateTimeKind.Utc).AddSeconds(window.ResetsAt).ToLocalTime().ToString("MM-dd HH:mm");
        }
        private static void Stat(Panel parent,string label,string value,string id)
        {
            var stack=new StackPanel();stack.Children.Add(Theme.Text(label,9,Theme.Muted));var text=Theme.Text(value,19,Theme.Ink);text.TextTrimming=TextTrimming.CharacterEllipsis;text.ToolTip=value;text.FontWeight=FontWeights.SemiBold;text.Margin=new Thickness(0,5,0,0);AutomationProperties.SetAutomationId(text,id);stack.Children.Add(text);
            parent.Children.Add(new Border{Background=Theme.Surface,CornerRadius=new CornerRadius(9),Padding=new Thickness(11),Margin=new Thickness(3),Child=stack});
        }
        private static void Small(Panel parent,string label,long value){var stack=new StackPanel();stack.Children.Add(Theme.Text(label,9,Theme.Muted));var text=Theme.Text(TokenText.Compact(value),12,Theme.Ink);text.Margin=new Thickness(0,4,0,0);stack.Children.Add(text);parent.Children.Add(stack);}
        [StructLayout(LayoutKind.Sequential)] internal struct NativeRect{public int Left,Top,Right,Bottom;}
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h,out NativeRect rect);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h,IntPtr after,int x,int y,int width,int height,uint flags);
        internal bool Contains(System.Drawing.Point point){NativeRect rect;return IsVisible&&GetWindowRect(new WindowInteropHelper(this).Handle,out rect)&&point.X>=rect.Left&&point.X<rect.Right&&point.Y>=rect.Top&&point.Y<rect.Bottom;}
        internal void PositionAt(System.Drawing.Point anchor)
        {
            // Place in physical pixels after layout. This avoids mixing logical coordinates between monitors with different DPI.
            UpdateLayout();var handle=new WindowInteropHelper(this).Handle;NativeRect rect;if(!GetWindowRect(handle,out rect))return;
            var work=Forms.Screen.FromPoint(anchor).WorkingArea;int width=rect.Right-rect.Left,height=rect.Bottom-rect.Top;
            int x=Math.Max(work.Left+8,Math.Min(work.Right-width-8,anchor.X-width+28)),y=Math.Max(work.Top+8,Math.Min(work.Bottom-height-8,anchor.Y-height-12));SetWindowPos(handle,IntPtr.Zero,x,y,0,0,0x15);
        }
    }
}
