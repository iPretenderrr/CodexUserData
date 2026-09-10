using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Runtime.InteropServices;

namespace CodexUserData
{
    // Transient, non-activating visual only. The real window settles once, after the animation,
    // rather than resizing its native handle on every frame. Snapshots never leave memory.
    internal sealed class DockTransition : IDisposable
    {
        private readonly Window owner;
        private readonly FrameworkElement content;
        private readonly Action completed;
        private Window overlay;
        private DockMorph view;
        private bool finished;
        private readonly double originalOpacity;
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd,IntPtr after,int x,int y,int width,int height,uint flags);
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd,int index);
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd,int index,int value);

        internal DockTransition(Window window,FrameworkElement root,Action after)
        {owner=window;content=root;completed=after;originalOpacity=root.Opacity;}
        internal void Begin(Rect source,Rect target,int side,BitmapSource destination,int fps,bool browser)
        {
            if(browser)
            {
                // WebView2 owns a separate compositor; a WPF bitmap cannot reliably capture it.
                var fade=new DoubleAnimation(originalOpacity,0,Theme.MotionTime(170));Timeline.SetDesiredFrameRate(fade,fps);
                fade.Completed+=delegate{Finish();};content.BeginAnimation(UIElement.OpacityProperty,fade);return;
            }
            try
            {
                var bitmap=Capture(content,source.Width,source.Height);
                Rect union=Rect.Union(source,target);union.Inflate(2,2);
                var from=new Rect(source.X-union.X,source.Y-union.Y,source.Width,source.Height);
                var to=new Rect(target.X-union.X,target.Y-union.Y,target.Width,target.Height);
                bool simple=(RenderCapability.Tier>>16)==0;
                view=new DockMorph(bitmap,destination,from,to,side,simple?1:fps<=30?32:64){Width=union.Width,Height=union.Height};
                overlay=new Window{Title="CodexUserData 吸附过渡",Owner=owner,ShowActivated=false,ShowInTaskbar=false,Topmost=true,WindowStyle=WindowStyle.None,ResizeMode=ResizeMode.NoResize,AllowsTransparency=true,Background=Brushes.Transparent,Opacity=owner.Opacity,Width=union.Width,Height=union.Height,Content=new Viewbox{Stretch=Stretch.Fill,Child=view}};
                overlay.SourceInitialized+=delegate
                {
                    IntPtr handle=new WindowInteropHelper(overlay).Handle;
                    SetWindowLong(handle,-20,GetWindowLong(handle,-20)|0x08000000|0x00000020|0x00000080); // No activation, click-through, tool window.
                    SetWindowPos(handle,new IntPtr(-1),(int)union.X,(int)union.Y,(int)Math.Ceiling(union.Width),(int)Math.Ceiling(union.Height),0x10);
                };
                overlay.Show();overlay.UpdateLayout();content.Visibility=Visibility.Hidden;
                var animation=new DoubleAnimation(0,1,Theme.MotionTime(simple?220:460)){FillBehavior=FillBehavior.HoldEnd};Timeline.SetDesiredFrameRate(animation,fps);
                animation.Completed+=delegate{Finish();};view.BeginAnimation(DockMorph.ProgressProperty,animation);
            }
            catch(InvalidOperationException){Finish();}
            catch(OutOfMemoryException){Finish();}
        }
        internal static BitmapSource Capture(FrameworkElement visual,double pixelWidth,double pixelHeight)
        {
            visual.UpdateLayout();int width=Math.Max(1,(int)Math.Ceiling(pixelWidth)),height=Math.Max(1,(int)Math.Ceiling(pixelHeight));
            var image=new RenderTargetBitmap(width,height,96*width/visual.ActualWidth,96*height/visual.ActualHeight,PixelFormats.Pbgra32);image.Render(visual);image.Freeze();return image;
        }
        private void Finish()
        {
            if(finished)return;finished=true;
            content.BeginAnimation(UIElement.OpacityProperty,null);content.Opacity=originalOpacity;
            try{completed();}finally
            {
                content.Visibility=Visibility.Visible;content.UpdateLayout();
                // Let the settled strip reach the render queue before removing the snapshot.
                owner.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle,new Action(CloseOverlay));
            }
        }
        private void CloseOverlay(){if(view!=null){view.BeginAnimation(DockMorph.ProgressProperty,null);view=null;}if(overlay!=null){overlay.Close();overlay=null;}}
        public void Dispose()
        {
            finished=true;content.BeginAnimation(UIElement.OpacityProperty,null);content.Opacity=originalOpacity;content.Visibility=Visibility.Visible;CloseOverlay();
        }
    }

    internal sealed class DockMorph : FrameworkElement
    {
        internal static readonly DependencyProperty ProgressProperty=DependencyProperty.Register("Progress",typeof(double),typeof(DockMorph),new FrameworkPropertyMetadata(0.0,FrameworkPropertyMetadataOptions.AffectsRender));
        private readonly BitmapSource source,destination;
        private readonly BitmapSource[] slices;
        private readonly double[] divisions;
        private readonly Rect from,to;
        private readonly int side;
        private readonly bool horizontal;
        internal DockMorph(BitmapSource image,BitmapSource target,Rect start,Rect end,int edge,int count)
        {
            source=image;destination=target;from=start;to=end;side=edge;horizontal=edge>=2;IsHitTestVisible=false;
            int length=horizontal?image.PixelHeight:image.PixelWidth;count=Math.Min(count,length);
            slices=new BitmapSource[count];divisions=new double[count+1];
            // Integer crops partition every source pixel exactly once. Reuse them for all frames.
            for(int i=0;i<count;i++)
            {
                int a=i*length/count,b=(i+1)*length/count;divisions[i]=a/(double)length;
                var crop=new CroppedBitmap(image,horizontal?new Int32Rect(0,a,image.PixelWidth,b-a):new Int32Rect(a,0,b-a,image.PixelHeight));crop.Freeze();slices[i]=crop;
            }
            divisions[count]=1;
        }
        private static double Smooth(double value){value=Math.Max(0,Math.Min(1,value));return value*value*(3-2*value);}
        private static double Mix(double a,double b,double t){return a+(b-a)*t;}
        // The edge-facing end arrives first while the far end lingers, stretching a curved neck.
        // Interpolate the ordered bounds once, then partition them so slices cannot cross.
        internal Rect SliceBounds(double a,double b,double time)
        {
            double depth=(a+b)/2;if(side==1||side==3)depth=1-depth;
            double neck=Smooth((time-.32*depth)/.68),travel=Smooth((time-.1)/.9),unfurl=Smooth((time-.78)/.22);
            double sourceCross=horizontal?from.Width:from.Height,targetCross=horizontal?to.Width:to.Height;
            double cross=Mix(Mix(sourceCross,Math.Min(28,Math.Min(targetCross,sourceCross*.24)),neck),targetCross,unfurl);
            double center=Mix(horizontal?from.X+from.Width/2:from.Y+from.Height/2,horizontal?to.X+to.Width/2:to.Y+to.Height/2,neck);
            double sourceStart=horizontal?from.Top:from.Left,sourceEnd=horizontal?from.Bottom:from.Right,targetStart=horizontal?to.Top:to.Left,targetEnd=horizontal?to.Bottom:to.Right;
            double lead=Smooth(time/.65),follow=Smooth((time-.28)/.72);bool reverse=side==1||side==3;
            double start=Mix(sourceStart,targetStart,reverse?follow:lead),end=Mix(sourceEnd,targetEnd,reverse?lead:follow);
            // An overshot drop can put the whole source past the target. Use a regular shrink
            // for that frame instead of folding the image inside out.
            if(end<=start){start=Mix(sourceStart,targetStart,travel);end=Mix(sourceEnd,targetEnd,travel);}
            double length=end-start;
            return horizontal?new Rect(center-cross/2,start+a*length,cross,(b-a)*length):new Rect(start+a*length,center-cross/2,(b-a)*length,cross);
        }
        protected override void OnRender(DrawingContext dc)
        {
            double t=(double)GetValue(ProgressProperty),blend=Smooth((t-.80)/.20);
            if(t<=0){dc.DrawImage(source,from);return;}
            dc.PushOpacity(1-blend);
            if(slices.Length==1)dc.DrawImage(source,new Rect(Mix(from.X,to.X,Smooth(t)),Mix(from.Y,to.Y,Smooth(t)),Mix(from.Width,to.Width,Smooth(t)),Mix(from.Height,to.Height,Smooth(t))));
            else for(int i=0;i<slices.Length;i++)dc.DrawImage(slices[i],SliceBounds(divisions[i],divisions[i+1],t));
            dc.Pop();dc.PushOpacity(blend);dc.DrawImage(destination,to);dc.Pop();
        }
    }
}
