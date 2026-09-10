using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace CodexUserData
{
    internal sealed class ReleaseInfo
    {
        internal Version Version;
        internal string Tag,Notes;
        internal Uri Page;
        internal string Describe(Version current)
        {
            var local=new Version(current.Major,current.Minor,Math.Max(0,current.Build),Math.Max(0,current.Revision));
            int order=local.CompareTo(Version);
            return order<0?"发现新版本 "+Tag:order>0?"本地版本 v"+current.ToString(3)+" 比最新公开版本 "+Tag+" 更新。":"当前已是最新公开版本 "+Tag+"。";
        }
    }
    internal static class ReleaseUpdate
    {
        internal const string Endpoint="https://api.github.com/repos/iPretenderrr/CodexUserData/releases/latest";
        internal const int MaxResponseBytes=512*1024;
        private const string ReleasePath="/iPretenderrr/CodexUserData/releases/tag/";
        internal static async Task<ReleaseInfo> CheckAsync(CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            var request=(HttpWebRequest)WebRequest.Create(Endpoint);request.Method="GET";
            request.UserAgent="CodexUserData/"+typeof(ReleaseUpdate).Assembly.GetName().Version.ToString(3);request.Accept="application/vnd.github+json";
            request.AllowAutoRedirect=false;request.AutomaticDecompression=DecompressionMethods.GZip|DecompressionMethods.Deflate;
            request.Timeout=10000;request.ReadWriteTimeout=10000;
            using(var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            {
                deadline.CancelAfter(10000);
                // HttpWebRequest.Timeout does not bound GetResponseAsync. Aborting the request
                // also interrupts a stalled response stream when the dialog closes or times out.
                using(deadline.Token.Register(request.Abort))
                try
                {
                    using(var response=(HttpWebResponse)await request.GetResponseAsync().ConfigureAwait(false))
                    {
                        if(response.StatusCode!=HttpStatusCode.OK)throw new InvalidDataException("无法读取公开版本信息。");
                        if(response.ContentLength>MaxResponseBytes)throw new InvalidDataException("版本响应过大。");
                        using(var stream=response.GetResponseStream())return Parse(await ReadResponseAsync(stream,deadline.Token).ConfigureAwait(false));
                    }
                }
                catch(Exception)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if(deadline.IsCancellationRequested)throw new TimeoutException("检查更新超时，请稍后重试。");
                    throw;
                }
            }
        }
        internal static async Task<string> ReadResponseAsync(Stream stream,CancellationToken cancellation)
        {
            var buffer=new byte[8192];using(var content=new MemoryStream())
            {
                while(true)
                {
                    cancellation.ThrowIfCancellationRequested();int count=await stream.ReadAsync(buffer,0,buffer.Length,cancellation).ConfigureAwait(false);
                    if(count==0)break;if(content.Length+count>MaxResponseBytes)throw new InvalidDataException("版本响应过大。");content.Write(buffer,0,count);
                }
                cancellation.ThrowIfCancellationRequested();return new UTF8Encoding(false,true).GetString(content.ToArray());
            }
        }
        internal static ReleaseInfo Parse(string json)
        {
            if(json==null||Encoding.UTF8.GetByteCount(json)>MaxResponseBytes)throw new InvalidDataException("版本响应无效。");
            var data=new JavaScriptSerializer{MaxJsonLength=MaxResponseBytes,RecursionLimit=24}.Deserialize<Dictionary<string,object>>(json);object raw;
            if(data==null)throw new InvalidDataException("版本响应无效。");
            foreach(string field in new[]{"draft","prerelease"})if(data.TryGetValue(field,out raw)&&(!(raw is bool)||(bool)raw))throw new InvalidDataException("该版本不是公开稳定版本。");
            string tag=data.TryGetValue("tag_name",out raw)?raw as string:null;Version version;
            if(tag==null||!Regex.IsMatch(tag,@"\Av?(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(\.(0|[1-9][0-9]*))?\z",RegexOptions.IgnoreCase)||!System.Version.TryParse(tag.TrimStart('v','V'),out version))throw new InvalidDataException("无法识别公开版本号。");
            version=new Version(version.Major,version.Minor,version.Build,Math.Max(0,version.Revision));
            string url=data.TryGetValue("html_url",out raw)?raw as string:null;Uri page;
            if(!Uri.TryCreate(url,UriKind.Absolute,out page)||page.Scheme!="https"||!String.Equals(page.Host,"github.com",StringComparison.OrdinalIgnoreCase)||!page.IsDefaultPort||page.UserInfo.Length!=0||page.Query.Length!=0||page.Fragment.Length!=0||!page.AbsolutePath.StartsWith(ReleasePath,StringComparison.OrdinalIgnoreCase)||!String.Equals(Uri.UnescapeDataString(page.AbsolutePath.Substring(ReleasePath.Length)),tag,StringComparison.Ordinal))throw new InvalidDataException("版本页面链接不属于本项目。");
            string notes=data.TryGetValue("body",out raw)?raw as string:null;
            if(String.IsNullOrWhiteSpace(notes))notes="该版本尚未提供更新说明。";
            // Notes are plain text only. No HTML/Markdown renderer or link activation is used.
            if(notes.Length>32000)notes=notes.Substring(0,32000)+"\n\n说明较长，完整内容可在版本页面查看。";
            return new ReleaseInfo{Version=version,Tag=tag,Notes=notes,Page=page};
        }
    }
}
