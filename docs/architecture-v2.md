# Muralis Architecture V2 — Blueprint（Phase 0）

> **状态**：Phase 0 交付物，已按审核意见 R1–R7 修订，等待最终批准进入 Phase 1。
> **分支**：`architecture-v2` ｜ **日期**：2026-09-15 ｜ **基线**：v0.2.0（`d6bbe17`）
> **范围**：本文件只描述设计与迁移计划。Phase 0 不修改任何生产代码。

### 修订记录

| 版本 | 内容 |
| --- | --- |
| v0 | 初稿：现状盘点、模块关系、Shell/Surface/Monitor/Scene/Canvas/Widget/Media 设计、Roadmap |
| v1 | 按审核意见修订：**R1** interop 边界改为领域归属规则；**R2** `MonitorIdentity` 与 `MonitorRuntimeInfo` 拆分，Profile 只绑 `StableId`；**R3** Runtime State 必须可删除可重建，新增 Asset Bindings 持久层；**R4** Scene 不是 `BackdropKind`，引入 Scene 编排层；**R5** Canvas/Widget 渲染技术不提前锁定，Phase 1 只实现 Video 内容；**R6** 开放问题 Q1–Q7 已裁决（§15）；**R7** Phase 1 拆为 Commit A–G 且不迁移与 Desktop Shell 无关的 App native 代码 |

---

## 0. 结论摘要

1. **不推倒重来**：v0.2.0 的 App / DesktopHost / Core 三层方向正确；Core 的 providers、下载队列、图库、缓存管线几乎原样保留。
2. **一次机制性重构**：把「每个视频会话自己找 WorkerW、自己建窗、自己重挂载」改为「每进程一个 Desktop Shell 线程 + 一套 Surface 宿主机制 + 内容与生命周期分离」。Phase 1 完成，零行为变更。
3. **项目数不增加**：`Muralis.DesktopHost` 更名扩容为 `Muralis.Desktop`；不建 Widgets / Media / Themes / Personalization / Cloud（未来触发条件见 §13.4）。
4. **单进程**（Q1 裁决）：桌面层随主程序退出；`IDesktopShell` 保持未来可跨进程演进的设计，但不建独立 host 进程。
5. **Interop 领域归属**（R1）：Desktop 只拥有桌面层原生机制（WorkerW / Surface / Monitor / Shell lifecycle / 定位 z 序 DPI / DXGI-D3D）；App 保留自身的 Windows 平台能力（MainWindow、托盘、单实例、文件选择器）。同一机制只允许一个所有者。
6. **配置分层**（R3）：Scene（意图）/ Profile + Asset Bindings（持久用户绑定）/ Runtime State（可删除可重建）三类数据各司其职；删除 Runtime State 不得丢失任何用户配置。
7. **渲染中立**（R5）：Desktop Shell 统一生命周期、定位、DPI、z 序，不统一渲染实现；Canvas / Widget 的渲染技术在 Phase 5 / 6 原型验证后再定。

---

## 1. 现状盘点（基于 v0.2.0 实际代码）

### 1.1 三层结构

| 程序集 | TFM | 依赖 | 内容 |
| --- | --- | --- | --- |
| `Muralis.App`（Muralis.exe） | net10.0-windows10.0.26100（WinUI 3，unpackaged） | Core + DesktopHost + WindowsAppSDK | MVVM、DI（`AppHost`）、8 个服务、4 处 P/Invoke 文件 |
| `Muralis.DesktopHost` | net10.0-windows10.0.26100（**无 WinUI**） | Core | `DesktopHostSession`（线程 + 消息泵 + 窗口 + swapchain + MediaPlayer）、`DesktopVideoWallpaperService`、`Interop/`×3 |
| `Muralis.Core` | net10.0（平台无关） | 无 | 模型、providers、下载队列、SQLite 图库、缓存、轮换 planner、设置持久化 |

### 1.2 影响 V2 设计的关键事实

1. **WorkerW 覆盖整个虚拟桌面**：`RepositionOverPrimaryDisplay` 用 `ClientToScreen(worker)` 做屏幕 → 客户区换算即是证据。正确模型是「1 个 WorkerW 宿主 + N 个显示器子窗口」，不存在「每显示器一个 worker」。
2. **帧呈现运行在 MediaPlayer 的媒体线程**（`VideoFrameAvailable`）；窗口创建/定位/重挂载在自建线程上；两者靠 `_renderGate` 协调。该线程契约在 V2 中保留并显式写为接口约定。
3. **挂载恢复逻辑每个会话自己实现**：`WmShellRestarted` 消息 + 5 秒线程定时器兜底 + `WmDestroy` 兜底，三条入口都调用 `HandleMountLoss`/`TryRemount`。新增桌面内容就要复制整套逻辑——V2 要消灭的正是这一点。
4. **动态壁纸只支持主屏**：`MonitorFromPoint(default, MONITOR_DEFAULT_TO_PRIMARY)` 硬编码；`OnDisplayCheckTimer` 只比较主屏宽高，不感知 DPI、位置与显示器增删。
5. **静态壁纸已多显示器就绪**：`IDesktopWallpaper`（`WindowsWallpaperService`）+ `MonitorInfo.Id = device path`；动态壁纸没有跟上。
6. **`AppSettings` 是扁平单文件**：`Theme/Language/Providers/Rotation/VideoWallpaper/...` 全部塞在一份 `settings.json`；`SchemaVersion = 1` 存在但没有任何迁移器。
7. **P/Invoke 分散在 7 个文件**：`DesktopHost/NativeMethods`(23)、`DesktopHost/D3D11Interop`(2)、`App/TrayInterop`(15)、`App/ShellInterop`(5)、`App/DesktopWallpaperInterop`(1)、`App/WindowHelper`(1)、`App/TrayService`(1)。其中 `TrayInterop` 与 `NativeMethods` 重复实现了 `WindowClass`/`RegisterClass`/`CreateWindowEx`/`DefWindowProc`——重复机制需要归并，但归属应按领域划分（§12）。
8. **`IVideoWallpaperService` 是会话式接口**（`Start(path, muted)`/`Stop`/`NotifyShellRestarted`），无法承载「每显示器不同内容、内容类型可变」的未来；按约束不在它上面加参数。
9. **`RotationService` 全局轮换**（`monitorId: null`）；`ILocalLibrary.RecordUsageAsync(wallpaper, monitorName)` 已有显示器概念的雏形。
10. **`ILocalLibrary.RecordUsageAsync(wallpaper, monitorName)`、`MonitorInfo` 已被 UI（Settings/Detail 页）使用**，显示器模型演进必须保留 UI 兼容。

---

## 2. 保留 / 重构 / 新增

### 2.1 直接保留（不动或仅微调）

**Core（约 90% 原样）**
- `Wallpaper` 模型、`IWallpaperProvider` + 4 个 provider、`WallpaperProviderManager`
- HTTP 管线：`HttpRetryHandler`、`MetadataCacheHandler`、`MetadataCache`
- 下载：`DownloadService`、`DownloadQueueService`、`DownloadItem`
- 图库：`SqliteWallpaperRepository`、`LocalLibrary`（仅加表/字段）、`ContentHash`、`WallpaperId`、`ImageMetadataReader`、`ImageCacheService`
- `RotationPlanner`（纯算法）、`UpdateChecker`、`DisplayFormat`、`FileNameHelper`、`DirectoryHelper`
- 全部现有测试用例

**App（MVVM 与自身平台能力原样）**
- 所有 View / ViewModel、本地化（`LocalizationService` + resx 卫星程序集）、`ThemeService`、`NavigationService`、`DialogService`、`FilePickerService`
- `AppHost` DI 容器结构（只增注册项）、`MainWindow` 行为、托盘（`TrayService` + `TrayInterop`）、单实例（`SingleInstanceGuard`）、`WindowHelper`
- `WindowsWallpaperService`：Phase 1 不动；Phase 2 的显示器枚举改走 `IMonitorManager`，其 `IDesktopWallpaper` COM interop 随多显示器工作迁入 Desktop（§12、§13）

