using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CodexUserData
{
    internal static class DiagnosticStabilityProbe
    {
        private static void Check(bool pass,string name){StabilityProbe.Check(pass,"Diagnostics: "+name);}
        internal static void Run(string root)
        {
            const string sentinel="PRIVATE_SENTINEL_7F4C\r\ninjected=unreviewed";
            var preferences=new Preferences();
            // Reflection is confined to fixture poisoning. Production exports never enumerate
            // preferences: future added string settings must remain private without extra work.
            foreach(var property in typeof(Preferences).GetProperties().Where(p=>p.PropertyType==typeof(string)))property.SetValue(preferences,sentinel,null);
            preferences.BallMode=true;preferences.BallMonitor=sentinel;
            preferences.KnownModels=new[]{sentinel};preferences.Metrics=new[]{sentinel};preferences.GradientColors=new[]{sentinel};preferences.OrbShortColors=new[]{sentinel};preferences.OrbLongColors=new[]{sentinel};
            preferences.PriceOverrides=new Dictionary<string,decimal[]>{{sentinel,new[]{1m,2m,3m,4m}}};
            preferences.BallPlacements=new Dictionary<string,BallPlacement>{{sentinel,new BallPlacement{Dock=sentinel,X=.25,Y=.5}}};
            preferences.CustomShapeSizes=new Dictionary<string,double[]>{{sentinel,new[]{250d,180d}}};
            preferences.AnimationSpeed=Double.NaN;preferences.BallOpacity=Double.PositiveInfinity;preferences.RefreshSeconds=Int32.MinValue;
            var usage=new UsageSnapshot{Warning=sentinel,SourceName=sentinel,CountLabel=sentinel,LatestRecord=sentinel,KnownModels=new[]{sentinel},CoverageFiles=3,CoverageWarnings=2,
                TotalTokens=9876543210123456L,CostUsd=98765.4321m,EquivalentUsd=98765.4321m};
            usage.Models.Add(new ModelUsage{Model=sentinel,Effort=sentinel});
            long now=LocalCodexUsage.Unix(DateTime.UtcNow);
            var activity=new ActivityReport{ObservedAt=now,ActiveTasks=1,Until=now+4,CompletionSerial=9876543210123456L};
            var quota=new QuotaBucket{Id=sentinel,Name=sentinel,Origin=sentinel,ObservedAt=now,Primary=new QuotaWindow{Minutes=300,UsedPercent=87.654321,ResetsAt=now+300}};
            string report=Diagnostics.Create(preferences,usage,activity,quota);
            Check(!report.Contains("PRIVATE_SENTINEL")&&!report.Contains("injected="),"diagnostic allowlist rejects all poisoned setting and snapshot strings");
            Check(!report.Contains("9876543210123456")&&!report.Contains("98765.4321")&&!report.Contains("87.654321"),"diagnostics omit tokens, cost, quota percentages and completion serial");
            Check(report.Contains("日志文件数: 3")&&report.Contains("统计提示数: 2")&&report.Contains("运行任务数: 1"),"diagnostics retain bounded troubleshooting counts");
            Check(report.Contains("数据来源: unknown")&&report.Contains("主题: unknown")&&report.Contains("界面形态: unknown")&&report.Contains("额度来源: unknown"),"unrecognized choices are mapped to fixed unknown values");
            Check(report.Contains("动画速率: unknown")&&report.Contains("悬浮球透明度: unknown")&&report.Contains("刷新间隔秒: unknown")&&!report.Contains("NaN")&&!report.Contains("Infinity"),"invalid numeric settings never become raw report values");
            Check(report.Length<4096&&!report.Contains(root),"diagnostic report is bounded and excludes the fixture location");
            preferences.Source="local";preferences.Range="week";preferences.ThemeMode="light";preferences.OrbAnimation="eco";preferences.BallDock="";preferences.BallStyle="html";
            preferences.AnimationSpeed=1.25;preferences.BallOpacity=.65;preferences.RefreshSeconds=5;quota.Origin="在线查询";
            report=Diagnostics.Create(preferences,usage,activity,quota);
            Check(report.Contains("数据来源: local")&&report.Contains("统计范围: week")&&report.Contains("主题: light")&&report.Contains("动画预设: eco")&&report.Contains("界面形态: html"),"known safe settings are available for troubleshooting");
            Check(report.Contains("额度来源: online")&&report.Contains("额度状态: fresh")&&report.Contains("任务状态: running"),"diagnostics identify fresh quota and running evidence without their identifiers");
            usage.CoverageFiles=Int32.MaxValue;usage.CoverageWarnings=-1;activity.ActiveTasks=-1;activity.UncertainTasks=Int32.MaxValue;activity.MonitoringUnavailable=true;
            quota.ObservedAt=Int64.MaxValue;report=Diagnostics.Create(preferences,usage,activity,quota);
            Check(report.Contains("日志文件数: unknown")&&report.Contains("统计提示数: unknown")&&report.Contains("运行任务数: unknown")&&report.Contains("任务状态: unknown"),"invalid counters cannot masquerade as valid aggregate state");
            Check(report.Contains("监测可用性: unavailable")&&report.Contains("额度状态: invalid-time"),"unavailable monitoring and invalid quota time are explicit");
            report=Diagnostics.Create(null,null,null,null);
            Check(report.Contains("设置状态: unavailable")&&report.Contains("统计快照: unavailable")&&report.Contains("任务状态: unknown")&&report.Length<4096,"null diagnostic inputs produce a complete bounded report");
            Check(report.Contains("程序版本:")&&report.Contains("系统版本:")&&report.Contains("程序架构:")&&report.Contains("CLR 版本:"),"report includes only requested software and runtime version information");
        }
    }
}
