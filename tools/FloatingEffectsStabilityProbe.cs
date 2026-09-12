using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CodexUserData
{
    // Generated fixtures only; this suite never queries a CLI or opens the user's ledger.
    internal static class FloatingEffectsStabilityProbe
    {
        private static byte[] Render(FrameworkElement visual,string path)
        {
            visual.UpdateLayout();int w=(int)Math.Ceiling(visual.ActualWidth),h=(int)Math.Ceiling(visual.ActualHeight);
            var bitmap=new RenderTargetBitmap(w,h,96,96,PixelFormats.Pbgra32);bitmap.Render(visual);
            var pixels=new byte[w*h*4];bitmap.CopyPixels(pixels,w*4,0);
            var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using(var file=File.Create(path))encoder.Save(file);return pixels;
        }
        private static bool Animated(UIElement element)
        {return DependencyPropertyHelper.GetValueSource(element,UIElement.OpacityProperty).IsAnimated;}
        internal static async Task Run(string root)
        {
            var legacy=Program.Json.Deserialize<Preferences>("{}");
            StabilityProbe.Check(legacy.EffectStrength==1,"old settings retain original 1x floating effects");
            foreach(double input in new[]{Double.NaN,Double.PositiveInfinity,-1d,.5,1,3,8})
            {
                var p=new Preferences{BallEffectIntensity=input};p.Validate();
                double expected=Double.IsNaN(input)||Double.IsInfinity(input)?1:Math.Max(.5,Math.Min(3,input));
                StabilityProbe.Check(p.BallEffectIntensity==expected&&p.Clone().EffectStrength==expected,"effect strength validates and round-trips: "+input);
            }
            string output=Path.Combine(Path.GetDirectoryName(root),"floating-effects-images");Directory.CreateDirectory(output);
            var editorPrefs=new Preferences{CodexHome=Path.Combine(root,"codex"),Database=Path.Combine(root,"missing.db"),LiveQuota=false,QuotaCli="",OrbAnimation="eco"};Theme.Apply(editorPrefs);
            var editor=new SettingsWindow(editorPrefs,null){ShowActivated=false};
            try
            {
                editor.Show();StabilityProbe.Call(editor,"SelectPage","floating");editor.UpdateLayout();
                var slider=GuideStabilityProbe.Find(editor,"BallEffectIntensity") as Slider;
                StabilityProbe.Check(slider!=null&&slider.Minimum==.5&&slider.Maximum==3&&slider.Value==1,"floating settings expose a 0.5-3x slider with original default");
                slider.Value=2.5;var draft=StabilityProbe.Field<Preferences>(editor,"draft");
                StabilityProbe.Check(draft.BallEffectIntensity==2.5&&editorPrefs.BallEffectIntensity==1,"strength edits are isolated until settings are saved");
            }
            finally{editor.Close();}
            foreach(string theme in new[]{"dark","light"})
            {
                var p=new Preferences{ThemeMode=theme};Theme.Apply(p);var chrome=new CapsuleActivityChrome{Width=240,Height=50};
                chrome.Measure(new Size(240,50));chrome.Arrange(new Rect(0,0,240,50));byte[] previous=null;
                foreach(double strength in new[]{.5,1,3})
                {
                    p.BallEffectIntensity=strength;chrome.ApplyPalette(p);chrome.Start(30);
                    chrome.BeginAnimation(CapsuleActivityChrome.PhaseProperty,null);chrome.BeginAnimation(CapsuleActivityChrome.BreathProperty,null);
                    chrome.SetValue(CapsuleActivityChrome.PhaseProperty,.45);chrome.SetValue(CapsuleActivityChrome.BreathProperty,1d);
                    var pixels=Render(chrome,Path.Combine(output,theme+"-"+strength+".png"));
                    if(previous!=null)StabilityProbe.Check(!pixels.SequenceEqual(previous),"visible strength steps in "+theme+" capsule");previous=pixels;
                    StabilityProbe.Check(StabilityProbe.Field<int>(chrome,"trailSteps")==24,"strength does not increase lightweight trail samples");chrome.Stop();
                }
            }
            var data=GuideStabilityProbe.Snapshot();
            foreach(string form in new[]{"small","large","orb","classic","glass","top","left"})
            {
                var p=new Preferences{Source="local",LiveQuota=false,BallStyle=form=="orb"?"orb":form=="classic"||form=="glass"?"island":"capsule",IslandMaterial=form=="classic"?"classic":"glass",BallExpanded=form=="large",BallDock=form=="top"||form=="left"?form:"",BallLeft=350,BallTop=220,OrbAnimation="eco",BallEffectIntensity=3};
                Theme.Apply(p);var ball=new FloatingBall(()=>p,()=>{},()=>{},()=>{}){ShowActivated=false};
                try
                {
                    ball.Apply(data,data.Quotas[0],"Fixture","");ball.Show();
                    long now=LocalCodexUsage.Unix(DateTime.Now);ball.ApplyActivity(new ActivityReport{ActiveTasks=1,Until=now+60,ObservedAt=now});await Task.Delay(450);
                    double width=ball.Width,height=ball.Height;
                    Render((FrameworkElement)ball.Content,Path.Combine(output,form+"-running-3x.png"));
                    p.BallEffectIntensity=.5;ball.Apply(data,data.Quotas[0],"Fixture","");await Task.Delay(60);
                    StabilityProbe.Check(ball.Width==width&&ball.Height==height,"strength preserves window geometry: "+form);
                    if(form=="small"||form=="large")StabilityProbe.Check(StabilityProbe.Field<double>(StabilityProbe.Field<CapsuleActivityChrome>(ball,"capsuleChrome"),"strength")==.5,"saved intensity refreshes cached capsule paint without new usage data: "+form);
                    ball.ApplyActivity(new ActivityReport());ball.SetCompletionPending(true);await Task.Delay(100);
                    var target=((Decorator)ball.Content).Child;
                    if(!ball.IsIsland)StabilityProbe.Check(Animated(target),"pending completion breathes: "+form);
                    ball.Hide();await Task.Delay(50);
                    StabilityProbe.Check(!Animated(target)&&StabilityProbe.Field<int>(ball,"activityMotionFps")==0,"hidden form stops host animation: "+form);
                    ball.Show();ball.SetCompletionPending(false);
                    StabilityProbe.Check(!Animated(target),"acknowledgement clears completion opacity: "+form);
                    p.OrbAnimation="off";p.BallEffectIntensity=3;Theme.Apply(p);ball.Apply(data,data.Quotas[0],"Fixture","");ball.SetCompletionPending(true);
                    StabilityProbe.Check(!Animated(target),"3x respects disabled animation: "+form);
                }
                finally{ball.Dispose();}
            }
            var feedbackPrefs=new Preferences{OrbAnimation="eco",BallStyle="capsule"};Theme.Apply(feedbackPrefs);
            var feedbackBall=new FloatingBall(()=>feedbackPrefs,()=>{},()=>{},()=>{}){ShowActivated=false};
            try
            {
                feedbackBall.Show();await Task.Delay(300);var target=((Decorator)feedbackBall.Content).Child;double previous=1;
                foreach(double strength in new[]{.5,1,3})
                {
                    feedbackPrefs.BallEffectIntensity=strength;feedbackBall.SetCompletionPending(true);await Task.Delay(1080);
                    StabilityProbe.Check(target.Opacity<previous-.1,"completion breathing is stronger at "+strength+"x");previous=target.Opacity;
                    feedbackBall.SetCompletionPending(false);
                }
                feedbackBall.SetCompletionPending(true);feedbackPrefs.BallEffectIntensity=.5;feedbackBall.SetCompletionPending(false);
                StabilityProbe.Check(!Animated(target),"strength change and acknowledgement together leave no stale opacity clock");
            }
            finally{feedbackBall.Dispose();}
        }
    }
}