**DesktopHost 的桌面层 Interop**
- `NativeMethods`、`D3D11Interop`、`DesktopWorkerWindow`（两种拓扑探测逻辑保留，增强为能力探测 + 结构化日志）

### 2.2 需要重构（拆 / 合 / 换边界）

| 现有物 | V2 处置 | 理由 |
| --- | --- | --- |
| `DesktopHostSession`（761 行，5 种职责） | **拆 4 份**：`DesktopShell`（线程/泵/watchdog）、`Win32SurfaceHost`（建窗/定位/销毁）、`VideoSurfaceContent`（播放 + 帧呈现）、`MountPolicy`（挂载/重挂载归 shell） | 一个类同时是线程、窗口、渲染器、恢复逻辑；任何新内容都要复制它 |
| `DesktopVideoWallpaperService` | 保留 `IVideoWallpaperService` 实现作为**兼容适配器**（内部改调 `IDesktopShell`）；Phase 4 由 `IDesktopBackdropService` 取代后删除 | 兼容层必须有明确删除期限 |
| `ShellInterop` | **拆分**：`TaskbarCreated` 注册与隐藏窗口监听 → Desktop（`ShellEventSource`）；激活广播 / `AllowSetForegroundWindow` / `RegisterWindowMessage`（激活消息）→ 留在 App（单实例是应用生命周期关注点） | 按领域归属，不按「是否 P/Invoke」归属 |
| `TrayInterop` / `TrayService` | **留在 App**（托盘是应用行为） | 与 Desktop Surface 无关 |
| `DesktopWallpaperInterop` | Phase 2 迁入 Desktop（与多显示器工作同批），`WindowsWallpaperService` 保留为消费者 | 桌面背景应用属于桌面层；但 Phase 1 不做无关迁移（R7） |
| `ShellLifecycleWatcher` | 演化为 Desktop 的 `ShellEventSource`（事件面扩展：`ShellRestarted` / `DisplaysChanged` / 会话事件）；App 只订阅（托盘重建图标、转发给 `IDesktopShell`） | 显示器拓扑变化与 Explorer 重启共用一个消息源 |
| `AppSettings` | Phase 3 拆为分层文档（§8），加迁移器，`SchemaVersion` 升 2，旧文件备份 `.v1.bak` | 防止未来功能继续塞进单文件 |
| `MonitorInfo` | Phase 1 起由 `IMonitorManager` 统一生产（`WindowsWallpaperService.GetMonitorsAsync` 委托 + 薄映射）；Phase 2 UI 直接使用新 Monitor 模型后删除 | 避免两套显示器真相 |
| `RotationService` | Phase 3 起改为 profile/scene 感知（每显示器独立轮换）；`RotationPlanner` 不动 | 全局轮换与按显示器场景冲突 |
| `App.xaml.cs` 的启动编排 | 视频恢复改由 Runtime State 文档驱动（Phase 3 起逐步移出 App） | 恢复逻辑属于 shell |

### 2.3 新增（按阶段）

| 新增 | 阶段 |
| --- | --- |
| `IDesktopShell` / `ISurfaceHost` / `IDesktopSurface` / `ISurfaceContent` / `IMonitorManager` / `ShellEventSource` | Phase 1 |
| `MonitorIdentity` / `MonitorRuntimeInfo` / `Monitor` / `MonitorProfile`（Profile 绑 `StableId`） | Phase 1 模型、Phase 2 落库 |
| `Configuration/`：分层文档读写 + 迁移链（含 Asset Bindings） | Phase 3 |
| `Scenes/`：`SceneDocument`、迁移链、校验器、导入导出（`.muralis`） | Phase 4 |
| `ISceneService` 契约 + `SceneOrchestrator`（App 层实现） | 契约 Phase 1 记录、实现 Phase 4 |
| Canvas 数据模型 + `CanvasSurface` | Phase 5 |
| Widget 契约 + 调度器 + 宿主 | Phase 6 |
| Dock | Phase 7 |
| `IAudioPlaybackService` + `ISystemMediaIntegration`（SMTC） | Phase 8 |

---

## 3. 目标模块与依赖关系

### 3.1 模块图

```
                       ┌────────────────────────────────────────────────────┐
                       │  Muralis.App   (WinUI 3, unpackaged, x64/ARM64)    │
                       │  Views / ViewModels / AppHost(DI)                  │
                       │  App 自身 Windows 平台能力（§12 表）：              │
                       │    MainWindow 互操作 / 托盘 / 单实例激活 /          │
                       │    文件选择器 owner / 主题 / 开机启动               │
                       │  未来: SceneOrchestrator（Phase 4）                 │
                       └───────────────┬────────────────────────────────────┘
                                       │ ProjectReference
                       ┌───────────────▼────────────────────────────────────┐
                       │  Muralis.Desktop  (net10.0-windows, 无 UI 框架)     │
                       │  Shell/     IDesktopShell, DesktopShell,           │
                       │             ShellWatchdog, ShellEventSource        │
                       │  Surfaces/  ISurfaceHost, Win32SurfaceHost,        │
                       │             IDesktopSurface, ISurfaceContent,      │
                       │             VideoSurfaceContent                    │
                       │  Monitors/  IMonitorManager, MonitorManager        │
                       │  Layout/    DIP/像素/锚点换算（纯逻辑，可测试）      │
                       │  Interop/   桌面层 Win32 / DXGI / COM P/Invoke     │
                       │  ⚠ 只收桌面层机制，不是通用 native 工具集           │
                       └───────────────┬────────────────────────────────────┘
                                       │ ProjectReference
                       ┌───────────────▼────────────────────────────────────┐
                       │  Muralis.Core  (net10.0, 平台无关)                  │
                       │  Models/         Wallpaper, Monitor(Identity/       │
                       │                  RuntimeInfo), BackdropSpec,        │
                       │                  Scene/Profile/Canvas/Widget DTO    │
                       │  Scenes/         SceneDocument, 迁移链, 校验器       │
                       │  Configuration/  分层文档 + Asset Bindings + 迁移    │
                       │  Abstractions/   IWallpaperService,                 │
                       │                  IDesktopBackdropService,           │
                       │                  ISceneService（契约）,              │
                       │                  ILocalLibrary, IDownload*, ...     │
                       │  Services/       providers, download, library,      │
                       │                  rotation planner, 调度器            │
                       └────────────────────────────────────────────────────┘
```

### 3.2 依赖方向与禁令

| 规则 | 说明 |
| --- | --- |
| `App → Core + Desktop` | 允许 |
| `Desktop → Core` | 允许 |
| `Core → 任何 Windows API / Desktop / App` | **禁止**（保持 net10.0，`dotnet build` 在非 Windows 可过） |
| `Desktop → App / WinUI / WindowsAppSDK` | **禁止**（保证 shell 层可独立进程化、可在无 XAML 线程环境运行） |
| Desktop 收纳范围 | **只收 §12 归属表中的桌面层机制**；App 自身的 Windows 平台能力继续留在 App。禁止把 Desktop 演变为通用 native 工具集 |
| 同一原生机制的所有者 | **唯一**：每个原生能力只允许一个实现文件/组件；发现重复（当前的 `WindowClass`/`RegisterClass` 重复）时归并到其领域所有者 |
| 循环依赖 | 结构上不可能：单链 `Core ← Desktop ← App` |

### 3.3 可自动检查的架构门禁（Commit G 交付）

1. `Muralis.Core.dll` 不引用 Windows SDK 程序集；
2. `Muralis.Desktop.dll` 不引用 WinUI / WindowsAppSDK / `Muralis`（App）；
3. `Muralis.App` 不声明桌面层入口点的 P/Invoke（denylist：`SetParent`、`FindWindowW/FindWindowExW`、`EnumWindows`、`SetWindowPos`、`MonitorFromPoint`、`GetMonitorInfoW`、`D3D11CreateDeviceAndSwapChain`、`CreateDirect3D11SurfaceFromDXGISurface`、`PostThreadMessageW` 等）；
4. （评审级，非自动）每个隐藏窗口与原生机制有唯一所有者，无重复 `RegisterClass` 的窗口类。

