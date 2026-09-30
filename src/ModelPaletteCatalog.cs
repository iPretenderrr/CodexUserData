using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Media;

namespace CodexUserData
{
    // Public properties are the on-disk/wire schema and survive obfuscation.
    internal sealed class ModelPaletteDocument
    {
        public int schema {get;set;}
        public long revision {get;set;}
        public string algorithm {get;set;}
        public Dictionary<string,ModelPaletteFamilyDocument> families {get;set;}
    }
    internal sealed class ModelPaletteFamilyDocument
    {
        public string[] dark {get;set;}
        public string[] light {get;set;}
        public ModelPaletteRound[] rounds {get;set;}
        public string[] models {get;set;}
    }
    internal sealed class ModelPaletteRound
    {
        public double[] dark {get;set;}
        public double[] light {get;set;}
    }
    internal static class ModelIdentity
    {
        internal static readonly string[] Families={"astra","sol","terra","luna","gpt5"};
        private static readonly Regex Version=new Regex(@"\Agpt-(\d+)(?:\.(\d+))?(?:\.(\d+))?((?:[-_][a-z0-9][a-z0-9._-]*)?)\z",RegexOptions.CultureInvariant);
        internal static string Canonical(string source)
        {
            if(String.IsNullOrWhiteSpace(source)||source.Length>160||source.Any(Char.IsControl))return "unknown";
            string name=source.Trim().ToLowerInvariant();
            int slash=name.LastIndexOf('/');if(slash>=0&&name.Substring(slash+1).StartsWith("gpt-",StringComparison.Ordinal))name=name.Substring(slash+1);
            if(name=="codexautoreview"||name=="codex_auto_review")return "codex-auto-review";
            var match=Version.Match(name);int major,minor=0,patch=0;
            if(!match.Success||!Int32.TryParse(match.Groups[1].Value,out major)||match.Groups[2].Success&&!Int32.TryParse(match.Groups[2].Value,out minor)||match.Groups[3].Success&&!Int32.TryParse(match.Groups[3].Value,out patch))return name;
            return "gpt-"+major.ToString(CultureInfo.InvariantCulture)+"."+minor.ToString(CultureInfo.InvariantCulture)+(patch==0?"":"."+patch.ToString(CultureInfo.InvariantCulture))+match.Groups[4].Value;
        }
        internal static int Family(string canonical)
        {
            if(canonical=="unknown"||canonical=="codex-auto-review")return -2;
            var parts=canonical.Split(new[]{'-','_','/'},StringSplitOptions.RemoveEmptyEntries);
            for(int i=parts.Length-1;i>=0;i--)for(int f=0;f<4;f++)if(parts[i]==Families[f])return f;
            return canonical.StartsWith("gpt-5.",StringComparison.Ordinal)||canonical.StartsWith("gpt-5-",StringComparison.Ordinal)?4:-1;
        }
        internal static uint Hash(string value){uint hash=2166136261;foreach(char c in value)hash=unchecked((hash^c)*16777619);return hash;}
    }
    internal static class ModelColorMath
    {
        internal static readonly double[] HueStart={200,18,115,328,242},HueSpan={35,40,50,44,52};
        internal static Color Parse(string value)
        {if(value==null||value.Length!=7||value[0]!='#'||value.Skip(1).Any(c=>!Uri.IsHexDigit(c)))throw new InvalidDataException("配色包含无效色号。");return Color.FromRgb(Byte.Parse(value.Substring(1,2),NumberStyles.HexNumber),Byte.Parse(value.Substring(3,2),NumberStyles.HexNumber),Byte.Parse(value.Substring(5,2),NumberStyles.HexNumber));}
        internal static string Hex(Color c){return "#"+c.R.ToString("X2")+c.G.ToString("X2")+c.B.ToString("X2");}
        private static double Linear(double v){return v<=.04045?v/12.92:Math.Pow((v+.055)/1.055,2.4);}
        internal static double Contrast(Color a,Color b)
        {
            double x=.2126*Linear(a.R/255.0)+.7152*Linear(a.G/255.0)+.0722*Linear(a.B/255.0),y=.2126*Linear(b.R/255.0)+.7152*Linear(b.G/255.0)+.0722*Linear(b.B/255.0);
            return (Math.Max(x,y)+.05)/(Math.Min(x,y)+.05);
        }
        internal static double[] Lab(Color c)
        {
            double r=Linear(c.R/255.0),g=Linear(c.G/255.0),b=Linear(c.B/255.0);
            double l=Math.Pow(.4122214708*r+.5363325363*g+.0514459929*b,1.0/3),m=Math.Pow(.2119034982*r+.6806995451*g+.1073969566*b,1.0/3),s=Math.Pow(.0883024619*r+.2817188376*g+.6299787005*b,1.0/3);
            return new[]{.2104542553*l+.793617785*m-.0040720468*s,1.9779984951*l-2.428592205*m+.4505937099*s,.0259040371*l+.7827717662*m-.808675766*s};
        }
        internal static double Distance(double[] a,double[] b)
        {double l=a[0]-b[0],x=a[1]-b[1],y=a[2]-b[2];return Math.Sqrt(l*l+x*x+y*y);}
        internal static double Distance(Color a,Color b){return Distance(Lab(a),Lab(b));}
        private static byte Channel(double v)
        {
            // Reject real gamut overflow instead of clipping different colors to
            // the same edge. The tolerance only absorbs matrix round-off.
            if(Double.IsNaN(v)||v<-.000001||v>1.000001)throw new InvalidDataException("配色偏移超出显示色域。");
            v=Math.Max(0,Math.Min(1,v));return (byte)Math.Round(255*(v<=.0031308?12.92*v:1.055*Math.Pow(v,1/2.4)-.055));
        }
        internal static Color Shift(Color color,double[] offset)
        {
            if(offset.All(component=>component==0))return color;
            var v=Lab(color);double L=v[0]+offset[0],a=v[1]+offset[1],b=v[2]+offset[2];
            Color result;if(!TryLab(L,a,b,out result))throw new InvalidDataException("配色偏移超出显示色域。");return result;
        }
        internal static bool TryLab(double L,double a,double b,out Color color)
        {
            double l=L+.3963377774*a+.2158037573*b,m=L-.1055613458*a-.0638541728*b,s=L-.0894841775*a-1.291485548*b;
            l=l*l*l;m=m*m*m;s=s*s*s;
            double r=4.0767416621*l-3.3077115913*m+.2309699292*s,g=-1.2684380046*l+2.6097574011*m-.3413193965*s,blue=-.0041960863*l-.7034186147*m+1.707614701*s;
            color=new Color();if(Double.IsNaN(r)||Double.IsNaN(g)||Double.IsNaN(blue)||r<-.000001||r>1.000001||g<-.000001||g>1.000001||blue<-.000001||blue>1.000001)return false;
            color=Color.FromRgb(Channel(r),Channel(g),Channel(blue));return true;
        }
        internal static bool InFamily(Color c,int family)
        {
            double hue=System.Drawing.Color.FromArgb(c.R,c.G,c.B).GetHue();
            double relative=(hue-HueStart[family]+360)%360;return relative<=HueSpan[family]+2||relative>=358;
        }
        internal static Color Hsl(double hue,double saturation,double lightness)
        {
            hue=(hue%360+360)%360;double c=(1-Math.Abs(2*lightness-1))*saturation,x=c*(1-Math.Abs(hue/60%2-1)),m=lightness-c/2,r=0,g=0,b=0;
            if(hue<60){r=c;g=x;}else if(hue<120){r=x;g=c;}else if(hue<180){g=c;b=x;}else if(hue<240){g=x;b=c;}else if(hue<300){r=x;b=c;}else{r=c;b=x;}
            return Color.FromRgb((byte)Math.Round((r+m)*255),(byte)Math.Round((g+m)*255),(byte)Math.Round((b+m)*255));
        }
        internal static Color Fallback(string canonical,bool light)
        {
            int family=ModelIdentity.Family(canonical);if(family==-2)return Parse(light?"#626775":"#ABA9A7");
            uint h=ModelIdentity.Hash(canonical);double hue=family<0?h%360:HueStart[family]+HueSpan[family]*(h%1024)/1023.0;
            double saturation=(light?.64:.68)+((h>>10)%128)/127.0*.22,limit=light?.60:.35;
            var background=Parse(light?"#E7EFF7":"#2B3846");
            while(Contrast(Hsl(hue,saturation,limit),background)<4.55&&limit>.08&&limit<.92)limit+=light?-.01:.01;
            double low=light?.20:limit,high=light?limit:.86;
            return Hsl(hue,saturation,low+(high-low)*((h>>17)%1024)/1023.0);
        }
    }
    internal sealed class ModelPaletteSnapshot
    {
        private readonly ModelPaletteDocument document;
        private readonly Dictionary<string,Color[]> models=new Dictionary<string,Color[]>(StringComparer.Ordinal);
        private readonly Color[][] dark=new Color[5][],light=new Color[5][];
        internal long Revision {get{return document.revision;}}
        internal int RoundCount(int family){return dark[family].Length/10;}
        internal Color At(int family,int index,bool isLight){return (isLight?light:dark)[family][index];}
        internal Color ColorFor(string canonical,bool isLight){Color[] pair;return models.TryGetValue(canonical,out pair)?pair[isLight?1:0]:ModelColorMath.Fallback(canonical,isLight);}
        internal string ToJson(){return ModelPaletteCatalog.Serializer().Serialize(document);}
        internal ModelPaletteSnapshot(ModelPaletteDocument value)
        {
            document=value;
            for(int f=0;f<5;f++)
            {
                var family=value.families[ModelIdentity.Families[f]];int count=family.rounds.Length*10;
                dark[f]=new Color[count];light[f]=new Color[count];
                foreach(bool isLight in new[]{false,true})
                {
                    var colors=(isLight?light:dark)[f];var bases=(isLight?family.light:family.dark).Select(ModelColorMath.Parse).ToArray();
                    for(int i=0;i<count;i++)
                    {
                        var round=family.rounds[i/10];colors[i]=ModelColorMath.Shift(bases[i%10],isLight?round.light:round.dark);
                        if(!ModelColorMath.InFamily(colors[i],f)||ModelColorMath.Contrast(colors[i],ModelColorMath.Parse(isLight?"#E7EFF7":"#2B3846"))<4.5)throw new InvalidDataException("配色不符合色系或可读性要求。");
                    }
                    if(colors.Distinct().Count()!=count)throw new InvalidDataException("配色轮次产生重复色号。");
                    var labs=colors.Select(ModelColorMath.Lab).ToArray();
                    // Checking every pair less than ten indices apart covers all
                    // windows, including all nine crossings at every boundary.
                    for(int i=0;i<count;i++)for(int j=Math.Max(0,i-9);j<i;j++)if(ModelColorMath.Distance(labs[i],labs[j])+1e-9<ModelPaletteCatalog.MinimumDistance)throw new InvalidDataException("连续十个模型的配色间隔不足。");
                }
                for(int i=0;i<family.models.Length;i++)models.Add(family.models[i],new[]{dark[f][i],light[f][i]});
            }
        }
        internal void ValidateExtension(ModelPaletteSnapshot previous)
        {
            if(Revision<previous.Revision)throw new InvalidDataException("不能回退配色配置版本。");
            foreach(string key in ModelIdentity.Families)
            {
                var next=document.families[key];var old=previous.document.families[key];
                if(!next.dark.SequenceEqual(old.dark)||!next.light.SequenceEqual(old.light)||next.models.Length<old.models.Length||!next.models.Take(old.models.Length).SequenceEqual(old.models)||next.rounds.Length<old.rounds.Length)throw new InvalidDataException("配色更新不能改动已有编号或基础颜色。");
                for(int q=0;q<old.rounds.Length;q++)if(!next.rounds[q].dark.SequenceEqual(old.rounds[q].dark)||!next.rounds[q].light.SequenceEqual(old.rounds[q].light))throw new InvalidDataException("配色更新不能改动已有轮次。");
                if(Revision==previous.Revision&&(next.models.Length!=old.models.Length||next.rounds.Length!=old.rounds.Length))throw new InvalidDataException("同一配色版本的内容不能改变。");
            }
        }
    }
    internal static class ModelPaletteCatalog
    {
        internal const string Endpoint="https://raw.githubusercontent.com/iPretenderrr/CodexUserData/main/model-palette.json";
        internal const int MaxBytes=256*1024;
        internal const double MinimumDistance=.05;
        private static readonly SemaphoreSlim RefreshGate=new SemaphoreSlim(1,1);
        private static readonly ModelPaletteSnapshot bundled=Parse(BundledJson());
        private static ModelPaletteSnapshot current=bundled;
        private static DateTime lastAttempt=DateTime.MinValue;
        internal static ModelPaletteSnapshot Snapshot {get{return Volatile.Read(ref current);}}
        internal static string CachePath {get{return Path.Combine(Program.DataFolder,"model-palette-cache-v1.json");}}
        internal static JavaScriptSerializer Serializer(){return new JavaScriptSerializer{MaxJsonLength=MaxBytes,RecursionLimit=16};}
        internal static string BundledJson()
        {
            using(var stream=typeof(ModelPaletteCatalog).Assembly.GetManifestResourceStream("CodexUserData.ModelPalette.json"))
            {if(stream==null)throw new InvalidDataException("程序缺少内置模型配色。");using(var reader=new StreamReader(stream,Encoding.UTF8))return reader.ReadToEnd();}
        }
        internal static ModelPaletteSnapshot Parse(string json)
        {
            if(String.IsNullOrWhiteSpace(json)||Encoding.UTF8.GetByteCount(json)>MaxBytes)throw new InvalidDataException("配色配置大小无效。");
            ModelPaletteDocument d;try{d=Serializer().Deserialize<ModelPaletteDocument>(json);}catch(Exception ex){throw new InvalidDataException("配色 JSON 无效。",ex);}
            if(d==null||d.schema!=1||d.algorithm!="family-cycle-v1"||d.revision<=0||d.families==null||d.families.Count!=5)throw new InvalidDataException("配色配置版本或字段无效。");
            foreach(string key in ModelIdentity.Families)
            {
                ModelPaletteFamilyDocument f;
                if(!d.families.TryGetValue(key,out f)||f==null||f.dark==null||f.light==null||f.dark.Length!=10||f.light.Length!=10||f.rounds==null||f.rounds.Length<1||f.rounds.Length>32||f.models==null||f.models.Length>f.rounds.Length*10)throw new InvalidDataException("配色色位或轮次不完整。");
                f.dark=f.dark.Select(c=>ModelColorMath.Hex(ModelColorMath.Parse(c))).ToArray();f.light=f.light.Select(c=>ModelColorMath.Hex(ModelColorMath.Parse(c))).ToArray();
                foreach(var r in f.rounds)
                {
                    if(r==null)throw new InvalidDataException("配色轮次无效。");
                    foreach(var v in new[]{r.dark,r.light})if(v==null||v.Length!=3||v.Any(x=>Double.IsNaN(x)||Double.IsInfinity(x))||v.Sum(x=>x*x)>.04*.04)throw new InvalidDataException("配色偏移超过允许范围。");
                }
                if(f.rounds[0].dark.Concat(f.rounds[0].light).Any(x=>x!=0))throw new InvalidDataException("第一轮必须使用基础色。");
                var names=new HashSet<string>(StringComparer.Ordinal);
                foreach(string name in f.models)if(String.IsNullOrEmpty(name)||name!=ModelIdentity.Canonical(name)||name.Contains("/")||ModelIdentity.Family(name)!=Array.IndexOf(ModelIdentity.Families,key)||!names.Add(name))throw new InvalidDataException("模型编号包含无效、重复或跨系列名称。");
            }
            return new ModelPaletteSnapshot(d);
        }
        internal static void LoadCached()
        {
            foreach(string path in new[]{CachePath,CachePath+".bak"})try
            {
                if(!File.Exists(path)||new FileInfo(path).Length>MaxBytes)continue;var next=Parse(File.ReadAllText(path,Encoding.UTF8));next.ValidateExtension(bundled);
                if(next.Revision>=Snapshot.Revision)Volatile.Write(ref current,next);return;
            }catch(Exception){}
        }
        internal static bool Install(ModelPaletteSnapshot next)
        {return CommitDownload(next,null,CancellationToken.None);}
        private static bool CommitDownload(ModelPaletteSnapshot next,string temporary,CancellationToken cancellation)
        {
            var dispatcher=Application.Current==null?null:Application.Current.Dispatcher;
            if(dispatcher!=null&&!dispatcher.CheckAccess())throw new InvalidOperationException("配色必须在界面线程应用。");
            cancellation.ThrowIfCancellationRequested();
            var old=Snapshot;next.ValidateExtension(old);if(next.Revision==old.Revision)return false;
            // Resolve everything first. Publication and mutable brush updates run
            // in one dispatcher callback, so WPF never paints half a revision.
            var prepared=ModelColors.Prepare(next);cancellation.ThrowIfCancellationRequested();
            // Only the small atomic rename happens on the UI thread. The file
            // was flushed in the worker; cancellation/stale revisions cannot
            // overwrite the last valid cache while waiting for this callback.
            if(temporary!=null){if(File.Exists(CachePath))File.Replace(temporary,CachePath,CachePath+".bak");else File.Move(temporary,CachePath);}
            Volatile.Write(ref current,next);ModelColors.Commit(prepared);Theme.InvalidateColors();return true;
        }
        private static string Stage(string json)
        {
            Directory.CreateDirectory(Program.DataFolder);string temporary=CachePath+"."+Guid.NewGuid().ToString("N")+".tmp";
            try
            {
                byte[] bytes=new UTF8Encoding(false).GetBytes(json);using(var file=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None)){file.Write(bytes,0,bytes.Length);file.Flush(true);}
                return temporary;
            }catch{try{if(File.Exists(temporary))File.Delete(temporary);}catch(Exception){}throw;}
        }
        internal static async Task<string> ReadResponseAsync(Stream stream,CancellationToken cancellation)
        {
            var buffer=new byte[8192];using(var content=new MemoryStream())
            {
                while(true){int count=await stream.ReadAsync(buffer,0,buffer.Length,cancellation).ConfigureAwait(false);if(count==0)break;if(content.Length+count>MaxBytes)throw new InvalidDataException("配色响应过大。");content.Write(buffer,0,count);}
                cancellation.ThrowIfCancellationRequested();return new UTF8Encoding(false,true).GetString(content.ToArray());
            }
        }
        internal static async Task<bool> RefreshAsync(CancellationToken cancellation,bool force)
        {
            cancellation.ThrowIfCancellationRequested();await RefreshGate.WaitAsync(cancellation).ConfigureAwait(false);
            try
            {
                if(!force&&DateTime.UtcNow-lastAttempt<TimeSpan.FromHours(24))return false;lastAttempt=DateTime.UtcNow;
                var request=(HttpWebRequest)WebRequest.Create(Endpoint);request.Method="GET";request.UserAgent="CodexUserData/"+typeof(ModelPaletteCatalog).Assembly.GetName().Version.ToString(3);request.Accept="application/json";request.AllowAutoRedirect=false;request.AutomaticDecompression=DecompressionMethods.GZip|DecompressionMethods.Deflate;request.Timeout=request.ReadWriteTimeout=10000;
                using(var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellation))
                {
                    deadline.CancelAfter(10000);using(deadline.Token.Register(request.Abort))
                    using(var response=(HttpWebResponse)await request.GetResponseAsync().ConfigureAwait(false))
                    {
                        if(response.StatusCode!=HttpStatusCode.OK||response.ContentLength>MaxBytes)throw new InvalidDataException("无法读取配色配置。");
                        string json;using(var stream=response.GetResponseStream())json=await ReadResponseAsync(stream,deadline.Token).ConfigureAwait(false);
                        var next=Parse(json);next.ValidateExtension(Snapshot);if(next.Revision==Snapshot.Revision)return false;
                        cancellation.ThrowIfCancellationRequested();string temporary=Stage(next.ToJson());
                        try
                        {
                            var app=Application.Current;if(app==null||app.Dispatcher.HasShutdownStarted)return false;
                            return await app.Dispatcher.InvokeAsync(()=>CommitDownload(next,temporary,cancellation));
                        }finally{try{if(File.Exists(temporary))File.Delete(temporary);}catch(Exception){}}
                    }
                }
            }
            catch(Exception){cancellation.ThrowIfCancellationRequested();return false;}
            finally{RefreshGate.Release();}
        }
    }
}
