# CodexUserData HTML 形态接口 v1

形态使用 HTML、CSS、JavaScript；TypeScript、React、Vue 等需先构建成离线静态文件。无需编写或加载本机 DLL。仅 HTML 形态需要 Microsoft Edge WebView2 Runtime，内置圆环和胶囊无需此依赖。

## 开始

1. 复制 `examples/aurora`，编辑 `index.html`。将所需脚本、图片、字体放在同一文件夹内。
2. 在 `shape.json` 填写 `apiVersion: 1`、名称 `name`、入口 `entry`、`width` 和 `height`（DIP，宽 64–800，高 40–600）。
3. 设置 → 导入 HTML 形态 → 选择 `shape.json` → 保存设置 → 点击主页悬浮球按钮。
4. 导入会复制整个形态目录到当前用户的 `%LOCALAPPDATA%\CodexUserData\skins\<编号>`。后续可直接编辑该副本，右键“重新载入自定义形态”；或修改原件后重新导入。

宿主在任意位置接管右键菜单，页面的 `contextmenu.preventDefault()` 无法替换它。菜单包含圆环、小胶囊、大胶囊、自定义形态、重新载入、形态设置、返回完整窗口和退出。页面有错误时仍可使用菜单。Alt + 鼠标左键拖动为宿主移动手势，不依赖页面脚本；Esc 可返回主界面。贴边后的额度条也保留相同菜单。

## 数据与操作

引用示例里的 `codexuserdata.js`：

```html
<script src="codexuserdata.js"></script>
<script>
const unsubscribe = CodexUserData.subscribe(data => {
  document.querySelector('#tokens').textContent = data.today?.tokenText ?? '—';
});
document.querySelector('#handle').addEventListener('pointerdown', e => {
  if (e.button === 0) CodexUserData.drag();
});
// 从按钮等操作中调用：
// CodexUserData.resize(300, 100);
// CodexUserData.restoreMain();
</script>
```

数据按设置的用量刷新间隔推送；任务状态更新时也推送。页面不要再扫描日志或自行查询账户。隐藏形态时停止界面工作，恢复后读取最新快照。数据不是计费凭证。

| 字段 | 含义 |
| --- | --- |
| `observedAt` | 本次推送 Unix 秒时间 |
| `source` / `theme` | 数据源 local/ccswitch；主题 dark/light/custom |
| `today` | 今日数据；尚未读取到时为 null |
| `today.date`, `tokens`, `tokenText` | 当地日期、数字 Tokens、适于显示的简写 |
| `today.input`, `output`, `cacheRead`, `cacheWrite`, `requests` | 未缓存输入、输出、缓存读写、用量记录数 |
| `today.apiEquivalentUsd` | 按当前价格估算的美元等效价值；没有价格按 0 计入 |
| `today.models[]` | model、effort、tokens、apiEquivalentUsd；按模型与思考强度分组 |
| `quota[]` | minutes、remainingPercent、resetsAt、observedAt |
| `quota[].remainingPercent` | 剩余额度 0–100；未知、过期或快照超过 5 分钟为 null，不能当作 0% 或 100% |
| `activity.activeTasks` / `uncertainTasks` | 检测到活跃/待确认的任务数；日志有延迟，不是服务器运行状态保证 |
| `activity.completedTasks` / `completionSerial` | 本轮检测到的明确完成数 / 本次运行内递增的完成序号。页面按序号去重；首次接收只记录基线，不播放历史完成效果。启动、后台审查、中断和超时不触发完成 |
| `motion` | smooth / eco / off；尊重此设置与 prefers-reduced-motion |

底层协议是 `window.chrome.webview`：宿主发送 `{type:'snapshot', apiVersion:1, data:{...}}`；页面发送 `{type:'ready'}`、`{type:'drag'}`、`{type:'resize',width:300,height:100}` 或 `{type:'restoreMain'}`。不支持其他命令。

## 资源与性能

背景可透明；使用 CSS 圆角、SVG、Canvas 制作任意视觉形态。窗口命中区域仍是矩形。尺寸按 DPI 自动缩放；运行时 resize 不改写配置，下次载入恢复 shape.json 尺寸。

资源仅来自该形态目录，支持 HTML、JS、CSS、JSON、SVG、PNG、JPG、WebP、GIF、WOFF2；单个响应最多 8 MB，导入最多 256 个文件、25 MB、12 层目录，不接受符号链接。将依赖打包到本地，不能使用 CDN、远程请求、iframe、弹窗或下载。不会向页面暴露账号标识、凭据、数据库路径、日志内容或文件读写 API。

仅渲染需要的帧；静止时暂停 requestAnimationFrame，使用 transform/opacity 做动画，节能模式降低粒子数量，关闭动画时显示静态效果。低性能设备可随时切回内置胶囊。

只分享原始形态文件夹，勿分享用户数据目录或旧版的 `data` 文件夹。
