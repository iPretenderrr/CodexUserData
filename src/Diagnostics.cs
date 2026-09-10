using System;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace CodexUserData
{
    // Export an explicit small allowlist, never serialized preferences/snapshots or exception
    // text. New model fields remain private by default unless reviewed and added here.
    internal static class Diagnostics
    {
        internal static string Create(Preferences preferences,UsageSnapshot usage,ActivityReport activity,QuotaBucket quota)
        {
            long now=LocalCodexUsage.Unix(DateTime.UtcNow);var report=new StringBuilder(1600);
            report.AppendLine("CodexUserData 诊断报告");report.AppendLine("格式版本：1");
            report.AppendLine("仅包含下列运行状态；不会自动上传。");report.AppendLine();
            Add(report,"程序版本",Assembly.GetExecutingAssembly().GetName().Version.ToString());
            Add(report,"系统版本",Environment.OSVersion.Version.ToString());
            Add(report,"系统架构",Environment.Is64BitOperatingSystem?"64-bit":"32-bit");
            Add(report,"程序架构",Environment.Is64BitProcess?"64-bit":"32-bit");
            Add(report,"CLR 版本",Environment.Version.ToString());report.AppendLine();

            Add(report,"设置状态",preferences==null?"unavailable":"available");
            Add(report,"数据来源",Pick(preferences==null?null:preferences.Source,"local","ccswitch"));
            Add(report,"统计范围",Pick(preferences==null?null:preferences.Range,"today","week","month","all"));
            Add(report,"主题",Pick(preferences==null?null:preferences.ThemeMode,"dark","light","custom"));
            Add(report,"动画预设",Pick(preferences==null?null:preferences.OrbAnimation,"auto","smooth","eco","off"));
            Add(report,"界面形态",Form(preferences));
            Add(report,"动画速率",preferences==null?"unknown":Number(preferences.AnimationSpeed,.5,2));
            Add(report,"悬浮球透明度",preferences==null?"unknown":Number(preferences.BallOpacity,.2,1));
            Add(report,"刷新间隔秒",preferences==null?"unknown":Number(preferences.RefreshSeconds,2,3600));
            Add(report,"在线查询",preferences==null?"unknown":Flag(preferences.LiveQuota));
            Add(report,"完成提醒",preferences==null?"unknown":Flag(preferences.CompletionFlash));
            Add(report,"位置锁定",preferences==null?"unknown":Flag(preferences.BallPositionLocked));report.AppendLine();

            Add(report,"统计快照",usage==null?"unavailable":"available");
            Add(report,"日志文件数",usage==null?"unknown":Count(usage.CoverageFiles,10000000));
            Add(report,"统计提示数",usage==null?"unknown":Count(usage.CoverageWarnings,10000000));
            Add(report,"存在统计说明",usage==null?"unknown":Flag(!String.IsNullOrEmpty(usage.Warning)));
            Add(report,"提供实际费用",usage==null?"unknown":Flag(usage.CostAvailable));report.AppendLine();

            bool fresh=activity!=null&&activity.ObservedAt>0&&activity.ObservedAt<=now+5&&now-activity.ObservedAt<=5;
            Add(report,"监测报告",activity==null?"unavailable":fresh?"fresh":"stale-or-invalid");
            Add(report,"监测可用性",!fresh?"unknown":activity.MonitoringUnavailable?"unavailable":"available");
            Add(report,"任务状态",ActivityState(activity,fresh,now));
            Add(report,"运行任务数",!fresh?"unknown":Count(activity.ActiveTasks,10000));
            Add(report,"待确认任务数",!fresh?"unknown":Count(activity.UncertainTasks,10000));
            Add(report,"本轮完成事件数",!fresh?"unknown":Count(activity.CompletedTasks,10000));report.AppendLine();

            Add(report,"额度来源",quota==null?"unavailable":quota.Origin=="在线查询"?"online":quota.Origin=="日志快照"?"log":"unknown");
            Add(report,"额度状态",QuotaState(quota,now));
            report.AppendLine();report.AppendLine("报告未包含用量金额、Token 数量、额度百分比、个人路径、账号标识、模型名称或日志内容。");
            return report.ToString();
        }
        private static void Add(StringBuilder report,string label,string value){report.Append(label).Append(": ").AppendLine(value);}
        private static string Flag(bool value){return value?"on":"off";}
        private static string Pick(string value,params string[] choices)
        {foreach(string choice in choices)if(String.Equals(value,choice,StringComparison.Ordinal))return choice;return "unknown";}
        private static string Number(double value,double minimum,double maximum)
        {return Double.IsNaN(value)||Double.IsInfinity(value)||value<minimum||value>maximum?"unknown":value.ToString("0.##",CultureInfo.InvariantCulture);}
        private static string Count(int value,int maximum){return value<0||value>maximum?"unknown":value.ToString(CultureInfo.InvariantCulture);}
        private static string Form(Preferences value)
        {
            if(value==null)return "unknown";if(!value.BallMode)return value.Collapsed?"main-compact":"main";
            if(!String.IsNullOrEmpty(value.BallDock))return Pick(value.BallDock,"left","right","top","bottom")=="unknown"?"unknown":"docked";
            return value.BallStyle=="capsule"?(value.BallExpanded?"capsule-expanded":"capsule-small"):Pick(value.BallStyle,"orb","html");
        }
        private static string ActivityState(ActivityReport report,bool fresh,long now)
        {
            if(!fresh||report.ActiveTasks<0||report.ActiveTasks>10000||report.UncertainTasks<0||report.UncertainTasks>10000)return "unknown";
            if(report.ActiveTasks>0&&report.Until>now&&report.Until<=now+900)return "running";
            if(report.MonitoringUnavailable)return "unknown";
            return report.ActiveTasks>0||report.UncertainTasks>0?"uncertain":"idle";
        }
        private static string QuotaState(QuotaBucket quota,long now)
        {
            if(quota==null)return "unavailable";
            if(quota.ObservedAt<=0||quota.ObservedAt>now+5)return "invalid-time";
            if(!quota.IsFresh(now))return "stale";
            if(quota.Primary!=null&&quota.Primary.RemainingPercent(quota,now).HasValue||quota.Secondary!=null&&quota.Secondary.RemainingPercent(quota,now).HasValue)return "fresh";
            return "awaiting-valid-sample";
        }
    }
}