---

## 4. Desktop Shell 设计

### 4.1 进程与线程模型（Q1 裁决）

- **单进程**：桌面层随 Muralis 主程序退出；不建独立 Desktop Host 进程。
- 崩溃/卡死隔离由 **shell 专用线程**提供（v0.2.0 已达成「UI 线程永不碰 native」）。
- `IDesktopShell` 按**跨进程友好**设计以备未来演进：纯数据参数、事件流接口、无 UI 线程亲和、不向调用方返回 HWND（只返回不透明句柄用于诊断日志）。

### 4.2 结构

```
DesktopShell（每进程一个实例，拥有唯一 shell 线程 + 消息泵）
│
├─ ShellWatchdog           5s 心跳（现有定时器一般化）：
│                          挂载存活校验 / 拓扑变化 / 丢失重挂载重试（指数退避）
├─ ShellEventSource        隐藏 top-level 窗口（必须是真窗口：message-only 窗口
│                          收不到广播，保留现有约束与实现）：
│                            TaskbarCreated   → ShellRestarted
│                            WM_DISPLAYCHANGE → DisplaysChanged
│                            WM_DPICHANGED    → 由各 surface 自行接收
│                            会话锁/解锁       → （Phase 6 widget 调度消费）
├─ DesktopLayerHost        WorkerW 发现/重建（复用 DesktopWorkerWindow 两种拓扑探测）
│   │                      一个虚拟桌面级父窗口；子窗口按屏幕坐标偏移定位
│   ├─ MonitorSlot[A]      有序 SurfaceStack（z 序 = 列表顺序）
│   │    └─ [ BackdropSurface(Video) ]      ← 每显示器 ≤1 个「画家」
│   ├─ MonitorSlot[B]      [ BackdropSurface(Video) ] / 空
│   └─ MonitorSlot[N]      ...
│
├─ InteractiveHost         交互内容宿主工厂（Phase 5+ 原型后定型，§5.3）
│
└─ Win32SurfaceHost        ISurfaceHost 唯一实现：CreateWindowEx / SetParent /
                           定位（DIP→像素）/ DPI 变更 / 销毁
```

**关于两个隐藏窗口**：Desktop 的 `ShellEventSource` 承载桌面层生命周期消息；App 保留一个仅用于接收「第二次启动激活广播」的小窗口（单实例是应用关注点）。两者职责不同、互不重复；相比当前单一 window 多一个小窗口，换取归属清晰。

### 4.3 挂载生命周期状态机（由 shell 统一持有）

```
                 Attach(monitor, layer)
   Detached ────────────────────────────► Mounted
      ▲                                     │  ▲
      │                                     │  │ remount（watchdog 驱动，指数退避）
   Detach()                                 ▼  │
      │                                  Orphaned
      └───────────────────────────────────┘
        （Explorer 重启 / worker 被销毁 / WmDestroy 到达）
```

核心语义：**「要挂载什么」是 shell 持有的意图，不是窗口的属性**。窗口只是一次挂载的产物——v0.2.0 的 `_playbackActive` + `_mountLost` 已验证该语义，V2 将其从会话私有字段提升为 shell 一等状态。

### 4.4 类对应关系（精确回答「保留 / 拆分 / 演化」）

| 现有 | 处置 | 归属 |
| --- | --- | --- |
| `DesktopVideoWallpaperService` | 兼容适配器，Phase 4 删除 | `Desktop/Surfaces/Compatibility/` |
| `DesktopHostSession` | 拆解：线程/泵/watchdog → `DesktopShell`+`ShellWatchdog`；建窗/定位 → `Win32SurfaceHost`；播放/呈现 → `VideoSurfaceContent`；重挂载 → `MountPolicy`（shell） | `Desktop/Shell` + `Desktop/Surfaces` |
| `ShellLifecycleWatcher` | 演化 `ShellEventSource`（行为不变，事件面扩展） | `Desktop/Shell/` |
| `ShellInterop` | 拆分：`TaskbarCreated` 部分 → Desktop `ShellEventSource`；激活广播 / ASFW → 留 App | Desktop + App |
| `DesktopWorkerWindow` | 原样保留 + 能力探测日志 + 找不到 worker 时降级静态壁纸并通知用户 | `Desktop/Shell/` |
| `NativeMethods` / `D3D11Interop` | 原样迁移 + 扩充（`WM_DISPLAYCHANGE`、DPI、`EnumDisplayMonitors`） | `Desktop/Interop/` |
| `TrayInterop` / `TrayService` / `WindowHelper` / `SingleInstanceGuard` | **留在 App**（§12） | App |

---

## 5. Surface 模型设计

### 5.1 契约（渲染技术中立，R5）

```csharp
// 内容：这个表面「提供什么」。Phase 1 只有 video 一种实现。
interface ISurfaceContent : IAsyncDisposable
{
    SurfaceKind Kind { get; }                        // Backdrop（Phase 1 仅此一种）
    SurfaceInteraction Interaction { get; }           // None | Pointer | PointerAndKeyboard
    SurfaceActivation Activation { get; }             // Never | OnClick | Always
    Task MountAsync(ISurfaceTarget target, CancellationToken ct);
    Task UnmountAsync();
    void OnGeometryChanged(MonitorGeometry geometry, double scale);
}

// ISurfaceTarget：宿主提供给内容的抽象目标（不含 UI 框架类型）
interface ISurfaceTarget
{
    nint WindowHandle { get; }       // 宿主创建的窗口；内容可（Phase 5/6）在其上承载 XAML/Composition 内容
    PixelRect PixelBounds { get; }
    double ScaleFactor { get; }
    SurfaceLayer Layer { get; }      // WallpaperLayer | DesktopInteractiveLayer（后者为未来）
}

// 机制：窗口 + 生命周期的唯一实现（Win32SurfaceHost）
interface ISurfaceHost
{
    Task<IDesktopSurface> CreateAsync(SurfaceRequest request, CancellationToken ct);
    Task DestroyAsync(IDesktopSurface surface);
}

// 单元：一次挂载的绑定，由 DesktopShell 持有
interface IDesktopSurface : IAsyncDisposable
{
    string Id { get; }
    ISurfaceContent Content { get; }
    SurfaceState State { get; }        // Detached / Mounted / Orphaned
    MonitorRef Monitor { get; }
    int ZOrder { get; }
    Task AttachAsync(MonitorTarget target, CancellationToken ct);
    Task DetachAsync();
    void MoveTo(MonitorRef monitor, MonitorGeometry geometry);
}
```

**两条轴决定一切**：

- `SurfaceKind`（内容语义）→ 决定进哪个层、每显示器几个、是否共享窗口；
- `Interaction × Activation`（输入语义）→ 决定窗口样式与 z 序策略；默认 `None + Never`，交互必须显式声明。

### 5.2 共享窗口 vs 独立窗口（Phase 1 已验证部分 + 未来候选）

| Surface | Phase 1 状态 | 窗口策略（候选，部分待原型验证） | 每显示器实例 |
| --- | --- | --- | --- |
| **Backdrop**（视频；未来其他真正背景类型） | **实现**（native/DXGI 内容） | WorkerW 子窗口 | **≤1** 个「画家」：全屏不透明层，两个会互相覆盖 |
| WidgetLayer | 不实现 | 候选：1 个宿主窗口内承载 N 个 widget（避免每 widget 一个 HWND 的 z 序/激活/DPI 混乱）——窗口形态与渲染技术由 Phase 6 原型决定 | 1（候选） |
| Canvas | 不实现 | 候选：全屏交互层；窗口/band 方案由 Phase 5 原型决定（§9.3） | ≤1（候选） |
| Dock | 不实现 | 候选：浮层或 AppBar；由 Phase 7 原型决定 | 0..1（候选） |

### 5.3 交互 vs 非交互，与「不提前锁死渲染技术」（R5）

