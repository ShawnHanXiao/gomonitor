# GoMonitor

GoMonitor 是一个 Windows 托盘常驻工具，用于监控 [OpenCode Go](https://opencode.ai) 的 API 用量，并内置本地反向代理，解决 Trae IDE 直连 OpenCode Go 时缺失 `x-opencode-session` 会话头导致的 400 错误。

![GoMonitor 图标](design/icon-bolt-trio.svg)

## 功能特性

### 用量监控

- 每 60 秒（可配置）轮询官方用量接口 `GET https://opencode.ai/zen/go/v1/usage`
- 托盘图标实时展示 **5 小时 / 每周 / 每月** 三个窗口期的用量等级
- 鼠标 hover 显示一行紧凑摘要：`5h 13.0% | Week 12.0% | Month 8.0% | Off-Peak`
- 左键单击弹出无边框深色用量面板：百分比、进度条、重置倒计时、Refresh、Open Console
- 异常状态（无 Key、网络失败、解析失败）保留上次数据并以灰色图标 + 错误摘要提示

### 托盘图标

图标为蓝色渐变圆角底上的三道光泽闪电，从左至右依次对应 **5 小时 / 每周 / 每月**，每道闪电的颜色代表对应窗口期的用量等级：

| 颜色 | 含义 |
| --- | --- |
| 🔵 蓝色 `#3FC6F5` | 使用量 `< 60%` |
| 🟡 黄色 `#FFDD2E` | 使用量 `60% – 85%` |
| 🔴 红色 `#F5483F` | 使用量 `≥ 85%` |
| ⚪ 灰色 `#8B949E` | 错误 / 无 Key / 不可用 |

图标由 `System.Drawing` 在运行时绘制（源码见 `src/GoMonitor/Services/TrayIconController.cs`），设计与预览资源位于 [design/](design/) 目录（含 [preview.html](design/preview.html) 全状态预览页）。

### OpenCode 本地反向代理

OpenCode 自 2026-09-06 起要求对 `opencode.ai/zen/go/v1/*` 的请求携带 `x-opencode-session` 等会话头，Trae IDE 直连不发送该头，导致 400 错误。GoMonitor 内置代理可透明解决：

- **透明转发**：进程内监听 `127.0.0.1:9355`，自动注入 `x-opencode-session` / `x-opencode-request` / `x-opencode-client` / `x-opencode-project` / `User-Agent` 等会话头
- **会话管理**：基于 `SHA256(system prompt + 首条 user 消息)` 稳定哈希识别会话，同一对话保持同一 session ID 以保留提示词缓存（缓存命中率约 99%）；支持会话空闲过期（默认 6 小时）与手动 "New Session" 重置
- **模型名重写**：模型 ID 带 `proxy-` 前缀（如 `proxy-glm-5.3-flash`）时自动剥离前缀转发真实模型名，避免 Trae 将官方模型名路由到自己的云端通道
- **Token 统计**：旁路解析响应中的 usage（输入 / 输出 / 缓存 token），按天持久化到 `proxy-usage/YYYY-MM-DD.jsonl`，面板展示今日汇总、按模型分组统计与最近请求
- **模型更新提示**：定期拉取官方模型列表并与本地快照 diff，发现新增 / 下架模型时在面板提示
- **SSE 流式透传**：不整体缓冲响应，客户端断开时联动取消上游请求

### 其他

- 开机自启（写入 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`，以 `--minimized` 参数仅驻留托盘）
- 单实例运行，关闭窗口仅隐藏，通过托盘菜单 `Exit` 退出

## 快速开始

### 环境要求

- Windows 10/11
- .NET 9 SDK（构建 / 开发）；运行发布产物则无需安装运行时

### 构建与测试

```powershell
git clone https://github.com/ShawnHanXiao/gomonitor.git
cd gomonitor
dotnet build GoMonitor.sln
dotnet test GoMonitor.sln
```

### 发布单文件 exe

```powershell
dotnet publish src/GoMonitor/GoMonitor.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true `
  -o publish
```

产物为 `publish/GoMonitor.exe`（约 74 MB 自包含单文件，可独立运行）。

## 认证与配置

GoMonitor 按以下优先级获取 API Key：

1. 设置窗口中手动填写的 Key 覆盖值
2. OpenCode 本机认证文件 `~/.local/share/opencode/auth.json` 中的 `opencode-go.key`

所有数据文件均位于 `%APPDATA%\GoMonitor\`：

| 文件 | 说明 |
| --- | --- |
| `settings.json` | 应用设置（轮询间隔、开机自启、代理开关 / 端口 / UA、会话空闲时长等） |
| `known-models.json` | 官方模型列表快照（用于更新提示 diff） |
| `proxy-usage/YYYY-MM-DD.jsonl` | 代理每日请求记录（默认保留 90 天） |
| `proxy-errors.log` | 代理请求失败详情（含内部异常） |

## 使用 OpenCode 代理

1. 托盘右键菜单 → `Settings`，启用 Proxy（或使用菜单 `Proxy: Off/Running` 快捷开关）
2. 在 Trae 中将 OpenCode 自定义模型配置为：

   - **Base URL**：`http://127.0.0.1:9355/zen/go/v1`
   - **Model ID**：`proxy-<真实模型名>`，例如 `proxy-glm-5.3-flash`
   - **API Key**：可留空（代理自动补上本机 `auth.json` 中的 key）

3. 正常对话即可；面板下方可查看今日 Token 统计与缓存命中率
4. 需要强制开启新会话时使用托盘菜单 `New Session`

> 代理仅监听本机回环地址 `127.0.0.1`，不会暴露到局域网。日志与统计不记录 prompt 内容与 API Key 明文。

## 项目结构

```text
gomonitor/
  docs/                     # 设计文档（需求、架构、代理方案）
  design/                   # 托盘图标设计资源与预览页
  src/GoMonitor/
    Models/                 # 用量、设置、代理数据模型
    Services/               # 用量轮询、托盘绘制、代理、会话、统计、模型目录
    Views/                  # 用量面板与设置窗口
  tests/GoMonitor.Tests/    # xUnit 单元测试（解析、阈值、会话、代理端到端等）
```

## 开发

```powershell
dotnet build GoMonitor.sln   # 构建
dotnet test GoMonitor.sln    # 运行全部单元测试（xUnit）
```

主要模块：

| 模块 | 职责 |
| --- | --- |
| `OpenCodeUsageService` | 轮询官方用量接口，输出 `UsageSnapshot` |
| `TrayIconController` | 运行时绘制托盘图标、tooltip、托盘菜单 |
| `OpenCodeProxyService` | HttpListener 代理：头注入、模型重写、流式透传、TLS 重试 |
| `SessionRegistry` | 会话哈希 → ID 映射、空闲过期、手动重置 |
| `ProxyUsageRecorder` | usage 解析（JSON / SSE）、jsonl 落盘与聚合 |
| `ModelCatalogService` | 官方模型列表拉取与新增 / 下架 diff |

## 相关文档

- [00-需求与设计方案](docs/00-需求与设计方案.md)
- [01-技术栈与架构](docs/01-技术栈与架构.md)
- [02-opencode-proxy设计方案](docs/02-opencode-proxy设计方案.md)

## 致谢

- [opencode-go-proxy-for-trae](https://github.com/LIMTCYT/opencode-go-proxy-for-trae) —— 会话头注入方案参考
- [Trae 论坛：OpenCode 会话头讨论](https://forum.trae.cn/t/topic/180164) —— 验证了头注入可保留提示词缓存
