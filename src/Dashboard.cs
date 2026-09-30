using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace CodexUserData
{
    internal sealed class ModelPanel : StackPanel
    {
        private string fingerprint;
        private UniformGrid entries;
        internal ModelPanel(){SizeChanged+=delegate{Reflow();};}
        private void Reflow(){if(entries!=null)entries.Columns=ActualWidth<540?1:ActualWidth<880?2:3;}
        internal void Apply(UsageSnapshot snapshot)
        {
            if(snapshot==null){fingerprint=null;Children.Clear();entries=null;return;}
            string key=snapshot.InputTokens+":"+snapshot.CacheReadTokens+":"+snapshot.CacheCreationTokens+":"+snapshot.OutputTokens+"/"+String.Join("|",snapshot.Models.Select(m=>m.Model+":"+m.Effort+":"+m.Tokens+":"+m.EquivalentUsd+":"+m.UnpricedTokens));if(key==fingerprint)return;fingerprint=key;Children.Clear();
            var card=new Border{Background=Theme.Surface,CornerRadius=new CornerRadius(10),Padding=new Thickness(12),Margin=new Thickness(0,10,0,0)};var panel=new StackPanel();card.Child=panel;Children.Add(card);
            var head=new Grid();head.ColumnDefinitions.Add(new ColumnDefinition());head.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});var money=Theme.Text(ModelColors.Money(snapshot.EquivalentUsd,snapshot.UnpricedTokens,snapshot.TotalTokens),20,Theme.Ink);money.FontWeight=FontWeights.SemiBold;Grid.SetColumn(money,1);head.Children.Add(money);var heading=Theme.Text("API 估算费用",12,Theme.Ink);heading.TextWrapping=TextWrapping.Wrap;heading.Margin=new Thickness(0,0,8,0);head.Children.Add(heading);panel.Children.Add(head);
            var note=Theme.Text("按模型单价估算 · 非实际账单",10,Theme.Muted);note.Margin=new Thickness(0,5,0,10);note.TextWrapping=TextWrapping.Wrap;note.ToolTip=ApiPrices.Basis+"\n不含长上下文加价、Fast/Batch/Flex 差价、地区加价、工具费用；没有价格的模型或计价项按 0 估算，可在设置中补充。";panel.Children.Add(note);
            var mix=new Grid{Height=5,Margin=new Thickness(0,0,0,7)};long cached=snapshot.CacheReadTokens+snapshot.CacheCreationTokens;long[] sizes={snapshot.InputTokens,cached,snapshot.OutputTokens};string[] colors={"#75CBAC","#E3AD80","#83B9FF"};
            for(int i=0;i<3;i++){mix.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(Math.Max(.001,sizes[i]),GridUnitType.Star)});var fill=new Border{Background=Theme.B(colors[i])};Grid.SetColumn(fill,i);mix.Children.Add(fill);}panel.Children.Add(mix);
            var mixLabel=Theme.Text("未缓存 "+TokenText.Compact(sizes[0])+" · 缓存 "+TokenText.Compact(sizes[1])+" · 输出 "+TokenText.Compact(sizes[2]),10,Theme.Muted);mixLabel.TextWrapping=TextWrapping.Wrap;panel.Children.Add(mixLabel);
            entries=new UniformGrid{Columns=1,Margin=new Thickness(-3,8,-3,-3)};panel.Children.Add(entries);
            foreach(var row in snapshot.Models.OrderByDescending(m=>m.Tokens))
            {
                var tile=new StackPanel();entries.Children.Add(new Border{Background=Theme.Background,CornerRadius=new CornerRadius(7),Padding=new Thickness(9,7,9,7),Margin=new Thickness(3),Child=tile});
                var line=new Grid();line.ColumnDefinitions.Add(new ColumnDefinition());line.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});tile.Children.Add(line);
                var title=Theme.Text("● "+row.Model,11,ModelColors.For(row.Model));title.TextTrimming=TextTrimming.CharacterEllipsis;title.ToolTip=row.Model;title.Margin=new Thickness(0,0,5,0);line.Children.Add(title);
                var total=Theme.Text(TokenText.Compact(row.Tokens),12,Theme.Ink);total.FontWeight=FontWeights.SemiBold;Grid.SetColumn(total,1);line.Children.Add(total);
                var extra=new DockPanel{Margin=new Thickness(0,4,0,0)};tile.Children.Add(extra);
                var cost=Theme.Text(ModelColors.Money(row.EquivalentUsd,row.UnpricedTokens,row.Tokens),10,Theme.Muted);DockPanel.SetDock(cost,Dock.Right);extra.Children.Add(cost);
                var effort=Theme.Text(ModelColors.Effort(row.Effort)+" · "+(snapshot.TotalTokens>0?100.0*row.Tokens/snapshot.TotalTokens:0).ToString("0.#")+"%",10,Theme.Muted);extra.Children.Add(effort);
            }
            Reflow();
        }
    }
    internal class StyledWindow : Window
    {
        internal bool UseNativeFrame {get;set;}
        internal void SetBody(UIElement body,string caption,string eyebrow,bool resize)
        {
            bool native=UseNativeFrame&&resize;
            WindowStyle=native?WindowStyle.SingleBorderWindow:WindowStyle.None;AllowsTransparency=!native;Background=native?(Brush)Theme.WindowBackground:Brushes.Transparent;ResizeMode=resize?ResizeMode.CanResize:ResizeMode.NoResize;Theme.InstallStyles(this);
            // Large chart windows keep a native (non-layered) frame so Windows
            // can compose maximize/restore as one transition. Other floating
            // and translucent dialogs retain their existing presentation.
            if(native)System.Windows.Shell.WindowChrome.SetWindowChrome(this,new System.Windows.Shell.WindowChrome{CaptionHeight=0,ResizeBorderThickness=new Thickness(7),GlassFrameThickness=new Thickness(0),CornerRadius=new CornerRadius(14),UseAeroCaptionButtons=false});
            var border=new Border{Background=Theme.WindowBackground,CornerRadius=new CornerRadius(14),BorderThickness=new Thickness(1),BorderBrush=Theme.Frame};var root=new Grid();border.Child=root;Content=border;
            root.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});root.RowDefinitions.Add(new RowDefinition());root.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
            var header=new DockPanel{Margin=new Thickness(18,12,12,10),Background=Brushes.Transparent};root.Children.Add(header);
            var close=Theme.ToolbarButton("close","关闭"+caption);close.Click+=delegate{WindowInteraction.Close(this);};DockPanel.SetDock(close,Dock.Right);header.Children.Add(close);
            if(resize)
            {
                var maximize=Theme.ToolbarButton("maximize","最大化 / 还原");maximize.Click+=delegate{WindowInteraction.ToggleMaximize(this);};
                Action syncFrame=delegate
                {
                    bool maximized=WindowState==WindowState.Maximized;
                    border.CornerRadius=new CornerRadius(maximized?0:14);
                    if(native)border.Margin=WindowInteraction.MaximizedFramePadding(this);
                    maximize.Content=Theme.ToolbarIcon(maximized?"restore":"maximize");
                };
                StateChanged+=delegate{WindowInteraction.CompleteReveal(this);syncFrame();};
                if(native)SizeChanged+=delegate{border.Margin=WindowInteraction.MaximizedFramePadding(this);};
                syncFrame();DockPanel.SetDock(maximize,Dock.Right);header.Children.Add(maximize);
            }
            var title=new StackPanel();title.Children.Add(Theme.Text("●  "+eyebrow,9,Theme.Muted));var text=Theme.Text(caption,17,Theme.Ink);text.Margin=new Thickness(0,5,0,0);title.Children.Add(text);header.Children.Add(title);
            WindowInteraction.Header(this,header,()=>{if(resize)WindowInteraction.ToggleMaximize(this);});WindowInteraction.Attach(this);
            Grid.SetRow(body,1);root.Children.Add(body);

        }
    }
}
