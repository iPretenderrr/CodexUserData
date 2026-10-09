using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;

namespace CodexUserData
{
    internal sealed class BallPlacement
    {
        public double X {get;set;} public double Y {get;set;} public string Dock {get;set;}
        internal static BallPlacement Capture(Rect bounds,Rect work,string dock)
        {return new BallPlacement{X=Theme.Bound((bounds.Left-work.Left)/Math.Max(1,work.Width-bounds.Width),0,1,0),Y=Theme.Bound((bounds.Top-work.Top)/Math.Max(1,work.Height-bounds.Height),0,1,0),Dock=dock};}
        internal Rect Restore(Size pixels,Rect work)
        {return WindowInteraction.FitBounds(new Rect(work.Left+X*Math.Max(0,work.Width-pixels.Width),work.Top+Y*Math.Max(0,work.Height-pixels.Height),pixels.Width,pixels.Height),work,0);}
    }
    internal sealed class Preferences
    {
        public string App {get;set;} public string Range {get;set;} public string Database {get;set;}
        public bool Pinned {get;set;} public bool Collapsed {get;set;} public double Left {get;set;} public double Top {get;set;}
        public RemoteOptions Remote {get;set;}
        public string UsageView {get;set;}
        public long MilestoneStep {get;set;}
        public string Source {get;set;} public string CodexHome {get;set;}
        public double Width {get;set;} public double Height {get;set;} public double Opacity {get;set;}
        public int RefreshSeconds {get;set;} public string[] Metrics {get;set;}
        public bool ShowHeatmap {get;set;} public bool ShowTrend {get;set;} public int TrendDays {get;set;} public int CacheTrendDays {get;set;} public string TrendAggregation {get;set;} public string HeatmapAggregation {get;set;}
        public bool ShowQuota {get;set;} public bool LiveQuota {get;set;} public bool ShowModels {get;set;} public string QuotaCli {get;set;}
        public Dictionary<string,decimal[]> PriceOverrides {get;set;}
        public string[] KnownModels {get;set;}
        public bool BallMode {get;set;} public bool BallExpanded {get;set;}
        public string BallStyle {get;set;} public string OrbQuotaWindow {get;set;} public string OrbAnimation {get;set;}
        public double OrbSize {get;set;}
        public double AnimationSpeed {get;set;}
        public double BallEffectIntensity {get;set;}
        // Strength changes visual amplitude, never frame rate or animation duration.
        internal double EffectStrength {get{return Clamp(BallEffectIntensity,.5,3,1);}}
        public string[] OrbShortColors {get;set;} public string[] OrbLongColors {get;set;}
        public double OrbShortAngle {get;set;} public double OrbLongAngle {get;set;}
        public bool StartWithWindows {get;set;} public bool StartWithCodex {get;set;}
        public bool StartupAnimationEnabled {get;set;} public string StartupAnimationStyle {get;set;}
        public bool StartupSoundEnabled {get;set;}
        public double BallOpacity {get;set;} public bool OrbFollowTheme {get;set;}
        public bool IslandFollowTheme {get;set;} public string[] IslandColors {get;set;} public string IslandMaterial {get;set;}
        public string CustomShape {get;set;}
        public bool CompletionFlash {get;set;}
        public bool CliNoticeShown {get;set;}
        public bool MinimizeToTray {get;set;} // Missing in older settings defaults to taskbar (false).
        public double BallLeft {get;set;} public double BallTop {get;set;} public string BallDock {get;set;}
        public bool BallPositionLocked {get;set;}
        public string BallMonitor {get;set;}
        public Dictionary<string,BallPlacement> BallPlacements {get;set;}
        public Dictionary<string,double[]> CustomShapeSizes {get;set;}
        public string ThemeMode {get;set;} public string ThemeBase {get;set;} public string GradientKind {get;set;} public string GradientSpread {get;set;}
        public double GradientSpan {get;set;}
        public string[] GradientColors {get;set;} public double[] GradientStops {get;set;}
        public double GradientAngle {get;set;} public double GradientCenterX {get;set;} public double GradientCenterY {get;set;} public double GradientRadius {get;set;}
        public double GradientStrength {get;set;} public double ThemeCardOpacity {get;set;}
        public Preferences()
        {
            string user=Environment.GetEnvironmentVariable("USERPROFILE") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            BallLeft=Double.NaN;BallTop=Double.NaN;BallDock="";BallEffectIntensity=1;
            StartupAnimationEnabled=true;StartupAnimationStyle="cinema";StartupSoundEnabled=true;
            BallMonitor="";BallPlacements=new Dictionary<string,BallPlacement>(StringComparer.OrdinalIgnoreCase);CustomShapeSizes=new Dictionary<string,double[]>(StringComparer.OrdinalIgnoreCase);
            BallOpacity=1;OrbFollowTheme=true;IslandFollowTheme=true;IslandMaterial="glass";IslandColors=new[]{"#3976FF","#35C8E2","#87F3BE","#C68BFF","#FF83C4"};CompletionFlash=true;BallStyle="orb";OrbQuotaWindow="auto";OrbAnimation="auto";OrbSize=84;AnimationSpeed=1;
            OrbShortColors=new[]{"#316BF1","#5F97FF","#89CDEC"};OrbLongColors=new[]{"#7965EA","#AB8DF0","#E2B0ED"};OrbShortAngle=OrbLongAngle=45;
            ThemeMode="dark";ThemeBase="auto";GradientKind="linear";GradientSpread="pad";GradientColors=new[]{"#F7BBE3","#E6D7FA","#AAF1ED"};GradientStops=new[]{0d,48d,100d};GradientAngle=120;GradientSpan=100;GradientCenterX=50;GradientCenterY=35;GradientRadius=80;GradientStrength=85;ThemeCardOpacity=82;
            PriceOverrides=new Dictionary<string,decimal[]>();KnownModels=new string[0];
            App="";Range="today";Pinned=true;Left=Double.NaN;Top=Double.NaN;
            Database=Path.Combine(user,".cc-switch","cc-switch.db"); CodexHome=Environment.GetEnvironmentVariable("CODEX_HOME");
            if(String.IsNullOrWhiteSpace(CodexHome))CodexHome=Path.Combine(user,".codex");
            Remote=new RemoteOptions();UsageView="combined";MilestoneStep=MilestoneEngine.Base;
            Source=File.Exists(Database)&&!Directory.Exists(Path.Combine(CodexHome,"sessions"))?"ccswitch":"local";Width=380;Height=440;Opacity=1;RefreshSeconds=5;
            Metrics=new[]{"tokens","requests","cacheRate","cost","input","output"};
            ShowHeatmap=true;ShowTrend=true;TrendDays=30;CacheTrendDays=30;TrendAggregation="daily";HeatmapAggregation="daily";
            ShowQuota=true;LiveQuota=true;ShowModels=true;QuotaCli=""; // Executable discovery belongs to startup, not settings deserialization.
        }
        internal Preferences Clone(){return Program.Json.Deserialize<Preferences>(Program.Json.Serialize(this));}
        internal void Validate()
        {
            Theme.Normalize(this);BallOpacity=Clamp(BallOpacity,.2,1,1);BallEffectIntensity=EffectStrength;
            StartupAnimationStyle=StartupSplash.NormalizeStyle(StartupAnimationStyle);
            NormalizePlacement();
            OrbPalette.Normalize(this);
            IslandColors=OrbPalette.Clean(IslandColors,new[]{"#3976FF","#35C8E2","#87F3BE","#C68BFF","#FF83C4"});
            if(IslandMaterial!="classic")IslandMaterial="glass";
            if(BallStyle!="capsule"&&BallStyle!="island"&&BallStyle!="html")BallStyle="orb";
            if(!new[]{"auto","short","week"}.Contains(OrbQuotaWindow))OrbQuotaWindow="auto";
            if(!new[]{"auto","smooth","eco","off"}.Contains(OrbAnimation))OrbAnimation="auto";
            OrbSize=Clamp(OrbSize,56,128,84);
            if(!new[]{"today","week","month","all"}.Contains(Range))Range="today";
            if(Remote==null)Remote=new RemoteOptions();
            if(UsageView!="local"&&UsageView!="remote")UsageView="combined";
            if(!MilestoneEngine.Steps.Contains(MilestoneStep))MilestoneStep=MilestoneEngine.Base;
            if(Source!="local")Source="ccswitch";
            if(String.IsNullOrWhiteSpace(Database))Database=new Preferences().Database;
            if(String.IsNullOrWhiteSpace(CodexHome))CodexHome=new Preferences().CodexHome;
            if(QuotaCli==null)QuotaCli="";
            PriceOverrides=ApiPrices.Clean(PriceOverrides);KnownModels=(KnownModels??new string[0]).Where(m=>!String.IsNullOrWhiteSpace(m)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            // An unrecognized saved form must not reopen a large or partially docked window.
            if(!new[]{"","left","right","top","bottom"}.Contains(BallDock)){BallDock="";BallExpanded=false;}
            App=App??""; Width=Clamp(Width,260,1100,380);Height=Clamp(Height,280,1000,440);Opacity=Clamp(Opacity,.2,1,1);
            RefreshSeconds=Math.Max(2,Math.Min(3600,RefreshSeconds));
            Metrics=(Metrics??new string[0]).Where(k=>Theme.MetricLabels.ContainsKey(k)).Distinct().ToArray();
            if(Metrics.Length==0)Metrics=new[]{"tokens"};
            if(!new[]{1,7,14,30,60,90,180}.Contains(TrendDays))TrendDays=30;
            if(!new[]{1,7,14,30,60,90,180}.Contains(CacheTrendDays))CacheTrendDays=30;
            if(TrendAggregation!="weekly"&&TrendAggregation!="cumulative")TrendAggregation="daily";
            if(HeatmapAggregation!="weekly"&&HeatmapAggregation!="cumulative")HeatmapAggregation="daily";
        }
        private static double Clamp(double v,double min,double max,double fallback){return Double.IsNaN(v)||Double.IsInfinity(v)?fallback:Math.Max(min,Math.Min(max,v));}
        internal static string ShapeSizeKey(string relative)
        {
            if(String.IsNullOrWhiteSpace(relative)||relative.Length>240||Path.IsPathRooted(relative))return null;
            var parts=relative.Replace('\\','/').Split('/');
            if(parts.Any(p=>p.Length==0||p=="."||p==".."||p.IndexOfAny(Path.GetInvalidFileNameChars())>=0))return null;
            return String.Join("/",parts);
        }
        internal void NormalizePlacement()
        {
            if(String.IsNullOrEmpty(BallMonitor)||BallMonitor.Length>128||BallMonitor.Any(Char.IsControl))BallMonitor="";
            var monitors=new Dictionary<string,BallPlacement>(StringComparer.OrdinalIgnoreCase);
            foreach(var pair in BallPlacements??new Dictionary<string,BallPlacement>())
            {
                if(monitors.Count>=8)break;
                if(String.IsNullOrWhiteSpace(pair.Key)||pair.Key.Length>128||pair.Key.Any(Char.IsControl)||pair.Value==null)continue;
                monitors[pair.Key]=new BallPlacement{X=Clamp(pair.Value.X,0,1,0),Y=Clamp(pair.Value.Y,0,1,0),Dock=new[]{"left","right","top","bottom"}.Contains(pair.Value.Dock)?pair.Value.Dock:""};
            }
            BallPlacements=monitors;
            var sizes=new Dictionary<string,double[]>(StringComparer.OrdinalIgnoreCase);
            foreach(var pair in CustomShapeSizes??new Dictionary<string,double[]>())
            {
                if(sizes.Count>=32)break;string key=ShapeSizeKey(pair.Key);
                if(key==null||pair.Value==null||pair.Value.Length!=2)continue;
                sizes[key]=new[]{Clamp(pair.Value[0],64,800,240),Clamp(pair.Value[1],40,600,90)};
            }
            CustomShapeSizes=sizes;
        }
        internal bool RememberShapeSize(string relative,double width,double height)
        {
            string key=ShapeSizeKey(relative);if(key==null)return false;
            if(CustomShapeSizes==null)CustomShapeSizes=new Dictionary<string,double[]>(StringComparer.OrdinalIgnoreCase);
            width=Clamp(width,64,800,240);height=Clamp(height,40,600,90);double[] previous;
            if(CustomShapeSizes.TryGetValue(key,out previous)&&previous!=null&&previous.Length==2&&Math.Abs(previous[0]-width)<.5&&Math.Abs(previous[1]-height)<.5)return false;
            if(!CustomShapeSizes.ContainsKey(key)&&CustomShapeSizes.Count>=32)CustomShapeSizes.Remove(CustomShapeSizes.Keys.First());
            CustomShapeSizes[key]=new[]{width,height};return true;
        }
    }
}
