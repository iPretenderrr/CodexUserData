# 开发与项目备份

## 目录

| 文件 | 用途 |
| --- | --- |
| src/*.cs、src/widget.manifest、src/build.ps1 | WPF 应用源码及构建入口；图标由构建脚本生成 |
| tools/ReleaseProbe.cs、tools/HtmlReleaseProbe.cs | 针对实际混淆产物的回归验证，使用独立演示数据 |
| tools/verify-source.ps1、tools/*StabilityProbe.cs、tools/FakeQuotaHelper.cs | 源码集成回归与模拟截图；不访问真实日志、账号或在线额度 |
| restore-dependencies.ps1 | 从 NuGet 恢复固定版本依赖，并验证 SHA-256 |
| pack.ps1、obfuscation.rules.xml | 编译、混淆、验证并生成可分发程序包 |
| CodexUserData.exe.config、启动.cmd | 公共运行配置与相对路径启动入口 |
| docs、examples/aurora | 使用文档、接口与演示形态 |

## 构建与打包

Windows x64 上使用 Windows PowerShell 5.1 和 .NET Framework 4.8。首次执行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\restore-dependencies.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\src\build.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\pack.ps1
```

恢复脚本下载固定版本 Microsoft.Web.WebView2 1.0.3296.44、Obfuscar 2.2.50，以及 tools/remote-packages.json 中锁定 SHA-256 的 SSH.NET 2026.0.0 和运行依赖，不需要开发者的安装路径、账号或配置。缓存完整时可以用 `-Offline` 校验并恢复。版本或哈希不一致会停止。

普通构建在根目录生成 EXE 和WebView2 与 SSH.NET 运行依赖 DLL，不启动程序。`pack.ps1` 使用独立 `.build` 目录重新构建并运行两组验证，需要已安装 WebView2 Runtime 的交互式 Windows 桌面；不会覆盖正在运行的根目录 EXE。成功后在 `dist` 生成 protected ZIP 与 SHA-256 文件。程序包包含 MIT 许可证、第三方声明和使用文档。

日常修改后可先运行源码回归：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\verify-source.ps1
```

回归在 `.build/source-verification` 创建独立模拟用户目录，覆盖窗口切换、HTML 显隐、日志增量读取、配置恢复、监测状态、图表与诊断等边界。需要可创建 WPF / WebView2 窗口的桌面会话；受限环境的浏览器启动失败不等同于产品功能失败。模拟截图输出到同目录的 `guide-images`，不得替换为真实用户截图。

发布前同步 `src/Program.cs` 的程序集版本、`pack.ps1` 的默认版本以及用户文档，再运行 `pack.ps1`。脚本会拒绝程序版本与包名版本不一致的产物。自动回归不能替代实体多屏热拔插、不同显示缩放和长期运行检查。

`pack.ps1` 使用带 BOM 的 UTF-8，以便 Windows PowerShell 5.1 正确读取白名单中的中文文档名；修改时请保留该编码。

源码公开后，混淆只是保留的发行处理流程，不用于隐藏公开源码。产物可能因编译工具和混淆过程而不同，不保证逐字节可复现。

## 备份范围与隐私

Git 仓库备份代码、脚本、文档与示例，依赖可重新下载。仓库不备份个人账号、使用记录或软件设置。需要保留个人设置时，请另外私下备份 `%LOCALAPPDATA%\CodexUserData`，不要放入公开仓库。

`.gitignore` 采用明确文件白名单。新增源码文件时，要同时检查内容、更新忽略白名单和 `src/build.ps1` 的源码列表，避免新代码遗漏。不要通过 `git add -f` 绕过个人数据排除规则。

提交前运行 `git status --short` 和 `git diff --cached`，确认只有预期代码与文档。不要提交 `.env`、用户设置、数据库、原始会话、浏览器缓存、真实截图、发布历史或混淆映射。手册截图使用演示数据；新增截图也需要脱敏。

提交记录会包含作者名称和邮箱，请使用可公开署名以及 GitHub 提供的 noreply 邮箱。忽略规则不会清理已经提交的文件和历史，也不能阻止网站手动上传私人文件。

## 额度历史验证

`tools/verify-source.ps1 -QuotaOnly` 使用模拟查询与 180 天分钟记录验证持久化、重复采样、损坏尾行、保留期限、缺失时段、后台读取及曲线缓存。实际在线额度沿用一分钟查询，按 UTC 日保存到用户目录的 `quota-history`；按 Codex 数据目录的 SHA-256 摘要分目录，不保存路径原文或认证内容。历史读取、分组与曲线预处理在后台，日期缓存按文件长度、修改时间和身份复用，并按有上限的 LRU 淘汰；关闭图表后不主动读历史。


## 远程相关验证

`tools/verify-source.ps1 -RemoteOnly` 运行远程合并、增量、连接恢复及现有数据/任务状态检查；只用模拟日志。

实际 SFTP 协议检查：在 `.build/sftp-fixture-deps` 安装测试依赖 `paramiko==4.0.0`，运行 `tools/verify-sftp.ps1 -Python <python.exe>`。临时服务器只监听 127.0.0.1，密钥和日志随机生成在 `.build`，完成后停止。不要提交该目录。

`tools/remote-packages.json` 是发布依赖的唯一清单，恢复不依赖 dotnet SDK。新增依赖同时检查运行时绑定、第三方声明及程序包白名单。

## 周期切换与性能验证

`tools/verify-source.ps1 -PeriodOnly` 只运行周期缓存、统计投影、异步绘图和现有用量图检查。改动日志读取时另运行 `-RemoteOnly`；改动额度历史时另运行 `-QuotaOnly`。

`tools/measure-periods.ps1` 在 `.build` 生成 180 天双额度分钟记录，对比首次读取和反复切换；不读取用户日志。计时仅表示模拟数据在当前设备上的耗时。日缓存采用有上限的 LRU；轻量档降低样本上限。当前日期追加读取保留完整行检查点，截断或替换时重建对应日期。

首页切周期使用界面持有的只读统计，即使后台正在保存缓存也可切换。统计按日志版本、价格、本地小时边界及未来记录时间失效；本机与远程合并复用同一份数字记录。

`pack.ps1 -PeriodOnly` 保留编译、混淆、版本、程序包白名单和专门的受保护周期检查，适用于仅涉及周期/统计的补丁。它不运行悬浮球和 HTML 检查；涉及对应功能的改动仍应使用匹配的检查或完整打包流程。

## 模型配色维护与验证

使用 `tools/verify-source.ps1 -ColorOnly` 和 `pack.ps1 -ColorOnly` 定向验证配色。`model-palette.json` 是公开统一编号表，编译时嵌入程序作为离线基线；字段名属于持久化协议，混淆时保留。Astra 蓝、Sol 橙黄、Terra 绿、Luna 红粉、其他 GPT-5 靛紫，自动审核灰。每个系列的 `models` 数组下标即固定编号，跨大小版本和变体统一计数；只能追加，不得插入或重排。基础型号的缺省小版本补 `.0`，补丁零合并；`6.10` 与 `6.1` 分别解析。提供商前缀只在最后一段明确以 `gpt-` 开头时归一。

每族 `dark` / `light` 各十色。编号 `n` 使用 `n % 10` 的基础色和 `rounds[n / 10]` 的固定 OKLab 向量。`node tools/build-model-palette.js` 默认只验证既有色板并输出预览；`--generate`、`--refine`、`--pair` 仅用于离线设计，已有正式色板时将候选写入 `.build/palette-design/model-palette.candidate.json`，不会覆盖正式文件。运行时不做搜索，只解析和缓存。正式发布后的基础色、已有编号和轮次偏移保持不变；新轮次和模型在提高 `revision` 后追加。改变算法或已有配色必须另行设计显式迁移，不能伪装为普通追加更新。

配置中的全部轮次均验证色域、RGB 量化后的重复、色系、参考背景对比度至少 4.5，以及每个滑动十色窗口的最小 OKLab 距离至少 0.05；等价于检查每两个相邻轮次全部九个跨界窗口。常用模型组合在冻结编号前单独目视检查。数值检查不代替真实线宽、字号和曲线交叉区域的视觉检查。

配色配置从本仓库固定 HTTPS 地址下载，不发送模型列表或日志。启动和每 24 小时后台检查，设置里的“检查更新”可强制检查；串行下载、限时和响应大小限制防止重复开销。验证完整配置及追加关系后原子写入 `model-palette-cache-v1.json` 并保留备份，在一次 UI 调度内更新已有共享画刷；增加 `Theme.Revision` 使冻结的图表绘制缓存失效，不重读日志。校验或网络失败继续使用有效配置。私有旧文件 `model-colors.json` 不再参与配色，也不会删除或改写。

大面积堆叠柱、矩形图及其排名色点使用 `ModelColors.FillFor`：以深色主题的明亮基础色派生更饱满的填充，保持 OKLab 色相，超出显示色域时降低色度而不截断 RGB。灰色仍随主题变化，文字和细曲线保留 `For` 的可读性配色。派生结果按型号缓存，配置或主题更新时原位刷新画刷，不逐帧计算；不改动公开编号和冻结的基础色板。`-ColorOnly` 同时验收现有轮次的填充色跨轮十色间距。

尚未登记的模型用规范化名称计算稳定备用色，不写本地分配表；正式编号加入后可能调整一次。跨电脑一致限定相同主题和配置版本；跨很多轮次任意挑选的模型仍可能颜色接近。发布新配置前，应同步运行颜色专项，并保持旧编号/色板/轮次不可变。

## 模型占比与里程碑验证

模型占比的堆叠柱与矩形树图由 `ModelShareChart` 单一绘制控件承载，复用同页范围读取和模型画刷。新视图测试 `ModelShareViewsStabilityProbe` 纳入 `-ModelShareOnly`：覆盖密集序列合并的 Token 守恒、累计端点、树图面积、点击与悬停分离、视图切换不读盘、窄布局及来源切换。柱图用当前桶内原始 Tokens 计算百分比；矩形图只使用所选区间的原始总量，日周累计控制在此视图隐藏。压缩柱的明细对应实际显示的合并区间，不伪装为单日。

模型占比页改动使用 `tools/verify-source.ps1 -ModelShareOnly`，覆盖百分比分母、无用量断点、范围与来源切换、异步取消、宽窄布局、原有图表回归以及共享范围读取的集成。`pack.ps1 -ModelShareOnly` 验证混淆后的占比页，不启动无关的 HTML 形态测试。

Token/费用趋势复用同一绘图组件的 `SetUsageData`，柱图使用实际数值轴，模型占比页继续使用 100% 轴。费用以 decimal 保留，不转为整数 Tokens；矩形图始终使用原始区间总量，不累加累计节点。`HistoryPanel.SetTrendView` 仅切换内存数据的展示，矩形图隐藏并保留原来的日/周/累计选项；密集柱采用相同压缩与选择校验。`UsageViewsStabilityProbe` 随图表专项执行，检查微小费用、价格刷新、累计压缩、模型筛选和窄布局；受保护包专项也检查用量页视图。

`tools/verify-source.ps1 -MilestoneOnly` 使用独立模拟日志和数据库验证五档分段、未知耗时、归档日期范围、缓存重建、来源切换与页面生命周期，并生成深浅主题截图。共用去重及远程账本的改动另外运行 `-RemoteOnly`。数值缓存存于用户数据目录的 `milestones` 子目录，不保存对话内容；缓存损坏或历史变化时可重新生成。

累计曲线位于图表窗口的独立“累计里程碑”子页，主页与原有用量图表不持有该控件。通过同一份有效历史生成内存数值索引，日期范围不重置基线；索引不重复写入磁盘。里程碑曲线的节点压缩、独立范围控件、来源切换与页面生命周期纳入 `-MilestoneOnly`，已有日/周、费用、模型筛选的回归使用 `-PeriodOnly`。鼠标交互仅重绘选择层。

`pack.ps1 -MilestoneOnly` 验证混淆产物中的分段计算、缓存重启、设置兼容和图表页加载，保留标准的程序包文件白名单。仅适用于本次统计页改动，不替代其他功能相应的检查。
