using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace CodexUserData
{
    // One owner coordinates the mutable local ledger, startup handoff and exported
    // records. UI callers capture their source first and query only on workers.
    internal sealed class LocalUsageSession
    {
        private readonly object gate=new object();
        private readonly string cachePath;
        private readonly StartupUsagePreload startup;
        private LocalCodexUsage reader,exportedReader;
        private string readerHome;
        private List<LogCursor> exportedRecords;
        private int exportedVersion=-1;
        private bool startupScanAvailable;

        internal LocalUsageSession(string cache,StartupUsagePreload preload)
        {cachePath=cache;startup=preload;}

        internal bool HasStartup {get{return startup!=null;}}
        internal Task StartupCompletion {get{return startup==null?Task.FromResult(0):startup.Completion;}}
        internal void CancelStartup(){if(startup!=null)startup.Cancel();}

        internal T Read<T>(string directory,Func<LocalCodexUsage,T> query,Func<bool> cancelled)
        {return Read(directory,(source,failure)=>query(source),cancelled,false);}

        internal T Read<T>(string directory,Func<LocalCodexUsage,string,T> query,Func<bool> cancelled,bool tolerateFailure)
        {return Access(directory,false,false,null,cancelled,tolerateFailure,query);}

        internal T Refresh<T>(string directory,bool scan,Action<string> progress,Func<bool> cancelled,bool tolerateFailure,Func<LocalCodexUsage,string,T> query)
        {return Access(directory,true,scan,progress,cancelled,tolerateFailure,query);}

        private T Access<T>(string directory,bool refresh,bool scan,Action<string> progress,Func<bool> cancelled,bool tolerateFailure,Func<LocalCodexUsage,string,T> query)
        {
            lock(gate)
            {
                string failure=null;
                try
                {
                    bool prepared=Prepare(directory,progress,cancelled);
                    // Charts may adopt the preload first. Its completed scan still
                    // satisfies the first refresh, avoiding duplicate startup I/O.
                    if(refresh)
                    {
                        bool skipStartupScan=startupScanAvailable;startupScanAvailable=false;
                        if(!prepared&&scan&&!skipStartupScan)reader.Update(progress,cancelled);
                    }
                }
                catch(IOException){if(!tolerateFailure)throw;failure="本机日志读取失败，保留上次数据。";}
                catch(UnauthorizedAccessException){if(!tolerateFailure)throw;failure="本机日志没有读取权限，保留上次数据。";}
                ThrowIfCancelled(cancelled);
                // A failed new directory must never borrow the previous source's
                // records. Remote aggregation may still proceed with no local rows.
                var current=SameHome(directory)?reader:null;
                return query(current,failure);
            }
        }

        internal List<LogCursor> Export(LocalCodexUsage source)
        {
            lock(gate)
            {
                // Export copies cursors, so unchanged ledgers reuse the same list
                // identity and preserve the union cache on low-performance devices.
                if(exportedReader!=source||exportedVersion!=source.DataVersion)
                {exportedRecords=source.Export();exportedReader=source;exportedVersion=source.DataVersion;}
                return exportedRecords;
            }
        }

        internal void ClearExport()
        {lock(gate){exportedReader=null;exportedRecords=null;exportedVersion=-1;}}

        private bool Prepare(string directory,Action<string> progress,Func<bool> cancelled)
        {
            ThrowIfCancelled(cancelled);
            if(startup!=null)
            {
                var prepared=startup.Take(directory);
                if(prepared!=null){reader=prepared;readerHome=directory;startupScanAvailable=true;}
            }
            if(SameHome(directory))return false;
            var candidate=new LocalCodexUsage(directory,cachePath);
            candidate.Update(progress,cancelled);
            ThrowIfCancelled(cancelled);
            // Publish only after the first successful scan. Otherwise a later
            // chart could mistake a failed initial read for valid zero usage.
            reader=candidate;readerHome=directory;startupScanAvailable=false;
            return true;
        }

        private bool SameHome(string directory)
        {return reader!=null&&String.Equals(readerHome,directory,StringComparison.OrdinalIgnoreCase);}
        private static void ThrowIfCancelled(Func<bool> cancelled)
        {if(cancelled!=null&&cancelled())throw new OperationCanceledException();}
    }
}
