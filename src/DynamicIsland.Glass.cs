using System;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace CodexUserData
{
    internal sealed partial class DynamicIsland
    {
        // Animate the painted capsule inside one native envelope. Resizing a transparent
        // HWND every frame makes Windows reallocate its surface and can expose blank frames.
        internal static readonly DependencyProperty DisplayWidthProperty=DependencyProperty.Register("DisplayWidth",typeof(double),typeof(DynamicIsland),new FrameworkPropertyMetadata(0d,FrameworkPropertyMetadataOptions.AffectsRender));
        internal static readonly DependencyProperty DisplayHeightProperty=DependencyProperty.Register("DisplayHeight",typeof(double),typeof(DynamicIsland),new FrameworkPropertyMetadata(0d,FrameworkPropertyMetadataOptions.AffectsRender));
        private static readonly DependencyProperty ExpansionProperty=DependencyProperty.Register("Expansion",typeof(double),typeof(DynamicIsland),new FrameworkPropertyMetadata(0d,FrameworkPropertyMetadataOptions.AffectsRender));
        private static readonly DependencyProperty PressProperty=DependencyProperty.Register("Press",typeof(double),typeof(DynamicIsland),new FrameworkPropertyMetadata(0d,FrameworkPropertyMetadataOptions.AffectsRender));
        internal double RenderWidth {get{double v=(double)GetValue(DisplayWidthProperty);return v>0?v:ActualWidth;}}
        internal double RenderHeight {get{double v=(double)GetValue(DisplayHeightProperty);return v>0?v:ActualHeight;}}
        private Brush glassBody,glassReflection,glassInner,glassInk,glassMuted,glassTrack;
        private Pen glassEdge,glassLip,glassInset,glassRingPen,glassTrackPen,glyphPen;
        private FormattedText glassTokens,glassCaption,glassState;
        private FormattedText[] ringValues=new FormattedText[0],ringLabels=new FormattedText[0];
        private Geometry[] ringArcs=new Geometry[0];
        private readonly ScaleTransform pressScale=new ScaleTransform();
        private readonly TranslateTransform[] ringPositions={new TranslateTransform(),new TranslateTransform()};
        private readonly StreamGeometry glassOutline=new StreamGeometry();
        private int pressGeneration;

        internal Size StopMorph()
        {
            var current=new Size(RenderWidth,RenderHeight);double detail=(double)GetValue(ExpansionProperty);
            BeginAnimation(DisplayWidthProperty,null);BeginAnimation(DisplayHeightProperty,null);BeginAnimation(ExpansionProperty,null);
            SetValue(DisplayWidthProperty,current.Width);SetValue(DisplayHeightProperty,current.Height);SetValue(ExpansionProperty,detail);return current;
        }
        internal void SetPresentationSize(double width,double height)
        {StopMorph();SetValue(DisplayWidthProperty,width);SetValue(DisplayHeightProperty,height);SetValue(ExpansionProperty,expanded?1d:0d);}
        internal void MorphTo(Size from,Size target,Action completed)
        {
            double detail=(double)GetValue(ExpansionProperty);StopMorph();
            int fps=Theme.ActivityFrameRate(preferences.OrbAnimation);
            if(!Theme.MotionAllowed||fps==0){SetPresentationSize(target.Width,target.Height);completed();return;}
            var duration=Theme.MotionTime(target.Width>from.Width?340:280);
            // Separate content and geometry clocks let numbers stay full size during the morph.
            AnimatePaint(DisplayWidthProperty,from.Width,target.Width,duration,fps,null);
            AnimatePaint(ExpansionProperty,detail,expanded?1:0,duration,fps,null);
            AnimatePaint(DisplayHeightProperty,from.Height,target.Height,duration,fps,completed);
        }
        private void AnimatePaint(DependencyProperty property,double from,double to,Duration duration,int fps,Action completed)
        {
            var animation=new DoubleAnimation(from,to,duration){EasingFunction=new CubicEase{EasingMode=EasingMode.EaseOut}};
            Timeline.SetDesiredFrameRate(animation,fps);var clock=(AnimationClock)animation.CreateClock(true);
            if(completed!=null)clock.Completed+=delegate{if(!disposed)completed();};
            ApplyAnimationClock(property,clock,HandoffBehavior.SnapshotAndReplace);clock.Controller.Begin();clock.Controller.SeekAlignedToLastTick(TimeSpan.Zero,TimeSeekOrigin.BeginTime);
        }
        internal void SetPressed(bool pressed)
        {
            int generation=++pressGeneration;double from=(double)GetValue(PressProperty),to=pressed?1d:0d;BeginAnimation(PressProperty,null);SetValue(PressProperty,to);
            if(!disposed&&IsVisible&&Theme.MotionAllowed&&Math.Abs(from-to)>.001)AnimatePaint(PressProperty,from,to,Theme.MotionTime(pressed?85:160),Theme.MotionFrameRate,delegate{if(generation==pressGeneration){BeginAnimation(PressProperty,null);SetValue(PressProperty,to);}});
        }
        private static LinearGradientBrush GlassGradient(params string[] stops)
        {
            var brush=new LinearGradientBrush{StartPoint=new Point(.2,0),EndPoint=new Point(.8,1)};
            for(int i=0;i<stops.Length;i++)brush.GradientStops.Add(new GradientStop(ColorOf(stops[i]),(double)i/(stops.Length-1)));
            brush.Freeze();return brush;
        }
        private void BuildGlassPaint()
        {
            bool light=Theme.IsLight;
            // Translucency and reflected highlights are native vector layers. No desktop
            // capture, backdrop polling or expensive per-frame blur is used on low-end PCs.
            glassBody=light?GlassGradient("#D5FFFFFF","#B5EAF0F5","#90DAE4EC","#D8F6FAFC"):GlassGradient("#F0090C10","#E0101419","#CF161D24","#E0262E38");
            glassReflection=light?GlassGradient("#90FFFFFF","#10FFFFFF","#00FFFFFF","#50FFFFFF"):GlassGradient("#3FFFFFFF","#02FFFFFF","#00FFFFFF","#18D7E6F0");
            glassInner=light?Theme.B("#80FFFFFF"):Theme.B("#50101922");
            glassEdge=new Pen(GlassGradient(light?"#D0FFFFFF":"#E5F0F5F8",light?"#7095A5B4":"#3873808E",light?"#60B1BECB":"#46566371",light?"#C0FFFFFF":"#DCF4F8FC"),1.6);
            glassLip=new Pen(GlassGradient("#D0FFFFFF","#00FFFFFF","#06FFFFFF","#92FFFFFF"),.75);
            glassInset=new Pen(light?Theme.B("#3D7D94A9"):Theme.B("#85000000"),1);
            glassInk=light?Theme.B("#253141"):Theme.B("#F1F5F9");glassMuted=light?Theme.B("#536579"):Theme.B("#B8C3CF");glassTrack=light?Theme.B("#28708CA0"):Theme.B("#38DAE6EF");
            glassRingPen=new Pen(OrbPalette.Create(EffectiveColors(preferences),35,true),2.6){StartLineCap=PenLineCap.Round,EndLineCap=PenLineCap.Round};
            glassTrackPen=new Pen(glassTrack,2.6);glyphPen=new Pen(glassMuted,2){StartLineCap=PenLineCap.Round,EndLineCap=PenLineCap.Round};
        }
        private void UpdateGlassData(string token,QuotaWindow[] windows,long now)
        {
            if(glassInk==null)return;
            glassTokens=Text(token,21,glassInk,semibold);glassCaption=Text("今日 Tokens",9,glassMuted,regular);
            string shortState=running?activity.ActiveTasks+" 项":completionPending?"已完成":QuotaStatus.ActivityLabel(activity,false,now)=="当前空闲"?"":"待确认";
            glassState=Text(shortState,9,glassMuted,regular);
            ringValues=windows.Select(w=>Text(w.RemainingPercent(bucket,now).HasValue?w.RemainingPercent(bucket,now).Value.ToString("0")+"%":"—",10,glassInk,semibold)).ToArray();
            ringLabels=windows.Select(w=>Text(w.Label,8,glassMuted,regular)).ToArray();
            ringArcs=windows.Select(w=>QuotaArc(w.RemainingPercent(bucket,now))).ToArray();
        }
        private static Geometry QuotaArc(double? percent)
        {
            var geometry=new StreamGeometry();double portion=percent.HasValue?Math.Max(0,Math.Min(1,percent.Value/100)):0;
            if(portion>=.9999){var circle=new EllipseGeometry(new Point(0,0),15,15);circle.Freeze();return circle;}
            if(portion>0)using(var path=geometry.Open())
            {double angle=portion*Math.PI*2;path.BeginFigure(new Point(0,-15),false,false);path.ArcTo(new Point(15*Math.Sin(angle),-15*Math.Cos(angle)),new Size(15,15),0,portion>.5,SweepDirection.Clockwise,true,false);}
            geometry.Freeze();return geometry;
        }
        private void DrawGlass(DrawingContext dc)
        {
            if(glassTokens==null)return;
            double width=RenderWidth,height=RenderHeight,phase=(double)GetValue(PhaseProperty),breath=(double)GetValue(BreathProperty),detail=(double)GetValue(ExpansionProperty);
            double press=(double)GetValue(PressProperty),wave=running||completionPending?Math.Sin(phase*2*Math.PI)*.55:0;
            pressScale.CenterX=width/2;pressScale.CenterY=height/2;pressScale.ScaleX=1-.012*press;pressScale.ScaleY=1-.035*press;dc.PushTransform(pressScale);
            var bounds=new Rect(4,4+wave,width-8,height-8);double radius=bounds.Height/2;
            using(var path=glassOutline.Open())
            {
                double l=bounds.Left,r=bounds.Right,t=bounds.Top,b=bounds.Bottom;
                path.BeginFigure(new Point(l+radius,t),true,true);path.LineTo(new Point(r-radius,t),true,false);path.ArcTo(new Point(r-radius,b),new Size(radius,radius),0,false,SweepDirection.Clockwise,true,false);path.LineTo(new Point(l+radius,b),true,false);path.ArcTo(new Point(l+radius,t),new Size(radius,radius),0,false,SweepDirection.Clockwise,true,false);
            }
            dc.DrawGeometry(glassBody,glassEdge,glassOutline);dc.PushClip(glassOutline);dc.DrawRectangle(glassReflection,null,bounds);
            var inset=new Rect(bounds.Left+2,bounds.Top+2,bounds.Width-4,bounds.Height-4);
            dc.DrawRoundedRectangle(null,glassInset,inset,radius-2,radius-2);inset.Inflate(-.8,-.8);dc.DrawRoundedRectangle(null,glassLip,inset,radius-2.8,radius-2.8);
            // A broad moving reflection and coloured edge light carry the activity. The
            // quota rings stay truthful; their lengths never animate as fake task progress.
            if(running||completionPending)
            {
                Color tint=colors[Math.Min(1,colors.Length-1)];tint.A=(byte)(18+30*breath);innerBrush.GradientStops[0].Color=tint;
                innerBrush.Center=innerBrush.GradientOrigin=new Point(.5+.38*Math.Cos(phase*2*Math.PI),.5+.30*Math.Sin(phase*2*Math.PI));dc.DrawRectangle(innerBrush,null,bounds);
                int count=currentFps<=30?Math.Min(2,rimPens.Length):rimPens.Length;
                for(int i=0;i<count;i++){double a=phase*2*Math.PI+i*2*Math.PI/count;int k=i*rimPens.Length/count;rimBrushes[k].Center=rimBrushes[k].GradientOrigin=new Point(.5+.44*Math.Cos(a),.5+.42*Math.Sin(a));rimBrushes[k].Opacity=.45+.25*breath;rimPens[k].Thickness=2.1;dc.DrawGeometry(null,rimPens[k],glassOutline);}
            }
            dc.Pop();
            double centerY=height/2,tokenX=52;
            DrawActivityGlyph(dc,28,centerY,phase,breath);
            // Compact mode is a single centred row. Labels appear only as it expands;
            // interpolate the existing text position instead of scaling the typography.
            if(detail>.01){dc.PushOpacity(detail);glassCaption.MaxTextWidth=90;dc.DrawText(glassCaption,new Point(tokenX,centerY-20));dc.DrawText(glassState,new Point(122,centerY-20));dc.Pop();}
            glassTokens.MaxTextWidth=Math.Max(55,width-(ringValues.Length>1?130:108));
            double compactTokenY=centerY-glassTokens.Height/2;
            dc.DrawText(glassTokens,new Point(tokenX,compactTokenY+(centerY-7-compactTokenY)*detail));
            if(ringValues.Length==0){dc.DrawText(unavailable,new Point(width-100,centerY-7));}
            else
            {
                int main=ringValues.Length-1;double ringY=centerY-5*detail;
                DrawQuotaRing(dc,main,width-36-6*detail,ringY,1,detail);
                if(ringValues.Length>1&&detail>.55)DrawQuotaRing(dc,0,width-36-60*detail,ringY,(detail-.55)/.45,detail);
            }
            dc.Pop();
        }
        private void DrawQuotaRing(DrawingContext dc,int index,double x,double y,double opacity,double detail)
        {
            dc.PushOpacity(opacity);var position=ringPositions[index];position.X=x;position.Y=y;dc.PushTransform(position);
            dc.DrawEllipse(null,glassTrackPen,new Point(0,0),15,15);dc.DrawGeometry(null,glassRingPen,ringArcs[index]);
            dc.DrawText(ringValues[index],new Point(-ringValues[index].Width/2,-ringValues[index].Height/2));dc.Pop();
            if(detail>.01){dc.PushOpacity(detail);dc.DrawText(ringLabels[index],new Point(x-ringLabels[index].Width/2,y+17));dc.Pop();}dc.Pop();
        }
        private void DrawActivityGlyph(DrawingContext dc,double x,double y,double phase,double breath)
        {
            var glow=running||completionPending?glassRingPen.Brush:glassMuted;
            dc.DrawEllipse(glassInner,null,new Point(x,y),14,14);
            var pen=glyphPen;pen.Brush=glow;
            if(completionPending&&!running){dc.DrawLine(pen,new Point(x-5,y),new Point(x-1,y+4));dc.DrawLine(pen,new Point(x-1,y+4),new Point(x+6,y-5));return;}
            for(int i=0;i<4;i++){double amplitude=running?3+5*(.5+.5*Math.Sin(phase*2*Math.PI+i*.9)):2+(i==1||i==2?3:0);double px=x-6+i*4;dc.DrawLine(pen,new Point(px,y-amplitude),new Point(px,y+amplitude));}
        }
    }
}
