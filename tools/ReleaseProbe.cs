using System;
using System.IO;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Text;
using System.Runtime.InteropServices;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
internal static class ReleaseProbe
{
 static Assembly assembly;static string map;static int checks;
 [DllImport("winsqlite3.dll",EntryPoint="sqlite3_open_v2",CallingConvention=CallingConvention.Cdecl)] static extern int Open(byte[] path,out IntPtr db,int flags,IntPtr vfs);
 [DllImport("winsqlite3.dll",EntryPoint="sqlite3_exec",CallingConvention=CallingConvention.Cdecl)] static extern int Exec(IntPtr db,byte[] sql,IntPtr callback,IntPtr arg,out IntPtr error);
 [DllImport("winsqlite3.dll",EntryPoint="sqlite3_close",CallingConvention=CallingConvention.Cdecl)] static extern int CloseDb(IntPtr db);
 static Type TypeFor(string name){var m=Regex.Match(map,@"^\[CodexUserData\]CodexUserData\."+Regex.Escape(name)+@" -> \[CodexUserData\](.+)$",RegexOptions.Multiline);if(!m.Success)throw new Exception("Missing obfuscation map for "+name);return assembly.GetType(m.Groups[1].Value.Trim(),true);}
 static object New(string name,params object[] args){return Activator.CreateInstance(TypeFor(name),BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic,null,args,null);}
 static object Call(string type,object instance,string method,params object[] args)
 {
  var m=Regex.Match(map,@"CodexUserData\."+Regex.Escape(type)+"::"+Regex.Escape(method)+@"\["+args.Length+@"\][^\r\n]*? -> ([^\r\n]+)");string renamed=m.Success?m.Groups[1].Value.Trim():method;
  var found=TypeFor(type).GetMethods(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static).Where(x=>!x.ContainsGenericParameters&&x.IsStatic==(instance==null)&&x.Name==renamed&&x.GetParameters().Length==args.Length&&x.GetParameters().Select((p,i)=>args[i]==null?!p.ParameterType.IsValueType:p.ParameterType.IsInstanceOfType(args[i])).All(b=>b)).Select(x=>new{Method=x,Exact=x.GetParameters().Select((p,i)=>args[i]!=null&&p.ParameterType==args[i].GetType()?1:0).Sum()}).ToArray();
  if(found.Length==0)throw new Exception("Missing protected method: "+type+"."+method);
  // Obfuscation reuses names: prefer Scan(DateTime) to a timer callback accepting object.
  return found.Where(x=>x.Exact==found.Max(y=>y.Exact)).Single().Method.Invoke(instance,args);
 }
 static object Get(object o,string key){return o.GetType().GetProperty(key).GetValue(o,null);}
 static T InternalField<T>(string type,object instance,string key)
 {
  var m=Regex.Match(map,@"CodexUserData\."+Regex.Escape(type)+"::"+Regex.Escape(key)+@" -> ([^\r\n]+)");
  return (T)instance.GetType().GetFields(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic).Single(f=>f.Name==m.Groups[1].Value.Trim()&&f.FieldType==typeof(T)).GetValue(instance);
 }
 static void ActivityRegression(string dir,JavaScriptSerializer json)
 {
  string home=Path.Combine(dir,"activity150"),logs=Path.Combine(home,"sessions");Directory.CreateDirectory(logs);string file=Path.Combine(logs,"rollout-large.jsonl");var start=DateTime.UtcNow;
  Func<string,string,DateTime,string> ev=(kind,id,at)=>json.Serialize(new{type="event_msg",timestamp=at.ToString("o"),payload=new{type=kind,turn_id=id}})+"\n";
  string meta=json.Serialize(new{type="session_meta",timestamp=start.AddMinutes(-1).ToString("o"),payload=new{id="large",timestamp=start.AddMinutes(-1).ToString("o"),instructions=new string('x',65536)}})+"\n";
  File.WriteAllText(file,meta+new string(' ',600000)+"\n"+ev("task_started","work",start));var monitor=New("CodexActivity",home,false);
  try{
   var report=Call("CodexActivity",monitor,"Scan",start);Check(InternalField<int>("ActivityReport",report,"ActiveTasks")==1,"protected monitor reads a 64 KiB header in a large session");Check(InternalField<int>("ActivityReport",report,"CompletedTasks")==0,"protected monitor never flashes during bootstrap");
   File.AppendAllText(file,ev("task_complete","work",start.AddSeconds(1)));Call("CodexActivity",monitor,"Queue",file);report=Call("CodexActivity",monitor,"Scan",start.AddSeconds(1));Check(InternalField<int>("ActivityReport",report,"CompletedTasks")==1&&InternalField<long>("ActivityReport",report,"CompletionSerial")==1,"protected completion emits a deduplicated sequence");
   report=Call("CodexActivity",monitor,"Scan",start.AddSeconds(2));Check(InternalField<int>("ActivityReport",report,"CompletedTasks")==0,"protected completion never repeats on later scans");
   File.AppendAllText(file,ev("task_started","abort",start.AddSeconds(3))+ev("turn_aborted","abort",start.AddSeconds(4)));Call("CodexActivity",monitor,"Queue",file);report=Call("CodexActivity",monitor,"Scan",start.AddSeconds(4));Check(InternalField<int>("ActivityReport",report,"CompletedTasks")==0,"protected abort never appears as a successful completion");
   string child=Path.Combine(logs,"rollout-review.jsonl");File.WriteAllText(child,json.Serialize(new{type="session_meta",timestamp=start.ToString("o"),payload=new{id="review",parent_thread_id="large",thread_source="guardian_review"}})+"\n"+ev("task_started","review",start.AddSeconds(5)));Call("CodexActivity",monitor,"Queue",child);report=Call("CodexActivity",monitor,"Scan",start.AddSeconds(5));Check(InternalField<int>("ActivityReport",report,"ActiveTasks")==0,"protected background reviews do not activate user-task effects");
   Call("CodexActivity",monitor,"Scan",start.AddSeconds(6));Check((long)Call("CodexActivity",monitor,"get_LastBytesRead")==0,"protected idle scans read zero log bytes");
   File.AppendAllText(file,ev("task_started","large-final",start.AddSeconds(7)));Call("CodexActivity",monitor,"Queue",file);Call("CodexActivity",monitor,"Scan",start.AddSeconds(7));
   var largeJson=new JavaScriptSerializer{MaxJsonLength=8*1024*1024};File.AppendAllText(file,largeJson.Serialize(new{type="event_msg",timestamp=start.AddSeconds(8).ToString("o"),payload=new{last_agent_message=new string('x',3*1024*1024),type="task_complete",turn_id="large-final"}})+"\n");Call("CodexActivity",monitor,"Queue",file);report=Call("CodexActivity",monitor,"Scan",start.AddSeconds(8));Check(InternalField<int>("ActivityReport",report,"CompletedTasks")==1,"protected streaming projection reads lifecycle fields after a multi-megabyte final answer");
  }finally{Call("CodexActivity",monitor,"Dispose");}
 }
 static void Set(object o,string key,object value){o.GetType().GetProperty(key).SetValue(o,value,null);}
 static void Check(bool ok,string message){if(!ok)throw new Exception(message);checks++;Console.WriteLine("PASS "+message);}
 [StructLayout(LayoutKind.Sequential)] struct WindowRect {public int Left,Top,Right,Bottom;}
 [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr window,out WindowRect rect);
 [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr window,uint message,IntPtr w,IntPtr l);
 static bool ClearCorners(Window window,int width,int height)
 {
  var content=(FrameworkElement)window.Content;content.Measure(new Size(width,height));content.Arrange(new Rect(0,0,width,height));content.UpdateLayout();
  // Include the Window background: rendering only Content misses the original rectangular halo.
  var surface=new DrawingVisual();using(var dc=surface.RenderOpen()){dc.DrawRectangle(window.Background,null,new Rect(0,0,width,height));dc.DrawRectangle(new VisualBrush(content),null,new Rect(0,0,width,height));}
  foreach(double scale in new[]{1d,1.5d,2d}){int w=(int)(width*scale),h=(int)(height*scale);var bitmap=new RenderTargetBitmap(w,h,96*scale,96*scale,PixelFormats.Pbgra32);bitmap.Render(surface);
   foreach(var point in new[]{new[]{0,0},new[]{w-1,0},new[]{0,h-1},new[]{w-1,h-1}}){var pixel=new byte[4];bitmap.CopyPixels(new Int32Rect(point[0],point[1],1,1),pixel,4,0);if(pixel[3]!=0)return false;}
  }return true;
 }
 static bool ResizeEdges(Window window)
 {
  IntPtr hwnd=new System.Windows.Interop.WindowInteropHelper(window).Handle;WindowRect r;if(!GetWindowRect(hwnd,out r))return false;
  var source=System.Windows.Interop.HwndSource.FromHwnd(hwnd);int inset=(int)Math.Ceiling(5*source.CompositionTarget.TransformToDevice.M11),midX=(r.Left+r.Right)/2,midY=(r.Top+r.Bottom)/2;
  foreach(var p in new[]{new[]{r.Left+inset,midY,10},new[]{r.Right-inset,midY,11},new[]{midX,r.Top+inset,12},new[]{midX,r.Bottom-inset,15},new[]{r.Left+inset,r.Top+inset,13},new[]{r.Right-inset,r.Top+inset,14},new[]{r.Left+inset,r.Bottom-inset,16},new[]{r.Right-inset,r.Bottom-inset,17}})
  {int packed=(p[0]&0xffff)|((p[1]&0xffff)<<16);if(SendMessage(hwnd,0x84,IntPtr.Zero,new IntPtr(packed)).ToInt32()!=p[2])return false;}return true;
 }
 [STAThread] static int Main(string[] args)
 {
  try{
   AppDomain.CurrentDomain.SetData("CodexUserData.TestDataFolder",Path.Combine(Path.GetFullPath(args[2]),"fixture-user"));
   assembly=Assembly.LoadFrom(Path.GetFullPath(args[0]));map=File.ReadAllText(args[1]);string dir=args[2];Directory.CreateDirectory(dir);
   Check(assembly.GetType("CodexUserData.WidgetWindow")==null&&Regex.Matches(map,@"^\[CodexUserData\].+ -> \[CodexUserData\]",RegexOptions.Multiline).Count>20,"implementation types are renamed");
   var json=new JavaScriptSerializer();var prefs=New("Preferences");Set(prefs,"MinimizeToTray",true);Set(prefs,"PriceOverrides",new Dictionary<string,decimal[]>{{"fixture-model",new[]{1m,.1m,0m,2m}}});
   string settings=json.Serialize(prefs);var clone=Call("Preferences",prefs,"Clone");Check(settings.Contains("\"MinimizeToTray\":true")&&(bool)Get(clone,"MinimizeToTray")&&((IDictionary)Get(clone,"PriceOverrides")).Contains("fixture-model"),"settings and custom-price schema survive obfuscation");
   string home=Path.Combine(dir,"fixture"),logs=Path.Combine(home,"sessions"),cache=Path.Combine(dir,"cache.gz");Directory.CreateDirectory(logs);string id="00000000-0000-0000-0000-000000000001";string time=DateTime.UtcNow.AddMinutes(-1).ToString("o");
   var counters=new{input_tokens=100,cached_input_tokens=20,output_tokens=10,reasoning_output_tokens=3,total_tokens=110};
   string meta=json.Serialize(new{type="session_meta",timestamp=time,payload=new{id=id,timestamp=time}});
   string usage=json.Serialize(new{type="event_msg",timestamp=time,payload=new{type="token_count",info=new{total_token_usage=counters,last_token_usage=counters}}});File.WriteAllText(Path.Combine(logs,"rollout-test-"+id+".jsonl"),meta+"\n"+usage+"\n"+usage+"\n");
   var reader=New("LocalCodexUsage",home,cache,false);var snapshot=Call("LocalCodexUsage",reader,"Read","today",DateTime.Now,null,null);
   Check((long)Get(snapshot,"TotalTokens")==110&&(long)Get(snapshot,"Requests")==1,"actual obfuscated ledger deduplicates usage and preserves totals");
   Call("LocalCodexUsage",reader,"SaveCache",true);reader=New("LocalCodexUsage",home,cache,false);snapshot=Call("LocalCodexUsage",reader,"Read","today",DateTime.Now,null,null);
   Check((long)Get(snapshot,"TotalTokens")==110&&File.Exists(cache),"serialized cache remains readable by a new reader");
   var reply=json.DeserializeObject("{\"rateLimits\":{\"primary\":{\"usedPercent\":7,\"windowDurationMins\":300,\"resetsAt\":9999999999}}}");var quotas=(IList)Call("QuotaReader",null,"ParseResult",reply,100L);Check(quotas.Count==1&&(double)Get(Get(quotas[0],"Primary"),"UsedPercent")==7,"quota JSON still parses with expected keys");
   string dbPath=Path.Combine(dir,"fixture.db");IntPtr db,error;
   if(Open(Encoding.UTF8.GetBytes(dbPath+"\0"),out db,6,IntPtr.Zero)!=0)throw new Exception("SQLite fixture open failed");
   try{if(Exec(db,Encoding.UTF8.GetBytes("CREATE TABLE proxy_request_logs(model TEXT); CREATE TABLE usage_daily_rollups(model TEXT); INSERT INTO proxy_request_logs VALUES('fixture-model');\0"),IntPtr.Zero,IntPtr.Zero,out error)!=0)throw new Exception("SQLite fixture setup failed");}finally{CloseDb(db);}
   var models=(IEnumerable<string>)Call("UsageDatabase",null,"ModelCatalog",dbPath);Check(models.Contains("fixture-model"),"obfuscated native SQLite calls can read the fixture database");
   var version=assembly.GetName().Version;Check(version.Major==1,"obfuscated assembly metadata is valid");
   Set(prefs,"BallStyle","html");Set(prefs,"CustomShape",@"skins\fixture\shape.json");Set(prefs,"StartWithWindows",true);var portablePrefs=Call("Preferences",prefs,"Clone");Check((string)Get(portablePrefs,"BallStyle")=="html"&&(string)Get(portablePrefs,"CustomShape")==@"skins\fixture\shape.json"&&(bool)Get(portablePrefs,"StartWithWindows"),"HTML form and startup preferences survive obfuscation");
   var zero=New("ModelUsage");Set(zero,"Model","fixture-no-price");Call("ModelUsage",zero,"Add",10L,20L,30L,40L,1L);Check((decimal)Get(zero,"EquivalentUsd")==0&&(long)Get(zero,"UnpricedTokens")==0&&(string)Call("ModelColors",null,"Money",0m,100L,100L)=="$0.00","protected pricing treats missing rates as zero without unpriced labels");
   string shape=Path.Combine(dir,"shape");Directory.CreateDirectory(shape);File.WriteAllText(Path.Combine(shape,"index.html"),"<!doctype html><b>fixture</b>");File.WriteAllText(Path.Combine(shape,"shape.json"),"{\"apiVersion\":1,\"name\":\"fixture\",\"entry\":\"index.html\",\"width\":240,\"height\":80}");var manifest=Call("ShapeManifest",null,"Read",Path.Combine(shape,"shape.json"));Check((int)Get(manifest,"apiVersion")==1&&(double)Get(manifest,"width")==240,"protected HTML manifest preserves its public JSON schema");
   Check((bool)Call("CustomShapeView",null,"Allowed","https://shape.codexuserdata.local/index.html")&&!(bool)Call("CustomShapeView",null,"Allowed","file:///C:/outside"),"protected HTML origin checks remain active");
   string legacy=Path.Combine(dir,"legacy-data"),portable=Path.Combine(dir,"portable-data");Directory.CreateDirectory(legacy);File.WriteAllText(Path.Combine(legacy,"widget-settings.json"),"{\"Width\":350}");Call("PortableStore",null,"Initialize",portable,legacy);File.WriteAllText(Path.Combine(portable,"widget-settings.json"),"keep-new");Call("PortableStore",null,"Initialize",portable,legacy);Check(File.ReadAllText(Path.Combine(portable,"widget-settings.json"))=="keep-new"&&File.ReadAllText(Path.Combine(legacy,"widget-settings.json"))=="{\"Width\":350}","protected migration preserves existing portable data and legacy originals");
   Set(prefs,"BallStyle","orb");Set(prefs,"StartWithWindows",false);
   string migrated=Path.Combine(dir,"user-migration"),prior=Path.Combine(dir,"portable-migration");Directory.CreateDirectory(migrated);Directory.CreateDirectory(Path.Combine(prior,"skins","demo"));
   string userSettings=Path.Combine(migrated,"widget-settings.json"),portableSettings=Path.Combine(prior,"widget-settings.json");
   File.WriteAllText(userSettings,"{\"Width\":300}");File.SetLastWriteTimeUtc(userSettings,DateTime.UtcNow.AddHours(-2));File.WriteAllText(portableSettings,"{\"Width\":450,\"CustomShape\":\"skins/demo/shape.json\"}");
   File.WriteAllText(Path.Combine(prior,"skins","demo","shape.json"),"{\"apiVersion\":1}");File.WriteAllText(Path.Combine(prior,"skins","demo","index.html"),"<b>keep shape</b>");
   Call("PortableStore",null,"Initialize",migrated,prior);
   Check(File.ReadAllText(userSettings).Contains("450")&&File.Exists(portableSettings),"newer portable preferences migrate into user storage without deleting originals");
   Check(Directory.GetFiles(Path.Combine(migrated,"migration-backups"),"widget-settings.json",SearchOption.AllDirectories).Any(f=>File.ReadAllText(f).Contains("300")),"replaced user preferences receive a recoverable migration backup");
   Check(File.ReadAllText(Path.Combine(migrated,"skins","demo","index.html"))=="<b>keep shape</b>","custom shape assets follow their relative preferences into user storage");
   File.WriteAllText(portableSettings,"{\"Width\":800}");Call("PortableStore",null,"Initialize",migrated,prior);Check(File.ReadAllText(userSettings).Contains("450"),"completed migration cannot roll back user data from a later portable launch");
   string badSource=Path.Combine(dir,"damaged-portable"),safeTarget=Path.Combine(dir,"safe-user");Directory.CreateDirectory(badSource);Directory.CreateDirectory(safeTarget);File.WriteAllText(Path.Combine(safeTarget,"widget-settings.json"),"{\"Width\":410}");File.SetLastWriteTimeUtc(Path.Combine(safeTarget,"widget-settings.json"),DateTime.UtcNow.AddHours(-2));File.WriteAllText(Path.Combine(badSource,"widget-settings.json"),"{broken");bool rejected=false;try{Call("PortableStore",null,"Initialize",safeTarget,badSource);}catch(TargetInvocationException e){rejected=e.InnerException is IOException;}Check(rejected&&File.ReadAllText(Path.Combine(safeTarget,"widget-settings.json")).Contains("410"),"damaged portable settings cannot replace working user preferences");
   string absent=Path.Combine(dir,"absent-portable"),laterUser=Path.Combine(dir,"later-user");Call("PortableStore",null,"Initialize",laterUser,absent);Check(!File.Exists(Path.Combine(laterUser,"storage-user-v2.json")),"running from a new folder does not prevent a later first portable migration");
   new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};Set(prefs,"Source","local");Set(prefs,"Width",600d);Set(prefs,"Height",700d);
   var window=(Window)New("WidgetWindow",prefs,true);Call("WidgetWindow",window,"ApplySnapshot",snapshot);var body=(FrameworkElement)window.Content;body.Measure(new Size(600,700));body.Arrange(new Rect(0,0,600,700));body.UpdateLayout();
   var bitmap=new RenderTargetBitmap(600,700,96,96,PixelFormats.Pbgra32);bitmap.Render(body);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using(var f=File.Create(Path.Combine(dir,"obfuscated-ui.png")))encoder.Save(f);
   Check(body.ActualWidth==600&&body.ActualHeight==700,"actual obfuscated WPF view renders successfully");
   var settingsWindow=(Window)New("SettingsWindow",prefs,null);Check(settingsWindow.Content!=null,"settings UI opens with compatible cloned preferences");
   var settingsFields=settingsWindow.GetType().GetFields(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic);
   IDictionary settingsPages=(IDictionary)settingsFields.Single(f=>f.FieldType==typeof(Dictionary<string,ScrollViewer>)).GetValue(settingsWindow),settingsNavigation=(IDictionary)settingsFields.Single(f=>f.FieldType==typeof(Dictionary<string,Button>)).GetValue(settingsWindow);
   bool categories=settingsPages.Count==5&&settingsNavigation.Count==5;
   foreach(string key in new[]{"appearance","floating","display","data","behavior"})
   {
    Call("SettingsWindow",settingsWindow,"SelectPage",key);
    categories=categories&&settingsPages.Values.Cast<ScrollViewer>().Count(x=>x.Visibility==Visibility.Visible)==1&&settingsNavigation.Values.Cast<Button>().Count(x=>(string)x.Tag=="selected")==1;
   }
   Check(categories,"settings are split into five switchable categories");
   Set(prefs,"ThemeMode","custom");Set(prefs,"GradientColors",new[]{"#F7BBE3","#E6D7FA","#AAF1ED"});Set(prefs,"GradientStops",new[]{0d,48d,100d});Set(prefs,"GradientKind","radial");Set(prefs,"GradientSpan",65d);
   var themeClone=Call("Preferences",prefs,"Clone");Check((string)Get(themeClone,"ThemeMode")=="custom"&&((string[])Get(themeClone,"GradientColors")).Length==3&&(double)Get(themeClone,"GradientSpan")==65,"theme settings survive protected JSON roundtrip");
   Call("Theme",null,"Apply",prefs);body.UpdateLayout();bitmap.Render(body);Set(prefs,"ThemeMode","light");Call("Theme",null,"Apply",prefs);body.UpdateLayout();bitmap.Render(body);
   var themeSettings=(Window)New("SettingsWindow",themeClone,null);var themeBody=(FrameworkElement)themeSettings.Content;themeBody.Measure(new Size(760,720));themeBody.Arrange(new Rect(0,0,760,720));themeBody.UpdateLayout();
   Check(themeBody.ActualWidth==760,"protected theme editor renders and palettes switch after template sealing");
   var flyout=(Window)New("TrayFlyout",new Action(()=>{}),new Action(()=>{}),new Action(()=>{}));Check(flyout.Content!=null,"tray flyout constructs after string hiding");
   Check(ClearCorners(window,600,700)&&ClearCorners(settingsWindow,760,720)&&ClearCorners(flyout,370,610),"main and popup corners are fully transparent at 100/150/200 percent DPI");
   Set(prefs,"BallDock","");Set(prefs,"BallStyle","capsule");var getPrefs=System.Linq.Expressions.Expression.Lambda(typeof(Func<>).MakeGenericType(TypeFor("Preferences")),System.Linq.Expressions.Expression.Constant(prefs)).Compile();
   foreach(bool expanded in new[]{false,true}){Set(prefs,"BallExpanded",expanded);var ball=(Window)New("FloatingBall",getPrefs,new Action(()=>{}),new Action(()=>{}),new Action(()=>{}));Check(ClearCorners(ball,(int)ball.Width,(int)ball.Height),"floating ball corners are fully transparent: "+(expanded?"large":"small"));}
   Set(prefs,"BallStyle","orb");Set(prefs,"OrbQuotaWindow","short");Set(prefs,"OrbSize",100d);Set(prefs,"OrbAnimation","eco");var orbPrefs=Call("Preferences",prefs,"Clone");
   Check((string)Get(orbPrefs,"BallStyle")=="orb"&&(string)Get(orbPrefs,"OrbAnimation")=="eco"&&(double)Get(orbPrefs,"OrbSize")==100,"orb settings survive protected JSON roundtrip");
   Set(prefs,"OrbShortColors",new[]{"#20BBAA","#73DBAD","#AADDEE"});Set(prefs,"OrbLongColors",new[]{"#FA9566","#EFC578"});Set(prefs,"OrbShortAngle",125d);Set(prefs,"OrbLongAngle",70d);var colorClone=Call("Preferences",prefs,"Clone");
   Check(((string[])Get(colorClone,"OrbShortColors")).Length==3&&((string[])Get(colorClone,"OrbLongColors"))[0]=="#FA9566"&&(double)Get(colorClone,"OrbShortAngle")==125,"independent ring gradients survive protected JSON roundtrip");
   var colorEditor=(Window)New("OrbAppearance",prefs);var colorBody=(FrameworkElement)colorEditor.Content;colorBody.Measure(new Size(440,760));colorBody.Arrange(new Rect(0,0,440,760));colorBody.UpdateLayout();
   var colorBitmap=new RenderTargetBitmap(440,760,96,96,PixelFormats.Pbgra32);colorBitmap.Render(colorBody);var colorPng=new PngBitmapEncoder();colorPng.Frames.Add(BitmapFrame.Create(colorBitmap));using(var f=File.Create(Path.Combine(dir,"orb-colors.png")))colorPng.Save(f);
   Check(colorBody.ActualWidth==440&&ClearCorners(colorEditor,440,760),"protected color editor renders with transparent styled corners");
   var orbWindow=(Window)New("FloatingBall",getPrefs,new Action(()=>{}),new Action(()=>{}),new Action(()=>{}));Set(quotas[0],"ObservedAt",DateTimeOffset.Now.ToUnixTimeSeconds());Call("FloatingBall",orbWindow,"Apply",snapshot,quotas[0],"fixture","fixture");
   Check(orbWindow.Width==100&&orbWindow.Height==100&&ClearCorners(orbWindow,100,100),"protected circular orb renders with fully transparent corners");
   var orb=((Border)orbWindow.Content).Child;Check(System.Windows.Automation.AutomationProperties.GetName(orb).Contains("93%"),"protected orb center uses actual quota percentage");
   Check(((FrameworkElement)orb).ToolTip==null&&!ToolTipService.GetIsEnabled(orb),"protected orb suppresses hover tooltip text");
   var weekly=New("QuotaWindow");Set(weekly,"Minutes",10080);Set(weekly,"UsedPercent",27d);Set(weekly,"ResetsAt",9999999999L);Set(quotas[0],"Secondary",weekly);Call("FloatingBall",orbWindow,"Apply",snapshot,quotas[0],"fixture","fixture");
   string dualName=System.Windows.Automation.AutomationProperties.GetName(orb);Check(dualName.Contains("5h 93%")&&dualName.Contains("7d 73%"),"protected dual rings keep both quota windows independent");
   var shortQuota=Get(quotas[0],"Primary");Set(quotas[0],"Primary",null);Call("FloatingBall",orbWindow,"Apply",snapshot,quotas[0],"fixture","fixture");
   string singleName=System.Windows.Automation.AutomationProperties.GetName(orb);Check((int)Call("QuotaOrb",orb,"get_VisibleRingCount")==1&&singleName.Contains("7d 73%")&&!singleName.Contains("5h")&&ClearCorners(orbWindow,100,100),"protected weekly-only quota renders a centered single ring without a 5h placeholder");
   Set(quotas[0],"Secondary",null);Call("FloatingBall",orbWindow,"Apply",snapshot,quotas[0],"fixture","fixture");
   Check((int)Call("QuotaOrb",orb,"get_VisibleRingCount")==0&&System.Windows.Automation.AutomationProperties.GetName(orb).Contains("待更新"),"protected unavailable quota renders one neutral waiting state");
   Set(quotas[0],"Secondary",weekly);for(int step=0;step<8;step++){Set(quotas[0],"Primary",step%2==0?null:shortQuota);Call("FloatingBall",orbWindow,"Apply",snapshot,quotas[0],"fixture","fixture");}
   Check(Object.ReferenceEquals(orb,((Border)orbWindow.Content).Child)&&orbWindow.Width==100&&(int)Call("QuotaOrb",orb,"get_VisibleRingCount")==2,"quota availability transitions preserve the same orb and native window size");
   int windowCount=Application.Current.Windows.Count;Call("QuotaOrb",orb,"SetPressed",true);Call("QuotaOrb",orb,"SetPressed",false);Call("QuotaOrb",orb,"Pulse");Check(Application.Current.Windows.Count==windowCount,"protected orb click feedback creates no details popup");
   Call("FloatingBall",orbWindow,"SetExpanded",true);Call("FloatingBall",orbWindow,"SetOrb");Check(orbWindow.Width==100,"protected orb and capsule transitions restore the chosen diameter");
   var latestTask=DateTime.UtcNow.AddSeconds(-2).ToString("o");string fixtureLog=Path.Combine(logs,"rollout-test-"+id+".jsonl");
   File.AppendAllText(fixtureLog,json.Serialize(new{type="event_msg",timestamp=latestTask,payload=new{type="task_started"}})+"\n");var active=Call("LocalCodexUsage",reader,"Read","today",DateTime.Now,null,null);
   Check((int)Get(active,"ActiveTasks")==1&&(long)Get(active,"TotalTokens")==110,"protected activity parser preserves token accounting");
   var liveActivity=New("CodexActivity",home,false);var liveReport=Call("CodexActivity",liveActivity,"Scan",DateTime.Now);var liveOrb=((Border)orbWindow.Content).Child;Call("QuotaOrb",liveOrb,"ApplyActivity",liveReport);
   Check(System.Windows.Automation.AutomationProperties.GetItemStatus(liveOrb)=="任务活动中","protected independent event monitor drives activity without a usage refresh");
   File.AppendAllText(fixtureLog,json.Serialize(new{type="event_msg",timestamp=latestTask,payload=new{type="task_complete"}})+"\n");active=Call("LocalCodexUsage",reader,"Read","today",DateTime.Now,null,null);Check((int)Get(active,"ActiveTasks")==0,"protected activity parser handles completion");
   liveReport=Call("CodexActivity",liveActivity,"Scan",DateTime.Now.AddSeconds(6));Call("QuotaOrb",liveOrb,"ApplyActivity",liveReport);Check(System.Windows.Automation.AutomationProperties.GetItemStatus(liveOrb)=="空闲","protected independent monitor clears a completed task");Call("CodexActivity",liveActivity,"Dispose");
   ActivityRegression(dir,json);Check((bool)Get(clone,"CompletionFlash"),"protected completion setting defaults on and survives cloning");
   Set(prefs,"BallOpacity",.45);Set(prefs,"OrbFollowTheme",true);Set(prefs,"StartWithCodex",true);var settings160=Call("Preferences",prefs,"Clone");Check((double)Get(settings160,"BallOpacity")==.45&&(bool)Get(settings160,"OrbFollowTheme")&&(bool)Get(settings160,"StartWithCodex"),"protected whole-form opacity, theme-follow and sync-start settings survive cloning");
   Check((bool)Call("CodexLaunchWatcher",null,"ShouldLaunch",false,true,false)&&!(bool)Call("CodexLaunchWatcher",null,"ShouldLaunch",true,true,false),"protected Codex startup respects the launch edge and manual exit");
   window.Show();Check(window.IsVisible,"obfuscated native window initialization succeeds");Check(ResizeEdges(window),"four edges and four visible corners retain native resize hit tests");window.Close();
   File.WriteAllText(Path.Combine(dir,"verification.json"),"{\"passed\":true,\"checks\":"+checks+"}");Console.WriteLine("PASS "+checks+" protected-release checks");return 0;
  }catch(Exception e){while(e.InnerException!=null)e=e.InnerException;Console.WriteLine(e.GetType().Name+": "+e.Message);return 1;}
 }
}
