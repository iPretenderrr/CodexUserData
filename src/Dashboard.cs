using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace CodexUserData
{
    internal static class ModelColors
    {
        private static readonly string[] palette={"#83B9FF","#E3AD80","#63BBC6","#75CBAC","#E98FAD","#C9CB72","#91A3BB","#ABA9A7"};
        private static readonly string[] lightPalette={"#2563B9","#9A531D","#187B8B","#08765C","#AF3765","#727114","#536F91","#626775"};
        private static readonly Dictionary<string,Brush> brushes=new Dictionary<string,Brush>();
        private static readonly Dictionary<string,int> indices=new Dictionary<string,int>();
        internal static void RefreshTheme(){foreach(var pair in brushes)((SolidColorBrush)pair.Value).Color=(Color)ColorConverter.ConvertFromString((Theme.IsLight?lightPalette:palette)[indices[pair.Key]]);}
        internal static Brush For(string model)
        {
            model=model??"unknown";Brush brush;if(brushes.TryGetValue(model,out brush))return brush;
            int index;switch(model){case "gpt-6-astra":index=0;break;case "gpt-5.6-sol":case "gpt-5.6":index=1;break;case "gpt-5.5":index=2;break;case "gpt-5.6-terra":index=3;break;case "gpt-5.6-luna":index=4;break;case "gpt-5.3-codex":index=5;break;case "gpt-5.4":index=6;break;case "codex-auto-review":case "unknown":index=7;break;default:uint hash=2166136261;foreach(char c in model)hash=(hash^c)*16777619;index=(int)(hash%7);break;}
            brush=Theme.LiveColor((Theme.IsLight?lightPalette:palette)[index]);brushes[model]=brush;indices[model]=index;return brush;
        }
        internal static string Effort(string effort){return String.IsNullOrEmpty(effort)||effort=="unknown"?"未记录":effort;}
        internal static string Money(decimal value,long unpriced,long tokens){return value>0&&value<.01m?"<$0.01":"$"+value.ToString("N2",CultureInfo.InvariantCulture);}
    }
    internal sealed class ModelPanel : StackPanel
    {
        private string fingerprint;
        private UniformGrid entries;
        internal ModelPanel(){SizeChanged+=delegate{Reflow();};}
        private void Reflow(){if(entries!=null)entries.Columns=ActualWidth<540?1:ActualWidth<880?2:3;}
        internal void Apply(UsageSnapshot snapshot)
        {
            if(snapshot==null){fingerprint=null;Children.Clear();return;}
            string key=snapshot.InputTokens+":"+snapshot.CacheReadTokens+":"+snapshot.CacheCreationTokens+":"+snapshot.OutputTokens+"/"+String.Join("|",snapshot.Models.Select(m=>m.Model+":"+m.Effort+":"+m.Tokens+":"+m.EquivalentUsd+":"+m.UnpricedTokens));if(key==fingerprint)return;fingerprint=key;Children.Clear();
            var card=new Border{Background=Theme.Surface,CornerRadius=new CornerRadius(10),Padding=new Thickness(12),Margin=new Thickness(0,10,0,0)};var panel=new StackPanel();card.Child=panel;Children.Add(card);
            var head=new DockPanel();var money=Theme.Text(ModelColors.Money(snapshot.EquivalentUsd,snapshot.UnpricedTokens,snapshot.TotalTokens),20,Theme.Ink);money.FontWeight=FontWeights.SemiBold;DockPanel.SetDock(money,Dock.Right);head.Children.Add(money);head.Children.Add(Theme.Text("API 等效价值",12,Theme.Ink));panel.Children.Add(head);
            var note=Theme.Text("按模型单价估算 · 非实际账单",10,Theme.Muted);note.Margin=new Thickness(0,5,0,10);note.ToolTip=ApiPrices.Basis+"\n不含长上下文加价、Fast/Batch 差价、地区加价、工具费用；未公开价格的模型不猜价。";panel.Children.Add(note);
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
        internal void SetBody(UIElement body,string caption,string eyebrow,bool resize)
        {
            WindowStyle=WindowStyle.None;AllowsTransparency=true;Background=Brushes.Transparent;ResizeMode=resize?ResizeMode.CanResize:ResizeMode.NoResize;Theme.InstallStyles(this);
            var border=new Border{Background=Theme.WindowBackground,CornerRadius=new CornerRadius(14),BorderThickness=new Thickness(1),BorderBrush=Theme.Frame};var root=new Grid();border.Child=root;Content=border;
            root.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});root.RowDefinitions.Add(new RowDefinition());root.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
            var header=new DockPanel{Margin=new Thickness(18,12,12,10),Background=Brushes.Transparent};root.Children.Add(header);
            var close=Theme.ToolbarButton("close","关闭"+caption);close.Click+=delegate{Close();};DockPanel.SetDock(close,Dock.Right);header.Children.Add(close);
            if(resize){var maximize=Theme.ToolbarButton("maximize","最大化 / 还原");maximize.Click+=delegate{WindowState=WindowState==WindowState.Maximized?WindowState.Normal:WindowState.Maximized;};StateChanged+=delegate{maximize.Content=Theme.ToolbarIcon(WindowState==WindowState.Maximized?"restore":"maximize");};DockPanel.SetDock(maximize,Dock.Right);header.Children.Add(maximize);}
            var title=new StackPanel();title.Children.Add(Theme.Text("●  "+eyebrow,9,Theme.Muted));var text=Theme.Text(caption,17,Theme.Ink);text.Margin=new Thickness(0,5,0,0);title.Children.Add(text);header.Children.Add(title);
            WindowInteraction.Header(this,header,()=>{if(resize)WindowState=WindowState==WindowState.Maximized?WindowState.Normal:WindowState.Maximized;});WindowInteraction.Attach(this);
            Grid.SetRow(body,1);root.Children.Add(body);

        }
    }
}