- **非交互（壁纸层）**：WorkerW 子窗口天然不接收输入（被图标层覆盖）；窗口 `WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW`，`WM_ERASEBKGND` 返回 1（保留现有做法）。
- **交互内容（图标层之上）**：需要真实的可交互 UI。**渲染与承载技术在 Phase 5 / 6 原型验证后再定**，候选包括：
  1. WinUI 3 内容承载（DesktopWindowXamlSource / XamlIsland，WASDK 2.x 具体 API 需 spike 确认）；
  2. Microsoft.UI.Composition 自绘；
  3. Direct2D / DirectComposition 自绘；
  4. 其他经验证合理的方案。
- **Phase 1 的诚实边界**：`ISurfaceContent` 抽象由**唯一实现（Video）**验证；不假装所有内容类型已能共用一套完整渲染契约。抽象在未来可能需要扩展，这是接受的代价；但「生命周期 / 桌面定位 / DPI / z 序由 shell 统一」这一层是确定的。
- 交互窗口必须经 `InteractiveHost` 工厂创建（统一激活与 z 序策略）；禁止裸 `CreateWindowEx`。
- 原生渲染（视频）与未来 UI 渲染（Canvas/Widget）**允许不同实现**；shell 统一的是宿主与生命周期，不是绘制方式。

### 5.4 防止各自建宿主的硬规则

1. 新桌面内容**只实现 `ISurfaceContent`**；创建/定位/销毁窗口只能经 `ISurfaceHost`。
2. 挂载/重挂载/DPI/拓扑策略**只在 `DesktopShell`**；surface 不感知 Explorer 重启，只收到 `OnGeometryChanged` / `Orphaned` 通知。
3. 桌面层入口点的 P/Invoke 对 App 不可用（门禁检查，§3.3）。
4. 交互窗口必须经 `InteractiveHost`；同一原生机制只有一个所有者。

---

## 6. Multi-Monitor 模型（R2）

### 6.1 身份与运行时分离

**原则：只有「这是哪一台显示器」的稳定信息才能定义身份；`IsPrimary` / `OrderIndex` / `DeviceName` 等运行时属性不得参与身份判定。**

```csharp
// —— 身份：只包含稳定信息，Profile 只绑定它 ——
sealed record MonitorIdentity(
    string StableId,                 // 唯一持久键：EDID（厂商+产品+序列号）派生哈希
    EdidInfo? Edid,                  // ManufacturerCode, ProductCode, SerialNumber, SerialText
    IdentityConfidence Confidence,   // Exact（EDID 完整）| SignatureFallback（降级签名）
    string? LastKnownFriendlyName);  // 持久匹配 hint（仅辅助，不参与判定）

// —— 运行时信息：每次会话可变化 ——
sealed record MonitorRuntimeInfo(
    string DevicePath,               // \\?\DISPLAY#…（IDesktopWallpaper 用）
    string DeviceName,               // \\.\DISPLAYn
    string FriendlyName,
    bool IsPrimary,
    int OrderIndex,
    PixelRect Bounds,
    PixelRect WorkArea,
    uint Dpi,
    double ScaleFactor,
    MonitorOrientation Orientation,
    MirroringInfo Mirroring);        // IsMirrored + MirrorGroup + LeaderDevicePath

// —— 组合视图（运行时由 IMonitorManager 提供） ——
sealed record Monitor(MonitorIdentity Identity, MonitorRuntimeInfo Runtime);
```

**降级签名（`SignatureFallback`）**：EDID 不可读时使用「`\\.\DISPLAYn` + 像素尺寸 + 相对顺序签名」并显式标注置信度；降级身份在显示器重新排列后可能失配，失配时走 §6.2 的降级链。

### 6.2 匹配链与 Profile 绑定

Profile 只写 `StableId`。运行时解析流程：

```
StableId 精确匹配（Exact）
  → StableId 匹配 SignatureFallback（同尺寸/同位置候选，若唯一则确认）
  → 无匹配：按运行时顺序（主屏优先、再按 x 坐标）给出候选，首次歧义询问用户
  → 用户确认后：重建 StableId 绑定（写入 Profile）
```

镜像模式（多 DeviceName 同 DevicePath）在 `IMonitorManager` 去重，并显式表达 `MirroringInfo`；backdrop 只渲染一个（否则重复解码）。

### 6.3 每显示器绑定（Desktop Profile 的一部分）

```csharp
sealed record MonitorBinding(
    string StableId,                 // 唯一身份键（R2）
    WallpaperRef? StaticWallpaper,   // 复用现有静态壁纸能力
    SceneRef? Scene,                 // 场景（意图）
    bool DynamicBackdropEnabled,     // 该屏是否启用动态背景
    string? LayoutOverrideId,        // 用户拖动后的本地布局覆盖（不写回场景）
    bool WidgetsEnabled,
    bool DockEnabled);

sealed record DesktopProfileDocument(
    int SchemaVersion,
    List<MonitorBinding> Bindings,
    ProfileDefaults Defaults,        // OnNewMonitor: FollowPrimaryScene（默认）| Empty | DefaultScene(id)
    SpanPolicy Span);                // 跨屏壁纸与「每屏独立」的取舍
```

**新显示器默认策略（Q5 裁决）**：跟随主屏的 **Scene**（场景默认布局），**不复制**主屏的 runtime layout override——新显示器从场景默认布局生成自己的布局。

### 6.4 必须处理的变化（数据模型先行，实现 Phase 2）

| 事件 | 期望行为 |
| --- | --- |
| 分辨率变化 | backdrop 重建（现有逻辑保留）；布局按 `reflowPolicy` 重排；DIP 重算 |
| DPI 变化 | 交互内容重排版（`WM_DPICHANGED`）；backdrop 重建 swapchain |
| 热插拔新显示器 | 按 `Defaults.OnNewMonitor` 建绑定（默认跟随主屏 Scene，不复制布局 override） |
| 显示器移除 | 绑定保留在 Profile（标记 orphan，不删）；场景迁移策略可配置 |
| 主屏切换 | `IsPrimary` 重算（仅运行时信息）；绑定不受影响（身份不含主屏标志） |
| 镜像 | 同 DevicePath 去重，只渲染一个 backdrop |

---

## 7. Scene Document 模型

### 7.1 形态：zip bundle（`.muralis`），内部全 JSON + 素材

```
nebula-desk.muralis (zip)
├─ scene.json                 清单: schemaVersion, id, name, tags, requires, 引用表, 完整性哈希
├─ wallpaper/wallpaper.json   静态 + 动态壁纸意图
├─ layout/canvas.json         桌面布局意图（§9）
├─ widgets/widgets.json       widget 实例与配置（§10）
├─ dock/dock.json             坞站项（§9.4）
├─ theme/theme.json           调色板/明暗/字体引用
├─ media/media.json           媒体设置（Phase 8）
├─ assets/                    sha256 命名的素材（图片/视频/字体子集），引用一律相对路径
└─ preview/                   scene.png + thumb.png（导入前预览，导出时生成）
```

### 7.2 scene.json 示例

```json
{
  "schemaVersion": 1,
  "kind": "muralis.scene",
  "id": "9f1c4a2e-…",
  "name": "Nebula Desk",
  "description": "深空风格工作桌面",
  "author": "Muralis",
  "createdAt": "2026-09-15T10:00:00Z",
  "modifiedAt": "2026-09-15T10:00:00Z",
  "tags": ["space", "dark"],
  "license": { "spdx": "CC-BY-4.0", "source": "https://…" },
  "requires": { "muralis": ">=0.4.0", "features": ["video", "widgets", "dock"] },
  "docs": {
    "wallpaper": "wallpaper/wallpaper.json",
    "layout":    "layout/canvas.json",
    "widgets":   "widgets/widgets.json",
    "dock":      "dock/dock.json",
    "theme":     "theme/theme.json",
    "media":     "media/media.json"
  },
  "assets": [
    { "ref": "sha256:ab12…", "path": "assets/ab12….mp4",
      "mediaType": "video/mp4", "bytes": 18273645 }
  ],
  "preview": { "image": "preview/scene.png", "thumb": "preview/thumb.png" }
}
```

`wallpaper/wallpaper.json`（意图，不含任何本机路径）：

