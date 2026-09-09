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
        private readonly TextBox refresh,home,database,quotaCli;
        private readonly TextBlock error,opacityLabel;
        private readonly ThemeEditor themeEditor;
        internal SettingsWindow(Preferences current,Action<double> preview)
        {
            draft=current.Clone();Title="悬浮窗设置";ShowInTaskbar=false;Width=430;Height=760;MinWidth=360;MinHeight=420;Theme.InstallStyles(this);
            MaxHeight=SystemParameters.WorkArea.Height;WindowStartupLocation=WindowStartupLocation.CenterOwner;
            Background=Theme.Background;Foreground=Theme.Ink;FontFamily=new FontFamily("Segoe UI, Microsoft YaHei UI");FontSize=12;
            var outer=new Grid{Margin=new Thickness(22,0,22,16),Background=Brushes.Transparent};SetBody(outer,"悬浮窗设置","PERSONALIZE",true);
            outer.RowDefinitions.Add(new RowDefinition{Height=new GridLength(1,GridUnitType.Star)});outer.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
            var scroll=new ScrollViewer{VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};outer.Children.Add(scroll);
            var body=new StackPanel{Margin=new Thickness(0,0,9,0)};scroll.Content=body;
            body.Children.Add(Theme.Text("让数据按你的方式显示",20,Theme.Ink));
            themeEditor=new ThemeEditor(draft);body.Children.Add(themeEditor);
            Closed+=delegate{Theme.Apply(Result??current);};
            Label(body,"悬浮窗形态");
            var ballStyle=new ChoiceButton(new Dictionary<string,string>{{"orb","光环 · 圆形额度球"},{"capsule","胶囊 · 今日 Tokens"},{"html","自定义 · HTML 形态"}},"悬浮窗形态");ballStyle.Select(draft.BallStyle);ballStyle.Changed+=v=>{draft.BallStyle=v;draft.BallExpanded=false;};body.Children.Add(ballStyle);
            var shapeName=Theme.Text(String.IsNullOrEmpty(draft.CustomShape)?"尚未导入自定义形态":Path.GetFileName(Path.GetDirectoryName(draft.CustomShape)),11,Theme.Muted);shapeName.TextWrapping=TextWrapping.Wrap;body.Children.Add(shapeName);
            var import=Theme.Button("导入 HTML 形态…","选择 shape.json",180);import.Margin=new Thickness(0,8,0,0);body.Children.Add(import);
            import.Click+=delegate{var picker=new Microsoft.Win32.OpenFileDialog{Filter="形态配置|shape.json",InitialDirectory=Path.Combine(Program.Folder,"examples")};if(picker.ShowDialog()!=true)return;try{draft.CustomShape=ShapeManifest.Import(picker.FileName);draft.BallStyle="html";ballStyle.Select("html");shapeName.Text=ShapeManifest.Read(ShapeManifest.Resolve(draft.CustomShape)).name??"已导入形态";}catch(Exception ex){error.Text=ex.Message;}};
            Note(body,"形态复制到用户数据目录的 skins 文件夹，原文件不会改动。任意位置右键可切回主界面；Alt + 拖动可移动。示例位于 examples，接口说明位于 docs。",Theme.Muted);
            var orbColors=Theme.Button("圆环配色  →","自定义短周期、长周期渐变",180);orbColors.HorizontalAlignment=HorizontalAlignment.Stretch;orbColors.Margin=new Thickness(0,10,0,0);orbColors.Height=36;body.Children.Add(orbColors);
            orbColors.Click+=delegate{var editor=new OrbAppearance(draft){Owner=this};if(editor.ShowDialog()==true){draft.OrbFollowTheme=editor.Result.OrbFollowTheme;draft.OrbShortColors=editor.Result.OrbShortColors;draft.OrbLongColors=editor.Result.OrbLongColors;draft.OrbShortAngle=editor.Result.OrbShortAngle;draft.OrbLongAngle=editor.Result.OrbLongAngle;}};
            Label(body,"圆球贴边后，额度条显示");
            var orbQuota=new ChoiceButton(new Dictionary<string,string>{{"auto","自动 · 优先短周期"},{"short","短周期剩余额度（通常 5 小时）"},{"week","长周期剩余额度（通常 7 天）"}},"贴边额度周期");orbQuota.Select(draft.OrbQuotaWindow);orbQuota.Changed+=v=>draft.OrbQuotaWindow=v;body.Children.Add(orbQuota);
            Label(body,"圆球尺寸");var orbSizeLabel=Theme.Text(((int)draft.OrbSize)+" px",11,Theme.Muted);body.Children.Add(orbSizeLabel);
            var orbSize=new Slider{Minimum=56,Maximum=128,Value=draft.OrbSize,TickFrequency=2,IsSnapToTickEnabled=true};System.Windows.Automation.AutomationProperties.SetName(orbSize,"圆球直径");orbSize.ValueChanged+=delegate{draft.OrbSize=orbSize.Value;orbSizeLabel.Text=((int)orbSize.Value)+" px";};body.Children.Add(orbSize);
            Label(body,"光环动画");
            var orbMotion=new ChoiceButton(new Dictionary<string,string>{{"auto","自动 · 按设备性能调节"},{"smooth","流畅 · 最高 60 帧"},{"eco","节能 · 最高 30 帧"},{"off","关闭动画"}},"光环动画档位");orbMotion.Select(draft.OrbAnimation);orbMotion.Changed+=v=>draft.OrbAnimation=v;body.Children.Add(orbMotion);
            Note(body,"运行或悬停时，圆环与底盘联动，粒子沿环流动。中心上方的小亮点只表示检测到任务活动；悬停不会点亮它。单击回弹，双击返回主窗，按住拖动。贴边后停止动画。",Theme.Muted);
            var completion=new CheckBox{Content="任务完成后闪烁提示",IsChecked=draft.CompletionFlash,Foreground=Theme.Ink,Margin=new Thickness(0,12,0,5)};body.Children.Add(completion);completion.Checked+=delegate{draft.CompletionFlash=true;};completion.Unchecked+=delegate{draft.CompletionFlash=false;};
            Note(body,"明确完成后持续柔和闪烁，鼠标移入悬浮窗或托盘即可确认；未确认时开始新任务会立即恢复运行效果。启动时的历史记录、后台审查、超时和中断不触发。任务状态仍受日志落盘延迟影响。",Theme.Muted);
            var prices=Theme.Button("模型价格  →","编辑所有模型价格",180);prices.HorizontalAlignment=HorizontalAlignment.Stretch;prices.Background=Theme.Surface;prices.Foreground=Theme.Accent;prices.Margin=new Thickness(0,14,0,0);prices.Height=38;body.Children.Add(prices);
            prices.Click+=delegate{var editor=new PriceEditor(draft){Owner=this};if(editor.ShowDialog()==true){draft.PriceOverrides=editor.Result;draft.KnownModels=editor.KnownModels;}};
            Note(body,"编辑已用模型的单价；保存设置后，全部历史用量按新价格重新估算。",Theme.Muted);
            Label(body,"数据来源");
            var source=new ChoiceButton(new Dictionary<string,string>{{"ccswitch","CC Switch 数据库"},{"local","本地 Codex 日志"}},"设置数据来源");source.Select(draft.Source);source.Changed+=v=>draft.Source=v;body.Children.Add(source);
            Note(body,"两个来源独立统计，切换查看；不相加，以免重复计算。",Theme.Muted);
            Label(body,"最小化方式");
            var minimizeMode=new ChoiceButton(new Dictionary<string,string>{{"taskbar","最小化到任务栏"},{"tray","最小化到托盘"}},"最小化方式");
            minimizeMode.Select(draft.MinimizeToTray?"tray":"taskbar");minimizeMode.Changed+=v=>draft.MinimizeToTray=v=="tray";body.Children.Add(minimizeMode);
            Note(body,"默认保留任务栏按钮；选择托盘后窗口隐藏，双击托盘图标可恢复。两种方式都会继续统计。",Theme.Muted);
            var startup=new CheckBox{Content="开机自启动（登录 Windows 后启动）",IsChecked=draft.StartWithWindows,Foreground=Theme.Ink,Margin=new Thickness(0,12,0,6)};body.Children.Add(startup);startup.Checked+=delegate{draft.StartWithWindows=true;};startup.Unchecked+=delegate{draft.StartWithWindows=false;};
            Note(body,"默认关闭。移动程序文件夹后，请重新启动软件以更新自启动路径。",Theme.Muted);
            var sync=new CheckBox{Content="随 Codex 启动",IsChecked=draft.StartWithCodex,Foreground=Theme.Ink,Margin=new Thickness(0,8,0,6)};body.Children.Add(sync);sync.Checked+=delegate{draft.StartWithCodex=true;draft.StartWithWindows=false;startup.IsChecked=false;};sync.Unchecked+=delegate{draft.StartWithCodex=false;};startup.Checked+=delegate{draft.StartWithCodex=false;sync.IsChecked=false;};
            Note(body,"启用后，登录 Windows 时运行轻量后台检测器；发现本机 Codex 桌面端或 CLI 启动后打开悬浮窗。手动退出后，本次 Codex 运行期间不再拉起。移动程序后需启动一次以更新路径。",Theme.Muted);
            Label(body,"悬浮球整体不透明度");
            var ballAlphaLabel=Theme.Text(((int)(draft.BallOpacity*100))+"%",12,Theme.Accent);body.Children.Add(ballAlphaLabel);
            var ballAlpha=new Slider{Minimum=20,Maximum=100,Value=draft.BallOpacity*100,TickFrequency=5,IsSnapToTickEnabled=true};ballAlpha.ValueChanged+=delegate{draft.BallOpacity=ballAlpha.Value/100;ballAlphaLabel.Text=((int)ballAlpha.Value)+"%";};body.Children.Add(ballAlpha);Note(body,"保存后应用到圆环、大小胶囊、贴边额度条和 HTML 形态，包括底盘、文字与特效。",Theme.Muted);
            Label(body,"窗口不透明度");
            opacityLabel=Theme.Text(((int)(draft.Opacity*100))+"%",12,Theme.Accent);body.Children.Add(opacityLabel);
            var opacity=new Slider{Minimum=20,Maximum=100,Value=draft.Opacity*100,TickFrequency=5,IsSnapToTickEnabled=true,Margin=new Thickness(0,8,0,2)};
            System.Windows.Automation.AutomationProperties.SetName(opacity,"窗口不透明度");
            opacity.ValueChanged+=delegate{draft.Opacity=opacity.Value/100;opacityLabel.Text=((int)opacity.Value)+"%";if(preview!=null)preview(draft.Opacity);};body.Children.Add(opacity);
            Label(body,"刷新间隔 · 秒");refresh=Theme.Input(draft.RefreshSeconds.ToString());body.Children.Add(refresh);Note(body,"2–3600 秒。首次索引完成后，只读取新增日志。",Theme.Muted);
            Label(body,"显示的数据");var metrics=new System.Windows.Controls.Primitives.UniformGrid{Columns=2};body.Children.Add(metrics);
            foreach(var entry in Theme.MetricLabels)
            {
                var box=new CheckBox{Content=entry.Value,IsChecked=draft.Metrics.Contains(entry.Key),Foreground=Theme.Ink,Margin=new Thickness(0,7,3,7),FontSize=11};
                fields[entry.Key]=box;metrics.Children.Add(box);
            }
            Note(body,"“估算费用”来自 CC Switch；API 等效价值按模型单价另算。推理已含在输出中，缺失指标显示“—”。",Theme.Muted);
            var models=new CheckBox{Content="显示模型、思考强度与 API 等效价值明细",IsChecked=draft.ShowModels,Foreground=Theme.Ink,Margin=new Thickness(0,10,0,8)};body.Children.Add(models);models.Checked+=delegate{draft.ShowModels=true;};models.Unchecked+=delegate{draft.ShowModels=false;};
            Label(body,"悬浮窗内显示图表");
            var heatmap=new CheckBox{Content="每日用量热度图",IsChecked=draft.ShowHeatmap,Foreground=Theme.Ink,Margin=new Thickness(0,5,0,8)};body.Children.Add(heatmap);heatmap.Checked+=delegate{draft.ShowHeatmap=true;};heatmap.Unchecked+=delegate{draft.ShowHeatmap=false;};
            var trend=new CheckBox{Content="每日 Token 趋势",IsChecked=draft.ShowTrend,Foreground=Theme.Ink,Margin=new Thickness(0,5,0,8)};body.Children.Add(trend);trend.Checked+=delegate{draft.ShowTrend=true;};trend.Unchecked+=delegate{draft.ShowTrend=false;};
            Note(body,"顶部 ▥ 可在独立大窗口查看两张图表。",Theme.Muted);
            Label(body,"Codex 剩余额度");
            var quota=new CheckBox{Content="顶部显示剩余额度",IsChecked=draft.ShowQuota,Foreground=Theme.Ink,Margin=new Thickness(0,5,0,8)};body.Children.Add(quota);quota.Checked+=delegate{draft.ShowQuota=true;};quota.Unchecked+=delegate{draft.ShowQuota=false;};
            var live=new CheckBox{Content="每分钟查询在线额度",IsChecked=draft.LiveQuota,Foreground=Theme.Ink,Margin=new Thickness(0,5,0,8)};body.Children.Add(live);live.Checked+=delegate{draft.LiveQuota=true;};live.Unchecked+=delegate{draft.LiveQuota=false;};
            Note(body,"使用已登录的 Codex CLI，只查询账号额度。托盘悬停也可查看；离线时明确标记最近快照，过期不推算余额。",Theme.Muted);
            Label(body,"Codex CLI 可执行文件");quotaCli=Theme.Input(draft.QuotaCli);body.Children.Add(quotaCli);
            var cliActions=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(0,7,0,0)};body.Children.Add(cliActions);
            var detect=Theme.Button("自动检测","重新检测本机 Codex CLI",108);cliActions.Children.Add(detect);
            var browseCli=Theme.Button("手动选择…","选择 codex.exe",108);browseCli.Margin=new Thickness(8,0,0,0);cliActions.Children.Add(browseCli);
            var cliStatus=Theme.Text(File.Exists(draft.QuotaCli)?"已找到本机可执行文件":"未找到 CLI；本地日志统计仍可使用。",11,Theme.Muted);cliStatus.TextWrapping=TextWrapping.Wrap;body.Children.Add(cliStatus);
            detect.Click+=async delegate{detect.IsEnabled=false;cliStatus.Text="正在检测…";try{string found=await System.Threading.Tasks.Task.Run(()=>QuotaReader.FindDefaultExe());if(!String.IsNullOrEmpty(found)){quotaCli.Text=found;cliStatus.Text="已自动填入检测结果。";}else{cliStatus.Text="所有位置均未找到，请手动选择 codex.exe。在线额度需要可用且已登录的 CLI。";cliStatus.Foreground=Theme.Warning;}}finally{detect.IsEnabled=true;}};
            browseCli.Click+=delegate{var picker=new Microsoft.Win32.OpenFileDialog{Filter="Codex CLI|codex.exe|可执行文件|*.exe"};if(picker.ShowDialog()==true){quotaCli.Text=picker.FileName;cliStatus.Text="已选择，保存后用于在线额度查询。";}};
            Label(body,"Codex 数据目录");home=Theme.Input(draft.CodexHome);PathRow(body,home,true);
            Label(body,"CC Switch 数据库");database=Theme.Input(draft.Database);PathRow(body,database,false);
            Note(body,"仅以只读方式访问数据。用量以本机日志落盘为准，不代表套餐剩余额度或实际账单。",Theme.Muted);
            Label(body,"数据保存位置");Note(body,"%LOCALAPPDATA%\\CodexUserData：同一 Windows 用户下，各新版共用设置、价格、自定义形态和用量缓存，与程序解压位置无关。1.4 版用户请先在原目录启动新版，自动迁移旧 data。",Theme.Muted);
            var openData=Theme.Button("打开用户数据目录","打开设置与缓存保存位置",180);openData.Margin=new Thickness(0,8,0,0);body.Children.Add(openData);openData.Click+=delegate{try{Directory.CreateDirectory(Program.DataFolder);System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo{FileName=Program.DataFolder,UseShellExecute=true});}catch(Exception ex){error.Text="无法打开数据目录："+ex.Message;}};
            var bottom=new StackPanel{Margin=new Thickness(0,12,0,0)};Grid.SetRow(bottom,1);outer.Children.Add(bottom);
            error=Theme.Text("",11,Theme.Warning);error.TextWrapping=TextWrapping.Wrap;bottom.Children.Add(error);
            var buttons=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,8,0,0)};bottom.Children.Add(buttons);
            var cancel=Theme.Button("取消","取消设置",72);cancel.IsCancel=true;cancel.Click+=delegate{DialogResult=false;};buttons.Children.Add(cancel);
            var save=Theme.Button("保存设置","保存设置",98);save.Background=Theme.Hover;save.Foreground=Theme.Accent;save.Margin=new Thickness(8,0,0,0);save.IsDefault=true;save.Click+=Save;buttons.Children.Add(save);
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
            if(!themeEditor.IsValid){error.Text="请检查自定义主题的颜色，使用 #RRGGBB 格式。";return;}
            int seconds;
            if(!Int32.TryParse(refresh.Text,out seconds)||seconds<2||seconds>3600){error.Text="刷新间隔请输入 2–3600 之间的整数。";return;}
            string[] selected=fields.Where(x=>x.Value.IsChecked==true).Select(x=>x.Key).ToArray();
            if(selected.Length==0){error.Text="请至少选择一项显示数据。";return;}
            try{draft.CodexHome=Path.GetFullPath(home.Text.Trim());draft.Database=Path.GetFullPath(database.Text.Trim());draft.QuotaCli=String.IsNullOrWhiteSpace(quotaCli.Text)?"":Path.GetFullPath(quotaCli.Text.Trim());}
            catch(Exception){error.Text="请填写有效的本地路径。";return;}
            if(draft.Source=="local"&&!Directory.Exists(Path.Combine(draft.CodexHome,"sessions"))&&!Directory.Exists(Path.Combine(draft.CodexHome,"archived_sessions"))){error.Text="这个目录中未找到 sessions 或 archived_sessions。";return;}
            if(draft.Source=="ccswitch"&&!File.Exists(draft.Database)){error.Text="找不到 CC Switch 数据库文件。";return;}
            if(draft.BallStyle=="html")try{ShapeManifest.Read(ShapeManifest.Resolve(draft.CustomShape));}catch(Exception){error.Text="请先导入有效的 HTML 形态，或选择内置形态。";return;}
            draft.RefreshSeconds=seconds;draft.Metrics=selected;draft.Validate();
            try{StartupRegistration.Configure(draft,Path.Combine(Program.Folder,"CodexUserData.exe"));}catch(Exception ex){error.Text="无法更新自启动设置："+ex.Message;return;}
            Result=draft;DialogResult=true;
        }
    }
}
