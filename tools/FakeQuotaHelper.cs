using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace CodexUserData
{
    // Standalone test fixture compiled by verify-source.ps1. It never invokes Codex, reads an
    // account, or uses the network. A marker limits writes to the directory created by the probe.
    internal static class FakeQuotaHelper
    {
        public static int Main(string[] args)
        {
            string root=Environment.GetEnvironmentVariable("CODEX_HOME");
            if(args.Length!=2||args[0]!="app-server"||args[1]!="--stdio"||String.IsNullOrEmpty(root)||!File.Exists(Path.Combine(root,"fake-quota.fixture")))return 2;
            if(Console.ReadLine()==null)return 3;
            Console.WriteLine("{\"id\":1,\"result\":{}}");Console.Out.Flush();
            if(Console.ReadLine()==null||Console.ReadLine()==null)return 4;
            File.WriteAllText(Path.Combine(root,"helper.pid"),Process.GetCurrentProcess().Id.ToString());
            File.WriteAllText(Path.Combine(root,"helper.ready"),"waiting");
            if(File.Exists(Path.Combine(root,"fake-quota.reply")))
            {
                Console.WriteLine("{\"id\":2,\"result\":{\"rateLimits\":{\"limitId\":\"codex\",\"primary\":{\"usedPercent\":24,\"windowDurationMins\":300,\"resetsAt\":"+(DateTimeOffset.UtcNow.ToUnixTimeSeconds()+3600)+"}}}}");
                Console.Out.Flush();return 0;
            }
            // A finite fallback prevents an orphan even if the probe process itself is killed.
            Thread.Sleep(30000);return 0;
        }
    }
}
