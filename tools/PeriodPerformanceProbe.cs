using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace CodexUserData
{
    // Synthetic quota files only. This probe never opens the user's Codex directory.
    internal static class PeriodPerformanceProbe
    {
        public static int Main(string[] args)
        {
            try { Run(args[0]).GetAwaiter().GetResult(); return 0; }
            catch(Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }
        private static async Task Run(string root)
        {
            string scope=QuotaHistoryStore.Scope(Path.Combine(root,"synthetic-home"));
            string folder=Path.Combine(root,scope);Directory.CreateDirectory(folder);
            DateTime first=DateTime.UtcNow.Date.AddDays(-179);
            for(int day=0;day<180;day++)
            {
                DateTime date=first.AddDays(day);string file=Path.Combine(folder,date.ToString("yyyy-MM-dd")+".jsonl");
                if(File.Exists(file))continue;
                using(var writer=new StreamWriter(file,false,new UTF8Encoding(false),65536))
                for(int minute=0;minute<1440;minute++)foreach(int period in new[]{300,10080})
                {
                    long time=LocalCodexUsage.Unix(date.AddMinutes(minute));
                    writer.WriteLine("{\"Time\":"+time+",\"Bucket\":\"codex\",\"Minutes\":"+period+",\"Remaining\":"+(100-(day*1440+minute)%period*100.0/period).ToString("0.000",CultureInfo.InvariantCulture)+",\"Reset\":"+(time/ (period*60L)+1)*(period*60L)+"}");
                }
            }
            var store=new QuotaHistoryStore(root);long end=LocalCodexUsage.Unix(first.AddDays(180))-1;
            foreach(int days in new[]{180,1,180,7,30,1,180})
            {
                var watch=Stopwatch.StartNew();var data=await store.ReadAsync(scope,end-days*86400L+1,end);watch.Stop();
                Console.WriteLine("READ "+days+"d: "+watch.ElapsedMilliseconds+" ms; "+data.Length+" samples");
                if(data.Length!=days*1440*2)throw new InvalidOperationException("Unexpected synthetic range total");
            }
        }
    }
}
