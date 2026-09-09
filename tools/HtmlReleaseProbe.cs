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
        var matches=T(type).GetMethods(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static).Where(x=>!x.ContainsGenericParameters&&x.IsStatic==(instance==null)&&x.Name==renamed&&x.GetParameters().Length==args.Length&&x.GetParameters().Select((p,i)=>args[i]==null?!p.ParameterType.IsValueType:p.ParameterType.IsInstanceOfType(args[i])).All(b=>b)).ToArray();
        return matches.Single().Invoke(instance,args);
    }
    static object Field(string type,object instance,string name)
    {
        var match=Regex.Match(map,@"CodexUserData\."+Regex.Escape(type)+"::"+Regex.Escape(name)+@" -> ([^\r\n]+)");
        if(!match.Success||instance==null)return null;
        var owner=T(type);Type expected=name=="custom"?T("CustomShapeView"):typeof(string);
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
        bool restored=false;ball=(Window)New("FloatingBall",get,new Action(()=>{}),new Action(()=>restored=true),new Action(()=>{}));
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
        var menu=((Border)ball.Content).ContextMenu;
        var right=new MouseButtonEventArgs(Mouse.PrimaryDevice,0,MouseButton.Right){RoutedEvent=Mouse.PreviewMouseDownEvent,Source=web};web.RaiseEvent(right);
        Check(right.Handled&&menu.IsOpen,"protected host retains native right-click routing");
        menu.Items.OfType<MenuItem>().Single(m=>Convert.ToString(m.Header)=="返回完整窗口").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));Check(restored,"protected native menu returns to the main UI");menu.IsOpen=false;
        await web.ExecuteScriptAsync("chrome.webview.postMessage({type:'resize',width:312,height:96})");await Task.Delay(120);Check(ball.Width==312&&ball.Height==96,"protected page-to-host commands survive string hiding");
        Call("FloatingBall",ball,"SetOrb");Check(Find<WebView2CompositionControl>(ball)==null,"protected form switch removes its browser renderer");
        ((IDisposable)ball).Dispose();ball=null;File.WriteAllText(Path.Combine(output,"html-verification.json"),"{\"passed\":true,\"checks\":"+checks+"}");
    }
    [STAThread]static int Main(string[] args)
    {
        AppDomain.CurrentDomain.SetData("CodexUserData.TestDataFolder",Path.Combine(Path.GetFullPath(args[2]),"html-fixture-user"));
        assembly=Assembly.LoadFrom(args[0]);map=File.ReadAllText(args[1]);output=args[2];Directory.CreateDirectory(output);int result=0;
        var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};app.Startup+=async delegate{try{await Run();}catch(Exception ex){Console.Error.WriteLine(ex);result=1;}finally{if(ball!=null)((IDisposable)ball).Dispose();app.Shutdown();}};app.Run();return result;
    }
}
