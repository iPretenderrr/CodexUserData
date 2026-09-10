using System;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CodexUserData
{
    internal static class PerformanceStabilityProbe
    {
        private static void Check(bool condition,string message){StabilityProbe.Check(condition,message);}
        internal static void Run(string root)
        {
            Check(Theme.ResolveFrameRate("auto",2,false,true)==60&&Theme.ResolveFrameRate("auto",2,true,true)==30,"automatic motion follows battery power without changing stored preferences");
            Check(Theme.ResolveFrameRate("auto",1,false,true)==30&&Theme.ResolveFrameRate("smooth",2,true,true)==60,"automatic motion limits partial hardware acceleration while explicit full mode remains available");
            Check(Theme.ResolveFrameRate("eco",2,false,true)==30&&Theme.ResolveFrameRate("off",2,false,true)==0&&Theme.ResolveFrameRate("smooth",2,false,false)==0,"lightweight mode retains motion and accessibility preferences disable decorative animation");
            Check(QuotaOrb.WaveSamples(30)==40&&QuotaOrb.WaveSamples(60)==112&&QuotaOrb.ParticleCount(30)==4&&QuotaOrb.ParticleCount(60)==22&&QuotaOrb.ParticleCount(0)==0,"lightweight orb reduces both geometry samples and particle work");
            Check(CapsuleActivityChrome.PerimeterSamplesFor(30)==128&&CapsuleActivityChrome.PerimeterSamplesFor(60)==512&&CapsuleActivityChrome.TrailStepsFor(30)==24&&CapsuleActivityChrome.TrailStepsFor(60)==72,"lightweight capsule reduces cached perimeter samples and connected trail segments");
            var prefs=new Preferences{OrbAnimation="eco"};Theme.Apply(prefs);
            foreach(int fps in new[]{30,60})
            {
                var chrome=new CapsuleActivityChrome();chrome.ApplyPalette(prefs);
                // A presentation source flushes WPF's deferred OnRenderSizeChanged callback.
                // Keep synthetic rendering invisible and non-activating on the user's desktop.
                var host=new Window{Width=340,Height=54,Content=chrome,ShowActivated=false,ShowInTaskbar=false,WindowStyle=WindowStyle.None,ResizeMode=ResizeMode.NoResize,AllowsTransparency=true,Background=Brushes.Transparent,Opacity=0};
                host.Show();host.UpdateLayout();chrome.Start(fps);
                chrome.BeginAnimation(CapsuleActivityChrome.PhaseProperty,null);chrome.BeginAnimation(CapsuleActivityChrome.BreathProperty,null);chrome.SetValue(CapsuleActivityChrome.PhaseProperty,.43);chrome.SetValue(CapsuleActivityChrome.BreathProperty,.8);
                Console.WriteLine("CAPSULE FIXTURE fps="+fps+" size="+chrome.ActualWidth+"x"+chrome.ActualHeight+" visible="+chrome.IsVisible+" active="+StabilityProbe.Field<bool>(chrome,"active")+" clip="+(StabilityProbe.Field<RectangleGeometry>(chrome,"clip")!=null)+" bounds="+StabilityProbe.Field<Rect>(chrome,"bounds"));
                // Exercise the actual drawing method independently of WPF's decision to cull
                // an invisible native host. This verifies the profile's geometry, not its HWND.
                var drawing=new DrawingVisual();using(var dc=drawing.RenderOpen())typeof(CapsuleActivityChrome).GetMethod("OnRender",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(chrome,new object[]{dc});
                var image=new RenderTargetBitmap(340,54,96,96,PixelFormats.Pbgra32);image.Render(drawing);
                var pixels=new byte[340*54*4];image.CopyPixels(pixels,340*4,0);bool painted=false;for(int i=3;i<pixels.Length;i+=4)if(pixels[i]>0){painted=true;break;}
                Check(painted,"capsule profile "+fps+" renders a connected activity layer without invalid geometry");
                chrome.Stop();Check(!chrome.HasAnimatedProperties,"stopping capsule profile "+fps+" removes its repeating animation clocks");host.Close();
            }
            var orb=new QuotaOrb();orb.Apply(prefs,null,null,"synthetic");orb.SetPressed(true);
            typeof(QuotaOrb).GetMethod("Stop",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(orb,null);
            Check(!StabilityProbe.Field<bool>(orb,"subscribed")&&!StabilityProbe.Field<System.Windows.Threading.DispatcherTimer>(orb,"heartbeat").IsEnabled&&StabilityProbe.Field<double>(orb,"press")==0,"hidden orb stops its render subscription and clock without accumulating pressed state");orb.Dispose();
        }
    }
}
