using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace CodexUserData
{
    // The page owns its range and selection. Presets reuse the bounded snapshot;
    // custom reads are serialized and only the latest visible request may publish.
    internal sealed class ModelSharePanel : StackPanel, IDisposable
    {
        private readonly Func<DateTime,DateTime,CancellationToken,Task<UsageSnapshot>> readRange;
        private readonly Func<string> currentScope;
        private readonly UsageChart chart=new UsageChart(false);
        private readonly Dictionary<int,Button> ranges=new Dictionary<int,Button>();
        private readonly Dictionary<string,Button> modes=new Dictionary<string,Button>();
        private readonly StackPanel headerActions=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};
        private readonly TextBlock title=Theme.Text("模型占比 · 每日",13,Theme.Ink);
        private readonly DockPanel state=new DockPanel{Margin=new Thickness(0,3,0,2)};
        private readonly WrapPanel legend=new WrapPanel{Margin=new Thickness(0,5,0,5)};
        private readonly TextBlock source=Theme.Text("",11,Theme.Muted),status=Theme.Text("",11,Theme.Muted),warning=Theme.Text("",11,Theme.Warning);
        private readonly StackPanel detailRows=new StackPanel();
        private readonly Border detail=new Border{Visibility=Visibility.Collapsed,Background=Theme.Surface,CornerRadius=new CornerRadius(7),Padding=new Thickness(9),Margin=new Thickness(0,5,0,0)};
        private readonly Button customButton,retry;
        private UsageSnapshot snapshot,customSnapshot;
        private UsageRangeSpec customRange;
        private CancellationTokenSource cancellation;
        private Window owner;
        private bool ownerMinimized;
        private string knownScope,sourceLabel,selectedDate,highlighted,viewSignature;
        private int rangeDays=30,generation;
        private bool loading,pending,pumpQueued,disposed;
        private string failure,snapshotFailure,aggregation="daily";

        internal ModelSharePanel(Func<DateTime,DateTime,CancellationToken,Task<UsageSnapshot>> loader,Func<string> scope)
        {
            readRange=loader;currentScope=scope;
            // Match the usage chart: title/actions first, date ranges below.
            // Keep clear + aggregation together when a narrow window needs a new row.
            var header=new Grid();header.ColumnDefinitions.Add(new ColumnDefinition());header.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});header.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});header.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
            title.TextWrapping=TextWrapping.Wrap;title.VerticalAlignment=VerticalAlignment.Center;header.Children.Add(title);
            Grid.SetColumn(headerActions,1);header.Children.Add(headerActions);Children.Add(header);
            var clear=Theme.Button("清除选择","清除所选时段（Esc）",66);clear.Height=27;clear.FontSize=11;clear.Margin=new Thickness(0,0,6,0);clear.Click+=delegate{ClearSelection();};headerActions.Children.Add(clear);
            headerActions.Children.Add(Theme.AggregationSelector(modes,"模型占比",SetAggregation));
            var choices=new WrapPanel{Margin=new Thickness(-2,9,0,8)};Children.Add(choices);
            foreach(int days in new[]{1,7,14,30,60,90,180})
            {
                int choice=days;var button=Theme.Button(days==1?"当天":days+"D",days==1?"当天按小时显示":"近 "+days+" 天按日显示",42);button.Height=27;button.FontSize=11;button.Margin=new Thickness(0,0,3,4);
                button.Click+=delegate{SelectRange(choice);};ranges.Add(days,button);choices.Children.Add(button);
            }
            customButton=Theme.Button("自定义","选择日期和时间范围",64);customButton.Height=27;customButton.FontSize=11;customButton.Margin=new Thickness(0,0,3,4);customButton.Click+=delegate{OpenRange();};choices.Children.Add(customButton);
            source.TextWrapping=TextWrapping.Wrap;Children.Add(source);
            retry=Theme.Button("重试","重新读取当前自定义范围",44);retry.Height=24;retry.Visibility=Visibility.Collapsed;retry.Click+=delegate{Refresh();};DockPanel.SetDock(retry,Dock.Right);state.Children.Add(retry);status.TextWrapping=TextWrapping.Wrap;state.Children.Add(status);Children.Add(state);
            chart.Height=350;Children.Add(chart);Children.Add(legend);warning.TextWrapping=TextWrapping.Wrap;Children.Add(warning);detail.Child=detailRows;Children.Add(detail);
            chart.Pick+=Pick;PreviewKeyDown+=OnKeyDown;Loaded+=OnLoaded;Unloaded+=OnUnloaded;IsVisibleChanged+=OnVisibleChanged;
            SizeChanged+=OnSizeChanged;UpdateButtons();
        }
        private bool Active{get{return !disposed&&IsLoaded&&IsVisible&&(owner==null||owner.WindowState!=WindowState.Minimized);}}
        internal void Apply(UsageSnapshot value,string scope,string label)
        {
            if(disposed)return;
            bool sourceChanged=!String.Equals(knownScope,scope,StringComparison.Ordinal);if(sourceChanged)ResetSource(scope);
            bool changed=sourceChanged||!Object.ReferenceEquals(snapshot,value);snapshot=value;sourceLabel=label;
            if(customRange!=null&&changed)pending=true;
            if(Active){Render();QueuePump();}
        }
        internal void FailRefresh(string message,string scope)
        {
            if(disposed||scope!=knownScope)return;snapshotFailure=String.IsNullOrWhiteSpace(message)?"读取失败":message;if(Active)UpdateStatus();
        }
        // A cached snapshot can be replayed during range changes or window restore.
        // Only the completed source read is allowed to clear its failure notice.
        internal void RefreshSucceeded(){if(disposed)return;snapshotFailure=null;if(Active)UpdateStatus();}
        internal void SetAggregation(string mode)
        {
            if(disposed||!modes.ContainsKey(mode)||aggregation==mode)return;aggregation=mode;viewSignature=null;ClearSelection();UpdateButtons();if(Active)Render();
        }
        private void ResetSource(string scope)
        {
            generation++;if(cancellation!=null)cancellation.Cancel();knownScope=scope;snapshot=customSnapshot=null;pending=false;failure=snapshotFailure=null;viewSignature=null;highlighted=null;chart.Highlight(null);ClearSelection();
            // Clear old-source values immediately, even when the page is hidden.
            chart.SetData(null,rangeDays,false,0,false,"daily",false,true);legend.Children.Clear();source.Text="";status.Text="等待当前来源的数据";warning.Text="";retry.Visibility=Visibility.Collapsed;
        }
        internal void SelectRange(int days)
        {
            if(disposed||!ranges.ContainsKey(days)||customRange==null&&rangeDays==days)return;
            generation++;if(cancellation!=null)cancellation.Cancel();rangeDays=days;customRange=null;customSnapshot=null;pending=false;failure=null;viewSignature=null;ClearSelection();UpdateButtons();if(Active)Render();
        }
        internal void SetRange(DateTime from,DateTime to)
        {
            if(disposed)return;var next=UsageRangeSpec.Create(from,to);
            if(customRange!=null&&customRange.Key==next.Key){Refresh();return;}
            generation++;if(cancellation!=null)cancellation.Cancel();customRange=next;customSnapshot=null;rangeDays=-1;failure=null;pending=true;viewSignature=null;ClearSelection();UpdateButtons();if(Active){Render();QueuePump();}
        }
        private void OpenRange()
        {
            DateTime from=customRange==null?DateTime.Today.AddDays(1-Math.Max(1,rangeDays)):customRange.From,to=customRange==null?DateTime.Now:customRange.To;
            var picker=new DateTimeRangePicker(from,to);var window=new StyledWindow{Title="模型占比时间范围",Width=760,Height=430,MinWidth=720,MinHeight=410,ResizeMode=ResizeMode.NoResize,Owner=Window.GetWindow(this),ShowInTaskbar=false,WindowStartupLocation=WindowStartupLocation.CenterOwner};window.SetBody(picker,"选择日期和时间","CUSTOM RANGE",false);
            picker.CancelButton.Click+=delegate{WindowInteraction.Close(window);};picker.ApplyButton.Click+=delegate{WindowInteraction.CompleteDialog(window,true,()=>picker.TryRead(out from,out to));};if(window.ShowDialog()==true)SetRange(from,to);
        }
        private void UpdateButtons()
        {
            title.Text="模型占比 · "+(aggregation=="weekly"?"每周":aggregation=="cumulative"?"累计":"每日");
            foreach(var pair in ranges){bool active=customRange==null&&rangeDays==pair.Key;pair.Value.Background=active?Theme.Hover:Brushes.Transparent;pair.Value.Foreground=active?Theme.Accent:Theme.Muted;}
            customButton.Background=customRange!=null?Theme.Hover:Brushes.Transparent;customButton.Foreground=customRange!=null?Theme.Accent:Theme.Muted;
            foreach(var pair in modes){bool active=pair.Key==aggregation;pair.Value.Background=active?Theme.Hover:Brushes.Transparent;pair.Value.Foreground=active?Theme.Accent:Theme.Muted;System.Windows.Automation.AutomationProperties.SetItemStatus(pair.Value,active?"已选中":"未选中");}
        }
        internal void Refresh()
        {
            if(disposed)return;string scope=currentScope();if(!String.Equals(knownScope,scope,StringComparison.Ordinal))ResetSource(scope);
            if(!Active)return;if(customRange!=null)pending=true;Render();QueuePump();
        }
        private void QueuePump()
        {
            if(loading||pumpQueued||!pending||customRange==null||!Active)return;pumpQueued=true;
            Dispatcher.BeginInvoke(DispatcherPriority.Background,new Action(delegate{pumpQueued=false;if(Active&&pending&&customRange!=null)Pump();}));
        }
        private async void Pump()
        {
            if(loading||!pending||!Active||customRange==null)return;loading=true;pending=false;failure=null;
            var range=customRange;string scope=knownScope;int version=generation;var request=new CancellationTokenSource();cancellation=request;UpdateStatus();
            UsageSnapshot result=null;Exception error=null;
            try{result=await readRange(range.From,range.To,request.Token);}catch(OperationCanceledException){}catch(Exception ex){error=ex;}
            // Refresh notifications coalesce while a read is running; they do not
            // cancel it, so slow disks can still publish fresh data.
            bool accept=Active&&version==generation&&!request.IsCancellationRequested&&String.Equals(scope,currentScope(),StringComparison.Ordinal)&&customRange!=null&&customRange.Key==range.Key;
            if(Object.ReferenceEquals(cancellation,request))cancellation=null;request.Dispose();loading=false;
            if(accept){if(error!=null)failure=String.IsNullOrWhiteSpace(error.Message)?"读取失败，请重试。":error.Message;else if(result==null||result.DataUnavailable)failure=result==null?"未取得数据":String.IsNullOrWhiteSpace(result.Warning)?"当前来源的数据暂不可用，请重试。":result.Warning;else{customSnapshot=result;failure=null;}Render();}
            QueuePump();
        }
        private void Render()
        {
            if(!Active)return;UsageSnapshot data=customRange==null?snapshot:customSnapshot;bool hourly=customRange==null&&rangeDays==1;
            if(data!=null&&data.DataUnavailable)data=null;
            DailyUsage[] raw=data==null?new DailyUsage[0]:customRange!=null?((data.Timeline??new DailyUsage[0]).Length>0?data.Timeline:data.Daily):hourly?data.Hourly:data.Daily;raw=raw??new DailyUsage[0];
            int through=hourly&&data!=null?Math.Min(raw.Length,data.HourlyThrough):raw.Length;
            DailyUsage[] visible=hourly?raw.Take(through).ToArray():customRange!=null?raw:raw.Skip(Math.Max(0,raw.Length-rangeDays)).ToArray();
            source.Text=customRange==null?"":customRange.Label;source.ToolTip=sourceLabel;source.Visibility=customRange==null?Visibility.Collapsed:Visibility.Visible;UpdateStatus();
            warning.Text=data==null?"":data.Warning??"";
            if(data!=null&&data.CoverageWarnings>0&&String.IsNullOrWhiteSpace(warning.Text))warning.Text="部分记录未计入；占比仅代表当前已读数据。";
            if(hourly&&data!=null&&data.HourlyUnallocatedTokens>0)warning.Text+=(warning.Text.Length>0?"\n":"")+"另有 "+TokenText.Compact(data.HourlyUnallocatedTokens)+" Tokens 只有日汇总，未分摊到小时。";
            warning.Visibility=String.IsNullOrWhiteSpace(warning.Text)?Visibility.Collapsed:Visibility.Visible;
            string signature=aggregation+"/"+(data!=null?"ready":customRange==null&&snapshot!=null&&snapshot.DataUnavailable?"unavailable":"waiting")+"/"+hourly+"/"+through+"/"+UsageChart.Signature(visible);if(signature==viewSignature)return;viewSignature=signature;
            // Restrict to the selected range before weekly/cumulative aggregation.
            // Legend totals stay based on raw buckets, never summed running totals.
            var plotted=aggregation=="daily"?raw:ModelShareValues.Aggregate(visible,aggregation);bool plotHourly=hourly&&aggregation!="weekly";
            int plotThrough=plotHourly?Math.Min(through,plotted.Length):plotted.Length;
            chart.SetData(plotted,aggregation=="daily"&&customRange==null?rangeDays:plotted.Length,plotHourly,plotThrough,false,"daily",false,true);
            var totals=new Dictionary<string,long>(StringComparer.OrdinalIgnoreCase);
            foreach(var day in visible)foreach(var item in ModelShareValues.Tokens(day)){long previous;totals.TryGetValue(item.Key,out previous);totals[item.Key]=checked(previous+item.Value);}
            long total=totals.Values.Sum();ModelColors.EnsureModels(totals.Keys);legend.Children.Clear();
            if(highlighted!=null&&!totals.ContainsKey(highlighted))highlighted=null;chart.Highlight(highlighted);
            foreach(var pair in totals.OrderByDescending(p=>p.Value))
            {
                string model=pair.Key;double percent=ModelShareValues.Percent(pair.Value,total);string share=percent>0&&percent<.1?"<0.1%":percent.ToString("0.0",CultureInfo.InvariantCulture)+"%";
                var button=Theme.LegendChip(model,ModelColors.For(model),share,model+" · 区间占比 "+share+"\n点击高亮，再次点击取消");button.Tag=model;SizeLegend(button);
                button.MouseEnter+=delegate{chart.Highlight(model);};button.MouseLeave+=delegate{chart.Highlight(highlighted);};button.Click+=delegate{highlighted=String.Equals(highlighted,model,StringComparison.OrdinalIgnoreCase)?null:model;chart.Highlight(highlighted);UpdateLegendSelection();};legend.Children.Add(button);
            }
            UpdateLegendSelection();
            if(total==0)legend.Children.Add(Theme.Text(data==null?(customRange==null&&snapshot!=null&&snapshot.DataUnavailable?"当前数据不可用":"等待数据"):"该范围没有已记录用量",11,Theme.Muted));
            int selected=selectedDate==null?-1:Array.FindIndex(plotted,d=>d.Date==selectedDate);int first=plotHourly||customRange!=null||aggregation!="daily"?0:Math.Max(0,plotted.Length-rangeDays);
            if(selected<first||selected>=plotThrough){ClearSelection();return;}chart.Selected=selected;RenderDetail(plotted[selected]);chart.InvalidateVisual();
        }
        private void UpdateLegendSelection()
        {
            foreach(Button button in legend.Children.OfType<Button>())Theme.SelectChip(button,String.Equals(button.Tag as string,highlighted,StringComparison.OrdinalIgnoreCase));
        }
        private void UpdateStatus()
        {
            state.Visibility=Visibility.Visible;
            if(customRange==null&&!String.IsNullOrEmpty(snapshotFailure)){retry.Visibility=Visibility.Collapsed;status.Foreground=Theme.Warning;status.Text=snapshot==null?"读取失败，等待重试":"刷新失败，当前为上次结果";status.ToolTip=snapshotFailure;return;}
            if(customRange==null&&snapshot!=null&&snapshot.DataUnavailable){retry.Visibility=Visibility.Collapsed;status.Foreground=Theme.Warning;status.Text="当前数据不可用，等待同步";status.ToolTip=String.IsNullOrWhiteSpace(snapshot.Warning)?"暂时无法读取当前来源。":snapshot.Warning;return;}
            bool failed=!String.IsNullOrEmpty(failure);retry.Visibility=failed?Visibility.Visible:Visibility.Collapsed;status.Foreground=failed?Theme.Warning:Theme.Muted;
            status.Text=failed?"读取失败，可重试"+(customSnapshot!=null?"；保留上次结果":""):customRange!=null&&(loading||pending)?(customSnapshot==null?"正在读取…":"正在更新…"):"";status.ToolTip=failed?failure:null;state.Visibility=status.Text.Length==0?Visibility.Collapsed:Visibility.Visible;
        }
        private void Pick(int index,bool clicked)
        {
            if(!clicked||index<0||index>=chart.Days.Length)return;selectedDate=chart.Days[index].Date;RenderDetail(chart.Days[index]);
        }
        private void RenderDetail(DailyUsage day)
        {
            detailRows.Children.Clear();var title=Theme.Text(String.IsNullOrEmpty(day.DisplayLabel)?day.Date:day.DisplayLabel,12,Theme.Ink);title.TextWrapping=TextWrapping.Wrap;detailRows.Children.Add(title);
            var parts=ModelShareValues.Tokens(day);long total=parts.Values.Sum();
            if(total==0)detailRows.Children.Add(Theme.Text("该时段无用量，不计算占比",11,Theme.Muted));
            else foreach(var pair in parts.OrderByDescending(p=>p.Value))
            {
                var row=Theme.Text("● "+pair.Key+"  "+ModelShareValues.Percent(pair.Value,total).ToString("0.0",CultureInfo.InvariantCulture)+"%",11,ModelColors.For(pair.Key));row.TextWrapping=TextWrapping.Wrap;row.Margin=new Thickness(0,3,0,0);detailRows.Children.Add(row);
            }
            detail.Visibility=Visibility.Visible;
        }
        internal void ClearSelection(){selectedDate=null;chart.ClearPointer();detail.Visibility=Visibility.Collapsed;detailRows.Children.Clear();}
        private void OnKeyDown(object sender,KeyEventArgs e){if(e.Key==Key.Escape){ClearSelection();e.Handled=true;}}
        private void SizeLegend(Button button)
        {Theme.FitChip(button,ActualWidth);}
        private void OnSizeChanged(object sender,SizeChangedEventArgs e)
        {
            bool stacked=ActualWidth<520;Grid.SetRow(headerActions,stacked?1:0);Grid.SetColumn(headerActions,stacked?0:1);Grid.SetColumnSpan(headerActions,stacked?2:1);headerActions.Margin=stacked?new Thickness(0,5,0,0):new Thickness(0);
            chart.Height=ActualWidth<420?270:350;foreach(Button button in legend.Children.OfType<Button>())SizeLegend(button);
        }
        private void OnLoaded(object sender,RoutedEventArgs e){if(disposed)return;DetachOwner();owner=Window.GetWindow(this);ownerMinimized=owner!=null&&owner.WindowState==WindowState.Minimized;if(owner!=null)owner.StateChanged+=OwnerStateChanged;Refresh();}
        private void OnUnloaded(object sender,RoutedEventArgs e){Suspend();DetachOwner();}
        private void OnVisibleChanged(object sender,DependencyPropertyChangedEventArgs e){if(IsVisible)Refresh();else Suspend();}
        private void OwnerStateChanged(object sender,EventArgs e){bool minimized=owner!=null&&owner.WindowState==WindowState.Minimized;if(minimized==ownerMinimized)return;ownerMinimized=minimized;if(minimized)Suspend();else if(Active)Refresh();}
        private void Suspend(){generation++;pending=false;if(cancellation!=null)cancellation.Cancel();chart.ClearPointer();viewSignature=null;}
        private void DetachOwner(){if(owner!=null)owner.StateChanged-=OwnerStateChanged;owner=null;}
        public void Dispose()
        {
            if(disposed)return;disposed=true;Suspend();DetachOwner();Loaded-=OnLoaded;Unloaded-=OnUnloaded;IsVisibleChanged-=OnVisibleChanged;SizeChanged-=OnSizeChanged;PreviewKeyDown-=OnKeyDown;chart.Pick-=Pick;
            snapshot=customSnapshot=null;ClearSelection();legend.Children.Clear();chart.SetData(null,0,false,0,false,"daily",false,true);
        }
    }
}
