using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace CodexUserData
{
    // A single drawing surface keeps both views cheap: no per-model controls,
    // animation clocks, or work while the page is hidden.
    internal sealed class ModelShareChart : FrameworkElement
    {
        internal sealed class ShareCell
        {
            internal Rect Bounds;
            internal string Model;
            internal long Tokens,Total;
            // Tokens stay available to existing share consumers; USD geometry
            // uses decimal amounts directly, including values below one cent.
            internal decimal Value,TotalValue;
            internal DailyUsage Bucket;
            internal int First,Last;
        }
        private sealed class SourceBucket
        {
            internal DailyUsage Bucket;
            internal Dictionary<string,decimal> Parts;
        }
        private sealed class Column
        {
            internal DailyUsage Bucket;
            internal Dictionary<string,decimal> Parts;
            internal decimal Total;
            internal int First,Last;
            internal int FirstCell,CellCount;
            internal string Key;
        }
        private sealed class ModelLayer
        {
            internal string Model;
            internal StreamGeometry Geometry;
        }

        private SourceBucket[] source=new SourceBucket[0];
        private Dictionary<string,decimal> totals=new Dictionary<string,decimal>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string,long> tokenTotals=new Dictionary<string,long>(StringComparer.OrdinalIgnoreCase);
        private string[] modelOrder=new string[0];
        private string mode="bars",signature,selectedKey,selectedModel;
        private bool cumulative,hourly,absolute,cost;
        private decimal axisMaximum=1;
        private readonly List<ShareCell> cells=new List<ShareCell>();
        private readonly List<ModelLayer> layers=new List<ModelLayer>();
        private Column[] columns=new Column[0];
        private Size layoutSize;
        private bool layoutDirty=true;
        private Rect plot;
        private DrawingGroup drawing;
        private int themeRevision=-1,hovered=-1,selected=-1;
        private Point lastPointer=new Point(Double.NaN,Double.NaN);
        private readonly ToolTip tip=new ToolTip{Background=Theme.Surface,Foreground=Theme.Ink,BorderBrush=Theme.Line,Padding=new Thickness(10),MaxWidth=400};
        private readonly Typeface typeface=new Typeface("Segoe UI, Microsoft YaHei UI");

        internal event Action<DailyUsage,string> Pick;
        internal DailyUsage SelectedBucket {get;private set;}
        internal string HighlightedModel {get;private set;}
        internal int GeometryBuilds {get;private set;}
        internal int DrawingBuilds {get;private set;}
        internal Rect PlotBounds {get{EnsureLayout();return plot;}}
        internal ShareCell[] LayoutCells {get{EnsureLayout();return cells.ToArray();}}
        internal int ColumnCount {get{EnsureLayout();return columns.Length;}}
        internal DailyUsage SelectionForLayout(){EnsureLayout();return SelectedBucket;}

        internal ModelShareChart()
        {
            Theme.Watch(this);Height=230;ClipToBounds=true;Focusable=true;Cursor=Cursors.Cross;ToolTip=tip;
            AutomationProperties.SetName(this,"模型 Token 占比图；点击选择模型或时段，方向键浏览，Enter 确认");
            MouseMove+=delegate(object sender,MouseEventArgs e)
            {
                var screen=PointToScreen(e.GetPosition(this));
                // Scrolling may synthesize movement without a pointer changing position.
                if(screen==lastPointer)return;lastPointer=screen;Choose(Hit(e.GetPosition(this)),false);
            };
            MouseLeave+=delegate{ClearHover();};
            MouseLeftButtonDown+=delegate(object sender,MouseButtonEventArgs e)
            {Focus();Choose(Hit(e.GetPosition(this)),true);};
            KeyDown+=OnChartKeyDown;
            IsVisibleChanged+=delegate{if(!IsVisible)ClearHover();};
        }

        protected override AutomationPeer OnCreateAutomationPeer(){return new ChartPeer(this);}
        private sealed class ChartPeer : FrameworkElementAutomationPeer
        {
            internal ChartPeer(ModelShareChart owner):base(owner){}
            protected override string GetClassNameCore(){return "ModelShareChart";}
            protected override AutomationControlType GetAutomationControlTypeCore(){return AutomationControlType.Custom;}
            protected override bool IsControlElementCore(){return true;}
            protected override bool IsContentElementCore(){return true;}
        }

        internal void SetData(DailyUsage[] buckets,Dictionary<string,long> values,string view,bool cumulative,bool hourly)
        {
            var nextTotals=new Dictionary<string,long>(StringComparer.OrdinalIgnoreCase);
            if(values!=null)foreach(var pair in values)if(pair.Value>0)
            {
                string name=String.IsNullOrWhiteSpace(pair.Key)?"unknown":pair.Key;
                long previous;nextTotals.TryGetValue(name,out previous);nextTotals[name]=checked(previous+pair.Value);
            }
            ApplyData(Snapshot(buckets,false),nextTotals.ToDictionary(p=>p.Key,p=>(decimal)p.Value,StringComparer.OrdinalIgnoreCase),nextTotals,view,cumulative,hourly,false,false);
        }

        internal void SetUsageData(DailyUsage[] buckets,DailyUsage[] rawRange,string view,bool cumulative,bool hourly,bool cost)
        {
            var nextTotals=new Dictionary<string,decimal>(StringComparer.OrdinalIgnoreCase);
            var nextTokenTotals=new Dictionary<string,long>(StringComparer.OrdinalIgnoreCase);
            // A cumulative timeline is already summed. Tree areas always come
            // from the raw interval, independently of temporal aggregation.
            foreach(var day in rawRange??new DailyUsage[0])
            {
                var tokens=ModelShareValues.Tokens(day);
                foreach(var part in tokens){long prior;nextTokenTotals.TryGetValue(part.Key,out prior);nextTokenTotals[part.Key]=checked(prior+part.Value);}
                var parts=cost?Amounts(day,true):tokens.ToDictionary(p=>p.Key,p=>(decimal)p.Value,StringComparer.OrdinalIgnoreCase);
                foreach(var part in parts){decimal prior;nextTotals.TryGetValue(part.Key,out prior);nextTotals[part.Key]=checked(prior+part.Value);}
            }
            ApplyData(Snapshot(buckets,cost),nextTotals,nextTokenTotals,view,cumulative,hourly,true,cost);
        }

        private void ApplyData(SourceBucket[] nextSource,Dictionary<string,decimal> nextTotals,Dictionary<string,long> nextTokenTotals,string view,bool cumulative,bool hourly,bool absolute,bool cost)
        {
            string nextMode=view=="treemap"?"treemap":"bars";
            string nextSignature=Signature(nextSource,nextTotals,nextTokenTotals,nextMode,cumulative,hourly,absolute,cost);
            if(signature==nextSignature)return;
            bool sameView=mode==nextMode&&this.cumulative==cumulative&&this.hourly==hourly&&this.absolute==absolute&&this.cost==cost;
            source=nextSource;totals=nextTotals;tokenTotals=nextTokenTotals;mode=nextMode;this.cumulative=cumulative;this.hourly=hourly;this.absolute=absolute;this.cost=cost;signature=nextSignature;
            // A stable order across every column avoids misleading changes in stack order.
            var order=new Dictionary<string,decimal>(totals,StringComparer.OrdinalIgnoreCase);
            foreach(var bucket in source)foreach(var part in bucket.Parts)if(!order.ContainsKey(part.Key))order.Add(part.Key,0);
            modelOrder=order.OrderByDescending(p=>p.Value).ThenBy(p=>p.Key,StringComparer.OrdinalIgnoreCase).Select(p=>p.Key).ToArray();
            if(!sameView){selectedKey=null;selectedModel=null;SelectedBucket=null;}
            else if(mode!="bars"){selectedKey=null;SelectedBucket=null;}
            if(selectedKey!=null)ReconcileSelection(ProjectColumns());
            layoutDirty=true;drawing=null;selected=-1;ClearHover();InvalidateVisual();
            string metric=cost?"模型 API 估算费用（USD）":"模型 Tokens";
            AutomationProperties.SetName(this,metric+(absolute?"用量图":"占比图")+"；点击选择模型或时段，方向键浏览，Enter 确认");
            AutomationProperties.SetHelpText(this,mode=="treemap"?"所选区间的"+metric+"面积占比":absolute?"各时段"+metric+"的实际用量堆叠柱状图；无用量时留空":"各时段模型 Tokens 的 100% 堆叠占比；无用量时留空");
        }

        internal void Highlight(string model)
        {
            if(String.Equals(HighlightedModel,model,StringComparison.OrdinalIgnoreCase))return;
            HighlightedModel=model;AutomationProperties.SetItemStatus(this,model==null?"全部模型":"高亮："+model);InvalidateVisual();
        }

        internal void ClearPointer()
        {
            selected=-1;selectedKey=null;selectedModel=null;SelectedBucket=null;ClearHover();InvalidateVisual();
        }

        internal void ClearHover()
        {
            bool changed=hovered>=0;hovered=-1;tip.IsOpen=false;lastPointer=new Point(Double.NaN,Double.NaN);
            if(changed)InvalidateVisual();
        }

        internal void SelectTile(string model)
        {
            if(mode!="treemap")return;EnsureLayout();ClearHover();
            selected=model==null?-1:cells.FindIndex(c=>String.Equals(c.Model,model,StringComparison.OrdinalIgnoreCase));
            selectedModel=selected<0?null:cells[selected].Model;selectedKey=null;SelectedBucket=null;
            // Ranking clicks select the same tile without emitting Pick again;
            // re-entering the panel callback would toggle the selection back off.
            AutomationProperties.SetItemStatus(this,selected<0?"未选择":CellDescription(cells[selected]));InvalidateVisual();
        }

        private static SourceBucket[] Snapshot(DailyUsage[] buckets,bool cost)
        {
            if(buckets==null)return new SourceBucket[0];
            return buckets.Select(b=>new SourceBucket{Bucket=b,Parts=Amounts(b,cost)}).ToArray();
        }

        private static Dictionary<string,decimal> Amounts(DailyUsage bucket,bool cost)
        {
            if(!cost)return ModelShareValues.Tokens(bucket).ToDictionary(p=>p.Key,p=>(decimal)p.Value,StringComparer.OrdinalIgnoreCase);
            var values=new Dictionary<string,decimal>(StringComparer.OrdinalIgnoreCase);
            foreach(var model in bucket.Models)
            {
                if(model.EquivalentUsd<=0)continue;
                string name=String.IsNullOrWhiteSpace(model.Model)?"unknown":model.Model;
                decimal prior;values.TryGetValue(name,out prior);values[name]=checked(prior+model.EquivalentUsd);
            }
            return values;
        }

        private static void PartSignature(StringBuilder text,Dictionary<string,decimal> parts)
        {
            foreach(var part in parts.OrderBy(p=>p.Key,StringComparer.OrdinalIgnoreCase))
            {text.Append(part.Key.Length).Append(':').Append(part.Key).Append('=').Append(part.Value.ToString(CultureInfo.InvariantCulture)).Append(';');}
        }
        private static string Signature(SourceBucket[] buckets,Dictionary<string,decimal> values,Dictionary<string,long> tokens,string view,bool running,bool hours,bool absolute,bool cost)
        {
            var text=new StringBuilder();text.Append(view).Append('/').Append(running).Append('/').Append(hours).Append('/').Append(absolute).Append('/').Append(cost).Append('|');
            foreach(var item in buckets)
            {
                var b=item.Bucket;string date=b.Date??"",label=b.DisplayLabel??"";
                text.Append(date.Length).Append(':').Append(date).Append(label.Length).Append(':').Append(label).Append('/').Append(b.Tokens).Append('/').Append(b.Input).Append('/').Append(b.Output).Append('/').Append(b.CacheRead).Append('/').Append(b.CacheWrite).Append('/').Append(b.Reasoning).Append('/').Append(b.Requests).Append('/').Append(b.CostUsd.ToString(CultureInfo.InvariantCulture)).Append('|');
                PartSignature(text,item.Parts);text.Append('|');
                // Price updates need to replace the pinned bucket even when
                // token counts and recorded CostUsd are unchanged.
                foreach(var model in b.Models.OrderBy(m=>m.Model,StringComparer.OrdinalIgnoreCase).ThenBy(m=>m.Effort,StringComparer.OrdinalIgnoreCase))
                {
                    string name=model.Model??"",effort=model.Effort??"";
                    text.Append(name.Length).Append(':').Append(name).Append(effort.Length).Append(':').Append(effort).Append('/').Append(model.Tokens).Append('/').Append(model.Input).Append('/').Append(model.Output).Append('/').Append(model.CacheRead).Append('/').Append(model.CacheWrite).Append('/').Append(model.Requests).Append('/').Append(model.EquivalentUsd.ToString(CultureInfo.InvariantCulture)).Append('/').Append(model.UnpricedTokens).Append(';');
                }
                text.Append('|');
            }
            text.Append("totals|");PartSignature(text,values);
            text.Append("tokens|");foreach(var part in tokens.OrderBy(p=>p.Key,StringComparer.OrdinalIgnoreCase))text.Append(part.Key.Length).Append(':').Append(part.Key).Append('=').Append(part.Value).Append(';');
            return text.ToString();
        }

        private Rect ChartPlot(Size size)
        {
            double left=mode=="treemap"?1:Math.Min(absolute?64:43,Math.Max(0,size.Width-2));
            double top=mode=="treemap"?1:12,bottom=mode=="treemap"?1:30;
            return new Rect(left,top,Math.Max(0,size.Width-left-(mode=="treemap"?1:7)),Math.Max(0,size.Height-top-bottom));
        }
        private Column[] ProjectColumns()
        {
            if(mode!="bars")return new Column[0];
            // At most one column per five logical pixels: long histories cannot
            // create thousands of geometries in a narrow viewport.
            int count=Math.Max(1,(int)(ChartPlot(new Size(Math.Max(0,ActualWidth),Math.Max(0,ActualHeight))).Width/5));
            return BuildColumns(source,count,cumulative);
        }

        internal static DailyUsage[] Compress(DailyUsage[] buckets,int maximumColumns,bool running)
        {return BuildColumns(Snapshot(buckets,false),Math.Max(1,maximumColumns),running).Select(c=>c.Bucket).ToArray();}

        private static Column[] BuildColumns(SourceBucket[] buckets,int maximumColumns,bool running)
        {
            int count=Math.Min(buckets.Length,Math.Max(1,maximumColumns));var result=new Column[count];
            for(int i=0;i<count;i++)
            {
                int first=(int)((long)i*buckets.Length/count),last=(int)((long)(i+1)*buckets.Length/count)-1;
                var parts=new Dictionary<string,decimal>(StringComparer.OrdinalIgnoreCase);
                if(running)foreach(var part in buckets[last].Parts)parts.Add(part.Key,part.Value);
                else for(int j=first;j<=last;j++)foreach(var part in buckets[j].Parts)
                {decimal previous;parts.TryGetValue(part.Key,out previous);parts[part.Key]=checked(previous+part.Value);}
                DailyUsage bucket=first==last?buckets[first].Bucket:Combine(buckets,first,last,running);
                string firstDate=buckets[first].Bucket.Date??"",lastDate=buckets[last].Bucket.Date??"";
                string firstLabel=buckets[first].Bucket.DisplayLabel??"",lastLabel=buckets[last].Bucket.DisplayLabel??"";
                string key=firstDate.Length+":"+firstDate+lastDate.Length+":"+lastDate+firstLabel.Length+":"+firstLabel+lastLabel.Length+":"+lastLabel;
                result[i]=new Column{Bucket=bucket,Parts=parts,Total=parts.Values.Sum(),First=first,Last=last,Key=key};
            }
            return result;
        }

        private static DailyUsage Combine(SourceBucket[] buckets,int first,int last,bool running)
        {
            var result=new DailyUsage{Date=buckets[first].Bucket.Date};
            // Cumulative points already include preceding usage. Summing them
            // again would multiply both the amount and the apparent model share.
            int start=running?last:first;var models=new Dictionary<string,ModelUsage>(StringComparer.OrdinalIgnoreCase);
            for(int i=start;i<=last;i++)
            {
                var item=buckets[i].Bucket;
                checked{result.Tokens+=item.Tokens;result.Input+=item.Input;result.Output+=item.Output;result.CacheRead+=item.CacheRead;result.CacheWrite+=item.CacheWrite;result.Reasoning+=item.Reasoning;result.Requests+=item.Requests;result.CostUsd+=item.CostUsd;}
                foreach(var model in item.Models)
                {
                    string name=String.IsNullOrWhiteSpace(model.Model)?"unknown":model.Model;
                    string key=name.Length+":"+name+(model.Effort??"");ModelUsage row;
                    if(!models.TryGetValue(key,out row)){row=new ModelUsage{Model=name,Effort=model.Effort};models.Add(key,row);}
                    checked{row.Tokens+=model.Tokens;row.Input+=model.Input;row.Output+=model.Output;row.CacheRead+=model.CacheRead;row.CacheWrite+=model.CacheWrite;row.Requests+=model.Requests;row.EquivalentUsd+=model.EquivalentUsd;row.UnpricedTokens+=model.UnpricedTokens;}
                }
            }
            result.Models=models.Values.ToList();
            string from=Caption(buckets[first].Bucket),to=Caption(buckets[last].Bucket);
            result.DisplayLabel=running?"累计至 "+(buckets[last].Bucket.Date??to)+"（显示区间 "+(buckets[first].Bucket.Date??from)+" ～ "+(buckets[last].Bucket.Date??to)+"）":from+" ～ "+to;
            return result;
        }

        private void ReconcileSelection(Column[] projected)
        {
            if(selectedKey==null){SelectedBucket=null;return;}
            var current=projected.FirstOrDefault(c=>c.Key==selectedKey&&c.Total>0);
            if(current==null){selectedKey=null;selectedModel=null;SelectedBucket=null;return;}
            SelectedBucket=current.Bucket;
        }

        private void EnsureLayout()
        {
            var size=new Size(Math.Max(0,ActualWidth),Math.Max(0,ActualHeight));
            if(!layoutDirty&&layoutSize==size)return;
            layoutSize=size;layoutDirty=false;drawing=null;GeometryBuilds++;cells.Clear();layers.Clear();plot=ChartPlot(size);
            hovered=-1;selected=-1;tip.IsOpen=false;
            if(mode=="treemap")
            {
                columns=new Column[0];cells.AddRange(Treemap(totals,tokenTotals,plot));
            }
            else
            {
                columns=BuildColumns(source,Math.Max(1,(int)(plot.Width/5)),cumulative);ReconcileSelection(columns);
                axisMaximum=absolute&&columns.Length>0?columns.Max(c=>c.Total):1;
                if(axisMaximum<=0)axisMaximum=1;
                if(plot.Width>0&&plot.Height>0&&columns.Length>0)
                {
                    double pitch=plot.Width/columns.Length,gap=Math.Min(1.3,pitch*.18);
                    for(int i=0;i<columns.Length;i++)
                    {
                        var column=columns[i];column.FirstCell=cells.Count;if(column.Total<=0)continue;
                        double bottom=plot.Bottom;decimal used=0;
                        var tokens=cost?ModelShareValues.Tokens(column.Bucket):null;
                        long tokenTotal=cost?tokens.Values.Sum():checked((long)column.Total);
                        decimal denominator=absolute?axisMaximum:column.Total;
                        foreach(string model in modelOrder)
                        {
                            decimal value;if(!column.Parts.TryGetValue(model,out value)||value<=0)continue;
                            long tokenCount=0;if(cost)tokens.TryGetValue(model,out tokenCount);else tokenCount=checked((long)value);
                            used=checked(used+value);double top=used==denominator?plot.Top:plot.Bottom-plot.Height*((double)used/(double)denominator);
                            cells.Add(new ShareCell{Model=model,Tokens=tokenCount,Total=tokenTotal,Value=value,TotalValue=column.Total,Bucket=column.Bucket,First=column.First,Last=column.Last,Bounds=new Rect(plot.Left+i*pitch+gap/2,top,Math.Max(0,pitch-gap),Math.Max(0,bottom-top))});
                            bottom=top;
                        }
                        column.CellCount=cells.Count-column.FirstCell;
                    }
                }
            }
            // Frozen paths are reused for highlighting; moving the pointer never
            // creates new geometry or reaggregates a timeline.
            foreach(var group in cells.GroupBy(c=>c.Model,StringComparer.OrdinalIgnoreCase))
            {
                var shape=new StreamGeometry();using(var context=shape.Open())foreach(var cell in group)
                {
                    var b=cell.Bounds;if(b.Width<=0||b.Height<=0)continue;
                    context.BeginFigure(b.TopLeft,true,true);context.LineTo(b.TopRight,true,false);context.LineTo(b.BottomRight,true,false);context.LineTo(b.BottomLeft,true,false);
                }
                shape.Freeze();layers.Add(new ModelLayer{Model=group.Key,Geometry=shape});
            }
            if(selectedKey!=null)
            {
                selected=cells.FindIndex(c=>c.Bucket==SelectedBucket&&(selectedModel==null||String.Equals(c.Model,selectedModel,StringComparison.OrdinalIgnoreCase)));
                // Bars pin the whole interval. A model disappearing or becoming
                // unpriced must not remove its still-valid interval outline.
                if(selected<0){selected=cells.FindIndex(c=>c.Bucket==SelectedBucket);if(selected>=0)selectedModel=cells[selected].Model;}
            }
            // A periodic refresh can resize every tile. Keep selection by model,
            // not the previous tile index, and forget models no longer present.
            if(mode=="treemap"&&selectedModel!=null){selected=cells.FindIndex(c=>String.Equals(c.Model,selectedModel,StringComparison.OrdinalIgnoreCase));if(selected<0)selectedModel=null;}
        }

        // Balanced bisection preserves proportional areas and all positive models.
        // Splitting the longer side produces compact rectangles without packages.
        internal static ShareCell[] Treemap(Dictionary<string,long> values,Rect bounds)
        {
            values=values??new Dictionary<string,long>();
            return Treemap(values.ToDictionary(p=>p.Key,p=>(decimal)p.Value,StringComparer.OrdinalIgnoreCase),values,bounds);
        }
        private static ShareCell[] Treemap(Dictionary<string,decimal> values,Dictionary<string,long> tokens,Rect bounds)
        {
            var parts=values.Where(p=>p.Value>0).OrderByDescending(p=>p.Value).ThenBy(p=>p.Key,StringComparer.OrdinalIgnoreCase).ToArray();
            var result=new List<ShareCell>();decimal total=parts.Sum(p=>p.Value);long tokenTotal=tokens.Values.Where(v=>v>0).Sum();
            if(parts.Length>0)Split(parts,0,parts.Length,total,total,tokens,tokenTotal,bounds,result);return result.ToArray();
        }
        private static void Split(KeyValuePair<string,decimal>[] parts,int start,int count,decimal amount,decimal total,Dictionary<string,long> tokens,long tokenTotal,Rect bounds,List<ShareCell> result)
        {
            if(count==1)
            {
                long tokenCount;tokens.TryGetValue(parts[start].Key,out tokenCount);
                result.Add(new ShareCell{Model=parts[start].Key,Tokens=tokenCount,Total=tokenTotal,Value=parts[start].Value,TotalValue=total,Bounds=bounds,First=-1,Last=-1});return;
            }
            decimal prefix=parts[start].Value;int split=1;
            // Find the closest contiguous split to half the group's amount.
            while(split<count-1)
            {
                decimal next=checked(prefix+parts[start+split].Value);
                if(Math.Abs(next-amount/2)>Math.Abs(prefix-amount/2))break;
                prefix=next;split++;
            }
            double ratio=(double)prefix/(double)amount;Rect a,b;
            if(bounds.Width>=bounds.Height)
            {
                double width=bounds.Width*ratio;a=new Rect(bounds.Left,bounds.Top,width,bounds.Height);b=new Rect(bounds.Left+width,bounds.Top,Math.Max(0,bounds.Width-width),bounds.Height);
            }
            else
            {
                double height=bounds.Height*ratio;a=new Rect(bounds.Left,bounds.Top,bounds.Width,height);b=new Rect(bounds.Left,bounds.Top+height,bounds.Width,Math.Max(0,bounds.Height-height));
            }
            Split(parts,start,split,prefix,total,tokens,tokenTotal,a,result);Split(parts,start+split,count-split,amount-prefix,total,tokens,tokenTotal,b,result);
        }

        internal int Hit(Point point)
        {
            EnsureLayout();if(!plot.Contains(point))return -1;
            if(mode=="bars")
            {
                if(columns.Length==0||plot.Width<=0)return -1;
                int column=Math.Min(columns.Length-1,(int)((point.X-plot.Left)/plot.Width*columns.Length));
                var item=columns[column];for(int i=item.FirstCell+item.CellCount-1;i>=item.FirstCell;i--)
                {var bounds=cells[i].Bounds;if(bounds.Width>0&&bounds.Height>0&&bounds.Contains(point))return i;}return -1;
            }
            for(int i=cells.Count-1;i>=0;i--){var bounds=cells[i].Bounds;if(bounds.Width>0&&bounds.Height>0&&bounds.Contains(point))return i;}return -1;
        }
        internal void Choose(int index,bool clicked)
        {
            EnsureLayout();if(index<0||index>=cells.Count){ClearHover();return;}
            if(clicked)
            {
                selected=index;var cell=cells[index];selectedModel=cell.Model;SelectedBucket=cell.Bucket;
                selectedKey=mode=="bars"?columns.First(c=>c.First==cell.First&&c.Last==cell.Last).Key:null;
                AutomationProperties.SetItemStatus(this,CellDescription(cell));
                if(Pick!=null)Pick(cell.Bucket,cell.Model);
            }
            if(hovered!=index){hovered=index;SetTooltip(cells[index]);InvalidateVisual();}
            else if(clicked)InvalidateVisual();
        }

        private void OnChartKeyDown(object sender,KeyEventArgs e)
        {
            EnsureLayout();if(e.Key==Key.Escape){ClearHover();e.Handled=true;return;}
            if(cells.Count==0)return;int current=hovered>=0?hovered:selected>=0?selected:0,next=current;
            if(e.Key==Key.Enter||e.Key==Key.Space){Choose(current,true);e.Handled=true;return;}
            if(mode=="bars"&&(e.Key==Key.Left||e.Key==Key.Right))
            {
                var active=cells[current];int column=Array.FindIndex(columns,c=>c.First==active.First&&c.Last==active.Last),direction=e.Key==Key.Left?-1:1;
                for(int i=column+direction;i>=0&&i<columns.Length;i+=direction)
                {
                    int candidate=cells.FindIndex(c=>c.First==columns[i].First&&c.Last==columns[i].Last&&String.Equals(c.Model,active.Model,StringComparison.OrdinalIgnoreCase));
                    if(candidate<0)candidate=cells.FindIndex(c=>c.First==columns[i].First&&c.Last==columns[i].Last);
                    if(candidate>=0){next=candidate;break;}
                }
            }
            else if(e.Key==Key.Left||e.Key==Key.Up)next=Math.Max(0,current-1);
            else if(e.Key==Key.Right||e.Key==Key.Down)next=Math.Min(cells.Count-1,current+1);
            else if(e.Key==Key.Home)next=0;else if(e.Key==Key.End)next=cells.Count-1;else return;
            // Browsing is only a hover; Enter/Space explicitly activates selection.
            Choose(next,false);e.Handled=true;
        }

        private static string Caption(DailyUsage bucket){return String.IsNullOrEmpty(bucket.DisplayLabel)?bucket.Date??"":bucket.DisplayLabel;}
        private static string ShareText(decimal value,decimal total)
        {double share=total>0?100.0*((double)value/(double)total):0;return share>0&&share<.1?"<0.1%":share.ToString("0.0",CultureInfo.InvariantCulture)+"%";}
        private string AmountText(decimal value,bool compact)
        {
            if(!cost)return (compact?TokenText.Compact(checked((long)value)):TokenText.Exact(checked((long)value)))+" Tokens";
            return (compact?ChartValue.Money(value):"$"+value.ToString("0.############################",CultureInfo.InvariantCulture))+" USD";
        }
        private string CellDescription(ShareCell cell)
        {return (cell.Bucket==null?"所选区间":Caption(cell.Bucket))+"，"+cell.Model+"，"+AmountText(cell.Value,false)+"，"+ShareText(cell.Value,cell.TotalValue);}
        private void SetTooltip(ShareCell cell)
        {
            tip.Content=(cell.Bucket==null?"所选区间":Caption(cell.Bucket))+"\n"+cell.Model+"\n"+AmountText(cell.Value,false)+" · "+ShareText(cell.Value,cell.TotalValue)+(cost?"\n"+TokenText.Exact(cell.Tokens)+" Tokens":"")+"\n合计 "+AmountText(cell.TotalValue,false)+"\n"+(cell.Bucket==null?"点击查看该模型":absolute?"点击固定该时段用量":"点击固定该时段占比");
            tip.PlacementTarget=this;tip.Placement=PlacementMode.Mouse;tip.IsOpen=IsMouseOver;
        }

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);if(ActualWidth<=0||ActualHeight<=0)return;
            dc.DrawRectangle(Brushes.Transparent,null,new Rect(0,0,ActualWidth,ActualHeight));EnsureLayout();
            if(drawing==null||themeRevision!=Theme.Revision)
            {
                themeRevision=Theme.Revision;drawing=new DrawingGroup();using(var context=drawing.Open())DrawBase(context);
                drawing=(DrawingGroup)drawing.GetCurrentValueAsFrozen();DrawingBuilds++;
            }
            dc.DrawDrawing(drawing);
            string active=hovered>=0&&hovered<cells.Count?cells[hovered].Model:HighlightedModel;
            if(active!=null)
            {
                var layer=layers.FirstOrDefault(l=>String.Equals(l.Model,active,StringComparison.OrdinalIgnoreCase));
                if(layer!=null)dc.DrawGeometry(null,new Pen(Theme.Ink,1.15),layer.Geometry);
            }
            if(selected>=0&&selected<cells.Count)DrawPointer(dc,cells[selected],Theme.Accent,1.8,true);
            if(hovered>=0&&hovered<cells.Count)DrawPointer(dc,cells[hovered],Theme.Ink,1.4,false);
        }

        private void DrawPointer(DrawingContext dc,ShareCell cell,Brush brush,double thickness,bool wholeColumn)
        {
            Rect bounds=cell.Bounds;if(mode=="bars"&&wholeColumn)bounds=new Rect(bounds.Left,plot.Top,bounds.Width,plot.Height);
            if(bounds.Width<=0||bounds.Height<=0)return;double inset=Math.Min(thickness/2,Math.Min(bounds.Width,bounds.Height)/2);
            bounds.Inflate(-inset,-inset);dc.DrawRectangle(null,new Pen(brush,thickness),bounds);
        }
        private FormattedText Formatted(string value,double size,Brush brush)
        {return new FormattedText(value,CultureInfo.InvariantCulture,FlowDirection.LeftToRight,typeface,size,brush,VisualTreeHelper.GetDpi(this).PixelsPerDip);}
        private void Text(DrawingContext dc,string value,double x,double y,double size,Brush brush)
        {dc.DrawText(Formatted(value,size,brush),new Point(x,y));}

        private void DrawBase(DrawingContext dc)
        {
            if(mode=="bars")DrawAxes(dc);
            foreach(var layer in layers)dc.DrawGeometry(ModelColors.FillFor(layer.Model),null,layer.Geometry);
            var separator=new Pen(Theme.Line,.55);
            // Neutral seams preserve the model's fill color and do not
            // need their own framework elements or hit-testing objects.
            foreach(var cell in cells)if(cell.Bounds.Width>=.7&&cell.Bounds.Height>=.7)dc.DrawRectangle(null,separator,cell.Bounds);
            if(mode=="treemap")foreach(var cell in cells)DrawTreeLabel(dc,cell);
            if(cells.Count==0)Text(dc,cost?"该范围内暂无估算费用":"该范围内无模型用量",Math.Min(plot.Right,plot.Left+10),plot.Top+15,11,Theme.Muted);
        }
        private void DrawAxes(DrawingContext dc)
        {
            if(plot.Width<=0||plot.Height<=0)return;var grid=new Pen(Theme.Line,.6);
            for(int i=0;i<=4;i++)
            {
                double y=plot.Bottom-plot.Height*i/4;dc.DrawLine(grid,new Point(plot.Left,y),new Point(plot.Right,y));
                string label=absolute?ChartValue.Axis((double)axisMaximum*i/4,cost):(i*25).ToString(CultureInfo.InvariantCulture)+"%";
                var text=Formatted(label,9,Theme.Muted);dc.DrawText(text,new Point(absolute?Math.Max(0,plot.Left-text.Width-8):0,y-6));
            }
            if(columns.Length==0)return;
            int labelCount=Math.Min(columns.Length,Math.Max(1,(int)(plot.Width/(hourly?84:62))));double lastRight=Double.NegativeInfinity;
            for(int i=0;i<labelCount;i++)
            {
                int index=labelCount==1?0:(int)Math.Round((columns.Length-1)*i/(double)(labelCount-1));
                string label=AxisLabel(source[columns[index].First].Bucket.Date);var text=Formatted(label,9,Theme.Muted);
                double center=plot.Left+(index+.5)*plot.Width/columns.Length,x=Math.Max(plot.Left,Math.Min(plot.Right-text.Width,center-text.Width/2));
                if(x<lastRight+7)continue;dc.DrawText(text,new Point(x,plot.Bottom+8));lastRight=x+text.Width;
            }
        }
        private string AxisLabel(string date)
        {
            DateTime parsed;if(DateTime.TryParse(date,CultureInfo.InvariantCulture,DateTimeStyles.None,out parsed))return parsed.ToString(hourly?"MM/dd HH:mm":"MM/dd",CultureInfo.InvariantCulture);
            return String.IsNullOrEmpty(date)?"":date.Length>10?date.Substring(0,10):date;
        }
        private void DrawTreeLabel(DrawingContext dc,ShareCell cell)
        {
            var bounds=cell.Bounds;if(bounds.Width<44||bounds.Height<25)return;
            var brush=ModelColors.FillFor(cell.Model) as SolidColorBrush;Brush ink=brush==null?Brushes.Black:ContrastInk(brush.Color);
            double pad=Math.Min(10,Math.Max(5,bounds.Width*.05));double width=Math.Max(1,bounds.Width-2*pad);
            var name=Formatted(cell.Model,bounds.Width>=140&&bounds.Height>=70?12:10,ink);name.MaxTextWidth=width;name.MaxLineCount=1;name.Trimming=TextTrimming.CharacterEllipsis;
            var caption=Formatted(absolute?AmountText(cell.Value,true):ShareText(cell.Value,cell.TotalValue),bounds.Width>=140&&bounds.Height>=70?19:12,ink);
            caption.MaxTextWidth=width;caption.MaxLineCount=1;caption.Trimming=TextTrimming.CharacterEllipsis;
            var percent=absolute?Formatted(ShareText(cell.Value,cell.TotalValue),10,ink):null;
            bool showAmount=bounds.Height>=name.Height+caption.Height+10;
            bool showPercent=absolute&&showAmount&&bounds.Height>=name.Height+caption.Height+percent.Height+18&&percent.Width<=width;
            double height=name.Height+(showAmount?caption.Height+4:0)+(showPercent?percent.Height+4:0),y=bounds.Top+(bounds.Height-height)/2;
            dc.PushClip(new RectangleGeometry(bounds));dc.DrawText(name,new Point(bounds.Left+pad,y));
            if(showAmount)dc.DrawText(caption,new Point(bounds.Left+pad,y+name.Height+4));
            if(showPercent)dc.DrawText(percent,new Point(bounds.Left+pad,y+name.Height+caption.Height+8));dc.Pop();
        }
        private static Brush ContrastInk(Color color)
        {
            Func<byte,double> linear=v=>{double c=v/255.0;return c<=.04045?c/12.92:Math.Pow((c+.055)/1.055,2.4);};
            double luminance=.2126*linear(color.R)+.7152*linear(color.G)+.0722*linear(color.B);
            // Choose the larger WCAG contrast ratio; never recolor the model fill.
            return (luminance+.05)/.05>=1.05/(luminance+.05)?Brushes.Black:Brushes.White;
        }
    }
}
