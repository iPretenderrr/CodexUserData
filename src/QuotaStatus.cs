using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Drawing=System.Drawing;
using Forms=System.Windows.Forms;

namespace CodexUserData
{
    // Account reads have their own slow clock; changing the statistics interval never launches extra helpers.
    internal sealed class QuotaStatus : StackPanel, IDisposable
    {
        private readonly Func<Preferences> preferences;
        private readonly TextBlock title,note,left,right;
        private readonly Border barLeft,barRight;
        private readonly DispatcherTimer clock=new DispatcherTimer{Interval=TimeSpan.FromSeconds(60)};
        private readonly Dictionary<string,QuotaBucket> latest=new Dictionary<string,QuotaBucket>();
        private Forms.NotifyIcon tray;
        private TrayFlyout flyout;
        private UsageSnapshot usage;
        private string usageScope;
        private readonly Action showWindow,showCharts;
        private readonly DispatcherTimer hoverTimer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(100)};
        private Drawing.Point anchor;
        private DateTime hoverStarted,lastInside;
        private bool busy,disposed;
        private readonly bool preview;
        private string failure="",iconKey="";
        private string balanceIcon="?";
        private ActivityReport activity;
        private bool completionPending;
        internal event Action CompletionChanged;
        internal bool CompletionPending {get{return CompletionVisible;}}
        private int completionStep;
        private readonly DispatcherTimer completionTimer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(800)};
        internal event Action Changed;
        internal QuotaBucket MainBucket {get{QuotaBucket bucket;return latest.TryGetValue("codex",out bucket)?bucket:null;}}
        internal string StatusText {get{return note.Text;}}
        [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
        internal QuotaStatus(Func<Preferences> getPreferences,Action toggle,Action exit,bool preview,Action show=null,Action charts=null)
        {
            preferences=getPreferences;this.preview=preview;showWindow=show??toggle;showCharts=charts??showWindow;Margin=new Thickness(0,0,0,9);
            var heading=new DockPanel();Children.Add(heading);
            title=Theme.Text("CODEX · 剩余额度",10,Theme.Muted);heading.Children.Add(title);
            var row=new Grid{Margin=new Thickness(0,5,0,0)};row.ColumnDefinitions.Add(new ColumnDefinition());row.ColumnDefinitions.Add(new ColumnDefinition());Children.Add(row);
            left=Theme.Text("等待额度",11,Theme.Ink);right=Theme.Text("",11,Theme.Ink);
            barLeft=Bar(row,0,left);barRight=Bar(row,1,right);
            note=Theme.Text("",9,Theme.Muted);note.TextTrimming=TextTrimming.CharacterEllipsis;note.Margin=new Thickness(0,4,0,0);Children.Add(note);
            System.Windows.Automation.AutomationProperties.SetAutomationId(title,"QuotaTitle");System.Windows.Automation.AutomationProperties.SetAutomationId(left,"QuotaPrimary");System.Windows.Automation.AutomationProperties.SetAutomationId(note,"QuotaTimestamp");
            System.Windows.Automation.AutomationProperties.SetAutomationId(this,"CodexQuota");
            ToolTip="后台查询 Codex 账号额度，不需要打开 Codex 窗口。";
            if(!preview)
            {
                // Explorer can retain the first tooltip as the icon's accessible title, so keep it timeless.
                tray=new Forms.NotifyIcon{Visible=true,Text="CodexUserData · 用量悬浮窗",Icon=Drawing.SystemIcons.Information};
                var menu=new Forms.ContextMenuStrip();menu.Items.Add("额度与今日用量",null,delegate{Dispatcher.BeginInvoke(new Action(()=>ShowFlyout()));});menu.Items.Add("显示 / 隐藏悬浮窗",null,delegate{Dispatcher.BeginInvoke(toggle);});
                menu.Items.Add("立即刷新额度",null,delegate{Dispatcher.BeginInvoke(new Action(Refresh));});
                menu.Items.Add("退出",null,delegate{Dispatcher.BeginInvoke(exit);});tray.ContextMenuStrip=menu;
                tray.DoubleClick+=delegate{Dispatcher.BeginInvoke(new Action(()=>{if(flyout!=null)flyout.Hide();hoverTimer.Stop();toggle();}));};
                tray.MouseMove+=delegate{AcknowledgeCompletion();if(!hoverTimer.IsEnabled){anchor=Forms.Cursor.Position;hoverStarted=lastInside=DateTime.UtcNow;hoverTimer.Start();}};
                tray.MouseClick+=delegate(object sender,Forms.MouseEventArgs e){if(e.Button==Forms.MouseButtons.Left&&e.Clicks==1)Dispatcher.BeginInvoke(new Action(()=>ShowFlyout()));};
            }
            completionTimer.Tick+=delegate{completionStep++;if(!CompletionVisible)completionTimer.Stop();UpdateTrayIcon();UpdateActivityText();};
            hoverTimer.Tick+=HoverTick;clock.Tick+=delegate{Render();Refresh();};Render();
        }
        private static Border Bar(Grid row,int column,TextBlock label)
        {
            var stack=new StackPanel{Margin=new Thickness(column==0?0:8,0,column==0?8:0,0)};Grid.SetColumn(stack,column);row.Children.Add(stack);stack.Children.Add(label);
            var track=new Grid{Height=3,Background=Theme.Line,Margin=new Thickness(0,5,0,0)};stack.Children.Add(track);
            var fill=new Border{Background=Theme.Accent,HorizontalAlignment=HorizontalAlignment.Left};track.Children.Add(fill);
            track.SizeChanged+=delegate{double fraction=fill.Tag is double?(double)fill.Tag:0;fill.Width=Math.Max(0,track.ActualWidth*fraction);};return fill;
        }
        internal void Start(){clock.Start();Refresh();}
        internal void Configure(bool newAccount)
        {
            if(newAccount){latest.Clear();SetCompletion(false);}Render();Refresh();
        }
        internal void Accept(IEnumerable<QuotaBucket> buckets)
        {
            foreach(var b in buckets??new QuotaBucket[0]){QuotaBucket old;if(!latest.TryGetValue(b.Id,out old)||b.ObservedAt>=old.ObservedAt)latest[b.Id]=b;}Render();
        }
        internal void AcceptUsage(UsageSnapshot snapshot,string scope){usage=snapshot;usageScope=scope;if(flyout!=null&&flyout.IsVisible)UpdateFlyout();}
        private void UpdateFlyout(){flyout.Apply(latest.Values,usage,usageScope,note.Text);UpdateActivityText();if(flyout.IsVisible)flyout.PositionAt(anchor);}
        private bool CompletionVisible {get{return preferences().CompletionFlash&&completionPending;}}
        internal void ApplyActivity(ActivityReport report)
        {
            activity=report;bool next=preferences().CompletionFlash&&report!=null&&report.ActiveTasks==0&&(completionPending||report.CompletedTasks>0);SetCompletion(next);
            // The activity poll only updates a label and a cached icon, never rebuilds the flyout.
            UpdateTrayIcon();UpdateActivityText();
        }
        internal void AcknowledgeCompletion(){SetCompletion(false);}
        private void SetCompletion(bool pending)
        {
            if(pending==completionPending)return;completionPending=pending;completionStep=0;
            if(pending)completionTimer.Start();else completionTimer.Stop();UpdateTrayIcon();UpdateActivityText();if(CompletionChanged!=null)CompletionChanged();
        }
        private void UpdateActivityText(){if(flyout!=null)flyout.ApplyActivity(activity,CompletionVisible);}
        private void UpdateTrayIcon()
        {
            if(tray==null||disposed)return;
            bool check=CompletionVisible&&(!SystemParameters.ClientAreaAnimation||completionStep%2==0);
            SetIcon(check?"✓":balanceIcon);
        }
        private void ShowFlyout()
        {
            if(preview||disposed)return;
            if(flyout==null){flyout=new TrayFlyout(showWindow,showCharts,Refresh);flyout.PreviewMouseMove+=delegate{AcknowledgeCompletion();};flyout.IsVisibleChanged+=delegate{if(tray!=null)tray.Text=flyout.IsVisible?"":"Codex 用量速览";};}
            if(!hoverTimer.IsEnabled)anchor=Forms.Cursor.Position;lastInside=DateTime.UtcNow;UpdateFlyout();flyout.Reveal(anchor);hoverTimer.Start();
        }
        private void HoverTick(object sender,EventArgs args)
        {
            if(disposed){hoverTimer.Stop();return;}
            var point=Forms.Cursor.Position;bool near=Math.Abs(point.X-anchor.X)<=20&&Math.Abs(point.Y-anchor.Y)<=20;
            if(flyout==null||!flyout.IsVisible){if(!near){hoverTimer.Stop();return;}if((DateTime.UtcNow-hoverStarted).TotalMilliseconds>=250)ShowFlyout();return;}
            if(near||flyout.Contains(point)){lastInside=DateTime.UtcNow;if(flyout.Dismissing)flyout.Reveal(anchor);}
            else if((DateTime.UtcNow-lastInside).TotalMilliseconds>550){flyout.Dismiss();hoverTimer.Stop();}
        }
        internal async void Refresh()
        {
            if(disposed||busy||preview)return;var p=preferences();if(!p.LiveQuota){failure="在线查询已关闭";Render();return;}busy=true;
            string home=p.CodexHome,cli=p.QuotaCli;
            try
            {
                var result=await Task.Run(()=>QuotaReader.Query(cli,home));
                if(!disposed&&home==preferences().CodexHome&&cli==preferences().QuotaCli){failure="";Accept(result);}
            }
            catch(Exception){if(!disposed){failure="在线额度暂不可用";Render();}}
            finally{busy=false;}
        }
        private static void Window(QuotaWindow window,TextBlock label,Border bar,long now)
        {
            bool valid=window!=null&&!(window.ResetsAt>0&&now>=window.ResetsAt);
            label.Text=window==null?"未返回额度":window.Label+" · "+window.Remaining(now);
            double fraction=valid?Math.Max(0,Math.Min(1,1-window.UsedPercent/100)):0;
            bar.Tag=fraction;bar.Width=Math.Max(0,((FrameworkElement)bar.Parent).ActualWidth*fraction);bar.Background=fraction<.15?Theme.Warning:Theme.Accent;
        }
        private void Render()
        {
            if(disposed)return;Visibility=preferences().ShowQuota?Visibility.Visible:Visibility.Collapsed;
            // Spark is a separate quota bucket. Never silently substitute it for the general Codex balance.
            QuotaBucket bucket;latest.TryGetValue("codex",out bucket);long now=LocalCodexUsage.Unix(DateTime.Now);
            var firstWindow=bucket==null?null:bucket.Primary??bucket.Secondary;var secondWindow=bucket==null||bucket.Primary==null?null:bucket.Secondary;
            Window(firstWindow,left,barLeft,now);Window(secondWindow,right,barRight,now);
            var leftColumn=(FrameworkElement)left.Parent;var rightColumn=(FrameworkElement)right.Parent;
            rightColumn.Visibility=secondWindow==null?Visibility.Collapsed:Visibility.Visible;
            Grid.SetColumnSpan(leftColumn,rightColumn.Visibility==Visibility.Collapsed?2:1);
            if(bucket==null){note.Text=String.IsNullOrEmpty(failure)?"等待账号额度 · 每分钟更新":failure;title.Text="CODEX · 额度未知";}
            else
            {
                title.Text="CODEX · 剩余额度";
                string stamp=new DateTime(1970,1,1,0,0,0,DateTimeKind.Utc).AddSeconds(bucket.ObservedAt).ToLocalTime().ToString("MM-dd HH:mm");
                note.Text=bucket.Origin+" "+stamp+(now-bucket.ObservedAt>300?" · 较旧快照":"")+(String.IsNullOrEmpty(failure)?"":" · "+failure);
            }
            var details=new List<string>{note.Text};
            foreach(var b in latest.Values.OrderBy(b=>b.Id))
            {
                details.Add((b.Id=="codex"?"Codex":b.Name??b.Id)+"："+Describe(b.Primary??b.Secondary,now)+(b.Primary!=null&&b.Secondary!=null?"；"+Describe(b.Secondary,now):""));
            }
            if(latest.Count==0)details.Add("需要 Codex CLI 已登录 ChatGPT；API Key 模式可能没有套餐额度。");
            ToolTip=String.Join("\n",details);if(flyout!=null&&flyout.IsVisible)UpdateFlyout();
            if(tray!=null)
            {
                string text=bucket==null?"Codex 额度未知 · "+note.Text:"Codex 剩余 "+left.Text+(secondWindow!=null?" / "+right.Text:"")+"\n"+note.Text;
                // The rich flyout replaces Explorer's limited native tooltip, preventing two overlapping popups.
                tray.Text=flyout!=null&&flyout.IsVisible?"":"Codex 用量速览";
                var main=bucket==null?null:bucket.Secondary??bucket.Primary;
                double? remaining=TrayFlyout.Remaining(bucket,main,now);
                balanceIcon=remaining.HasValue?remaining.Value.ToString("0",CultureInfo.InvariantCulture):"?";UpdateTrayIcon();
            }
            if(Changed!=null)Changed();
        }
        private static string Describe(QuotaWindow w,long now)
        {
            if(w==null)return "未返回";string reset=w.ResetsAt>0?new DateTime(1970,1,1,0,0,0,DateTimeKind.Utc).AddSeconds(w.ResetsAt).ToLocalTime().ToString("MM-dd HH:mm"):"未提供";
            return w.Label+"剩余 "+w.Remaining(now)+"，重置 "+reset;
        }
        internal static Drawing.Bitmap CreateBadge(string value)
        {
            // A recognizable 16px silhouette instead of illegible miniature text. Exact balances
            // live in the flyout; the ring length remains proportional to remaining quota.
            var bitmap=new Drawing.Bitmap(32,32);int number;bool known=Int32.TryParse(value,out number),done=value=="✓";
            var from=Drawing.Color.FromArgb(68,127,250);var to=Drawing.Color.FromArgb(80,229,203);
            if(known&&number<15){from=Drawing.Color.FromArgb(249,173,86);to=Drawing.Color.FromArgb(244,98,133);}
            using(var g=Drawing.Graphics.FromImage(bitmap))using(var gradient=new Drawing.Drawing2D.LinearGradientBrush(new Drawing.Rectangle(2,2,28,28),from,to,40f))
            using(var track=new Drawing.Pen(Drawing.Color.FromArgb(130,122,143,167),4))using(var arc=new Drawing.Pen(gradient,4){StartCap=Drawing.Drawing2D.LineCap.Round,EndCap=Drawing.Drawing2D.LineCap.Round})
            {
                g.SmoothingMode=Drawing.Drawing2D.SmoothingMode.AntiAlias;g.Clear(Drawing.Color.Transparent);g.DrawEllipse(track,3,3,26,26);
                if(done){using(var tick=new Drawing.Pen(gradient,3.6f){StartCap=Drawing.Drawing2D.LineCap.Round,EndCap=Drawing.Drawing2D.LineCap.Round,LineJoin=Drawing.Drawing2D.LineJoin.Round})g.DrawLines(tick,new[]{new Drawing.PointF(9,16),new Drawing.PointF(14,21),new Drawing.PointF(24,11)});g.DrawEllipse(arc,3,3,26,26);}
                else
                {
                    if(known&&number>0)g.DrawArc(arc,3,3,26,26,-90,Math.Min(359.9f,360*Math.Max(0,Math.Min(100,number))/100f));
                    using(var mark=new Drawing.Pen(gradient,2.4f){StartCap=Drawing.Drawing2D.LineCap.Round,EndCap=Drawing.Drawing2D.LineCap.Round}){g.DrawLines(mark,new[]{new Drawing.PointF(11,12),new Drawing.PointF(15,16),new Drawing.PointF(11,20)});g.DrawLine(mark,18,20,22,20);}
                }
            }
            return bitmap;
        }
        private void SetIcon(string value)
        {
            if(value==iconKey)return;iconKey=value;
            using(var bitmap=CreateBadge(value))
            {
                IntPtr handle=bitmap.GetHicon();try{using(var borrowed=Drawing.Icon.FromHandle(handle)){var old=tray.Icon;tray.Icon=(Drawing.Icon)borrowed.Clone();if(old!=null&&old!=Drawing.SystemIcons.Information)old.Dispose();}}finally{DestroyIcon(handle);}
            }
        }
        public void Dispose(){disposed=true;clock.Stop();hoverTimer.Stop();completionTimer.Stop();if(flyout!=null){flyout.Close();flyout=null;}if(tray!=null){tray.Visible=false;var icon=tray.Icon;tray.ContextMenuStrip.Dispose();tray.Dispose();if(icon!=null&&icon!=Drawing.SystemIcons.Information)icon.Dispose();tray=null;}}
    }
}
