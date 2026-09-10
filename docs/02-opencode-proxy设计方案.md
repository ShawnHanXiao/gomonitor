# GoMonitor OpenCode 代理设计方案

## 1. 背景

OpenCode 自 2026-09-06 起要求对 `opencode.ai/zen/go/v1/*` 的请求携带 `x-opencode-session` 等会话头，缺失时返回 400（`Request is missing x-opencode-session`）。Trae IDE 直连 OpenCode Go 时不会发送该头，导致无法使用。

社区已有两个可行方案：

- [opencode-go-proxy-for-trae](https://github.com/LIMTCYT/opencode-go-proxy-for-trae)：Python 本地反向代理，注入会话头 + 模型名 `proxy-` 前缀重写。
- [Trae 论坛帖子](https://forum.trae.cn/t/topic/180164)：验证了头注入方案可保留提示词缓存（同会话同 session ID 缓存率约 99%），并指出 session ID 管理不当有被判定滥用的风险。

本方案将代理能力集成进 GoMonitor（当前是纯托盘用量监控工具），同时增加 Token 用量统计与模型更新提示。

## 2. 目标与非目标

### 2.1 目标（v1）

1. **内置反向代理**：GoMonitor 进程内监听 `127.0.0.1:<port>`，透明转发 Trae → OpenCode，自动注入会话头，解决 400 错误。
2. **会话管理**：自动识别会话（同对话稳定同 session ID，保缓存命中）+ 手动强制新会话兜底。
3. **模型名重写**：支持 `proxy-` 前缀别名，剥离后转发真实模型名（防止 Trae 把官方模型名路由到自己的云端通道）。
4. **Token 统计**：旁路解析响应中的 usage 字段（输入/输出 token、缓存命中），按天/按模型持久化，监控面板展示。
5. **模型更新提示**：定期拉取官方模型列表，与本地已知列表对比，发现新增/下架模型时在面板提示。

### 2.2 非目标（v1 不做）

- 协议转换（OpenAI Chat Completions ↔ Anthropic Messages / responses 协议）。v1 只做透传，遇到协议不兼容的模型在面板提示，转换放 v2。
- 多上游账号、请求重试、限流。
- 历史趋势图表（v1 只做当天汇总 + 最近请求列表）。

## 3. 总体架构

```text
Trae IDE
   |
   |  Base URL: http://127.0.0.1:9355/zen/go/v1
   |  Model ID: proxy-glm-5.3-flash
   v
+----------------------------------------------+
| GoMonitor.exe (WPF 常驻进程)                  |
|  +----------------------------------------+  |
|  | OpenCodeProxyService (HttpListener)    |  |
|  |  1. 解析请求路径/模型名                  |  |
|  |  2. 会话识别 + 头注入                    |  |
|  |  3. SSE 流式透传                        |  |
|  |  4. 旁路解析响应 usage -> 统计           |  |
|  +-------------------|--------------------+  |
|                      v                       |
|  SessionRegistry / UsageStatsStore /        |
|  ModelCatalogService                         |
+----------------------|-----------------------+
                        v
          https://opencode.ai/zen/go/v1/*
          （注入 x-opencode-session 等头）
```

- 代理嵌入 GoMonitor 主进程（已确认），托盘右键菜单可启停。
- 监听地址固定 `127.0.0.1`，只接受本机回环连接。
- 复用现有 `AuthKeyProvider`：请求未带 Authorization 时由代理补上本机 key（Trae 配置了 key 则透传优先）。

## 4. 请求头注入

每次转发前注入（参考 Python 版实现）：

| Header | 值 | 说明 |
| --- | --- | --- |
| `x-opencode-session` | 会话 ID，如 `AB12CD34` | 8 位随机串（数字+大小写字母），同一会话保持稳定 |
| `x-opencode-request` | `msg_<n>` | 同一会话内请求序号递增 |
| `x-opencode-client` | `cli` | 伪装官方客户端类型 |
| `x-opencode-project` | `global` | 项目标识 |
| `User-Agent` | `opencode/1.18.29 cli`（可配置） | 与官方客户端一致 |

已存在的同名头以客户端显式发送的为准（不覆盖），其余强制注入。

## 5. 会话识别策略（已确认：自动识别 + 手动重置）

会话 ID 解析优先级：

1. **客户端显式会话标识**：请求头或请求体中的 session 字段（若 Trae 未来支持）。
2. **内容哈希**：`SHA256(system prompt + 首条 user 消息)` 前 8 字节映射为 ID。同一对话 system+首问不变，故 session ID 稳定；新对话自动产生新 ID。
3. **随机临时会话**：无法解析 body 时（如非 JSON 请求），使用当前活跃会话或新建临时 ID。

补充规则：

- **空闲过期**：某会话超过 `sessionIdleHours`（默认 6h，可配置）无请求后失效，再次出现同哈希请求时生成新 ID，降低长期复用被判滥用的风险。
- **手动重置**：面板提供 "New Session" 按钮，清空哈希→ID 映射；下一个请求即开新会话。用于"每次开新对话"但哈希恰好相同的极端情况，以及主动规避风控。
- 会话表仅存内存（哈希、ID、首次/最后活跃时间、请求计数），不落盘；重启后自然重建。

## 6. 模型名重写

- Trae 自定义模型 ID 必须带 `proxy-` 前缀（如 `proxy-glm-5.3-flash`）。
- 代理在转发前剥离前缀还原真实模型名：
  - `chat/completions` 等请求体 JSON：改写 `model` 字段。
  - 请求路径中含模型名的（如有）：改写路径段。
- 无前缀的模型名原样透传（允许用户直连测试）。
- 面板展示前缀说明与推荐配置，降低配置出错率。

## 7. 流式透传与旁路统计

### 7.1 透传

- 使用 `HttpListenerResponse` + 流式拷贝（`Stream.CopyToAsync`，8KB 缓冲），不整体缓冲响应。
- 透传上游状态码、`Content-Type` 等安全头（剥离 `Transfer-Encoding`、`Content-Length` 由监听器重新计算）。
- 客户端断开时取消上游请求（`CancellationToken` 联动）。

### 7.2 usage 旁路解析（不影响转发）

- 对 `application/json` 响应：缓冲完整 body 后解析 `usage` 字段再回写客户端。
- 对 `text/event-stream`：逐 chunk 扫描 `data:` 行，正则提取 `usage` / `usage` 节点（OpenAI 格式最后 chunk、Anthropic 格式 `message_start`/`message_delta` 事件），同时原样转发。
- 解析失败不影响转发，静默降级为"本次未统计"。

## 8. Token 统计（已确认：持久化 + 面板展示）

每次请求记录一条：

```csharp
public sealed record ProxyRequestRecord(
    DateTimeOffset Timestamp,
    string SessionId,
    string Model,            // 重写后的真实模型名
    int InputTokens,
    int OutputTokens,
    int CacheReadTokens,
    int CacheWriteTokens,
    bool HasError,
    string? ErrorMessage);
```

- **存储**：`%APPDATA%\GoMonitor\proxy-usage\YYYY-MM-DD.jsonl`，按天一个文件，追加写入。超过 90 天的文件启动时清理（可配置）。
- **聚合**：内存中维护当天聚合（按模型分组的请求数 / token / 缓存命中），启动时从当天文件恢复。
- **面板展示**：MonitorWindow 新增 "Proxy" 区块：
  - 今日汇总：请求数、输入/输出 token、缓存命中率（一行紧凑文本）。
  - 按模型分组的小型表格（模型名、请求数、token）。
  - 最近 5 条请求（时间、模型、token、错误标记）。
  - 缓存命中率 = `cacheRead / (cacheRead + input)`，口径与官方控制台一致便于对照验证。

## 9. 模型更新提示

- 数据源：`GET https://opencode.ai/zen/go/v1/models`（实测无需鉴权，返回 `{"object":"list","data":[{"id":...,"created":...}]}`）。
- `ModelCatalogService` 每次刷新用量时顺带拉取（复用 60s 轮询，频率足够），与 `%APPDATA%\GoMonitor\known-models.json` 中上次快照 diff：
  - **新增模型**：面板顶部显示提示条 `New models available: xxx, yyy`，同时提供 "Copy IDs" 快捷操作；提示可一键忽略（更新快照）。
  - **下架模型**：灰字提示 `Removed: xxx`。
- 首次运行无快照时静默建立基线，不提示。

## 10. UI 与设置改动

### 10.1 设置窗口新增字段

| 字段 | 默认值 | 说明 |
| --- | --- | --- |
| `proxyEnabled` | `false` | 代理开关，保存后立即生效 |
| `proxyPort` | `9355` | 监听端口，占用时报错提示 |
| `proxyUserAgent` | `opencode/1.18.29 cli` | 注入的 UA |
| `sessionIdleHours` | `6` | 会话空闲过期时间 |
| `upstreamProto/Host` | `https` / `opencode.ai` | 高级字段，折叠显示 |

### 10.2 托盘菜单

右键菜单新增：`Proxy: Start/Stop`（显示当前状态）、`New Session`。

### 10.3 MonitorWindow

在现有三行用量下方增加 Proxy 区块（第 8 节）与模型提示条（第 9 节），保持现有紧凑风格，代理未启用时显示一行 `Proxy off - enable in Settings`。

## 11. 配置与持久化汇总

```text
%APPDATA%\GoMonitor\
  settings.json          # 现有 + 第 10.1 节新增字段
  known-models.json      # 模型快照（id 列表 + 更新时间）
  proxy-usage\
    2026-09-10.jsonl     # 每日请求记录
```

## 12. 代码结构

```text
src/GoMonitor/
  Services/
    OpenCodeProxyService.cs     # HttpListener 监听、转发、头注入、流式拷贝
    SessionRegistry.cs          # 会话哈希 -> ID 映射、请求计数、空闲过期
    ProxyUsageRecorder.cs       # usage 解析（含 SSE chunk 扫描）、jsonl 落盘、聚合
    ModelCatalogService.cs      # /models 拉取与 diff
  Models/
    ProxyModels.cs              # ProxyRequestRecord、ModelInfo、代理设置项
  Views/
    MonitorWindow.*             # 新增 Proxy 区块与模型提示条
    SettingsWindow.*            # 新增代理设置字段
tests/GoMonitor.Tests/
  SessionRegistryTests.cs       # 哈希稳定性、空闲过期、手动重置
  ProxyHeaderTests.cs           # 头注入与模型名重写
  UsageScanTests.cs             # OpenAI/Anthropic 两种 usage 解析
  ModelDiffTests.cs             # 新增/下架 diff
```

## 13. 边界与异常

- **端口被占用**：设置保存时报错，代理保持停止状态。
- **上游不可达/超时**：向客户端回 502 与上游错误摘要（与 Python 版 `[upstream-error]` 行为一致），同时在面板最近请求中标红。
- **HTTPS 证书**：上游为公网 `https`，代理与上游之间用 `HttpClient`（`SslStreams` 由框架处理），客户端到代理是纯 HTTP 回环，无需自签证书。
- **请求体不可读**（非 JSON/无 body）：跳过哈希识别与模型重写，直接透传。
- **代理停止时**：Trae 请求直接连接失败，面板提示检查代理状态。
- **隐私**：日志与统计不记录 prompt 内容与 API key 明文；请求记录只含模型名/token 数。

## 14. 测试计划

- 单元测试（见第 12 节文件）：
  - 会话哈希对同一 (system, first-user) 输入稳定；不同输入不同。
  - `proxy-` 前缀剥离、无前缀透传、路径与 body 两处改写。
  - 头注入不覆盖客户端已有头；`x-opencode-request` 递增。
  - OpenAI SSE 最后 chunk usage、Anthropic `message_start` usage、非流式 JSON usage 三种解析。
  - 模型 diff：新增、下架、无变化。
- 手动冒烟：
  - Trae 配置 `http://127.0.0.1:9355/zen/go/v1` + `proxy-` 前缀模型后可正常对话，无 400。
  - 同一对话多轮后，官方控制台"使用历史"中 session ID 与面板一致，缓存命中非零。
  - 断网/填错 key 时面板错误状态正常。
  - 官方上线新模型后面板出现提示条。

## 15. 实施阶段

| 阶段 | 内容 | 交付物 |
| --- | --- | --- |
| P1 | 代理核心：监听、头注入、模型重写、流式透传 | Trae 可正常对话，400 消失 |
| P2 | 会话管理：哈希识别、空闲过期、手动重置、面板展示 | 缓存命中率可验证 |
| P3 | Token 统计：旁路解析、落盘、面板区块 | 今日汇总可见 |
| P4 | 模型更新提示 + 设置窗口/托盘菜单改造 | 全功能闭环 |

每阶段独立可验收，P1 完成即可解决邮件中的 400 问题。

## 16. v2 候选

- 协议转换层（OpenAI ↔ Anthropic Messages / responses），覆盖 muse 等特殊协议模型。
- 历史趋势图、按模型周/月统计。
- 多上游配置、请求级重试与故障切换。
