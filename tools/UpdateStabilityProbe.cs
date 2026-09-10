using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace CodexUserData
{
    internal static class UpdateStabilityProbe
    {
        private static string Response(string tag,string page=null,string body="Release notes")
        {return Program.Json.Serialize(new{tag_name=tag,html_url=page??"https://github.com/iPretenderrr/CodexUserData/releases/tag/"+tag,body=body,draft=false,prerelease=false});}
        private static void Reject(Action action,string message)
        {
            bool rejected=false;try{action();}catch(InvalidDataException){rejected=true;}catch(ArgumentException){rejected=true;}
            StabilityProbe.Check(rejected,message);
        }
        internal static void Run(string root)
        {
            var current=new Version(1,7,0,0);var older=ReleaseUpdate.Parse(Response("v1.6.10"));
            StabilityProbe.Check(older.Describe(current).Contains("本地版本")&&older.Describe(current).Contains("更新"),"local candidate newer than public release is identified without suggesting a downgrade");
            StabilityProbe.Check(ReleaseUpdate.Parse(Response("v1.7.0")).Describe(current).Contains("已是最新"),"three-part public version equals a four-part assembly version with zero revision");
            StabilityProbe.Check(ReleaseUpdate.Parse(Response("v1.10.0")).Version>new Version(1,9,0,0),"release comparison uses numeric version components");
            foreach(string tag in new[]{"v1.7.0-beta","1.7","v01.7.0","v1.7.0\n","v1.7.0;run"})Reject(()=>ReleaseUpdate.Parse(Response(tag)),"invalid or unstable version tag is rejected: "+tag.Replace("\n","\\n"));
            foreach(string link in new[]{"http://github.com/iPretenderrr/CodexUserData/releases/tag/v1.7.0","https://example.com/iPretenderrr/CodexUserData/releases/tag/v1.7.0","https://github.com/other/repo/releases/tag/v1.7.0","https://github.com/iPretenderrr/CodexUserData/releases/tag/v9.0.0","https://github.com/iPretenderrr/CodexUserData/releases/tag/v1.7.0?next=other","https://github.com@evil.example/iPretenderrr/CodexUserData/releases/tag/v1.7.0"})Reject(()=>ReleaseUpdate.Parse(Response("v1.7.0",link)),"release navigation rejects an untrusted or mismatched URL");
            string markup="<script>run()</script>\n[external](https://example.com)";StabilityProbe.Check(ReleaseUpdate.Parse(Response("v1.7.0",null,markup)).Notes==markup,"release notes remain literal text rather than executing markup or links");
            Reject(()=>ReleaseUpdate.Parse(Response("v1.7.0").Replace("\"draft\":false","\"draft\":true")),"draft release metadata is rejected");
            using(var oversized=new MemoryStream(new byte[ReleaseUpdate.MaxResponseBytes+1]))Reject(()=>ReleaseUpdate.ReadResponseAsync(oversized,CancellationToken.None).GetAwaiter().GetResult(),"decompressed release response is bounded while reading the stream");
            var canceled=new CancellationTokenSource();canceled.Cancel();bool observed=false;
            using(var input=new MemoryStream(Encoding.UTF8.GetBytes(Response("v1.7.0"))))try{ReleaseUpdate.ReadResponseAsync(input,canceled.Token).GetAwaiter().GetResult();}catch(OperationCanceledException){observed=true;}
            StabilityProbe.Check(observed,"canceled update reading stops before consuming response data");observed=false;
            try{ReleaseUpdate.CheckAsync(canceled.Token).GetAwaiter().GetResult();}catch(OperationCanceledException){observed=true;}
            canceled.Dispose();StabilityProbe.Check(observed,"pre-canceled update checks do not create a network request");
            string diagnostic="{\n  \"appVersion\": \"1.7.0\",\n  \"synthetic\": true\n}";var preview=new DiagnosticPreviewWindow(diagnostic);
            try
            {
                var textbox=Find(preview);StabilityProbe.Check(textbox!=null&&textbox.IsReadOnly&&textbox.Text==diagnostic,"diagnostics window previews the complete immutable export text");
                string path=Path.Combine(root,"diagnostics-preview-copy.txt");preview.SavePreview(path);
                StabilityProbe.Check(File.ReadAllText(path)==diagnostic,"saving diagnostics writes the exact preview without regenerating it");
            }
            finally{preview.Close();}
        }
        private static TextBox Find(DependencyObject node)
        {
            var text=node as TextBox;if(text!=null&&AutomationProperties.GetAutomationId(text)=="DiagnosticsPreview")return text;
            foreach(object item in LogicalTreeHelper.GetChildren(node)){var child=item as DependencyObject;if(child==null)continue;var found=Find(child);if(found!=null)return found;}return null;
        }
    }
}
