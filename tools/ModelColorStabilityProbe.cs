using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
namespace CodexUserData
{
 internal static class ModelColorStabilityProbe
 {
  private static void Check(bool value,string message){StabilityProbe.Check(value,"Model colors: "+message);}
  private static void Reject(Action action,string message){bool rejected=false;try{action();}catch(InvalidDataException){rejected=true;}Check(rejected,message);}
  private static ModelPaletteDocument Document(ModelPaletteSnapshot snapshot){return ModelPaletteCatalog.Serializer().Deserialize<ModelPaletteDocument>(snapshot.ToJson());}
  private static ModelPaletteSnapshot Parse(ModelPaletteDocument document){return ModelPaletteCatalog.Parse(ModelPaletteCatalog.Serializer().Serialize(document));}
  private static void RejectChange(ModelPaletteSnapshot basis,Action<ModelPaletteDocument> change,string message){var d=Document(basis);d.revision++;change(d);Reject(()=>Parse(d).ValidateExtension(basis),message);}
  private static void Reset(ModelPaletteSnapshot snapshot)
  {
   // Fixture-only isolation: never load or modify the real user's cache.
   typeof(ModelPaletteCatalog).GetField("current",BindingFlags.Static|BindingFlags.NonPublic).SetValue(null,snapshot);ModelColors.RefreshTheme();
  }
  private static double Separation(string a,string b,bool light){return ModelColorMath.Distance(ModelColorMath.Parse(ModelColors.ColorHex(a,light)),ModelColorMath.Parse(ModelColors.ColorHex(b,light)));}
  internal static void Run(string root)
  {
   Check(Path.GetFullPath(Program.DataFolder)==Path.GetFullPath(root),"all storage tests use the generated fixture directory");
   var bundled=ModelPaletteCatalog.Parse(ModelPaletteCatalog.BundledJson());Reset(bundled);var document=Document(bundled);
   string path=Path.Combine(root,"model-colors.json");File.WriteAllText(path,"{\"schema\":1,\"assignments\":{\"gpt-6.1-sol\":5,\"fixture-old\":12}}");File.WriteAllText(path+".bak","legacy backup fixture");byte[] old=File.ReadAllBytes(path),backup=File.ReadAllBytes(path+".bak");
   Check(ModelIdentity.Canonical(" provider/GPT-06.00-SOL ")=="gpt-6.0-sol"&&ModelIdentity.Canonical("gpt-6.0.0-sol")=="gpt-6.0-sol"&&ModelIdentity.Canonical("gpt-6.0.1-sol")=="gpt-6.0.1-sol","version aliases normalize without losing patch identity");
   Check(ModelIdentity.Family(ModelIdentity.Canonical("provider/gpt-5.8-luna"))==3&&ModelIdentity.Family(ModelIdentity.Canonical("gpt-5.9-codex"))==4&&ModelIdentity.Family(ModelIdentity.Canonical("gpt-50"))==-1,"explicit family overrides GPT-5 fallback");
   Check(document.families["sol"].models.Take(3).SequenceEqual(new[]{"gpt-6.0-sol","gpt-6.1-sol","gpt-5.6-sol"})&&document.families["gpt5"].models.Take(8).SequenceEqual(new[]{"gpt-5.3-codex","gpt-5.3-codex-spark","gpt-5.4","gpt-5.5","gpt-5.6","gpt-5.0","gpt-5.1","gpt-5.2"}),"published Sol and GPT-5 numbering is fixed across major versions and variants");
   foreach(bool light in new[]{false,true})
   {
    for(int f=0;f<5;f++)
    {
     string key=ModelIdentity.Families[f];var family=document.families[key];int count=bundled.RoundCount(f)*10;var colors=Enumerable.Range(0,count).Select(i=>bundled.At(f,i,light)).ToArray();var bg=ModelColorMath.Parse(light?"#E7EFF7":"#2B3846");
     Check(colors.Distinct().Count()==count&&colors.All(c=>ModelColorMath.InFamily(c,f)&&ModelColorMath.Contrast(c,bg)>=4.5),key+" all rounds: unique RGB, family hue and contrast >= 4.5 ("+(light?"light":"dark")+")");
     double min=Double.MaxValue;for(int i=0;i<count;i++)for(int j=Math.Max(0,i-9);j<i;j++)min=Math.Min(min,ModelColorMath.Distance(colors[i],colors[j]));
     Check(min+1e-9>=.05,key+" every sliding ten-model window including round boundaries: minimum "+min.ToString("0.000000",CultureInfo.InvariantCulture));
     Check(family.models.Select((name,index)=>ModelColors.ColorHex(name,light)==ModelColorMath.Hex(colors[index])).All(x=>x),key+" model numbers resolve exactly to fixed palette indices");
     Check(family.models.All(name=>ModelColors.ColorHex("provider/"+name.ToUpperInvariant(),light)==ModelColors.ColorHex(name,light)),key+" provider and case aliases share public numbering");
    }
    double sol=Separation("gpt-6-sol","gpt-6.1-sol",light);Check(sol>=.05,"Sol 6/6.1 actual OKLab distance "+sol.ToString("0.000000",CultureInfo.InvariantCulture)+" ("+(light?"light":"dark")+")");
    string[] common={"gpt-5.3-codex","gpt-5.3-codex-spark","gpt-5.4","gpt-5.5","gpt-5.6"};double near=Double.MaxValue;for(int i=0;i<common.Length;i++)for(int j=0;j<i;j++)near=Math.Min(near,Separation(common[i],common[j],light));Check(near>=.05,"common GPT-5 actual pair minimum "+near.ToString("0.000000",CultureInfo.InvariantCulture));
    Check(new[]{"codex-auto-review","CODEXAUTOREVIEW","codex_auto_review"}.All(n=>ModelColors.ColorHex(n,light)==(light?"#626775":"#ABA9A7")),"automatic review aliases stay gray");
   }
   FillChecks(bundled);
   var unknown=Enumerable.Range(0,40).Select(i=>"gpt-91."+i+"-sol-fixture").Concat(new[]{"fixture-new-a","fixture-new-b"}).ToArray();var before=unknown.ToDictionary(n=>n,n=>ModelColors.ColorHex(n,false));foreach(string n in unknown.Reverse())ModelColors.For(n);
   Check(before.All(p=>ModelColors.ColorHex(p.Key,false)==p.Value)&&before["fixture-new-a"]!=before["fixture-new-b"],"unknown colors are deterministic across discovery order");
   File.WriteAllText(path,"{damaged unrelated legacy registry");Check(before.All(p=>ModelColors.ColorHex(p.Key,false)==p.Value),"corrupt legacy registry cannot affect colors");File.WriteAllBytes(path,old);
   Check(ModelIdentity.Canonical(new string('x',161))=="unknown"&&ModelIdentity.Canonical("bad\u0001model")=="unknown","invalid IDs use neutral fallback");
   RejectChange(bundled,d=>d.families["sol"].dark=d.families["sol"].dark.Reverse().ToArray(),"existing base slots cannot be reordered");
   RejectChange(bundled,d=>d.families["sol"].models=d.families["sol"].models.Reverse().ToArray(),"existing model numbers cannot be reassigned");
   RejectChange(bundled,d=>d.families["sol"].models=d.families["sol"].models.Skip(1).ToArray(),"existing model numbers cannot be removed");
   RejectChange(bundled,d=>d.families["sol"].rounds=null,"missing rounds are rejected");
   RejectChange(bundled,d=>d.families["sol"].rounds[0].dark=new[]{.04,0d,0d},"nonzero base round is rejected");
   RejectChange(bundled,d=>d.families["sol"].dark[0]="#GG0000","invalid RGB values are rejected");
   RejectChange(bundled,d=>d.families["sol"].dark[0]="#00FF00","wrong family hue is rejected");
   RejectChange(bundled,d=>d.families["sol"].models[1]=d.families["sol"].models[0],"duplicate canonical model numbers are rejected");
   RejectChange(bundled,d=>d.families["sol"].models[0]="gpt-6-sol","noncanonical aliases cannot create duplicate numbering");
   RejectChange(bundled,d=>d.revision=bundled.Revision-1,"older revisions are rejected");
   RejectChange(bundled,d=>d.families["sol"].rounds=d.families["sol"].rounds.Take(1).ToArray(),"published rounds cannot be removed");
   RejectChange(bundled,d=>d.families["sol"].rounds[1]=d.families["sol"].rounds[0],"duplicate RGB rounds are rejected");
   RejectChange(bundled,d=>d.families["sol"].rounds[1].dark=new[]{.04,.04,.04},"oversized offsets are rejected");
   Reject(()=>ModelColorMath.Shift(ModelColorMath.Parse("#FFFFFF"),new[]{.01,0d,0d}),"real gamut overflow is rejected rather than clipped");
   int fAppend=Array.FindIndex(ModelIdentity.Families,key=>document.families[key].models.Length<document.families[key].rounds.Length*10);Check(fAppend>=0,"fixture append has a free slot");string keyAppend=ModelIdentity.Families[fAppend],added=fAppend==4?"gpt-5.99-fixture":"gpt-99.1-"+keyAppend;
   var extension=Document(bundled);extension.revision++;extension.families[keyAppend].models=extension.families[keyAppend].models.Concat(new[]{added}).ToArray();var next=Parse(extension);next.ValidateExtension(bundled);
   var same=Document(next);same.revision=bundled.Revision;Reject(()=>Parse(same).ValidateExtension(bundled),"same revision cannot append hidden content");
   var changed=Document(bundled);changed.families["sol"].rounds[1].dark[0]+=.001;Reject(()=>Parse(changed).ValidateExtension(bundled),"same revision cannot alter an existing round");
   CacheChecks(bundled,next);PayloadChecks();var brush=(SolidColorBrush)ModelColors.For("gpt-6.1-sol");
   foreach(string mode in new[]{"dark","light","custom"}){Theme.Apply(new Preferences{ThemeMode=mode,ThemeBase="light",OrbAnimation="off"});Check(Object.ReferenceEquals(brush,ModelColors.For("provider/GPT-6.1-SOL"))&&brush.Color==ModelColorMath.Parse(ModelColors.ColorHex("gpt-6.1-sol",Theme.IsLight)),"shared brushes update in place for "+mode);if(mode!="custom"){CapturePalette(root,mode,bundled);CaptureChart(root,mode);}}
   CommitBoundaryChecks(bundled,next,added);ConfigurationChart(root,bundled,next,added);Check(File.ReadAllBytes(path).SequenceEqual(old)&&File.ReadAllBytes(path+".bak").SequenceEqual(backup)&&Directory.GetFiles(root,"model-colors.json*").Length==2,"all operations leave legacy registry and backup untouched");Reset(bundled);
  }
  private static void FillChecks(ModelPaletteSnapshot snapshot)
  {
   for(int f=0;f<5;f++)
   {
    var basis=Enumerable.Range(0,snapshot.RoundCount(f)*10).Select(i=>snapshot.At(f,i,false)).ToArray();var colors=basis.Select(c=>ModelColors.VividFill(c,f)).ToArray();double min=Double.MaxValue,oldChroma=0,newChroma=0,maxHue=0;
    for(int i=0;i<colors.Length;i++)
    {
     var a=ModelColorMath.Lab(basis[i]);var b=ModelColorMath.Lab(colors[i]);oldChroma+=Math.Sqrt(a[1]*a[1]+a[2]*a[2]);newChroma+=Math.Sqrt(b[1]*b[1]+b[2]*b[2]);double hue=Math.Abs(Math.Atan2(a[2],a[1])-Math.Atan2(b[2],b[1]));maxHue=Math.Max(maxHue,Math.Min(hue,2*Math.PI-hue));
     for(int j=Math.Max(0,i-9);j<i;j++)min=Math.Min(min,ModelColorMath.Distance(colors[i],colors[j]));
    }
    Check(colors.Distinct().Count()==colors.Length&&min>=.05&&maxHue<Math.PI/180&&newChroma>oldChroma,ModelIdentity.Families[f]+" vivid fills: every sliding ten-color window >= .05, perceptual hue within one degree and greater average chroma; minimum "+min.ToString("0.000000",CultureInfo.InvariantCulture));
   }
   Theme.Apply(new Preferences{ThemeMode="dark",OrbAnimation="off"});var fill=(SolidColorBrush)ModelColors.FillFor("gpt-6-sol");Color dark=fill.Color;var gray=(SolidColorBrush)ModelColors.FillFor("codex-auto-review");
   Theme.Apply(new Preferences{ThemeMode="light",OrbAnimation="off"});
   Check(Object.ReferenceEquals(fill,ModelColors.FillFor("provider/GPT-6.0-SOL"))&&fill.Color==dark&&!fill.IsFrozen&&fill.Color!=((SolidColorBrush)ModelColors.For("gpt-6-sol")).Color,"fill aliases reuse a live brush across theme changes while light-theme text retains its readable palette");
   Check(gray.Color==((SolidColorBrush)ModelColors.For("codex-auto-review")).Color,"neutral review fill follows the gray text palette after theme changes");
  }
  // Call before ConfigurationChart; this test restores the in-memory fixture baseline.
  private static void CommitBoundaryChecks(ModelPaletteSnapshot bundled,ModelPaletteSnapshot next,string added)
  {
   string path=ModelPaletteCatalog.CachePath;Reset(bundled);File.WriteAllText(path,bundled.ToJson());byte[] baseline=File.ReadAllBytes(path);
   var brush=(SolidColorBrush)ModelColors.For(added);Color oldColor=brush.Color;int revision=Theme.Revision;
   Func<bool> unchanged=()=>ModelPaletteCatalog.Snapshot.Revision==bundled.Revision&&brush.Color==oldColor&&Theme.Revision==revision&&File.ReadAllBytes(path).SequenceEqual(baseline);
   string staged=(string)PalettePrivate("Stage",next.ToJson());
   try
   {
    using(var cancel=new CancellationTokenSource()){cancel.Cancel();bool cancelled=false;try{PalettePrivate("CommitDownload",next,staged,cancel.Token);}catch(OperationCanceledException){cancelled=true;}Check(cancelled&&unchanged(),"cancellation at UI commit preserves disk cache, snapshot, live brush and revision");}
   }
   finally{if(File.Exists(staged))File.Delete(staged);}
   bool failed=false;try{PalettePrivate("CommitDownload",next,path+".missing-"+Guid.NewGuid().ToString("N"),CancellationToken.None);}catch(IOException){failed=true;}
   Check(failed&&unchanged(),"atomic cache-save failure cannot publish new colors");
   staged=(string)PalettePrivate("Stage",next.ToJson());
   try
   {
    Check((bool)PalettePrivate("CommitDownload",next,staged,CancellationToken.None)&&ModelPaletteCatalog.Snapshot.Revision==next.Revision&&File.ReadAllText(path)==next.ToJson()&&File.ReadAllBytes(path+".bak").SequenceEqual(baseline)&&Object.ReferenceEquals(brush,ModelColors.For(added))&&brush.Color!=oldColor,"successful staged commit atomically publishes cache, backup and existing brush");
   }
   finally{if(File.Exists(staged))File.Delete(staged);}
   byte[] latest=File.ReadAllBytes(path);Color latestColor=brush.Color;revision=Theme.Revision;staged=(string)PalettePrivate("Stage",bundled.ToJson());
   try
   {
    Reject(()=>PalettePrivate("CommitDownload",bundled,staged,CancellationToken.None),"stale queued download is rejected at UI commit");
    Check(ModelPaletteCatalog.Snapshot.Revision==next.Revision&&brush.Color==latestColor&&Theme.Revision==revision&&File.ReadAllBytes(path).SequenceEqual(latest),"stale queued download preserves the newer disk cache and visible colors");
   }
   finally{if(File.Exists(staged))File.Delete(staged);}
   Reset(bundled);
  }
  private static object PalettePrivate(string name,params object[] args)
  {
   try{return typeof(ModelPaletteCatalog).GetMethod(name,BindingFlags.NonPublic|BindingFlags.Static).Invoke(null,args);}
   catch(TargetInvocationException error){throw error.InnerException;}
  }
  private static void CacheChecks(ModelPaletteSnapshot bundled,ModelPaletteSnapshot next)
  {
   string path=ModelPaletteCatalog.CachePath;File.WriteAllText(path,next.ToJson());Reset(bundled);ModelPaletteCatalog.LoadCached();Check(ModelPaletteCatalog.Snapshot.Revision==next.Revision,"valid cached extension loads offline");
   File.WriteAllText(path,"{damaged");File.WriteAllText(path+".bak",next.ToJson());Reset(bundled);ModelPaletteCatalog.LoadCached();Check(ModelPaletteCatalog.Snapshot.Revision==next.Revision,"damaged cache uses valid backup");
   File.WriteAllText(path+".bak","{also damaged");Reset(bundled);ModelPaletteCatalog.LoadCached();Check(ModelPaletteCatalog.Snapshot.Revision==bundled.Revision,"damaged caches fall back to bundled palette");
   File.WriteAllBytes(path,new byte[ModelPaletteCatalog.MaxBytes+1]);Reset(bundled);ModelPaletteCatalog.LoadCached();Check(ModelPaletteCatalog.Snapshot.Revision==bundled.Revision,"oversized cache cannot replace bundled palette");
  }
  private static void PayloadChecks()
  {
   using(var stream=new MemoryStream(new byte[ModelPaletteCatalog.MaxBytes+1]))Reject(()=>ModelPaletteCatalog.ReadResponseAsync(stream,CancellationToken.None).GetAwaiter().GetResult(),"download payload size is bounded");
   byte[] payload=Encoding.UTF8.GetBytes(ModelPaletteCatalog.BundledJson());using(var stream=new MemoryStream(payload))Check(ModelPaletteCatalog.ReadResponseAsync(stream,CancellationToken.None).GetAwaiter().GetResult()==Encoding.UTF8.GetString(payload),"bounded response preserves JSON");
   using(var cancel=new CancellationTokenSource())using(var stream=new MemoryStream(payload)){cancel.Cancel();bool rejected=false;try{ModelPaletteCatalog.ReadResponseAsync(stream,cancel.Token).GetAwaiter().GetResult();}catch(OperationCanceledException){rejected=true;}Check(rejected,"cancelled response is rejected");}
  }
  private static DailyUsage[] Fixture(string[] names,int length){var days=DailyUsage.Empty(new DateTime(2026,9,30),length);for(int day=0;day<days.Length;day++)for(int m=0;m<names.Length;m++){long tokens=100+(long)(70*Math.Sin(day*.15+m));days[day].Models.Add(new ModelUsage{Model=names[m],Tokens=tokens});days[day].Tokens+=tokens;}return days;}
  private static RenderTargetBitmap ChartFrame(UsageChart chart,string path)
  {
   chart.Measure(new Size(900,280));chart.Arrange(new Rect(0,0,900,280));var visual=new DrawingVisual();using(var dc=visual.RenderOpen()){dc.DrawRectangle(Theme.Background,null,new Rect(0,0,900,280));StabilityProbe.Call(chart,"OnRender",dc);}var bitmap=new RenderTargetBitmap(900,280,96,96,PixelFormats.Pbgra32);bitmap.Render(visual);Save(bitmap,path);return bitmap;
  }
  private static void ConfigurationChart(string root,ModelPaletteSnapshot bundled,ModelPaletteSnapshot next,string added)
  {
   Theme.Apply(new Preferences{ThemeMode="dark",OrbAnimation="off"});var brush=(SolidColorBrush)ModelColors.For(added);Color old=brush.Color;var fill=(SolidColorBrush)ModelColors.FillFor(added);Color oldFill=fill.Color;var chart=new UsageChart(true);chart.SetData(Fixture(new[]{added},180),180);var before=ChartFrame(chart,Path.Combine(root,"palette-config-before.png"));var drawing=StabilityProbe.Field<DrawingGroup>(chart,"cachedDrawing");int revision=Theme.Revision;
   Check(drawing.IsFrozen&&ModelPaletteCatalog.Install(next)&&Theme.Revision>revision&&Object.ReferenceEquals(brush,ModelColors.For(added))&&brush.Color!=old,"configuration append updates existing brush and theme revision");
   Check(Object.ReferenceEquals(fill,ModelColors.FillFor(added))&&fill.Color!=oldFill&&!fill.IsFrozen,"configuration append refreshes the existing vivid fill brush without freezing or replacing it");
   var after=ChartFrame(chart,Path.Combine(root,"palette-config-after.png"));var updated=StabilityProbe.Field<DrawingGroup>(chart,"cachedDrawing");var a=new byte[900*280*4];var b=new byte[a.Length];before.CopyPixels(a,900*4,0);after.CopyPixels(b,900*4,0);Check(updated.IsFrozen&&!Object.ReferenceEquals(drawing,updated)&&!a.SequenceEqual(b),"real UsageChart rebuilds frozen model stripes and visibly changes after install");
   revision=Theme.Revision;Check(!ModelPaletteCatalog.Install(next)&&Theme.Revision==revision,"identical revision avoids redundant redraw");Reject(()=>ModelPaletteCatalog.Install(bundled),"install refuses rollback");
  }
  private static void CaptureChart(string root,string theme)
  {
   string[] names={"gpt-5.3-codex","gpt-5.3-codex-spark","gpt-5.4","gpt-5.5","gpt-5.6","gpt-6-sol","gpt-6.1-sol"};var body=new StackPanel();body.Children.Add(Theme.Text("GPT-5 / Sol · 实际线宽 · 模拟数据",15,Theme.Ink));var legend=new WrapPanel{Margin=new Thickness(0,10,0,10)};foreach(var name in names)legend.Children.Add(Theme.LegendChip(name,ModelColors.For(name),null,name));body.Children.Add(legend);var chart=new UsageChart(false){Height=270};body.Children.Add(chart);chart.SetData(Fixture(names,30),30,false,30,false,"daily",false,true);var window=new Window{Width=1060,Height=440,ShowActivated=false,ShowInTaskbar=false,Content=new Border{Background=Theme.Background,Padding=new Thickness(16),Child=body}};try{window.Show();window.UpdateLayout();ModelShareStabilityProbe.Capture(window,Path.Combine(root,"model-colors-chart-"+theme+".png"));}finally{window.Close();}
  }
  private static void CapturePalette(string root,string theme,ModelPaletteSnapshot snapshot)
  {
   var visual=new DrawingVisual();using(var dc=visual.RenderOpen()){dc.DrawRectangle(Theme.Background,null,new Rect(0,0,1240,540));Draw(dc,"固定十色色板 · "+(theme=="dark"?"深色":"浅色")+" · 模拟预览",20,16,Theme.Ink,18);for(int row=0;row<5;row++){double y=65+row*88;Draw(dc,ModelIdentity.Families[row],20,y+13,Theme.Ink,14);for(int col=0;col<10;col++){double x=108+col*112;var color=new SolidColorBrush(snapshot.At(row,col,Theme.IsLight));color.Freeze();dc.DrawRoundedRectangle(Theme.Hover,null,new Rect(x,y,104,66),8,8);dc.DrawRoundedRectangle(color,null,new Rect(x+10,y+11,84,3),1.5,1.5);Draw(dc,"#"+(col+1),x+10,y+23,color,12);Draw(dc,ModelColorMath.Hex(color.Color),x+10,y+43,Theme.Muted,10);}}}var bitmap=new RenderTargetBitmap(1240,540,96,96,PixelFormats.Pbgra32);bitmap.Render(visual);Save(bitmap,Path.Combine(root,"model-families-"+theme+".png"));
  }
  private static void Save(BitmapSource bitmap,string path){var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using(var file=File.Create(path))encoder.Save(file);}
  private static void Draw(DrawingContext dc,string text,double x,double y,Brush color,double size){dc.DrawText(new FormattedText(text,CultureInfo.InvariantCulture,FlowDirection.LeftToRight,new Typeface("Segoe UI"),size,color),new Point(x,y));}
 }
}