```json
{
  "schemaVersion": 1, "kind": "muralis.wallpaper",
  "static":  { "asset": "sha256:…", "fit": "Fill" },
  "dynamic": { "type": "video", "asset": "sha256:…", "loop": true, "muted": true,
               "playbackPolicy": { "pauseOnFullscreen": true, "pauseOnBattery": false } }
}
```

### 7.3 版本化 / 验证 / 迁移（硬性设计）

| 要求 | 机制 |
| --- | --- |
| 版本化 | 每个文档顶层 `int schemaVersion`（当前 =1）；只在形状变化时 +1；`kind` 字段自描述并防误用 |
| 可序列化 | 纯 `System.Text.Json`；`JsonNode` 为主、强类型 DTO 为辅；全项目共用一份 `JsonSerializerOptions` |
| 可导入导出 | 导入 = 解压临时目录 → 校验 → 原子移动到 `scenes/<id>/`；导出 = 重打包 + 重算哈希 + 生成 preview |
| 可验证 | `SceneValidator`：版本已知、`docs` 引用存在、`assets` 哈希匹配、坐标范围合法、widget 类型已知、`requires.features` 已实现；上限：bundle ≤4 GiB、单资产 ≤2 GiB、条目 ≤10 000 |
| 可迁移 | `IMigration { int From; int To; void Migrate(JsonObject doc); }` 链式执行；**更高**的未知版本 → 拒绝加载并明确报错 |
| 无代码 | 校验器白名单所有 `kind`；禁止表达式/脚本/模板语法；素材仅限图片/视频/字体；绝不执行 bundle 内任何文件 |
| 安全 | 防 zip slip（路径规范化 + 前缀校验）、拒绝符号链接/重复条目、压缩比检查、永不写出 `scenes/` 之外 |

### 7.4 Scene 在架构中的位置（R4）

**Scene 不是 `BackdropKind`，也不是任何单一子系统的参数。** Scene 是**编排层文档**：描述「一个桌面应该是什么样」。未来由 `SceneOrchestrator` 读取并分派到各子系统：

```
                    ┌─────────────────────────────────────────────┐
                    │  SceneOrchestrator（Phase 4，App 层实现）    │
                    │  读取 SceneDocument，按子系统分派             │
                    │  契约 ISceneService 定义在 Core              │
                    └──┬───────┬───────┬───────┬───────┬──────────┘
                       │       │       │       │       │
                 Wallpaper  Backdrop  Canvas  Widgets  Dock / Theme / Media
                 静态壁纸    桌面背景   桌面图标  小组件     （后续阶段）
              IWallpaperService  IDesktopBackdropService  ...（各子系统仍按自己的阶段落地）
```

Phase 0/1 只记录该分层，**不实现 SceneOrchestrator**。

---

## 8. 配置与数据分层（R3）

### 8.1 分层总表

| # | 文档 | 路径 | 内容 | 生命周期 | 删除后果 | 迁移 |
| --- | --- | --- | --- | --- | --- | --- |
| 1 | **Application Settings** | `settings.json` | 应用行为：语言、主题偏好、开机启动、关闭到托盘、下载目录、更新通道、providers 启用态 | 长期 | 丢应用偏好 | SchemaVersion + `SettingsMigrator`（v1→v2） |
| 2 | **凭据** | `providers.json` | API 密钥（永不入 repo） | 长期 | 丢密钥 | 不变 |
| 3 | **Desktop Profiles** | `desktop/profiles.json` | `StableId → 绑定`（场景/静态壁纸/动态背景开关/widgets/dock）、新显示器默认策略 | 长期，用户可编辑 | **丢失用户配置** ⚠ | 文档级 SchemaVersion |
| 4 | **Asset Bindings** | `bindings/assets.json` | 持久资源绑定：场景资产引用（`sha256:…`）↔ 本机文件路径；用户为本机挑选的视频/图片的持久记录；可用性状态（Present/Missing） | 长期 | **丢失本机绑定** ⚠ | 文档级 SchemaVersion |
| 5 | **Scene Documents** | `scenes/<id>/`（+ 导入的 `.muralis`） | 场景包（§7） | 可导入/导出/分享 | 丢场景（但可重新导入） | SceneMigrationPipeline |
| 6 | **Runtime State** | `state/desktop-state.json` | **仅**：当前实际挂载内容、当前运行状态、恢复所需临时信息、最后一次 Surface 状态 | **可再生** | **无损失**（删除即重建） | 不需要（结构变化即丢弃重建） |
| 7 | Catalog（既有） | `muralis.db` | 图库/收藏/标签/使用记录/下载 | 长期 | 丢收藏记录 | SQL 版本表 |
| 8 | Cache（既有） | `cache/` | 缩略图、元数据缓存 | 可再生 | 无损失 | — |

### 8.2 关键原则

1. **Runtime State 可删除原则（R3）**：`state/desktop-state.json` 中只允许出现「重建即可恢复」的信息。任何**删除后仍应被记住**的绑定，必须存 Profile / Asset Bindings / Catalog。
   - 反例（禁止）：用户挑选的本地视频路径、外部资源引用、场景资产 ↔ 本机文件的持久绑定。
   - 正例（允许）：「显示器 A 当前挂着场景 X 的视频 V」这类当前状态。
   - 验收测试：**删除 `state/` 目录后重启，用户配置、场景绑定、本机文件绑定全部完好**。
2. **Scene 是意图，Profile/Bindings 是本机现实**：场景只写相对引用（`sha256:…`）；「这段视频在本机的哪个文件」永远记录在 Asset Bindings；「哪台显示器挂什么」记录在 Profile。
3. **用户拖动的布局偏差写 Profile/State，不写回场景**（本地个性化可一键重置）。
4. **`settings.json` 瘦身**：`VideoWallpaper` / `Rotation` 等桌面内容字段迁出（→ Profile / Scene / Bindings），App 设置只剩「应用行为」。
5. **Phase 3 迁移**：读 v1 → 备份 `.v1.bak` → 写分层文档（`VideoWallpaper.VideoPath` → Asset Bindings + Profile，**不**进 Runtime State）→ 校验 → 旧字段清除；迁移失败不删旧文件、降级默认值。

---

## 9. Desktop Canvas 数据模型

> 只定义模型。渲染与接管方式在 Phase 5 原型验证（§9.3 / §5.3）。

### 9.1 坐标方案

**结论：锚点 + DIP 偏移为主，比例因子为辅；绝对像素仅作导入输入。**

```json
{ "anchor": "BottomRight", "dx": -48, "dy": -48, "sizeDip": [96, 96],
  "stretch": null, "z": 10 }
```

- `anchor`：9 宫格之一（`TopLeft…BottomRight`）+ 可选 `stretch: {fx, fy}`（0..1 空闲空间比例定位）；
- `dx, dy`：相对锚点在 **DIP** 下的偏移 → 渲染时乘显示器 `ScaleFactor`；分辨率变、DPI 变都不失尺寸感；
- 渲染公式：`pixel = AnchorPoint(workArea, anchor) + (dx, dy) * scale`；`stretch` 存在时按 `workArea` 空闲空间插值；
- 跨分辨率/跨屏迁移由 `reflowPolicy` 决定：`PreserveRelative`（默认）/ `ClampWithinWorkArea` / `ReflowToGrid`，全部为 Core 内纯函数、可单测；
- 网格：`GridSpec { enabled, cellDip, originAnchor, columns, rows }` + 吸附策略 `Free | Grid | FlowColumns`。

### 9.2 模型

```csharp
sealed record DesktopLayout(
    int SchemaVersion, string Kind, string Id, string Name,
    CoordinateSpace Space, GridSpec? Grid,
    List<DesktopItem> Items, List<DesktopGroup> Groups,
    ReflowPolicy Reflow);

sealed record DesktopItem(
    string Id, DesktopItemKind Kind,      // Icon | Shortcut | FileRef | Folder | Url | Separator
    ItemTarget Target,                    // 快捷方式路径 / 文件路径 / URL / shell 命名空间引用
    Placement Placement,                  // anchor + dx/dy(DIP) + sizeDip + stretch
    string? GroupId, int Z, LabelSpec Label, IconOverrideSpec? Icon);

sealed record DesktopGroup(string Id, string Name, GroupStyle Style, // Frame | Tint | None
                           bool Collapsed, Placement? Placement, List<string> ItemIds);

sealed record DockItem(string Id, DesktopItemKind Kind, ItemTarget Target,
                       int Order, bool IsPinned, bool ShowLabel, BadgePolicy Badge);
```

