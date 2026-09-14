using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace CodexUserData
{
    internal sealed class RemoteOptions
    {
        public bool Enabled {get;set;}
        public string Host {get;set;} public int Port {get;set;}
        public string User {get;set;} public string Directory {get;set;}
        public string KeyFile {get;set;} public string Secret {get;set;}
        public string Fingerprint {get;set;}
        public RemoteOptions(){Host=User=KeyFile=Secret=Fingerprint="";Directory=".codex";Port=22;}
        internal string Identity {get{return Hash(Host.Trim().ToLowerInvariant()+":"+Port+"\n"+User+"\n"+Directory+"\n"+Fingerprint);}}
        internal string Configuration {get{return Identity+Hash(KeyFile+"\n"+Secret);}}
        internal static string Hash(string text){using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text??""))).Replace("-","").ToLowerInvariant();}
        internal static string Protect(string text){return String.IsNullOrEmpty(text)?"":Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(text),null,DataProtectionScope.CurrentUser));}
        internal string Unlock(){return String.IsNullOrEmpty(Secret)?"":Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(Secret),null,DataProtectionScope.CurrentUser));}
        internal void Validate(){if(String.IsNullOrWhiteSpace(Host)||String.IsNullOrWhiteSpace(User)||Port<1||Port>65535)throw new ArgumentException("请填写服务器、用户名和有效端口。");if(!String.IsNullOrEmpty(KeyFile)&&!File.Exists(KeyFile))throw new ArgumentException("找不到选择的私钥文件。");}
    }
    internal sealed class LogFile
    {
        internal string Path;internal long Length,Modified,Created;internal bool Directory,Live;
        internal static string SessionId(string path)
        {
            string name=path.Replace('\\','/').Split('/').Last();
            var match=System.Text.RegularExpressions.Regex.Match(name,@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");
            return match.Success?match.Value.ToLowerInvariant():RemoteOptions.Hash(path);
        }
    }
    // A small read-only boundary also permits deterministic tests without a real account.
    internal interface IRemoteFiles : IDisposable
    {
        string Root {get;}
        void Connect();
        IEnumerable<LogFile> List(string path);
        LogFile Stat(string path);
        Stream Open(string path);
    }
    internal sealed class HostTrustException : IOException
    {
        internal readonly string Fingerprint;
        internal HostTrustException(string value):base("服务器指纹尚未信任或已变化，请在设置中测试连接并核对指纹。 "){Fingerprint=value;}
    }
    internal sealed class SftpFiles : IRemoteFiles
    {
        private readonly RemoteOptions options;private SftpClient client;private PrivateKeyFile key;
        public string Root {get;private set;}
        internal SftpFiles(RemoteOptions value){options=value;}
        public void Connect()
        {
            options.Validate();string secret=options.Unlock();AuthenticationMethod authentication;
            if(String.IsNullOrEmpty(options.KeyFile))authentication=new PasswordAuthenticationMethod(options.User,secret);
            else{key=String.IsNullOrEmpty(secret)?new PrivateKeyFile(options.KeyFile):new PrivateKeyFile(options.KeyFile,secret);authentication=new PrivateKeyAuthenticationMethod(options.User,key);}
            var info=new ConnectionInfo(options.Host,options.Port,options.User,authentication){Timeout=TimeSpan.FromSeconds(8)};
            client=new SftpClient(info){OperationTimeout=TimeSpan.FromSeconds(8),KeepAliveInterval=TimeSpan.FromSeconds(20),BufferSize=65536};
            string rejected=null;
            // Host identity is verified during key exchange, before user authentication.
            client.HostKeyReceived+=(s,e)=>{using(var sha=SHA256.Create()){string actual="SHA256:"+Convert.ToBase64String(sha.ComputeHash(e.HostKey)).TrimEnd('=');e.CanTrust=actual==options.Fingerprint;if(!e.CanTrust)rejected=actual;}};
            try{client.Connect();}catch{if(rejected!=null)throw new HostTrustException(rejected);throw;}
            string requested=String.IsNullOrWhiteSpace(options.Directory)?".codex":options.Directory.Trim();
            if(requested=="~")requested=".";else if(requested.StartsWith("~/",StringComparison.Ordinal))requested=requested.Substring(2);
            client.ChangeDirectory(requested);Root=client.WorkingDirectory.TrimEnd('/');if(Root.Length==0)Root="/";
        }
        public IEnumerable<LogFile> List(string path)
        {
            foreach(var file in client.ListDirectory(path))
            {
                if(file.Name=="."||file.Name==".."||file.IsSymbolicLink)continue;
                if(file.IsDirectory||file.IsRegularFile&&file.Name.StartsWith("rollout-",StringComparison.Ordinal)&&file.Name.EndsWith(".jsonl",StringComparison.Ordinal))
                    yield return new LogFile{Path=file.FullName,Length=file.Length,Modified=file.LastWriteTimeUtc.Ticks,Directory=file.IsDirectory};
            }
        }
        public LogFile Stat(string path){var a=client.GetAttributes(path);if(a.IsSymbolicLink||!a.IsRegularFile)throw new IOException("日志不是普通文件。");return new LogFile{Path=path,Length=a.Size,Modified=a.LastWriteTimeUtc.Ticks};}
        public Stream Open(string path){return client.OpenRead(path);}
        public void Dispose(){if(client!=null)client.Dispose();if(key!=null)key.Dispose();client=null;key=null;}
        internal static string Error(Exception ex)
        {
            if(ex is HostTrustException)return ex.Message;
            if(ex is SshAuthenticationException)return "SSH 认证失败，请检查用户名、密码或私钥。";
            if(ex is CryptographicException||ex is FormatException)return "无法解密凭据，请重新填写密码或私钥口令。";
            if(ex is SftpPathNotFoundException||ex is DirectoryNotFoundException)return "未找到远程日志目录，请检查 Codex 数据目录。";
            if(ex is SftpPermissionDeniedException||ex is UnauthorizedAccessException)return "没有读取远程日志的权限。";
            if(ex is ArgumentException)return "远程连接配置不完整或格式不正确。";
            return "远程连接中断或读取失败，将自动重试；本地统计继续更新。";
        }
        internal static string Test(RemoteOptions options)
        {
            using(var files=new SftpFiles(options))
            {
                files.Connect();int roots=0;var queue=new Queue<string>();
                foreach(var file in files.List(files.Root))if(file.Directory&&(file.Path.EndsWith("/sessions",StringComparison.Ordinal)||file.Path.EndsWith("/archived_sessions",StringComparison.Ordinal))){queue.Enqueue(file.Path);roots++;}
                if(roots==0)throw new DirectoryNotFoundException();
                int directories=0;bool log=false,usage=false;
                while(queue.Count>0&&directories++<64&&!usage)
                {
                    foreach(var file in files.List(queue.Dequeue()))
                    {
                        if(file.Directory){queue.Enqueue(file.Path);continue;}log=true;
                        using(var stream=files.Open(file.Path)){stream.Position=Math.Max(0,file.Length-256*1024);byte[] bytes=new byte[(int)Math.Min(256*1024,file.Length)];int count=stream.Read(bytes,0,bytes.Length);usage=Encoding.UTF8.GetString(bytes,0,count).Contains("\"token_count\"");}
                        if(usage)break;
                    }
                }
                return "连接成功 · "+files.Root+"\n"+(usage?"已找到用量事件。保存设置后开始同步。":log?"已找到会话日志；抽样未发现用量事件，保存后继续完整扫描。":"目录可读；抽样未发现会话日志，保存后继续完整扫描。");
            }
        }
    }
    internal static class UsageUnion
    {
        private static string EventKey(LocalUsageEvent e){return e.Time.ToString(CultureInfo.InvariantCulture)+"|"+e.Model+"|"+e.Effort+"|"+e.Signature;}
        internal static Dictionary<string,LogCursor> Merge(IEnumerable<LogCursor> records,out int conflicts)
        {
            conflicts=0;var result=new Dictionary<string,LogCursor>(StringComparer.OrdinalIgnoreCase);
            foreach(var group in records.GroupBy(c=>c.Id,StringComparer.OrdinalIgnoreCase))
            {
                var candidates=group.OrderByDescending(c=>c.Meta).ThenByDescending(c=>c.Events.Count).ToList();var combined=LocalCodexUsage.CopyCursor(candidates[0]);
                // Event multiplicities preserve two real calls with equal counters/timestamps.
                var counts=new Dictionary<string,int>();foreach(var e in combined.Events){string k=EventKey(e);counts[k]=counts.ContainsKey(k)?counts[k]+1:1;}
                foreach(var next in candidates.Skip(1))
                {
                    if(next.Meta&&combined.Meta&&(next.Parent!=combined.Parent||next.Started!=combined.Started)){conflicts++;continue;}
                    var seen=new Dictionary<string,int>();
                    foreach(var e in next.Events)
                    {
                        string k=EventKey(e);int occurrence=seen.ContainsKey(k)?seen[k]+1:1;seen[k]=occurrence;int present;counts.TryGetValue(k,out present);
                        if(occurrence>present){combined.Events.Add(e);counts[k]=occurrence;}
                    }
                    combined.Quotas.AddRange(next.Quotas);
                }
                combined.Events=combined.Events.OrderBy(e=>e.Time).ToList();result[group.Key]=combined;
            }
            return result;
        }
        internal static UsageSnapshot Snapshot(IEnumerable<LogCursor> records,string range,DateTime now,string label)
        {
            int conflicts;var merged=Merge(records,out conflicts);var result=LocalCodexUsage.Aggregate(merged,range,now);result.SourceName=label;
            if(conflicts>0){result.CoverageWarnings+=conflicts;result.Warning+="\n部分跨端会话身份冲突，保留较完整记录，其余待核对。";}return result;
        }
    }
}
