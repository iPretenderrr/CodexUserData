using System;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace CodexUserData
{
    // Only this store owns the recovery flag and the atomic settings write.
    // A read never mutates either the primary file or its known-good backup.
    internal sealed class SettingsStore
    {
        private readonly string path;
        private readonly Action warn;
        private readonly object gate=new object();
        private readonly JavaScriptSerializer json=new JavaScriptSerializer {MaxJsonLength=64*1024*1024};
        private bool recovered,warningShown;

        internal SettingsStore(string settingsPath,Action onSaveFailure)
        {path=settingsPath;warn=onSaveFailure;}

        internal Preferences Read()
        {
            lock(gate)
            {
                recovered=false;Exception error=null;Preferences result;
                if(TryRead(path,out result,out error))return result;
                Exception backupError;
                if(TryRead(path+".bak",out result,out backupError))
                {
                    // The next save preserves the damaged primary separately.
                    recovered=true;return result;
                }
                if(error!=null||backupError!=null)throw new IOException("设置及备份均无法读取，已保留原文件。请检查用户数据目录中的 widget-settings.json 和 .bak 备份。",error??backupError);
                return new Preferences();
            }
        }

        private bool TryRead(string file,out Preferences value,out Exception error)
        {
            value=null;error=null;
            try
            {
                if(!File.Exists(file))return false;
                value=json.Deserialize<Preferences>(File.ReadAllText(file,Encoding.UTF8));
                if(value==null)throw new IOException("Empty settings");value.Validate();return true;
            }
            catch(IOException ex){error=ex;}catch(UnauthorizedAccessException ex){error=ex;}
            catch(ArgumentException ex){error=ex;}catch(InvalidOperationException ex){error=ex;}
            return false;
        }

        internal void Save(Preferences preferences)
        {
            bool notify=false;
            lock(gate)
            {
                string temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    byte[] bytes=new UTF8Encoding(false).GetBytes(json.Serialize(preferences));
                    using(var file=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None))
                    {file.Write(bytes,0,bytes.Length);file.Flush(true);}
                    if(File.Exists(path))File.Replace(temporary,path,path+(recovered?".corrupt":".bak"));
                    else File.Move(temporary,path);
                    recovered=false;warningShown=false;
                }
                catch(IOException){notify=!warningShown;warningShown=true;}
                catch(UnauthorizedAccessException){notify=!warningShown;warningShown=true;}
                finally{try{if(File.Exists(temporary))File.Delete(temporary);}catch(IOException){}catch(UnauthorizedAccessException){}}
            }
            // UI notification is injected and occurs outside the persistence lock.
            if(notify&&warn!=null)warn();
        }
    }
}
