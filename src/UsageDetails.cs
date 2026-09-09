using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace CodexUserData
{
    // Build the selected day's grouped rows only after a short hover dwell, not on every pointer pixel.
    internal sealed class UsageDetails : Border
    {
        private readonly StackPanel body=new StackPanel();
        private string signature;
        private readonly string idPrefix,caption;
        private UniformGrid modelGrid,metricGrid;
        private void Reflow(){if(modelGrid!=null)modelGrid.Columns=ActualWidth<600?1:ActualWidth<900?2:3;if(metricGrid!=null)metricGrid.Columns=ActualWidth<500?2:4;}
        internal string Heading {get;private set;}
        internal int ModelRows {get;private set;}
        internal UsageDetails(string prefix="",string title="")
        {
            idPrefix=prefix;caption=title;AutomationProperties.SetAutomationId(this,prefix+"DetailCard");
            Background=Theme.Surface;CornerRadius=new CornerRadius(11);Padding=new Thickness(14);Margin=new Thickness(0,10,0,0);Child=body;
            SizeChanged+=delegate{Reflow();};Clear();
        }
        internal void Clear(string message="等待当前来源的数据")
        {
            if(signature=="empty/"+message)return;signature="empty/"+message;Heading=message;ModelRows=0;body.Children.Clear();
            var text=Theme.Text(message,11,Theme.Muted);text.TextWrapping=TextWrapping.Wrap;AutomationProperties.SetAutomationId(text,idPrefix+"DetailStatus");body.Children.Add(text);
        }
        internal void Apply(DailyUsage day,bool pinned,string countLabel)
        {
            string next=UsageChart.Signature(new[]{day})+"/"+pinned+"/"+countLabel;if(next==signature)return;signature=next;body.Children.Clear();
            if(caption.Length>0){var title=Theme.Text(caption,11,Theme.Muted);title.Margin=new Thickness(0,0,0,9);body.Children.Add(title);}
            Heading=day.Date+(pinned?" · 已选中":"");
            var heading=new DockPanel();var badge=Theme.Text(pinned?"已选中":"用量明细",10,pinned?Theme.Accent:Theme.Muted);DockPanel.SetDock(badge,Dock.Right);heading.Children.Add(badge);
            var date=Theme.Text(day.Date,13,Theme.Ink);date.FontWeight=FontWeights.SemiBold;AutomationProperties.SetAutomationId(date,idPrefix+"DailyUsageDetail");AutomationProperties.SetName(date,Heading);heading.Children.Add(date);body.Children.Add(heading);
            var totals=new UniformGrid{Columns=2,Margin=new Thickness(-3,10,-3,0)};body.Children.Add(totals);
            Metric(totals,"TOKENS",TokenText.Compact(day.Tokens),Theme.Ink,idPrefix+"SelectedDayTokens");
            Metric(totals,"API 等效 · USD",ModelColors.Money(day.Models.Sum(m=>m.EquivalentUsd),day.Models.Sum(m=>m.UnpricedTokens),day.Tokens),Theme.Ink,idPrefix+"SelectedDayValue");
            var metrics=new UniformGrid{Columns=2,Margin=new Thickness(-3,3,-3,7)};body.Children.Add(metrics);metricGrid=metrics;
            Metric(metrics,"未缓存输入",TokenText.Compact(day.Input),Theme.Muted,null,true);Metric(metrics,"输出",TokenText.Compact(day.Output),Theme.Muted,null,true);
            Metric(metrics,"缓存读取",TokenText.Compact(day.CacheRead),Theme.Muted,null,true);Metric(metrics,"缓存创建",TokenText.Compact(day.CacheWrite),Theme.Muted,null,true);
            var line=new DockPanel{Margin=new Thickness(1,5,1,8)};line.Children.Add(Theme.Text("模型构成",11,Theme.Ink));var count=Theme.Text(countLabel+"  "+TokenText.Compact(day.Requests),10,Theme.Muted);count.HorizontalAlignment=HorizontalAlignment.Right;line.Children.Add(count);body.Children.Add(line);
            modelGrid=new UniformGrid{Columns=1,Margin=new Thickness(-3,0,-3,0)};body.Children.Add(modelGrid);
            ModelRows=0;
            foreach(var group in day.Models.GroupBy(m=>m.Model).OrderByDescending(g=>g.Sum(m=>m.Tokens)))
            {
                ModelRows++;long tokens=group.Sum(m=>m.Tokens);double fraction=day.Tokens>0?tokens/(double)day.Tokens:0;Brush color=ModelColors.For(group.Key);
                var panel=new StackPanel();var row=new Border{Background=Theme.Background,CornerRadius=new CornerRadius(8),Padding=new Thickness(10),Margin=new Thickness(3,0,3,6),Child=panel};modelGrid.Children.Add(row);
                var names=new Grid();names.ColumnDefinitions.Add(new ColumnDefinition());names.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});panel.Children.Add(names);
                var name=Theme.Text("● "+group.Key,11,color);name.TextTrimming=TextTrimming.CharacterEllipsis;name.ToolTip=group.Key;name.Margin=new Thickness(0,0,8,0);names.Children.Add(name);
                var amount=Theme.Text(TokenText.Compact(tokens),13,Theme.Ink);amount.FontWeight=FontWeights.SemiBold;Grid.SetColumn(amount,1);names.Children.Add(amount);
                var bar=new Grid{Height=3,Margin=new Thickness(0,7,0,6),Background=Theme.Line};bar.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(Math.Max(.001,fraction),GridUnitType.Star)});bar.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(Math.Max(.001,1-fraction),GridUnitType.Star)});bar.Children.Add(new Border{Background=color});panel.Children.Add(bar);
                var values=new DockPanel();var share=Theme.Text((fraction*100).ToString("0.#",CultureInfo.InvariantCulture)+"%",10,Theme.Muted);values.Children.Add(share);
                var cost=Theme.Text(ModelColors.Money(group.Sum(m=>m.EquivalentUsd),group.Sum(m=>m.UnpricedTokens),tokens),10,Theme.Muted);cost.HorizontalAlignment=HorizontalAlignment.Right;values.Children.Add(cost);panel.Children.Add(values);
                var efforts=new WrapPanel{Margin=new Thickness(0,6,0,0)};panel.Children.Add(efforts);
                foreach(var effort in group.OrderByDescending(m=>m.Tokens))
                {
                    var label=Theme.Text(ModelColors.Effort(effort.Effort)+"  "+TokenText.Compact(effort.Tokens),10,color);
                    efforts.Children.Add(new Border{Child=label,Background=Theme.Surface,CornerRadius=new CornerRadius(4),Padding=new Thickness(6,3,6,3),Margin=new Thickness(0,3,5,0),ToolTip="API 等效 "+ModelColors.Money(effort.EquivalentUsd,effort.UnpricedTokens,effort.Tokens)});
                }
            }
            Reflow();
            if(ModelRows==0)body.Children.Add(Theme.Text("该时段暂无已记录用量",11,Theme.Muted));
        }
        private static void Metric(Panel parent,string label,string value,Brush color,string id,bool small=false)
        {
            var stack=new StackPanel();stack.Children.Add(Theme.Text(label,9,Theme.Muted));
            var number=Theme.Text(value,small?13:17,color);number.FontWeight=FontWeights.SemiBold;number.TextWrapping=TextWrapping.Wrap;number.ToolTip=value;number.Margin=new Thickness(0,4,0,0);
            if(id!=null)AutomationProperties.SetAutomationId(number,id);stack.Children.Add(number);
            parent.Children.Add(new Border{Background=Theme.Background,CornerRadius=new CornerRadius(7),Padding=new Thickness(9,8,9,8),Margin=new Thickness(3),Child=stack});
        }
    }
}
