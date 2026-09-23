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

namespace CodexUserData
{
    internal sealed class PriceCatalogDocument
    {
        public int schema {get;set;}
        public long revision {get;set;}
        public string checkedOn {get;set;}
        public string basis {get;set;}
        public string source {get;set;}
        public Dictionary<string,decimal[]> models {get;set;}
    }

    // The client never scrapes OpenAI's pricing page. A small, schema-checked catalog in
    // this repository is reviewed against the official page and cached as the last known
    // good value. Network failure therefore cannot block startup or erase working prices.
    internal static class PriceCatalog
    {
        internal const string Endpoint="https://raw.githubusercontent.com/iPretenderrr/CodexUserData/main/model-prices.json";
        internal const int MaxResponseBytes=64*1024;
        private static readonly TimeSpan RefreshInterval=TimeSpan.FromHours(24);
        private static readonly Regex ModelId=new Regex(@"\A[A-Za-z0-9][A-Za-z0-9._-]{0,79}\z",RegexOptions.CultureInvariant);
        private static string CachePath {get{return Path.Combine(Program.DataFolder,"api-prices-cache-v1.json");}}

        internal static void LoadCached()
        {
            try
            {
                if(!File.Exists(CachePath))return;
                var info=new FileInfo(CachePath);if(info.Length<=0||info.Length>MaxResponseBytes)return;
                PriceCatalogDocument document=Parse(File.ReadAllText(CachePath,Encoding.UTF8));
                ApiPrices.ConfigureCatalog(document.models,document.revision,document.checkedOn);
            }
            catch(Exception){}
        }

        internal static async Task<bool> RefreshAsync(CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                if(File.Exists(CachePath)&&DateTime.UtcNow-File.GetLastWriteTimeUtc(CachePath)<RefreshInterval)return false;
                var request=(HttpWebRequest)WebRequest.Create(Endpoint);request.Method="GET";
                request.UserAgent="CodexUserData/"+typeof(PriceCatalog).Assembly.GetName().Version.ToString(3);
                request.Accept="application/json";request.AllowAutoRedirect=false;
                request.AutomaticDecompression=DecompressionMethods.GZip|DecompressionMethods.Deflate;
                request.Timeout=10000;request.ReadWriteTimeout=10000;
                using(var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellation))
                {
                    deadline.CancelAfter(10000);using(deadline.Token.Register(request.Abort))
                    using(var response=(HttpWebResponse)await request.GetResponseAsync().ConfigureAwait(false))
                    {
                        if(response.StatusCode!=HttpStatusCode.OK)throw new InvalidDataException("价格清单响应无效。");
                        if(response.ContentLength>MaxResponseBytes)throw new InvalidDataException("价格清单响应过大。");
                        string json;using(var stream=response.GetResponseStream())json=await ReadResponseAsync(stream,deadline.Token).ConfigureAwait(false);
                        var document=Parse(json);bool changed=ApiPrices.ConfigureCatalog(document.models,document.revision,document.checkedOn);
                        Save(json);return changed;
                    }
                }
            }
            catch(OperationCanceledException){cancellation.ThrowIfCancellationRequested();return false;}
            catch(Exception){return false;}
        }

        internal static async Task<string> ReadResponseAsync(Stream stream,CancellationToken cancellation)
        {
            var buffer=new byte[8192];using(var content=new MemoryStream())
            {
                while(true)
                {
                    cancellation.ThrowIfCancellationRequested();int count=await stream.ReadAsync(buffer,0,buffer.Length,cancellation).ConfigureAwait(false);
                    if(count==0)break;if(content.Length+count>MaxResponseBytes)throw new InvalidDataException("价格清单响应过大。");content.Write(buffer,0,count);
                }
                cancellation.ThrowIfCancellationRequested();return new UTF8Encoding(false,true).GetString(content.ToArray());
            }
        }

        internal static PriceCatalogDocument Parse(string json)
        {
            if(String.IsNullOrWhiteSpace(json)||Encoding.UTF8.GetByteCount(json)>MaxResponseBytes)throw new InvalidDataException("价格清单无效。");
            var serializer=new JavaScriptSerializer{MaxJsonLength=MaxResponseBytes,RecursionLimit=12};
            PriceCatalogDocument document;try{document=serializer.Deserialize<PriceCatalogDocument>(json);}catch(Exception ex){throw new InvalidDataException("价格清单 JSON 无效。",ex);}
            DateTime checkedOn;
            if(document==null||document.schema!=1||document.revision<=0||document.basis!="standard-short"||
               document.source!="https://developers.openai.com/api/docs/pricing"||
               !DateTime.TryParseExact(document.checkedOn,"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out checkedOn)||
               document.models==null||document.models.Count==0||document.models.Count>256)
                throw new InvalidDataException("价格清单字段无效。");
            var clean=new Dictionary<string,decimal[]>(StringComparer.OrdinalIgnoreCase);
            foreach(var pair in document.models)
            {
                if(!ModelId.IsMatch(pair.Key??"")||pair.Value==null||pair.Value.Length!=4||pair.Value.Any(v=>v<0||v>1000000))throw new InvalidDataException("价格清单包含无效模型或金额。");
                if(clean.ContainsKey(pair.Key))throw new InvalidDataException("价格清单包含重复模型。");
                clean[pair.Key]=(decimal[])pair.Value.Clone();
            }
            document.models=clean;return document;
        }

        private static void Save(string json)
        {
            Directory.CreateDirectory(Program.DataFolder);string temporary=CachePath+"."+Guid.NewGuid().ToString("N")+".tmp";
            try
            {
                File.WriteAllText(temporary,json,new UTF8Encoding(false));
                if(File.Exists(CachePath))File.Replace(temporary,CachePath,null);else File.Move(temporary,CachePath);
            }
            finally{try{if(File.Exists(temporary))File.Delete(temporary);}catch(Exception){}}
        }
    }
}
