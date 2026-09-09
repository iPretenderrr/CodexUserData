using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace CodexUserData
{
    // Edits only the settings draft. The owner restores the saved palette on cancellation.
    internal sealed class ThemeEditor : StackPanel
    {
        private readonly Preferences draft;
        private readonly StackPanel custom=new StackPanel(),colors=new StackPanel(),geometry=new StackPanel();
        private readonly Dictionary<string,Button> modes=new Dictionary<string,Button>();
        private readonly List<TextBox> colorInputs=new List<TextBox>();
        private readonly DispatcherTimer repaint=new DispatcherTimer(DispatcherPriority.Background);
        private readonly TextBlock validation=Theme.Text("",11,Theme.Warning);
        internal bool IsValid {get{return draft.ThemeMode!="custom"||colorInputs.All(t=>Theme.ValidHex(t.Text.Trim()));}}

        internal ThemeEditor(Preferences preferences)
        {
            draft=preferences;Theme.Normalize(draft);Margin=new Thickness(0,20,0,4);
            repaint.Interval=TimeSpan.FromMilliseconds(40);repaint.Tick+=delegate{repaint.Stop();if(IsValid)Theme.Apply(draft);};
            Unloaded+=delegate{repaint.Stop();};
            Heading(this,"外观主题");
            var choices=new UniformGrid{Columns=3,Margin=new Thickness(0,0,0,10)};Children.Add(choices);
            foreach(var item in new[]{new[]{"dark","深色 · 午夜"},new[]{"light","浅色 · 云白"},new[]{"custom","自定义"}})
            {
                string key=item[0];var button=Theme.Button(item[1],"切换到"+item[1],60);button.Height=36;button.FontSize=12;button.Margin=new Thickness(2,0,2,0);button.BorderThickness=new Thickness(1);
                button.Click+=delegate{draft.ThemeMode=key;UpdateMode();Queue();};modes.Add(key,button);choices.Children.Add(button);
            }
            var previewContent=new StackPanel();previewContent.Children.Add(Theme.Text("CODEX  /  主题预览",10,Theme.Muted));
            var previewRow=new DockPanel{Margin=new Thickness(0,9,0,0)};previewContent.Children.Add(previewRow);
            var quota=Theme.Text("额度  86%",12,Theme.Accent);DockPanel.SetDock(quota,Dock.Right);previewRow.Children.Add(quota);
            var token=Theme.Text("128万",25,Theme.Ink);token.FontWeight=FontWeights.SemiBold;previewRow.Children.Add(token);
            previewContent.Children.Add(new Border{Height=4,CornerRadius=new CornerRadius(2),Background=Theme.Accent,Margin=new Thickness(0,12,35,0)});
            Children.Add(new Border{Background=Theme.WindowBackground,BorderBrush=Theme.Frame,BorderThickness=new Thickness(1.5),CornerRadius=new CornerRadius(12),Padding=new Thickness(10),Child=new Border{Background=Theme.Surface,CornerRadius=new CornerRadius(8),Padding=new Thickness(12),Child=previewContent}});
            Note(this,"实时预览 · 保存后保留，取消可还原");Children.Add(custom);
            Heading(custom,"配色灵感");var presets=new UniformGrid{Columns=3};custom.Children.Add(presets);
            Preset(presets,"绮彩",new[]{"#F7BBE3","#E6D7FA","#AAF1ED"},"light",120);
            Preset(presets,"极光",new[]{"#252947","#493777","#17616C"},"dark",135);
            Preset(presets,"落日",new[]{"#FFD6BA","#F8C8DC","#D7CBFA"},"light",35);
            Heading(custom,"渐变颜色 · 1–5 色");custom.Children.Add(colors);BuildColors();
            validation.TextWrapping=TextWrapping.Wrap;validation.Margin=new Thickness(0,5,0,0);custom.Children.Add(validation);
            Heading(custom,"渐变方式");
            var kind=new ChoiceButton(new Dictionary<string,string>{{"linear","线性渐变"},{"radial","径向渐变"}},"渐变方式");kind.Select(draft.GradientKind);kind.Changed+=v=>{draft.GradientKind=v;BuildGeometry();Queue();};custom.Children.Add(kind);
            custom.Children.Add(geometry);BuildGeometry();
            Heading(custom,"边界延展");
            var spread=new ChoiceButton(new Dictionary<string,string>{{"pad","延伸端点颜色"},{"reflect","镜像渐变"},{"repeat","重复渐变"}},"渐变边界延展");spread.Select(draft.GradientSpread);spread.Changed+=v=>{draft.GradientSpread=v;Queue();};custom.Children.Add(spread);
            Heading(custom,"文字与卡片风格");
            var tone=new ChoiceButton(new Dictionary<string,string>{{"auto","自动匹配明暗"},{"light","浅色卡片 · 深色文字"},{"dark","深色卡片 · 浅色文字"}},"文字与卡片风格");tone.Select(draft.ThemeBase);tone.Changed+=v=>{draft.ThemeBase=v;Queue();};custom.Children.Add(tone);
            // Presets also synchronize this selector, avoiding stale labels after a preset change.
            presetChanged+=delegate{tone.Select(draft.ThemeBase);};
            SliderRow(custom,"渐变浓度",0,100,draft.GradientStrength,"%",v=>draft.GradientStrength=v);
            SliderRow(custom,"卡片不透明度",55,100,draft.ThemeCardOpacity,"%",v=>draft.ThemeCardOpacity=v);
            Note(custom,"色点可拖动位置；缩短渐变范围可配合镜像或重复。卡片透明度仅影响卡片，窗口透明度在下方单独设置。");UpdateMode();
        }
        private event Action presetChanged;
        private void Preset(Panel parent,string name,string[] palette,string tone,double angle)
        {
            var b=Theme.Button(name,"应用"+name+"配色",50);b.Height=32;b.Margin=new Thickness(2);b.Background=Theme.Surface;b.BorderThickness=new Thickness(1);parent.Children.Add(b);
            b.Click+=delegate{draft.GradientColors=(string[])palette.Clone();draft.GradientStops=new[]{0d,48d,100d};draft.ThemeBase=tone;draft.GradientAngle=angle;BuildColors();BuildGeometry();if(presetChanged!=null)presetChanged();Queue();};
        }
        private void UpdateMode()
        {
            foreach(var item in modes){bool active=item.Key==draft.ThemeMode;item.Value.Background=active?Theme.Hover:Theme.Surface;item.Value.BorderBrush=active?Theme.Accent:Theme.Line;item.Value.Foreground=active?Theme.Accent:Theme.Muted;}
            custom.Visibility=draft.ThemeMode=="custom"?Visibility.Visible:Visibility.Collapsed;
        }
        private void Queue(){repaint.Stop();repaint.Start();}
        private void BuildGeometry()
        {
            geometry.Children.Clear();
            if(draft.GradientKind=="linear")
            {
                SliderRow(geometry,"角度",0,360,draft.GradientAngle,"°",v=>draft.GradientAngle=v);
                SliderRow(geometry,"渐变范围",10,200,draft.GradientSpan,"%",v=>draft.GradientSpan=v);
            }
            else
            {
                SliderRow(geometry,"中心 · 横向",0,100,draft.GradientCenterX,"%",v=>draft.GradientCenterX=v);
                SliderRow(geometry,"中心 · 纵向",0,100,draft.GradientCenterY,"%",v=>draft.GradientCenterY=v);
                SliderRow(geometry,"扩散半径",10,150,draft.GradientRadius,"%",v=>draft.GradientRadius=v);
            }
        }
        private void BuildColors()
        {
            colors.Children.Clear();colorInputs.Clear();validation.Text="";
            for(int i=0;i<draft.GradientColors.Length;i++)
            {
                int index=i;var row=new Grid{Margin=new Thickness(0,3,0,3)};
                foreach(var width in new[]{36d,92d,Double.NaN,30d})row.ColumnDefinitions.Add(new ColumnDefinition{Width=Double.IsNaN(width)?new GridLength(1,GridUnitType.Star):new GridLength(width)});
                var chip=Theme.Button("","选择第 "+(i+1)+" 个颜色",28);chip.Width=28;chip.Height=28;chip.Background=Theme.B(draft.GradientColors[i]);chip.BorderThickness=new Thickness(1);row.Children.Add(chip);
                var hex=Theme.Input(draft.GradientColors[i]);hex.Height=28;hex.FontSize=11;hex.Padding=new Thickness(5,3,5,3);hex.Margin=new Thickness(3,0,5,0);Grid.SetColumn(hex,1);row.Children.Add(hex);colorInputs.Add(hex);
                System.Windows.Automation.AutomationProperties.SetName(hex,"第 "+(i+1)+" 个颜色的十六进制值");
                var position=new Slider{Minimum=0,Maximum=100,Value=draft.GradientStops[i],TickFrequency=1,IsSnapToTickEnabled=true,Margin=new Thickness(6,0,6,0)};position.ToolTip="色点位置 "+(int)position.Value+"%";Grid.SetColumn(position,2);row.Children.Add(position);
                System.Windows.Automation.AutomationProperties.SetName(position,"第 "+(i+1)+" 个颜色的位置");
                position.ValueChanged+=delegate{draft.GradientStops[index]=position.Value;position.ToolTip="色点位置 "+(int)position.Value+"%";Queue();};
                var remove=Theme.Button("−","移除第 "+(i+1)+" 个颜色",26);remove.Width=26;remove.IsEnabled=draft.GradientColors.Length>1;remove.Opacity=remove.IsEnabled?1:.35;Grid.SetColumn(remove,3);row.Children.Add(remove);
                remove.Click+=delegate{draft.GradientColors=draft.GradientColors.Where((c,j)=>j!=index).ToArray();draft.GradientStops=draft.GradientStops.Where((c,j)=>j!=index).ToArray();BuildColors();Queue();};
                hex.TextChanged+=delegate{string value=hex.Text.Trim();bool valid=Theme.ValidHex(value);hex.BorderBrush=valid?Theme.Line:Theme.Warning;validation.Text=IsValid?"":"颜色请输入 #RRGGBB，例如 #88B4D8。";if(valid){draft.GradientColors[index]=value.ToUpperInvariant();chip.Background=Theme.B(value);Queue();}};
                chip.Click+=delegate
                {
                    // Use the OS color picker for precise RGB selection; no third-party UI runtime.
                    using(var picker=new System.Windows.Forms.ColorDialog{FullOpen=true,Color=System.Drawing.ColorTranslator.FromHtml(draft.GradientColors[index])})
                    {
                        var owner=Window.GetWindow(this);var native=new System.Windows.Forms.NativeWindow();if(owner!=null)native.AssignHandle(new WindowInteropHelper(owner).Handle);
                        try{if(picker.ShowDialog(native)==System.Windows.Forms.DialogResult.OK)hex.Text=String.Format("#{0:X2}{1:X2}{2:X2}",picker.Color.R,picker.Color.G,picker.Color.B);}finally{native.ReleaseHandle();}
                    }
                };
                colors.Children.Add(row);
            }
            var add=Theme.Button("＋ 添加颜色","添加渐变颜色",90);add.HorizontalAlignment=HorizontalAlignment.Left;add.Foreground=Theme.Accent;add.IsEnabled=draft.GradientColors.Length<5;add.Opacity=add.IsEnabled?1:.4;colors.Children.Add(add);
            add.Click+=delegate{draft.GradientColors=draft.GradientColors.Concat(new[]{"#BBD9EE"}).ToArray();draft.GradientStops=Enumerable.Range(0,draft.GradientColors.Length).Select(i=>100d*i/(draft.GradientColors.Length-1)).ToArray();BuildColors();Queue();};
        }
        private void SliderRow(Panel panel,string name,double min,double max,double value,string unit,Action<double> change)
        {
            var label=new DockPanel{Margin=new Thickness(0,13,0,1)};var amount=Theme.Text(Math.Round(value)+unit,11,Theme.Accent);DockPanel.SetDock(amount,Dock.Right);label.Children.Add(amount);label.Children.Add(Theme.Text(name,11,Theme.Ink));panel.Children.Add(label);
            var slider=new Slider{Minimum=min,Maximum=max,Value=value,TickFrequency=1,IsSnapToTickEnabled=true};System.Windows.Automation.AutomationProperties.SetName(slider,name);
            slider.ValueChanged+=delegate{amount.Text=Math.Round(slider.Value)+unit;change(slider.Value);Queue();};panel.Children.Add(slider);
        }
        private static void Heading(Panel panel,string text){var t=Theme.Text(text,12,Theme.Ink);t.FontWeight=FontWeights.SemiBold;t.Margin=new Thickness(0,10,0,8);panel.Children.Add(t);}
        private static void Note(Panel panel,string text){var t=Theme.Text(text,11,Theme.Muted);t.TextWrapping=TextWrapping.Wrap;t.LineHeight=17;t.Margin=new Thickness(0,8,0,0);panel.Children.Add(t);}
    }
}
