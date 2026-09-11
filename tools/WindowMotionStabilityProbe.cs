using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace CodexUserData
{
    // Uses plain local windows and generated preferences only. No fixture accesses the user's
    // settings, account, CLI, session logs, or currently running application.
    internal static class WindowMotionStabilityProbe
    {
        private static Window TestWindow(System.Windows.Media.Brush color)
        {
            var window=new Window{Width=300,Height=120,Left=140,Top=140,ShowActivated=false,ShowInTaskbar=false,WindowStyle=WindowStyle.None,AllowsTransparency=true,Background=System.Windows.Media.Brushes.Transparent,Content=new Border{Background=color}};
            WindowInteraction.EnableMotion(window);return window;
        }
        private static FrameworkElement View(Window window){return (FrameworkElement)window.Content;}
        private static async Task Settle(int milliseconds=420){await Task.Delay(milliseconds);}
        internal static async Task Run(string root)
        {
            var a=TestWindow(System.Windows.Media.Brushes.SlateBlue);var b=TestWindow(System.Windows.Media.Brushes.CornflowerBlue);
            try
            {
                a.Show();await Settle();bool preparedWhileHidden=false,shown=false;
                WindowInteraction.ShowFrom(b,a,delegate{preparedWhileHidden=!b.IsVisible;},delegate{shown=true;});
                StabilityProbe.Check(preparedWhileHidden&&a.IsVisible&&b.IsVisible,"window transfer prepares and mounts its destination before hiding the source");
                await Task.Delay(55);
                StabilityProbe.Check(a.IsVisible&&b.IsVisible&&View(a).Opacity>.01&&View(b).Opacity>.01,"window transfer overlaps visible source and destination frames instead of exposing a blank desktop frame");
                await StabilityProbe.Until(()=>shown&&!a.IsVisible&&b.IsVisible,"overlapped window transfer did not settle on its destination",1800);

                WindowInteraction.ShowFrom(a,b,null,null);await Task.Delay(55);double outgoing=View(b).Opacity;
                WindowInteraction.ShowFrom(b,a,null,null);double reversed=View(b).Opacity;
                StabilityProbe.Check(outgoing<.99&&outgoing>.01&&reversed<.99&&Math.Abs(reversed-outgoing)<.18,"reversing a transfer resumes the source from its current opacity without jumping to full");
                await StabilityProbe.Until(()=>b.IsVisible&&!a.IsVisible,"rapid reverse transfer did not retain the latest target",1800);

                int obsoleteShown=0;WindowInteraction.ShowFrom(a,b,null,delegate{obsoleteShown++;});
                WindowInteraction.ShowFrom(b,a,null,null);await Settle(650);
                StabilityProbe.Check(b.IsVisible&&!a.IsVisible&&obsoleteShown==0,"a newer reverse request cancels the obsolete destination and shown callback");

                int hiddenShown=0;WindowInteraction.ShowFrom(a,b,null,delegate{hiddenShown++;});WindowInteraction.Hide(a);await Settle(650);
                StabilityProbe.Check(!a.IsVisible&&hiddenShown==0,"hiding an incoming target invalidates its delayed shown callback");

                bool failed=false;try{WindowInteraction.ShowFrom(a,b,delegate{throw new InvalidOperationException("synthetic prepare failure");},null);}catch(InvalidOperationException){failed=true;}
                a.Show();await Settle();
                StabilityProbe.Check(failed&&a.IsVisible&&View(a).Opacity>.99,"failed prepare releases its reveal hold so a later ordinary show remains usable");
                a.Hide();

                string fixture=Path.Combine(root,"motion-html");Directory.CreateDirectory(fixture);
                var prefs=new Preferences{Source="local",CodexHome=fixture,Database=Path.Combine(fixture,"missing.db"),LiveQuota=false,QuotaCli="",BallStyle="html",CustomShape="missing/shape.json",BallDock="",BallLeft=420,BallTop=260,OrbAnimation="smooth"};prefs.Validate();Theme.Apply(prefs);
                var html=new FloatingBall(()=>prefs,delegate{},delegate{},delegate{});
                try
                {
                    WindowInteraction.PrepareReveal(html);
                    StabilityProbe.Check(html.IsCustom&&html.Opacity==0,"HTML destination retains native opacity gating until its presentation is ready");
                }
                finally{html.Dispose();}
            }
            finally{a.Close();b.Close();}
        }
    }
}
