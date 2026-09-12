using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using Microsoft.Web.WebView2.Wpf;

namespace CodexUserData
{
    internal static class HtmlStabilityProbe
    {
        private static T Find<T>(DependencyObject root) where T:DependencyObject
        {if(root is T)return (T)root;for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){var found=Find<T>(VisualTreeHelper.GetChild(root,i));if(found!=null)return found;}return null;}
        private static UsageSnapshot Usage(long tokens)
        {return new UsageSnapshot{TotalTokens=tokens,Daily=new[]{new DailyUsage{Date=DateTime.Now.ToString("yyyy-MM-dd"),Tokens=tokens,Input=tokens,Requests=1}}};}
        internal static async Task Run(string root)
        {
            StabilityProbe.Check(CustomShapeView.IsControllerVisibilityRace(new InvalidOperationException("wrapped",new System.Runtime.InteropServices.COMException("visibility",unchecked((int)0x8007139F)))),"WebView visibility race recognizes the SDK exception wrapper");
            StabilityProbe.Check(!CustomShapeView.IsControllerVisibilityRace(new InvalidOperationException("other failure")),"unrelated browser failures are not swallowed as visibility races");
            string relative=Path.Combine("skins","source-fixture","shape.json"),manifest=Path.Combine(root,relative);
            Directory.CreateDirectory(Path.GetDirectoryName(manifest));
            File.WriteAllText(manifest,"{\"apiVersion\":1,\"name\":\"Synthetic fixture\",\"entry\":\"index.html\",\"width\":240,\"height\":84}");
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(manifest),"index.html"),"<!doctype html><html><style>html,body{margin:0;background:transparent;color:white}main{border-radius:16px;background:#234968;padding:16px}</style><main id='value'>Fixture</main><script>window.fixtureMessages=[];chrome.webview.addEventListener('message',e=>{if(e.data.type==='snapshot'){window.fixtureMessages.push(e.data.data.today?.tokens??null);window.fixtureMotion=e.data.data.motion;document.getElementById('value').textContent='Fixture ready'}});</script></html>");
            var p=new Preferences{Source="local",CodexHome=Path.Combine(root,"codex"),Database=Path.Combine(root,"missing.db"),LiveQuota=false,ShowQuota=false,QuotaCli="",RefreshSeconds=3600,BallStyle="html",CustomShape=relative,BallDock="",BallLeft=350,BallTop=220,Left=150,Top=150,OrbAnimation="smooth",BallOpacity=.65};
            var widget=new WidgetWindow(p,false);widget.Show();await Task.Delay(250);StabilityProbe.Call(widget,"OpenBall");
            var ball=StabilityProbe.Field<FloatingBall>(widget,"ball");WebView2CompositionControl web=null;
            try{await StabilityProbe.Until(()=>{web=Find<WebView2CompositionControl>(ball);return web!=null&&web.CoreWebView2!=null;},"HTML controller did not initialize",20000);}
            catch(Exception ex){var failed=StabilityProbe.Field<CustomShapeView>(ball,"custom");throw new Exception("HTML initialization: "+(failed==null?"no custom view":failed.LoadStage+" / "+failed.LastError),ex);}
            string content="";var deadline=DateTime.UtcNow.AddSeconds(10);
            while(!content.Contains("Fixture ready")){if(DateTime.UtcNow>deadline)throw new TimeoutException("HTML never received fixture data");content=await web.ExecuteScriptAsync("document.getElementById('value')?.textContent||''");await Task.Delay(40);}
            await Task.Delay(400);StabilityProbe.Check(ball.IsVisible&&!widget.IsVisible,"real Widget opens HTML with a data-ready page");
            WindowInteraction.PrepareReveal(ball);ball.Apply(null,null,"fixture","");
            StabilityProbe.Check(ball.Opacity==0,"HTML data refresh cannot expose a prepared transparent frame");
            WindowInteraction.Reveal(ball);await Task.Delay(400);
            // Drive the public browser bridge rather than calling the resize implementation.
            await web.ExecuteScriptAsync("chrome.webview.postMessage({type:'resize',width:312,height:104})");
            await StabilityProbe.Until(()=>ball.Width==312&&ball.Height==104,"bridge resize lost",2500);
            var custom=StabilityProbe.Field<CustomShapeView>(ball,"custom");var work=SystemParameters.WorkArea;
            var same=Usage(111000);widget.ApplySnapshot(same);await Task.Delay(150);
            int beforeRepeat=Int32.Parse(await web.ExecuteScriptAsync("window.fixtureMessages.length"));
            for(int i=0;i<20;i++)widget.ApplySnapshot(same);
            await Task.Delay(150);
            StabilityProbe.Check(Int32.Parse(await web.ExecuteScriptAsync("window.fixtureMessages.length"))==beforeRepeat,"identical visible data does not send duplicate browser snapshots");
            p.BallEffectIntensity=3;long effectNow=LocalCodexUsage.Unix(DateTime.Now);
            ball.Apply(same,null,"fixture","");ball.ApplyActivity(new ActivityReport{ActiveTasks=1,Until=effectNow+60,ObservedAt=effectNow});
            // WPF applies a new animation clock on the next dispatcher/render tick.
            await Task.Delay(60);var effectHost=((System.Windows.Controls.Decorator)ball.Content).Child;
            StabilityProbe.Check(DependencyPropertyHelper.GetValueSource(effectHost,UIElement.OpacityProperty).IsAnimated,"existing HTML receives host-owned running breathing (custom="+ball.IsCustom+", visible="+ball.IsVisible+", motion="+Theme.MotionAllowed+", host="+effectHost.GetType().Name+")");
            ball.ApplyActivity(new ActivityReport());ball.SetCompletionPending(true);
            string effectData=(string)StabilityProbe.Call(custom,"SerializeData");
            StabilityProbe.Check(effectData.Contains("\"effectIntensity\":3")&&effectData.Contains("\"completionPending\":true"),"HTML data contract includes persisted strength and pending acknowledgement");
            ball.SetCompletionPending(false);
            StabilityProbe.Check(!DependencyPropertyHelper.GetValueSource(effectHost,UIElement.OpacityProperty).IsAnimated,"HTML acknowledgement clears the host breathing clock");
            p.OrbAnimation="eco";Theme.Apply(p);widget.ApplySnapshot(same);await Task.Delay(100);
            StabilityProbe.Check(await web.ExecuteScriptAsync("window.fixtureMotion")=="\"eco\"","HTML receives the lightweight motion profile");
            p.OrbAnimation="off";Theme.Apply(p);widget.ApplySnapshot(same);await Task.Delay(100);
            StabilityProbe.Check(await web.ExecuteScriptAsync("window.fixtureMotion")=="\"off\"","HTML receives disabled decorative motion");
            p.OrbAnimation="smooth";Theme.Apply(p);widget.ApplySnapshot(same);await Task.Delay(100);
            await web.ExecuteScriptAsync("window.fixtureBeforeDock=window.fixtureMessages.length");
            ball.Left=work.Left+2;ball.Top=work.Top+work.Height/2;StabilityProbe.Call(ball,"SnapToEdge");
            await StabilityProbe.Until(()=>ball.IsPillar,"HTML did not dock",3500);await Task.Delay(350);
            StabilityProbe.Check(Object.ReferenceEquals(custom,StabilityProbe.Field<CustomShapeView>(ball,"custom")),"docking retains the same custom controller");
            var suspendDeadline=DateTime.UtcNow.AddSeconds(2);while(!web.CoreWebView2.IsSuspended&&DateTime.UtcNow<suspendDeadline)await Task.Delay(40);
            StabilityProbe.Check(web.CoreWebView2.IsSuspended,"docked HTML requests actual browser suspension");
            // Do not execute JS while hidden: doing so could itself wake the browser and
            // conceal a faulty suspension or a backlog of intermediate snapshots.
            for(int i=1;i<=20;i++)widget.ApplySnapshot(Usage(111000+i));
            await Task.Delay(120);
            StabilityProbe.Check(web.CoreWebView2.IsSuspended,"hidden data updates leave the browser suspended");
            ball.Left=work.Left+work.Width/2;ball.Top=work.Top+work.Height/2;StabilityProbe.Call(ball,"SnapToEdge");
            await StabilityProbe.Until(()=>!ball.IsPillar&&ball.IsVisible&&Math.Abs(ball.Opacity-.65)<.01,"HTML restore did not settle",5000);
            StabilityProbe.Check(ball.Width==312&&ball.Height==104,"HTML undocking preserves the page-requested dimensions");
            StabilityProbe.Check(Find<WebView2CompositionControl>(ball)!=null,"HTML restore retains a live renderer: "+custom.LastError);
            StabilityProbe.Check(!web.CoreWebView2.IsSuspended,"restored browser is resumed");
            StabilityProbe.Check(await web.ExecuteScriptAsync("window.fixtureMessages.slice(window.fixtureBeforeDock).length>0&&window.fixtureMessages.slice(window.fixtureBeforeDock).every(value=>value===111020)")=="true","restoring hidden HTML delivers only the latest data without a queued backlog");
            for(int i=0;i<6;i++){ball.Hide();await Task.Delay(12);ball.Show();await Task.Delay(12);}
            await custom.WaitForPresentationAsync();await Task.Delay(350);
            StabilityProbe.Check(ball.IsVisible&&!web.CoreWebView2.IsSuspended&&await web.ExecuteScriptAsync("6*7")=="42","rapid hide/show finishes with a responsive resumed browser");
            // Hold future page frames so the close check exercises an outstanding waiter,
            // rather than accidentally accepting an already completed presentation task.
            await web.ExecuteScriptAsync("window.requestAnimationFrame=function(){return 0}");
            await web.ExecuteScriptAsync("chrome.webview.postMessage({type:'restoreMain'})");
            await StabilityProbe.Until(()=>widget.IsVisible&&!ball.IsVisible,"HTML bridge failed to restore the actual main window",4000);
            StabilityProbe.Check(!p.BallMode&&!p.Collapsed,"HTML restore uses the full Widget lifecycle");
            StabilityProbe.Call(widget,"OpenBall");
            await StabilityProbe.Until(()=>ball.IsVisible,"HTML could not reopen for disposal check",2500);
            var presentation=custom.WaitForPresentationAsync();
            StabilityProbe.Check(!presentation.IsCompleted,"disposal fixture has an outstanding page presentation");
            widget.Close();
            StabilityProbe.Check(await Task.WhenAny(presentation,Task.Delay(500))==presentation,"closing the host completes pending HTML presentation waits");
            await presentation;
            StabilityProbe.Check(StabilityProbe.Field<WebView2CompositionControl>(custom,"web")==null,"closing the host releases its browser control");
        }
    }
}
