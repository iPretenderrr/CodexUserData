# 开发与项目备份

## 目录

| 文件 | 用途 |
| --- | --- |
| src/*.cs、src/widget.manifest、src/build.ps1 | WPF 应用源码及构建入口；图标由构建脚本生成 |
| tools/ReleaseProbe.cs、tools/HtmlReleaseProbe.cs | 针对实际混淆产物的回归验证，使用独立演示数据 |
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

恢复脚本仅下载固定版本 Microsoft.Web.WebView2 1.0.3296.44 和 Obfuscar 2.2.50，不需要开发者的安装路径、账号或配置。缓存完整时可以用 `-Offline` 校验并恢复。版本或哈希不一致会停止。

普通构建在根目录生成 EXE 和三个 WebView2 DLL，不启动程序。`pack.ps1` 使用独立 `.build` 目录重新构建并运行两组验证，需要已安装 WebView2 Runtime 的交互式 Windows 桌面；不会覆盖正在运行的根目录 EXE。成功后在 `dist` 生成 protected ZIP 与 SHA-256 文件。程序包包含 MIT 许可证、第三方声明和使用文档。

源码公开后，混淆只是保留的发行处理流程，不用于隐藏公开源码。产物可能因编译工具和混淆过程而不同，不保证逐字节可复现。

## 备份范围与隐私

Git 仓库备份代码、脚本、文档与示例，依赖可重新下载。仓库不备份个人账号、使用记录或软件设置。需要保留个人设置时，请另外私下备份 `%LOCALAPPDATA%\CodexUserData`，不要放入公开仓库。

`.gitignore` 采用明确文件白名单。新增源码文件时，要同时检查内容、更新忽略白名单和 `src/build.ps1` 的源码列表，避免新代码遗漏。不要通过 `git add -f` 绕过个人数据排除规则。

提交前运行 `git status --short` 和 `git diff --cached`，确认只有预期代码与文档。不要提交 `.env`、用户设置、数据库、原始会话、浏览器缓存、真实截图、发布历史或混淆映射。手册截图使用演示数据；新增截图也需要脱敏。

提交记录会包含作者名称和邮箱，请使用可公开署名以及 GitHub 提供的 noreply 邮箱。忽略规则不会清理已经提交的文件和历史，也不能阻止网站手动上传私人文件。
