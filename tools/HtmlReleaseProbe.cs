using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Wpf;

// Exercises the actual protected EXE, not a second build from unobfuscated sources.
internal static class HtmlReleaseProbe
{
    static Assembly assembly;static string map,output;static Window ball;static int checks;
    static Type T(string name){var m=Regex.Match(map,@"^\[CodexUserData\]CodexUserData\."+Regex.Escape(name)+@" -> \[CodexUserData\](.+)$",RegexOptions.Multiline);return assembly.GetType(m.Groups[1].Value.Trim(),true);}
    static object New(string name,params object[] args){return Activator.CreateInstance(T(name),BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance,null,args,null);}
    static void Set(object o,string property,object value){o.GetType().GetProperty(property).SetValue(o,value,null);}
    static object Call(string type,object instance,string method,params object[] args)
    {
        var m=Regex.Match(map,@"CodexUserData\."+Regex.Escape(type)+"::"+Regex.Escape(method)+@"\["+args.Length+@"\][^\r\n]*? -> ([^\r\n]+)");string renamed=m.Success?m.Groups[1].Value.Trim():method;
        var matches=T(type).GetMethods(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static).Where(x=>!x.ContainsGenericParameters&&x.IsStatic==(instance==null)&&x.Name==renamed&&x.GetParameters().Length==args.Length&&x.GetParameters().Select((p,i)=>args[i]==null?!p.ParameterType.IsValueType:p.ParameterType.IsInstanceOfType(args[i])).All(b=>b)).Select(x=>new{Method=x,Exact=x.GetParameters().Select((p,i)=>args[i]!=null&&p.ParameterType==args[i].GetType()?1:0).Sum()}).ToArray();
        if(matches.Length==0)throw new MissingMethodException(type,method);
        return matches.Where(x=>x.Exact==matches.Max(y=>y.Exact)).Single().Method.Invoke(instance,args);
    }
    static object CallAs(string type,object instance,string method,Type[] signature,params object[] args)
    {
        var m=Regex.Match(map,@"CodexUserData\."+Regex.Escape(type)+"::"+Regex.Escape(method)+@"\["+args.Length+@"\][^\r\n]*? -> ([^\r\n]+)");string renamed=m.Success?m.Groups[1].Value.Trim():method;
        var selected=T(type).GetMethods(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static).Single(x=>x.IsStatic==(instance==null)&&x.Name==renamed&&x.GetParameters().Select(p=>p.ParameterType).SequenceEqual(signature));
        return selected.Invoke(instance,args);
    }
    static object Field(string type,object instance,string name)
    {
        var match=Regex.Match(map,@"CodexUserData\."+Regex.Escape(type)+"::"+Regex.Escape(name)+@" -> ([^\r\n]+)");
        if(!match.Success||instance==null)return null;
        var owner=T(type);Type expected=name=="custom"?T("CustomShapeView"):name=="shell"?typeof(Border):typeof(string);
        return owner.GetFields(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic).Single(f=>f.DeclaringType==owner&&f.Name==match.Groups[1].Value.Trim()&&f.FieldType==expected).GetValue(instance);
    }
    static U Find<U>(DependencyObject root) where U:DependencyObject
    {if(root is U)return (U)root;for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){var result=Find<U>(VisualTreeHelper.GetChild(root,i));if(result!=null)return result;}return null;}
    static void Check(bool ok,string title){if(!ok)throw new Exception(title);Console.WriteLine("PASS "+title);checks++;}
    static async Task Run()
    {
        string relative=Path.Combine("skins","protected-fixture"),folder=Path.Combine((string)AppDomain.CurrentDomain.GetData("CodexUserData.TestDataFolder"),relative);Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder,"shape.json"),"{\"apiVersion\":1,\"name\":\"Protected fixture\",\"entry\":\"index.html\",\"width\":240,\"height\":84}");
        File.WriteAllText(Path.Combine(folder,"index.html"),"<!doctype html><html><meta charset='utf-8'><style>html,body{margin:0;color:#def;background:transparent;font:24px sans-serif}main{background:#203247;border-radius:20px;padding:24px}</style><main id='value'>waiting</main><script>chrome.webview.addEventListener('message',e=>{if(e.data.type==='snapshot')value.textContent=e.data.data.today?.tokenText??'unknown'});document.addEventListener('contextmenu',e=>e.preventDefault());</script></html>");
        var prefs=New("Preferences");Set(prefs,"BallStyle","html");Set(prefs,"CustomShape",Path.Combine(relative,"shape.json"));Set(prefs,"BallDock","");Set(prefs,"BallLeft",420d);Set(prefs,"BallTop",360d);
        var get=System.Linq.Expressions.Expression.Lambda(typeof(Func<>).MakeGenericType(T("Preferences")),System.Linq.Expressions.Expression.Constant(prefs)).Compile();
        bool restored=false;var main=new Window{Width=420,Height=360,ShowActivated=false,ShowInTaskbar=false,Content=new Border{Background=Brushes.SlateBlue}};Call("WindowInteraction",null,"EnableMotion",main);
        ball=(Window)New("FloatingBall",get,new Action(()=>{}),new Action(()=>CallAs("WindowInteraction",null,"ShowFrom",new[]{typeof(Window),typeof(Window),typeof(Action),typeof(Action)},main,ball,new Action(()=>{}),new Action(()=>restored=true))),new Action(()=>{}));
        var daily=New("DailyUsage");Set(daily,"Date",DateTime.Today.ToString("yyyy-MM-dd"));Set(daily,"Tokens",2468000L);var days=Array.CreateInstance(T("DailyUsage"),1);days.SetValue(daily,0);var snapshot=New("UsageSnapshot");Set(snapshot,"Daily",days);
        Call("FloatingBall",ball,"Apply",snapshot,null,"fixture","fixture");ball.ShowActivated=false;ball.Show();
        var until=DateTime.UtcNow.AddSeconds(18);WebView2CompositionControl web=null;
        while(true)
        {
            try{if(web!=null&&web.CoreWebView2!=null)break;}
            catch(ObjectDisposedException ex){var custom=Field("FloatingBall",ball,"custom");throw new Exception("Protected HTML renderer was disposed at "+Convert.ToString(Field("CustomShapeView",custom,"LoadStage"))+": "+Convert.ToString(Field("CustomShapeView",custom,"LastError")),ex);}
            if(DateTime.UtcNow>until)throw new Exception("Protected HTML initialization timed out");web=Find<WebView2CompositionControl>(ball);await Task.Delay(100);
        }
        string value="";while(!value.Contains("246")){if(DateTime.UtcNow>until)throw new Exception("Protected HTML never received numeric snapshot: "+value);try{value=await web.ExecuteScriptAsync("document.getElementById('value')?.textContent??''");}catch(ObjectDisposedException ex){var custom=Field("FloatingBall",ball,"custom");throw new Exception("Protected HTML renderer was disposed at "+Convert.ToString(Field("CustomShapeView",custom,"LoadStage"))+": "+Convert.ToString(Field("CustomShapeView",custom,"LastError")),ex);}await Task.Delay(100);}
        Check(value.Contains("246"),"actual protected HTML host publishes its versioned data contract");
        Check(web.DefaultBackgroundColor.A==0&&ball.Width==240&&ball.Height==84,"protected browser renderer keeps transparent background and manifest dimensions");
        var menu=((Border)Field("FloatingBall",ball,"shell")).ContextMenu;
        var right=new MouseButtonEventArgs(Mouse.PrimaryDevice,0,MouseButton.Right){RoutedEvent=Mouse.PreviewMouseDownEvent,Source=web};web.RaiseEvent(right);
        Check(right.Handled&&menu.IsOpen,"protected host retains native right-click routing");
        menu.IsOpen=false;
        int sizeChanges=0;ball.SizeChanged+=delegate{sizeChanges++;};double initialWidth=ball.Width;
        for(int i=0;i<20;i++)Call("FloatingBall",ball,"ResizeCustom",280d+i,90d+i);
        Check(ball.Width==initialWidth,"HTML resize requests wait for the render queue");await Task.Delay(120);
        Check(ball.Width==299&&ball.Height==109&&sizeChanges<=2,"HTML resize bursts commit only their newest dimensions");
        var originalCustom=Field("FloatingBall",ball,"custom");var work=SystemParameters.WorkArea;
        ball.Left=work.Left+4;ball.Top=work.Top+work.Height/2;Call("FloatingBall",ball,"SnapToEdge");await Task.Delay(55);
        Check(ball.IsVisible&&ball.Opacity>0&&ball.Opacity<1,"HTML docking fades the complete browser window");
        var dockDeadline=DateTime.UtcNow.AddSeconds(3);while(!(bool)Call("FloatingBall",ball,"get_IsPillar")&&DateTime.UtcNow<dockDeadline)await Task.Delay(30);
        Check((bool)Call("FloatingBall",ball,"get_IsPillar")&&Object.ReferenceEquals(originalCustom,Field("FloatingBall",ball,"custom"))&&!((FrameworkElement)originalCustom).IsVisible,"docked HTML keeps one suspended browser controller");
        ball.Left=work.Left+work.Width/2;ball.Top=work.Top+work.Height/2;var undockClock=System.Diagnostics.Stopwatch.StartNew();Call("FloatingBall",ball,"SnapToEdge");
        bool sawUndockFade=false;var undockDeadline=DateTime.UtcNow.AddSeconds(3);while(((bool)Call("FloatingBall",ball,"get_IsPillar")||!Object.ReferenceEquals(web,Find<WebView2CompositionControl>(ball)))&&DateTime.UtcNow<undockDeadline){if(ball.Opacity>0&&ball.Opacity<1)sawUndockFade=true;await Task.Delay(30);}while(ball.Opacity<.999&&DateTime.UtcNow<undockDeadline){if(ball.Opacity>0&&ball.Opacity<1)sawUndockFade=true;await Task.Delay(30);}
        Check(sawUndockFade&&!(bool)Call("FloatingBall",ball,"get_IsPillar")&&Object.ReferenceEquals(originalCustom,Field("FloatingBall",ball,"custom"))&&Object.ReferenceEquals(web,Find<WebView2CompositionControl>(ball))&&undockClock.ElapsedMilliseconds<1600,"edge strip fades into the existing HTML renderer without reinitialization");
        Call("FloatingBall",ball,"SetOrb");
        // Shape changes now commit at the fade midpoint; wait for that lifecycle boundary.
        var switchDeadline=DateTime.UtcNow.AddSeconds(2);while(Find<WebView2CompositionControl>(ball)!=null&&DateTime.UtcNow<switchDeadline)await Task.Delay(30);
        Check(Find<WebView2CompositionControl>(ball)==null,"protected form switch removes its browser renderer");
        Call("FloatingBall",ball,"SetCustom");var htmlDeadline=DateTime.UtcNow.AddSeconds(8);while((web=Find<WebView2CompositionControl>(ball))==null&&DateTime.UtcNow<htmlDeadline)await Task.Delay(40);
        while(web!=null&&web.CoreWebView2==null&&DateTime.UtcNow<htmlDeadline)await Task.Delay(40);
        Check(web!=null&&web.CoreWebView2!=null,"shared browser environment accelerates a later HTML form");
        // Core initialization precedes the first visible browser frame. Wait for the form
        // a real user can click, then observe rendered frames rather than one 55ms sample.
        while((ball.Opacity<.999||((FrameworkElement)ball.Content).Opacity<.999)&&DateTime.UtcNow<htmlDeadline)await Task.Delay(30);
        Check(ball.IsVisible&&ball.Opacity>=.999&&((FrameworkElement)ball.Content).Opacity>=.999,"HTML form presents its first frame before simulated menu input");
        bool sawRestoreFade=false;EventHandler sampleRestore=delegate{if(ball.IsVisible&&ball.Opacity>0&&ball.Opacity<1)sawRestoreFade=true;};
        CompositionTarget.Rendering+=sampleRestore;
        try
        {
            menu.Items.OfType<MenuItem>().Single(m=>Convert.ToString(m.Header)=="返回完整窗口").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            var fadeDeadline=DateTime.UtcNow.AddSeconds(3);while(ball.IsVisible&&DateTime.UtcNow<fadeDeadline)await Task.Delay(20);
        }
        finally{CompositionTarget.Rendering-=sampleRestore;}
        Check(sawRestoreFade,"HTML-to-main transition fades the complete browser window");
        var restoreDeadline=DateTime.UtcNow.AddSeconds(3);while((ball.IsVisible||!main.IsVisible||((FrameworkElement)main.Content).Opacity<.999)&&DateTime.UtcNow<restoreDeadline)await Task.Delay(30);
        Check(restored&&!ball.IsVisible&&main.IsVisible&&((FrameworkElement)main.Content).Opacity>=.999,"protected native menu returns to the main UI without a browser-frame flash");
        main.Close();((IDisposable)ball).Dispose();ball=null;File.WriteAllText(Path.Combine(output,"html-verification.json"),"{\"passed\":true,\"checks\":"+checks+"}");
    }
    [STAThread]static int Main(string[] args)
    {
        AppDomain.CurrentDomain.SetData("CodexUserData.TestDataFolder",Path.Combine(Path.GetFullPath(args[2]),"html-fixture-user"));
        assembly=Assembly.LoadFrom(args[0]);map=File.ReadAllText(args[1]);output=args[2];Directory.CreateDirectory(output);int result=0;
        var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};app.Startup+=async delegate{try{await Run();}catch(Exception ex){Console.Error.WriteLine(ex);result=1;}finally{if(ball!=null)((IDisposable)ball).Dispose();app.Shutdown();}};app.Run();return result;
    }
}
