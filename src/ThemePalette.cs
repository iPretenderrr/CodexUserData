using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace CodexUserData
{
    internal static partial class Theme
    {
        // A constant binding keeps shared brushes editable when WPF seals control templates.
        // Palette changes recolor existing controls without rebuilding windows or rereading logs.
        private sealed class BrushState {public double UnitOpacity {get{return 1;}}}
        private static readonly BrushState brushState=new BrushState();
        internal static SolidColorBrush LiveColor(string value)
        {
            var b=new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
            BindingOperations.SetBinding(b,Brush.OpacityProperty,new Binding("UnitOpacity"){Source=brushState,Mode=BindingMode.OneWay});return b;
        }
        internal static readonly SolidColorBrush Background=LiveColor("#15191E"),Surface=LiveColor("#21272E"),Hover=LiveColor("#2B3846"),Line=LiveColor("#3C4652"),Ink=LiveColor("#EBF0F5"),Muted=LiveColor("#A2ACB9"),Accent=LiveColor("#82B4E8"),Warning=LiveColor("#F4BB7A"),Scroll=LiveColor("#626F7F"),OnAccent=LiveColor("#122436");
        internal static readonly SolidColorBrush[] HeatLevels={LiveColor("#2B3142"),LiveColor("#35496B"),LiveColor("#4B70A4"),LiveColor("#7098CC"),LiveColor("#A7C8EE")};
        internal static readonly DrawingBrush WindowBackground=LiveDrawing(),Frame=LiveDrawing();
        private static readonly List<WeakReference> themedViews=new List<WeakReference>();
        internal static int Revision {get;private set;}
        internal static bool IsLight {get;private set;}
        private static string motionMode="auto";
        internal static string MotionMode {get{return motionMode;}private set{motionMode=value;}}
        private static double animationSpeed=1;
        internal static double AnimationSpeed {get{return animationSpeed;}private set{animationSpeed=value;}}
        internal static TimeSpan MotionTime(double milliseconds){return TimeSpan.FromMilliseconds(milliseconds/AnimationSpeed);}
        internal static bool MotionAllowed {get{return MotionMode!="off"&&SystemParameters.ClientAreaAnimation&&!SystemParameters.HighContrast&&(RenderCapability.Tier>>16)>0;}}
        internal static int MotionFrameRate {get{return MotionMode=="eco"?30:(RenderCapability.Tier>>16)>=2?60:30;}}
        internal static int ActivityFrameRate(string mode)
        {
            if(mode=="off"||!SystemParameters.ClientAreaAnimation||SystemParameters.HighContrast)return 0;
            if(mode=="eco"||(RenderCapability.Tier>>16)==0)return 30;
            if(mode=="auto"&&System.Windows.Forms.SystemInformation.PowerStatus.PowerLineStatus==System.Windows.Forms.PowerLineStatus.Offline)return 30;
            return 60;
        }
        private static string appliedKey;
        private static DrawingBrush LiveDrawing(){var b=new DrawingBrush{Stretch=Stretch.Fill};BindingOperations.SetBinding(b,Brush.OpacityProperty,new Binding("UnitOpacity"){Source=brushState});return b;}
        internal static void Watch(FrameworkElement view){themedViews.Add(new WeakReference(view));}
        internal static bool ValidHex(string value){return value!=null&&value.Length==7&&value[0]=='#'&&value.Skip(1).All(c=>Uri.IsHexDigit(c));}
        internal static double Bound(double n,double min,double max,double fallback){return Double.IsNaN(n)||Double.IsInfinity(n)?fallback:Math.Max(min,Math.Min(max,n));}
        internal static void Normalize(Preferences p)
        {
            p.AnimationSpeed=Bound(p.AnimationSpeed,.5,2,1);
            if(!new[]{"dark","light","custom"}.Contains(p.ThemeMode))p.ThemeMode="dark";
            if(!new[]{"auto","light","dark"}.Contains(p.ThemeBase))p.ThemeBase="auto";
            if(!new[]{"linear","radial"}.Contains(p.GradientKind))p.GradientKind="linear";
            if(!new[]{"pad","reflect","repeat"}.Contains(p.GradientSpread))p.GradientSpread="pad";
            if(p.GradientColors==null||p.GradientColors.Length==0||p.GradientColors.Any(c=>!ValidHex(c)))p.GradientColors=new[]{"#F7BBE3","#E6D7FA","#AAF1ED"};
            p.GradientColors=p.GradientColors.Take(5).Select(c=>c.ToUpperInvariant()).ToArray();
            if(p.GradientStops==null||p.GradientStops.Length!=p.GradientColors.Length)p.GradientStops=Enumerable.Range(0,p.GradientColors.Length).Select(i=>p.GradientColors.Length==1?0:i*100.0/(p.GradientColors.Length-1)).ToArray();
            p.GradientStops=p.GradientStops.Select(v=>Bound(v,0,100,50)).ToArray();
            p.GradientAngle=Bound(p.GradientAngle,0,360,120);p.GradientSpan=Bound(p.GradientSpan,10,200,100);p.GradientCenterX=Bound(p.GradientCenterX,0,100,50);p.GradientCenterY=Bound(p.GradientCenterY,0,100,40);
            p.GradientRadius=Bound(p.GradientRadius,10,150,80);p.GradientStrength=Bound(p.GradientStrength,0,100,85);p.ThemeCardOpacity=Bound(p.ThemeCardOpacity,55,100,82);
        }
        private static Color ColorOf(string text){return (Color)ColorConverter.ConvertFromString(text);}
        private static Color Mix(Color a,Color b,double f){return Color.FromRgb((byte)Math.Round(a.R+(b.R-a.R)*f),(byte)Math.Round(a.G+(b.G-a.G)*f),(byte)Math.Round(a.B+(b.B-a.B)*f));}
        private static void Set(SolidColorBrush b,string text){b.Color=ColorOf(text);}
        internal static Brush Gradient(Preferences p,bool border)
        {
            Normalize(p);var stops=new GradientStopCollection();Color baseColor=ColorOf(IsLight?"#F5F7FA":"#15191E");
            for(int i=0;i<p.GradientColors.Length;i++)stops.Add(new GradientStop(border?ColorOf(p.GradientColors[i]):Mix(baseColor,ColorOf(p.GradientColors[i]),p.GradientStrength/100),p.GradientStops[i]/100));
            GradientBrush b;
            if(p.GradientKind=="radial")b=new RadialGradientBrush{Center=new Point(p.GradientCenterX/100,p.GradientCenterY/100),GradientOrigin=new Point(p.GradientCenterX/100,p.GradientCenterY/100),RadiusX=p.GradientRadius/100,RadiusY=p.GradientRadius/100};
            else{double a=p.GradientAngle*Math.PI/180,dx=Math.Cos(a),dy=Math.Sin(a),len=.5/Math.Max(Math.Abs(dx),Math.Abs(dy))*p.GradientSpan/100;b=new LinearGradientBrush{StartPoint=new Point(.5-dx*len,.5-dy*len),EndPoint=new Point(.5+dx*len,.5+dy*len)};}
            b.GradientStops=stops;b.SpreadMethod=p.GradientSpread=="repeat"?GradientSpreadMethod.Repeat:p.GradientSpread=="reflect"?GradientSpreadMethod.Reflect:GradientSpreadMethod.Pad;b.Freeze();return b;
        }
        private static void Paint(DrawingBrush b,Brush paint){var d=new GeometryDrawing(paint,null,new RectangleGeometry(new Rect(0,0,1,1)));d.Freeze();b.Drawing=d;}
        internal static void Apply(Preferences p)
        {
            Normalize(p);AnimationSpeed=p.AnimationSpeed;MotionMode=new[]{"auto","smooth","eco","off"}.Contains(p.OrbAnimation)?p.OrbAnimation:"auto";string key=p.ThemeMode+"/"+p.ThemeBase+"/"+p.GradientKind+"/"+p.GradientSpread+"/"+String.Join(",",p.GradientColors)+"/"+String.Join(",",p.GradientStops)+"/"+p.GradientAngle+"/"+p.GradientCenterX+"/"+p.GradientCenterY+"/"+p.GradientRadius+"/"+p.GradientStrength+"/"+p.ThemeCardOpacity+"/"+MotionMode;
            key+="/"+p.GradientSpan;if(key==appliedKey)return;appliedKey=key;bool custom=p.ThemeMode=="custom";
            double luminance=p.GradientColors.Select(c=>{var col=ColorOf(c);return (.2126*col.R+.7152*col.G+.0722*col.B)/255;}).Average();
            IsLight=p.ThemeMode=="light"||(custom&&(p.ThemeBase=="light"||p.ThemeBase=="auto"&&luminance>.6));
            // Neutral surfaces carry the content; blue is reserved for interaction and progress.
            Set(Background,IsLight?"#F1F4F7":"#15191E");Set(Surface,IsLight?"#FFFFFF":"#21272E");Set(Hover,IsLight?"#E7EFF7":"#2B3846");Set(Line,IsLight?"#D4DCE5":"#3C4652");Set(Ink,IsLight?"#232C36":"#EBF0F5");Set(Muted,IsLight?"#606D7B":"#A2ACB9");Set(Accent,IsLight?"#346EA6":"#82B4E8");Set(Warning,IsLight?"#995807":"#F4BB7A");Set(Scroll,IsLight?"#A2AFBD":"#626F7F");Set(OnAccent,IsLight?"#FFFFFF":"#122436");
            // A single blue hue gives the calendar a quiet base while keeping five distinct levels.
            var heat=IsLight?new[]{"#F0F3F8","#DBE8FB","#A7CAF5","#6C9EE8","#477BD4"}:new[]{"#2B3142","#35496B","#4B70A4","#7098CC","#A7C8EE"};for(int i=0;i<5;i++)Set(HeatLevels[i],heat[i]);
            if(custom){var c=Surface.Color;c.A=(byte)Math.Round(p.ThemeCardOpacity*2.55);Surface.Color=c;c=Background.Color;c.A=(byte)Math.Round(Math.Min(100,p.ThemeCardOpacity+8)*2.55);Background.Color=c;Paint(WindowBackground,Gradient(p,false));Paint(Frame,Gradient(p,true));}
            else{var g=new LinearGradientBrush(ColorOf(IsLight?"#FFFFFF":"#20262E"),ColorOf(IsLight?"#F3F6F9":"#11161B"),65);g.Freeze();Paint(WindowBackground,g);Paint(Frame,B(IsLight?"#D4DCE5":"#3C4652"));}
            ModelColors.RefreshTheme();Revision++;
            for(int i=themedViews.Count-1;i>=0;i--){var v=themedViews[i].Target as FrameworkElement;if(v==null)themedViews.RemoveAt(i);else v.InvalidateVisual();}
        }
    }
}
