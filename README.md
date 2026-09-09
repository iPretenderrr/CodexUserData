# CodexUserData

Windows 桌面用量悬浮窗：查看本地 Codex 的 Token 用量、模型分布、历史趋势和账号额度，也支持独立读取 CC Switch 数据。

本项目以 MIT 许可证开源，仓库包含应用源码、构建与打包脚本、使用文档及 HTML 形态示例。

## 下载与运行

在本仓库的 **Releases** 页面下载 `CodexUserData-版本号-win-x64-protected.zip`，完整解压后运行 `CodexUserData.exe`。不要把 GitHub 的 **Code → Download ZIP** 当成程序安装包。

支持 Windows 10 / 11 x64，需要 .NET Framework 4.8。自定义 HTML 形态还需要 Microsoft Edge WebView2 Runtime。本程序为未签名便携版。

## 功能

- 今日与历史 Tokens、模型与思考强度分布、热度图和趋势图。
- 主窗口、大小胶囊、圆环和贴边额度条，可调整透明度、主题与动画。
- 任务活动提示；贴边运行脉冲；完成后持续闪烁，鼠标移入确认。
- 托盘额度速览；可选开机启动或随 Codex 启动。
- 自定义模型价格，按 API 单价估算等效价值。
- 支持导入用户编写的 HTML 形态，右键可返回主界面。

任务状态来自本机日志，可能受到写入延迟、日志缺失和启动时机影响。API 等效价值是估算，不是实际账单；没有价格的项目按 0 估算，不代表免费。

## 使用说明

- [PDF 使用说明书](docs/CodexUserData-使用说明书.pdf)
- [HTML 使用说明书](docs/CodexUserData-使用说明书.html)（下载到本地后用浏览器打开）
- [HTML 形态接口](docs/HTML形态接口.md)
- [Aurora 形态示例](examples/aurora)

说明书中的数值和截图均为演示数据。

## 数据与升级

程序读取使用者自己的本地 Codex 日志或 CC Switch 数据库。在线额度需要本机可用且已登录的 Codex CLI；发布包不附带账号或登录凭据。

设置、价格和缓存保存在当前 Windows 用户的 `%LOCALAPPDATA%\CodexUserData`。同一用户升级后继续读取此目录：退出旧版，再解压运行新版即可。该用户数据目录及旧版 `data` 文件夹属于个人数据，请勿随问题反馈上传。

反馈问题时可提供版本、复现操作和脱敏截图；不要提交原始对话日志、数据库、账号凭据或整个用户数据目录。

第三方声明见 [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt)。

## 从源码构建

需要 Windows 10 / 11 x64、Windows PowerShell 5.1、.NET Framework 4.8。首次恢复依赖需要联网；HTML 形态及打包中的浏览器验证需要 WebView2 Runtime。

在源码根目录执行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\restore-dependencies.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\src\build.ps1
```

然后运行根目录 `CodexUserData.exe`。详细目录说明、验证和打包见 [开发说明](docs/DEVELOPMENT.md)。

## 许可证

本项目代码采用 [MIT License](LICENSE)，版权署名使用 CodexUserData contributors。第三方组件仍遵循各自许可证，见第三方声明。
