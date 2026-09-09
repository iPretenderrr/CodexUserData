using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace CodexUserData
{
    internal sealed class PriceEditor : StyledWindow
    {
        private sealed class Row
        {
            internal string Model;internal Border Card;internal TextBox[] Inputs;internal TextBlock State;
            internal bool Custom;
        }
        private readonly Dictionary<string,Row> rows=new Dictionary<string,Row>(StringComparer.OrdinalIgnoreCase);
        private readonly Preferences draft;
        private readonly StackPanel list=new StackPanel();
        private readonly TextBlock status=Theme.Text("正在补齐两个来源的全部历史模型…",11,Theme.Muted),error=Theme.Text("",11,Theme.Warning);
        private readonly TextBox search=Theme.Input("");
        private volatile bool closed;
        internal Dictionary<string,decimal[]> Result;
        internal string[] KnownModels;
        internal PriceEditor(Preferences preferences)
        {
            draft=preferences.Clone();Title="模型价格";Width=650;Height=730;MinWidth=360;MinHeight=440;MaxHeight=SystemParameters.WorkArea.Height;ShowInTaskbar=false;WindowStartupLocation=WindowStartupLocation.CenterOwner;
            FontFamily=new System.Windows.Media.FontFamily("Segoe UI, Microsoft YaHei UI");
            var root=new DockPanel{Margin=new Thickness(18,0,18,16)};SetBody(root,"模型价格","API EQUIVALENT · USD",true);
            var intro=new StackPanel();DockPanel.SetDock(intro,Dock.Top);root.Children.Add(intro);
            var note=Theme.Text("每百万 Tokens 的美元价格。留空或没有价格按 0 估算，不代表实际免费。\n按模型统一应用到所有思考强度及历史日期，仅影响等效估算。",11,Theme.Muted);note.TextWrapping=TextWrapping.Wrap;note.Margin=new Thickness(0,0,0,10);intro.Children.Add(note);
            intro.Children.Add(Theme.Text("查找模型",10,Theme.Muted));search.Margin=new Thickness(0,5,0,0);search.ToolTip="搜索模型名称";AutomationProperties.SetName(search,"搜索模型价格");intro.Children.Add(search);search.TextChanged+=delegate{Filter();};
            status.TextWrapping=TextWrapping.Wrap;status.Margin=new Thickness(0,8,0,10);intro.Children.Add(status);
            var footer=new StackPanel{Margin=new Thickness(0,10,0,0)};DockPanel.SetDock(footer,Dock.Bottom);root.Children.Add(footer);
            error.TextWrapping=TextWrapping.Wrap;footer.Children.Add(error);
            var actions=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};footer.Children.Add(actions);
            var cancel=Theme.Button("取消","取消价格编辑",72);cancel.Click+=delegate{WindowInteraction.CompleteDialog(this,false);};actions.Children.Add(cancel);
            var save=Theme.Button("应用到设置","应用模型价格",108);save.Background=Theme.Hover;save.Foreground=Theme.Accent;save.Click+=delegate{Save();};actions.Children.Add(save);
            var scroll=new ScrollViewer{Content=list,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};root.Children.Add(scroll);
            PreviewKeyDown+=delegate(object sender,System.Windows.Input.KeyEventArgs args){if(args.Key==System.Windows.Input.Key.Escape){args.Handled=true;WindowInteraction.CompleteDialog(this,false);}};
            AddModels(draft.KnownModels.Concat(draft.PriceOverrides.Keys).Concat(ApiPrices.DefaultModels));
            Loaded+=delegate{LoadModels();};Closed+=delegate{closed=true;};
        }
        private async void LoadModels()
        {
            // A separate reader uses the existing numeric index; never mutates the live reader/cache.
            var result=await Task.Run(()=>
            {
                var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);var failures=new List<string>();
                try{if(File.Exists(draft.Database))names.UnionWith(UsageDatabase.ModelCatalog(draft.Database));else failures.Add("CC Switch 数据库未找到");}catch(Exception){failures.Add("CC Switch 模型读取失败");}
                try
                {
                    if(Directory.Exists(draft.CodexHome))
                    {
                        var reader=new LocalCodexUsage(draft.CodexHome,Path.Combine(Program.DataFolder,"local-codex-cache.json.gz"),true);
                        names.UnionWith(reader.Read("all",DateTime.Now,null,()=>closed).KnownModels);
                    }
                    else failures.Add("本地 Codex 目录未找到");
                }
                catch(OperationCanceledException){}catch(Exception){failures.Add("本地 Codex 模型读取失败");}
                return Tuple.Create(names.ToArray(),String.Join("；",failures));
            });
            if(closed)return;AddModels(result.Item1);status.Text=rows.Count+" 个模型 · 已合并历史记录、内置价格和自定义价格"+(result.Item2.Length>0?"\n"+result.Item2:" ");
        }
        private void AddModels(IEnumerable<string> names)
        {
            foreach(string model in names.Where(m=>!String.IsNullOrWhiteSpace(m)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(m=>m))
            {
                if(rows.ContainsKey(model))continue;
                decimal[] rate;bool custom=draft.PriceOverrides.TryGetValue(model,out rate);if(!custom)rate=ApiPrices.Default(model);
                var row=new Row{Model=model,Inputs=new TextBox[4],Custom=custom};rows[model]=row;
                var panel=new StackPanel();row.Card=new Border{Background=Theme.Surface,CornerRadius=new CornerRadius(9),Padding=new Thickness(12,9,12,10),Margin=new Thickness(0,0,6,7),Child=panel};
                var header=new DockPanel();panel.Children.Add(header);
                var reset=Theme.Button("恢复默认","恢复 "+model+" 默认价格",68);reset.FontSize=10;reset.Height=25;reset.Padding=new Thickness(5,2,5,2);DockPanel.SetDock(reset,Dock.Right);header.Children.Add(reset);
                row.State=Theme.Text(custom?"自定义":rate.All(v=>v<0)?"按 0 估算":"内置",10,Theme.Muted);row.State.Margin=new Thickness(8,0,6,0);DockPanel.SetDock(row.State,Dock.Right);header.Children.Add(row.State);
                var title=Theme.Text("● "+model,12,ModelColors.For(model));title.TextTrimming=TextTrimming.CharacterEllipsis;title.ToolTip=model;header.Children.Add(title);
                var fields=new UniformGrid{Columns=4,Margin=new Thickness(-3,7,-3,0)};panel.Children.Add(fields);
                string[] labels={"未缓存输入","缓存读取","缓存创建","输出"};
                for(int i=0;i<4;i++)
                {
                    var cell=new StackPanel{Margin=new Thickness(3,0,3,0)};fields.Children.Add(cell);cell.Children.Add(Theme.Text(labels[i],10,Theme.Muted));
                    var input=Theme.Input(RateText(rate[i]));input.Padding=new Thickness(5);input.Margin=new Thickness(0,5,0,0);input.ToolTip=labels[i]+" · USD / 1M Tokens · 留空按 0 估算";AutomationProperties.SetName(input,model+" "+labels[i]+"价格");row.Inputs[i]=input;cell.Children.Add(input);
                    input.TextChanged+=delegate{row.Custom=true;row.State.Text="自定义";};
                }
                reset.Click+=delegate{var defaults=ApiPrices.Default(model);for(int i=0;i<4;i++)row.Inputs[i].Text=RateText(defaults[i]);row.Custom=false;row.State.Text=defaults.All(v=>v<0)?"按 0 估算":"内置";};
            }
            // Only sort when the async catalog adds names; existing TextBoxes and edits stay intact.
            int index=0;foreach(var row in rows.Values.OrderBy(r=>r.Model)){if(!list.Children.Contains(row.Card))list.Children.Insert(index,row.Card);index++;}Filter();
        }
        private void Filter(){foreach(var row in rows.Values)row.Card.Visibility=row.Model.IndexOf(search.Text.Trim(),StringComparison.OrdinalIgnoreCase)>=0?Visibility.Visible:Visibility.Collapsed;}
        private static string RateText(decimal value){return value<0?"":value.ToString("0.########",CultureInfo.InvariantCulture);}
        internal static bool Parse(string text,out decimal value)
        {
            value=-1;if(String.IsNullOrWhiteSpace(text))return true;
            return Decimal.TryParse(text.Trim(),NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out value)&&value>=0&&value<=1000000;
        }
        private void Save()
        {
            var result=new Dictionary<string,decimal[]>(StringComparer.OrdinalIgnoreCase);
            foreach(var row in rows.Values)
            {
                var rates=new decimal[4];for(int i=0;i<4;i++)if(!Parse(row.Inputs[i].Text,out rates[i])){error.Text=row.Model+"：请输入 0–1000000 的价格，或留空；小数点使用 .";search.Text="";row.Inputs[i].BringIntoView();row.Inputs[i].Focus();return;}
                if(row.Custom)result[row.Model]=rates;
            }
            Result=result;KnownModels=rows.Keys.ToArray();WindowInteraction.CompleteDialog(this,true);
        }
    }
}
