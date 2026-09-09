using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace CodexUserData
{
    internal static class OrbPalette
    {
        internal static string[] Clean(string[] colors,string[] fallback)
        {
            var valid=(colors??new string[0]).Where(c=>c!=null&&Theme.ValidHex(c.Trim())).Take(5).Select(c=>c.Trim().ToUpperInvariant()).ToArray();
            return valid.Length>0?valid:(string[])fallback.Clone();
        }
        internal static void Normalize(Preferences p)
        {
            p.OrbShortColors=Clean(p.OrbShortColors,new[]{"#316BF1","#5F97FF","#89CDEC"});p.OrbLongColors=Clean(p.OrbLongColors,new[]{"#7965EA","#AB8DF0","#E2B0ED"});
            p.OrbShortAngle=Theme.Bound(p.OrbShortAngle,0,360,45);p.OrbLongAngle=Theme.Bound(p.OrbLongAngle,0,360,45);
        }
        internal static string[] EffectiveColors(Preferences p,bool week)
        {
            if(!p.OrbFollowTheme)return week?p.OrbLongColors:p.OrbShortColors;
            string[] colors;
            if(p.ThemeMode=="custom")colors=p.GradientColors;
            else colors=p.ThemeMode=="light"?new[]{"#2458E5","#138FAC","#46CDB7"}:new[]{"#3976FF","#35C8E2","#87F3BE"};
            // Keep the chosen theme hues, while separating the gradient endpoints visibly.
            if(colors.Length==1){Color c=(Color)ColorConverter.ConvertFromString(colors[0]);colors=new[]{Color.FromRgb((byte)(c.R*.6),(byte)(c.G*.6),(byte)(c.B*.6)).ToString(),colors[0]};}
            return week?colors.Reverse().ToArray():colors;
        }
        internal static double EffectiveAngle(Preferences p,bool week){return p.OrbFollowTheme?(p.ThemeMode=="custom"?p.GradientAngle:35):(week?p.OrbLongAngle:p.OrbShortAngle);}
        internal static Brush ForBar(Preferences p,bool week,bool vertical=false){return Create(EffectiveColors(p,week),vertical?270:0,true);}
        internal static string Key(Preferences p){return String.Join(",",EffectiveColors(p,false))+"/"+String.Join(",",EffectiveColors(p,true))+"/"+EffectiveAngle(p,false)+"/"+EffectiveAngle(p,true);}
        internal static Brush Create(string[] colors,double angle,bool relative=false)
        {
            double a=angle*Math.PI/180,extent=relative?.5:62,center=relative?.5:64;
            var b=new LinearGradientBrush{MappingMode=relative?BrushMappingMode.RelativeToBoundingBox:BrushMappingMode.Absolute,StartPoint=new Point(center-Math.Cos(a)*extent,center-Math.Sin(a)*extent),EndPoint=new Point(center+Math.Cos(a)*extent,center+Math.Sin(a)*extent)};
            for(int i=0;i<colors.Length;i++)b.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(colors[i]),colors.Length==1?0:i/(double)(colors.Length-1)));
            b.Freeze();return b;
        }
        internal static Brush Light(string[] colors)
        {
            Color c=(Color)ColorConverter.ConvertFromString(colors[colors.Length/2]);var b=new SolidColorBrush(Color.FromRgb((byte)(c.R*.25+191),(byte)(c.G*.25+191),(byte)(c.B*.25+191)));b.Freeze();return b;
        }
        internal static Brush Caption(string[] colors,Brush original)
        {
            Color c=(Color)ColorConverter.ConvertFromString(colors[colors.Length/2]);double light=c.R*.299+c.G*.587+c.B*.114;
            if(Theme.IsLight&&light>160)c=Color.FromRgb((byte)(c.R*.52),(byte)(c.G*.52),(byte)(c.B*.52));
            else if(!Theme.IsLight&&light<95)c=Color.FromRgb((byte)(c.R*.55+114),(byte)(c.G*.55+114),(byte)(c.B*.55+114));else return original;
            var result=new SolidColorBrush(c);result.Freeze();return result;
        }
    }
    internal sealed class OrbAppearance : StyledWindow
    {
        private readonly Preferences draft;
        private readonly List<TextBox> inputs=new List<TextBox>();
        private readonly StackPanel palettes=new StackPanel();
        private readonly QuotaOrb example=new QuotaOrb{Width=144,Height=144};
        private readonly TextBlock validation=Theme.Text("",11,Theme.Warning);
        private readonly QuotaBucket demo;
        internal Preferences Result;
        internal bool IsValid {get{return inputs.All(t=>Theme.ValidHex(t.Text.Trim()));}}
        internal OrbAppearance(Preferences current)
        {
            draft=current.Clone();OrbPalette.Normalize(draft);Title="圆环配色";Width=440;Height=760;MinWidth=360;MinHeight=460;MaxHeight=SystemParameters.WorkArea.Height;ShowInTaskbar=false;WindowStartupLocation=WindowStartupLocation.CenterOwner;Theme.InstallStyles(this);
            var outer=new DockPanel{Margin=new Thickness(22,8,22,16)};SetBody(outer,"圆环配色","ORBIT COLORS",true);
            var actions=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,12,0,0)};DockPanel.SetDock(actions,Dock.Bottom);outer.Children.Add(actions);
            var cancel=Theme.Button("取消","取消配色修改",76);cancel.Margin=new Thickness(4);cancel.Click+=delegate{DialogResult=false;};actions.Children.Add(cancel);
            var save=Theme.Button("保存配色","保存圆环配色",100);save.Margin=new Thickness(4);save.Click+=delegate{if(!IsValid){validation.Text="请检查颜色格式，使用 #RRGGBB。";return;}Result=draft;DialogResult=true;};actions.Children.Add(save);
            var body=new StackPanel();outer.Children.Add(new ScrollViewer{Content=body,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled});
            var preview=new StackPanel{HorizontalAlignment=HorizontalAlignment.Center};preview.Children.Add(example);preview.Children.Add(Theme.Text("演示额度 · 悬停体验动画",11,Theme.Muted));body.Children.Add(preview);
            long now=LocalCodexUsage.Unix(DateTime.Now);demo=new QuotaBucket{ObservedAt=now,Primary=new QuotaWindow{Minutes=300,UsedPercent=18,ResetsAt=now+86400},Secondary=new QuotaWindow{Minutes=10080,UsedPercent=34,ResetsAt=now+86400}};
            var follow=new CheckBox{Content="圆环及额度条跟随主题渐变",IsChecked=draft.OrbFollowTheme,Foreground=Theme.Ink,Margin=new Thickness(0,12,0,10)};body.Children.Add(follow);follow.Checked+=delegate{draft.OrbFollowTheme=true;Refresh();};follow.Unchecked+=delegate{draft.OrbFollowTheme=false;Refresh();};
            body.Children.Add(Theme.Text("关闭跟随主题后，使用下面的独立圆环配色。",10,Theme.Muted));body.Children.Add(palettes);validation.TextWrapping=TextWrapping.Wrap;body.Children.Add(validation);
            var reset=Theme.Button("恢复默认配色","恢复蓝色和紫色渐变",130);reset.HorizontalAlignment=HorizontalAlignment.Left;reset.Margin=new Thickness(0,12,0,0);body.Children.Add(reset);
            reset.Click+=delegate{var defaults=new Preferences();draft.OrbShortColors=defaults.OrbShortColors;draft.OrbLongColors=defaults.OrbLongColors;draft.OrbShortAngle=draft.OrbLongAngle=45;Build();Refresh();};
            Closed+=delegate{example.Dispose();};Build();Refresh();
        }
        private void Refresh(){if(IsValid)example.Apply(draft,demo,null,"palette-demo");}
        private void Build()
        {
            palettes.Children.Clear();inputs.Clear();validation.Text="";Section(false);Section(true);
        }
        private void Section(bool week)
        {
            string title=week?"长周期 · 通常 7 天":"短周期 · 通常 5 小时";var heading=Theme.Text(title,12,Theme.Ink);heading.FontWeight=FontWeights.SemiBold;heading.Margin=new Thickness(0,18,0,8);palettes.Children.Add(heading);
            var colors=week?draft.OrbLongColors:draft.OrbShortColors;
            for(int i=0;i<colors.Length;i++)
            {
                int index=i;var row=new DockPanel{Margin=new Thickness(0,3,6,3)};palettes.Children.Add(row);
                var remove=Theme.Button("−","移除颜色",28);remove.Width=28;remove.Height=28;remove.Margin=new Thickness(6,0,0,0);remove.IsEnabled=colors.Length>1;DockPanel.SetDock(remove,Dock.Right);row.Children.Add(remove);
                var chip=Theme.Button("","选择颜色",32);chip.Width=32;chip.Height=28;chip.Background=Theme.B(colors[i]);chip.BorderThickness=new Thickness(1);chip.Margin=new Thickness(0,0,8,0);DockPanel.SetDock(chip,Dock.Left);row.Children.Add(chip);
                var input=Theme.Input(colors[i]);input.Height=28;input.Padding=new Thickness(8,3,8,3);input.FontSize=12;row.Children.Add(input);inputs.Add(input);
                System.Windows.Automation.AutomationProperties.SetName(input,title+"第 "+(i+1)+" 个颜色");
                input.TextChanged+=delegate{bool valid=Theme.ValidHex(input.Text.Trim());input.BorderBrush=valid?Theme.Line:Theme.Warning;validation.Text=IsValid?"":"颜色请输入 #RRGGBB，例如 #66BBEE。";if(valid){colors[index]=input.Text.Trim().ToUpperInvariant();chip.Background=Theme.B(colors[index]);Refresh();}};
                chip.Click+=delegate
                {
                    using(var picker=new System.Windows.Forms.ColorDialog{FullOpen=true,Color=System.Drawing.ColorTranslator.FromHtml(colors[index])})
                    {
                        var native=new System.Windows.Forms.NativeWindow();native.AssignHandle(new WindowInteropHelper(this).Handle);
                        try{if(picker.ShowDialog(native)==System.Windows.Forms.DialogResult.OK)input.Text=String.Format("#{0:X2}{1:X2}{2:X2}",picker.Color.R,picker.Color.G,picker.Color.B);}finally{native.ReleaseHandle();}
                    }
                };
                remove.Click+=delegate{var next=colors.Where((c,j)=>j!=index).ToArray();if(week)draft.OrbLongColors=next;else draft.OrbShortColors=next;Build();Refresh();};
            }
            var add=Theme.Button("＋ 添加颜色","最多 5 个渐变颜色",110);add.HorizontalAlignment=HorizontalAlignment.Left;add.IsEnabled=colors.Length<5;add.Margin=new Thickness(0,5,0,7);palettes.Children.Add(add);
            add.Click+=delegate{var next=colors.Concat(new[]{colors.Last()}).ToArray();if(week)draft.OrbLongColors=next;else draft.OrbShortColors=next;Build();Refresh();};
            var label=Theme.Text("渐变角度  "+(week?draft.OrbLongAngle:draft.OrbShortAngle).ToString("0",CultureInfo.InvariantCulture)+"°",11,Theme.Muted);palettes.Children.Add(label);
            var angle=new Slider{Minimum=0,Maximum=360,Value=week?draft.OrbLongAngle:draft.OrbShortAngle,IsSnapToTickEnabled=true,TickFrequency=1};palettes.Children.Add(angle);
            angle.ValueChanged+=delegate{if(week)draft.OrbLongAngle=angle.Value;else draft.OrbShortAngle=angle.Value;label.Text="渐变角度  "+angle.Value.ToString("0",CultureInfo.InvariantCulture)+"°";Refresh();};
        }
    }
}
