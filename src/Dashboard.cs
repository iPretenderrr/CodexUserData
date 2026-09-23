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
        private static readonly string[] palette={"#83B9FF","#E3AD80","#63BBC6","#75CBAC","#E98FAD","#C9CB72","#91A3BB","#ABA9A7","#42C7E8","#B58AF3"};
        private static readonly string[] lightPalette={"#2563B9","#9A531D","#187B8B","#08765C","#AF3765","#727114","#536F91","#626775","#087E9C","#6E48B5"};
        private static readonly Color[] slotDark=Enumerable.Range(0,ModelColorRegistry.CandidateCount).Select(slot=>CreateSlotColor(slot,false)).ToArray();
        private static readonly Color[] slotLight=Enumerable.Range(0,ModelColorRegistry.CandidateCount).Select(slot=>CreateSlotColor(slot,true)).ToArray();
        private static readonly Lab[] fixedDark=palette.Select(value=>Oklab((Color)ColorConverter.ConvertFromString(value))).ToArray();
        private static readonly Lab[] fixedLight=lightPalette.Select(value=>Oklab((Color)ColorConverter.ConvertFromString(value))).ToArray();
        private static readonly Lab[] slotDarkLab=slotDark.Select(Oklab).ToArray(),slotLightLab=slotLight.Select(Oklab).ToArray();
        private static readonly Dictionary<string,Brush> brushes=new Dictionary<string,Brush>(StringComparer.OrdinalIgnoreCase);
        internal static void RefreshTheme(){foreach(var pair in brushes)((SolidColorBrush)pair.Value).Color=(Color)ColorConverter.ConvertFromString(ColorHex(pair.Key,Theme.IsLight));}
        internal static Brush For(string model)
        {
            model=model??"unknown";Brush brush;if(brushes.TryGetValue(model,out brush))return brush;EnsureModels(new[]{model});
            brush=Theme.LiveColor(ColorHex(model,Theme.IsLight));brushes[model]=brush;return brush;
        }
        internal static void EnsureModels(IEnumerable<string> models)
        {
            if(models==null)return;ModelColorRegistry.Ensure(models.Where(model=>FixedIndex(Normalize(model))<0),(model,used)=>ChooseSlot(model,used));
        }
        // ColorHex remains read-only so diagnostics and protected probes cannot rewrite user
        // data. The main UI registers new names before creating their cached WPF brushes.
        internal static string ColorHex(string model,bool light)
        {
            model=Normalize(model);int index=FixedIndex(model);
            if(index>=0)return (light?lightPalette:palette)[index];
            int slot;if(ModelColorRegistry.TryGet(model,out slot))return SlotHex(slot,light);
            // Read-only helpers still need a deterministic fallback when a model has not yet
            // been registered by the main UI process.
            uint hash=StableHash(model);
            double hue=hash%360,saturation=(light?58:66)+((hash>>9)%9),luminance=(light?38:66)+((hash>>17)%7);
            return Hex(Hsl(hue,saturation/100,luminance/100));
        }
        private static int ChooseSlot(string model,int[] used)
        {
            var occupied=new HashSet<int>(used);int start=(int)(StableHash(model)%ModelColorRegistry.CandidateCount),best=-1;double bestScore=-1;
            for(int offset=0;offset<ModelColorRegistry.CandidateCount;offset++)
            {
                int slot=(start+offset)%ModelColorRegistry.CandidateCount;if(occupied.Contains(slot)&&occupied.Count<ModelColorRegistry.CandidateCount)continue;
                double score=Double.MaxValue;
                for(int i=0;i<fixedDark.Length;i++)score=Math.Min(score,Math.Min(Distance(slotDarkLab[slot],fixedDark[i]),Distance(slotLightLab[slot],fixedLight[i])));
                foreach(int existing in occupied)score=Math.Min(score,Math.Min(Distance(slotDarkLab[slot],slotDarkLab[existing]),Distance(slotLightLab[slot],slotLightLab[existing])));
                if(score>bestScore){bestScore=score;best=slot;}
            }
            return best>=0?best:start;
        }
        internal static string SlotHex(int slot,bool light){return Hex(light?slotLight[slot]:slotDark[slot]);}
        private static Color CreateSlotColor(int slot,bool light)
        {
            double hue=(slot*137.50776405003785+11)%360,saturation=(light?60:68)+(slot%3)*5,luminance=(light?35:62)+((slot/3)%2)*8;
            Color color=Hsl(hue,saturation/100,luminance/100),background=(Color)ColorConverter.ConvertFromString(light?"#E7EFF7":"#2B3846");
            // Model names use small text, so tune only generated colors until they meet
            // WCAG AA contrast against the lower-contrast hover surface in both themes.
            double step=light?-0.02:0.02;
            while(Contrast(color,background)<4.5&&luminance>8&&luminance<92){luminance+=step*100;color=Hsl(hue,saturation/100,luminance/100);}
            return color;
        }
        private struct Lab {internal double L,A,B;}
        private static double Distance(Lab a,Lab b){double l=a.L-b.L,x=a.A-b.A,y=a.B-b.B;return l*l+x*x+y*y;}
        private static Lab Oklab(Color color)
        {
            double r=Linear(color.R/255.0),g=Linear(color.G/255.0),b=Linear(color.B/255.0);
            double l=Math.Pow(.4122214708*r+.5363325363*g+.0514459929*b,1.0/3),m=Math.Pow(.2119034982*r+.6806995451*g+.1073969566*b,1.0/3),s=Math.Pow(.0883024619*r+.2817188376*g+.6299787005*b,1.0/3);
            return new Lab{L=.2104542553*l+.793617785*m-.0040720468*s,A=1.9779984951*l-2.428592205*m+.4505937099*s,B=.0259040371*l+.7827717662*m-.808675766*s};
        }
        private static double Linear(double value){return value<=.04045?value/12.92:Math.Pow((value+.055)/1.055,2.4);}
        internal static double Contrast(Color first,Color second)
        {
            double a=.2126*Linear(first.R/255.0)+.7152*Linear(first.G/255.0)+.0722*Linear(first.B/255.0),b=.2126*Linear(second.R/255.0)+.7152*Linear(second.G/255.0)+.0722*Linear(second.B/255.0);
            return (Math.Max(a,b)+.05)/(Math.Min(a,b)+.05);
        }
        private static string Normalize(string model){return String.IsNullOrWhiteSpace(model)?"unknown":model.Trim().ToLowerInvariant();}
        private static uint StableHash(string model){uint hash=2166136261;foreach(char c in model)hash=(hash^c)*16777619;return hash;}
        private static int FixedIndex(string model)
        {
            switch(model){case "gpt-6-astra":return 0;case "gpt-5.6-sol":case "gpt-5.6":return 1;case "gpt-5.5":return 2;case "gpt-5.6-terra":return 3;case "gpt-5.6-luna":return 4;case "gpt-5.3-codex":return 5;case "gpt-5.4":return 6;case "codex-auto-review":case "unknown":return 7;case "gpt-6-sol":return 8;case "gpt-6-luna":return 9;default:return -1;}
        }
        private static Color Hsl(double hue,double saturation,double lightness)
        {
            double chroma=(1-Math.Abs(2*lightness-1))*saturation,x=chroma*(1-Math.Abs((hue/60)%2-1)),m=lightness-chroma/2,r=0,g=0,b=0;
            if(hue<60){r=chroma;g=x;}else if(hue<120){r=x;g=chroma;}else if(hue<180){g=chroma;b=x;}else if(hue<240){g=x;b=chroma;}else if(hue<300){r=x;b=chroma;}else{r=chroma;b=x;}
            return Color.FromRgb((byte)Math.Round((r+m)*255),(byte)Math.Round((g+m)*255),(byte)Math.Round((b+m)*255));
        }
        private static string Hex(Color color){return "#"+color.R.ToString("X2")+color.G.ToString("X2")+color.B.ToString("X2");}
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
        internal void SetBody(UIElement body,string caption,string eyebrow,bool resize)
        {
            WindowStyle=WindowStyle.None;AllowsTransparency=true;Background=Brushes.Transparent;ResizeMode=resize?ResizeMode.CanResize:ResizeMode.NoResize;Theme.InstallStyles(this);
            var border=new Border{Background=Theme.WindowBackground,CornerRadius=new CornerRadius(14),BorderThickness=new Thickness(1),BorderBrush=Theme.Frame};var root=new Grid();border.Child=root;Content=border;
            root.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});root.RowDefinitions.Add(new RowDefinition());root.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
            var header=new DockPanel{Margin=new Thickness(18,12,12,10),Background=Brushes.Transparent};root.Children.Add(header);
            var close=Theme.ToolbarButton("close","关闭"+caption);close.Click+=delegate{WindowInteraction.Close(this);};DockPanel.SetDock(close,Dock.Right);header.Children.Add(close);
            if(resize){var maximize=Theme.ToolbarButton("maximize","最大化 / 还原");maximize.Click+=delegate{WindowInteraction.ToggleMaximize(this);};StateChanged+=delegate{maximize.Content=Theme.ToolbarIcon(WindowState==WindowState.Maximized?"restore":"maximize");};DockPanel.SetDock(maximize,Dock.Right);header.Children.Add(maximize);}
            var title=new StackPanel();title.Children.Add(Theme.Text("●  "+eyebrow,9,Theme.Muted));var text=Theme.Text(caption,17,Theme.Ink);text.Margin=new Thickness(0,5,0,0);title.Children.Add(text);header.Children.Add(title);
            WindowInteraction.Header(this,header,()=>{if(resize)WindowInteraction.ToggleMaximize(this);});WindowInteraction.Attach(this);
            Grid.SetRow(body,1);root.Children.Add(body);

        }
    }
}
