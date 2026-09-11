using System;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
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
            internal int TransferRevision;
            internal FrameworkElement View;
            internal ScaleTransform Scale;
            internal TranslateTransform Shift;
            internal double NextRevealScale=.94;
            internal bool Prepared;
            internal bool HoldingReveal;
            internal bool Dismissing;
            internal bool NativeFade;
            internal double NativeOpacity;
            internal bool Closed;
            internal bool CompletingDialog,DialogWasEnabled;
        }
        // Keep closed-state guards without retaining disposed windows or their visual trees.
        private static readonly ConditionalWeakTable<Window,MotionState> motions=new ConditionalWeakTable<Window,MotionState>();
        private static readonly DependencyProperty NativeProgressProperty=DependencyProperty.RegisterAttached("NativeProgress",typeof(double),typeof(WindowInteraction),new PropertyMetadata(1.0,NativeProgressChanged));
        private static void NativeProgressChanged(DependencyObject sender,DependencyPropertyChangedEventArgs args)
        {
            var window=sender as Window;MotionState state;
            if(window!=null&&motions.TryGetValue(window,out state)&&!state.Closed)window.Opacity=state.NativeOpacity*(double)args.NewValue;
        }
        private static bool IsClosed(Window window){MotionState state;return window==null||motions.TryGetValue(window,out state)&&state.Closed;}
        // Work areas and requested bounds are physical pixels. Keep this pure so mixed-DPI
        // and small-screen constraints can be verified without moving desktop windows.
        internal static Rect FitBounds(Rect requested,Rect work,double margin)
        {
            double xInset=Math.Min(Math.Max(0,margin),Math.Max(0,(work.Width-1)/2));
            double yInset=Math.Min(Math.Max(0,margin),Math.Max(0,(work.Height-1)/2));
            double width=Math.Min(Math.Max(1,Math.Ceiling(requested.Width)),Math.Max(1,Math.Floor(work.Width-2*xInset)));
            double height=Math.Min(Math.Max(1,Math.Ceiling(requested.Height)),Math.Max(1,Math.Floor(work.Height-2*yInset)));
            return new Rect(Math.Max(work.Left+xInset,Math.Min(work.Right-xInset-width,requested.Left)),Math.Max(work.Top+yInset,Math.Min(work.Bottom-yInset-height,requested.Top)),width,height);
        }
        internal static void SetOpacity(Window window,double opacity)
        {
            if(IsClosed(window))return;var state=Visual(window);state.NativeOpacity=Theme.Bound(opacity,0,1,1);
            // User opacity is a base value; refreshes must never overwrite an entrance gate.
            window.Opacity=state.NativeOpacity*(state.NativeFade?(double)window.GetValue(NativeProgressProperty):1);
        }
        internal static void FadeNative(Window window,double target,int milliseconds,Action completed=null)
        {
            if(IsClosed(window))return;var state=Visual(window);
            if(!state.NativeFade){state.NativeOpacity=window.Opacity;state.NativeFade=true;}
            double from=(double)window.GetValue(NativeProgressProperty);
            var fade=Animate(from,target,milliseconds,target==0?EasingMode.EaseIn:EasingMode.EaseOut);
            if(completed!=null)fade.Completed+=delegate{if(!state.Closed)completed();};
            window.BeginAnimation(NativeProgressProperty,fade);
        }
        internal static void CompleteNativeFade(Window window)
        {
            MotionState state;if(IsClosed(window)||!motions.TryGetValue(window,out state)||!state.NativeFade)return;
            window.BeginAnimation(NativeProgressProperty,null);window.SetValue(NativeProgressProperty,1.0);window.Opacity=state.NativeOpacity;state.NativeFade=false;
        }
        [StructLayout(LayoutKind.Sequential)] private struct Bounds { public int Left,Top,Right,Bottom; }
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd,out Bounds rect);
        internal static void Attach(Window window,Action finished=null,Action starting=null)
        {
            EnableMotion(window);
            // Route the keyboard close through the same transition as the title button.
            // Do not intercept Closing: shutdown and owner disposal must remain synchronous.
            window.PreviewKeyDown+=delegate(object sender,KeyEventArgs e)
            {
                if(e.Key==Key.System&&e.SystemKey==Key.F4){e.Handled=true;Close(window);}
            };
            // Resize from the visible rounded border. Pixels outside the corner are intentionally
            // transparent; a full-window alpha fill would expose a gray rectangle on light desktops.
            window.SourceInitialized+=delegate
            {
                var source=HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);
                source.AddHook(delegate(IntPtr hwnd,int message,IntPtr wp,IntPtr lp,ref bool handled)
                {
                    if(message==0x231){CompleteReveal(window);if(starting!=null)starting();}
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
            if(IsClosed(window))return;var state=Visual(window);
            if(state.Attached)return;state.Attached=true;
            if(automatic)window.IsVisibleChanged+=delegate
            {
                if(window.IsVisible){if(!state.Prepared)PrepareReveal(window);if(!state.HoldingReveal)QueueReveal(window);}
                else{state.Revision++;state.Prepared=false;state.HoldingReveal=false;if(state.CompletingDialog){state.CompletingDialog=false;window.IsEnabled=state.DialogWasEnabled;}Reset(window,state);}
            };
            window.Closed+=delegate{state.Revision++;state.Prepared=false;Reset(window,state);state.Closed=true;};
        }
        private static bool CanAnimate {get{return Theme.MotionAllowed;}}
        private static int FramesPerSecond {get{return Theme.MotionFrameRate;}}
        private static MotionState Visual(Window window)
        {
            var state=motions.GetValue(window,w=>new MotionState{NativeOpacity=w.Opacity});
            var view=window.Content as FrameworkElement;
            if(view==null)return state;
            if(Object.ReferenceEquals(view,state.View))return state;
            state.View=view;state.Scale=new ScaleTransform(1,1);state.Shift=new TranslateTransform();
            var group=new TransformGroup();group.Children.Add(state.Scale);group.Children.Add(state.Shift);
            view.RenderTransformOrigin=new Point(.5,.5);view.RenderTransform=group;return state;
        }
        private static void Reset(Window window,MotionState state)
        {
            state.Dismissing=false;
            CompleteNativeFade(window);
            if(state.View!=null){state.View.BeginAnimation(UIElement.OpacityProperty,null);state.View.Opacity=1;}
            if(state.Scale!=null){state.Scale.BeginAnimation(ScaleTransform.ScaleXProperty,null);state.Scale.BeginAnimation(ScaleTransform.ScaleYProperty,null);state.Scale.ScaleX=state.Scale.ScaleY=1;}
            if(state.Shift!=null){state.Shift.BeginAnimation(TranslateTransform.YProperty,null);state.Shift.Y=0;}
        }
        private static DoubleAnimation Animate(double from,double to,int milliseconds,EasingMode easing)
        {
            var animation=new DoubleAnimation(from,to,Theme.MotionTime(milliseconds)){EasingFunction=new CubicEase{EasingMode=easing}};
            Timeline.SetDesiredFrameRate(animation,FramesPerSecond);return animation;
        }
        private static void PrepareNativeReveal(Window window,MotionState state)
        {
            if(!CanAnimate||!IsBrowser(window)||state.NativeFade)return;
            state.NativeOpacity=window.Opacity;state.NativeFade=true;
            window.BeginAnimation(NativeProgressProperty,null);window.SetValue(NativeProgressProperty,0.0);
        }
        internal static void PrepareReveal(Window window)
        {
            if(IsClosed(window))return;var state=Visual(window);if(state.CompletingDialog)return;state.Revision++;state.Prepared=true;Reset(window,state);if(!CanAnimate||state.View==null)return;
            // Hide the content before the first compositor frame. This prevents a full-size frame
            // appearing briefly before the opening animation starts.
            PrepareNativeReveal(window,state);state.View.Opacity=0;state.Scale.ScaleX=state.Scale.ScaleY=state.NextRevealScale;state.NextRevealScale=.94;state.Shift.Y=7;
        }
        internal static void CompleteReveal(Window window)
        {
            if(IsClosed(window))return;var state=Visual(window);if(state.CompletingDialog)return;state.Revision++;state.Prepared=false;state.HoldingReveal=false;Reset(window,state);
        }
        internal static void ResumeReveal(Window window)
        {
            // Repeated activation must not jump an in-progress entrance to full opacity.
            // Only reverse an outgoing transition when the user asks to bring it back.
            if(IsClosed(window))return;var state=Visual(window);if(!state.CompletingDialog&&state.Dismissing)Reveal(window);
        }
        private static void QueueReveal(Window window)
        {
            var state=Visual(window);int revision=state.Revision;
            // Loaded runs after pending layout/render work. Prepare before Show, then wait for
            // the final native size before growing the view; no full-opacity first frame.
            window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded,new Action(async delegate
            {
                if(state.Closed||state.Revision!=revision||!window.IsVisible)return;
                window.UpdateLayout();var ball=window as FloatingBall;
                if(ball!=null&&ball.IsCustom)await ball.WaitForPresentationAsync();
                if(!state.Closed&&state.Revision==revision&&window.IsVisible)Reveal(window);
            }));
        }
        internal static void ChangeShape(Window window,Action change)
        {
            if(IsClosed(window))return;var state=Visual(window);if(state.CompletingDialog)return;int revision=++state.Revision;
            if(!CanAnimate||!window.IsVisible||state.View==null){Reset(window,state);change();return;}
            // Browser-backed shapes still need a fully hidden midpoint while their first frame
            // is prepared. Native vector forms retain part of the old frame, avoiding a black
            // flash while the same HWND changes dimensions.
            bool browser=IsBrowser(window);double midpointOpacity=browser?0:.34,midpointScale=browser?.86:.96;
            state.Dismissing=true;var fade=Animate(state.View.Opacity,midpointOpacity,browser?105:85,EasingMode.EaseIn);
            var shrink=Animate(state.Scale.ScaleX,midpointScale,browser?105:85,EasingMode.EaseIn);
            fade.Completed+=delegate
            {
                if(state.Closed||state.Revision!=revision||!window.IsVisible)return;
                try{change();}catch{Reset(window,state);throw;}state=Visual(window);state.Revision++;PrepareNativeReveal(window,state);
                state.View.BeginAnimation(UIElement.OpacityProperty,null);state.Scale.BeginAnimation(ScaleTransform.ScaleXProperty,null);state.Scale.BeginAnimation(ScaleTransform.ScaleYProperty,null);state.Shift.BeginAnimation(TranslateTransform.YProperty,null);
                state.View.Opacity=midpointOpacity;state.Scale.ScaleX=state.Scale.ScaleY=midpointScale;state.Shift.Y=0;
                window.UpdateLayout();if(IsBrowser(window))QueueReveal(window);else Reveal(window);
            };
            if(browser||state.NativeFade)FadeNative(window,0,105);
            state.View.BeginAnimation(UIElement.OpacityProperty,fade);state.Scale.BeginAnimation(ScaleTransform.ScaleXProperty,shrink);state.Scale.BeginAnimation(ScaleTransform.ScaleYProperty,shrink);
        }
        internal static void ToggleMaximize(Window window)
        {
            var target=window.WindowState==WindowState.Maximized?WindowState.Normal:WindowState.Maximized;
            ChangeShape(window,()=>window.WindowState=target);
        }
        internal static void ShowFrom(Window target,Window source,Action prepare,Action shown)
        {
            if(IsClosed(target))return;
            var state=Visual(target);if(state.CompletingDialog||!IsClosed(source)&&Visual(source).CompletingDialog)return;int transferRevision=++state.TransferRevision;
            // Mount the destination's prepared first frame before dismissing the source. The
            // two native windows then overlap during their short fades, so desktop pixels never
            // show through between forms. HoldingReveal suppresses the automatic Show handler.
            bool alreadyVisible=target.IsVisible;
            if(!alreadyVisible)
            {
                state.HoldingReveal=true;
                try
                {
                    state.NextRevealScale=.86;PrepareReveal(target);
                    if(prepare!=null)prepare();if(state.Closed)return;target.UpdateLayout();target.Show();
                }
                catch
                {
                    // A failed prepare must not leave an unseen window permanently holding its
                    // automatic reveal. A later ordinary Show or retry starts from clean state.
                    state.HoldingReveal=false;state.Prepared=false;Reset(target,state);throw;
                }
            }
            else if(prepare!=null)prepare();
            MotionState current;if(state.Closed||!motions.TryGetValue(target,out current)||current!=state)return;
            state.HoldingReveal=false;
            if(alreadyVisible)ResumeReveal(target);else if(IsBrowser(target))QueueReveal(target);else Reveal(target);
            Action finished=delegate
            {
                MotionState latest;if(state.Closed||!motions.TryGetValue(target,out latest)||latest!=state||state.TransferRevision!=transferRevision)return;
                if(shown!=null)shown();
            };
            // Invalidating the destination first cancels any earlier transfer in the opposite
            // direction. A rapid main -> ball -> main sequence can only show its last target.
            if(source==target)finished();else Dismiss(source,true,finished);
        }
        internal static void Reveal(Window window)
        {
            if(IsClosed(window)||!window.IsVisible)return;var state=Visual(window);if(state.CompletingDialog)return;state.Prepared=false;state.Dismissing=false;int revision=++state.Revision;
            bool native=state.NativeFade;
            if(!CanAnimate||state.View==null){Reset(window,state);return;}
            double opacity=state.View.Opacity,scale=state.Scale.ScaleX,shift=state.Shift.Y;
            state.View.BeginAnimation(UIElement.OpacityProperty,null);state.Scale.BeginAnimation(ScaleTransform.ScaleXProperty,null);state.Scale.BeginAnimation(ScaleTransform.ScaleYProperty,null);state.Shift.BeginAnimation(TranslateTransform.YProperty,null);
            state.View.Opacity=opacity;state.Scale.ScaleX=state.Scale.ScaleY=scale;state.Shift.Y=shift;
            // Animate only the existing root visual, keeping native bounds and resize hit testing stable.
            var fade=Animate(opacity,1,190,EasingMode.EaseOut);var grow=Animate(scale,1,210,EasingMode.EaseOut);var rise=Animate(shift,0,210,EasingMode.EaseOut);
            grow.Completed+=delegate{if(!state.Closed&&state.Revision==revision){state.Revision++;Reset(window,state);}};
            state.View.BeginAnimation(UIElement.OpacityProperty,fade);state.Scale.BeginAnimation(ScaleTransform.ScaleXProperty,grow);state.Scale.BeginAnimation(ScaleTransform.ScaleYProperty,grow);state.Shift.BeginAnimation(TranslateTransform.YProperty,rise);
            if(native)FadeNative(window,1,190);
        }
        private static void Dismiss(Window window,bool hide,Action after)
        {
            if(IsClosed(window)){if(after!=null)after();return;}var state=Visual(window);state.TransferRevision++;int revision=++state.Revision;
            if(!window.IsVisible){Reset(window,state);if(after!=null)after();return;}
            if(!CanAnimate||state.View==null){if(hide)window.Hide();Reset(window,state);if(after!=null)after();return;}
            state.Dismissing=true;
            double opacity=state.View.Opacity,scale=state.Scale==null?1:state.Scale.ScaleX,shift=state.Shift==null?0:state.Shift.Y;
            var fade=Animate(opacity,0,135,EasingMode.EaseIn);var shrink=Animate(scale,.90,145,EasingMode.EaseIn);var drop=Animate(shift,5,145,EasingMode.EaseIn);
            bool browser=IsBrowser(window);
            shrink.Completed+=delegate{if(state.Closed||state.Revision!=revision)return;state.Revision++;state.Prepared=false;if(hide)window.Hide();Reset(window,state);if(after!=null)after();};
            if(browser||state.NativeFade)FadeNative(window,0,135);
            else state.View.BeginAnimation(UIElement.OpacityProperty,fade);
            state.Scale.BeginAnimation(ScaleTransform.ScaleXProperty,shrink);state.Scale.BeginAnimation(ScaleTransform.ScaleYProperty,shrink);state.Shift.BeginAnimation(TranslateTransform.YProperty,drop);
        }
        private static bool IsBrowser(Window window){var ball=window as FloatingBall;return ball!=null&&ball.IsCustom;}
        internal static void Hide(Window window,Action after=null){if(!IsClosed(window)&&Visual(window).CompletingDialog)return;Dismiss(window,true,after);}
        internal static void Close(Window window)
        {
            if(IsClosed(window)||Visual(window).CompletingDialog)return;Dismiss(window,false,()=>{if(!IsClosed(window))window.Close();});
        }
        internal static void CompleteDialog(Window window,bool result)
        {CompleteDialog(window,result,null);}
        internal static void CompleteDialog(Window window,bool result,Func<bool> beforeClose)
        {
            if(IsClosed(window))return;var state=Visual(window);if(state.CompletingDialog)return;
            // Commit exactly once after the exit animation. A second click, Escape or title
            // close must not cancel a save after its persistent side effects have begun.
            state.CompletingDialog=true;state.DialogWasEnabled=window.IsEnabled;window.IsEnabled=false;
            Action reopen=delegate{if(state.Closed)return;state.CompletingDialog=false;window.IsEnabled=state.DialogWasEnabled;Reveal(window);};
            Dismiss(window,false,delegate
            {
                if(state.Closed)return;
                try{if(beforeClose!=null&&!beforeClose()){reopen();return;}}
                catch{reopen();throw;}
                if(state.Closed)return;
                try{window.DialogResult=result;}catch(InvalidOperationException){window.Close();}
                // An application Closing handler may reject closing. Restore interaction
                // rather than leaving a live dialog permanently disabled.
                if(!state.Closed&&window.IsVisible)reopen();
            });
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
                CompleteReveal(window);
                try{window.DragMove();if(finished!=null)finished();}
                catch(InvalidOperationException){/* Button release can race the start of the native move loop. */}
            };
        }
    }
}
