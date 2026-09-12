using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace CodexUserData
{
    // A single vector layer keeps the island transparent at its corners and avoids blur
    // surfaces. Text layouts are rebuilt only when their source values actually change.
    internal sealed partial class DynamicIsland : FrameworkElement, IDisposable
    {
        internal static readonly DependencyProperty PhaseProperty=DependencyProperty.Register("Phase",typeof(double),typeof(DynamicIsland),new FrameworkPropertyMetadata(0.0,FrameworkPropertyMetadataOptions.AffectsRender));
        internal static readonly DependencyProperty BreathProperty=DependencyProperty.Register("Breath",typeof(double),typeof(DynamicIsland),new FrameworkPropertyMetadata(0.0,FrameworkPropertyMetadataOptions.AffectsRender));
        private readonly Typeface regular=new Typeface(new FontFamily("Segoe UI, Microsoft YaHei UI"),FontStyles.Normal,FontWeights.Normal,FontStretches.Normal);
        private readonly Typeface semibold=new Typeface(new FontFamily("Segoe UI, Microsoft YaHei UI"),FontStyles.Normal,FontWeights.SemiBold,FontStretches.Normal);
        private Brush body,bodyHover,ink,muted,line;
        private Color[] colors=new Color[0];
        private RadialGradientBrush[] rimBrushes=new RadialGradientBrush[0];private Pen[] rimPens=new Pen[0];private RadialGradientBrush innerBrush;private Pen linePen;
        private UsageSnapshot usage;private QuotaBucket bucket;private ActivityReport activity;private Preferences preferences;
        private string source="",stamp="",dataKey="",summary="",paintKey="";
        private bool expanded,completionPending,disposed,animated,running,clockCompletion,suspended;private int currentFps;
        private double clockSpeed;
        private readonly DispatcherTimer freshnessClock=new DispatcherTimer{Interval=TimeSpan.FromSeconds(1)};
        private readonly StreamGeometry outline=new StreamGeometry();
        private readonly SolidColorBrush breathingWash=new SolidColorBrush();
        private Brush sheen;
        private FormattedText title,value,status;
        private FormattedText[] quotaLabels=new FormattedText[0],quotaValues=new FormattedText[0];private FormattedText unavailable;
        internal string Summary {get{return summary;}}
        internal DynamicIsland()
        {
            SnapsToDevicePixels=true;Focusable=false;AutomationProperties.SetAutomationId(this,"DynamicIsland");
            IsMouseDirectlyOverChanged+=delegate{InvalidateVisual();};
            IsVisibleChanged+=delegate{suspended=!IsVisible;if(IsVisible&&!disposed){RefreshText();freshnessClock.Start();}else{freshnessClock.Stop();SetPressed(false);}SyncMotion();};
            // Age telemetry once per second, not once per animation frame. This also expires
            // a running indication if the publisher stops sending reports altogether.
            freshnessClock.Tick+=delegate{RefreshText();SyncMotion();};
        }
        internal void SetExpanded(bool value){if(disposed||expanded==value)return;expanded=value;RefreshText();}
        internal void ApplyActivity(ActivityReport report){if(disposed)return;activity=report;RefreshText();SyncMotion();}
        internal static string[] EffectiveColors(Preferences prefs)
        {
            if(prefs==null)return new string[0];
            if(!prefs.IslandFollowTheme)return prefs.IslandColors;
            if(prefs.ThemeMode=="custom")return prefs.GradientColors;
            return Theme.IsLight?new[]{"#2458E5","#138FAC","#46CDB7"}:new[]{"#3976FF","#35C8E2","#87F3BE"};
        }
        internal void Apply(UsageSnapshot snapshot,QuotaBucket quota,ActivityReport report,string scope,string quotaStatus,Preferences prefs)
        {
            if(disposed)return;usage=snapshot;bucket=quota;activity=report;source=scope??"";stamp=quotaStatus??"";preferences=prefs;
            bool light=Theme.IsLight;
            // Island theme-follow is independent of the ring's own theme-follow switch.
            string[] palette=EffectiveColors(prefs);
            string key=Theme.Revision+"/"+(prefs==null?"glass":prefs.IslandMaterial)+"/"+String.Join(",",palette??new string[0]);
            if(key!=paintKey)
            {
                paintKey=key;body=Theme.B(light?"#F2F7FAFC":"#F20B0D11");bodyHover=Theme.B(light?"#FFFFFFFF":"#FF12161C");ink=Theme.Ink;muted=Theme.Muted;line=Theme.Line;
                colors=(palette??new string[0]).Select(ColorOf).ToArray();if(colors.Length==0)colors=new[]{Colors.CornflowerBlue,Colors.Turquoise};BuildPaintCache();InvalidateVisual();
            }
            RefreshText();SyncMotion();
        }
        internal void SetCompletionPending(bool value){if(disposed)return;completionPending=value;RefreshText();SyncMotion();}
        private static Color ColorOf(string value){try{return (Color)ColorConverter.ConvertFromString(value);}catch{return Colors.CornflowerBlue;}}
        private void BuildPaintCache()
        {
            rimBrushes=new RadialGradientBrush[colors.Length];rimPens=new Pen[colors.Length];
            for(int i=0;i<colors.Length;i++){Color c=colors[i];var brush=new RadialGradientBrush(Color.FromArgb(220,c.R,c.G,c.B),Color.FromArgb(0,c.R,c.G,c.B)){RadiusX=.48,RadiusY=.75};rimBrushes[i]=brush;rimPens[i]=new Pen(brush,2.8);}
            innerBrush=new RadialGradientBrush(Colors.Transparent,Colors.Transparent){RadiusX=.55,RadiusY=.9};linePen=new Pen(line,.65);
            var gloss=new LinearGradientBrush{StartPoint=new Point(0,0),EndPoint=new Point(0,1)};
            gloss.GradientStops.Add(new GradientStop(Color.FromArgb(Theme.IsLight?(byte)90:(byte)17,255,255,255),0));gloss.GradientStops.Add(new GradientStop(Colors.Transparent,.65));gloss.Freeze();sheen=gloss;
            BuildGlassPaint();
        }
        private void RefreshText()
        {
            long now=LocalCodexUsage.Unix(DateTime.Now);var today=usage==null?null:usage.Daily.LastOrDefault(d=>d.Date==DateTime.Now.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture));
            running=activity!=null&&activity.ObservedAt>0&&activity.ObservedAt<=now+5&&now-activity.ObservedAt<=5&&activity.ActiveTasks>0&&activity.Until>now;
            freshnessClock.Interval=TimeSpan.FromSeconds(running?1:15);
            var windows=bucket==null?new QuotaWindow[0]:new[]{bucket.Primary,bucket.Secondary}.Where(w=>w!=null).ToArray();
            string token=today==null?"—":TokenText.Compact(today.Tokens);
            string task=QuotaStatus.ActivityLabel(activity,completionPending,now);if(task=="当前空闲")task="今日 Tokens";
            string quota=windows.Length==0?"额度待更新":windows[windows.Length-1].Label+" "+windows[windows.Length-1].Remaining(bucket,now);
            string key=paintKey+"|"+VisualTreeHelper.GetDpi(this).PixelsPerDip+"|"+expanded+"|"+token+"|"+task+"|"+quota+"|"+source+"|"+stamp+"|"+(bucket==null?"":bucket.DescribeSource(now))+"|"+String.Join("/",windows.Select(w=>w.Label+w.Remaining(bucket,now)));
            if(key==dataKey)return;dataKey=key;
            if(ink==null){ink=Theme.Ink;muted=Theme.Muted;}
            title=Text("今日 Tokens",9,muted,regular);value=Text(token,20,ink,semibold);status=Text(quota,10,ink,regular);
            string detail=bucket==null?(String.IsNullOrWhiteSpace(stamp)?"额度来源未知":stamp):bucket.DescribeSource(now);
            quotaLabels=windows.Select(w=>Text(w.Label,9,muted,regular)).ToArray();quotaValues=windows.Select(w=>Text(w.Remaining(bucket,now),18,ink,semibold)).ToArray();
            UpdateGlassData(token,windows,now);
            unavailable=Text("额度待更新",11,muted,regular);
            summary=task+"；今日 "+(today==null?"等待记录":token+" Tokens")+"；"+(windows.Length==0?"额度暂不可用":String.Join("，",windows.Select(w=>w.Label+"剩余"+w.Remaining(bucket,now))))+"；"+detail;
            AutomationProperties.SetName(this,summary);InvalidateVisual();
        }
        private FormattedText Text(string text,double size,Brush brush,Typeface face)
        {return new FormattedText(text??"",CultureInfo.CurrentUICulture,FlowDirection.LeftToRight,face,size,brush,VisualTreeHelper.GetDpi(this).PixelsPerDip){MaxLineCount=1,Trimming=TextTrimming.CharacterEllipsis};}
        internal void Start(int fps){if(disposed)return;suspended=false;SyncMotion();}
        internal void Stop(){suspended=true;StopClocks();}
        private void SyncMotion()
        {
            int fps=preferences==null?0:Theme.ActivityFrameRate(preferences.OrbAnimation);
            bool completion=completionPending&&!running,need=!disposed&&!suspended&&IsVisible&&Theme.MotionAllowed&&fps>0&&(running||completion);
            double speed=preferences==null?1:Theme.Bound(preferences.AnimationSpeed,.5,2,1);
            if(!need){StopClocks();currentFps=fps;return;}if(animated&&currentFps==fps&&clockCompletion==completion&&clockSpeed==speed)return;
            StopClocks();animated=true;currentFps=fps;clockCompletion=completion;clockSpeed=speed;
            var phase=new DoubleAnimation(0,1,TimeSpan.FromSeconds(3.2/speed)){RepeatBehavior=RepeatBehavior.Forever};
            var breath=new DoubleAnimation(0,1,TimeSpan.FromSeconds((completion?1.35:1.4)/speed)){AutoReverse=true,RepeatBehavior=RepeatBehavior.Forever,EasingFunction=new SineEase{EasingMode=EasingMode.EaseInOut}};
            Timeline.SetDesiredFrameRate(phase,fps);Timeline.SetDesiredFrameRate(breath,fps);BeginAnimation(PhaseProperty,phase);BeginAnimation(BreathProperty,breath);
        }
        private void StopClocks(){if(!animated)return;animated=false;BeginAnimation(PhaseProperty,null);BeginAnimation(BreathProperty,null);InvalidateVisual();}
        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);if(disposed||RenderWidth<20||RenderHeight<20||body==null||title==null)return;
            if(preferences==null||preferences.IslandMaterial!="classic"){DrawGlass(dc);return;}
            double phase=(double)GetValue(PhaseProperty),breath=(double)GetValue(BreathProperty),pad=3.5,radius=Math.Min(24,(RenderHeight-2*pad)/2);
            Rect rect=new Rect(pad,pad,Math.Max(1,RenderWidth-pad*2),Math.Max(1,RenderHeight-pad*2));
            // Four overlapping color lobes read as one fluid rim. Eco mode uses two larger
            // lobes, reducing radial-gradient paint work while preserving state color.
            bool completion=completionPending&&!running;
            double strength=preferences==null?1:preferences.EffectStrength;
            int lobes=currentFps>0&&currentFps<=30?Math.Min(2,rimBrushes.Length):rimBrushes.Length;double glow=(completion?0.78:running?0.62:0.34)*(running||completion?strength:1);
            for(int i=0;i<lobes;i++)
            {
                double a=phase*Math.PI*2+i*Math.PI*2/lobes;Point center=new Point(.5+.42*Math.Cos(a),.5+.38*Math.Sin(a));
                int index=(i*rimBrushes.Length/lobes)%rimBrushes.Length;rimBrushes[index].Center=center;rimBrushes[index].GradientOrigin=center;rimBrushes[index].Opacity=Math.Min(1,.52+.22*glow+.08*breath);rimPens[index].Thickness=2.2+glow;
                dc.DrawRoundedRectangle(null,rimPens[index],rect,radius,radius);
            }
            // The body subtly swells at the moving light position; the text is painted later
            // in fixed coordinates and therefore remains perfectly stable.
            // Keep deformation inside the existing transparent margin at maximum strength.
            double flex=(running||completion)?Math.Min(2.8,(.8+1.1*breath)*Math.Sqrt(strength)):0;double wave=Math.Sin(phase*Math.PI*2);
            using(var g=outline.Open()){double l=pad+1,r=RenderWidth-pad-1,t=pad+1-flex*Math.Max(0,wave),b=RenderHeight-pad-1-flex*Math.Max(0,-wave);g.BeginFigure(new Point(l+radius,t),true,true);g.LineTo(new Point(r-radius,t),true,false);g.ArcTo(new Point(r,t+radius),new Size(radius,radius),0,false,SweepDirection.Clockwise,true,false);g.LineTo(new Point(r,b-radius),true,false);g.ArcTo(new Point(r-radius,b),new Size(radius,radius),0,false,SweepDirection.Clockwise,true,false);g.LineTo(new Point(l+radius,b),true,false);g.ArcTo(new Point(l,b-radius),new Size(radius,radius),0,false,SweepDirection.Clockwise,true,false);g.LineTo(new Point(l,t+radius),true,false);g.ArcTo(new Point(l+radius,t),new Size(radius,radius),0,false,SweepDirection.Clockwise,true,false);}dc.DrawGeometry(IsMouseOver?bodyHover:body,linePen,outline);
            dc.PushClip(outline);dc.DrawRectangle(sheen,null,rect);
            if(running||completion)
            {
                Color c=completion?colors[colors.Length-1]:colors[Math.Min(1,colors.Length-1)];breathingWash.Color=Color.FromArgb((byte)((7+18*breath)*strength),c.R,c.G,c.B);dc.DrawRectangle(breathingWash,null,rect);
                c.A=(byte)((30+40*breath)*strength);Point origin=new Point(.5+.36*Math.Cos(phase*Math.PI*2),.5+.22*Math.Sin(phase*Math.PI*2));innerBrush.GradientStops[0].Color=c;innerBrush.GradientStops[1].Color=Color.FromArgb(0,c.R,c.G,c.B);innerBrush.Center=origin;innerBrush.GradientOrigin=origin;dc.DrawRectangle(innerBrush,null,rect);
            }
            dc.Pop();
            dc.PushClip(outline);DrawClassicContent(dc);dc.Pop();
        }
        private void DrawClassicContent(DrawingContext dc)
        {
            double detail=(double)GetValue(ExpansionProperty),center=RenderHeight/2;
            value.MaxTextWidth=104;dc.DrawText(value,new Point(18,center-value.Height/2+6*detail));
            if(detail<1)
            {
                dc.PushOpacity(1-detail);status.MaxTextWidth=Math.Max(40,RenderWidth-132);
                dc.DrawText(status,new Point(RenderWidth-status.Width-18,center-status.Height/2));dc.Pop();
            }
            if(detail<=.01)return;
            dc.PushOpacity(detail);dc.DrawText(title,new Point(18,center-24));
            double cell=Math.Max(40,RenderWidth-154)/Math.Max(1,quotaLabels.Length),x=136;
            if(quotaLabels.Length==0)dc.DrawText(unavailable,new Point(x,center-unavailable.Height/2));
            for(int i=0;i<quotaLabels.Length;i++)
            {
                double middle=x+cell/2;
                dc.DrawText(quotaLabels[i],new Point(middle-quotaLabels[i].Width/2,center-21));
                dc.DrawText(quotaValues[i],new Point(middle-quotaValues[i].Width/2,center-3));x+=cell;
            }
            dc.Pop();
        }
        public void Dispose(){if(disposed)return;disposed=true;freshnessClock.Stop();StopMorph();BeginAnimation(PressProperty,null);StopClocks();}
    }
}
