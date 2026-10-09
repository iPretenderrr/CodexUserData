using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
// Reflection keeps this fixture independent of private names after obfuscation.
// It uses isolated settings and a silent film, never the installed application.
class ProtectedStartupProbe
{
    [STAThread] static int Main(string[] args)
    {
        try
        {
            AppDomain.CurrentDomain.SetData("CodexUserData.TestDataFolder",Path.GetFullPath(args[1]));
            var assembly=Assembly.LoadFrom(Path.GetFullPath(args[0]));
            foreach(string name in new[]{"index.html","cinema.webm","layers.webm","matrix.webm","prism.webm","chime.wav","cinema.png","layers.png","matrix.png","prism.png"})
                using(var stream=assembly.GetManifestResourceStream("CodexUserData.Startup."+name))
                    if(stream==null||stream.Length==0)throw new Exception("Missing protected resource: "+name);
            var preferences=assembly.GetTypes().Single(t=>t.GetProperty("StartupAnimationStyle")!=null);
            var value=Activator.CreateInstance(preferences,true);var property=preferences.GetProperty("StartupAnimationStyle");
            if((string)property.GetValue(value,null)!="cinema")throw new Exception("Protected default changed");
            var sound=preferences.GetProperty("StartupSoundEnabled");
            if(sound==null||!(bool)sound.GetValue(value,null))throw new Exception("Protected sound default changed");
            var json=new JavaScriptSerializer();
            foreach(string style in new[]{"cinema","layers","matrix","prism"})
            {property.SetValue(value,style,null);sound.SetValue(value,style=="layers",null);var copy=json.Deserialize(json.Serialize(value),preferences);if((string)property.GetValue(copy,null)!=style||(bool)sound.GetValue(copy,null)!=(style=="layers"))throw new Exception("Protected settings roundtrip failed");}
            var type=assembly.GetTypes().Single(t=>typeof(Window).IsAssignableFrom(t)&&t.GetConstructor(BindingFlags.Instance|BindingFlags.NonPublic,null,new[]{typeof(string),typeof(Action)},null)!=null);
            var ctor=type.GetConstructor(BindingFlags.Instance|BindingFlags.NonPublic,null,new[]{typeof(string),typeof(Action)},null);
            var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};int completed=0;
            var window=(Window)ctor.Invoke(new object[]{"cinema",new Action(()=>completed++)});
            window.WindowStartupLocation=WindowStartupLocation.Manual;window.Left=-20000;window.Top=-20000;window.ShowActivated=false;
            var clock=Stopwatch.StartNew();window.Show();var frame=new DispatcherFrame();
            var poster=((Grid)window.Content).Children.OfType<Image>().Single();
            if(poster.Source==null||window.Opacity!=1)throw new Exception("Protected poster is not initially visible");
            bool retired=false;
            var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(40)};
            timer.Tick+=delegate{if(window.IsVisible&&poster.Source==null)retired=true;if(completed>0&&!window.IsVisible||clock.Elapsed.TotalSeconds>25)frame.Continue=false;};
            timer.Start();Dispatcher.PushFrame(frame);timer.Stop();
            if(!retired||completed!=1||clock.Elapsed.TotalSeconds<8||clock.Elapsed.TotalSeconds>16)throw new Exception("Protected movie did not complete normally: "+clock.Elapsed.TotalSeconds);
            app.Shutdown();Console.WriteLine("PROTECTED_STARTUP=resources, settings roundtrip and actual cinema playback passed");return 0;
        }
        catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}