### 9.3 与 Windows 真实桌面的关系（Q3 裁决）

**长期路线 A：Muralis 自己管理图标层。** 实施顺序被裁决固定为：

1. **Phase 5 先做非破坏 Preview**：Canvas 编辑器（应用内）+ 桌面覆盖预览（只画不接管，`WS_EX_TRANSPARENT`），验证坐标系、渲染、多屏；
2. **接管 Windows 原生图标是之后的独立功能**，且**必须可关闭**（一键回落系统图标）；
3. **具体 Windows z-order / desktop-band 实现延后到原型验证**——本 Blueprint 不假装该问题已经解决（详见 §5.3 与 §15 Q6）。

被否决路线（记录备查）：B. 通过 `SysListView32` 重排系统图标——保留原生渲染但能力上限低（无分组/无缩放/无自由 z）且与 Win11 自动排列对抗，脆弱。

### 9.4 文件引用原则

Canvas/Widget/Dock 引用的真实文件（快捷方式、文件）**只读引用，绝不移动/复制用户文件**；布局是视图数据。目标失效（文件不存在）时显示「缺失」态而不是删除条目。

---

## 10. Widget Contract

> 只定义契约。渲染与承载技术在 Phase 6 原型验证（§5.3）。

### 10.1 描述符（Core，代码内定义，内置 widget 专用）

```csharp
sealed record WidgetDescriptor(
    string Id,                       // "muralis.clock"
    string DisplayName, string Category, string IconKey,
    Version Version,
    WidgetDataPolicy DataPolicy,     // ScenePortable | LocalOnly
    WidgetCapabilities Capabilities, // NeedsNetwork | Interactive | UsesLocation | …
    WidgetSizeSpec Size,             // 默认/最小/最大（DIP）
    WidgetRefreshSpec Refresh,       // 最小间隔、是否支持暂停
    WidgetHostKind Hosts,            // Desktop | Dock | Both
    Func<JsonNode, WidgetConfigValidation> ValidateConfig);
```

```json
// scene → widgets/widgets.json
{ "schemaVersion": 1, "kind": "muralis.widgets",
  "instances": [
    { "instanceId": "w_clock_1", "type": "muralis.clock", "typeVersion": "1.x",
      "placement": { "anchor": "TopRight", "dx": -32, "dy": 32, "sizeDip": [220, 120] },
      "config": { "format": "HH:mm", "showSeconds": false, "showDate": true },
      "refresh": { "intervalSeconds": 1 },
      "data": { "timeZone": "Asia/Shanghai" } }
  ] }
```

`data` 仅在 `DataPolicy = ScenePortable` 时随场景导出；`LocalOnly` 的 widget 数据（缓存、滚动位置等）存 Runtime State。

### 10.2 生命周期与调度

```
Discover（宿主列出可用 descriptor）
  → Validate（config 校验失败 → 拒绝挂载该实例并标记，不拖垮整屏）
  → Mount（在宿主中承载，分配 WidgetContext）
  → Activate → Tick*（统一 WidgetScheduler 驱动）
       ├ Suspend（全屏应用 / 会话锁定 / 专注模式）→ Resume
       └ 刷新节流：每 widget 最小间隔（时钟 1s、系统监控 1s、天气 ≥15min）
  → Unmount → Dispose
```

### 10.3 宿主与约束

- **统一宿主**：`WidgetScheduler`（单一定时器 + 抖动 + 门控：全屏 / 电池 / 会话锁定）+ 每显示器的 widget 宿主（形态由 Phase 6 原型定，§5.2）。
- **能力声明可执行化**：`NeedsNetwork` 由宿主注入受限 HTTP 服务（禁用即真禁用）；无文件系统自由访问，选文件只能经宿主中介。
- **不做沙箱**：第一阶段（P1）全部为 Muralis 内置 widget，信任边界是代码评审而非 AppContainer/lowbox；何时需要见 §13.4。
- **资源限制**：每 widget 渲染预算（超时/异常被宿主捕获并降级为占位卡）；禁止 UI 线程阻塞；每屏实例数上限（默认 24）。
- **不做第三方任意代码插件市场**（符合约束）。

---

## 11. Media 与 Scene 编排边界（R4）

### 11.1 三个关注点，三个抽象，禁止合并

| 关注点 | 归属 | 抽象 | 与现状的关系 |
| --- | --- | --- | --- |
| 桌面背景内容（视频；未来其他真正属于 Background 的类型） | `Muralis.Desktop` | `VideoSurfaceContent` + 前门 `IDesktopBackdropService`（monitor-aware、内容类型驱动） | 由 `DesktopHostSession` + `DesktopVideoWallpaperService` 演进 |
| 应用内音乐播放 | Core 服务 + App UI | `IAudioPlaybackService`（播放列表 / 传输控制 / 无 HWND） | 新增（Phase 8） |
| 系统媒体控制 | App 平台层 | `ISystemMediaIntegration`（SMTC：上报元数据、接收媒体键） | 新增（Phase 8） |

### 11.2 Backdrop 契约（不含 Scene，R4）

```csharp
enum BackdropKind { None, Video }   // 未来只允许增加"真正属于桌面背景"的类型

sealed record BackdropSpec(BackdropKind Kind, string? AssetRef,
                           VideoPlaybackOptions? Video);

interface IDesktopBackdropService
{
    Task<BackdropStatus> ApplyAsync(MonitorRef monitor, BackdropSpec spec, CancellationToken ct);
    Task ClearAsync(MonitorRef monitor, CancellationToken ct);
    event EventHandler<BackdropStatusChanged>? StatusChanged;
}
```

`BackdropSpec.AssetRef` 解析本机文件的顺序：**Asset Bindings → 场景包内素材**（通过 `ISceneService` 的编排传入），backdrop 服务自身不读 Profile/Scene。

### 11.3 明确不做的事

- **不给 `IVideoWallpaperService` 加参数**；它的最终形态是 `IDesktopBackdropService`。Phase 1–3 由兼容适配器桥接，Phase 4 删除适配器。
- 动态壁纸播放**永不注册 SMTC、永不占用媒体键**（保留 `CommandManager.IsEnabled = false` 的正确决定）；音乐播放器**必须**注册 SMTC。两者以 `MediaSessionRole { Backdrop, UserMedia }` 显式区分。
- 视频播放策略（`pauseOnFullscreen` / `pauseOnBattery` / `muted` / `loop`）提升为 `VideoPlaybackOptions`（数据），由 Scene/Profile 声明。

---

## 12. Native Interop 边界（R1）

### 12.1 规则

- **Muralis.Desktop 只拥有与桌面层直接相关的 Windows 原生机制**：Windows Desktop / WorkerW、Desktop Surface、Display / Monitor、Desktop Shell lifecycle、Surface 定位 / z-order / DPI、DXGI / D3D 桌面渲染基础设施。
- **Muralis.App 继续拥有属于应用自身的 Windows 平台能力**：MainWindow 互操作、Tray application behavior、File picker / window owner integration、单实例激活，以及其他与 Desktop Surface 无关的 App Windows integration。
- 目标：**消除重复 interop 与跨层调用**，而不是「App 零 P/Invoke」。
- **Muralis.Desktop 不得演变为通用 Windows native utility project**。

### 12.2 归属表

