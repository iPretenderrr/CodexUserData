using System;
using System.IO;
using System.Reflection;

namespace CodexUserData
{
    internal static class SettingsStoreStabilityProbe
    {
        internal static void Run(string root)
        {
            string directory=Path.Combine(root,"settings-store");Directory.CreateDirectory(directory);
            string path=Path.Combine(directory,"widget-settings.json");int warnings=0;
            var store=new SettingsStore(path,()=>warnings++);
            var discovery=(Lazy<string>)typeof(QuotaReader).GetField("defaultExecutable",BindingFlags.Static|BindingFlags.NonPublic).GetValue(null);
            bool detected=discovery.IsValueCreated;
            var first=store.Read();first.QuotaCli=Path.Combine(directory,"custom-codex.exe");first.StartupAnimationStyle="prism";
            first.Validate();var clone=first.Clone();
            StabilityProbe.Check(discovery.IsValueCreated==detected&&clone.QuotaCli==first.QuotaCli,"settings construction/validation/cloning does not discover or rewrite CLI paths");
            store.Save(first);string valid=File.ReadAllText(path);
            first.StartupAnimationStyle="matrix";store.Save(first);
            StabilityProbe.Check(store.Read().StartupAnimationStyle=="matrix"&&File.ReadAllText(path+".bak")==valid,"atomic settings replacement preserves the previous good snapshot");
            File.WriteAllText(path,"{ broken fixture");string damaged=File.ReadAllText(path);
            var recovered=store.Read();
            StabilityProbe.Check(recovered.StartupAnimationStyle=="prism"&&File.ReadAllText(path)==damaged,"backup recovery does not overwrite damaged primary during reads");
            store.Save(recovered);
            StabilityProbe.Check(File.ReadAllText(path+".corrupt")==damaged&&File.ReadAllText(path+".bak")==valid,"saving recovered settings preserves the valid backup and corrupt evidence");
            string before=File.ReadAllText(path);
            using(var locked=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.None))
            {store.Save(first);store.Save(first);}
            StabilityProbe.Check(warnings==1&&File.ReadAllText(path)==before&&Directory.GetFiles(directory,"*.tmp").Length==0,"failed saves retain settings, remove only their own temp files and warn once");
            store.Save(first);StabilityProbe.Check(store.Read().StartupAnimationStyle=="matrix","settings can save successfully after a transient file lock");
            File.WriteAllText(path,"{");File.WriteAllText(path+".bak","{");bool failed=false;
            try{store.Read();}catch(IOException){failed=true;}
            StabilityProbe.Check(failed&&File.ReadAllText(path)=="{"&&File.ReadAllText(path+".bak")=="{","two damaged settings files fail explicitly without replacing user data");
        }
    }
}
