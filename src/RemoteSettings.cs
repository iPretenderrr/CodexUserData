using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace CodexUserData
{
    internal sealed class RemoteSettings : StackPanel
    {
        private readonly CheckBox enabled;private readonly TextBox host,port,user,directory,key;
        private readonly PasswordBox secret;private readonly TextBlock status;private readonly Button test;
        private readonly RemoteOptions original;private string fingerprint,trustedEndpoint;private bool busy,closed;
        internal RemoteSettings(RemoteOptions options)
        {
            original=options;fingerprint=options.Fingerprint;trustedEndpoint=Endpoint(options.Host,options.Port,options.User);
            enabled=new CheckBox{Content="同时统计远程服务器（与本机使用同一账号）",IsChecked=options.Enabled,Foreground=Theme.Ink,Margin=new Thickness(0,14,0,8)};Children.Add(enabled);
            host=Input("服务器地址",options.Host);port=Input("SSH 端口",options.Port.ToString());user=Input("用户名",options.User);
            directory=Input("远程 Codex 目录（留空尝试 .codex）",options.Directory);
            key=Input("私钥文件（留空使用密码认证）",options.KeyFile);
            var browse=Theme.Button("选择私钥…","选择本机私钥文件",120);browse.HorizontalAlignment=HorizontalAlignment.Left;Children.Add(browse);
            browse.Click+=delegate{var picker=new Microsoft.Win32.OpenFileDialog{Filter="私钥文件|*.*"};if(picker.ShowDialog()==true)key.Text=picker.FileName;};
            Children.Add(Theme.Text("密码 / 私钥口令（留空保留已保存凭据）",11,Theme.Muted));
            secret=new PasswordBox{Margin=new Thickness(0,6,0,10),Padding=new Thickness(8),Background=Theme.Surface,Foreground=Theme.Ink,BorderBrush=Theme.Line};Children.Add(secret);
            var clear=Theme.Button("清除已保存凭据","保存设置后清除密码或口令",150);clear.HorizontalAlignment=HorizontalAlignment.Left;Children.Add(clear);clear.Click+=delegate{original.Secret="";secret.Clear();status.Text="已清除草稿中的凭据，保存设置后生效。";};
            test=Theme.Button("测试连接","核对指纹并检查远程日志",120);test.Margin=new Thickness(0,12,0,6);test.HorizontalAlignment=HorizontalAlignment.Left;Children.Add(test);test.Click+=async delegate{await Test();};
            status=Theme.Text("只读访问服务器日志。关闭远程统计后，合计仅包含本机用量。",11,Theme.Muted);status.TextWrapping=TextWrapping.Wrap;Children.Add(status);
        }
        private TextBox Input(string title,string text){var label=Theme.Text(title,12,Theme.Ink);label.Margin=new Thickness(0,10,0,6);Children.Add(label);var box=Theme.Input(text??"");Children.Add(box);return box;}
        private static string Endpoint(string h,int p,string u){return h.Trim().ToLowerInvariant()+":"+p+"/"+u;}
        internal RemoteOptions Read()
        {
            int number;if(!Int32.TryParse(port.Text,out number))throw new ArgumentException("SSH 端口请输入整数。");
            var result=new RemoteOptions{Enabled=enabled.IsChecked==true,Host=host.Text.Trim(),Port=number,User=user.Text.Trim(),Directory=String.IsNullOrWhiteSpace(directory.Text)?".codex":directory.Text.Trim(),KeyFile=key.Text.Trim(),Secret=String.IsNullOrEmpty(secret.Password)?original.Secret:RemoteOptions.Protect(secret.Password)};
            result.Fingerprint=Endpoint(result.Host,result.Port,result.User)==trustedEndpoint?fingerprint:"";
            if(result.Enabled){result.Validate();if(String.IsNullOrEmpty(result.Fingerprint))throw new ArgumentException("请先测试远程连接并确认服务器指纹。");}
            return result;
        }
        private RemoteOptions ForTest()
        {
            bool active=enabled.IsChecked==true;enabled.IsChecked=false;
            try{var result=Read();result.Enabled=active;result.Validate();return result;}finally{enabled.IsChecked=active;}
        }
        private async Task Test()
        {
            if(busy)return;busy=true;test.IsEnabled=false;status.Text="正在连接…";
            try
            {
                var options=ForTest();
                HostTrustException trust=null;
                try{string message=await Task.Run(()=>SftpFiles.Test(options));if(!closed)status.Text=message;}
                catch(HostTrustException ex){trust=ex;}
                if(trust!=null)
                {
                    if(closed)return;
                    string title=String.IsNullOrEmpty(options.Fingerprint)?"首次连接服务器":"服务器指纹发生变化";
                    var owner=Window.GetWindow(this);
                    if(MessageBox.Show(owner,"请与服务器管理员提供的 SSH 指纹核对：\n\n"+trust.Fingerprint+"\n\n确认信任该服务器？",title,MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes){status.Text="尚未信任服务器，未发送登录凭据。";return;}
                    fingerprint=trust.Fingerprint;trustedEndpoint=Endpoint(options.Host,options.Port,options.User);options.Fingerprint=fingerprint;
                    string message=await Task.Run(()=>SftpFiles.Test(options));if(!closed)status.Text=message;
                }
            }
            catch(Exception ex){if(!closed)status.Text=SftpFiles.Error(ex);}
            finally{busy=false;if(!closed)test.IsEnabled=true;}
        }
        internal void Close(){closed=true;}
    }
}