| 原生能力 | 所有者 | 位置 | 生效阶段 |
| --- | --- | --- | --- |
| WorkerW 探测与父子挂载、Surface 建窗/定位/z 序/DPI | **Desktop** | `Desktop/Shell` + `Desktop/Surfaces` + `Desktop/Interop` | Phase 1 |
| DXGI / D3D11 呈现基础设施 | **Desktop** | `Desktop/Interop/D3D11Interop` | Phase 1 |
| 桌面层 Shell 生命周期消息（TaskbarCreated / WM_DISPLAYCHANGE / 会话事件） | **Desktop** | `Desktop/Shell/ShellEventSource` | Phase 1（Commit C） |
| 显示器枚举 / 拓扑 / DPI | **Desktop** | `Desktop/Monitors` | Phase 1（Commit B） |
| 静态壁纸应用（`IDesktopWallpaper`、注册表样式、`SPI_SETDESKWALLPAPER`） | **Desktop** | `Desktop/Shell/Wallpaper` | Phase 2 迁移（Phase 1 仍在 App；随多显示器工作同批搬迁） |
| Tray 图标（`Shell_NotifyIcon`、原生弹出菜单） | App | `App/Services/Platform/TrayInterop` + `TrayService` | 不变 |
| MainWindow 互操作（`SetForegroundWindow`、窗口初始摆放） | App | `App/Infrastructure/WindowHelper` + 激活部分 `ShellInterop` | 不变 |
| 单实例（命名互斥体 + 激活广播 + `AllowSetForegroundWindow`） | App | `App/Infrastructure/SingleInstanceGuard` | 不变 |
| 文件选择器 owner 集成 | App | `App/Services/FilePickerService` | 不变 |

### 12.3 重复机制的归并点

| 重复项 | 归并到 |
| --- | --- |
| `WindowClass` / `RegisterClassW` / `CreateWindowExW` / `DefWindowProcW`（`TrayInterop` 与 `DesktopHost/NativeMethods` 各一份） | 各自领域保留**最小**声明，但**窗口类注册与隐藏窗口宿主**在 Desktop 侧统一（`ShellEventSource`）；托盘窗口类归 App；已合并的通用结构体不重复定义 |
| 注册窗口消息（`RegisterWindowMessageW`） | `TaskbarCreated` → Desktop；激活消息 → App（消息名常量分别定义，避免跨层引用） |
| `SetForegroundWindow` | App（激活是应用关注点） |

### 12.4 门禁

见 §3.3：自动检查 1–3（Core 无 Windows SDK、Desktop 无 WinUI、App 无桌面层入口点 P/Invoke）+ 评审级检查 4（机制唯一所有者）。

---

## 13. 迁移 Roadmap

### 13.1 通用出口标准（每阶段，缺一不可）

1. `dotnet build` + 全部测试通过；
2. v0.2.0 功能回归清单通过（静态壁纸、每显示器静态壁纸、轮换、下载队列、收藏/标签、视频壁纸 + Explorer 重启恢复、托盘、单实例、明暗主题、中英双语）；
3. 阶段末打 tag（`v0.3.0`…），**回退 = revert 到上个 tag**；
4. 无长期半迁移：兼容适配器必须标注删除阶段（当前唯一兼容层 `IVideoWallpaperService` → Phase 4 删除）；
5. 不允许长期存在两套真正负责 Desktop hosting 的实现。

### 13.2 阶段表

| # | 阶段 | 范围 | 关键交付 | 主要风险 | 缓解 |
| --- | --- | --- | --- | --- | --- |
| **1** | **Desktop Shell Foundation** | 零行为变更的机械抽取 + 桌面层契约落地；**不迁移与 Desktop Shell 无关的 App native code**（Commit A–G，§14） | 项目更名、契约、ShellEventSource、DesktopShell/Win32SurfaceHost、VideoSurfaceContent + 兼容适配器、旧会话删除、门禁 | 线程/挂载语义在拆分中失真（`WmTimer`/`WmDestroy` 三入口 + 媒体线程契约）；改名波及 | 以现状为参照写「挂载恢复场景清单」；保留 5s watchdog 与全部兜底路径；Commit 逐个小步走 |
| **2** | **Monitor Model & Multi-monitor Surfaces** | `MonitorIdentity`/`MonitorRuntimeInfo`/`Monitor` 落地；身份匹配链；backdrop 可挂任意显示器；`DesktopWallpaperInterop` 迁入 Desktop；Profile 文档首版 | 双屏不同内容；热插拔/DPI/分辨率变化正确重建；镜像去重 | 身份误判（同型号双屏）；热插拔时 swapchain 生命周期；多视频解码压力 | 置信度 + 降级链 + 首次歧义询问；镜像去重；支持「仅主屏动态、其余静态」的按屏策略；失败降级静态并提示 |
| **3** | **Configuration Split** | 分层文档（§8）：settings / profiles / bindings / state；`SettingsMigrator`；App 设置瘦身 | 迁移器 + `.v1.bak` 备份 + 校验；**删除 state 后配置不丢的验收测试** | 迁移 bug 丢用户配置；两套真相并存过久 | 先备份后迁移，失败不删旧；一次提交内完成「读旧写新」切换；Runtime State 可删除原则测试化 |
| **4** | **Scene Document Model & Orchestration** | `SceneDocument` + 迁移链 + 校验器 + `.muralis` 导入导出；`ISceneService` 契约 + `SceneOrchestrator`（App）；`IDesktopBackdropService` 取代视频接口；隐式场景（当前设置物化） | 场景应用/导出/导入/校验；**删除 `IVideoWallpaperService`** | 导入安全（zip slip / 炸弹 / 巨物）；schema 演进纪律；隐式场景与用户场景的语义冲突 | 沙箱化校验（临时目录 → 原子移动）；大小/条目上限；校验先于应用；隐式场景只读、首改时物化 |
| **5** | **Desktop Canvas Prototype** | Canvas 模型实现 + 编辑器 + **非破坏 Preview**（覆盖层）；`reflowPolicy` 全实现；多屏 | 可编辑、可预览、可回退、可迁移布局 | 坐标系与预览渲染在多屏/混合 DPI 下出错 | Preview 只画不接管（Q3）；覆盖层 `WS_EX_TRANSPARENT`；一键回落；接管独立开关、后续阶段 |
| **6** | **Widget Host** | 契约 + `WidgetScheduler` + widget 宿主（形态经原型确定）+ 3 个内置 widget（Clock / System Monitor / Notes） | 生命周期/调度/门控/配置校验 | CPU/GPU 失控、全屏时遮挡、DPI 文字渲染、宿主形态选型错误 | 刷新率下限 + 全屏/电池/锁定暂停 + 异常隔离占位卡 + 每屏实例上限；先 spike 承载方案再定架构 |
| **7** | **Dock & Interactive Layer** | Dock 模型 + 窗口方案（浮层/AppBar 原型验证）+ `InteractiveHost` 激活与 z 序策略 | Dock 可用、可自动隐藏、多屏可控 | AppBar 注册冲突；激活抢焦点影响全屏应用 | 默认浮层（非 AppBar），AppBar 可选；激活仅点击时且立即释放 |
| **8** | **Media Expansion** | `IAudioPlaybackService` + `ISystemMediaIntegration`（SMTC）+ 场景 media 文档 | 音乐播放器 + 媒体键正确路由 | SMTC 抢占（必须与 backdrop 会话隔离）；媒体路径可移植性 | 会话角色分离（§11.3）；媒体项引用场景资产 + Asset Bindings |
| **9** | **Shell Integration & Cloud（远景）** | 资源管理器右键「设为壁纸/场景」、跳转列表、通知；场景云同步、跨设备 profile | — | 云端凭据 / PII / 冲突合并 | 本阶段不设计细节；`Muralis.Cloud` 仅作为**可选**未来类库（依赖 Core） |

### 13.3 阶段内的「无半迁移」保障

- 每个阶段结束时，任一功能要么完全是旧实现、要么完全是新实现；
- 唯一允许的并行是**兼容适配器**（`IVideoWallpaperService`），其删除阶段（Phase 4）已固化在表中；
- Desktop hosting 在任意时刻只有一个实现（Phase 1 Commit F 之后）。

### 13.4 未来独立项目的触发条件（现在不建）

