using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace CodexUserData
{
    // Let Windows run the move/resize loop: no repeated managed Width/Left writes per mouse event.
    internal static class WindowInteraction
    {
        [StructLayout(LayoutKind.Sequential)] private struct Bounds { public int Left,Top,Right,Bottom; }
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd,out Bounds rect);
        internal static void Attach(Window window,Action finished=null,Action starting=null)
        {
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
