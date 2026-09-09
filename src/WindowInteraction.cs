using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace CodexUserData
{
    // Let Windows run the move/resize loop: no repeated managed Width/Left writes per mouse event.
    internal static class WindowInteraction
    {
        private sealed class MotionState
        {
            internal bool Attached;
            internal int Revision;
            internal FrameworkElement View;
            internal ScaleTransform Scale;
            internal TranslateTransform Shift;
        }
        private static readonly Dictionary<Window,MotionState> motions=new Dictionary<Window,MotionState>();
        [StructLayout(LayoutKind.Sequential)] private struct Bounds { public int Left,Top,Right,Bottom; }
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd,out Bounds rect);
        internal static void Attach(Window window,Action finished=null,Action starting=null)
        {
            EnableMotion(window);
            // Resize from the visible rounded border. Pixels outside the corner are intentionally
            // transparent; a full-window alpha fill would expose a gray rectangle on light desktops.
            window.SourceInitialized+=delegate
            {
                var source=HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);
                source.AddHook(delegate(IntPtr hwnd,int message,IntPtr wp,IntPtr lp,ref bool handled)
                {
                    if(message==0x231&&starting!=null)starting();
                    if(message==0x232 && finished!=null)finished(); // WM_EXITSIZEMOVE: save once after the native loop.
                    if(message!=0x84 || window.WindowState!=WindowState.Normal || window.ResizeMode==ResizeMode.NoResize)return IntPtr.Zero;
                    Bounds rect;if(!GetWindowRect(hwnd,out rect))return IntPtr.Zero;
                    long packed=lp.ToInt64();int x=(short)(packed&0xffff),y=(short)((packed>>16)&0xffff);
                    double scale=source.CompositionTarget.TransformToDevice.M11;int edge=(int)Math.Ceiling(7*scale);
                    bool left=x>=rect.Left&&x<rect.Left+edge,right=x<rect.Right&&x>=rect.Right-edge;
                    bool top=y>=rect.Top&&y<rect.Top+edge,bottom=y<rect.Bottom&&y>=rect.Bottom-edge;
                    int hit=top?(left?13:right?14:12):bottom?(left?16:right?17:15):left?10:right?11:0;
                    if(hit==0)return IntPtr.Zero;handled=true;return new IntPtr(hit);
                });
            };
        }
        internal static void EnableMotion(Window window){EnableMotion(window,true);}
        internal static void EnableMotion(Window window,bool automatic)
        {
            MotionState state;
            if(!motions.TryGetValue(window,out state)){state=new MotionState();motions[window]=state;}
            if(state.Attached)return;state.Attached=true;
            if(automatic)window.IsVisibleChanged+=delegate
            {
                if(window.IsVisible){PrepareReveal(window);window.Dispatcher.BeginInvoke(DispatcherPriority.Render,new Action(()=>Reveal(window)));}
                else state.Revision++;
            };
            window.Closed+=delegate{state.Revision++;motions.Remove(window);};
        }
        private static bool CanAnimate {get{return Theme.MotionAllowed;}}
        private static int FramesPerSecond {get{return Theme.MotionFrameRate;}}
        private static MotionState Visual(Window window)
        {
            MotionState state;
            if(!motions.TryGetValue(window,out state)){state=new MotionState();motions[window]=state;}
            var view=window.Content as FrameworkElement;
            if(view==null)return state;
            if(Object.ReferenceEquals(view,state.View))return state;
            state.View=view;state.Scale=new ScaleTransform(1,1);state.Shift=new TranslateTransform();
            var group=new TransformGroup();group.Children.Add(state.Scale);group.Children.Add(state.Shift);
            view.RenderTransformOrigin=new Point(.5,.5);view.RenderTransform=group;return state;
        }
        private static void Reset(MotionState state)
        {
            if(state.View!=null){state.View.BeginAnimation(UIElement.OpacityProperty,null);state.View.Opacity=1;}
            if(state.Scale!=null){state.Scale.BeginAnimation(ScaleTransform.ScaleXProperty,null);state.Scale.BeginAnimation(ScaleTransform.ScaleYProperty,null);state.Scale.ScaleX=state.Scale.ScaleY=1;}
            if(state.Shift!=null){state.Shift.BeginAnimation(TranslateTransform.YProperty,null);state.Shift.Y=0;}
        }
        private static DoubleAnimation Animate(double from,double to,int milliseconds,EasingMode easing)
        {
            var animation=new DoubleAnimation(from,to,TimeSpan.FromMilliseconds(milliseconds)){EasingFunction=new CubicEase{EasingMode=easing}};
            Timeline.SetDesiredFrameRate(animation,FramesPerSecond);return animation;
        }
        internal static void PrepareReveal(Window window)
        {
            if(window==null)return;var state=Visual(window);state.Revision++;Reset(state);if(!CanAnimate||state.View==null)return;
            // Hide the content before the first compositor frame. This prevents a full-size frame
            // appearing briefly before the opening animation starts.
            state.View.Opacity=0;state.Scale.ScaleX=state.Scale.ScaleY=.965;state.Shift.Y=7;
        }
        internal static void Reveal(Window window)
        {
            if(window==null||!window.IsVisible)return;var state=Visual(window);int revision=++state.Revision;
            if(!CanAnimate||state.View==null){Reset(state);return;}
            double opacity=state.View.Opacity,scale=state.Scale.ScaleX,shift=state.Shift.Y;
            state.View.BeginAnimation(UIElement.OpacityProperty,null);state.Scale.BeginAnimation(ScaleTransform.ScaleXProperty,null);state.Scale.BeginAnimation(ScaleTransform.ScaleYProperty,null);state.Shift.BeginAnimation(TranslateTransform.YProperty,null);
            state.View.Opacity=opacity;state.Scale.ScaleX=state.Scale.ScaleY=scale;state.Shift.Y=shift;
            // Animate only the existing root visual, keeping native bounds and resize hit testing stable.
            var fade=Animate(opacity,1,190,EasingMode.EaseOut);var grow=Animate(scale,1,210,EasingMode.EaseOut);var rise=Animate(shift,0,210,EasingMode.EaseOut);
            fade.Completed+=delegate{if(state.Revision==revision)Reset(state);};
            state.View.BeginAnimation(UIElement.OpacityProperty,fade);state.Scale.BeginAnimation(ScaleTransform.ScaleXProperty,grow);state.Scale.BeginAnimation(ScaleTransform.ScaleYProperty,grow);state.Shift.BeginAnimation(TranslateTransform.YProperty,rise);
        }
        private static void Dismiss(Window window,bool hide,Action after)
        {
            if(window==null||!window.IsVisible){if(after!=null)after();return;}var state=Visual(window);int revision=++state.Revision;
            if(!CanAnimate||state.View==null){if(hide)window.Hide();Reset(state);if(after!=null)after();return;}
            double opacity=state.View.Opacity,scale=state.Scale==null?1:state.Scale.ScaleX,shift=state.Shift==null?0:state.Shift.Y;
            var fade=Animate(opacity,0,135,EasingMode.EaseIn);var shrink=Animate(scale,.982,145,EasingMode.EaseIn);var drop=Animate(shift,5,145,EasingMode.EaseIn);
            fade.Completed+=delegate{if(state.Revision!=revision)return;if(hide)window.Hide();Reset(state);if(after!=null)after();};
            state.View.BeginAnimation(UIElement.OpacityProperty,fade);state.Scale.BeginAnimation(ScaleTransform.ScaleXProperty,shrink);state.Scale.BeginAnimation(ScaleTransform.ScaleYProperty,shrink);state.Shift.BeginAnimation(TranslateTransform.YProperty,drop);
        }
        internal static void Hide(Window window,Action after=null){Dismiss(window,true,after);}
        internal static void Close(Window window)
        {
            Dismiss(window,false,()=>{if(window!=null)window.Close();});
        }
        internal static void CompleteDialog(Window window,bool result)
        {
            Dismiss(window,false,()=>{try{window.DialogResult=result;}catch(InvalidOperationException){window.Close();}});
        }
        internal static void Header(Window window,FrameworkElement header,Action doubleClick,Action finished=null)
        {
            header.MouseLeftButtonDown+=delegate(object sender,MouseButtonEventArgs e)
            {
                // OriginalSource can be a Run or another content element, not only a Visual/TextBlock.
                DependencyObject node=e.OriginalSource as DependencyObject;
                while(node!=null && node!=header)
                {
                    if(node is ButtonBase || node is TextBoxBase || node is Selector || node is Thumb)return;
                    var content=node as FrameworkContentElement;
                    node=content!=null?content.Parent:VisualTreeHelper.GetParent(node);
                }
                if(e.ChangedButton!=MouseButton.Left||e.ButtonState!=MouseButtonState.Pressed)return;
                e.Handled=true;
                if(e.ClickCount==2 && doubleClick!=null){doubleClick();return;}
                try{window.DragMove();if(finished!=null)finished();}
                catch(InvalidOperationException){/* Button release can race the start of the native move loop. */}
            };
        }
    }
}
