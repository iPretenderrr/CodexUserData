using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace CodexUserData
{
    internal sealed class SettingsWindow : StyledWindow
    {
        internal Preferences Result;
        private readonly Preferences draft;
        private readonly Dictionary<string,CheckBox> fields=new Dictionary<string,CheckBox>();
        private readonly Dictionary<string,Button> navigation=new Dictionary<string,Button>();
        private readonly Dictionary<string,ScrollViewer> pages=new Dictionary<string,ScrollViewer>();
        private readonly Grid pageHost=new Grid();
        private readonly TextBox refresh,home,database,quotaCli;
        private readonly TextBlock error,opacityLabel;
        private readonly ThemeEditor themeEditor;
        private string activePage;
        internal SettingsWindow(Preferences current,Action<double> preview)
        {
            draft=current.Clone();Title="CodexUserData 设置";ShowInTaskbar=false;Width=Math.Min(760,SystemParameters.WorkArea.Width-32);Height=Math.Min(720,SystemParameters.WorkArea.Height-32);MinWidth=Math.Min(560,Width);MinHeight=Math.Min(460,Height);Theme.InstallStyles(this);
            MaxHeight=SystemParameters.WorkArea.Height;WindowStartupLocation=WindowStartupLocation.CenterOwner;
            Background=Theme.Background;Foreground=Theme.Ink;FontFamily=new FontFamily("Segoe UI, Microsoft YaHei UI");FontSize=12;
            var outer=new Grid{Margin=new Thickness(18,0,18,14),Background=Brushes.Transparent};SetBody(outer,"设置","PREFERENCES",true);
            outer.RowDefinitions.Add(new RowDefinition{Height=new GridLength(1,GridUnitType.Star)});outer.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
            var content=new Grid();content.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(138)});content.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});outer.Children.Add(content);
            var menu=new StackPanel{Margin=new Thickness(8,10,8,10)};
            var menuTitle=Theme.Text("设置分类",11,Theme.Muted);menuTitle.Margin=new Thickness(8,2,0,10);menu.Children.Add(menuTitle);
            var menuShell=new Border{Background=Theme.Surface,BorderBrush=Theme.Line,BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(12),Child=menu,Margin=new Thickness(0,0,12,0)};content.Children.Add(menuShell);
            Grid.SetColumn(pageHost,1);content.Children.Add(pageHost);

            AddNavigation(menu,"appearance","外观与主题");
            AddNavigation(menu,"floating","悬浮球");
            AddNavigation(menu,"display","数据显示");
            AddNavigation(menu,"data","数据与额度");
            AddNavigation(menu,"behavior","启动与行为");

            var appearance=Page("外观与主题","窗口主题、渐变和整体透明度");
            themeEditor=new ThemeEditor(draft);appearance.Children.Add(themeEditor);
            Label(appearance,"窗口不透明度");
            opacityLabel=Theme.Text(((int)(draft.Opacity*100))+"%",12,Theme.Accent);appearance.Children.Add(opacityLabel);
            var opacity=new Slider{Minimum=20,Maximum=100,Value=draft.Opacity*100,TickFrequency=5,IsSnapToTickEnabled=true,Margin=new Thickness(0,8,0,2)};
            System.Windows.Automation.AutomationProperties.SetName(opacity,"窗口不透明度");
            opacity.ValueChanged+=delegate{draft.Opacity=opacity.Value/100;opacityLabel.Text=((int)opacity.Value)+"%";if(preview!=null)preview(draft.Opacity);};appearance.Children.Add(opacity);
            Note(appearance,"只影响主窗口和设置窗口；悬浮球透明度在“悬浮球”中单独调整。",Theme.Muted);
            AddPage("appearance",appearance);

            var floating=Page("悬浮球","形态、圆环、贴边额度条和任务反馈");
            Label(floating,"悬浮窗形态");
            var ballStyle=new ChoiceButton(new Dictionary<string,string>{{"orb","光环 · 圆形额度球"},{"capsule","胶囊 · 今日 Tokens"},{"html","自定义 · HTML 形态"}},"悬浮窗形态");ballStyle.Select(draft.BallStyle);ballStyle.Changed+=v=>{draft.BallStyle=v;draft.BallExpanded=false;};floating.Children.Add(ballStyle);
            var shapeName=Theme.Text(String.IsNullOrEmpty(draft.CustomShape)?"尚未导入自定义形态":Path.GetFileName(Path.GetDirectoryName(draft.CustomShape)),11,Theme.Muted);shapeName.TextWrapping=TextWrapping.Wrap;shapeName.Margin=new Thickness(0,7,0,0);floating.Children.Add(shapeName);
            var import=Theme.Button("导入 HTML 形态…","选择 shape.json",180);import.HorizontalAlignment=HorizontalAlignment.Left;import.Margin=new Thickness(0,8,0,0);floating.Children.Add(import);
            import.Click+=delegate{var picker=new Microsoft.Win32.OpenFileDialog{Filter="形态配置|shape.json",InitialDirectory=Path.Combine(Program.Folder,"examples")};if(picker.ShowDialog()!=true)return;try{draft.CustomShape=ShapeManifest.Import(picker.FileName);draft.BallStyle="html";ballStyle.Select("html");shapeName.Text=ShapeManifest.Read(ShapeManifest.Resolve(draft.CustomShape)).name??"已导入形态";}catch(Exception ex){error.Text=ex.Message;}};
            Note(floating,"形态会复制到用户数据目录。任意位置右键可切回主界面；Alt + 拖动可移动。",Theme.Muted);
            var orbColors=Theme.Button("圆环配色  →","自定义短周期、长周期渐变",180);orbColors.HorizontalAlignment=HorizontalAlignment.Stretch;orbColors.Margin=new Thickness(0,12,0,0);orbColors.Height=36;floating.Children.Add(orbColors);
            orbColors.Click+=delegate{var editor=new OrbAppearance(draft){Owner=this};if(editor.ShowDialog()==true){draft.OrbFollowTheme=editor.Result.OrbFollowTheme;draft.OrbShortColors=editor.Result.OrbShortColors;draft.OrbLongColors=editor.Result.OrbLongColors;draft.OrbShortAngle=editor.Result.OrbShortAngle;draft.OrbLongAngle=editor.Result.OrbLongAngle;}};
            Label(floating,"贴边额度条周期");
            var orbQuota=new ChoiceButton(new Dictionary<string,string>{{"auto","自动 · 优先短周期"},{"short","短周期剩余额度（通常 5 小时）"},{"week","长周期剩余额度（通常 7 天）"}},"贴边额度周期");orbQuota.Select(draft.OrbQuotaWindow);orbQuota.Changed+=v=>draft.OrbQuotaWindow=v;floating.Children.Add(orbQuota);
            Label(floating,"圆球尺寸");var orbSizeLabel=Theme.Text(((int)draft.OrbSize)+" px",11,Theme.Muted);floating.Children.Add(orbSizeLabel);
            var orbSize=new Slider{Minimum=56,Maximum=128,Value=draft.OrbSize,TickFrequency=2,IsSnapToTickEnabled=true};System.Windows.Automation.AutomationProperties.SetName(orbSize,"圆球直径");orbSize.ValueChanged+=delegate{draft.OrbSize=orbSize.Value;orbSizeLabel.Text=((int)orbSize.Value)+" px";};floating.Children.Add(orbSize);
            Label(floating,"悬浮球整体不透明度");
            var ballAlphaLabel=Theme.Text(((int)(draft.BallOpacity*100))+"%",12,Theme.Accent);floating.Children.Add(ballAlphaLabel);
            var ballAlpha=new Slider{Minimum=20,Maximum=100,Value=draft.BallOpacity*100,TickFrequency=5,IsSnapToTickEnabled=true};ballAlpha.ValueChanged+=delegate{draft.BallOpacity=ballAlpha.Value/100;ballAlphaLabel.Text=((int)ballAlpha.Value)+"%";};floating.Children.Add(ballAlpha);
            Label(floating,"光环动画");
            var orbMotion=new ChoiceButton(new Dictionary<string,string>{{"auto","自动 · 按设备性能调节"},{"smooth","流畅 · 最高 60 帧"},{"eco","节能 · 最高 30 帧"},{"off","关闭动画"}},"光环动画档位");orbMotion.Select(draft.OrbAnimation);orbMotion.Changed+=v=>draft.OrbAnimation=v;floating.Children.Add(orbMotion);
            Note(floating,"“自动”会按设备性能调节；节能模式减少粒子和刷新帧率。贴边后停止圆环动画，运行任务时额度条显示轻量脉冲。",Theme.Muted);
            var completion=new CheckBox{Content="任务完成后闪烁提示",IsChecked=draft.CompletionFlash,Foreground=Theme.Ink,Margin=new Thickness(0,16,0,5)};floating.Children.Add(completion);completion.Checked+=delegate{draft.CompletionFlash=true;};completion.Unchecked+=delegate{draft.CompletionFlash=false;};
            Note(floating,"明确完成后持续闪烁，鼠标移入悬浮窗或托盘确认。任务状态仍可能受日志落盘延迟影响。",Theme.Muted);
            AddPage("floating",floating);

            var display=Page("数据显示","选择主页指标、图表和模型信息");
            Label(display,"主页显示的数据");var metrics=new System.Windows.Controls.Primitives.UniformGrid{Columns=2};display.Children.Add(metrics);
            foreach(var entry in Theme.MetricLabels)
            {
                var box=new CheckBox{Content=entry.Value,IsChecked=draft.Metrics.Contains(entry.Key),Foreground=Theme.Ink,Margin=new Thickness(0,7,3,7),FontSize=11};
                fields[entry.Key]=box;metrics.Children.Add(box);
            }
            Note(display,"推理 Tokens 已含在输出中，缺失指标显示“—”。",Theme.Muted);
            var models=new CheckBox{Content="显示模型、思考强度与 API 等效价值明细",IsChecked=draft.ShowModels,Foreground=Theme.Ink,Margin=new Thickness(0,15,0,8)};display.Children.Add(models);models.Checked+=delegate{draft.ShowModels=true;};models.Unchecked+=delegate{draft.ShowModels=false;};
            var prices=Theme.Button("模型价格  →","编辑所有模型价格",180);prices.HorizontalAlignment=HorizontalAlignment.Stretch;prices.Background=Theme.Surface;prices.Foreground=Theme.Accent;prices.Margin=new Thickness(0,8,0,0);prices.Height=38;display.Children.Add(prices);
            prices.Click+=delegate{var editor=new PriceEditor(draft){Owner=this};if(editor.ShowDialog()==true){draft.PriceOverrides=editor.Result;draft.KnownModels=editor.KnownModels;}};
            Note(display,"保存模型价格后，全部历史用量会按新价格重新估算。",Theme.Muted);
            Label(display,"主页图表");
            var heatmap=new CheckBox{Content="每日用量热度图",IsChecked=draft.ShowHeatmap,Foreground=Theme.Ink,Margin=new Thickness(0,5,0,8)};display.Children.Add(heatmap);heatmap.Checked+=delegate{draft.ShowHeatmap=true;};heatmap.Unchecked+=delegate{draft.ShowHeatmap=false;};
            var trend=new CheckBox{Content="每日 Token 趋势",IsChecked=draft.ShowTrend,Foreground=Theme.Ink,Margin=new Thickness(0,5,0,8)};display.Children.Add(trend);trend.Checked+=delegate{draft.ShowTrend=true;};trend.Unchecked+=delegate{draft.ShowTrend=false;};
            Note(display,"顶部图表按钮可在独立大窗口查看完整热度图和趋势。",Theme.Muted);
            AddPage("display",display);

            var data=Page("数据与额度","数据来源、刷新周期、Codex CLI 和本地路径");
            Label(data,"数据来源");
            var source=new ChoiceButton(new Dictionary<string,string>{{"ccswitch","CC Switch 数据库"},{"local","本地 Codex 日志"}},"设置数据来源");source.Select(draft.Source);source.Changed+=v=>draft.Source=v;data.Children.Add(source);
            Note(data,"两个来源独立统计，切换查看且不相加，避免重复计算。",Theme.Muted);
            Label(data,"刷新间隔 · 秒");refresh=Theme.Input(draft.RefreshSeconds.ToString());data.Children.Add(refresh);Note(data,"2–3600 秒。首次索引完成后，只读取新增日志。",Theme.Muted);
            Label(data,"Codex 剩余额度");
            var quota=new CheckBox{Content="顶部显示剩余额度",IsChecked=draft.ShowQuota,Foreground=Theme.Ink,Margin=new Thickness(0,5,0,8)};data.Children.Add(quota);quota.Checked+=delegate{draft.ShowQuota=true;};quota.Unchecked+=delegate{draft.ShowQuota=false;};
            var live=new CheckBox{Content="每分钟查询在线额度",IsChecked=draft.LiveQuota,Foreground=Theme.Ink,Margin=new Thickness(0,5,0,8)};data.Children.Add(live);live.Checked+=delegate{draft.LiveQuota=true;};live.Unchecked+=delegate{draft.LiveQuota=false;};
            Note(data,"在线查询使用已登录的 Codex CLI；离线时显示最近快照，过期后不推算余额。",Theme.Muted);
            Label(data,"Codex CLI 可执行文件");quotaCli=Theme.Input(draft.QuotaCli);data.Children.Add(quotaCli);
            var cliActions=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(0,7,0,0)};data.Children.Add(cliActions);
            var detect=Theme.Button("自动检测","重新检测本机 Codex CLI",108);cliActions.Children.Add(detect);
            var browseCli=Theme.Button("手动选择…","选择 codex.exe",108);browseCli.Margin=new Thickness(8,0,0,0);cliActions.Children.Add(browseCli);
            var cliStatus=Theme.Text(File.Exists(draft.QuotaCli)?"已找到本机可执行文件":"未找到 CLI；本地日志统计仍可使用。",11,Theme.Muted);cliStatus.TextWrapping=TextWrapping.Wrap;cliStatus.Margin=new Thickness(0,7,0,0);data.Children.Add(cliStatus);
            detect.Click+=async delegate{detect.IsEnabled=false;cliStatus.Text="正在检测…";try{string found=await System.Threading.Tasks.Task.Run(()=>QuotaReader.FindDefaultExe());if(!String.IsNullOrEmpty(found)){quotaCli.Text=found;cliStatus.Text="已自动填入检测结果。";}else{cliStatus.Text="所有位置均未找到，请手动选择 codex.exe。在线额度需要可用且已登录的 CLI。";cliStatus.Foreground=Theme.Warning;}}finally{detect.IsEnabled=true;}};
            browseCli.Click+=delegate{var picker=new Microsoft.Win32.OpenFileDialog{Filter="Codex CLI|codex.exe|可执行文件|*.exe"};if(picker.ShowDialog()==true){quotaCli.Text=picker.FileName;cliStatus.Text="已选择，保存后用于在线额度查询。";}};
            Label(data,"Codex 数据目录");home=Theme.Input(draft.CodexHome);PathRow(data,home,true);
            Label(data,"CC Switch 数据库");database=Theme.Input(draft.Database);PathRow(data,database,false);
            Note(data,"仅以只读方式访问原始数据。用量不代表套餐剩余额度或实际账单。",Theme.Muted);
            Label(data,"数据保存位置");Note(data,"%LOCALAPPDATA%\\CodexUserData：保存设置、价格、自定义形态和用量缓存，各新版共用。",Theme.Muted);
            var openData=Theme.Button("打开用户数据目录","打开设置与缓存保存位置",180);openData.HorizontalAlignment=HorizontalAlignment.Left;openData.Margin=new Thickness(0,8,0,0);data.Children.Add(openData);openData.Click+=delegate{try{Directory.CreateDirectory(Program.DataFolder);System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo{FileName=Program.DataFolder,UseShellExecute=true});}catch(Exception ex){error.Text="无法打开数据目录："+ex.Message;}};
            AddPage("data",data);

            var behavior=Page("启动与行为","最小化方式和自动启动规则");
            Label(behavior,"最小化方式");
            var minimizeMode=new ChoiceButton(new Dictionary<string,string>{{"taskbar","最小化到任务栏"},{"tray","最小化到托盘"}},"最小化方式");
            minimizeMode.Select(draft.MinimizeToTray?"tray":"taskbar");minimizeMode.Changed+=v=>draft.MinimizeToTray=v=="tray";behavior.Children.Add(minimizeMode);
            Note(behavior,"选择托盘后窗口会隐藏，双击托盘图标恢复；两种方式都会继续统计。",Theme.Muted);
            Label(behavior,"自动启动");
            var startup=new CheckBox{Content="开机自启动（登录 Windows 后启动）",IsChecked=draft.StartWithWindows,Foreground=Theme.Ink,Margin=new Thickness(0,4,0,6)};behavior.Children.Add(startup);startup.Checked+=delegate{draft.StartWithWindows=true;};startup.Unchecked+=delegate{draft.StartWithWindows=false;};
            var sync=new CheckBox{Content="随 Codex 启动",IsChecked=draft.StartWithCodex,Foreground=Theme.Ink,Margin=new Thickness(0,10,0,6)};behavior.Children.Add(sync);sync.Checked+=delegate{draft.StartWithCodex=true;draft.StartWithWindows=false;startup.IsChecked=false;};sync.Unchecked+=delegate{draft.StartWithCodex=false;};startup.Checked+=delegate{draft.StartWithCodex=false;sync.IsChecked=false;};
            Note(behavior,"两种自动启动方式互斥。随 Codex 启动使用轻量后台检测器；移动程序文件夹后，启动一次软件以更新路径。",Theme.Muted);
            AddPage("behavior",behavior);

            Closed+=delegate{Theme.Apply(Result??current);};
            var bottom=new StackPanel{Margin=new Thickness(0,12,0,0)};Grid.SetRow(bottom,1);outer.Children.Add(bottom);
            error=Theme.Text("",11,Theme.Warning);error.TextWrapping=TextWrapping.Wrap;bottom.Children.Add(error);
            var buttons=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,8,0,0)};bottom.Children.Add(buttons);
            var cancel=Theme.Button("取消","取消设置",72);cancel.IsCancel=true;cancel.Click+=delegate{DialogResult=false;};buttons.Children.Add(cancel);
            var save=Theme.Button("保存设置","保存设置",98);save.Background=Theme.Hover;save.Foreground=Theme.Accent;save.Margin=new Thickness(8,0,0,0);save.IsDefault=true;save.Click+=Save;buttons.Children.Add(save);
            SelectPage("appearance");
        }
        private void AddNavigation(Panel menu,string key,string title)
        {
            var button=Theme.Button(title,"打开“"+title+"”设置",108);button.HorizontalAlignment=HorizontalAlignment.Stretch;button.HorizontalContentAlignment=HorizontalAlignment.Left;button.Padding=new Thickness(9,3,8,3);button.Height=38;button.Margin=new Thickness(0,2,0,2);button.BorderThickness=new Thickness(1);button.Click+=delegate{SelectPage(key);};
            navigation[key]=button;menu.Children.Add(button);
        }
        private void AddPage(string key,StackPanel body)
        {
            // Each category keeps an independent scroll position. Switching pages only changes
            // visibility, so no controls or event handlers are rebuilt on slower devices.
            var scroll=new ScrollViewer{VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,Visibility=Visibility.Collapsed,Margin=new Thickness(10,0,0,0)};
            body.Margin=new Thickness(8,4,12,16);scroll.Content=body;pages[key]=scroll;pageHost.Children.Add(scroll);
        }
        private void SelectPage(string key)
        {
            if(!pages.ContainsKey(key))return;
            if(activePage==key){pages[key].ScrollToTop();return;}
            activePage=key;
            foreach(var page in pages)page.Value.Visibility=page.Key==key?Visibility.Visible:Visibility.Collapsed;
            foreach(var item in navigation)
            {
                bool selected=item.Key==key;item.Value.Tag=selected?"selected":null;item.Value.Background=selected?Theme.Hover:Brushes.Transparent;item.Value.Foreground=selected?Theme.Accent:Theme.Muted;item.Value.BorderBrush=selected?Theme.Accent:Brushes.Transparent;
            }
        }
        private static StackPanel Page(string title,string subtitle)
        {
            var body=new StackPanel();var heading=Theme.Text(title,20,Theme.Ink);heading.FontWeight=FontWeights.SemiBold;body.Children.Add(heading);
            var note=Theme.Text(subtitle,11,Theme.Muted);note.Margin=new Thickness(0,6,0,3);note.TextWrapping=TextWrapping.Wrap;body.Children.Add(note);return body;
        }
        private static void Label(Panel panel,string label){var text=Theme.Text(label,12,Theme.Ink);text.Margin=new Thickness(0,20,0,8);panel.Children.Add(text);}
        private static void Note(Panel panel,string note,Brush color){var text=Theme.Text(note,11,color);text.TextWrapping=TextWrapping.Wrap;text.LineHeight=17;text.Margin=new Thickness(0,7,0,0);panel.Children.Add(text);}
        private static void PathRow(Panel parent,TextBox input,bool folder)
        {
            var row=new DockPanel();var browse=Theme.Button("…","选择路径",30);browse.Margin=new Thickness(5,0,0,0);DockPanel.SetDock(browse,Dock.Right);row.Children.Add(browse);row.Children.Add(input);parent.Children.Add(row);
            browse.Click+=delegate
            {
                if(folder){using(var picker=new System.Windows.Forms.FolderBrowserDialog{Description="选择包含 sessions 的 Codex 数据目录",SelectedPath=input.Text})if(picker.ShowDialog()==System.Windows.Forms.DialogResult.OK)input.Text=picker.SelectedPath;}
                else{var picker=new Microsoft.Win32.OpenFileDialog{Filter="SQLite 数据库|*.db|所有文件|*.*",FileName=input.Text};if(picker.ShowDialog()==true)input.Text=picker.FileName;}
            };
        }
        private void Save(object sender,RoutedEventArgs args)
        {
            if(!themeEditor.IsValid){SelectPage("appearance");error.Text="请检查自定义主题的颜色，使用 #RRGGBB 格式。";return;}
            int seconds;
            if(!Int32.TryParse(refresh.Text,out seconds)||seconds<2||seconds>3600){SelectPage("data");error.Text="刷新间隔请输入 2–3600 之间的整数。";return;}
            string[] selected=fields.Where(x=>x.Value.IsChecked==true).Select(x=>x.Key).ToArray();
            if(selected.Length==0){SelectPage("display");error.Text="请至少选择一项显示数据。";return;}
            try{draft.CodexHome=Path.GetFullPath(home.Text.Trim());draft.Database=Path.GetFullPath(database.Text.Trim());draft.QuotaCli=String.IsNullOrWhiteSpace(quotaCli.Text)?"":Path.GetFullPath(quotaCli.Text.Trim());}
            catch(Exception){SelectPage("data");error.Text="请填写有效的本地路径。";return;}
            if(draft.Source=="local"&&!Directory.Exists(Path.Combine(draft.CodexHome,"sessions"))&&!Directory.Exists(Path.Combine(draft.CodexHome,"archived_sessions"))){SelectPage("data");error.Text="这个目录中未找到 sessions 或 archived_sessions。";return;}
            if(draft.Source=="ccswitch"&&!File.Exists(draft.Database)){SelectPage("data");error.Text="找不到 CC Switch 数据库文件。";return;}
            if(draft.BallStyle=="html")try{ShapeManifest.Read(ShapeManifest.Resolve(draft.CustomShape));}catch(Exception){SelectPage("floating");error.Text="请先导入有效的 HTML 形态，或选择内置形态。";return;}
            draft.RefreshSeconds=seconds;draft.Metrics=selected;draft.Validate();
            try{StartupRegistration.Configure(draft,Path.Combine(Program.Folder,"CodexUserData.exe"));}catch(Exception ex){SelectPage("behavior");error.Text="无法更新自启动设置："+ex.Message;return;}
            Result=draft;DialogResult=true;
        }
    }
}
