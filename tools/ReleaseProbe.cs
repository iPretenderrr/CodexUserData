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
 static object CallAs(string type,object instance,string method,Type[] signature,params object[] args)
 {
  var m=Regex.Match(map,@"CodexUserData\."+Regex.Escape(type)+"::"+Regex.Escape(method)+@"\["+args.Length+@"\][^\r\n]*? -> ([^\r\n]+)");string renamed=m.Success?m.Groups[1].Value.Trim():method;
  var selected=TypeFor(type).GetMethods(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static).Single(x=>x.IsStatic==(instance==null)&&x.Name==renamed&&x.GetParameters().Select(p=>p.ParameterType).SequenceEqual(signature));
  return selected.Invoke(instance,args);
 }
 static object Get(object o,string key){return o.GetType().GetProperty(key).GetValue(o,null);}
 static T InternalField<T>(string type,object instance,string key)
 {
  var m=Regex.Match(map,@"CodexUserData\."+Regex.Escape(type)+"::"+Regex.Escape(key)+@" -> ([^\r\n]+)");
  return (T)instance.GetType().GetFields(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic).Single(f=>f.Name==m.Groups[1].Value.Trim()&&f.FieldType==typeof(T)).GetValue(instance);
 }
 static void SetInternal<T>(string type,object instance,string key,T value)
 {
  var m=Regex.Match(map,@"CodexUserData\."+Regex.Escape(type)+"::"+Regex.Escape(key)+@" -> ([^\r\n]+)");
  instance.GetType().GetFields(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic).Single(f=>f.Name==m.Groups[1].Value.Trim()&&f.FieldType==typeof(T)).SetValue(instance,value);
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
 static FrameworkElement FindAutomation(DependencyObject root,string id)
 {
  var pending=new Queue<DependencyObject>();pending.Enqueue(root);
  while(pending.Count>0){var node=pending.Dequeue();var element=node as FrameworkElement;if(element!=null&&System.Windows.Automation.AutomationProperties.GetAutomationId(element)==id)return element;for(int i=0;i<VisualTreeHelper.GetChildrenCount(node);i++)pending.Enqueue(VisualTreeHelper.GetChild(node,i));}
  return null;
 }
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
 static byte[] CapsuleFrame(Window ball,FrameworkElement chrome,double phase,double breath,string path)
 {
  // Snapshot the activity layer after opening, independent of the separate reveal animation.
  var content=(FrameworkElement)ball.Content;content.BeginAnimation(UIElement.OpacityProperty,null);content.Opacity=1;content.RenderTransform=Transform.Identity;
  foreach(var field in chrome.GetType().GetFields(BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic))
  {
   var dp=field.GetValue(null) as DependencyProperty;if(dp==null)continue;
   if(dp.Name=="Phase"||dp.Name=="Breath"){chrome.BeginAnimation(dp,null);chrome.SetValue(dp,dp.Name=="Phase"?phase:breath);}
  }
  chrome.UpdateLayout();var bitmap=new RenderTargetBitmap((int)ball.Width,(int)ball.Height,96,96,PixelFormats.Pbgra32);bitmap.Render((Visual)ball.Content);
  var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using(var file=File.Create(path))png.Save(file);
  var pixels=new byte[bitmap.PixelWidth*bitmap.PixelHeight*4];bitmap.CopyPixels(pixels,bitmap.PixelWidth*4,0);return pixels;
 }
 static bool InteriorChanged(byte[] a,byte[] b,int width,int height)
 {
  int changed=0,count=0;for(int y=8;y<height-8;y++)for(int x=12;x<width-12;x++){int n=(y*width+x)*4;count++;if(Math.Abs(a[n]-b[n])+Math.Abs(a[n+1]-b[n+1])+Math.Abs(a[n+2]-b[n+2])>24)changed++;}
  return changed>count*.25;
 }
 static void Pump(int milliseconds)
 {
  var frame=new System.Windows.Threading.DispatcherFrame();var timer=new System.Windows.Threading.DispatcherTimer{Interval=TimeSpan.FromMilliseconds(milliseconds)};
  timer.Tick+=delegate{timer.Stop();frame.Continue=false;};timer.Start();System.Windows.Threading.Dispatcher.PushFrame(frame);
 }
 static bool WaitForUi(Func<bool> ready)
 {
  var clock=System.Diagnostics.Stopwatch.StartNew();while(!ready()&&clock.ElapsedMilliseconds<2500)Pump(40);return ready();
 }
 static void ResetBallPlacement(object prefs,string dock)
 {
  // Every case declares its own initial form. A prior case's SourceInitialized/drag
  // saves per-monitor placement even when its persistence callback is a no-op.
  // Clear only fixture state; production restoration remains exercised separately.
  ((IDictionary)Get(prefs,"BallPlacements")).Clear();Set(prefs,"BallMonitor","");
  Set(prefs,"BallLeft",Double.NaN);Set(prefs,"BallTop",Double.NaN);Set(prefs,"BallDock",dock);
 }
 static void DockRegression(object prefs,Delegate getPrefs,string dir)
 {
  var area=SystemParameters.WorkArea;Set(prefs,"BallStyle","capsule");Set(prefs,"BallDock","");Set(prefs,"BallExpanded",true);Set(prefs,"OrbAnimation","smooth");
  for(int side=0;side<4;side++)
  {
   foreach(double scale in new[]{1d,1.5d,2d})
   {
    var work=new Rect(-1920,0,1920,1080);var dock=(Rect)Call("FloatingBall",null,"DockBounds",side,500d,work,scale);
    Check(work.Contains(dock)&&(side==0?dock.Left==work.Left:side==1?dock.Right==work.Right:side==2?dock.Top==work.Top:dock.Bottom==work.Bottom),"dock endpoint is flush on edge "+side+" at scale "+scale);
   }
   ResetBallPlacement(prefs,"");Set(prefs,"BallExpanded",true);var ball=(Window)New("FloatingBall",getPrefs,new Action(()=>{}),new Action(()=>{}),new Action(()=>{}));ball.Show();
   ball.Left=side==0?area.Left+8:side==1?area.Right-ball.Width-8:area.Left+area.Width/2;
   ball.Top=side==2?area.Top+8:side==3?area.Bottom-ball.Height-8:area.Top+area.Height/2;
   Pump(240);Console.WriteLine("DOCK FIXTURE edge="+side+", pillar="+Call("FloatingBall",ball,"get_IsPillar")+", dock="+Get(prefs,"BallDock")+", remembered="+((IDictionary)Get(prefs,"BallPlacements")).Count+", monitorSet="+!String.IsNullOrEmpty((string)Get(prefs,"BallMonitor"))+", size="+ball.Width+"x"+ball.Height);
   Check(!(bool)Call("FloatingBall",ball,"get_IsPillar")&&ball.Width==340,"edge "+side+" fixture begins as an undocked large capsule");Call("FloatingBall",ball,"SnapToEdge");
   Check(((FrameworkElement)ball.Content).Visibility==Visibility.Hidden,"edge "+side+" morph hides the live source while retaining its snapshot");
   Pump(650);
   Check((bool)Call("FloatingBall",ball,"get_IsPillar")&&((FrameworkElement)ball.Content).Visibility==Visibility.Visible,"edge "+side+" morph settles to a visible dock strip");
   Check(!Application.Current.Windows.Cast<Window>().Any(w=>w.Title=="CodexUserData 吸附过渡"),"edge "+side+" morph releases its overlay");
   ((IDisposable)ball).Dispose();
  }
  ResetBallPlacement(prefs,"");Set(prefs,"BallExpanded",true);var interrupted=(Window)New("FloatingBall",getPrefs,new Action(()=>{}),new Action(()=>{}),new Action(()=>{}));interrupted.Show();interrupted.Left=area.Left+8;interrupted.Top=area.Top+area.Height/2;Pump(240);
  Call("FloatingBall",interrupted,"SnapToEdge");Call("FloatingBall",interrupted,"SetExpanded",false);Pump(600);
  Check(!(bool)Call("FloatingBall",interrupted,"get_IsPillar")&&((FrameworkElement)interrupted.Content).Visibility==Visibility.Visible,"changing form cancels docking without a late callback or hidden content");
  Call("FloatingBall",interrupted,"SnapToEdge");interrupted.Hide();Pump(600);
  Check(!Application.Current.Windows.Cast<Window>().Any(w=>w.Title=="CodexUserData 吸附过渡"),"hiding during docking leaves no overlay");((IDisposable)interrupted).Dispose();
  // Synthetic image only; diagnostic frames never contain a user's session or desktop.
  var drawing=new DrawingVisual();using(var dc=drawing.RenderOpen()){dc.DrawRoundedRectangle(Brushes.CornflowerBlue,null,new Rect(0,0,240,90),16,16);dc.DrawEllipse(Brushes.Aquamarine,null,new Point(70,45),23,23);}
  var image=new RenderTargetBitmap(240,90,96,96,PixelFormats.Pbgra32);image.Render(drawing);image.Freeze();
  for(int side=0;side<4;side++)
  {
   var from=new Rect(90,110,240,90);var target=side==0?new Rect(0,115,6,80):side==1?new Rect(414,115,6,80):side==2?new Rect(170,0,80,6):new Rect(170,304,80,6);
   var morph=(FrameworkElement)New("DockMorph",image,image,from,target,side,64);morph.Width=420;morph.Height=310;
   var property=(DependencyProperty)morph.GetType().GetFields(BindingFlags.Static|BindingFlags.NonPublic|BindingFlags.Public).Single(f=>f.FieldType==typeof(DependencyProperty)).GetValue(null);
   var sheet=new DrawingVisual();using(var dc=sheet.RenderOpen())for(int frame=0;frame<5;frame++)
   {
    double t=frame/4d;morph.SetValue(property,t);morph.Measure(new Size(420,310));morph.Arrange(new Rect(0,0,420,310));morph.UpdateLayout();
    var bitmap=new RenderTargetBitmap(420,310,96,96,PixelFormats.Pbgra32);bitmap.Render(morph);dc.DrawImage(bitmap,new Rect(frame*420,0,420,310));
    var first=(Rect)Call("DockMorph",morph,"SliceBounds",0d,1d,t);Check(first.Width>0&&first.Height>0,"edge "+side+" funnel stays nondegenerate at progress "+t);
   }
   var output=new RenderTargetBitmap(2100,310,96,96,PixelFormats.Pbgra32);output.Render(sheet);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(output));using(var file=File.Create(Path.Combine(dir,"dock-morph-"+side+".png")))png.Save(file);
  }
 }
 static void TransitionRegression(object prefs,Delegate getPrefs)
 {
  Set(prefs,"AnimationSpeed",1.7d);var clone=Call("Preferences",prefs,"Clone");Check(Math.Abs((double)Get(clone,"AnimationSpeed")-1.7)<.001,"animation speed survives protected settings serialization");
  Set(prefs,"AnimationSpeed",2d);Call("Theme",null,"Apply",prefs);Check(((TimeSpan)Call("Theme",null,"MotionTime",400d)).TotalMilliseconds==200,"2x animation speed halves transition duration");
  Set(prefs,"AnimationSpeed",.5d);Call("Theme",null,"Apply",prefs);Check(((TimeSpan)Call("Theme",null,"MotionTime",400d)).TotalMilliseconds==800,"0.5x animation speed doubles transition duration");
  Set(prefs,"AnimationSpeed",Double.NaN);Call("Theme",null,"Normalize",prefs);Check((double)Get(prefs,"AnimationSpeed")==1,"invalid animation speed recovers to the default");
  Set(prefs,"AnimationSpeed",1d);Set(prefs,"OrbAnimation","smooth");Call("Theme",null,"Apply",prefs);
  var a=new Window{Width=340,Height=100,ShowActivated=false,ShowInTaskbar=false,Content=new Border{Background=Brushes.SlateBlue}};
  var b=new Window{Width=500,Height=300,ShowActivated=false,ShowInTaskbar=false,Content=new Border{Background=Brushes.CornflowerBlue}};
  Call("WindowInteraction",null,"EnableMotion",a);Call("WindowInteraction",null,"EnableMotion",b);a.Show();Pump(240);
  int changes=0;Call("WindowInteraction",null,"ChangeShape",a,new Action(()=>{changes++;a.Width=174;}));Check(changes==0,"shape mutation waits for the transparent midpoint");
  Check(WaitForUi(()=>changes==1&&a.Width==174&&((FrameworkElement)a.Content).Opacity==1),"shrink-to-small finishes once and restores visible content");
  Call("WindowInteraction",null,"ChangeShape",a,new Action(()=>{changes++;a.Width=340;}));Pump(450);Check(a.Width==340,"small-to-large expansion reaches the requested size");
  Call("WindowInteraction",null,"ChangeShape",a,new Action(()=>{a.Width=200;}));Call("WindowInteraction",null,"ChangeShape",a,new Action(()=>{a.Width=280;}));Pump(450);Check(a.Width==280,"rapid shape changes honor only the last request");
  Call("WindowInteraction",null,"ChangeShape",a,new Action(()=>{a.Width=400;}));CallAs("WindowInteraction",null,"Hide",new[]{typeof(Window),typeof(Action)},a,null);Pump(450);Check(!a.IsVisible&&a.Width==280,"hiding cancels a pending shape mutation");
  a.Show();Pump(240);int prepared=0;Call("WindowInteraction",null,"ShowFrom",b,a,new Action(()=>{prepared++;}),new Action(()=>{}));
  bool transferred=WaitForUi(()=>!a.IsVisible&&b.IsVisible&&prepared==1&&((FrameworkElement)b.Content).Opacity==1);
  Check(transferred,"floating-to-main transfer leaves only a visible destination (source="+a.IsVisible+", destination="+b.IsVisible+", prepared="+prepared+", opacity="+((FrameworkElement)b.Content).Opacity+")");
  Call("WindowInteraction",null,"ShowFrom",a,b,new Action(()=>{}),new Action(()=>{}));Call("WindowInteraction",null,"ShowFrom",b,a,new Action(()=>{}),new Action(()=>{}));Pump(500);
  Check(b.IsVisible&&!a.IsVisible,"reversing a window transfer cancels the obsolete destination");
  Call("WindowInteraction",null,"ToggleMaximize",b);
  Check(b.WindowState==WindowState.Normal,"maximize waits for the transition midpoint");
  Check(WaitForUi(()=>b.WindowState==WindowState.Maximized&&((FrameworkElement)b.Content).Opacity==1),"maximize transition completes with visible content");
  Call("WindowInteraction",null,"ToggleMaximize",b);
  Check(WaitForUi(()=>b.WindowState==WindowState.Normal&&((FrameworkElement)b.Content).Opacity==1),"restore transition returns to normal bounds");
  Call("WindowInteraction",null,"ToggleMaximize",b);Call("WindowInteraction",null,"CompleteReveal",b);Pump(400);
  Check(b.WindowState==WindowState.Normal&&((FrameworkElement)b.Content).Opacity==1,"starting native interaction cancels a pending maximize");
  Call("WindowInteraction",null,"ShowFrom",a,b,new Action(()=>{}),new Action(()=>{}));CallAs("WindowInteraction",null,"Hide",new[]{typeof(Window),typeof(Action)},a,null);Pump(500);
  Check(!a.IsVisible,"hiding an incoming destination prevents its delayed reappearance");
  Set(prefs,"OrbAnimation","off");Call("Theme",null,"Apply",prefs);b.Show();Call("WindowInteraction",null,"ChangeShape",b,new Action(()=>{b.Width=360;}));Check(b.Width==360&&((FrameworkElement)b.Content).Opacity==1,"disabled animations apply shape changes immediately");a.Close();b.Close();
  Set(prefs,"OrbAnimation","smooth");ResetBallPlacement(prefs,"left");Set(prefs,"BallStyle","capsule");Call("Theme",null,"Apply",prefs);
  var ball=(Window)New("FloatingBall",getPrefs,new Action(()=>{}),new Action(()=>{}),new Action(()=>{}));ball.Show();Pump(240);Call("FloatingBall",ball,"SetExpanded",true);Pump(500);
  Check(!(bool)Call("FloatingBall",ball,"get_IsPillar")&&ball.Width==340&&((FrameworkElement)ball.Content).Opacity==1,"docked strip expands to the large capsule");
  Call("FloatingBall",ball,"SetExpanded",false);Pump(450);Check(ball.Width==174,"large capsule animates back to small");Call("FloatingBall",ball,"SetOrb");Pump(450);Check((bool)Call("FloatingBall",ball,"get_IsOrb"),"capsule expands into the circular form");((IDisposable)ball).Dispose();
 }
 static Button NamedButton(DependencyObject root,string name)
 {
  var button=root as Button;if(button!=null&&System.Windows.Automation.AutomationProperties.GetName(button)==name)return button;
  for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){var found=NamedButton(VisualTreeHelper.GetChild(root,i),name);if(found!=null)return found;}return null;
 }
 static void StabilityRegression(object original)
 {
  var prefs=Call("Preferences",original,"Clone");Set(prefs,"BallMode",false);ResetBallPlacement(prefs,"");Set(prefs,"BallStyle","capsule");Set(prefs,"OrbAnimation","smooth");Set(prefs,"AnimationSpeed",.5d);Set(prefs,"Opacity",.73d);Set(prefs,"Height",480d);
  var main=(Window)New("WidgetWindow",prefs,true);main.Show();Pump(500);main.Hide();
  var getter=System.Linq.Expressions.Expression.Lambda(typeof(Func<>).MakeGenericType(TypeFor("Preferences")),System.Linq.Expressions.Expression.Constant(prefs)).Compile();
  var ball=(Window)New("FloatingBall",getter,new Action(()=>{}),new Action(()=>Call("WidgetWindow",main,"RestoreWindow")),new Action(()=>{}));
  main.GetType().GetFields(BindingFlags.Instance|BindingFlags.NonPublic).Single(f=>f.FieldType==ball.GetType()).SetValue(main,ball);
  var host=(FrameworkElement)main.Content;double firstOpacity=-1;main.IsVisibleChanged+=delegate{if(main.IsVisible)firstOpacity=host.Opacity;};
  foreach(bool large in new[]{false,true})
  {
   Call("FloatingBall",ball,"SetExpanded",large);Set(prefs,"BallMode",true);ball.Show();Pump(500);
   Call("CompletionFeedback",null,"Set",main,true);
   var back=NamedButton((DependencyObject)ball.Content,"返回完整窗口");Check(back!=null,"capsule restore button exists: "+large);
   bool monotonic=true;double last=0;int frames=0;EventHandler observe=delegate{if(main.IsVisible){double now=host.Opacity;if(now+.02<last)monotonic=false;last=now;frames++;}};CompositionTarget.Rendering+=observe;
   try
   {
    back.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    Check(WaitForUi(()=>main.IsVisible&&host.Opacity>0&&host.Opacity<.9),"capsule-to-main has an observable entrance: "+large);
    Check(firstOpacity==0,"main is transparent before its first visible frame: "+large);
    Call("WidgetWindow",main,"RestoreWindow");
    Check(DependencyPropertyHelper.GetValueSource(host,UIElement.OpacityProperty).IsAnimated&&host.Opacity<1,"repeated restore preserves the entrance clock: "+large);
    Call("CompletionFeedback",null,"Set",main,false);
    Check(DependencyPropertyHelper.GetValueSource(host,UIElement.OpacityProperty).IsAnimated,"completion acknowledgment cannot cancel the entrance: "+large);
    Check(WaitForUi(()=>host.Opacity==1&&!ball.IsVisible)&&monotonic&&frames>=3,"capsule-to-main opacity advances without a flash: "+large);
    Check(main.Opacity==.73d,"transition preserves user window transparency: "+large);
   }
   finally{CompositionTarget.Rendering-=observe;}
   Call("CompletionFeedback",null,"Set",main,true);var skin=((System.Windows.Controls.Decorator)host).Child;
   Call("WindowInteraction",null,"ChangeShape",main,new Action(()=>main.Height=500));
   Check(WaitForUi(()=>main.Height==500&&host.Opacity==1)&&DependencyPropertyHelper.GetValueSource(skin,UIElement.OpacityProperty).IsAnimated,"completion keeps blinking after a shape transition: "+large);
   main.Hide();Check(!DependencyPropertyHelper.GetValueSource(skin,UIElement.OpacityProperty).IsAnimated,"hidden completion clock stops: "+large);
   Call("CompletionFeedback",null,"Set",main,false);main.Height=480;
  }
  main.Close();
  var flyout=(Window)New("TrayFlyout",new Action(()=>{}),new Action(()=>{}),new Action(()=>{}));var anchor=new System.Drawing.Point(700,700);
  Call("TrayFlyout",flyout,"Reveal",anchor);Call("TrayFlyout",flyout,"Reveal",anchor);
  Check(WaitForUi(()=>flyout.IsVisible&&flyout.Opacity==1&&((FrameworkElement)flyout.Content).Opacity==1),"repeated first-frame tray reveal cannot leave an invisible popup");
  int dismissed=0;Call("TrayFlyout",flyout,"Dismiss",new Action(()=>dismissed++),false);
  Check(WaitForUi(()=>!flyout.IsVisible)&&dismissed==1,"manual popup dismissal calls its continuation exactly once");flyout.Close();
  Call("Theme",null,"Apply",original);
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
   var reply=json.DeserializeObject("{\"rateLimits\":{\"primary\":{\"usedPercent\":7,\"windowDurationMins\":300,\"resetsAt\":9999999999}}}");var quotas=(IList)Call("QuotaReader",null,"ParseResult",reply,100L);var parsedWindow=Get(quotas[0],"Primary");Check(quotas.Count==1&&(double)Get(parsedWindow,"UsedPercent")==7,"quota JSON still parses with expected keys");
   Check((double?)Call("QuotaWindow",parsedWindow,"RemainingPercent",quotas[0],100L)==93d&&(double?)Call("QuotaWindow",parsedWindow,"RemainingPercent",quotas[0],406L)==null&&(double?)Call("QuotaWindow",parsedWindow,"RemainingPercent",quotas[0],94L)==null,"quota freshness is consistent for valid, stale, and future snapshots");
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
   bool categories=settingsPages.Count==6&&settingsNavigation.Count==6;
   foreach(string key in new[]{"appearance","floating","display","data","behavior","about"})
   {
    Call("SettingsWindow",settingsWindow,"SelectPage",key);
    categories=categories&&settingsPages.Values.Cast<ScrollViewer>().Count(x=>x.Visibility==Visibility.Visible)==1&&settingsNavigation.Values.Cast<Button>().Count(x=>(string)x.Tag=="selected")==1;
   }
   Check(categories,"settings are split into six switchable categories");
   var settingsBody=(FrameworkElement)settingsWindow.Content;settingsBody.Measure(new Size(760,720));settingsBody.Arrange(new Rect(0,0,760,720));settingsBody.UpdateLayout();
   Call("SettingsWindow",settingsWindow,"SelectPage","floating");settingsBody.UpdateLayout();
   var speedControl=FindAutomation(settingsBody,"AnimationSpeed") as Slider;Check(speedControl!=null&&speedControl.Minimum==.5&&speedControl.Maximum==2&&speedControl.IsSnapToTickEnabled,"settings exposes the 0.5x to 2x animation speed control");
   speedControl.BringIntoView();settingsBody.UpdateLayout();
   var motionSettings=new RenderTargetBitmap(760,720,96,96,PixelFormats.Pbgra32);motionSettings.Render(settingsBody);var motionPng=new PngBitmapEncoder();motionPng.Frames.Add(BitmapFrame.Create(motionSettings));using(var file=File.Create(Path.Combine(dir,"animation-settings.png")))motionPng.Save(file);
   var aboutVersion=FindAutomation((DependencyObject)settingsWindow.Content,"AboutVersion") as TextBlock;Check(aboutVersion!=null&&aboutVersion.Text=="v"+assembly.GetName().Version.ToString(3),"about page reports the protected assembly version");
   string diagnostic=(string)Call("Diagnostics",null,"Create",prefs,snapshot,null,quotas[0]);
   Check(diagnostic.Contains("程序版本:")&&!diagnostic.Contains(home)&&!diagnostic.Contains(dir),"protected diagnostics use the reviewed allowlist without local paths");
   var diagnosticWindow=(Window)New("DiagnosticPreviewWindow",diagnostic);string diagnosticFile=Path.Combine(dir,"diagnostic-preview.txt");Call("DiagnosticPreviewWindow",diagnosticWindow,"SavePreview",diagnosticFile);
   Check(File.ReadAllText(diagnosticFile)==diagnostic,"protected diagnostics save the exact reviewed preview");diagnosticWindow.Close();
   string publicTag="v"+assembly.GetName().Version.ToString(3);var release=Call("ReleaseUpdate",null,"Parse",json.Serialize(new{tag_name=publicTag,html_url="https://github.com/iPretenderrr/CodexUserData/releases/tag/"+publicTag,body="<b>Fixture notes</b>",draft=false,prerelease=false}));
   Check(((string)Call("ReleaseInfo",release,"Describe",assembly.GetName().Version)).Contains("已是最新")&&InternalField<string>("ReleaseInfo",release,"Notes")=="<b>Fixture notes</b>","protected update metadata parses numeric versions and preserves plain text notes");
   bool rejectedUpdate=false;try{Call("ReleaseUpdate",null,"Parse",json.Serialize(new{tag_name=publicTag,html_url="https://example.com/"+publicTag}));}catch(TargetInvocationException ex){rejectedUpdate=ex.InnerException is InvalidDataException;}
   Check(rejectedUpdate,"protected update metadata rejects links outside the project");
   Set(prefs,"ThemeMode","custom");Set(prefs,"GradientColors",new[]{"#F7BBE3","#E6D7FA","#AAF1ED"});Set(prefs,"GradientStops",new[]{0d,48d,100d});Set(prefs,"GradientKind","radial");Set(prefs,"GradientSpan",65d);
   var themeClone=Call("Preferences",prefs,"Clone");Check((string)Get(themeClone,"ThemeMode")=="custom"&&((string[])Get(themeClone,"GradientColors")).Length==3&&(double)Get(themeClone,"GradientSpan")==65,"theme settings survive protected JSON roundtrip");
   Call("Theme",null,"Apply",prefs);body.UpdateLayout();bitmap.Render(body);Set(prefs,"ThemeMode","light");Call("Theme",null,"Apply",prefs);body.UpdateLayout();bitmap.Render(body);
   var themeSettings=(Window)New("SettingsWindow",themeClone,null);var themeBody=(FrameworkElement)themeSettings.Content;themeBody.Measure(new Size(760,720));themeBody.Arrange(new Rect(0,0,760,720));themeBody.UpdateLayout();
   Check(themeBody.ActualWidth==760,"protected theme editor renders and palettes switch after template sealing");
   var flyout=(Window)New("TrayFlyout",new Action(()=>{}),new Action(()=>{}),new Action(()=>{}));Check(flyout.Content!=null,"tray flyout constructs after string hiding");
   Set(prefs,"OrbAnimation","smooth");Call("Theme",null,"Apply",prefs);
   var extraQuota=New("QuotaBucket");Set(extraQuota,"Id","fixture-extra");Set(extraQuota,"Name","Fixture extra");Set(extraQuota,"Primary",parsedWindow);
   var trayQuotas=Array.CreateInstance(TypeFor("QuotaBucket"),2);trayQuotas.SetValue(quotas[0],0);trayQuotas.SetValue(extraQuota,1);
   Call("TrayFlyout",flyout,"Apply",trayQuotas,null,"fixture","fixture");Call("TrayFlyout",flyout,"Reveal",new System.Drawing.Point(700,700));Pump(350);
   var trayBody=InternalField<StackPanel>("TrayFlyout",flyout,"body");var extraToggle=trayBody.Children.OfType<Button>().Single();double closedHeight=flyout.ActualHeight;
   extraToggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
   Check(!InternalField<bool>("TrayFlyout",flyout,"otherExpanded"),"tray expansion waits for transparent midpoint");
   Check(WaitForUi(()=>InternalField<bool>("TrayFlyout",flyout,"otherExpanded")&&((FrameworkElement)flyout.Content).Opacity==1)&&flyout.ActualHeight>closedHeight,"tray extra quotas expand and resize the popup");
   extraToggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
   Check(WaitForUi(()=>!InternalField<bool>("TrayFlyout",flyout,"otherExpanded")&&((FrameworkElement)flyout.Content).Opacity==1)&&Math.Abs(flyout.ActualHeight-closedHeight)<1,"tray extra quotas collapse back to their original height");
   extraToggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Call("TrayFlyout",flyout,"Apply",trayQuotas,null,"fixture","refreshed");Pump(400);
   Check(!InternalField<bool>("TrayFlyout",flyout,"otherExpanded")&&((FrameworkElement)flyout.Content).Opacity==1,"refresh during tray expansion cannot mutate detached content");
   flyout.Hide();
   var badge=(System.Drawing.Bitmap)Call("QuotaStatus",null,"CreateBadge","80",themeClone,false,0d);var pulseBadge=(System.Drawing.Bitmap)Call("QuotaStatus",null,"CreateBadge","80",themeClone,true,1d);
   try
   {
    int minX=badge.Width,minY=badge.Height,maxX=-1,maxY=-1;bool pulseDiffers=false;
    for(int y=0;y<badge.Height;y++)for(int x=0;x<badge.Width;x++){if(badge.GetPixel(x,y).A>16){minX=Math.Min(minX,x);minY=Math.Min(minY,y);maxX=Math.Max(maxX,x);maxY=Math.Max(maxY,y);}if(badge.GetPixel(x,y).ToArgb()!=pulseBadge.GetPixel(x,y).ToArgb())pulseDiffers=true;}
    Check(badge.Width==64&&badge.Height==64&&minX<=1&&minY<=1&&maxX>=62&&maxY>=62,"tray badge fills its source canvas for a clearer Windows tray downsample");
    Check(pulseDiffers,"running-task tray badge has a distinct breathing frame");
   }
   finally{badge.Dispose();pulseBadge.Dispose();}
   Check(ClearCorners(window,600,700)&&ClearCorners(settingsWindow,760,720)&&ClearCorners(flyout,370,610),"main and popup corners are fully transparent at 100/150/200 percent DPI");
   Set(prefs,"BallDock","");Set(prefs,"BallStyle","capsule");Set(prefs,"OrbAnimation","smooth");var getPrefs=System.Linq.Expressions.Expression.Lambda(typeof(Func<>).MakeGenericType(TypeFor("Preferences")),System.Linq.Expressions.Expression.Constant(prefs)).Compile();
   var capsuleActivity=New("ActivityReport");SetInternal("ActivityReport",capsuleActivity,"ActiveTasks",1);SetInternal("ActivityReport",capsuleActivity,"Until",DateTimeOffset.Now.ToUnixTimeSeconds()+60);
   foreach(bool expanded in new[]{false,true})
   {
    ResetBallPlacement(prefs,"");Set(prefs,"BallExpanded",expanded);var ball=(Window)New("FloatingBall",getPrefs,new Action(()=>{}),new Action(()=>{}),new Action(()=>{}));Check(ClearCorners(ball,(int)ball.Width,(int)ball.Height),"floating ball corners are fully transparent: "+(expanded?"large":"small"));
    string form=expanded?"large":"small";var chrome=FindAutomation((DependencyObject)ball.Content,"CapsuleActivityChrome");Check(chrome!=null,"full-window activity chrome is shared by "+form+" form");
    ball.Show();Call("FloatingBall",ball,"ApplyActivity",capsuleActivity);Check(chrome.HasAnimatedProperties,"running task animates the full capsule and continuous perimeter: "+form);
    foreach(string theme in new[]{"dark","light"})
    {
     Set(prefs,"ThemeMode",theme);Call("Theme",null,"Apply",prefs);Call("FloatingBall",ball,"Apply",snapshot,quotas[0],"fixture","fixture");
     string prefix=Path.Combine(dir,"capsule-"+form+"-"+theme);
     var low=CapsuleFrame(ball,chrome,.45,0,prefix+"-low.png");var high=CapsuleFrame(ball,chrome,.45,1,prefix+"-high.png");var flow=CapsuleFrame(ball,chrome,.03,1,prefix+"-flow.png");
     Check(InteriorChanged(low,high,(int)ball.Width,(int)ball.Height),"breathing visibly changes the whole "+theme+" "+form+" background");
     Check(InteriorChanged(high,flow,(int)ball.Width,(int)ball.Height),"flow crosses the whole "+theme+" "+form+" background");
    }
    Check(ClearCorners(ball,(int)ball.Width,(int)ball.Height),"active capsule keeps transparent corners: "+form);
    ball.Hide();Check(!chrome.HasAnimatedProperties,"hidden capsule stops animation: "+form);ball.Show();Check(chrome.HasAnimatedProperties,"shown active capsule resumes animation: "+form);
    Call("FloatingBall",ball,"ApplyActivity",New("ActivityReport"));Check(!chrome.HasAnimatedProperties,"idle capsule stops animation: "+form);((IDisposable)ball).Dispose();
   }
   DockRegression(prefs,getPrefs,dir);TransitionRegression(prefs,getPrefs);StabilityRegression(prefs);
   ResetBallPlacement(prefs,"");Set(prefs,"BallStyle","orb");Set(prefs,"OrbQuotaWindow","short");Set(prefs,"OrbSize",100d);Set(prefs,"OrbAnimation","eco");var orbPrefs=Call("Preferences",prefs,"Clone");
   Check((string)Get(orbPrefs,"BallStyle")=="orb"&&(string)Get(orbPrefs,"OrbAnimation")=="eco"&&(double)Get(orbPrefs,"OrbSize")==100,"orb settings survive protected JSON roundtrip");
   Set(prefs,"OrbShortColors",new[]{"#20BBAA","#73DBAD","#AADDEE"});Set(prefs,"OrbLongColors",new[]{"#FA9566","#EFC578"});Set(prefs,"OrbShortAngle",125d);Set(prefs,"OrbLongAngle",70d);var colorClone=Call("Preferences",prefs,"Clone");
   Check(((string[])Get(colorClone,"OrbShortColors")).Length==3&&((string[])Get(colorClone,"OrbLongColors"))[0]=="#FA9566"&&(double)Get(colorClone,"OrbShortAngle")==125,"independent ring gradients survive protected JSON roundtrip");
   var colorEditor=(Window)New("OrbAppearance",prefs);var colorBody=(FrameworkElement)colorEditor.Content;colorBody.Measure(new Size(440,760));colorBody.Arrange(new Rect(0,0,440,760));colorBody.UpdateLayout();
   var colorBitmap=new RenderTargetBitmap(440,760,96,96,PixelFormats.Pbgra32);colorBitmap.Render(colorBody);var colorPng=new PngBitmapEncoder();colorPng.Frames.Add(BitmapFrame.Create(colorBitmap));using(var f=File.Create(Path.Combine(dir,"orb-colors.png")))colorPng.Save(f);
   Check(colorBody.ActualWidth==440&&ClearCorners(colorEditor,440,760),"protected color editor renders with transparent styled corners");
   var orbWindow=(Window)New("FloatingBall",getPrefs,new Action(()=>{}),new Action(()=>{}),new Action(()=>{}));Set(quotas[0],"ObservedAt",DateTimeOffset.Now.ToUnixTimeSeconds());Call("FloatingBall",orbWindow,"Apply",snapshot,quotas[0],"fixture","fixture");
   Check(orbWindow.Width==100&&orbWindow.Height==100&&ClearCorners(orbWindow,100,100),"protected circular orb renders with fully transparent corners");
   var orb=InternalField<Border>("FloatingBall",orbWindow,"shell").Child;Check(System.Windows.Automation.AutomationProperties.GetName(orb).Contains("93%"),"protected orb center uses actual quota percentage");
   Check(((FrameworkElement)orb).ToolTip==null&&!ToolTipService.GetIsEnabled(orb),"protected orb suppresses hover tooltip text");
   var weekly=New("QuotaWindow");Set(weekly,"Minutes",10080);Set(weekly,"UsedPercent",27d);Set(weekly,"ResetsAt",9999999999L);Set(quotas[0],"Secondary",weekly);Call("FloatingBall",orbWindow,"Apply",snapshot,quotas[0],"fixture","fixture");
   string dualName=System.Windows.Automation.AutomationProperties.GetName(orb);Check(dualName.Contains("5h 93%")&&dualName.Contains("7d 73%"),"protected dual rings keep both quota windows independent");
   var shortQuota=Get(quotas[0],"Primary");Set(quotas[0],"Primary",null);Call("FloatingBall",orbWindow,"Apply",snapshot,quotas[0],"fixture","fixture");
   string singleName=System.Windows.Automation.AutomationProperties.GetName(orb);Check((int)Call("QuotaOrb",orb,"get_VisibleRingCount")==1&&singleName.Contains("7d 73%")&&!singleName.Contains("5h")&&ClearCorners(orbWindow,100,100),"protected weekly-only quota renders a centered single ring without a 5h placeholder");
   Set(quotas[0],"Secondary",null);Call("FloatingBall",orbWindow,"Apply",snapshot,quotas[0],"fixture","fixture");
   Check((int)Call("QuotaOrb",orb,"get_VisibleRingCount")==0&&System.Windows.Automation.AutomationProperties.GetName(orb).Contains("待更新"),"protected unavailable quota renders one neutral waiting state");
   Set(quotas[0],"Secondary",weekly);for(int step=0;step<8;step++){Set(quotas[0],"Primary",step%2==0?null:shortQuota);Call("FloatingBall",orbWindow,"Apply",snapshot,quotas[0],"fixture","fixture");}
   Check(Object.ReferenceEquals(orb,InternalField<Border>("FloatingBall",orbWindow,"shell").Child)&&orbWindow.Width==100&&(int)Call("QuotaOrb",orb,"get_VisibleRingCount")==2,"quota availability transitions preserve the same orb and native window size");
   int windowCount=Application.Current.Windows.Count;Call("QuotaOrb",orb,"SetPressed",true);Call("QuotaOrb",orb,"SetPressed",false);Call("QuotaOrb",orb,"Pulse");Check(Application.Current.Windows.Count==windowCount,"protected orb click feedback creates no details popup");
   Call("FloatingBall",orbWindow,"SetExpanded",true);Call("FloatingBall",orbWindow,"SetOrb");Check(orbWindow.Width==100,"protected orb and capsule transitions restore the chosen diameter");
   var latestTask=DateTime.UtcNow.AddSeconds(-2).ToString("o");string fixtureLog=Path.Combine(logs,"rollout-test-"+id+".jsonl");
   File.AppendAllText(fixtureLog,json.Serialize(new{type="event_msg",timestamp=latestTask,payload=new{type="task_started"}})+"\n");var active=Call("LocalCodexUsage",reader,"Read","today",DateTime.Now,null,null);
   Check((int)Get(active,"ActiveTasks")==1&&(long)Get(active,"TotalTokens")==110,"protected activity parser preserves token accounting");
   var liveActivity=New("CodexActivity",home,false);var liveReport=Call("CodexActivity",liveActivity,"Scan",DateTime.Now);var liveOrb=InternalField<Border>("FloatingBall",orbWindow,"shell").Child;Call("QuotaOrb",liveOrb,"ApplyActivity",liveReport);
   string runningLabel=System.Windows.Automation.AutomationProperties.GetItemStatus(liveOrb);
   Check(InternalField<int>("ActivityReport",liveReport,"ActiveTasks")==1&&runningLabel=="正在运行 · 1 项任务","protected independent event monitor drives activity without a usage refresh (status="+runningLabel+")");
   File.AppendAllText(fixtureLog,json.Serialize(new{type="event_msg",timestamp=latestTask,payload=new{type="task_complete"}})+"\n");active=Call("LocalCodexUsage",reader,"Read","today",DateTime.Now,null,null);Check((int)Get(active,"ActiveTasks")==0,"protected activity parser handles completion");
   Call("CodexActivity",liveActivity,"Queue",fixtureLog);liveReport=Call("CodexActivity",liveActivity,"Scan",DateTime.Now);Call("QuotaOrb",liveOrb,"ApplyActivity",liveReport);Check(InternalField<int>("ActivityReport",liveReport,"ActiveTasks")==0&&System.Windows.Automation.AutomationProperties.GetItemStatus(liveOrb)=="当前空闲","protected independent monitor clears a completed task with a current report");Call("CodexActivity",liveActivity,"Dispose");
   ActivityRegression(dir,json);Check((bool)Get(clone,"CompletionFlash"),"protected completion setting defaults on and survives cloning");
   Set(prefs,"BallOpacity",.45);Set(prefs,"OrbFollowTheme",true);Set(prefs,"StartWithCodex",true);var settings160=Call("Preferences",prefs,"Clone");Check((double)Get(settings160,"BallOpacity")==.45&&(bool)Get(settings160,"OrbFollowTheme")&&(bool)Get(settings160,"StartWithCodex"),"protected whole-form opacity, theme-follow and sync-start settings survive cloning");
   Check((bool)Call("CodexLaunchWatcher",null,"ShouldLaunch",false,true,false)&&!(bool)Call("CodexLaunchWatcher",null,"ShouldLaunch",true,true,false),"protected Codex startup respects the launch edge and manual exit");
   window.Show();Check(window.IsVisible,"obfuscated native window initialization succeeds");Check(ResizeEdges(window),"four edges and four visible corners retain native resize hit tests");window.Close();
   File.WriteAllText(Path.Combine(dir,"verification.json"),"{\"passed\":true,\"checks\":"+checks+"}");Console.WriteLine("PASS "+checks+" protected-release checks");return 0;
  }catch(Exception e){while(e.InnerException!=null)e=e.InnerException;Console.WriteLine(e.GetType().Name+": "+e.Message);return 1;}
 }
}
