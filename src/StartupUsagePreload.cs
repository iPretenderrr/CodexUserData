using System;
using System.IO;
using System.Threading.Tasks;

namespace CodexUserData
{
    // One owner scans the local ledger while the film plays. Consumers await
    // Completion before touching the shared cache, then take the same reader.
    internal sealed class StartupUsagePreload
    {
        private readonly string home;
        private LocalCodexUsage reader;
        private volatile bool cancelled;
        internal Task Completion {get;private set;}
        internal Exception Failure {get;private set;}

        internal StartupUsagePreload(string directory,string cache)
        {
            home=directory;
            Completion=Task.Run(delegate
            {
                try
                {
                    if(cancelled)return;
                    var prepared=new LocalCodexUsage(directory,cache);
                    prepared.Update(null,()=>cancelled);
                    if(!cancelled)reader=prepared;
                }
                // Normal refresh retains the existing error UI and retry path.
                // A preload failure must never stop startup or become an
                // unobserved fault when the application exits during playback.
                catch(Exception error){Failure=error;}
            });
        }

        // Called only after Completion, under WidgetWindow's usage gate. Drop
        // an obsolete source instead of mixing data after a settings change.
        internal LocalCodexUsage Take(string directory)
        {
            if(!Completion.IsCompleted)throw new InvalidOperationException("Preload is still running.");
            var result=reader;reader=null;
            return !cancelled&&String.Equals(home,directory,StringComparison.OrdinalIgnoreCase)?result:null;
        }

        internal void Cancel(){cancelled=true;}
    }
}
