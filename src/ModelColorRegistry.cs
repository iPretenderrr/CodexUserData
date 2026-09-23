using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace CodexUserData
{
    internal sealed class ModelColorDocument
    {
        public int schema {get;set;}
        public Dictionary<string,int> assignments {get;set;}
    }

    // Store compact palette slots instead of theme-specific colors. Theme changes can
    // derive a new light/dark pair without rewriting stable per-model assignments.
    internal static class ModelColorRegistry
    {
        internal const int CandidateCount=64;
        private const int MaxAssignments=256;
        private const int MaxBytes=64*1024;
        private static readonly object Gate=new object();
        private static readonly Dictionary<string,int> Assignments=new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);
        private static bool initialized,writable,writeBlocked,dirty;
        private static string PathName {get{return Path.Combine(Program.DataFolder,"model-colors.json");}}

        internal static void Initialize(bool allowWrites)
        {
            lock(Gate)
            {
                writable|=allowWrites;if(initialized)return;initialized=true;
                ModelColorDocument value,backup;bool primaryExists=File.Exists(PathName),primaryValid=TryRead(PathName,out value),backupValid=false;
                if(primaryValid)Load(value);
                else
                {
                    backupValid=TryRead(PathName+".bak",out backup);if(backupValid)Load(backup);
                    if(primaryExists)
                    {
                        // The normal UI repairs a damaged primary while preserving it for
                        // diagnosis. Read-only helper processes never change user data.
                        if(!writable)writeBlocked=true;
                        else if(backupValid){if(!RestorePrimary(backup,true))writeBlocked=true;}
                        else if(!QuarantinePrimary())writeBlocked=true;
                    }
                    else if(backupValid&&writable&&!RestorePrimary(backup,false))writeBlocked=true;
                }
            }
        }

        internal static bool TryGet(string model,out int slot)
        {Initialize(false);lock(Gate)return Assignments.TryGetValue(Normalize(model),out slot);}

        internal static bool Ensure(IEnumerable<string> models,Func<string,int[],int> select)
        {
            if(models==null)return false;Initialize(false);
            lock(Gate)
            {
                bool changed=false;
                foreach(string source in models)
                {
                    string model=Normalize(source);if(!ValidModel(model)||Assignments.ContainsKey(model)||Assignments.Count>=MaxAssignments)continue;
                    int slot=select(model,Assignments.Values.Distinct().ToArray());
                    if(slot<0||slot>=CandidateCount)slot=(int)(StableHash(model)%CandidateCount);
                    Assignments[model]=slot;changed=dirty=true;
                }
                if(dirty&&writable&&!writeBlocked&&Save())dirty=false;
                return changed;
            }
        }

        internal static ModelColorDocument Parse(string json)
        {
            if(String.IsNullOrWhiteSpace(json)||Encoding.UTF8.GetByteCount(json)>MaxBytes)throw new InvalidDataException("模型颜色文件无效。");
            ModelColorDocument document;
            try{document=Serializer().Deserialize<ModelColorDocument>(json);}catch(Exception ex){throw new InvalidDataException("模型颜色文件 JSON 无效。",ex);}
            if(document==null||document.schema!=1||document.assignments==null||document.assignments.Count>MaxAssignments)throw new InvalidDataException("模型颜色文件字段无效。");
            var clean=new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);
            foreach(var pair in document.assignments.OrderBy(p=>p.Key,StringComparer.OrdinalIgnoreCase))
            {
                string model=Normalize(pair.Key);
                if(!ValidModel(model)||pair.Value<0||pair.Value>=CandidateCount)throw new InvalidDataException("模型颜色文件包含无效分配。");
                clean[model]=pair.Value;
            }
            document.assignments=clean;return document;
        }

        private static bool TryRead(string path,out ModelColorDocument document)
        {
            document=null;
            try
            {
                if(!File.Exists(path))return false;var info=new FileInfo(path);if(info.Length<=0||info.Length>MaxBytes)return false;
                document=Parse(File.ReadAllText(path,Encoding.UTF8));return true;
            }
            catch(Exception){return false;}
        }
        private static void Load(ModelColorDocument document)
        {foreach(var pair in document.assignments)Assignments[pair.Key]=pair.Value;}

        private static bool RestorePrimary(ModelColorDocument document,bool replace)
        {
            string temporary=PathName+"."+Guid.NewGuid().ToString("N")+".tmp";
            try
            {
                Directory.CreateDirectory(Program.DataFolder);WriteDocument(temporary,document);
                if(replace)File.Replace(temporary,PathName,PathName+".corrupt-"+DateTime.UtcNow.ToString("yyyyMMddHHmmssfff"));
                else File.Move(temporary,PathName);
                return true;
            }
            catch(Exception){return false;}
            finally{try{if(File.Exists(temporary))File.Delete(temporary);}catch(Exception){}}
        }
        private static bool QuarantinePrimary()
        {
            try{File.Move(PathName,PathName+".corrupt-"+DateTime.UtcNow.ToString("yyyyMMddHHmmssfff"));return true;}catch(Exception){return false;}
        }

        private static bool Save()
        {
            string temporary=PathName+"."+Guid.NewGuid().ToString("N")+".tmp";
            try
            {
                Directory.CreateDirectory(Program.DataFolder);
                var ordered=new SortedDictionary<string,int>(Assignments,StringComparer.OrdinalIgnoreCase);
                var snapshot=new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);foreach(var pair in ordered)snapshot[pair.Key]=pair.Value;
                WriteDocument(temporary,new ModelColorDocument{schema=1,assignments=snapshot});
                if(File.Exists(PathName))File.Replace(temporary,PathName,PathName+".bak");else File.Move(temporary,PathName);
                return true;
            }
            catch(Exception){return false;}
            finally{try{if(File.Exists(temporary))File.Delete(temporary);}catch(Exception){}}
        }
        private static void WriteDocument(string path,ModelColorDocument document)
        {
            byte[] bytes=new UTF8Encoding(false).GetBytes(Serializer().Serialize(document));if(bytes.Length>MaxBytes)throw new InvalidDataException("模型颜色文件过大。");
            using(var file=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None)){file.Write(bytes,0,bytes.Length);file.Flush(true);}
        }
        private static JavaScriptSerializer Serializer(){return new JavaScriptSerializer{MaxJsonLength=MaxBytes,RecursionLimit=8};}
        private static string Normalize(string model){return String.IsNullOrWhiteSpace(model)?"":model.Trim().ToLowerInvariant();}
        private static bool ValidModel(string model){return model.Length>0&&model.Length<=160&&!model.Any(Char.IsControl);}
        private static uint StableHash(string value){uint hash=2166136261;foreach(char c in value)hash=(hash^c)*16777619;return hash;}
    }
}
