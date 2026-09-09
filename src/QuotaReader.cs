using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace CodexUserData
{
    internal sealed class QuotaWindow
    {
        public double UsedPercent {get;set;} public long Minutes {get;set;} public long ResetsAt {get;set;}
        internal string Remaining(long now){return ResetsAt>0&&now>=ResetsAt?"待更新":Math.Max(0,Math.Min(100,100-UsedPercent)).ToString("0.#",CultureInfo.InvariantCulture)+"%";}
        internal string Label {get{return Minutes>=1440?(Minutes/1440.0).ToString("0.#")+"天":Minutes>=60?(Minutes/60.0).ToString("0.#")+"小时":Minutes+"分钟";}}
    }
    internal sealed class QuotaBucket
    {
        public string Id {get;set;} public string Name {get;set;} public string Origin {get;set;}
        public long ObservedAt {get;set;} public QuotaWindow Primary {get;set;} public QuotaWindow Secondary {get;set;}
    }
    internal static class QuotaReader
    {
        private static readonly Lazy<string> defaultExecutable=new Lazy<string>(FindDefaultExe);
        internal static string DefaultExe {get{return defaultExecutable.Value;}}
        internal static string FindDefaultExe()
        {
            // Resolve the current user's installation. Do not launch shell shims or copy any credentials.
            var roots=new List<string>((Environment.GetEnvironmentVariable("PATH")??"").Split(Path.PathSeparator));
            foreach(var scope in new[]{EnvironmentVariableTarget.User,EnvironmentVariableTarget.Machine})roots.AddRange((Environment.GetEnvironmentVariable("PATH",scope)??"").Split(Path.PathSeparator));
            roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"npm"));
            roots.Add(Environment.GetEnvironmentVariable("npm_config_prefix"));
            // Only read the npm prefix value; never execute npm scripts or inspect auth settings.
            string user=Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            try{string file=Path.Combine(user,".npmrc");if(File.Exists(file))foreach(string line in File.ReadLines(file)){if(line.TrimStart().StartsWith("prefix=",StringComparison.OrdinalIgnoreCase))roots.Add(Environment.ExpandEnvironmentVariables(line.Substring(line.IndexOf('=')+1).Trim().Trim('"')));}}catch(IOException){}catch(UnauthorizedAccessException){}
            roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"nodejs"));
            foreach(string value in roots.Where(v=>!String.IsNullOrWhiteSpace(v)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    string directory=value.Trim().Trim('"');
                    foreach(string relative in new[]{"codex.exe",@"node_modules\@openai\codex\node_modules\@openai\codex-win32-x64\vendor\x86_64-pc-windows-msvc\bin\codex.exe",@"node_modules\@openai\codex\vendor\x86_64-pc-windows-msvc\bin\codex.exe"})
                    {
                        string path=Path.Combine(directory,relative);if(File.Exists(path))return Path.GetFullPath(path);
                    }
                }
                catch(ArgumentException){}catch(NotSupportedException){}catch(PathTooLongException){}
            }
            // Bounded extension/package discovery. Never crawl whole drives on startup.
            foreach(string extensions in new[]{Path.Combine(user,".vscode","extensions"),Path.Combine(user,".vscode-insiders","extensions"),Path.Combine(user,".cursor","extensions")})
            {
                try{if(Directory.Exists(extensions))foreach(string directory in Directory.GetDirectories(extensions,"openai.chatgpt-*").OrderByDescending(v=>Directory.GetLastWriteTimeUtc(v)).Take(12))
                    foreach(string relative in new[]{@"bin\windows-x86_64\codex.exe",@"bin\win32-x64\codex.exe"}){string path=Path.Combine(directory,relative);if(File.Exists(path))return path;}}
                catch(IOException){}catch(UnauthorizedAccessException){}
            }
            string apps=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Microsoft","WindowsApps");
            try{if(Directory.Exists(apps))foreach(string directory in Directory.GetDirectories(apps,"*Codex*").Take(12))foreach(string relative in new[]{@"app\resources\codex.exe",@"resources\codex.exe","codex.exe"}){string path=Path.Combine(directory,relative);if(File.Exists(path))return path;}}catch(IOException){}catch(UnauthorizedAccessException){}
            return ""; // Offline statistics still work; Settings can select a different CLI installation.
        }
        internal static object Get(Dictionary<string,object> d,params string[] keys){object value;foreach(string key in keys)if(d!=null&&d.TryGetValue(key,out value)&&value!=null)return value;return null;}
        private static long Number(Dictionary<string,object> d,params string[] keys){long n;return Int64.TryParse(Convert.ToString(Get(d,keys),CultureInfo.InvariantCulture),out n)?n:0;}
        private static QuotaWindow Window(object value)
        {
            var d=value as Dictionary<string,object>;double used;object raw=Get(d,"usedPercent","used_percent");
            if(raw==null||!Double.TryParse(Convert.ToString(raw,CultureInfo.InvariantCulture),NumberStyles.Float,CultureInfo.InvariantCulture,out used)||Double.IsNaN(used)||Double.IsInfinity(used))return null;
            return new QuotaWindow{UsedPercent=Math.Max(0,Math.Min(100,used)),Minutes=Number(d,"windowDurationMins","window_minutes"),ResetsAt=Number(d,"resetsAt","resets_at")};
        }
        internal static QuotaBucket Parse(object value,long time,string origin)
        {
            var d=value as Dictionary<string,object>;if(d==null)return null;
            var p=Window(Get(d,"primary"));var s=Window(Get(d,"secondary"));if(p==null&&s==null)return null;
            string id=Get(d,"limitId","limit_id") as string;
            return new QuotaBucket{Id=id??"codex",Name=(Get(d,"limitName","limit_name") as string)??(id??"Codex"),Primary=p,Secondary=s,ObservedAt=time,Origin=origin};
        }
        internal static List<QuotaBucket> ParseResult(object value,long time)
        {
            var d=value as Dictionary<string,object>;var many=Get(d,"rateLimitsByLimitId") as Dictionary<string,object>;var list=new List<QuotaBucket>();
            if(many!=null)foreach(var pair in many){var b=Parse(pair.Value,time,"在线查询");if(b!=null){b.Id=pair.Key;list.Add(b);}}
            if(list.Count==0){var b=Parse(Get(d,"rateLimits"),time,"在线查询");if(b!=null)list.Add(b);}return list;
        }
        internal static async Task<List<QuotaBucket>> Query(string executable,string home)
        {
            if(!File.Exists(executable))throw new FileNotFoundException("未找到 Codex CLI，当前显示最近日志中的额度快照。");
            var json=new JavaScriptSerializer{MaxJsonLength=1024*1024};
            var start=new ProcessStartInfo(executable,"app-server --stdio"){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,WorkingDirectory=Path.GetDirectoryName(executable)};
            start.EnvironmentVariables["CODEX_HOME"]=home;
            // Only initialize and read the quota endpoint. No thread/turn, model generation, login or reset call.
            using(var process=new Process{StartInfo=start})
            {
                process.Start();process.ErrorDataReceived+=delegate{};process.BeginErrorReadLine();
                try
                {
                    await process.StandardInput.WriteLineAsync("{\"id\":1,\"method\":\"initialize\",\"params\":{\"clientInfo\":{\"name\":\"ccswitch_usage_widget\",\"version\":\"4.0\"}}}");
                    DateTime deadline=DateTime.UtcNow.AddSeconds(15);bool requested=false;
                    while(DateTime.UtcNow<deadline)
                    {
                        var read=process.StandardOutput.ReadLineAsync();var timeout=Task.Delay(Math.Max(1,(int)(deadline-DateTime.UtcNow).TotalMilliseconds));
                        if(await Task.WhenAny(read,timeout)!=read)break;string line=await read;if(line==null)break;if(line.Length>1024*1024)continue;
                        Dictionary<string,object> reply;try{reply=json.DeserializeObject(line) as Dictionary<string,object>;}catch(ArgumentException){continue;}
                        object id=Get(reply,"id");if(id==null)continue;
                        if(Convert.ToString(id)=="1"&&!requested)
                        {
                            if(Get(reply,"error")!=null)throw new InvalidOperationException("Codex 额度连接未初始化成功");
                            await process.StandardInput.WriteLineAsync("{\"method\":\"initialized\",\"params\":{}}");await process.StandardInput.WriteLineAsync("{\"id\":2,\"method\":\"account/rateLimits/read\"}");requested=true;
                        }
                        else if(Convert.ToString(id)=="2")
                        {
                            if(Get(reply,"error")!=null)throw new InvalidOperationException("在线额度暂不可用，请确认 Codex CLI 的 ChatGPT 登录状态");
                            var quotas=ParseResult(Get(reply,"result"),LocalCodexUsage.Unix(DateTime.Now));if(quotas.Count==0)throw new InvalidOperationException("账号未返回可用的 Codex 额度窗口");return quotas;
                        }
                    }
                    throw new TimeoutException("额度查询超时，保留最近快照");
                }
                finally
                {
                    try{process.StandardInput.Close();}catch(IOException){}
                    // This is only the short-lived helper we started, never another Codex process.
                    if(!process.WaitForExit(500)){try{process.Kill();process.WaitForExit(500);}catch(InvalidOperationException){}}
                }
            }
        }
    }
}
