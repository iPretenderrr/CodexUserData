using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace CodexUserData
{
    // Each ring is an independent quota window. Activity changes the light, never the quota.
    // Cache shell/text, avoid blur effects, and stop the compositor subscription at rest.
    internal sealed class QuotaOrb : FrameworkElement, IDisposable
    {
        private sealed class Ring
        {
            internal readonly double Radius,Width;
            internal Brush Color,Light;
            internal Pen Stroke,Glow,TrackStroke,TailStroke,TailGlow;
            internal bool Known;
            internal double Remaining,Target,Focus,Drawn=-1,DrawnRadius=-1;
            internal bool Warped;
            internal Geometry Arc=Geometry.Empty,Track=Geometry.Empty;
            internal string Label="—",Period;
            internal FormattedText Number,Caption;
            internal Ring(double radius,double width,Brush color,Brush light,string period)
            {
                Radius=radius;Width=width;Period=period;TrackStroke=new Pen(Theme.Line,width);Recolor(color,light);
            }
            internal void Recolor(Brush color,Brush light)
            {
                Color=color;Light=light;Stroke=new Pen(color,Width){StartLineCap=PenLineCap.Round,EndLineCap=PenLineCap.Round};Stroke.Freeze();Glow=new Pen(color,Width+4){StartLineCap=PenLineCap.Round,EndLineCap=PenLineCap.Round};Glow.Freeze();
                TailStroke=new Pen(light,1.5){StartLineCap=PenLineCap.Round,EndLineCap=PenLineCap.Round};TailStroke.Freeze();TailGlow=new Pen(light,4){StartLineCap=PenLineCap.Round,EndLineCap=PenLineCap.Round};TailGlow.Freeze();
            }
        }
        private readonly Stopwatch clock=Stopwatch.StartNew();
        private readonly DispatcherTimer heartbeat=new DispatcherTimer{Interval=TimeSpan.FromSeconds(1)};
        private readonly Typeface face=new Typeface("Segoe UI, Microsoft YaHei UI");
        private readonly Ring[] rings={
            new Ring(52.5,7.2,Gradient("#316BF1","#5F97FF","#89CDEC"),Theme.B("#DAF5FF"),"5h"),
            new Ring(43.5,5.4,Gradient("#7965EA","#AB8DF0","#E2B0ED"),Theme.B("#F5E5FF"),"7d")};
        private Preferences prefs;
        private QuotaBucket bucket;
        private UsageSnapshot snapshot;
        private ActivityReport activity;
        private string context,dayKey,textKey,colorKey,activityText;
        private long previousTokens,activeUntil;
        private double previousSample,rateEnergy,lastBurst=-100,phase,energy,lastFrame=-1,press,pulseAt=-100,hover;
        private double pointerX,pointerY,offsetX,offsetY;
        private bool baseline,subscribed,disposed,pressed;
        private int focus=-1,paletteRevision=-1,fpsTarget;
        private DrawingGroup shell;
        private readonly Pen chassisStroke=new Pen(Theme.Line,.45),unknownStroke=new Pen(Theme.Line,6);
        private Brush chassisFill;
        private FormattedText label;
        private bool ShortKnown {get{return rings[0].Known;}}
        private bool LongKnown {get{return rings[1].Known;}}
        internal int VisibleRingCount {get;private set;}
        private bool Lightweight {get{return fpsTarget<=30;}}
        internal static int WaveSamples(int fps){return fps<=30?40:112;}
        internal static int ParticleCount(int fps){return fps<=0?0:fps<=30?4:22;}

        internal QuotaOrb()
        {
            SnapsToDevicePixels=false;UseLayoutRounding=false;Cursor=Cursors.SizeAll;Theme.Watch(this);
            AutomationProperties.SetAutomationId(this,"QuotaOrb");
            System.Windows.Controls.ToolTipService.SetIsEnabled(this,false);ToolTip=null;
            heartbeat.Tick+=delegate{RefreshState();};
            Loaded+=delegate{VisibilityChanged();};Unloaded+=delegate{Stop();};IsVisibleChanged+=delegate{VisibilityChanged();};
            MouseEnter+=delegate{TrackPointer(Mouse.GetPosition(this));};MouseMove+=delegate(object sender,MouseEventArgs e){TrackPointer(e.GetPosition(this));};
            MouseLeave+=delegate{pointerX=pointerY=0;FocusRing(-1);};
        }
        private static Brush Gradient(string a,string b,string c)
        {
            // Absolute coordinates keep the colors stationary when an arc changes length.
            var result=new LinearGradientBrush{MappingMode=BrushMappingMode.Absolute,StartPoint=new Point(20,20),EndPoint=new Point(108,108)};
            result.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(a),0));result.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(b),.5));result.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(c),1));result.Freeze();return result;
        }
        internal static QuotaWindow SelectWindow(QuotaBucket quota,string period)
        {
            if(quota==null)return null;
            if(period=="auto")return SelectWindow(quota,"short")??SelectWindow(quota,"week");
            return new[]{quota.Primary,quota.Secondary}.FirstOrDefault(w=>w!=null&&(period=="week"?w.Minutes>=1440:w.Minutes>0&&w.Minutes<1440));
        }
        internal void Apply(Preferences preferences,QuotaBucket quota,UsageSnapshot usage,string scope)
        {
            bool freshSample=!Object.ReferenceEquals(snapshot,usage);prefs=preferences;bucket=quota;snapshot=usage;
            if(prefs.OrbShortColors==null||prefs.OrbLongColors==null)OrbPalette.Normalize(prefs);
            string nextColors=OrbPalette.Key(prefs);
            if(colorKey!=nextColors)
            {
                colorKey=nextColors;rings[0].Recolor(OrbPalette.Create(OrbPalette.EffectiveColors(prefs,false),OrbPalette.EffectiveAngle(prefs,false)),OrbPalette.Light(OrbPalette.EffectiveColors(prefs,false)));rings[1].Recolor(OrbPalette.Create(OrbPalette.EffectiveColors(prefs,true),OrbPalette.EffectiveAngle(prefs,true)),OrbPalette.Light(OrbPalette.EffectiveColors(prefs,true)));textKey=null;InvalidateVisual();
            }
            var today=usage==null?null:usage.Daily.LastOrDefault(d=>d.Date==DateTime.Now.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture));
            double now=clock.Elapsed.TotalSeconds;long at=LocalCodexUsage.Unix(DateTime.Now);
            string key=today==null?null:today.Date;
            if(!baseline||context!=scope||dayKey!=key){baseline=true;previousTokens=today==null?0:today.Tokens;previousSample=now;lastBurst=-100;rateEnergy=0;}
            else if((activity==null||activity.ActiveTasks>0)&&today!=null&&today.Tokens>previousTokens&&usage.LatestUsageUnix>0&&at-usage.LatestUsageUnix>=0&&at-usage.LatestUsageUnix<=15)
            {
                double rate=(today.Tokens-previousTokens)/Math.Max(1,now-previousSample);
                rateEnergy=Math.Min(1,.25+Math.Log10(1+rate)/5);lastBurst=now;
            }
            context=scope;dayKey=key;previousTokens=today==null?0:today.Tokens;if(freshSample)previousSample=now;
            activeUntil=activity!=null?activity.Until:usage!=null&&usage.ActiveTasks>0?usage.ActivityUntil:0;
            RefreshState();
        }
        internal void ApplyActivity(ActivityReport report)
        {
            activity=report;activeUntil=report!=null&&report.ActiveTasks>0?report.Until:0;
            if(report==null||report.ActiveTasks==0){lastBurst=-100;rateEnergy=0;}RefreshState();
        }
        private static string PeriodLabel(QuotaWindow w,string fallback)
        {
            if(w==null)return fallback;
            return w.Minutes>=1440?(w.Minutes/1440.0).ToString("0.#",CultureInfo.InvariantCulture)+"d":(w.Minutes/60.0).ToString("0.#",CultureInfo.InvariantCulture)+"h";
        }
        private double Activity()
        {
            long now=LocalCodexUsage.Unix(DateTime.Now);bool running=activeUntil>now;
            double burst=rateEnergy*Math.Max(0,1-(clock.Elapsed.TotalSeconds-lastBurst)/10);
            return Math.Max(running?.72:0,burst);
        }
        private int FrameRate(){return prefs==null?0:Theme.ActivityFrameRate(prefs.OrbAnimation);}
        private void RefreshState()
        {
            if(disposed)return;long now=LocalCodexUsage.Unix(DateTime.Now);
            for(int i=0;i<2;i++)
            {
                var ring=rings[i];var w=SelectWindow(bucket,i==0?"short":"week");
                double? remaining=w==null?null:w.RemainingPercent(bucket,now);bool known=remaining.HasValue;
                double value=known?remaining.Value/100:0;
                // Unknown data clears immediately; a first valid sample never animates from fake zero.
                if(!ring.Known||!known)ring.Remaining=value;ring.Known=known;ring.Target=value;
                ring.Label=known?(value*100).ToString("0",CultureInfo.InvariantCulture)+"%":"—";
                ring.Period=PeriodLabel(w,i==0?"5h":"7d");
            }
            // Missing/expired periods occupy no visual slot. Change the layout and labels together
            // in this refresh, keeping a lone weekly quota centered instead of reserving a fake 5h row.
            int count=(ShortKnown?1:0)+(LongKnown?1:0);
            if(count!=VisibleRingCount){VisibleRingCount=count;if(focus>=0)focus=2;}
            string text=String.Join("/",rings.Select(r=>r.Period+":"+r.Label))+"/"+VisibleRingCount+"/"+Theme.Revision+"/"+(bucket==null?"":bucket.SourceLabel);
            if(textKey!=text)
            {
                textKey=text;var bold=new Typeface(face.FontFamily,FontStyles.Normal,FontWeights.SemiBold,FontStretches.Normal);
                foreach(var ring in rings)
                {
                    ring.Number=new FormattedText(ring.Label,CultureInfo.InvariantCulture,FlowDirection.LeftToRight,bold,VisibleRingCount==1?34:23,ring.Known?Theme.Ink:Theme.Muted,1);
                    Brush caption=ring.Known&&prefs!=null?OrbPalette.Caption(OrbPalette.EffectiveColors(prefs,ring!=rings[0]),ring.Color):Theme.Muted;
                    ring.Caption=new FormattedText(ring.Period+(VisibleRingCount==1?" · 剩余":""),CultureInfo.InvariantCulture,FlowDirection.LeftToRight,face,11,caption,1);
                }
                label=new FormattedText("额度待更新",CultureInfo.InvariantCulture,FlowDirection.LeftToRight,face,11,Theme.Muted,1);
                AutomationProperties.SetName(this,VisibleRingCount==0?"Codex 额度待更新":"Codex 剩余额度 · "+String.Join(" · ",rings.Where(r=>r.Known).Select(r=>r.Period+" "+r.Label)));InvalidateVisual();
            }
            string status=QuotaStatus.ActivityLabel(activity,false,now);
            AutomationProperties.SetItemStatus(this,status);
            if(activityText!=status){activityText=status;InvalidateVisual();}
            fpsTarget=FrameRate();if(fpsTarget==0)pulseAt=-100;UpdateMotion();
        }
        private void TrackPointer(Point position)
        {
            double size=Math.Min(ActualWidth,ActualHeight);if(size<=0)return;
            double x=(position.X-ActualWidth/2)*128/size,y=(position.Y-ActualHeight/2)*128/size,r=Math.Sqrt(x*x+y*y);
            if(r>63){pointerX=pointerY=0;FocusRing(-1);return;}
            pointerX=Math.Max(-1,Math.Min(1,x/52));pointerY=Math.Max(-1,Math.Min(1,y/52));
            FocusRing(VisibleRingCount<2?2:r>=48?0:r>=34?1:2);
            if(fpsTarget>0&&IsVisible&&focus>=0)UpdateMotion();
        }
        private void FocusRing(int index){if(disposed||focus==index)return;focus=index;UpdateMotion();}
        internal void SetPressed(bool value){if(disposed||pressed==value)return;pressed=value;UpdateMotion();}
        // Single clicks produce only a local light ripple, without a popup or data query.
        internal void Pulse(){if(disposed||fpsTarget==0)return;pulseAt=clock.Elapsed.TotalSeconds;UpdateMotion();}
        private double TargetFocus(int index){return focus==index||focus==2?1:0;}
        private double ClickStrength(){double age=clock.Elapsed.TotalSeconds-pulseAt;return age>=0&&age<1.25?Math.Exp(-age*2.8):0;}
        private bool Moving()
        {
            if(Activity()>.005||energy>.005||focus>=0||hover>.005||Math.Abs(press-(pressed?1:0))>.001||ClickStrength()>0||Math.Abs(offsetX-pointerX)>.005||Math.Abs(offsetY-pointerY)>.005)return true;
            for(int i=0;i<2;i++)if(Math.Abs(rings[i].Remaining-rings[i].Target)>.0001||Math.Abs(rings[i].Focus-TargetFocus(i))>.001)return true;
            return false;
        }
        private void UpdateMotion()
        {
            if(disposed)return;
            if(IsVisible&&fpsTarget>0&&Moving())Subscribe();
            else
            {
                Unsubscribe();bool changed=energy!=0||hover!=0||offsetX!=0||offsetY!=0||press!=(pressed?1:0);energy=hover=offsetX=offsetY=0;press=pressed?1:0;
                for(int i=0;i<2;i++){var r=rings[i];changed|=r.Remaining!=r.Target||r.Focus!=TargetFocus(i);r.Remaining=r.Target;r.Focus=TargetFocus(i);}
                if(changed)InvalidateVisual();
            }
        }
        private void VisibilityChanged(){if(IsVisible&&!disposed){heartbeat.Start();RefreshState();}else Stop();}
        private void Subscribe(){if(subscribed)return;subscribed=true;lastFrame=-1;CompositionTarget.Rendering+=Frame;}
        private void Unsubscribe(){if(!subscribed)return;subscribed=false;CompositionTarget.Rendering-=Frame;lastFrame=-1;}
        private void Stop(){heartbeat.Stop();Unsubscribe();energy=hover=offsetX=offsetY=pointerX=pointerY=0;focus=-1;pressed=false;press=0;pulseAt=-100;foreach(var r in rings){r.Focus=0;r.Remaining=r.Target;}InvalidateVisual();}
        private void Frame(object sender,EventArgs args)
        {
            if(disposed||!IsVisible){Stop();return;}int fps=fpsTarget;if(fps==0){RefreshState();return;}
            double now=clock.Elapsed.TotalSeconds;if(lastFrame>=0&&now-lastFrame<1.0/fps-.001)return;
            double dt=lastFrame<0?1.0/fps:Math.Min(.08,now-lastFrame);lastFrame=now;
            double nextActivity=Activity();energy+=(nextActivity-energy)*(1-Math.Exp(-dt*(nextActivity<energy?8:3.5)));press+=((pressed?1:0)-press)*(1-Math.Exp(-dt*22));
            hover+=((focus>=0?1:0)-hover)*(1-Math.Exp(-dt*9));offsetX+=(pointerX-offsetX)*(1-Math.Exp(-dt*12));offsetY+=(pointerY-offsetY)*(1-Math.Exp(-dt*12));
            for(int i=0;i<2;i++){var r=rings[i];r.Remaining+=(r.Target-r.Remaining)*(1-Math.Exp(-dt*8));r.Focus+=(TargetFocus(i)-r.Focus)*(1-Math.Exp(-dt*14));}
            phase+=dt*(1+Math.Max(energy,hover*.8)*1.2);InvalidateVisual();if(!Moving())UpdateMotion();
        }
        private static Point At(double radius,double angle){return new Point(64+Math.Cos(angle)*radius,64+Math.Sin(angle)*radius);}
        private static Geometry Arc(double radius,double fraction)
        {
            if(fraction<=0)return Geometry.Empty;
            if(fraction>=.99999){var circle=new EllipseGeometry(new Point(64,64),radius,radius);circle.Freeze();return circle;}
            var shape=new StreamGeometry();using(var dc=shape.Open()){dc.BeginFigure(At(radius,-Math.PI/2),false,false);dc.ArcTo(At(radius,-Math.PI/2+fraction*Math.PI*2),new Size(radius,radius),0,fraction>.5,SweepDirection.Clockwise,true,false);}shape.Freeze();return shape;
        }
        private void CacheShell()
        {
            var group=new DrawingGroup();using(var dc=group.Open())
            {
                var center=new Point(64,64);
                var fill=new RadialGradientBrush{Center=new Point(.45,.35),GradientOrigin=new Point(.32,.2),RadiusX=.85,RadiusY=.85};
                fill.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(Theme.IsLight?"#FFFFFF":"#24303D"),0));fill.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(Theme.IsLight?"#EEF3F9":"#151C26"),1));fill.Freeze();
                chassisFill=fill;
                dc.DrawEllipse(fill,null,center,59.8,59.8);
                dc.PushOpacity(.18);dc.DrawEllipse(Theme.WindowBackground,null,center,59.5,59.5);dc.Pop();
                dc.PushOpacity(Theme.IsLight?.5:.25);dc.DrawEllipse(null,new Pen(Theme.Line,.55),center,59.8,59.8);dc.Pop();
            }
            // Clone before freezing: shared theme brushes must remain mutable for live previews.
            shell=(DrawingGroup)group.GetCurrentValueAsFrozen();paletteRevision=Theme.Revision;
        }
        private static Point WavePoint(double radius,double angle,double time,double power)
        {
            // Both rings share this continuous deformation field, so they bend together without
            // crossing. Five logical pixels at full strength remain visible even in the 84px orb.
            double bend=.58*Math.Sin(3*angle-time*2)+.29*Math.Sin(2*angle+time)+.13*Math.Sin(5*angle-time*3);
            double r=radius+5*power*(radius/52.5)*bend;
            return At(r,angle+.035*power*Math.Sin(2*angle+time));
        }
        private Geometry WaveArc(double radius,double start,double end,double time,double power,bool filled=false)
        {
            if(end<=start)return Geometry.Empty;
            // The lightweight profile changes geometry complexity as well as frame cadence.
            int count=Math.Max(8,(int)Math.Ceiling(WaveSamples(fpsTarget)*(end-start)));
            var shape=new StreamGeometry();using(var line=shape.Open())
            {
                for(int i=0;i<=count;i++)
                {
                    double a=-Math.PI/2+Math.PI*2*(start+(end-start)*i/count);var point=WavePoint(radius,a,time,power);
                    if(i==0)line.BeginFigure(point,filled,end-start>=.99999);else line.LineTo(point,true,false);
                }
            }
            shape.Freeze();return shape;
        }
        private void DrawRing(DrawingContext dc,Ring ring,double otherFocus,double time,double power)
        {
            double fraction=ring.Remaining;
            double radius=VisibleRingCount==1?52.5:ring.Radius,drawValue=Math.Round(fraction,5);
            bool warp=power>.003;
            if(warp||ring.Warped||ring.Drawn!=drawValue||ring.DrawnRadius!=radius)
            {
                ring.Drawn=drawValue;ring.DrawnRadius=radius;ring.Warped=warp;
                ring.Track=warp?WaveArc(radius,0,1,time,power):Arc(radius,1);
                ring.Arc=warp?WaveArc(radius,0,drawValue,time,power):Arc(radius,drawValue);
            }
            double emphasis=(1-.3*Math.Max(0,otherFocus-ring.Focus))*(bucket!=null&&bucket.IsOnline?1:.76);
            dc.PushOpacity(emphasis);
            dc.PushOpacity(Theme.IsLight?.48:.5);dc.DrawGeometry(null,ring.TrackStroke,ring.Track);dc.Pop();
            if(ring.Known&&fraction>0)
            {
                if(!Lightweight){dc.PushOpacity(.07+.14*power+.13*ring.Focus);dc.DrawGeometry(null,ring.Glow,ring.Arc);dc.Pop();}
                dc.DrawGeometry(null,ring.Stroke,ring.Arc);
                // Sparse glints stay inside the colored arc with stable positions, no random flicker.
                int glints=Lightweight?3:9;
                for(int j=0;j<glints;j++)
                {
                    double along=(j+.55)/(glints+1);if(along>=fraction)continue;
                    double angle=-Math.PI/2+along*Math.PI*2;
                    double opacity=.18+.45*power*(.5+.5*Math.Sin(time*2-j));
                    dc.PushOpacity(opacity);dc.DrawEllipse(ring.Light,null,WavePoint(radius+(j%3-1)*1.1,angle,time,power),j%3==0?.85:.48,j%3==0?.85:.48);dc.Pop();
                }
                if(power>.005)
                {
                    double cycle=(time/(Math.PI*2)+(ring==rings[0]?0:.46))%1;
                    double angle=-Math.PI/2+cycle*fraction*Math.PI*2,fade=Math.Sin(cycle*Math.PI)*power;
                    // Layer a soft, longer trail underneath the moving highlight; no blur filter.
                    var tail=WaveArc(radius,Math.Max(0,cycle*fraction-.16),cycle*fraction,time,power);
                    if(!Lightweight){dc.PushOpacity(.18*fade);dc.DrawGeometry(null,ring.TailGlow,tail);dc.Pop();}
                    dc.PushOpacity(.68*fade);dc.DrawGeometry(null,ring.TailStroke,tail);dc.Pop();
                    if(!Lightweight){dc.PushOpacity(.2*fade);dc.DrawEllipse(ring.Light,null,WavePoint(radius,angle,time,power),3.6,3.6);dc.Pop();}
                    dc.PushOpacity(fade);dc.DrawEllipse(ring.Light,null,WavePoint(radius,angle,time,power),1.4,1.4);dc.Pop();
                }
            }
            dc.Pop();
        }
        private void DrawParticles(DrawingContext dc,double time,double power)
        {
            if(power<=.005)return;int count=ParticleCount(fpsTarget);
            // Deterministic orbital particles: bounded count, no allocations for particle state,
            // no random frame-to-frame flicker, and no particle crosses the central text.
            for(int i=0;i<count;i++)
            {
                var ring=VisibleRingCount==1?(ShortKnown?rings[0]:rings[1]):rings[i%2];
                double life=(time/(Math.PI*2)+i*.61803398875)%1;
                double angle=i*2.39996323+time*(i%2==0?1:-1),radius=50+10*life;
                double opacity=Math.Sin(life*Math.PI);opacity=opacity*opacity*power*.85;
                Point p=WavePoint(radius,angle,time,power*.18);double size=i%5==0?1.05:.55;
                Brush color=ring.Known?ring.Light:Theme.Muted;
                if(!Lightweight&&i%5==0){dc.PushOpacity(opacity*.16);dc.DrawEllipse(color,null,p,2.8,2.8);dc.Pop();}
                dc.PushOpacity(opacity);dc.DrawEllipse(color,null,p,size,size);
                if(!Lightweight&&i%7==0){var pen=new Pen(color,.55);dc.DrawLine(pen,new Point(p.X-1.8,p.Y),new Point(p.X+1.8,p.Y));dc.DrawLine(pen,new Point(p.X,p.Y-1.8),new Point(p.X,p.Y+1.8));}
                dc.Pop();
            }
        }
        private void DrawChassis(DrawingContext dc,double time,double power)
        {
            if(power<=.003||Lightweight)
            {
                dc.DrawDrawing(shell);
                // Reuse the cached chassis in eco mode. Its shared rotation/scale and a quiet
                // breathing tint retain task feedback without rebuilding a filled wave path.
                if(Lightweight&&power>.003){dc.PushOpacity(power*(.025+.055*(.5+.5*Math.Sin(time*2))));dc.DrawEllipse(rings[ShortKnown?0:1].Color,null,new Point(64,64),59,59);dc.Pop();}
                return;
            }
            // The body follows the same wave field with a smaller amplitude, moving as one
            // object with the rings. Native bounds and center text remain untouched.
            var body=WaveArc(58.5,0,1,time,power*.55,true);
            dc.DrawGeometry(chassisFill,chassisStroke,body);
            dc.PushOpacity(.18);dc.DrawGeometry(Theme.WindowBackground,null,body);dc.Pop();
        }
        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);double diameter=Math.Min(ActualWidth,ActualHeight);if(diameter<=0)return;
            if(shell==null||paletteRevision!=Theme.Revision){CacheShell();textKey=null;RefreshState();}
            dc.PushTransform(new TranslateTransform((ActualWidth-diameter)/2,(ActualHeight-diameter)/2));dc.PushTransform(new ScaleTransform(diameter/128,diameter/128));
            double time=phase,click=ClickStrength();
            double power=Math.Min(1,Math.Max(energy,Math.Max(hover*.88,Math.Max(press*.95,click))));
            // Stronger elastic feedback affects the colored rings only. The native window and text
            // never rotate or resize, so dragging, edge contact and percentage reading stay stable.
            double bounce=Math.Sin((clock.Elapsed.TotalSeconds-pulseAt)*15)*click;
            dc.PushTransform(new TranslateTransform(offsetX*.7,offsetY*.7));
            dc.PushTransform(new ScaleTransform(1-.065*press+.012*bounce,1-.025*press-.012*bounce,64,64));
            dc.PushTransform(new RotateTransform(power*6*Math.Sin(time)+bounce*8,64,64));
            DrawChassis(dc,time,power);
            if(VisibleRingCount==0)
            {
                dc.PushOpacity(.5);dc.DrawGeometry(null,unknownStroke,power>.003?WaveArc(52.5,0,1,time,power):Arc(52.5,1));dc.Pop();
            }
            else for(int i=0;i<2;i++)if(rings[i].Known)DrawRing(dc,rings[i],VisibleRingCount==1?0:rings[1-i].Focus,time,power);
            DrawParticles(dc,time,power);
            dc.Pop();dc.Pop();dc.Pop();
            double ripple=(clock.Elapsed.TotalSeconds-pulseAt)/.8;
            Brush pulseColor=ShortKnown?rings[0].Color:LongKnown?rings[1].Color:Theme.Muted;
            if(ripple>=0&&ripple<1){dc.PushOpacity(.7*(1-ripple));dc.DrawEllipse(null,new Pen(pulseColor,1.4),new Point(64,64),39+23*ripple,39+23*ripple);dc.Pop();}
            // Only live task evidence lights this beacon; hovering alone must not claim a task is running.
            if(activeUntil>LocalCodexUsage.Unix(DateTime.Now)){dc.PushOpacity(.15);dc.DrawEllipse(Theme.Ink,null,new Point(64,30),4,4);dc.Pop();dc.PushOpacity(.85);dc.DrawEllipse(Theme.Ink,null,new Point(64,30),1.5,1.5);dc.Pop();}
            if(VisibleRingCount==1)
            {
                var r=ShortKnown?rings[0]:rings[1];DrawCentered(dc,r.Caption,37,68);DrawCentered(dc,r.Number,51,74);
            }
            else if(VisibleRingCount==2)
            {
                // Just two generously separated rows; omit the redundant third "remaining" row.
                for(int i=0;i<2;i++)
                {
                    var r=rings[i];if(r.Number==null)continue;
                    double width=r.Caption.Width+8+r.Number.Width,x=64-width/2,y=i==0?37:65;
                    double scale=Math.Min(1,58/width);dc.PushTransform(new ScaleTransform(scale,scale,64,y+13));
                    dc.PushOpacity(1-.2*Math.Max(0,rings[1-i].Focus-r.Focus));
                    dc.DrawText(r.Caption,new Point(x,y+10));dc.DrawText(r.Number,new Point(x+r.Caption.Width+8,y));dc.Pop();dc.Pop();
                }
            }
            else
            {
                DrawCentered(dc,label,39,74);
                if(rings[0].Number!=null)DrawCentered(dc,rings[0].Number,58,64);
            }
            dc.Pop();dc.Pop();
        }
        private static void DrawCentered(DrawingContext dc,FormattedText text,double y,double maxWidth)
        {
            if(text==null)return;double scale=Math.Min(1,maxWidth/Math.Max(1,text.Width));
            dc.PushTransform(new ScaleTransform(scale,scale,64,y+text.Height/2));dc.DrawText(text,new Point(64-text.Width/2,y));dc.Pop();
        }
        public void Dispose(){disposed=true;Stop();}
    }
}
