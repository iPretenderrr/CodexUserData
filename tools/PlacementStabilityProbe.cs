using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace CodexUserData
{
    internal static class PlacementStabilityProbe
    {
        internal static void Run(string root)
        {
            var p=new Preferences();p.NormalizePlacement();
            StabilityProbe.Check(!p.BallPositionLocked&&p.BallPlacements.Count==0&&p.CustomShapeSizes.Count==0,"older preferences default to an unlocked ball without fabricated placement history");
            p.CustomShapeSizes=new Dictionary<string,double[]>{{"C:\\private\\shape.json",new[]{100d,100d}},{"../private/shape.json",new[]{100d,100d}},{"skins/a/shape.json",new[]{900d,700d}},{"skins\\b\\shape.json",new[]{299d,109d}}};p.NormalizePlacement();
            StabilityProbe.Check(p.CustomShapeSizes.Count==2&&p.CustomShapeSizes.ContainsKey("skins/b/shape.json")&&p.CustomShapeSizes["skins/a/shape.json"][0]==800&&p.CustomShapeSizes["skins/a/shape.json"][1]==600,"persisted HTML sizes reject absolute/traversing paths and normalize bounded relative keys");
            for(int i=0;i<40;i++)p.RememberShapeSize("skins/test"+i+"/shape.json",200+i,80+i);
            StabilityProbe.Check(p.CustomShapeSizes.Count==32&&p.CustomShapeSizes.ContainsKey("skins/test39/shape.json"),"HTML size memory stays bounded while accepting the newest request");
            var cloned=p.Clone();cloned.NormalizePlacement();
            StabilityProbe.Check(cloned.CustomShapeSizes["skins/test39/shape.json"].SequenceEqual(new[]{239d,119d}),"HTML viewport request survives preference serialization and restart");
            var capture=BallPlacement.Capture(new Rect(800,400,200,100),new Rect(0,0,1800,900),"right");
            var restored=capture.Restore(new Size(300,150),new Rect(-1600,0,1600,900));
            StabilityProbe.Check(Math.Abs(capture.X-.5)<.0001&&Math.Abs(capture.Y-.5)<.0001&&restored.Left==-950&&restored.Top==375,"per-monitor normalized positions adapt to new DPI and negative desktop coordinates");
            p.BallMonitor="fixture-unplugged-monitor";p.BallPlacements[p.BallMonitor]=capture;p.BallStyle="capsule";p.BallDock="";p.OrbAnimation="off";p.BallPositionLocked=true;
            Theme.Apply(p);
            int restores=0;var ball=new FloatingBall(()=>p,()=>{},()=>{restores++;},()=>{});ball.Show();ball.UpdateLayout();
            try
            {
                StabilityProbe.Check(p.BallMonitor=="fixture-unplugged-monitor"&&p.BallPlacements.ContainsKey("fixture-unplugged-monitor"),"fallback to a visible screen preserves disconnected monitor memory");
                var host=ball.Content as Decorator;var shell=host==null?null:host.Child as Border;var menu=shell==null?null:shell.ContextMenu;
                var lockItem=menu==null?null:menu.Items.OfType<MenuItem>().FirstOrDefault(item=>Convert.ToString(item.Header)=="锁定位置");
                StabilityProbe.Check(lockItem!=null&&lockItem.IsCheckable,"native context menu always includes a position lock control");
                ball.SetExpanded(true);StabilityProbe.Check(ball.Expanded,"position lock does not block explicit shape changes");
                var back=menu.Items.OfType<MenuItem>().First(item=>Convert.ToString(item.Header)=="返回完整窗口");back.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                StabilityProbe.Check(restores==1,"position lock preserves the native return-to-main action");
            }
            finally{ball.Dispose();}
            p.BallPlacements=new Dictionary<string,BallPlacement>();for(int i=0;i<20;i++)p.BallPlacements["fixture-monitor-"+i]=new BallPlacement{X=Double.NaN,Y=2,Dock="invalid"};p.NormalizePlacement();
            StabilityProbe.Check(p.BallPlacements.Count==8&&p.BallPlacements.Values.All(value=>value.X==0&&value.Y==1&&value.Dock==""),"monitor history is bounded and corrupted coordinates recover safely");
        }
    }
}