| 候选 | 何时才建 | 现在怎么办 |
| --- | --- | --- |
| `Muralis.Widgets` | widget ≥5 个且需要独立 UI 测试/设计器；或需要独立 build/资源管线 | Phase 6 先放宿主工程内（WinUI 类库化随时可做） |
| `Muralis.Media` | 桌面视频与应用内音乐出现**共享的**会话/队列/时间线模型时（当前不存在） | 三个关注点分属 Desktop/Core/App（§11） |
| `Muralis.Themes` | 主题引擎需要 XAML 资源字典的独立发布/热更新时 | 主题是文档（`theme.json`）+ App 内资源字典 |
| `Muralis.Personalization` | 个人化超过「标签/收藏」（如画像/推荐） | 现状在 Core 的图库与模型里 |
| `Muralis.Cloud` | 场景同步/账号体系立项时 | 只保证 Scene/Profile/Bindings 文档可移植 |
| 独立 Desktop Host 进程 | Q1 前提（桌面层需在 App 退出后存活）被产品推翻时 | `IDesktopShell` 已按跨进程友好设计 |

---

## 14. Phase 1 计划（Commit A–G）

### 14.1 范围声明（R7）

- Phase 1 只做 **Desktop Shell Foundation**；
- **不迁移**与 Desktop Shell 无关的 App native code：托盘、文件选择器、MainWindow 互操作、单实例、静态壁纸 COM **保持原位**（静态壁纸随 Phase 2 多显示器工作迁移）；
- 每个 Commit 后：build 通过、现有测试通过；
- **任意时刻不允许两套真正负责 Desktop hosting 的实现并存**。

### 14.2 Commit 划分

| Commit | 内容 | 涉及文件 |
| --- | --- | --- |
| **A** | 项目更名及引用更新（纯机械） | `Muralis.slnx`；`src/Muralis.DesktopHost/` → `src/Muralis.Desktop/`（含 csproj 更名）；`src/Muralis.App/Muralis.App.csproj` 的 ProjectReference；全部 `using Muralis.DesktopHost` → `Muralis.Desktop` |
| **B** | Desktop Shell / Monitor / Surface 契约（只有契约与最小编译单元，无行为） | 新增：`Desktop/Shell/IDesktopShell.cs`、`Desktop/Shell/DesktopShellState.cs`；`Desktop/Monitors/IMonitorManager.cs` + `MonitorManager.cs`（枚举 + DPI + 工作区；**仅主屏消费**）；`Desktop/Surfaces/ISurfaceHost.cs`、`IDesktopSurface.cs`、`ISurfaceContent.cs`、`ISurfaceTarget.cs`、`SurfaceEnums.cs`；`Core/Models/Monitor.cs`（`MonitorIdentity` + `MonitorRuntimeInfo` + `Monitor`，R2）；`Core/Abstractions/IDesktopBackdropService.cs` + `Core/Models/BackdropSpec.cs`（契约草案，UI 不接线）；`Core/Abstractions/ISceneService.cs`（契约占位，R4） |
| **C** | `ShellEventSource` + Desktop 生命周期（行为等价于现 `ShellLifecycleWatcher`） | 新增：`Desktop/Shell/ShellEventSource.cs`（隐藏窗口 + `TaskbarCreated` + `WM_DISPLAYCHANGE`）；`App/Services/Platform/ShellLifecycleWatcher.cs` 改为订阅者（保留激活消息接收）或删除并让 App 只保留激活小窗口；`App/Services/Platform/ShellInterop.cs` 拆分（激活部分留 App）；`App/TrayService` 改订阅 Desktop 的 `ShellRestarted` |
| **D** | `Win32SurfaceHost` / `DesktopShell`（机制，未接入视频） | 新增：`Desktop/Shell/DesktopShell.cs`（shell 线程 + 消息泵 + MountPolicy）；`Desktop/Shell/ShellWatchdog.cs`；`Desktop/Shell/DesktopLayerHost.cs`；`Desktop/Surfaces/Win32SurfaceHost.cs`；`Desktop/Layout/`（DIP↔像素最小换算）；`Desktop/Interop/NativeMethods.cs`、`D3D11Interop.cs`、`DesktopWorkerWindow.cs`（自 `DesktopHost/Interop` 平移 + 扩充） |
| **E** | `VideoSurfaceContent` + `IVideoWallpaperService` 兼容适配器（行为切换点） | 新增：`Desktop/Surfaces/VideoSurfaceContent.cs`（自 `DesktopHostSession` 的呈现/播放部分演化）；`Desktop/Surfaces/Compatibility/VideoWallpaperServiceAdapter.cs`；`App/Infrastructure/AppHost.cs` 注册切换；`App/App.xaml.cs` 恢复逻辑改走适配器（行为不变） |
| **F** | 删除旧实现，确认无双实现 | 删除：`src/Muralis.DesktopHost/DesktopHostSession.cs`、`DesktopVideoWallpaperService.cs`（及其余旧文件）；核对全仓无 `Muralis.DesktopHost` 残留 |
| **G** | 架构门禁 / 测试 / 诊断 | 新增：`tools/arch-check.ps1`（§3.3 的 1–3）；`tests/Muralis.Core.Tests` 增补纯逻辑用例（Monitor 身份匹配、DIP/锚点换算、`BackdropSpec` 序列化）；`Desktop/Shell` 结构化诊断日志（挂载/重挂载/拓扑事件） |

### 14.3 Phase 1 验收口径（与 v0.2.0 行为逐条对齐）

1. 视频壁纸：启动恢复、播放、静音切换、停止、Explorer 重启自动回挂、显示器尺寸变化重建——逐条一致（保留 `PresentedFrames` 诊断口做自动化验证）；
2. 静态壁纸：全屏/单屏应用、Fit 模式、注册表写入——不变；
3. 托盘、单实例、关闭到托盘、开机启动、轮换、下载、收藏、标签、主题、双语——不变；
4. 日志与 `StartupTrace` 阶段名不变（`docs/performance.md` 的基线方法继续有效）。

### 14.4 Phase 1 明确不做

Canvas、Widget、Dock、Media、Scene 实现、配置拆分、多显示器渲染（只做枚举统一与模型）、任何 UI 变化、静态壁纸 COM 迁移。

---

## 15. 已裁决事项与兼容承诺

### 15.1 Q1–Q7 裁决（R6）

| # | 裁决 |
| --- | --- |
| Q1 | **短期桌面层随 Muralis 主程序退出；不建立独立 Desktop Host 进程。** `IDesktopShell` 保持未来可跨进程演进的设计。 |
| Q2 | **同意** `Muralis.DesktopHost` → `Muralis.Desktop`。 |
| Q3 | 长期 Canvas 选择**路线 A**（Muralis 自己管理图标层）；但 **Phase 5 必须先实现非破坏 Preview**；接管 Windows 原生图标必须是**之后的独立可关闭功能**。 |
| Q4 | `.muralis` Scene **默认包含素材**；同时支持「仅引用」导出模式。 |
| Q5 | 新显示器默认 **FollowPrimary Scene**；**不复制**主显示器的 runtime layout override，新显示器从 Scene 默认布局生成自己的布局。 |
| Q6 | Widget 属于桌面交互视觉层，但**不得设计成永久覆盖普通应用窗口**；Canvas 接管模式启用后桌面图标由 Canvas 自己管理；**具体 Windows z-order / desktop-band 实现延后到原型验证**，不在 Blueprint 中伪装成已解决。 |
| Q7 | 文档采用单文件 `docs/architecture-v2.md`；Phase 3 之后若内容过大再拆为 `architecture/shell.md`、`scene.md`、`canvas.md`、`roadmap.md` 等。 |

### 15.2 兼容承诺（贯穿全部阶段）

1. v0.2.0 用户设置可无损迁移（Phase 3，失败不删旧文件）；
2. 静态壁纸与动态壁纸的现有行为在任一阶段结束时可用（Phase 1 起逐条回归）；
3. 删除 `state/` 目录永不导致用户配置丢失（Phase 3 起自动化验收）；
4. `IVideoWallpaperService` 的公开行为在 Phase 4 删除前保持可用（兼容适配器）。

### 15.3 本文件之后

Phase 0 到此结束。下一动作 = 你批准本文件后，按 §14 的 Commit A–G 执行 Phase 1；每一 Commit 单独可回退，且不改变用户可感知行为。
