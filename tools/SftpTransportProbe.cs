using System;
using System.IO;
using System.Reflection;
using System.Web.Script.Serialization;
using Renci.SshNet.Common;
namespace CodexUserData
{
    internal static class SftpTransportProbe
    {
        [STAThread] public static int Main(string[] args)
        {
            try
            {
                var options=new JavaScriptSerializer().Deserialize<RemoteOptions>(File.ReadAllText(args[0]));
                if(options.Host!="127.0.0.1")throw new InvalidOperationException("Fixture must use loopback");
                string fingerprint=options.Fingerprint,key=options.KeyFile;options.KeyFile="";options.Secret=RemoteOptions.Protect("fixture-password");options.Fingerprint="";
                bool rejected=false;try{SftpFiles.Test(options);}catch(HostTrustException e){rejected=e.Fingerprint==fingerprint;}Require(rejected,"untrusted host is rejected before authentication");
                options.Fingerprint="SHA256:wrong";rejected=false;try{SftpFiles.Test(options);}catch(HostTrustException){rejected=true;}Require(rejected,"changed fingerprint is rejected");
                options.Fingerprint=fingerprint;Require(SftpFiles.Test(options).Contains("已找到用量事件"),"password authentication resolves default directory and reads usage");
                options.Secret=RemoteOptions.Protect("wrong");bool failed=false;try{SftpFiles.Test(options);}catch(SshAuthenticationException){failed=true;}Require(failed,"wrong password is rejected");
                options.KeyFile=key;options.Secret=RemoteOptions.Protect("fixture-passphrase");Require(SftpFiles.Test(options).Contains("已找到用量事件"),"encrypted private key authentication works");
                options.Directory="/missing";failed=false;try{SftpFiles.Test(options);}catch(SftpPathNotFoundException){failed=true;}Require(failed,"wrong remote directory produces a distinct failure");
                Console.WriteLine("SFTP TRANSPORT CHECKS: 6");return 0;
            }
            catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
        }
        private static void Require(bool value,string label){if(!value)throw new InvalidOperationException(label);Console.WriteLine("PASS "+label);}
    }
}
