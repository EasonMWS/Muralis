# Muralis Architecture V2 — Blueprint（Phase 0）

> **状态**：Phase 0 交付物，已按审核意见 R1–R7 修订；Phase 1（1A–1G）与 Phase 2（Desktop Canvas Prototype，重排后）已落地，记录见 §14、§16。
> **分支**：`architecture-v2` ｜ **日期**：2026-09-15（v2 重排 2026-09-16）｜ **基线**：v0.2.0（`d6bbe17`）
> **范围**：本文件只描述设计与迁移计划。Phase 0 不修改任何生产代码。

### 修订记录

| 版本 | 内容 |
| --- | --- |
| v0 | 初稿：现状盘点、模块关系、Shell/Surface/Monitor/Scene/Canvas/Widget/Media 设计、Roadmap |
| v1 | 按审核意见修订：**R1** interop 边界改为领域归属规则；**R2** `MonitorIdentity` 与 `MonitorRuntimeInfo` 拆分，Profile 只绑 `StableId`；**R3** Runtime State 必须可删除可重建，新增 Asset Bindings 持久层；**R4** Scene 不是 `BackdropKind`，引入 Scene 编排层；**R5** Canvas/Widget 渲染技术不提前锁定，Phase 1 只实现 Video 内容；**R6** 开放问题 Q1–Q7 已裁决（§15）；**R7** Phase 1 拆为 Commit A–G 且不迁移与 Desktop Shell 无关的 App native 代码 |
| v2 | 按 2026-09-16 决议重排 Roadmap：**Phase 2 = Desktop Canvas Prototype**（非破坏原型，先把 §9 的坐标模型与 §5.3 的渲染承载在真机上验证）；原 Phase 2（Monitor Model & Multi-monitor Surfaces）整块延后，不再是下一个阶段。§13.2 阶段表保留原文并加注重排说明；落地记录见 §16 |

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
> 2026-09-16 更新：坐标模型与渲染承载已由 Phase 2 的**非破坏原型**（§16）先行验证——WUC Composition 承载在 shell 的 Surface 窗口上，锚点 + DIP 偏移按 §9.1 实现；**接管 Windows 原生图标仍未实施**，本节其余设计不变。

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

> **2026-09-16 重排（v2）**：上表为原始计划，保留原文备查。事实执行顺序改为：**Phase 2 = Desktop Canvas Prototype**（本表之外的新阶段，落地记录见 §16）——先把 §9 的坐标模型与 §5.3 的渲染承载在真机上做**非破坏原型**，验证自由布局、悬停放大、弹性动画、边缘停靠与 DPI 适配之后再动多显示器。**原 Phase 2（Monitor Model & Multi-monitor Surfaces）整块延后**，不再是下一个阶段；其余阶段编号与范围暂不变（重排不改动 §9.3 的「Preview 先行、接管独立可关闭」顺序）。

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

### 14.5 Phase 1D 落地记录：临时兼容路径与 Phase 1E / 1F 删除计划

Phase 1D（DesktopShell & Win32SurfaceHost）落地后的事实：**旧恢复代码没有被保留** —— `DesktopHostSession` 已不含 shell 线程、WorkerW 查找、建窗、定位与重挂载逻辑；Explorer 重启的唯一响应者是 `DesktopShell`（消费 `ShellEventSource`），因此不存在「DesktopShell 与旧会话两边同时重挂同一个 HWND」的风险。Window class 名 `MuralisDesktopHostWindow` 保持不变。

仍存在的唯一临时兼容路径与删除方式：

| 兼容物 | 现状 | Phase 1E / 1F 删除方式 |
| --- | --- | --- |
| `IVideoWallpaperService`（Core 契约） | App 与动态壁纸页仍经它启动 / 停止 / 订阅状态 | 1E：页面与启动恢复改接 shell-facing 的 backdrop 服务（§11.2 `IDesktopBackdropService` 的最小落地实现），随后不再有生产引用 |
| `DesktopVideoWallpaperService`（Desktop 适配器） | 纯转发（`AddSurfaceAsync` / `RemoveSurfaceAsync` + 状态事件），无窗口 / WorkerW / 重挂载逻辑 | 1F：行为平移到 backdrop 服务后整体删除，并从 `AppHost` 移除注册 |
| `IVideoWallpaperService.NotifyShellRestarted()` | 保留为记录日志的空操作 | 1F：随接口删除；重启语义已由 `IDesktopShell.ShellRestarted` 承担 |
| `DesktopHostSession`（类名与位置） | 视频内容（`ISurfaceContent`）；1D 故意不删、不更名 | 1E / 1F：如需按蓝图改名为 `VideoSurfaceContent`，在同一步内完成引用替换（纯改名，零行为变化） |

删除后的门禁：全仓不再出现 `IVideoWallpaperService`；Desktop hosting 的窗口 / 桌面层 / 重挂载在任意时刻只有 `DesktopShell` + `Win32SurfaceHost` 一个 owner。

### 14.6 Phase 1E 落地记录：VideoSurfaceContent 与兼容适配器

Phase 1E 落地后的事实：

- 新增 `src/Muralis.Desktop/Surfaces/VideoSurfaceContent.cs`（内部 `ISurfaceContent` 实现）：进程内**唯一**的 MediaPlayer/DXGI 呈现代码 —— 加载、循环、静音、首帧确认、swapchain 创建/绑定、present 失败与设备丢失重建（每挂载世代仅重建一次，再失败报 `Failed`）。
- `DesktopHostSession` 缩水为 135 行会话粘合：start-with-app 恢复流程、启动超时与失败映射、停止，**不含**任何视频 / native 代码；它和旧适配器一起留给 Phase 1F 删除。
- 旧 `DesktopVideoWallpaperService.cs` 删除；兼容适配器按蓝图更名为 `src/Muralis.Desktop/Surfaces/Compatibility/VideoWallpaperServiceAdapter.cs`，行为与 v0.2.0 逐条一致（20 s 启动超时、异常映射、Stop 幂等、失败启动后不误报 Stopped）。
- HWND 归属：建窗 / 销毁只属于 `Win32SurfaceHost`；视频侧只经 `IWin32SurfaceTarget` 获得句柄，绝不 `CreateWindowEx`。Swapchain 属 VideoSurfaceContent，随挂载/几何变化/设备丢失重建，永不重建 host window。
- Explorer 重启链路：`ShellEventSource` → `DesktopShell` → Surface `Orphaned` → `Win32SurfaceHost` 重挂 → `VideoSurfaceContent` 得到新 target 后重绑 swapchain 继续播放；视频内容只感知「目标失效 / 新目标可用」，不感知 `TaskbarCreated`/`WorkerW`。
- 状态口径：Orphaned 不向用户报 `Stopped`（重启恢复不计为一次停止）；只有内容级失败（打不开、codec、设备丢失重建失败）才报 `Failed`。

Phase 1F 删除 `DesktopHostSession` 前的阻塞项：App 侧（动态壁纸页 ViewModel + 启动恢复）仍经 Core 的 `IVideoWallpaperService` + 上述适配器消费视频；需先按 §11.2 落地 `IDesktopBackdropService` 的最小实现并切换消费方，之后才能删除 `DesktopHostSession`、`VideoWallpaperServiceAdapter`、`AppHost` 注册与 `IVideoWallpaperService` 契约本身。

### 14.7 Phase 1F 落地记录：legacy DesktopHostSession 已删除

**`DesktopHostSession` 已删除。** Phase 1E 遗留的会话粘合（发起挂载、等首帧、20 s 超时、失败映射、停止）并入 `VideoWallpaperServiceAdapter`，适配器直接对接 `IDesktopShell` —— 它仍不含线程、窗口、WorkerW 查找与重挂载逻辑。

- 真实生产链（唯一）：

  ```text
  App / ViewModel（动态壁纸页、启动恢复）
      ↓  IVideoWallpaperService（UI compatibility API）
  VideoWallpaperServiceAdapter
      ↓  IDesktopShell.AddSurfaceAsync / RemoveSurfaceAsync
  DesktopShell（线程、注册表、重挂载、watchdog）
      ↓  Win32SurfaceHost（唯一建/销窗口处）
  VideoSurfaceContent（MediaPlayer / DXGI / Present）
  ```

- `IVideoWallpaperService` 现存唯一用途 = **UI compatibility API**：在 `IDesktopBackdropService` 正式接管 UI 之前，动态壁纸页与启动恢复仍经它工作；接口上的 `NotifyShellRestarted` 已删除（无任何生产调用者，重启语义只属于 `ShellEventSource` → `DesktopShell`）。
- 日志分层使用现有 Serilog category，不新增机制：视频事件（playing / display changed / media failed / device lost）由 `VideoSurfaceContent` 自己的 category 记录，壳层事件（window lost / re-mounted / worker）由 `DesktopShell` 记录，适配器只记录 start/stop 与启动失败。
- 架构门禁（`tests/Muralis.Desktop.Tests/Architecture/ArchitectureGuardTests.cs`，11 条，断言失败信息给出违规文件）：无 `DesktopHostSession` 类型与源码引用；桌面窗口只在 `Surfaces/Win32SurfaceHost.cs` 创建（唯一例外：`Shell/ShellEventSource.cs` 自己的隐藏监听窗口）；`SetParent(` 只在 `Win32SurfaceHost`；`MediaPlayer` 只在 `Surfaces/VideoSurfaceContent.cs`；WorkerW 探测（`NativeMethods.FindWindowExW(`、`NativeMethods.EnumWindows(`、`"WorkerW"` 字面量、`SHELLDLL_DefView`）只在 `Interop/DesktopWorkerWindow.cs`；`TaskbarCreated` 只在 `Shell/ShellEventSource.cs`；`VideoSurfaceContent` 不出现 `WorkerW`/`SHELLDLL_DefView`/`CreateWindowEx`/`SetParent`/`TaskbarCreated`/`ShellRestarted`；`Shell/` 目录无 `MediaPlayer`/`IDXGISwapChain`/`VideoFrameAvailable`/`CopyFrameToVideoSurface`；App 不出现 `WorkerW`/`SHELLDLL_DefView`/`MuralisDesktopHostWindow`/`Win32SurfaceHost`/`DesktopLayerHost`/`SetParent(`；Core 不引用 `Muralis.Desktop`/`Microsoft.Windows.SDK.NET`/`WinRT.Runtime`/`Microsoft.UI.Xaml`；Desktop 不引用 `Microsoft.UI*`/`Microsoft.WindowsAppSDK*`/`Muralis.App`。源码扫描先剥离注释（允许注释里解释规则），token 使用调用点形式（`NativeMethods.…(`）与引号字面量，避免被类型名（如 `DesktopWorkerWindow`）误报。

### 14.8 Phase 1G 落地记录：最终验证（Phase 1 收口）

Phase 1G 不做新的架构迁移；本轮把 Phase 1 成果按所有权、线程模型、资源生命周期、显示拓扑、性能、功能回归六个口径集中验证。结论基于 Debug/Release 双配置 0 警告构建 + 247/247 测试 + Release 产物真机实测。

**所有权审计（每项职责单一所有者，均有 xunit 门禁持续断言）**

| 职责 | 唯一所有者 |
| --- | --- |
| 桌面层发现（WorkerW / SHELLDLL_DefView） | `Interop/DesktopWorkerWindow.cs` |
| 宿主窗口创建 / 销毁 / 挂载（CreateWindowEx / SetParent） | `Surfaces/Win32SurfaceHost.cs`（唯一例外：`ShellEventSource` 自己的隐藏监听窗口） |
| 窗口重挂载 / watchdog / 挂载注册表 | `Shell/DesktopShell.cs` |
| Explorer 重启侦测（TaskbarCreated） | `Shell/ShellEventSource.cs` |
| 视频渲染（MediaPlayer / DXGI / Present） | `Surfaces/VideoSurfaceContent.cs` |
| 显示器枚举 / 主屏探测 | `Monitors/MonitorManager.cs`（唯一构造点：`DesktopShell`） |
| UI 兼容 API + 视频状态事件 | `Surfaces/Compatibility/VideoWallpaperServiceAdapter.cs` |

无 `DesktopHostSession`、无第二处 WorkerW 发现、无第二处 MediaPlayer、无第二处 `CreateWindowEx` 宿主、无 legacy fallback。

**线程模型审计**

- UI 线程无 native 阻塞：启动恢复是 fire-and-forget，全部等待在后台；
- 适配器全 async（`SemaphoreSlim` 串行化、20 s 启动超时、`ConfigureAwait(false)`）；
- 窗口 / 挂载 / 重挂载只发生在 “Muralis desktop shell” 线程的消息泵上；
- 媒体回调只做呈现与状态上报，不改变 shell 所有权；shell 线程不做解码；
- 停止路径单向（适配器 → shell `RemoveSurface` → 内容 `Dispose`），无 dispose 死锁路径；本轮未发现需要修改的线程模型问题。

**资源生命周期（host-verify 压测，Release）**

- Start/Stop ×10：句柄 711 → 731（+20）、线程 21 → 21、壁纸层子窗口 0 → 0、每轮无宿主窗口残留；
- Start → Explorer 重启 → 自动重挂 → Stop ×2 轮：重挂 61–70 ms、恢复播放 ~245 ms、重启窗口期内无用户可见 `Stopped`、静态壁纸不受影响；
- 首帧 524 ms、持续 30 fps、停止 6–17 ms。

**显示拓扑（单屏）**

- 2560×1440 → 1600×900 → 2560×1440 全链路：`WM_DISPLAYCHANGE` 被 shell 捕获、宿主窗口几何跟随、视频重建并继续解码（1.28–1.36 %）、静态壁纸不动；
- **单屏已验证，多屏 / 热插拔 / 混合 DPI 留 Phase 2 实测**（不伪造多屏结论）。

**性能重基线（Release，对比 `docs/performance.md` 的 v0.2.0 基线）**

| 场景 | 指标 | v0.2.0 基线 | 1G 实测 | 判定 |
| --- | --- | --- | --- | --- |
| 1080p30 播放（静音） | CPU 均值 / 峰值 | 0.65–0.73 % / 1.11–1.30 % | 0.317 % / 1.302 % | 更好 |
| | GPU 均值（sum over engines） | 2.24–2.27 %（decode 1.50–1.53 %） | 1.75 %（decode 1.24 %） | 更好 |
| | 工作集 / 私有 | 267–270 MB / 236–238 MB | 283.6 MB / 251.6 MB | +5 %，噪声带内 |
| 空闲（无视频） | CPU / GPU | 0.026 % / 0 % | 0 % / 0 % | 更好 |
| | 工作集 / 私有 | 184.7 MB / 125.8 MB | 197.2 MB / 131.8 MB | +6.8 %，噪声带内 |
| 窗口出现 | | 571 ms | 519–656 ms | 持平 |

无 >10–15 % 的稳定性回退；主要指标下降。空闲工作集 +7 % 与运行间波动同量级（1E 实测 189.7 MB → 1G 197.2 MB），不构成回退。

**功能回归（v0.2.0 能力逐族，均为 Release 真机脚本）**

- 视频壁纸族：启动恢复、动态壁纸页播放/停止、硬杀恢复与残留清理、分辨率切换、Explorer 重挂、单实例唤醒 — 全绿；
- 静态壁纸族：应用 / Fill / 恢复 — 全绿（在线条目未下载时“设为桌面壁纸”禁用与 v0.2.0 一致：`DetailViewModel` 与 v0.2.0 逐字节相同）；
- 图库 / 下载族：导入 3 图、下载、应用、收藏、重启持久化 — 全绿；
- 设置族：自动轮换开/关 + 立即换图、开机启动（Run 键增删）、关闭到托盘、主题 Dark ↔ System、语言 zh ↔ en 运行时切换并回跟随系统 — 全绿。

自 v0.2.0 以来 `Muralis.App` 的差异仅 7 个壳层生命周期相关文件（47 插入 / 69 删除），功能页与 ViewModel 未被触碰。

**与 Blueprint 的偏差（记录，不改历史）**

- §14.2 Commit G 原计划 `tools/arch-check.ps1`；实际以 `tests/Muralis.Desktop.Tests/Architecture/ArchitectureGuardTests.cs`（11 条 xunit 事实）落地，随测试套件自动执行、失败信息给出违规文件。
- §1 现状盘点与 §14.5–14.7 历史过程记录中的旧名称（`Muralis.DesktopHost`、`DesktopHostSession`）保持原文，不回改。

**已知限制 / 诚实记录**

- 本轮测试环境为单显示器（2560×1440@300、32 bpp）；多屏结论未伪造；
- 测试窗口内出现过一次不可复现的启动异常（进程存活、未写日志、无崩溃记录），其后同类启动 20+ 次全部正常，未再出现；
- 三个 UI 回归脚本（m4 / m3_verify / m2a）因脚本自身滞后于 UI 演进（导航新增 Downloads / Dynamic wallpaper、语言项本地化、下载完成等待）做了测试侧修正后全绿；应用代码未因测试改动。

**Phase 1 完成条件**：全部达成（构建 / 测试 / 门禁 / 生命周期 / 性能 / 功能回归 / 单屏拓扑）。下一阶段入口 = Phase 2。（2026-09-16 重排：实际下一阶段为 Desktop Canvas Prototype，见 §16；多显示器阶段延后，见 §13.2。）

### Phase 1 状态（1A–1G）

| Phase | 内容 | 状态 |
| --- | --- | --- |
| 1A | `Muralis.DesktopHost` → `Muralis.Desktop` 纯机械改名 | 完成（`0d3a50c`） |
| 1B | Desktop Shell / Monitor / Surface 契约与数据模型 | 完成（`7f3ca4b`） |
| 1C | `ShellEventSource` + 桌面生命周期集中 | 完成（`ff4be93`） |
| 1D | `DesktopShell` / `DesktopSurface` / `Win32SurfaceHost` / `PrimaryDisplayProbe` | 完成（`8deee28`、`146953d`、`7fe5e41`） |
| 1E | `VideoSurfaceContent` + `VideoWallpaperServiceAdapter` | 完成（`5184b84`、`2a0f86a`） |
| 1F | 删除 legacy `DesktopHostSession` 与会话粘合 | 完成（`5724f85`、`2dfecb3`） |
| 1G | 最终验证（所有权 / 线程模型 / 生命周期 / 拓扑 / 性能 / 回归） | 完成（见 §14.8） |

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

---

## 16. Phase 2 落地记录：Desktop Canvas Prototype（2026-09-16）

> 2026-09-16 决议：Phase 2 重新定义为本落地记录描述的 **Desktop Canvas Prototype**；原 Phase 2（Monitor Model & Multi-monitor Surfaces）整块延后（见 §13.2 重排说明）。本阶段**不做**原生图标接管——目标是先把画布的坐标、渲染、动画、输入在真机上验证为非破坏原型。
> 结论基于 Debug/Release 双配置 0 警告构建 + 全部测试（Core 281 / Desktop 48）+ 本机真机实测（单屏 2560×1440 @96 DPI，scale 1.0）。

### 16.1 交付内容

- **`CanvasSurface`（`ISurfaceContent` 的第二个实现）**：在 Phase 1 的 `DesktopShell` / `SurfaceHost` 之上新增 `CanvasSurfaceContent`。画布只负责视觉内容、布局、输入、动画、拖拽；桌面层发现、宿主窗口创建、挂载 / 重挂载、显示器信息全部复用 shell——画布**不找 WorkerW、不建 shell 生命周期、不听 Explorer 重启、不做显示器枚举**（由 `ArchitectureGuardTests.CanvasContentDoesNotTouchTheDesktopLayer` 持续断言）。
- **原型项 8 个**：4 个自由项（Steam / Chrome / Blender / ComfyUI，围绕显示中心一行）+ 4 个 Dock 项（Files / Music / Settings / Terminal）。只有 Id / Name / IconKey / Placement / Anchor / Offset / Size / Z，不扫描 `.lnk`、不启动真实应用。
- **自由布局 + 拖动 + 重启恢复**：任意位置拖动，落点保存为锚点 + DIP 偏移；布局独立存 `%LOCALAPPDATA%\Muralis\desktop-canvas-prototype.json`（**不进** `settings.json`），删除即回到种子布局。
- **悬停放大（距离连续）**：`scale = 1 + (MaxScale − 1) · falloff(d / radius)`，默认 `MaxScale 1.6`、`InfluenceRadius 120 DIP`、`smoothstep` 衰减；Dock 轨道用更紧的 `1.5 / 120`。
- **弹性动画**：Microsoft.UI.Composition 的 `SpringVector3NaturalMotionAnimation`（周期 / 阻尼比在 JSON 里），无逐帧代码。
- **左边缘 Dock 原型**：触发带（24 DIP）→ 延迟 150 ms 展开；离开 → 延迟 600 ms 收起；收起 / 展开倍率、间距、边距全部来自 JSON，无魔数。
- **诊断（仅 Debug）**：Dynamic wallpaper 页的折叠面板显示 surface 状态 / mount 次数 / 显示器 / DPI / 画布 DIP / 指针位置 / 项数 / 悬停项与倍率 / Dock 相位与目标倍率 / 更新率 / 布局路径。
- **非破坏**：不隐藏原生图标、不动桌面 ListView、不 hook Explorer、不注入；开关关闭即完全移除画布窗口与命中区域（`ArchitectureGuardTests.NothingDrivesOrHidesTheNativeDesktopIcons` 与 `TheAppDoesNotTouchTheDesktopLayer` 持续断言）。

### 16.2 关键实现决定

| 决定 | 内容 | 理由 |
| --- | --- | --- |
| 渲染技术 | **Microsoft.UI.Composition**（WUC）承载在 shell 提供的 Win32 窗口上 | §5.3 要求渲染技术中立；WUC 自带弹性动画与合成器线程，天然避免「驻留 60 fps 轮询」（§九 禁止项） |
| 输入模型 | 画布窗口用 **Region** 把可命中区域限制为「项的放大外接矩形 + Dock 轨道 + 触发带」 | 桌面其余位置仍是真正的桌面：原生图标照常双击，画布不吞桌面输入、不抢焦点（`WS_EX_NOACTIVATE`） |
| 坐标 | 锚点（9 宫格）+ DIP 偏移：`pixel = AnchorPoint + offset · scaleFactor`；拖动落点求逆 | §9.1 的坐标方案落地并被单测覆盖（含 1280×720 / 1920×1080 / 2560×1440 / 1.25x / 1.5x / 2x 矩阵） |
| 悬停 | 距离驱动、连续；变化时才写合成器动画目标值 | 连续放大无跳变；静止时零更新 |
| Dock 状态机 | `CanvasDockAutoHide`：纯状态机 + 一次性定时器（Show / Hide Delay），无轮询、无时钟 | §八 参数全部来自 JSON；离开后 600 ms 收起、重新进入即取消收起的截止时间 |
| 诊断 | 不可变快照值对象，变化时整体替换；显示端跨线程读不需锁画布 | §十三；`DockScale` 报告相位目标值（Composition 属性 getter 只返回基值，读动画中的实际值不可行） |
| 布局文件 | 独立 JSON + 校验器 + 损坏回退种子 | §三：原型布局与 AppSettings 彻底分开 |

### 16.3 逐条验收（用户下达的 15 步演示，全部实测通过）

| 验收点 | 实测结果 |
| --- | --- |
| 开启 → 6–8 个图标项出现 | 8 项挂载为桌面图标宿主的子窗口（z 序在 `SHELLDLL_DefView` 之上），exstyle `0x8200088`（不激活 / 不占任务栏 / 无重定向位图） |
| 自由拖动 | Steam 从中心锚点拖到 +65 / −60 DIP，落点与保存值逐位一致；其他项不动 |
| 重启后位置恢复 | 全新进程启动后，该项在完全相同的桌面坐标 (1345, 660) 出现 |
| 悬停放大 | 指针进入行内：Steam 1.6x，邻居按距离衰减；像素级证据：放大后距中心 55 DIP 的探针点被瓦片覆盖（`27,40,56`），离开后同一探针点无内容（`0,0,0`） |
| 连续 50 次指针移动的即时读回 | 50/50 全部看到指针在内（测试侧需过滤被覆盖应用触发的系统 WM_MOUSELEAVE，见 16.5） |
| Dock 自动隐藏 | 隐藏时轨道区域在 Region 之外（不挡桌面）；指针进入触发带 → `Shown`、倍率 1.00、面板可像素级看到（`16,19,23`）；离开 600 ms → `Collapsed`、轨道区域移出 Region、面板消失（`255,255,255`） |
| 点击 ≠ 移动 | 原地点击不写布局文件 |
| 非破坏 | 关闭开关：画布窗口消失、静态壁纸不变、WorkerW 数量 11 → 11、图标宿主与图标视图句柄不变、宿主子窗口逐句柄逐一相同 |
| Explorer 重启恢复 | 杀掉 Explorer：1 次尝试内自动重挂，mount 1 → 2，期间 0 个 `Stopped` 状态事件；重挂后布局 8 项、exstyle、Region、z 序全部保持 |
| 性能 | 静置 3 s：**0–15.6 ms** CPU（0–0.5 % 单核），静止期仅诊断需要的 ~2 次/秒更新；30 步悬停扫过：**15.6–109.4 ms / 约 1 s 移动**；句柄 352 → 352（无泄漏） |
| GPU | 画布进程 0 %（低于计数器分辨率）；dwm 仅在一次桌面 churn 中出现单样本 3.2 %，动画本身未产生可测 GPU 负载 |
| 诊断面板（应用内，Debug） | `surface Running · mount 1 · Active`；`monitor \\.\DISPLAY5 · 2560x1440 at 0,0 · 96 dpi (1x)`；`items 8 · hovered none at 1.00x`；`dock Collapsed at 0.00x` |
| 应用内开关 | Dynamic wallpaper 页开关打开后画布落到桌面、关闭后移除；`settings.json` 复原后逐字节一致、原型布局文件删除 |

### 16.4 测试与提交

- 新增测试：`CanvasAnchorMathTests`（12）、`CanvasLayoutTests`（18）、`CanvasProximityTests`（9）、`CanvasRailLayoutTests`（10）、`CanvasDockAutoHideTests`（9）——布局序列化 / 校验、锚点与 DIP 往返（含拖动落点求逆）、悬停倍率与衰减、轨道几何、自动隐藏状态机；`ArchitectureGuardTests` 新增 2 条画布门禁；`SettingsServiceTests` 覆盖画布开关的持久化与恢复。Debug / Release 全绿（Core 281 / Desktop 48）。
- 提交（`architecture-v2` 分支）：`5ce8b44` 布局模型与原型存储 → `0b689d3` 图标层之上的交互面放置 → `1f019c7` 画布原型面 → `2823b59` 应用接线（开关 / 本地化 / 启动恢复）→ `adee81a` 测试 → `0dd4c77` 诊断修复（见 16.5）。

### 16.5 已知限制 / 诚实记录

- **单屏结论**：全部实测在 2560×1440 @96 DPI 单屏上完成；多屏 / 混合 DPI / 热插拔留给后续阶段，未伪造结论。
- **画布不抢全局输入**：桌面被其它窗口遮住的位置收不到鼠标消息（画布没有全局钩子，也不打算有）。原型接受；实机验收脚本为此在测试侧做了消息过滤（见下条）。
- **测试侧过滤器**：真实指针停在覆盖应用上时，系统会向画布投递真实的 `WM_MOUSELEAVE`，把所有合成指针状态清掉——验收工具因此在 shell 线程上挂了一个 `WH_GETMESSAGE` 过滤器（把被投递的 leave 改写为 `WM_NULL`）。这是测试工具的行为，**不在产品代码里**。
- **Dock 只接通 Left 边**：`CanvasDockOptions` 定义了四条边，实际接线与实测只有 Left。
- **原型图标是占位字形**，不是真实应用图标；不扫描快捷方式、不启动应用、无右键菜单 / 多选 / 框选（本阶段明确不做）。
- 诊断面板仅 Debug 构建可见（`IsDiagnosticsAvailable`）；快照随变化刷新，可能滞后输入一条消息。

### Phase 2 状态

| 内容 | 状态 |
| --- | --- |
| Phase 2 = Desktop Canvas Prototype（重排后） | 完成（见 §16.3；提交见 §16.4） |
| 原 Phase 2 = Monitor Model & Multi-monitor Surfaces | 延后（§13.2 重排说明） |

## 17. Phase 3A 落地记录：Desktop Input Foundation（2026-09-16）

> 2026-09-16：Phase 3 的第一段，**只做输入基础设施**——画布此前只在自己窗口的命中区域里稳定收到指针消息，一旦指针跨过项之间的空隙、或离开区域，悬停 / Dock 邻近 / 自动隐藏轨迹就断掉。本阶段把这件事从根上换掉：先审计现有指针路径与 Windows 的几种输入方案，再落地统一的 `DesktopPointerRouter`，并验证它与 WUC 视觉、命中区域、普通应用窗口、Explorer 重启、资源释放的关系。
> **本阶段不做**：真实 `.lnk` 扫描、启动真实应用、隐藏 Windows 原生图标、改 Explorer 的 ListView、正式 Dock、Scene / Widget / Music / FFT、多屏扩展。**Phase 3B 未开始。**
> 结论基于 Debug / Release 双配置 0 警告构建 + 全部测试（Core 281 / Desktop 84）+ 本机真机实测（单屏 2560×1440 @96 DPI，scale 1.0）：45 项真机检查全部通过。

### 17.1 交付内容

- **方案审计与选型**：对比了四条路——(a) 现状：只靠画布窗口的 `WM_MOUSEMOVE`；(b) `GetCursorPos` + `WindowFromPoint` 高频轮询；(c) `WH_MOUSE_LL` 全局钩子；(d) 原始输入 `RIDEV_INPUTSINK` + 隐藏窗口定位读取。选定 (d)，理由与代价见 17.2。
- **`DesktopPointerRouter`（新目录 `src/Muralis.Desktop/Input/`，12 个文件）**：整个桌面唯一监听指针的地方。在 shell 线程的隐藏顶层窗口上注册鼠标原始输入，读取光标与按键，判断指针所落上下文并发布。消费者只有三个事件：`PointerMoved` / `EnteredDesktopRegion` / `LeftDesktopRegion`，外加 `Current` 与 `Stats`。
- **三种上下文**：`Foreign`（普通应用、任务栏等）/ `Desktop`（`SHELLDLL_DefView` / `WorkerW` / `Progman` 链）/ `Surface`（我们自己的 `MuralisDesktopHostWindow`）。**Foreign 期间不发布任何移动事件**，只发一次 `LeftDesktopRegion`。
- **合并与节流**：原始报告只当"该读了"的信号，读取发生在一次性 flush 定时器里；突发在一次读取中合并，两次发布之间至少 6 ms（`PointerDispatchGate`）；burst 未结束则补一次尾包读取（见 17.2）。
- **画布接线（`CanvasSurfaceContent`）**：挂载时订阅 + `SampleOnce()`（挂载瞬间指针可能已经停在画布上），卸载时退订；窗口消息保留为区域内的第二条路径与无路由器时的回退，`WM_MOUSELEAVE` 在路由器在线时不再清空状态。
- **命中区域与命中测试**：窗口 region 仍是"各项 `Proximity.MaxScale` 外接矩形 + 轨道（可见时）+ 24 DIP 触发带"的**静态**并集；`InsideItem` 按**动画中的倍率**判定，两者不再互相将就（见 17.2）。
- **诊断**：Debug 面板新增 `router <上下文> · N.N/s · N reports · N dispatches`（`CanvasDiagnosticsSnapshot` 新增四个字段，仍由适配器读取，画布不碰路由器统计）。
- **架构门禁**：`OnlyThePointerRouterRegistersRawInput`（全仓只有路由器能注册原始输入）、`NothingInstallsGlobalHooks`（仓库里不允许出现任何 `SetWindowsHookEx` / `WH_MOUSE_LL` / `WH_KEYBOARD_LL`）。
- **真机验收脚本 `tools/p3a-pointer-verify.ps1`**：用 `SendInput` 驱动真实指针（与物理鼠标走同一套输入栈，原始输入同样收得到），用 UI Automation 读诊断面板，覆盖 14 组场景（启动 / 前置检查 / Test 1–12）共 45 项检查。

### 17.2 关键实现决定

| 决定 | 内容 | 理由 |
| --- | --- | --- |
| 指针方案 | **原始输入 `RIDEV_INPUTSINK`**，注册在 shell 线程的隐藏顶层窗口上 | 被动注册：不捕获、不消费、不改变任何其它窗口收到的输入；公共文档 API，无注入无驱动，Store / 杀软零红灯；`WM_INPUT` 之后照常走 `DefWindowProc`，系统才会释放原始输入缓冲 |
| 不用全局钩子 | 明确不装 `WH_MOUSE_LL`，并用门禁测试锁死 | 钩子在输入路径上拦截，可能拖慢或干扰其它应用的输入，风险与本阶段收益不成比例 |
| 不用高频轮询 | `GetCursorPos` 只作为事件驱动的补充（`SampleOnce`），仓库里没有 60 fps 循环 | 静止时零读取、零 CPU；`Idle` 实测 5 s 内 0 个报告、0.0 ms CPU |
| 一次报告 ≠ 一次读取 | 报告只触发一次"flush"（`SetTimer` 请求 6 ms，系统取整到 ≈15.6 ms）；**读取只发生在 flush 回调里**，绝不在 `WM_INPUT` 处理中读 | 报告可能先于系统应用该次移动到达：在 `WM_INPUT` 里读会永远慢一拍（本阶段真机复现过，见 17.5） |
| burst 尾包复读 | flush 时若仍有报告被吞（burst 未结束），发布后再补一次读取 | 读取仍可能与 burst 最后一次移动竞争：真机 40 回合里曾出现 19/20 的尾包丢失；补一次后 40/40，代价是每个 burst 最多多一次读取（位置没变就不发布） |
| 桌面判别 | 从 `WindowFromPoint` 沿父链上行（≤32 层）：先认自家窗口 → `Surface`，再认 shell 桌面类 → `Desktop`，其余 → `Foreign` | 画布窗口是图标宿主的**子**窗口，必须先于桌面类被认出来；类名只在 `DesktopWorkerWindow.IsDesktopLayerClass` 一处定义 |
| 普通应用窗口 | 上下文一变 `Foreign` 就停止发布移动，并只补一次 `LeftDesktopRegion` | 用户在浏览器里移动不应该让桌面付出任何代价（用户明确要求） |
| 无订阅者 | 没有订阅者时连报告都不计数、不读取 | 开关关闭后桌面上没有画布，指针成本严格为零 |
| 命中区域 vs WUC 视觉 | region = 各项 **max-scale** 外接矩形的并集（+ 轨道 + 触发带），只在挂载 / 落点 / Dock 相位 / 显示器变化时重建；点击判定用**动画中的** `view.Scale` | 视觉永远不会越出 region（悬停最多放大到 `MaxScale`），所以 region 不需要逐帧重建；反过来放大中的项在整个放大面上都可点，不会出现"图标看着大、可点区域还是原来的小框" |
| 线程模型 | 全部在 shell 线程上：注册、窗口过程、每次发布与每次 `SampleOnce` | 与 §1G 的线程模型一致；事件按顺序发布，消费者无需自己加锁；Explorer 重启不销毁 shell 线程，路由器因此原地不变 |

### 17.3 逐条验收（用户下达的真机清单 + 本阶段补充，全部实测通过）

| 验收点 | 实测结果 |
| --- | --- |
| 快速扫过一行，悬停连续、从不归零 | 行内 28 个采样：1.43 → 1.60（四个项中心）→ 1.47（项之间谷底）→ 1.39（区域边缘）；相邻最大跳变 **0.080**（阈值 0.12） |
| 同一扫掠抬到行上方 120 DIP（窗口 region 之外） | 28/28 采样上下文仍是 `Desktop`；悬停 1.11–1.21，与几何期望的 1.21 逐位一致 —— 这正是本阶段要修的那件事 |
| 离开画布 | 三个空白探针全部 `hover none / 1.00x`，Dock 保持 `Collapsed` |
| 左边缘触发带 | 进入触发带 → `Shown`；离开 600 ms → `Collapsed` |
| 普通应用窗口 | 指针移到应用窗口上：上下文 `Foreign`、画布悬停清空；任务栏同样 `Foreign`；指针回到桌面立即恢复 1.21x |
| 命中测试 | region 外 (1615,500) 命中桌面本体（root `Progman`）；放大后的项框内按下拖动 → 落点写入布局文件；region 外按下 → 布局文件不变 |
| ≥30 s 连续快速移动 | 1150 次移动 / 30.45 s；进程 CPU **1296.9 ms**（42.59 ms/s = 单核 **0.177 %**，24 核机器）；GPU 最大 **0.0 %**；报告 1006 → 发布 1006；发布率 avg **42.7/s**、max **64.0/s** |
| 静止 | 5 s 内 **0 个报告**；进程 CPU **0.0 ms**（面板打开）/ 15.6 ms（面板关闭，一次系统 tick 的杂项） |
| Explorer 重启 | `mount 1 → 2`，画布自动重挂；路由器窗口类名不变（**同一个窗口，未重挂**），重启后悬停正常 |
| 退出释放 | 原始输入注册已释放、**0** 个路由器窗口、**0** 个画布窗口；日志：attach 恰好 1 次、release 恰好 1 次 |
| 突发不丢尾包 | 40 回合 × 41 次连续移动，最终位置 **40/40** 命中 |

### 17.4 测试与提交

- 新增测试：`DesktopPointerRouterTests`（20）、`PointerWindowClassifierTests`（9）、`PointerDispatchGateTests`（5）——报告 / flush 契约、合并与尾包、三种上下文与父链判别、发布门限；`ArchitectureGuardTests` 新增 2 条门禁。Desktop 测试 48 → **84**，Core 281，Debug / Release 均 0 警告 0 错误。
- 提交（`architecture-v2` 分支）：`63e15f3` feat: add desktop pointer routing（含验收脚本 `tools/p3a-pointer-verify.ps1`）+ 本文件。

### 17.5 已知限制 / 诚实记录

- **单屏结论**：与 Phase 2 相同，全部实测在 2560×1440 @96 DPI 单屏上完成；多屏 / 混合 DPI 未测。
- **"latency" 的读法**：验收脚本读的是诊断面板文本，面板按 250 ms 刷新，因此 3–289 ms 的观测值是**面板节奏**，不是路由延迟。路由延迟由构造保证：报告 → 读取 ≤ 一个计时器 tick（≈15.6 ms），尾包再多一个 tick；发布率上限由闸门（6 ms）与计时器粒度共同限制在 ≈64/s（实测 max 64.0/s）。
- **曾经慢一拍**：第一版在 `WM_INPUT` 处理里直接读光标，真机上每个点都读到上一个位置（离区域越远越明显）。这是本阶段最重要的修复，flush 机制与 `TheReadingIsTakenWhenTheFlushFiresNotWhenTheReportArrives` 测试都为此存在。
- **原始输入注册失败时**：路由器降级为"只有窗口消息"，画布仍然可用但重新受 region 边界限制；日志会记一条 warning（`Attach` 永不抛出）。
- **只有鼠标**：键盘、触摸、笔不在此阶段范围内。
- **验收脚本会最小化挡住画布的窗口**：结束时逐个恢复（本轮已获得授权）；它只移动真实指针，不读取用户屏幕内容。

### Phase 3 状态

| 内容 | 状态 |
| --- | --- |
| Phase 3A = Desktop Input Foundation | 完成（见 §17.3；提交见 §17.4） |
| Phase 3B 及以后 | 未开始（按指令停在 3A） |

## 18. Phase 3B 落地记录：Real Desktop Items & App Launching（2026-09-16）

> 2026-09-16：Phase 3 的第二段。Phase 2/3A 的画布项只是瓦片——一个字形、没有能打开的东西；原型文件里的 Steam / Chrome / Blender / ComfyUI 是每次启动都强行出现的假程序。本阶段把画布项升级成**真正的桌面项**：可以代表一个程序、一个 `.lnk`、一个文件夹、一个文件或一个网址，有真实图标，双击经 shell 打开，目标不在了只标记不删除。
> **本阶段不做**：隐藏 Windows 原生桌面图标、自动扫描/导入整个桌面、Shell 上下文菜单正式版、右键扩展、把文件拖进桌面、多选、框选、重命名、删除真实文件、Scene / Widget / Music / FFT / Cloud。**Phase 3C 未开始。**
> 结论基于 Debug / Release 双配置 0 警告构建 + 全部测试（Core 366 / Desktop 125，共 491）+ 本机真机实测（单屏 2560×1440 @96 DPI，scale 1.0）：演示 58 项检查、50 项性能 9 项检查全部通过。

### 18.1 交付内容

- **DesktopItem 与 Target 模型**（`src/Muralis.Core/Desktop/`，10 个文件）：`DesktopItem`（Id / Name / Target / IconKey / Placement / Anchor / 锚点偏移 / SizeDip / Z / IsVisible，外加 `IsMissing()`、`Validate()`、`Clone()`）；`DesktopItemTarget` 抽象基类 + `ApplicationTarget` / `ShortcutTarget` / `FileTarget` / `FolderTarget` / `UrlTarget`，用 `System.Text.Json` 多态序列化（判别符字段 `kind`），未知 kind 直接拒绝加载——不是把一切塞进一个字符串路径。
- **清单生成**：`DesktopItemFactory`（路径 → item：名称取文件名，id 取 kind 前缀 `app_`/`lnk_`/`dir_`/`url_`/`file_` + 8 位随机；同一程序添加两次就是两个 item）、`DesktopItemPlacer`（新 item 落在离屏幕中心最近的空位，130 DIP 步距，存的是锚点偏移而不是像素）、`DesktopItemLaunch`（纯路由：location + `open` verb）。
- **持久化升级（spec §八 的迁移点就在本阶段）**：`DesktopLayout`（schema 2，`kind: "muralis.desktopLayout"`）写入 `%LOCALAPPDATA%\Muralis\desktop\layout.json`，与 `settings.json` 彻底分开；`DesktopLayoutStore` 原子写（临时文件 + `Move`），解析失败或校验不过的文档改名 `.bad` 而不是删除；`DesktopLayoutMigrator` 把 Phase 2 的 `desktop-canvas-prototype.json` **读一次**——参数（悬停 / 运动 / Dock）带过来，原型瓦片不迁移（它们没有 target），旧文件改名 `.v1.bak` 留在原地。迁移失败时旧文件一字不动，桌面从空布局开始。
- **图标层**（`src/Muralis.Desktop/Icons/`，5 个文件）：`ShellIconReader`（`SHGetFileInfoW` 取图标索引 → `SHGetImageList` 取图像列表 → `IImageList.GetIcon` 取句柄 → `GetDIBits` 复制像素 → 立刻 `DestroyIcon` / 释放 COM；结果统一为预乘 BGRA）、`IconBitmap`、`IconBitmapCache`（单 STA 工作线程 + 32 MB 预算 LRU + 请求去重）、`IconSurface` / `IconSurfaceDevice`（把像素上传成合成表面，配合 `CompositionBootstrap` 与 `D3D11Interop`）。`CanvasSurfaceContent` 只认像素：门禁测试禁止它出现任何 shell 图标调用。
- **启动**：`IDesktopItemLauncher` + `DesktopItemLaunchResult`（`Launched` / `Missing` / `Failed`）；`ShellItemLauncher` 用 `Process.Start(new ProcessStartInfo(location) { UseShellExecute = true, Verb = "open" })` 走系统关联，在线程池上执行，结果回投 shell 线程。全仓没有第二处 `Process.Start` 用于桌面项，也没有任何命令行长拼。
- **手势状态机**：`DesktopGestureRecognizer`（纯逻辑，喂坐标与时钟）：阈值全部来自系统——`GetDoubleClickTime`、`SM_CXDRAG` / `SM_CYDRAG`（拖动矩形）、`SM_CXDOUBLECLK` / `SM_CYDOUBLECLK`（双击矩形）。`PointerDown → 离开拖动矩形 = Drag（此后永不可能是 Click）`；`PointerUp → Click`；`第二次 Click 落在上一次的双击矩形与双击时间内 → DoubleClick（并结束连击链）`。
- **画布接线**：`Click → Select`（选择环，日志 `was selected`）；`DoubleClick → Launch`（且只在该项正是被选中的那一项时；日志 `is being opened` + `was opened by the shell`，诊断面板 `launch <id> → <outcome>`）；`DragEnd → 落点立即写入文档`（日志 `was dropped at X / Y DIP`）。
- **Missing 目标**：挂载 / 按下 / 落点三处问一次磁盘（在复制出来的 item 上、于工作线程问，挂载换代后回来的答案被丢弃）；只对问过的 item 生效——目标不在的标记（`MissingOpacity = 0.5` + 琥珀色徽标），目标回来的解除标记。**不删除、不移动、不改写**。
- **导入入口（页面）**：动态壁纸页新增“程序或快捷方式”（文件选择器过滤 `.exe` / `.lnk`）、“文件夹”、“地址”（裸地址补 `https://`，只接受 http / https）三个入口，以及一行一个 item 的列表（名称 + 路径 + 移除按钮；移除只从文档里去掉引用，磁盘上的文件一个字节都不动）。
- **诊断与门禁**：诊断面板新增 `launch <id> → <outcome>` 与 `icons N cached · N.N MB`；`ArchitectureGuardTests` 新增 `OnlyTheIconLayersKnowHowToReadAShellIcon`、`TheCanvasDoesNotUnderstandIconExtraction`。
- **真机验收**：`tools/p3b-item-verify.ps1`（`-Stage picker|demo|perf|full`），共用件抽到 `tools/p3-common.ps1`（3A 的脚本改为复用同一份）。

### 18.2 关键实现决定

| 决定 | 内容 | 理由 |
| --- | --- | --- |
| 每种 kind 一个 target 类型 | 文档里带 `kind` 判别符；未知 kind 拒绝加载而不是猜 | 未来的 packaged app / Shell namespace 作为新派生类型进场，旧文档原样还能读——模型不把将来的路堵死（spec §二） |
| 身份是生成的 id，不是路径 | `app_` / `lnk_` / `dir_` / `url_` 前缀 + 8 位随机 | 同一程序加两次是两个 item；目标以后移动或消失，item 仍然是它自己（spec §二） |
| `.lnk` 不自己解析 | 保存 `.lnk` 的路径，图标与启动都交给 shell | 解析 `.lnk` 要 `IShellLink` COM，而 shell 本来就是唯一权威；Muralis 只保存引用（spec §三） |
| 名称来自文件名 | `Path.GetFileNameWithoutExtension` | 与资源管理器显示一致；不读 `.lnk` 内部的描述（本阶段不需要） |
| 图标按“需要的像素数”取 | `sizeDip × DPI 倍率 × Proximity.MaxScale`：96 DIP @1×/1.6× = 154 px → jumbo 列表；≤44 px 才用小列表 | 放大到 1.6× 仍清晰，又不为小图标付 jumbo 的内存（spec §四） |
| 缓存里只有像素，没有 HICON | 读出即 `DestroyIcon`，缓存存预乘 BGRA | 每个图标不再持有 native 资源；50 项实测 0 个图标告警、句柄 hover 前后 1935 → 1926（spec §十四） |
| 一个 STA 工作线程 + 预算淘汰 | shell 调用全部序列化在这条线程；32 MB 预算按 LRU 淘汰 | 壳线程永不等待 shell；50 项是一队而不是一群；几百项也不会无界增长 |
| 启动只走 shell 的 `open` | `UseShellExecute = true` + `Verb = "open"`，绝不拼命令行、绝不给参数 | 关联程序由系统决定：网址 → 默认浏览器，文件 → 默认程序，文件夹 → 资源管理器（spec §五 / §十一） |
| URL 只认 http / https | 其它 scheme 在生成与加载两处都被拒绝 | 否则 item 能变成“把启动程序伪装成打开链接” |
| 双击才启动，且必须落在已选中那一项 | 第一次 click 选中，第二次 click 在双击矩形与时间内且同一项 → 启动 | 拖动永不启动；单击永不动手（spec §五 / §六） |
| 阈值全部来自系统 | `GetDoubleClickTime` / `SM_CXDRAG` / `SM_CXDOUBLECLK`，只有系统拒答时才用 500 ms 兜底 | 用户自己的鼠标设置就是标准（spec §六要求“不要随便硬编码”） |
| 拖动即断链 | 离开拖动矩形就把 `_clicked` 清掉，DragEnd 不会再产生 Click | 一次拖动的结束不可能被当成点击，更不可能启动（spec §六） |
| 缺目标只标记 | 半透明 + 警告徽标；文档里的 item 一字不改 | 用户可能只是暂时移走了文件；Muralis 不得因此删 item（spec §七） |
| 问磁盘只在这三个时刻 | 挂载 / 按下 / 落点，且在池线程上、对副本问 | 磁盘 I/O 不阻塞壳线程；不是每秒轮询文件系统 |
| 文档 v2 + 一次性迁移 | prototype 参数带过来、瓦片不迁移、旧文件改名 `.v1.bak` | 瓦片没有 target，迁过来也只能是假程序；不留永久临时格式（spec §八 / §九） |
| 损坏文档改名 `.bad` | 不是删除 | 手写文档出错的代价是“从头开始”，不是“文件没了” |

### 18.3 逐条验收（spec §十三 的 18 步，全部实测通过）

| # | 验收点 | 实测结果 |
| --- | --- | --- |
| 1 | 添加一个真实 `.exe` | 页面上的“程序或快捷方式”按钮 → 文件选择器 → `kind application path C:\Windows\System32\notepad.exe` |
| 2 | Muralis 显示真实程序图标 | `icons: every item with a file behind it got a real icon : cached 3 of 3`（程序 / 文件夹 / 快捷方式各有真图标，网址没有） |
| 3 | 拖动到任意位置 | `drag: the drop moved the item and was saved : 0,-130 -> 260,-310` |
| 4 | 重启 Muralis | `restart: the app closed cleanly on its own window` → 重新启动 → 画布挂载 |
| 5 | Item 位置恢复 | `restart: the dragged item is back where it was dropped : hovered dir_…`；文档哈希重启前后一致、文件名级未被改写 |
| 6 | Double Click | `double click: the program really started : pids …` |
| 7 | 程序启动 | `double click: the outcome is the shell taking it : launch app_… -> Launched`（日志 `opened C:\Windows\System32\notepad.exe`） |
| 8 | 添加真实 `.lnk` | `import: a real .lnk becomes a shortcut item : kind shortcut path …\P3B Notepad.lnk` |
| 9 | 正确提取名称和图标 | `import: the shortcut keeps its own name : name 'P3B Notepad'`；图标 `cached 3 of 3`、`no icon had to be left unresolved : warnings 0` |
| 10 | 启动快捷方式 | `shortcut: double clicking it starts what it points at : pids …` + `shortcut: the shell took the shortcut : launch lnk_… -> Launched` |
| 11 | 添加 Folder | `import: a real folder becomes a folder item : kind folder path …\P3B Folder` |
| 12 | 双击打开 Explorer | `folder: double clicking opens it in Explorer` + `folder: the shell took the request : launch dir_… -> Launched` |
| 13 | 添加 URL | `import: an address becomes a url item : kind url url https://example.com/` |
| 14 | 默认浏览器打开 | `address: the shell took the address : launch url_… -> Launched`；本机默认浏览器 msedge，进程数 13 → 16 |
| 15 | 拖动不会误启动 | `drag: the drag and its release launched nothing : no new process` + `drag: the drag did not open what it points at : no new window` |
| 16 | 删除 / 移动一个测试 Target | `missing: taking the target away marks the item : missing 1` |
| 17 | Muralis 显示 Missing 状态而不崩溃 | `missing: the item is still in the document, untouched : items 4` + `missing: the app is still alive and answering` + `missing: the marked item has not been moved` |
| 18 | Explorer restart 后 Item 全部恢复 | `explorer: the canvas comes back after the shell restarts : mount 1 -> 2`、`explorer: every item is on the desktop again : items 4`、`explorer: the item whose target came back is no longer marked : missing 0` |

演示同时覆盖了 spec 没逐条列出但同样要求的行为：单击只选中不启动（`click: a single click did not launch anything`）、悬停命中与放大（`hovered app_…` / `scale 1.6`）、页面行与移除（`page: one row per item…` / `remove: what it pointed at is still there : True`）、原型种子不再回来（`import: nothing from the old prototype seed is back`）、未复制任何文件（`nothing was copied into the fixture folder : strays 0`）。

### 18.4 测试与提交

- 新增测试：Core——`DesktopItemTests`、`DesktopItemFactoryTests`、`DesktopItemLaunchTests`、`DesktopItemPlacerTests`、`DesktopItemSerializationTests`、`DesktopLayoutTests`、`DesktopLayoutMigratorTests`、`DesktopLayoutStoreTests`（+ `TestSupport/TempWorkspace.cs`）；Desktop——`DesktopGestureRecognizerTests`、`IconBitmapCacheTests`、`ShellIconReaderTests`、`IconSurfaceLiveTests`，另加 2 条架构门禁。覆盖 target 序列化与未知 kind、id 与缺失判定、清单生成、启动路由、手势三态与系统阈值、图标缓存（去重 / 淘汰 / 并发）、布局文档往返与迁移、损坏文档处置。Core 281 → **366**，Desktop 84 → **125**（共 491），Debug / Release 双配置 0 警告 0 错误。
- 提交（`architecture-v2` 分支）：`ae1e15f` feat: add desktop item target model → `8cf061c` fix: name every item fact once in the document → `cc47d59` feat: resolve real application icons → `8abb377` feat: launch desktop items → `53ac46e` test: cover real desktop item interactions → `a1535eb` test: open the shortcut in the demo and wait for the instance slot。本节与本轮 CHANGELOG 随其后的 `docs:` 提交入库。
- 验收脚本：`tools/p3b-item-verify.ps1 -Stage demo`（58 项检查）、`-Stage perf`（50 项，9 项检查）、`-Stage picker`（导入入口 15 项）；产物在 `artifacts/p3b/`（未入库）。

### 18.5 性能（50 项真实程序与目录，单屏 2560×1440）

| 项目 | 实测 |
| --- | --- |
| 进程启动 → 50 项全部在屏上 | **1489 ms**（里程碑：app class 3 / window created 321 / window shown 328 / ui idle 578 ms） |
| 布局保存（50 项，原子写） | **12.1 ms** |
| 图标解析（冷，单项） | **2 ms**；47 项全部在 hover 之前完成，`cached 47 of 47` |
| 图标缓存内存 | **11.8 MB**（47 项 jumbo 像素） |
| Idle CPU | 78.125 ms / 10 s = 单核 **0.776 %** |
| Hover CPU（150 次移动 / 14.1 s） | 265.625 ms = 单核 **1.88 %**；GPU 峰值 **0.33 %** |
| 句柄 / GDI 对象 | 1935 → 1926（hover 之后）；89 → 87 |
| 工作集 | 249.3 MB |
| 图标告警（无法解析） | **0** |

### 18.6 已知限制 / 诚实记录

- **单屏结论**：与 Phase 2 / 3A 相同，全部实测在 2560×1440 @96 DPI 单屏上完成；高 DPI 与多屏未实测。图标按设备像素请求（`DIP × 倍率 × MaxScale`），设计上支持，但没有量（spec §一 只要求 16–128 DIP 不糊，这一点由“请求的像素数 ≥ 显示像素数”保证）。
- **单实例重启窗口（本阶段唯一一次演示失败的原因）**：进程对象报告“已退出”之后，Windows 还要一小会儿才放开它持有的内核对象——单实例互斥体最长可多活约一秒。这段时间里再次启动 Muralis，新进程会被判成“第二个实例”，它只把旧实例“唤醒”一下就退出（日志 `Another Muralis instance is already running; this launch only woke it`），用户看到的是“点了没反应”。验收脚本现在**等互斥体本身空出来**再启动（`Test-SingleInstanceFree`），演示因此稳定复现；应用侧的启动路径本轮没有加宽限期——**这是 Phase 3C 之前值得修的小问题**（真人关窗后一秒内再点图标会踩到）。
- **`.lnk` 的 Missing 判定只看 `.lnk` 文件本身**：`.lnk` 指向的目标被删掉时不会标 Missing（那需要解析 `.lnk`，而本阶段刻意不解析）。
- **`.lnk` 的显示名取文件名**，不读 `.lnk` 内的 Description / 目标名。
- **图标来自 shell 的 jumbo 图像列表**：不叠加 shell 的 overlay（快捷方式小箭头），也不跟随用户自定义图标缓存的变化刷新——重启后重新读一次 shell。图标缓存在内存里，没有磁盘缓存。
- **packaged app / Store 应用、Shell namespace 对象不在本阶段**：模型留好了位置（新 target 类型 + `kind` 判别符，旧文档不受影响），但没有实现。
- **没有重新绑定 target 的 UI**（spec §七 允许本阶段不做）；Missing 的判定发生在挂载 / 按下 / 落点，不是实时监视。
- **50 项性能是一次测量**，不是分布；hover CPU 是 14 s 单段。
- **验收脚本会最小化挡住画布的窗口**（结束逐个恢复，本轮已获授权）；它只移动真实指针、只读自家窗口与自家日志，不读取用户屏幕内容。
- **种子布局已彻底删除**：Steam / Chrome / Blender / ComfyUI 那批假程序不再出现；桌面只显示用户真正添加过的东西（`import: the desktop starts empty : items 0`）。

### Phase 3 状态

| 内容 | 状态 |
| --- | --- |
| Phase 3A = Desktop Input Foundation | 完成（见 §17.3；提交见 §17.4） |
| Phase 3B = Real Desktop Items & App Launching | 完成（见 §18.3；提交见 §18.4） |
| Phase 3C = Interactive Edge Dock | 完成（见 §19.3；提交见 §19.4） |
| Phase 3D 及以后 | 未开始（按指令停在 3C） |

## 19. Phase 3C 落地记录：Interactive Edge Dock（2026-09-16）

### 19.1 交付内容

- **单实例重启竞态修正（spec §一）**：互斥体从“创建标志”改成真正的锁（`initiallyOwned:false` + `WaitOne`），`AbandonedMutexException` 视为“上一个持有者已死，名字归我”，干净退出时在 `ProcessExit` 里显式 `ReleaseMutex`，不再依赖内核回收；名字被占时不再假设持有者还活着，而是**用命名 ack 事件做一次“你还能把自己显示出来吗”的握手**（`AllowSetForegroundWindow(ASFW_ANY)` + 广播 + 最长 500 ms 等应答），应答只在主窗口真的 `Activate()` 成功之后才发。全部有界重试，没有固定 sleep。
- **正式的 Dock 模型（spec §二、§十）**：新建 `Muralis.Core/Dock/` 命名空间——`DockEdge`/`DockAxis`（四边 + 方向抽象）、`DockEntry`（只持有 `ItemId`，**引用而不是复制** `DesktopItem`）、`DockOptions`（`Enabled`/`Edge`/`AutoHide`/`TriggerThicknessDip`/`PeekSizeDip`/`ShowDelay`/`HideDelay`/`ItemSizeDip`/`SpacingDip`/`EdgeMarginDip`/`PaddingDip`/`MaxScale`/`InfluenceRadiusDip`/`Falloff`/`Spring`/`Entries`）。**Dock 不再是 Canvas 的一种 placement**：`DesktopItem.Placement` 与 `CanvasItemPlacement` 一并删除，成员关系唯一来源是 `Dock.Entries`，Canvas 就是“dock 没有点名的那些 item”；`Validate()` 会拒绝“点了不存在的 item”“同一 item 进两次”“两边不一致”。文档升级为 **schema 3**（`DesktopLayout { Proximity, Motion, Dock, Items }`），Phase 2 原型（v1）与 Phase 3B 文档（v2）各有一条一次性升级路径（v2 把 `items[].placement` 读成 entries、把 `motion.dock` 搬进 `dock.spring`、把 `collapsedScale` 折算成 peek），原文件保留为 `.v1.bak` / `.v2.bak`。
- **四边统一几何（spec §三、§十四）**：`DockFrame` 把“沿边坐标 + 离边深度”映射到屏幕点/矩形，四边共用一套公式，没有四份分支；`DockGeometry` 给出 rail 矩形（reveal 0→1 是从边外滑入，不是缩放）、沿边条目中心、触发带、以及“dock 可能画到的全部像素”外接矩形（窗口 region 用它，动画永不裁切）。显示器原点与倍率只出现在 `DockFrame` 构造里。
- **macOS 风格邻近放大（spec §四、§五）**：`DockMagnification` ——先按“指针到**静止**中心的距离”算每项 scale（用共享的 `CanvasProximity.ScaleAt` 曲线，避免把位移反馈进距离），再按“每项按自己的放大后宽度占位、间隙随两侧 scale 等比放大”顺序铺开，最后整段重新居中到 rail 中心：**任何 scale 组合下都不重叠**（有单测逐点扫过验证）。参数全部来自 dock 配置（`MaxScale`/`InfluenceRadiusDip`/`SpacingDip`/`Spring.PeriodSeconds`/`Spring.DampingRatio`），动画走 WUC spring（`SpringVector3NaturalMotionAnimation`），滑轨与条目各一条。
- **显式 Auto-hide 状态机（spec §六）**：`DockState { Hidden, Revealing, Visible, Hiding, Dragging }` + `DockAutoHide`（无自己的时钟，调用方喂时间与“动效是否停稳”）。指针离开而 rail 还在外滑时按“离开那一刻”起算 hide delay，条目不闪；拖动期间状态为 `Dragging`，rail 一定在外，不会在手下收回；点开一个条目不会让它抖动（指针就在 rail 上）。
- **Dock 交互（spec §七、§八、§九）**：**Dock 单击启动，自由画布双击启动**；按下不立即定性，越过系统拖拽矩形才成为拖拽（拖拽永不启动）。沿 rail 拖动 = 重排（插入位预览 + 邻居平滑让位，**松手才写文档**）；拖出 rail = 移回画布（落点写成 anchor + DIP 偏移）；画布条目拖到 dock 上 = 加入（同一 item，只换层与尺寸）。指针上下文完全依赖 `DesktopPointerRouter` 的 Foreign/Desktop/Surface 判定：指针在普通窗口上时 dock 什么都不做（日志与 panel 都可证）。
- **App 侧（spec §十、§十一）**：动态壁纸页新增 Dock 卡片（开关 / 自动隐藏 / 四边选择器，`AutomationId` = `CanvasDockEnabled` / `CanvasDockAutoHide` / `CanvasDockEdge`），读写走 `IDesktopCanvasService.GetDockAsync/UpdateDockAsync`；dock 参数存在桌面布局文档里，**不写 `settings.json`**。开发面板新增 dock 一行（状态 · 条目数 · 开关 · 边 · reveal），并新增 `DockItemCount` / `DockEdge` / `DockEnabled` 诊断字段。

### 19.2 关键实现决定

- **成员关系只有一个来源**：删掉 `DesktopItem.Placement` 而不是让 `Entries` 与它并存。一个 item 住在哪里由 dock 说了算，画布就是剩下的全部；这样“拖动换家”只改一处，也不会出现“文档说我 docked、画布说我不在 dock”的裂缝。
- **动画里不重建视觉树**：指针事件只写属性——条目 offset/scale 交给 spring，rail 长度跟着放大后的 run 直接设尺寸（比插值更顺），rail 的圆角背板只在**尺寸真的变了**时重建几何。窗口 region 用的是“dock 可能画到的全部像素”外接矩形，所以动画期间 region 不动；隐藏时 region 收缩到触发带（4 DIP），rail 的像素不再属于窗口——这也是 harness 用 `PtInRegion` 直接验证“rail 真的出来了”的依据。
- **整段重排而不是钉住指针下的条目**：整段跟着放大重新居中保证连续、无跳变；代价是边界处指针可能落在两个放大的条目之间，此时**画面与命中都按渲染位置来**（`HitTest` 用 `RenderedXDip`），单测里把这个“最大项与最近项最多差一位”的性质固定下来。
- **不在放大态下也保持稳定**：`_dockPointerAlongDip` 只在 dock 处于外滑状态时才计算，所以 rail 收起时既无放大也无 hover，指针在远处扫过不产生任何工作（§十二“idle ≈ 0 CPU”）。
- **隐藏即让位**：dock 收起时，它的像素不在窗口 region 里，点击会落回桌面；只有 4 DIP 的触发带始终属于窗口（默认 `TriggerThicknessDip = 4`、`PeekSizeDip = 4`，都比原生桌面图标的第一列更靠边）。
- **v2→v3 升级是“读一次、写回一次、留一份 `.v2.bak`”**，不是就地改写：失败的升级绝不动原文件（沿用 3B 的迁移承诺）。

### 19.3 逐条验收（spec §十六 的验收演示，全部实测通过）

`tools/p3c-dock-verify.ps1 -Stage dock` 共 **41 项检查全通过**（`artifacts/p3c/p3-dock-verify.json`），覆盖 spec §十六 的每一步：

| spec §十六 | 实测（节选 harness 文案） |
| --- | --- |
| 启用左 dock，真实条目出现 | `the document was read as it was planted : Hidden · 3 items · on · Left edge · reveal 0` |
| 指针接近 → dock 展开 | `the pointer at the edge reveals it : Visible · … · reveal 1`；像素证据 `a revealed rail owns the pixels it draws into` |
| 图标连续放大 | `the item under the pointer is the one magnified : hovered dock_link` / `the magnified item reaches the dock maximum : hovered scale 1.6` |
| 邻居平滑位移且不重叠 | 单测逐点验证（`NoTwoItemsEverOverlap_HoweverLargeTheyGrow`、`TheRunStaysCentredOnTheRail_WhereverThePointerIs`）；真机 `a neighbour is smaller than the item under the pointer` |
| 快速扫过保持连续 | 单测 `AContinuousSweep_IsContinuous`；真机 sweep 覆盖整条 rail 并读到 hover |
| 指针离开 → auto-hide | `the rail retracts once the pointer is away : Hidden · … · reveal 0` |
| 单击启动 | `a single click opens the item : openings: 1` + `what it opens really starts : new notepads: …` |
| 拖动不会误启动 | `a reorder never opens anything : openings during the drag: 0`、`leaving the dock never opens anything : openings: 0`、汇总 `nothing was opened by the pointer alone : openings after everything: 1` |
| dock 内重排 | `carrying an item along the rail reorders the dock : dock_app,dock_link,dock_folder -> dock_link,dock_folder,dock_app` |
| Canvas → Dock | `a canvas item dropped on the dock joins it : dock holds …,free_folder,…` |
| Dock → Canvas | `a dock item dropped on the canvas leaves the dock` + `the item is on the canvas where it was let go : offset -384,-143 DIP, expected about -384,-144` |
| 切换 Right / Top / Bottom | `the page moves the dock to the Right edge : picker said Right` 等三条 + 各自 `the rail comes out on the … edge and magnifies there` |
| 重启 Muralis 后恢复 | `the dock comes back after Muralis restarts : Hidden · 3 items · on · Bottom edge` + `the order is the order the user left` |
| 重启 Explorer 后恢复 | `the canvas comes back after Explorer restarts : mount 1 -> 2`、`the order survives the shell restart`、`the icons are still the ones already resolved : 4 cached before, 5 now`、`the pointer works the dock again after the shell restart : Left; hovered free_folder at 1.6` |
| 指针在普通窗口上不触发桌面 dock | `a pointer over an ordinary window never brings the dock out : Hidden` + `… magnifies nothing : hovered none`（用的是 Muralis 自己的主窗口盖住左边缘） |
| 连续 20 次退出→启动无单实例竞态 | `-Stage restart`：20/20 起来、0 次“被当成第二个实例”、0 次双实例、退出后名字**最慢 10 ms** 就空出；`-Stage wake` 5/5 叫醒并前台 |
| 原生桌面未被永久修改 | 未新增/删除任何桌面窗口；脚本结束恢复 `settings.json`、布局文档与被最小化的用户窗口（`Restoring the desktop documents …`） |

### 19.4 测试与提交

- 新增 / 重写测试：Core——新建 `Dock/DockGeometryTests`、`Dock/DockMagnificationTests`、`Dock/DockAutoHideTests`、`Dock/DockReorderTests`（替换已删除的 `CanvasRailLayoutTests`、`CanvasDockAutoHideTests`），`DesktopLayoutTests` / `DesktopLayoutMigratorTests` / `DesktopItemPlacerTests` / `DesktopItemTests` / `DesktopItemSerializationTests` 按 v3 与新签名更新，并新增 v2→v3 升级用例（placement→entries、spring 搬家、collapsedScale→peek、非法 target 拒绝）。覆盖四边几何与 125 %/150 %/200 % 倍率 + 非零显示器原点、放大不重叠与整段居中、五个状态与两个延迟、重排插入位与预览置换。Core 366 → **415**，Desktop **125** 不变（共 540），Debug / Release 双配置 0 警告 0 错误。
- 提交（`architecture-v2` 分支）：`eb3f93a` fix: make single instance restart reliable → `5e62a10` feat: add interactive edge dock → `3449e23` test: cover interactive dock behavior → `docs: record the phase 3C landing`（本次提交，见 §19.7）。
- 验收脚本：`tools/p3c-dock-verify.ps1`，新增 `-Stage dock`（41 项）与 `-Stage perf`（18 项，10/25/50 三档）；`-Stage restart` / `-Stage wake` 为 §一 的回归；`tools/p3-common.ps1` 新增 `FindWindowByPrefix`（画布窗口是 Explorer 图标宿主的子窗口）、`GetWindowRgn`/`PtInRegion`/`GetRgnBox` 区域探针，`Parse-Diag` 跟随新的 dock 诊断行。产物在 `artifacts/p3c/`（未入库）。

### 19.5 性能（dock 全为真实条目，单屏 2560×1440 @96 DPI）

| 项目 | 10 项 | 25 项 | 50 项 |
| --- | --- | --- | --- |
| 启动 → 全部在 dock（含 3 次进程启动） | 2025 ms | 1847 ms | 1873 ms |
| 图标缓存条目 / 内存 | 6 / 1.5 MB | 21 / 5.3 MB | 46 / 11.5 MB |
| Idle CPU（rail 收起，10 s） | 93.75 ms = 单核 **0.94 %** | 46.875 ms = **0.47 %** | 140.625 ms = **1.40 %** |
| 沿 rail 连续扫动 CPU | 375 ms / 2.5 s = **15.1 %** | 343.75 ms / 5.9 s = **5.8 %** | 968.75 ms / 5.8 s = **16.7 %** |
| 扫动 GPU 峰值 | 0.017 % | 0.021 % | 0.021 % |
| 句柄 / GDI / USER 对象 | 1641 / 95 / 65 | 1767 / 97 / 68 | 1961 / 107 / 69 |
| 工作集 | 229.6 MB | 238.4 MB | 253.9 MB |
| 布局保存（原子写） | 1.4 ms | 1.4 ms | 1.4 ms |
| 展开→收起循环（各 5 次） | 5/5 | 5/5 | 5/5 |
| 图标条目 vs 需解析项 | 6 / 7 | 21 / 22 | 46 / 47 |

最后一行少 1 是**去重**：fixture 里 `windir` 与 `SystemRoot` 指向同一个目录，缓存按 (路径, 像素) 计一条。

### 19.6 已知限制 / 诚实记录

- **hover 名称标签未实现**：spec §十一 的“至少视觉上”四项里，图标 ✓、选中描边 ✓、rail 背板 ✓ 都在，**唯独 hover 名称标签没做**。当前桌面上方的合成层没有文本渲染路径（要么引入 Win2D/DirectWrite 依赖，要么自己走 GDI 位图 + 掩膜合成），本轮没有把它塞进 3C 的收尾里——留成一个明确的缺口，而不是画一个假的。其余三项与 §十一 的“运行指示可以是 model-only”一致：本轮**不画**运行小点（没有进程跟踪时画一个点等于骗人）。
- **底部 dock 会被任务栏盖住**：rail 在 y = 边到 72 DIP 之间，Windows 默认任务栏约 48 px——底部任务栏一开，Bottom dock 的指针就够不着（harness 用 `Test-EdgeCoveredByTaskbar` 直接把这一档记成“rail 像素属于画布，但指针不可达”，而不是假装通过）。这是 Windows 上“贴边 dock + 贴底任务栏”的固有冲突，需要用户把任务栏挪开或改边。
- **50 项时 rail 比屏幕高**：50 × 56 + 49 × 12 = 3388 DIP > 1440 DIP，超出的条目在屏幕外（本轮 dock **没有滚动/分页**）。性能矩阵仍然测了 50 项（放大、状态机、CPU 都正常），但“第 30 项之后在哪”是未定义行为。
- **多屏与高 DPI 仍未实测**：四边几何的单测覆盖了 125 %/150 %/200 % 与非零显示器原点（`DockFrame` 只从 bounds/scale 取坐标系），但真机验证仍在单屏 96 DPI；`monitor  \\.\DISPLAY5 · 2560x1440 at 0,0 · 96 dpi (1x)`。
- **Auto-hide 的“擦边抖动”没有专门的抗抖参数**：靠 ShowDelay(120 ms)/HideDelay(600 ms) 与“指针在 rail 上即算想要”来吸收；极端来回扫动下可能出现展开—收起的追赶，但没有实测到抖动（5/5 循环与 sweep 都稳定）。
- **重排的插入判定是“数有几个中心在指针之前”**，因此切换到下一槽位发生在邻居中心处，而不是严格的几何中点；有意选择“单调、不来回跳”。
- **拖动 dock 条目时放大暂停**：拖拽期间（`Dragging`）条目按静止尺寸排布、被拖的那一项贴着指针，rail 长度取静止长度——换来了“手下的东西不会跳”，代价是拖动时看不到放大。
- **验收脚本会最小化挡住桌面的窗口（结束恢复）、会关闭自己启动的 notepad 进程、会 `taskkill explorer.exe` 后重启它**（沿用 Phase 1G/3B 已获授权的做法，可用 `-SkipExplorerRestart` 跳过）；只移动真实指针、只读自家窗口/文档/日志，不读用户屏幕内容。
- **`DockEntry` 目前只有 `ItemId` 一个字段**：spec §二 举例提到 `Pinned`/`Group`/separator；本轮**没有加**这些还没有消费方的字段（“分组不是必须的”），条目是对象而不是裸 id，将来加字段不改文档形状。
- **性能是一次测量**，不是分布；idle/sweep 各 10 s 与 2.5–5.9 s 单段。sweep CPU 百分比是“单核占比”，且包含 harness 自身 `SendInput` 的节奏影响。
- **`feat:` 提交包含了它必须一起带上的测试改动**：删除 `CanvasRailLayoutTests` / `CanvasDockAutoHideTests` 与更新既有 Desktop 测试，否则 `Muralis.slnx`（含测试项目）在那次提交上编不过；新增的 `Dock/*Tests` 与 harness 在随后的 `test:` 提交里。四个提交都经 Debug 全量构建验证（0 警告 0 错误）。

### 19.7 提交与验收产物

| 提交 | 内容 |
| --- | --- |
| `eb3f93a` | fix: make single instance restart reliable（含 `tools/p3c-dock-verify.ps1` 的 restart/wake 阶段） |
| `5e62a10` | feat: add interactive edge dock（Core Dock 命名空间 + 文档 v3 + Desktop 表面 + App 设置与本地化） |
| `3449e23` | test: cover interactive dock behavior（`Dock/*Tests` + harness 的 dock/perf 阶段 + `p3-common` 探针） |
| `docs: record the phase 3C landing` | 本节 + CHANGELOG（就是包含本表的那次提交，哈希见 `git log -1`） |

验收产物（`artifacts/`，未入库）：`p3c/p3-dock-verify.json`（41 项全通过）、`p3c/p3-perf-verify.json`（18 项全通过）、`p3c/p3-restart-verify.json` 与 `p3c/p3-wake-verify.json`（10 项全通过）。

## 20. Phase 3D 落地记录：Native Desktop Takeover（2026-09-16）

### 20.1 交付内容

- **五个状态，不是一个 bool（spec §一）**：`DesktopTakeoverState`（Native / Enabling / Muralis / Disabling / RecoveryRequired）+ `DesktopMode`（Native / Preview / Takeover，用户的选择）+ `DesktopModeStatus`（把"愿望"与"事实"分开：`Mode` 是用户要的，`Takeover`/`Canvas` 是真的在发生，`EffectiveMode` 把两者合成一个可显示的结论）。`DesktopTakeoverMachine` 是纯函数状态机，合法迁移写死、非法迁移直接拒绝，`Validate()` 拒绝"声称接管过却没写原来什么样"的标记。
- **优先使用官方 Shell View API（spec §二）**：先探针后设计。`tools/p3d-shell-api-probe.ps1`（604 行）在本机实测了桌面自己的 `IFolderView2`：`FVM_ICON` 模式 1、44 个条目、auto arrange 1、`SetCurrentFolderFlags(FWF_NOICONS)` 被 shell 接受并能读回——所以 `DesktopIconStrategy` 的两级梯子是 `ShellViewFlags`（文档化路径，本机走的就是它）→ `IconWindow`（隐藏图标列表窗口：公开、可逆、不留痕，但 shell 每次重建都要重新施加）。**注册表 `HideIcons` 不是主路径、没有 `SysListView32` 定位改写、没有 Explorer/DLL 注入、没有文档化之外的钩子**，这些由 `ArchitectureGuardTests` 在测试里守着。
- **绝不修改用户 Desktop 文件（spec §三）**：扫描只读 `Desktop` 与 `Public Desktop`；harness 在每一档前后对两个目录做文件列表比对，全程 `0 difference(s)`。
- **记录原始状态（spec §四）**：`NativeDesktopVisualState` 记下接管前的图标可见性与**整个 flags 字**（本机 `0x40200224 [FWF_SNAPTOGRID | FWF_DESKTOP]`）；归还时把这个字原样写回，而不是"把图标打开"——用户本来就把图标关着，就还是关着。
- **Crash-safe Recovery（spec §五）**：`DesktopTakeoverRecord` + `DesktopTakeoverRecordStore`（原子写、校验 schema/kind、读不出来就搁置而不是照着做）。写标记在隐藏图标**之前**，删标记在图标验证回来**之后**——所以文件存在是保守声明：可能多余，绝不会漏。恢复在启动时走（`RecoverIfNeededAsync`），不在 Dispose 里。
- **正常退出（spec §六）**：`DesktopShutdown`——先还图标并验证，再把画布从桌面上取下来，最后才关 shell 层；顺序反过来就是把画布从一个还藏着自己图标的桌面下抽走。幂等、永不抛出。
- **Explorer Restart（spec §七）**：`DesktopTakeoverService.AttachTo(IDesktopShell)` 挂在 shell 的重建事件上重新施加接管；**Muralis 不重启 Explorer 来切换模式**（harness 里那次 `taskkill` 是为了验证"重启后自动恢复"，不是切换路径）。
- **Desktop Import（spec §八、§九、§十）**：`DesktopContentScanner` 读两个桌面目录（程序 / 快捷方式 / 文件 / 文件夹 / 网址），`DesktopContentAdopter` 只做"要不要采纳"的判断，`DesktopItemSyncService` 负责落进文档。身份 = **源路径**（`OrdinalIgnoreCase`），与显示名无关；开启时扫描一次、手动刷新、启动刷新，`FileSystemWatcher` 带防抖且只在画布在时开着，**没有任何按帧或按秒的扫描**。
- **首次开启体验（spec §十一）**：页面在动手**之前**说清"桌面有多少条目、会采纳多少、哪些不能采纳、现在是什么模式"，模式切换必经一次确认对话框。
- **Emergency Restore（spec §十二）与托盘保底（spec §十三）**：`IDesktopModeService.RestoreNativeDesktopAsync()` **不读画布、不读布局、不读模式**，直接让接管服务把图标还回去，再把画布摘掉；托盘菜单常驻"关闭接管"与"恢复 Windows 桌面"两条命令（`TrayService`，原生 `Shell_NotifyIcon` 菜单，从自己的消息循环里跑、不 await 桌面），页面在知道自己欠一次归还时给出同一个入口。
- **Dock 与 Canvas（spec §十四）**：Preview 与 Takeover **共用同一个画布服务**，区别只是图标藏不藏；harness 每次都断言"桌面上的画布窗口数 = 1"，所以不会出现两层各画一遍。
- **App 侧（spec §十五）**：动态壁纸页新增桌面模式三选一（`DesktopModeRow`）+ 采纳开关 + 手动刷新 + 首次预览信息，中英双语本地化；原来 `settings.json` 里的"画布开关"搬进桌面布局文档，**桌面状态只有一个家**。

### 20.2 关键实现决定

- **顺序就是安全**：`DesktopModeService.ApplyCoreAsync` 先把画布挂上并**验证它真的在**，才轮到隐藏图标——在一个没有替代品的桌面上藏图标是"拿走"而不是"替换"。归还走反方向：图标先回来并验证，才把画布取下。
- **记的是发生的事，不是被要求的事**：接管失败（图标没藏成）被记成 **Preview 并落盘**，因为它确实发生了（画布在、图标在）；画布没挂上则**什么都不记**，否则每次启动都会再失败一次。欠着归还的桌面**拒绝**任何新的接管（唯一的出路是归还），而要求其它模式时**先归还再动手**。
- **标记的存在是保守声明**：先写标记后藏图标、先验证恢复后删标记；`recover:` 段整段发生在进程被 kill 之后，先还桌面、再恢复用户要的模式。
- **恢复写回原值而不是"显示"**：用户桌面本来就藏图标时，接管再归还不能替用户把图标打开。
- **身份是路径而不是名字**：一个 item 绑它来自哪个文件；改名或移动就是新条目，旧 item 留在原地指着老路径（显示为缺失），**不会被悄悄重新指向，也不会被悄悄丢掉**（三条单测钉住这个语义）。
- **Explorer 重建由事件驱动**：shell 重建是 `ShellEventSource` 的一条消息，没有任何轮询循环——性能段里 idle 10 秒"桌面相关日志行 = 0"就是这条的实测。
- **接管只做显示与交互**：桌面文件一个字节都不改；这条写在文档里、写在 CHANGELOG 里，也由每一档 harness 的文件列表比对守着。

### 20.3 逐条验收（spec §十八 的 22 条矩阵）

`tools/p3d-takeover-verify.ps1 -Stage full` 共 **88 项检查全通过**（`artifacts/p3d/full-run.txt`：ui 38 / recover 19 / explorer 10 / tray 20 + harness 1），性能另起 `-Stage perf` **62 项全通过**（`artifacts/p3d/perf-run.txt`）。逐条对照：

| spec §十八 | 实测（节选 harness 文案） |
| --- | --- |
| 1 Native 启动 | `ui: the app started in Native, as the document said : remembered mode Native` |
| 2 Enable Takeover | `ui: the picker moved the desktop to takeover : the document says Takeover` |
| 3 原生图标消失 | `ui: the native icons are really hidden : 0x40201224 [FWF_SNAPTOGRID \| FWF_DESKTOP \| FWF_NOICONS]` |
| 4 Muralis items 正常显示 | `takeover: the canvas item is on the desktop with the icons hidden`；`perf 100: every planted item is on the canvas : items 100` |
| 5 项目数量与 Desktop 内容匹配 | `ui: the user's own items were adopted by reference : 42 item(s) name a source path`、`ui: every adopted item points into a desktop folder : 0 outside`；`perf 50: the icons were resolved from the shell : 47 cached of 47 with a file behind them` |
| 6 点击/启动正常 | `takeover: a single click picks the item out : selected ui_app`、`takeover: a single click opens nothing : no new process`、`takeover: a double click opens the program it names : pids …` |
| 7 拖拽布局正常 | `takeover: the drop moved the item and was saved : 0,0 -> 260,-180`、`takeover: the drag and its release opened nothing`、`takeover: the item is really drawn where it was let go : hovered ui_app` |
| 8 Dock 正常 | dock 本身由 3C 的 `-Stage dock` 41 项覆盖；接管期间只有一层画布：`ui: the canvas is on the desktop : 1 canvas window(s)`。**"接管 + dock 指针"的合体动作没有单独实测**，见 §20.6 |
| 9 Disable Takeover | `ui: the picker moved the desktop back to native : the document says Native` |
| 10 原生图标立即恢复 | `ui: the native icons are visible again : 0x40200224 [FWF_SNAPTOGRID \| FWF_DESKTOP]` |
| 11 原图标文件一个不少 | `ui: not one of the user's desktop files changed : 0 difference(s)`、`explorer/tray/recover: not one of the user's desktop files changed`、`perf: the user's desktop is untouched at every size` |
| 12 原始 native visibility 状态正确恢复 | `ui: the desktop flags are exactly what they were found with`、`tray: the desktop flags are the ones it was found with`、`recover: the desktop is exactly as it was found` |
| 13 Takeover ON → Explorer restart → 自动恢复 | `explorer: the takeover is re-applied to the new desktop`、`explorer: the rebuilt desktop hides its icons again : 0x40201224 …`、`explorer: the mode is still the one the user asked for` |
| 14 Takeover OFF → Explorer restart → 保持 Native | 3C `-Stage dock` 的重启恢复 + 本段 `explorer: the desktop flags are exactly what they were found with`（重启后归还仍精确到 flags） |
| 15 Takeover ON → 正常 Exit → 原生图标恢复 | `tray: turning the takeover off gives the desktop back`、`tray: no marker is left behind`、`tray: the desktop flags are the ones it was found with`，退出路径即 `DesktopShutdown`（`ui:` 段收尾 `0 canvas window(s) left` + 标记消失） |
| 16 Takeover ON → 模拟 crash → 下次启动自动 recovery | `recover: a killed run leaves its marker behind`、`recover: the next launch noticed the marker the crash left`、`recover: the desktop was given back before anything was built on it`、`recover: the mode the user asked for came back` |
| 17 layout.json 损坏 → Windows Desktop 仍可恢复 | `DesktopModeServiceTests.GivingTheDesktopBack_NeedsNoCanvasAndNoLayoutAndNoMode`（布局读不出、画布摘不掉，仍然归还并把画布自己的错误单独报出来）+ `recover: the emergency restore …` 一整段（页面不在画面里时同一入口仍有效） |
| 18 Canvas mount 失败 → 不隐藏 native icons | `DesktopModeServiceTests.ATakeover_IsNeverAskedForWhileTheCanvasIsNotShowing`（接管一次都没被请求、模式不落盘）+ `ATakeoverThatCannotHideTheIcons_IsRememberedAsAPreview` |
| 19 连续 Enable/Disable ×20 | `DesktopModeServiceTests.TwentyRoundsOfTakingOverAndGivingBack_LeaveTheDesktopNative`（20 次进出后仍 Native、计数与写入都收敛）+ 真机 3 轮（perf 10/50/100 各一次） |
| 20 Desktop 有 100 个项目时仍可工作 | `perf 100:` 一段 21 项（enable 5997 ms、disable 3882 ms、idle 218.75 ms/10 s、GPU 0.337 %、97 个图标 24.3 MB） |
| 21 Tray Emergency Restore 正常 | `recover: the emergency restore was chosen`、`… puts the desktop back`、`… leaves no marker behind`、`… leaves the desktop native`；`tray: the restore command was chosen` 同样一组 |
| 22 Windows 原生桌面数据 hash / 文件列表切换前后相同 | 每一档收尾的 `0 difference(s)`，含 `perf: the user's desktop is untouched at every size` |

### 20.4 测试与提交

- 新增测试：Core——`Desktop/Takeover/DesktopTakeoverMachineTests`（五态迁移与非法迁移）、`DesktopTakeoverRecordStoreTests`（原子写、坏文件搁置、脏 schema 拒绝）、`DesktopTakeoverOptionsTests`、`DesktopLayoutMigratorTests`（v3→v4 与 `.v3.bak`）、`DesktopContentScannerTests`、`DesktopContentAdoptionTests`（含三条"身份是路径"的用例）；Desktop——`Modes/DesktopModeServiceTests`（10 项：顺序、失败语义、欠归还、紧急归还、20 轮进出）、`Takeover/DesktopTakeoverLiveTests`（真桌面上的接管/归还/崩溃恢复/坏标记）、`Sync/DesktopItemSyncServiceTests`、`Architecture/ArchitectureGuardTests`（禁止注册表 `HideIcons` 主路径、禁止注入、禁止 `SysListView32` 定位）。Core 415 → **485**，Desktop 125 → **150**（共 635）；Debug / Release 双配置 **0 警告 0 错误**。
- 提交（`architecture-v2` 分支，见 §20.7）。
- 验收脚本：`tools/p3d-takeover-verify.ps1`（`-Stage probe|ui|recover|explorer|tray|perf|full`）、`tools/p3d-shell-api-probe.ps1`、`tools/p3d-shell-interop.ps1`（PowerShell 侧直接问 shell view 的探针）；`tools/p3-common.ps1` 增加图标 flags/native 状态的读取、桌面前后快照比对、`Read-StateAt` 的角落模式自救与 notepad 基线工具。产物在 `artifacts/p3d/`（未入库）。（**事后注**：三态接管随 §23 的两模式收敛撤出产品路径，`tools/p3d-takeover-verify.ps1` 已退役删除；`tools/p3-common.ps1`、`tools/p3d-shell-api-probe.ps1` 与 `tools/p3d-shell-interop.ps1` 仍在用。）

### 20.5 性能（接管期间的真实桌面，10 / 50 / 100 个条目）

| 项目 | 10 项 | 50 项 | 100 项 |
| --- | --- | --- | --- |
| Takeover enable（其中扫描与规划） | 4151 ms（2223 ms） | 4674 ms（2596 ms） | 5997 ms（3653 ms） |
| Takeover disable | 3091 ms | 3310 ms | 3882 ms |
| 一次 takeover 读取桌面的次数 | 2 | 2 | 2 |
| 解析出图标的条目 | 7 / 7 | 47 / 47 | 97 / 97 |
| 图标缓存条目 / 内存 | 7 / 1.8 MB | 47 / 11.8 MB | 97 / 24.3 MB |
| Idle CPU（图标已隐藏，10 s） | 31.25 ms = 单核 **0.31 %** | 109.375 ms = **1.09 %** | 218.75 ms = **2.19 %** |
| Idle 期间的桌面工作日志行 | **0** | **0** | **0** |
| 指针扫过整格 CPU | 453.125 ms / 13.9 s = **3.26 %** | 578.125 ms / 15.1 s = **3.82 %** | 250 ms / 15.1 s = **1.66 %** |
| 扫动 GPU 峰值 | 0.468 % | 0.078 % | 0.337 % |
| 句柄（扫动前 → 后） | 1685 → 1662 | 1985 → 1937 | 2367 → 2314 |
| GDI / USER 对象 | 101 / 71 | 121 / 70 | 119 / 73 |
| 工作集 | 248.6 MB | 294.3 MB | 347.6 MB |
| 桌面文件差异 | 0 | 0 | 0 |
| 整段检查 | 21 项全通过 | 21 项全通过 | 21 项全通过 |

spec §十九 的"Takeover 本身不得建立 polling loop"被回答了两次：**idle 10 秒的 CPU 预算**（上表）与 **idle 10 秒内 "The desktop holds …" / "Desktop layout loaded from …" 日志行数 = 0**（真有轮询就一定有日志）。enable 的耗时几乎全部是"扫描桌面 + 规划采纳"（10 项 2223 ms → 100 项 3653 ms），接管本身（问 shell view、读回、写标记）在 1 s 量级。

### 20.6 已知限制 / 诚实记录

- **"接管 + dock 指针"没有合体实测**：dock 交互在 3C 已用 41 项真机检查证明（原生桌面下），接管期间只验证了"画布只有一层"（`1 canvas window(s)`）与"接管后图标仍然藏着"。两者同时发生的指针路径没有单独跑——dock 与画布本来就是同一个画布服务的两层视图，本轮没有为这条组合再造一个 fixture（`Write-Layout` 不写 Dock 段，3D 的 fixture 里 dock 是关的）。
- **只实现了两级梯子，第二级未在真机触发**：本机走的是 `ShellViewFlags`，`IconWindow` 这一级只有代码与单测，没有实测到（要实测它就得先让第一级失败）。所以"退到第二级仍可恢复"是设计上成立、而不是测过的事实。
- **接管只在单屏 96 DPI 的单机 Windows 11 上实测**：`monitor  \\.\DISPLAY5 · 2560x1440 at 0,0 · 96 dpi (1x)`；多屏、高 DPI、以及"桌面被第三方工具改过"的组合未覆盖。
- **多用户 / 非英文桌面文件夹未覆盖**：扫描用的是 shell 报告的路径，没有被测过重定向过的 Desktop（如 OneDrive 接管桌面）——不过那条路径下扫描仍然只读。
- **接管期间没有"桌面条目实时变化"的自动同步实测**：`FileSystemWatcher` 的防抖路径有单测，真机上没有在接管中往桌面丢文件去验证它。
- **没有画 hover 名称标签、没有运行指示**（沿用 3C 的缺口）：桌面上的合成层没有文本渲染路径。
- **性能是一次测量**，不是分布；idle 10 s、扫动 13.9–15.1 s 各一段。CPU 百分比是"单核占比"，且含 harness 自身 `SendInput` 的节奏影响；100 项那档扫动百分比反而最低，因为每个条目小、指针跨越的放大项少。
- **验收脚本会最小化挡住桌面的窗口（结束恢复）、会关闭自己启动的 notepad、会 `taskkill explorer.exe` 后重启它**（沿用 Phase 1G/3B/3C 已获授权的做法）。它只移动真实指针、只读自家窗口与桌面文档；不读用户屏幕内容、不读凭据。
- **`feat:` 提交包含了它必须一起带上的测试改动**（`SettingsServiceTests`、`DesktopItemSyncServiceTests`）：设置搬家与同步服务签名变了，不一起带上 `Muralis.slnx` 就编不过；新增的测试类与 harness 在随后的 `test:` 提交里。

### 20.7 提交与验收产物

| 提交 | 内容 |
| --- | --- |
| `2dfb5b2` | feat: add the native desktop takeover service and its crash marker（Core 五态模型 + 标记存储 + Desktop shell view 梯子 + 文档 v4 + 探针脚本 + 架构守卫） |
| `44da80a` | feat: import windows desktop items（内容扫描 + 采纳规划 + 同步服务 + 只读扫描测试） |
| `fd6a3e0` | feat: add the desktop modes and the tray's desktop commands（模式服务 + 文档 v4 的接管段 + App 页面三选一与确认框 + 托盘两条保底命令 + `DesktopShutdown` + 设置搬家） |
| `f91ceda` | test: cover desktop takeover lifecycle（`Modes/DesktopModeServiceTests` + 崩溃恢复记录测试 + 身份语义三例 + harness 与探针） |
| `docs: complete interactive desktop phase` | 本节 + CHANGELOG（就是包含本表的那次提交，哈希见 `git log -1`） |

前四个提交各自都经 Debug 全量构建验证（0 警告 0 错误）；`fd6a3e0` 之前只有生产代码，`f91ceda` 之后 Debug / Release 双配置全量测试通过（Core 485 + Desktop 150）。

验收产物（`artifacts/`，未入库）：`p3d/full-run.txt` 与 `p3d/p3d-takeover-verify.json`（88 项全通过）、`p3d/perf-run.txt`（62 项全通过）、`p3d/residue-*.json.p3d-bak`（被移出用户 app-data 的残留，留档）。

**Phase 3 状态**：Phase 3A（Pointer Router）、3B（真实桌面条目与启动）、3C（交互式边缘 dock）、3D（原生桌面接管）四段全部落地，Phase 3「Interactive Desktop」完成。Muralis 现在可以在三种桌面模式之间切换，接管期间**不拥有用户桌面上的任何文件**——只读、只指、只停画图标，并且无论怎么退出都还得回去。

---

## 21. Phase 4 落地记录：Design Foundation、Desktop Experience 与 Pinned Apps Launcher（2026-09-17）

Phase 4 分三段，在同一天里连续做完，因此记在一节。**4A** 把界面语言与 dock 外壳立起来（Muralis Design Foundation、`DockHost` 三分区）。**4B** 把"桌面体验"抽成架构（三种模式、dock 自己的窗口边界、真实 Shelf）。**4C** 是 spec 的主体：把 dock 左侧的 Pinned Apps 从 mock 数据变成真正可使用、可配置、可持久化的启动区。

三者互为基础，而不是三次叠加：4C 的 Pinned Apps 只用 4A 的 `DockIcon` / token / `MotionTarget` 和 4B 的 dock 窗口，**没有新建第二套 dock、第二套图标系统、第二份设置**。提交是按层切的（Core+Desktop / App+UI / tests+harness / docs），不是按段切的——4A/4B 没有单独提交过，见 §21.8。

### 21.1 交付内容

**4A — Muralis Design Foundation 与三分区 dock 外壳**

- `src/Muralis.App/UI/Tokens/`：`Colors.xaml`（+ `Colors.Light` / `Colors.Dark`）、`Typography`、`Spacing`、`Radius`、`Elevation`、`Motion`——全 app 唯一的颜色 / 字号 / 间距 / 圆角 / 高度 / 时长来源。
- `Materials/`：`GlassSurface`（三档材质）+ `Materials.xaml`。
- `Motion/InteractionMotion.cs`：`HoverMotion`（进出 hover）、`TransformMotion`（`SetTranslateX` / `AnimateTranslateXAsync`）、`MotionDurations`（120 / 180 / 280 ms）。
- `Controls/`：`MuralisButton`、`MuralisCard`、`MuralisIconButton`、`MuralisNavigationItem`、`ControlVariants`，以及 dock 自己的 `DockIcon` / `DockAddTile`。
- `Playground/DesignPlaygroundPage`：`Ctrl+Shift+D` 打开的隐藏实验页，不进正式导航。
- `DockHost.xaml`：一个 `GlassSurface` 里五格——PinnedZone │ 分隔 │ ShelfScroller │ 分隔 │ Utilities。`MotionTargets` 把每个 `DockIcon` 的动效目标登记出来留给后面的动效引擎，本阶段一个都不用。
- `docs/design-foundation.md`；`DesignFoundationTests` + `Phase4AArchitectureTests` 钉住"不许新颜色、不许新 token、不许第二套 dock、Lab 不进导航"。

**4B — Desktop Experience 架构**

- `DesktopExperienceSettings`（`Native` / `CleanDesktop` / `FullTakeoverExperimental`）+ `DesktopExperienceService` + `IDesktopExperienceService`。模式是用户的选择，落盘在桌面文档里而不是 `settings.json`。
- **窗口边界搬了一次家**：3D 的 `ICleanDesktopShelfHost` 被换掉，代之以 `IDesktopDockHost`，归 dock 自己的 `DockExperienceService` 所有。理由是原来那个边界属于 Clean Desktop，而 dock 在完全原生的桌面上也要显示——它不是某个模式的从属物。Clean Desktop 现在只能 `EnsureVisibleAsync` / `ReleaseAsync`（**请求**），不能自己开关窗口。
- `DockExperienceService`：可见性 = `_wanted || _required`（用户要的，或桌面需要的），且只应用变化，所以"要求一件已经成立的事"不会闪窗口。
- `CleanDesktopPresentation` + `ICleanDesktopPresentation`（+ 非 Windows 上的 `UnavailableCleanDesktopPresentation`）：只改 shell view 的 `FWF_NOICONS`，退到 icon-list 窗口；marker 先写后藏、验证回来才删。
- `DesktopShelfService` + `IDesktopShelfService` + `DesktopShelfItem` / `DesktopShelfSnapshot`：真实桌面 Shelf，`FileSystemWatcher` 带防抖。
- `ShellIconProvider` + `IShellIconProvider` / `ShellIconData`：**唯一**的图标 provider。
- `DockLabPage`：隐藏实验页，只用来打印图标当前藏没藏。

**4C — Pinned Apps & Launcher**

Core：
- `PinnedApp`（`Id` / `DisplayName` / `LaunchTarget` / `IconIdentity` / `Kind` / `Identity` / `Arguments?` / `WorkingDirectory?`）+ `PinnedAppIdentity` + `PinnedAppTargets`（`.exe` / `.lnk`）。spec §3 要的九项齐全，"Order"由**列表次序**表达，不额外存一个会跟列表打架的序号。
- `PinnedApps`：纯规则——`Add` / `Remove` / `Move` / `FindByIdentity` / `MaximumCount = 12`。`PinnedAppAddResult` 把"加不进去"当成四种**结果**之一（`Added` / `Duplicate` / `LimitReached` / `Unsupported`）而不是异常，因为四种都要用用户的语言说出来。
- `IPinnedAppService` + `PinnedAppService`：唯一的 pinned 副本，`Changed` 事件挂在上面；一次变更一次落盘。
- `IApplicationLauncher` / `ApplicationLaunchRequest` / `ApplicationLaunchResult`（`Launched` / `Missing` / `Failed`）、`IApplicationInspector` / `ApplicationDescription`、`IApplicationLocationRevealer`。
- `DockSettings`（`IsVisible` + `PinnedApps`）落在 `settings.json` 的 `Dock` 段；`PinnedAppSettings.TryToPinnedApp` 逐条读，读不出来的那一条被跳过并计数。

Desktop：
- `ShellApplicationLauncher`：`ProcessStartInfo { UseShellExecute = true, Verb = "open" }`，`.lnk` 与 `.exe` 走同一条路。
- `ShellApplicationInspector`：`.exe` 用文件自己的版本信息取名字；`.lnk` 通过 COM `IShellLinkW` 只读解析。
- `ShellLocationRevealer`（"打开文件位置"）、`ShellLinkInterfaces`。
- `ShellIconProvider` 加尺寸参数：48 px 基准，按需 64 / 96，按尺寸分别缓存（`IconBitmapCache` LRU）。

App：
- `PinnedAppsPresenter` + `PinnedAppViewItem`：把 `IPinnedAppService` 的列表镜像成视图项，复用还活着的项、插入新的、删掉超出的；图标 fire-and-forget。
- `DockHost`：pinned zone 的 `Tapped` / `PointerPressed|Moved|Released|Canceled` / `DragOver` / `Drop` / `ContextFlyout`（Open / Open File Location / Unpin），外加 `PinnedAddRequested` 与 `PinnedNotice` 两个出口。
- `PinnedZoneDrag`：一次拖拽的全部状态机（`Press` / `Move` / `ReleaseAsync` / `Cancel` / `OnProjected`）。
- `DockAddTile`：空状态与 `+ Add App` 用同一个 tile，`IsFull` 时 0.5 透明度。
- `DesktopDockHost`：真实的 dock 窗口（无边框、置顶、不进 Alt+Tab、不可缩放、不可最小化），`PrepareAsync` / `ShowAsync` / `HideAsync` / `CloseAsync`。
- Settings 的最小 Dock 段（一个开关 + 一行说明）；11 条 `Dock_Pinned_*` 本地化字符串（中英）。

### 21.2 关键实现决定

- **身份是"归一化后的落地路径"，而且存下来**：`PinnedAppIdentity.Of(launchTarget, resolved)` 优先用 shortcut 解析出来的目标，`Path.GetFullPath` + `ToUpperInvariant`；解析不出来的 shortcut 退回它自己的路径。所以 `.lnk` 和它指向的 `.exe` 是**同一个应用**——harness 里两条路径都记成 `Duplicate`。`Identity` 是写进文件的，不是每次启动重算，所以一个目标在磁盘上被移动不会悄悄变成第二个 pin。
- **一个 pin 只在 release 之后写一次盘**：`MoveAsync` 先 `SameOrder` 判等，顺序没变连写都不写。harness 测得"约 84 次移动 / 6 次落盘"。
- **数据先于图片**：`Project` 立刻把视图项建好并触发 `Projected`，图标是 `_ = LoadIconAsync(item)`（spec §22 的"不要阻塞启动去同步提取几十个图标"）。`Projected` 只在视图项已经对上保存的列表之后才发，所以拖拽落点的清理与重排发生在同一个回调里，不会有一帧画在旧位置。
- **拖拽期什么都不做**：`PinnedZoneDrag.Move` 里只有三件事——`TransformMotion.SetTranslateX(_carried!, delta)`、`DockReorder.TargetIndex(...)`、以及目标索引真的变了才 `PlaceIndicator()`。没有动画、没有 await、没有布局、没有写盘（spec §16）。`TheReorderHotPathStaysDirectManipulationOnly` 用代码扫描守着：`Move(` 到 `ReleaseAsync()` 之间不许出现 `await` / `MoveAsync` / `SaveAsync` / `Persist` / `Animate` / `Scale` / `Glow`。
- **插入位置是一条线，不是"让邻居让开"**：邻居让开就得在落点之后的那次布局变化上再让回来，而两者之间没有一帧能干净地清掉，必然画出双倍偏移。一条线说同样的事，而且不会跟手上那个 icon 打架。
- **spring 只在落下**：`ReleaseAsync` 里 `AnimateTranslateXAsync(..., MotionDurations.Standard, CubicEase EaseOut)` 把 icon 送到"它被显示要去的位置"，**然后**才 `_commit`。
- **不用箭头按钮**：spec §15 允许"实现成本明显过高时"退回 context move。实测拖拽成本不高（见 §21.6），所以做的是真的拖拽。
- **unpin 只删配置**：`RemoveAsync` 只改 `_items` 再落盘，`File.Delete` 在这个文件里不存在。harness 每次 unpin 前后对 fixture 文件做 `长度|写入时间|文件名` 比对，`->` 两边完全相同。
- **读不出来的一条不拖垮整份配置**：`TryToPinnedApp` 拒绝**描述不出应用**的条目（缺 Id / 名字 / 目标 / `Kind` 不合法）；只是**不完整**的条目则从它已有的部分补齐（缺 `Identity` 就从 `LaunchTarget` 算，缺 `IconIdentity` 就用 `LaunchTarget`），这样手改过的文件仍然能用。`RestoreAsync` 把被拒的条数记一条 warning，剩下的照常加载。
- **dock 窗口只有一个主人**：`DockExperienceService` 是唯一 show/hide 的地方（`OnlyTheDockExperienceServiceShowsOrHidesTheDockWindow` 用白名单守着）。
- **先有 dock，再藏图标**：`CleanDesktopPresentation.ActivateAsync` 的顺序是"读到真实 Shelf → 要求 dock 可见 → 记原始 flags → 写 marker → 藏图标"。harness 的 `clean:` 段在用户**主动关掉 dock** 的情况下验证 dock 仍然被顶上来，并且 pins 在里面仍然能启动。
- **4C 的 pinned 路径不认识任何桌面模式**：`TheDockNeedsNothingFromAnyDesktopMode` 逐文件扫 `CleanDesktop` / `IDesktopExperienceService` / `DesktopExperienceMode`，六个 pin 路径文件里一个都不许出现（spec §29/§30）。

### 21.3 spec §43 的 17 项完成门

`tools/p4c-pinned-verify.ps1 -Stage full` = **69 项全通过**（`artifacts/p4c/full-run.txt`：harness 2 / pins 27 / restore 13 / missing 13 / clean 14）；性能另起 `-Stage perf` = **12 项全通过**（`artifacts/p4c/perf-run.txt`）。

| # | spec §43 | 实测（节选 harness 文案） |
| --- | --- | --- |
| 1 | .exe 可固定 | `pins: the program was pinned`（…pinned from …\muralis-fixture-shell.exe）、`pins: the pin was written with the program it points at` |
| 2 | .lnk 可固定 | `pins: the shortcut was pinned`、`pins: the shortcut was written as a shortcut named by its own file` |
| 3 | 图标正确 | 单测层面成立：`ShellIconReaderTests`（尺寸分桶、BGRA 预乘、目标不存在给空）、`IconSurfaceLiveTests`（真图标画到 compositor 上、50 个图标的时间）、`IconBitmapCacheTests`（按尺寸缓存、LRU 预算）。**harness 读不到画出来的像素**——图标在 composition 层，UIA 只能读 label。见 §21.7 |
| 4 | 单击启动 | `pins: a click on the program pin started it`、`pins: a click on the shortcut pin started what it points at`、`pins: the app logged the start` |
| 5 | 重复项被正确处理 | `pins: the repeated program was refused as already pinned`（`Duplicate`）、`pins: a duplicate was not pinned`、`pins: a shortcut and its own program were recognised as one application` |
| 6 | 可取消固定 | `missing: the Unpin command was chosen`、`missing: the pin left the zone`、`missing: the pin left the saved list` |
| 7 | 可排序 | `restore: the drag left the zone in the new order`（`order shortcut, order program`） |
| 8 | Order 持久化 | `restore: the new order was saved`、`restore: the drag committed exactly once`（一次拖拽对一条 move 日志） |
| 9 | 重启恢复 | `restore: the reordered pins came back in the new order`、`restore: the restart said what it restored` |
| 10 | Missing target 不崩溃 | `missing:` 整段 13 项：仍然列出、点击只记 warning 且不起进程、context menu 仍在且能 unpin |
| 11 | Pinned Zone 不随 Shelf 滚动 | `ThePinnedZoneCannotScrollAwayWithTheShelf`：`DockHost.xaml` 里 `<ScrollViewer` 只出现一次（就是 Shelf 自己的），pinned zone 与 drop indicator 都在它外面 |
| 12 | Native 模式可用 | `pins:` + `restore:` 两段共 40 项，全程 `DesktopExperience.Mode = Native` |
| 13 | Clean Desktop 模式可用 | `clean:` 段 14 项（dock 被顶上来、pins 在、Explorer 真的报了 `FWF_NOICONS`、pin 仍能启动、退出时图标回来） |
| 14 | Real Shelf 无回归 | `clean: the real Shelf read the desktop folders`（1.7 ms，42 项 = 28 user + 14 public）、`clean: the dock still has its 'Desktop Shelf' zone`、`clean: the desktop files the Shelf reads were not touched`（44 → 44） |
| 15 | Phase 3 无回归 | `TheShellAndItsSurfacesAreStillThere` 断言 3B/3C/3D 的 surface 与 service 文件仍在、`FileSystemWatcher` + `DebounceDelay` 仍在；Desktop 的 `Takeover` / `Modes` / `Shelf` / `Input` / `Surfaces` 全部仍绿（165 项） |
| 16 | Build / Tests 全部通过 | Debug 与 Release 双配置 **0 警告 0 错误**；Core **581** + Desktop **165**（共 746） |
| 17 | 真机运行通过 | 上面 `full` 69 + `perf` 12，两次都是 `ALL CHECKS PASSED` |

### 21.4 spec §40 的真机矩阵

| spec §40 | 实测 |
| --- | --- |
| Add .exe | `pins: the program was pinned` + `pins: the pin is named from the program itself`（名字来自文件版本信息，不是文件名） |
| Add .lnk | `pins: the shortcut was pinned` + `pins: the shortcut was pinned as the application it resolves to, not as a file`（identity 是解析后的目标，launch target 仍是 `.lnk`） |
| Duplicate add | `pins: the repeated program was refused as already pinned`；再点一次**没有**第二条 pinned 日志（`pinned lines 15 -> 15`），zone 里仍恰好两项 |
| Launch .exe | `pins: a click on the program pin started it : 1 new process(es)` |
| Launch .lnk | `pins: the shortcut was started through the shell, so the program it points at ran : …muralis-fixture-alt.exe` |
| Reorder | `restore: the drag left the zone in the new order` + `restore: the drag launched nothing : 0 fixture process(es)` |
| Restart Muralis | `restore: the dock window came up after the restart` |
| Order restored | `restore: the reordered pins came back in the new order`（拖动前后顺序相反，重启后保持拖动后的顺序） |
| Remove | `missing: the Unpin command was chosen` + `missing: the pin left the zone` + `missing: the pin left the saved list` |
| Missing target handling | `missing: clicking it said the target is gone`（`[WRN] … points at …, which is gone`）+ `missing: clicking it started nothing` + `missing: the pin is still listed after a refused click` |
| Native Mode | `pins:` / `restore:` / `missing:` 三段 |
| Clean Desktop Mode | `clean:` 段 14 项 |
| Shelf remains usable | `clean: the dock still has its 'Desktop Shelf' zone : 42 text(s)`、`clean: the dock still has its 'Utilities' zone : 2 text(s)`、`clean: the real Shelf read the desktop folders` |
| App exit | 每一段收尾的 `the app exited when the window was closed : no process left`（dock 是独立窗口，`CloseAsync` 关掉它、再由关掉主窗口结束消息循环） |

### 21.5 测试与提交

新增测试类：

- Core — `DockShell/PinnedAppTests`（身份、归一化、`KindOf`、`Add` 的四种结果、`Remove`、`Move`）、`DockShell/PinnedAppPersistenceTests`（序列化往返、条目被拒、不完整条目补齐、`Dock` 段默认值）、`DockShell/DockShellLayoutTests`、`Services/PinnedAppServiceTests`（21 项，用替身 `IApplicationLauncher` / `IApplicationInspector`）、`Services/DockExperienceServiceTests`（15 项：`_wanted || _required`、只应用变化、`ShutdownAsync` 之后不再开窗）、`Services/DesktopExperienceServiceTests`、`Architecture/DesignFoundationTests`、`Architecture/Phase4AArchitectureTests`、`Architecture/Phase4CArchitectureTests`。
- Desktop — `Launch/ShellApplicationLauncherTests`、`Launch/ShellApplicationInspectorTests`、`Shelf/DesktopShelfServiceTests`、`CleanDesktop/CleanDesktopLiveTests`；另有 `Icons/`（`ShellIconReaderTests` / `IconBitmapCacheTests` / `IconSurfaceLiveTests`）与更新过的 `Architecture/ArchitectureGuardTests`。

**没有任何单元测试真的启动第三方程序**（spec §38）：`PinnedAppServiceTests` 全程用替身 launcher 与 inspector；`ShellApplicationLauncherTests` 只走"目标不在"这类不会起进程的路径，另用 fixture 自己的文件。

数字：Core 485 → **581**，Desktop 150 → **165**（共 746）；Debug / Release 双配置 **0 警告 0 错误**。

### 21.6 性能（12 个 pin = 当前策略的设计上限）

`-Stage perf` 把 zone 种满 12 个 pin 再启动，两次独立运行：

| 指标 | 运行 A | 运行 B |
| --- | --- | --- |
| 启动 → 主窗口 | 731 ms | 761 ms |
| 启动 → dock 窗口 | 2094 ms | 2202 ms |
| 启动 → 12 个 label 全部就位 | 2251 ms | 2381 ms |
| idle（zone 满，静默后 5 秒） | 0.0 ms = 单核 0% | 0.0 ms = 单核 0% |
| 指针跟手，最好一轮（40 次移动） | **0.391 ms/次**（187.5 / 187.5 / 15.6 ms） | **3.516 ms/次**（171.9 / 203.1 / 140.6 ms） |
| 一次完整拖拽 + 重排（6 轮平均） | 419.3 ms 应用 CPU / 轮 | 375.0 ms / 轮 |
| 其中"跟着指针走"（6 轮合计） | 1078.1 ms | 906.3 ms |
| 其中"落下"（6 轮合计 / 每次） | 1437.5 ms / 239.6 ms | 1343.8 ms / 224.0 ms |
| 落盘 | 6 次重排 = 6 条 move 日志 | 同 |

**热路径本身很便宜**：最好一轮 0.391 ms/次移动（约 1.22% 单核）。也就是说"1:1 跟手"不是靠 spring 掩盖出来的——直接操纵路径确实轻，这正是 spec §41 要问的那件事。把两次运行放在一起看，跟手的读数落在 **0.39–3.5 ms/次**之间，而 harness 每次移动之间要等 20 ms，所以即便是最差的一轮，每次移动也只用掉了约五分之一的预算。

**但跟手读数不稳定，我不把它当成一个确定的数**：同一段代码在三轮里量出过 `187.5 / 187.5 / 15.6` 和 `171.9 / 203.1 / 140.6` 两种量级，更早一次还被 UIA 读数污染到 30 ms/次，idle 也见过一次 2250 ms/5 s。差异来自这个 app 自己的后台工作（桌面条目的图标、壁纸刷新）恰好落进某一轮。所以 harness 报的是**三轮里最安静的一轮**，并且在量 idle 之前先等一个低于 50 ms 的"安静秒"（`perf: the app went quiet with the zone full`）。**单次采样在这个 app 上不可靠**，这一点在本阶段被实测了两次。

**落下的那一侧很贵，我把它记为本阶段的已知性能缺口**：一次真正的落下约 224–240 ms 应用 CPU。这个数字把四件事加在一起——`AnimateTranslateXAsync` 的 spring、一次 `MoveAsync`（落盘）、`Project` 重建视图项、以及 WinUI 对 zone 的重新布局与重画。**我没有把它拆到单一原因上**：拆它需要在落点路径上插桩，而那正是本阶段要求保持干净的地方，在一个验收已经全绿的阶段末尾动它不划算。下次要动 spring 曲线或做 Nexus 放大之前，这里应该先量一次分段。

> **这一段后来被 §22 复核过，结论要按 §22 读。** 4C.1 就是这里说的"先量一次分段"，量完的结果是：那 224–240 ms **不是落下的成本，是外部 500 ms 测量窗口的成本**（一条窗口里必然装着落位动画之后带起的 hover 动画、落下后的布局 pass、以及别的后台线程的工作）。真实的落下是 **209 ms 墙钟**，其中 203.9 ms 是那条 180 ms 落位动画在 compositor 上跑完的时间，应用自己要做的全部工作约 5.2 ms。这段文字保留原样，作为"当时为什么没量、以及量之前猜的是什么"的记录。

### 21.7 已知限制 / 诚实记录

- **"图标正确"没有真机像素级验证**：pinned icon 画在 composition 层，UIA 读不到。harness 能证明的是"pin 被正确读出、label 正确、点得动、起得来"，`Icons/` 的单测能证明"尺寸分桶对、BGRA 转换对、缓存按尺寸键、50 个图标的时间可用、真图标能画到 compositor 上"，但**"屏幕上那个图标长得对不对"仍是人工验收项**。spec §41 的高刷手感同理。
- **跟手读数不稳定，落点成本未归因**（见 §21.6）。**落点那一半已在 §22 完成归因**：主要组成是那条 180 ms 的落位动画（203.9 ms），应用的落点工作约 5.2 ms，UI 线程最多 3 个计数节拍；跟手读数不稳定这一条在 §22.6 里仍然是限制（walk 报的是上界，不是路径自身成本）。
- **`.appref-ms` / UWP / packaged app / AppUserModelId 不支持**，按 spec §4/§35 是 Non Goal。`ShellApplicationInspector` 把这类 shortcut 描述成"解析不出目标"，于是它被当作 shortcut 自己的路径固定，身份也退化成 shortcut 路径——**它能固定、能启动，但不会和别的 pin 撞身份**。扩展点留在这里，本阶段不碰。
- **没有运行状态检测、没有多实例处理、没有窗口激活**（spec §10/§33/§34/§42）。同一个 pin 连点两次会起两个进程，由目标应用自己决定。
- **`MaximumCount = 12` 是本阶段的策略，不是产品常量**：spec §14 要求"有合理的最大策略、不要把 dock 撑满整个屏幕"，§37 的目标是 10–30。测试断言它落在 10–30 之间。满了之后 `DockAddTile` 变暗，picker 在打开前就被 `Dock_Pinned_Full` 挡掉；**没有 overflow 菜单**（spec §14 明确说不做第二层菜单）。
- **只测了 12 个 pin**：20 / 30 个的情况没有单独跑，而 12 已经等于当前策略允许的最大值。
- **`settings.json` 里 `CloseToTray` 的原值不可恢复**：本阶段早前有一次 harness 中途失败，它的清理没跑完，把 fixture 的 pin 和 `CloseToTray: false` 留在了用户的真实设置里，而下一次运行的备份又把更早的干净备份覆盖掉了。发现后我把 `Dock.PinnedApps` 清空、`CloseToTray` 恢复成 app 默认的 `true` 写回。**原始值已经找不回来了**——这是本阶段真实发生过的一次对用户文件的污染，记在这里。结构性修复已经做了：`Backup-Settings` 现在遇到遗留备份会**采用**而不是覆盖它，清理步骤也不再会中断整个 run。
- **`settings.json` 的 `SchemaVersion` 从 1 走到了 2**：新增的 `DesktopExperience` 与 `Dock` 两段都是可选的，`LoadAsync` 用 `??=` 把缺失的段落补齐，所以一份 v1 文件读进来不会失败，只是拿到默认的 dock 与模式。`AppSettings.SchemaVersion` 目前**没有任何读者会拒绝更新的版本**（与桌面文档 v4 不同，那份会拒绝），这是有意的宽松加载。
- **多屏 / 高 DPI 未覆盖**：dock 窗口的位置取 `DisplayArea` 的工作区（宽 `Math.Min(960, …)`、高 116、底部留 16），只在本机单屏 2560×1440 @96 DPI 上实测过。
- **`Phase4AArchitectureTests` 里那条 pinned zone 断言是为 4C 主动改过的**：4A 当时断言 pinned zone 由普通 item list 喂，4C 之后它由 `PinnedAppsPresenter` 喂，所以那条断言连同注释一起更新了。这是有意的，不是测试被绕过。

### 21.8 提交与验收产物

提交按**层**切，不按段切——4A/4B 从未单独提交过，所以 4C 的提交里必然带着它们：

| 提交 | 内容 |
| --- | --- |
| `2fccf50` | feat: add the dock experience layer, a real shelf and pinned app launching（Core 抽象 / DockShell 模型与纯规则 / 两个设置段与加载补齐 / `PinnedAppService` / Clean Desktop 与 Shelf 与 Shell 图标 / `ShellApplicationLauncher` / `ShellApplicationInspector` / `ShellLocationRevealer` / dock 窗口边界） |
| `a7971a8` | feat: pin applications to the dock and give the app its design foundation（`src/Muralis.App/UI/` 全部 + Design Foundation 文档 + App 侧装配、MainWindow、Settings 的最小 Dock 段、`Dock_Pinned_*` 本地化） |
| `a1787bd` | test: cover the pinned apps, the launcher contract and the dock boundaries（Core / Desktop 新增测试类 + `ArchitectureGuardTests` 与 `SettingsServiceTests` 的更新 + `tools/p4c-pinned-verify.ps1` + `tools/p3-common.ps1`） |
| `docs: complete the pinned apps phase` | 本节 + CHANGELOG（就是包含本表的那次提交，哈希见 `git log -1`） |

- 前两个提交各自经 Debug 全量构建验证（0 警告 0 错误）；`a1787bd` 之后 Debug / Release 双配置全量构建 + 全量测试通过（Core 581 + Desktop 150→165）。
- 每个提交都单独验过"只有它自己时也能编过"，而不是只看最终状态：`2fccf50` 只改了 `CanvasSurfaceContent.cs` 的三个 **private** 方法，且 App 侧没有任何文件引用它，所以带上 App 的 HEAD 状态仍然编得过。

验收产物（`artifacts/`，未入库）：`p4c/full-run.txt`（69 项全通过）、`p4c/perf-run.txt`（12 项全通过）、`p4c/p4c-pinned-verify.json`（脚本每次运行覆盖写，UTF-8 带 BOM）。

## 22. Phase 4C.1 落地记录：Pinned Dock Drop 的性能闸门（2026-09-17）

§21.6 把"一次落下约 224–240 ms 应用 CPU"记成了本阶段的已知缺口，并写着"下次要动 spring 曲线或做 Nexus 放大之前，这里应该先量一次分段"。本节就是那次分段。

**先说结论：那 224–240 ms 不是落下的成本，是测量窗口的成本。** 真实的落下是 **209–212 ms 墙钟**，其中 **203.9 ms 是那条 180 ms 的落位动画在 compositor 上跑完的时间**；落下期间应用自己要做的全部工作——提交、模型、落盘、投影、校验——合计 **约 5.2 ms**。UI 线程在整个落下里最多花掉 **46.9 ms**（24 次测量中最大的一次，即 3 个 15.625 ms 的计数节拍，中位 1 个节拍），所以**不存在 200 ms 级的 UI 线程阻塞路径**。跟手路径一行未改，仍是 raw delta 直接翻译。

### 22.1 交付内容

**剖面器 `src/Muralis.Core/Diagnostics/DropProfile.cs`（新，约 380 行）**

一个只在 `MURALIS_DOCK_PROFILE=1` 时工作的 opt-in 剖面器，把一次落下按段写进 `logs/dock-drop-profile.jsonl`，一行一个 JSON 对象。每一段（`seg`）记录：墙钟、**进程 CPU**、**该线程 CPU**、该线程分配字节、线程 id（跨越 await 的段还会记下回来的线程）、所属落下编号、以及这一行是否在该落下关闭之后才到达（`late`）。每个落下自身还有一条 `end`：墙钟、进程 CPU、**开这个落下的那条线程的 CPU**、分配。另有 `mark`（由调用方给的数字：布局 pass）与 `note`（不是时长的事件：落下动画报完成、排了一次落盘、图标被再读）。

关了它没有成本：`Measure()` 返回一个 default 的 `Scope` struct，`Dispose()` 立刻返回，调用点是"一次 bool 读 + 一次默认结构体构造"。

**插桩点按"报告读者会问的阶段"命名，而不是按包含它的代码命名。** spec 里点名要量的每一件事都有对应的一段：

| 要量的东西 | 段名 |
| --- | --- |
| pointer release handling | `release.capture` |
| candidate / order commit | `release.commit`、`commit.mutex`、`commit.model` |
| collection / model update | `ui.project.move`、`ui.project.insert` |
| persistence serialization | `settings.serialize` |
| settings write | `settings.open`、`settings.commit`、`settings.lock` |
| icon / cache activity | `ui.project.icons`（+ note `ui.project.icons.read`） |
| presenter refresh | `ui.project`（含 `items`/`trim`/`notify`）、`ui.project.apply` |
| UI collection refresh | `ui.project.items`、`ui.project.move` |
| Composition reset / spring setup | `motion.animate.setup`、`motion.animate.begin`（+ note `motion.animate.completed`） |
| logging | `commit.log` |
| synchronous dispatcher work | `publish.marshal`、`host.projected`、`layout.pass1` |
| 其余 | `dock.available`、`motion.pref`、`profile.write`、`release.settle`、`release.finish` |

**`src/Muralis.App/UI/Dock/DockLayoutMeter.cs`（新）**

落下后的那次布局 pass 发生在"要求布局的那个回调"返回之后，任何 scope 都看不见它。它用同一个时钟从外面计时，只在该落下窗口内跟随（第一次 pass 之后超过 120 ms 才来的 pass 会被丢弃——那是基准自己的窗口探针，不是落下的布局），报表里同时给出调用次数，因为次数本身就是读数的一部分：它说出了这次 pass 到底属于多少次落下。

**`tools/p4c-pinned-verify.ps1 -Stage dropbench`（新）**

一次稳定基准：种满 12 个 pin（当前策略的设计上限）、等一个低于 50 ms 的"安静秒"、**2 次预热（不计）**、**3 轮"跟着手走不落下"**（报中位/最好/最差）、**8 次计入的落下**（每次报该轮的读数与顺序是否正确），并给出中位/最好/最差，而不是只报最好的一次。它不顺带跑进 `-Stage full`，因为它需要开着剖面器启动。

**`PinnedAppViewItem.Update` 的一处修正**

`Tooltip` 是"有警告就是警告、没有就是名字"，只有这两者变了它才可能变；原来 `Update` 无条件 `Raise(nameof(Tooltip))`，于是一次纯重排会让 zone 里 12 个 pin 全部重新求值一遍 `ToolTipService.ToolTip` 绑定。现在只在名字变了的那条分支里 raise。

**跨线程的分配字节不再上报**

分配是每线程的计数器，一个跨 await 的段在两条线程上相减得不到任何量（报告里出现过 −90 MB）。现在这种段写 `null`。

### 22.2 关键实现决定

- **默认关闭，用环境变量打开。** 落点路径是本阶段要求保持干净的地方，剖面器不能常驻。关掉时它的成本是每个调用点一次 bool 读，没有分配、没有锁、没有 syscall；打开时它的自身开销也在报告里（`profile.write` 中位 0.31 ms × 2 次/落下）。
- **进程 CPU 和线程 CPU 都记。** 进程数回答"一共干了多少活"，也是 harness 从外面读到的那个量，所以两者可比；线程数回答"有多少落在了同时还要画帧、读输入的那条线程上"，那才是一帧卡住的量。一个在后台线程也在干活时跑的段，用进程数会被记到别人的账上。同一段跨 await 回来换了线程时，线程数写 `null` 而不是硬减。
- **诚实的量化下限：`TotalProcessorTime`（进程和线程都是）以 15.625 ms 为一个计数节拍。** 所以任何小于一个节拍的分段 CPU 只会读成 0 或 15.6，报告里的 `thCpu` 列不是连续量。本阶段所有"UI 线程花了多少"的结论都建立在这个粒度上，并且按节拍说（"最多 3 个节拍"），不当成精确值。
- **外面测的窗口数无法变成"只关于落下"。** 一条 500 ms 的窗口里必然装着落下以外的东西（落位动画之后 `ResumePointerMotion` 带起的 hover 动画、落下后的布局 pass、以及这个 app 自己别的线程的后台工作）。本阶段实测到外面读数的三个证据：24 次读数是 15.625 的整数倍；同一条 walk 在 1.25 s 里被读成过 **0.0 ms**（`187.5 / 0.0 / 265.6` 三轮）；`-Stage perf` 与 `-Stage dropbench` 在同一版本上也会差出一个量级。所以报告里"落下成本"一律用应用自己的记录，外面的数只用来做交叉参照。
- **不动的东西（已量、已拒）**：落位动画的时长（`MotionDurations.Standard` = 180 ms，是设计令牌，为了数字好看去改它就是拿手感换读数）；`ItemsControl` → `ItemsRepeater`（重排后的那次布局 pass 中位 18–21 ms，占一次落下的不到 10%，换控件属于动 dock 架构）；`settings.json` 每次落下写两遍（1 次 `Update` 的 fire-and-forget + 1 次 `PersistAsync` 的 await，实测 2.00 次序列化 / 2.00 次文件替换，省下来是 1.5 ms 墙钟和约 4 ms 线程池 CPU，代价是改一个共享服务的写入语义——按"不要为了数字好看牺牲数据可靠性"不做，但数字记在这里）。

### 22.3 分段数据（优化前 / 优化后）

同一台机器、同一份基准、Debug 构建、开着剖面器，每轮 12 个 pin、8 次计入的落下。三列口径完全相同：

| 指标（每次落下） | 优化前 | 优化后 A | 优化后 B |
| --- | --- | --- | --- |
| 落下墙钟（应用自量，中位） | 212.5 ms | 210.4 ms | **209.2 ms** |
| 落下墙钟范围 | 209.9–214.9 | 208.0–213.3 | 207.4–210.8 |
| 落位动画 `release.settle` | 205.18 ms | 204.52 ms | 204.06 ms |
| 提交 `release.commit` | 6.90 ms | 5.15 ms | **4.97 ms** |
| 投影 `ui.project` | 4.29 ms | 2.31 ms | **2.55 ms** |
| ↳ 12 × `ui.project.apply` | 0.165 ms/次（合 2.08 ms） | 0.024 ms/次 | **0.023 ms/次（合 0.32 ms）** |
| ↳ 12 × `Apply` 的分配 | 16.6 KB | 10.8 KB | 10.8 KB |
| ↳ `ui.project.move` | 1.74 ms | 1.44 ms | 1.68 ms |
| 落下期间 **UI 线程 CPU** | 0–31.2 ms | 0–46.9 ms | 0–15.6 ms |
| 落下期间进程 CPU | 31.3–125.0 ms | 46.9–93.8 ms | 31.3–93.8 ms |
| 落下后的布局 pass `layout.pass1` | 19.41 ms | 21.10 ms | 18.18 ms |
| 外部 500 ms 窗口读数 | 中位 203.1（109.4–265.6） | 中位 187.5（62.5–390.6） | 中位 187.5（78.1–250.0） |

把 24 次落下的 UI 线程 CPU 放在一起看：**0 / 15.6 / 31.2 / 46.9 ms 四个值，中位 1 个节拍（15.6 ms），最大 3 个节拍（46.9 ms）**。这就是"一次落下到底占住 UI 线程多久"的答案。

**落位动画那 204 ms 的拆法**（优化后 B）：要它跑 **180.0 ms**，它实际跑了 **203.9 ms**（中位，最大 205.3），动画报完成之后回到 `await` 处只花了 **0.1 ms**。也就是说：多出来的约 24 ms 全在 storyboard 自己从开始到"我结束了"之间（compositor 的完成通知延迟，约一个半帧），**不是应用代码造成的**；`setup` 0.13 ms、`begin` 0.03 ms、回来 0.1 ms，加起来 0.26 ms。

**剩下那 5 ms 的组成**（比 0.1 ms 大的每一项，优化后 B，中位）：`persist.awaited` 2.51、`ui.project.move` 1.68、`settings.commit` 0.79 × 2、`persist.update` 0.47、`ui.project.notify` 0.33、`release.finish` 0.29、`settings.serialize` 0.24 × 2、`settings.open` 0.23 × 2、`ui.project.apply` 0.023 × 12、`commit.log` 0.12。

**这一次优化的量级要说清楚**：修正 tooltip 那处之后，投影快了约 42%（4.29 → 2.55 ms）、12 次 `Apply` 快了约 7 倍（2.08 → 0.32 ms）、提交少了约 28%（6.90 → 4.97 ms）。放到一次 209 ms 的落下里，这是 **约 2.5 ms，1.2%**——是实打实的浪费被去掉了，但**它不是瓶颈**，瓶颈是那条设计上就要跑的 180 ms 动画。跟手一侧一行未改。

### 22.4 九项完成门

| 门 | 结论 | 证据 |
| --- | --- | --- |
| 找到 drop cost 的主要组成 | ✅ | 203.9 of 209 ms 是落位动画；其余 5.2 ms 逐项列出（§22.3） |
| 有数据证明，而非推测 | ✅ | 每个数字都来自应用自己的记录或进程计数；两条早先的猜测被推翻（见 §22.6） |
| 不再存在明显 200 ms+ UI-thread blocking drop path | ✅ | 24 次落下，UI 线程最多 3 个节拍（46.9 ms），中位 1 个 |
| 快速拖动仍然 1:1 跟手 | ✅ | 跟手路径未改（`SetTranslateX` 仍是 raw delta）；3 轮 walk 中位 2.34 ms/次移动 |
| reorder 正确 | ✅ | 8/8 次落下 UIA 读回的顺序正确 |
| persistence 正确 | ✅ | 8 次落下 = 8 条 move 日志，无多余写入；序列化 2.00 / 文件替换 2.00 与优化前一致 |
| restart 后顺序正确 | ✅ | 最后一轮把前两个 pin 放回原位，重启后与种下的顺序一致（既有断言） |
| 全部现有 tests / harness 通过 | ✅ | Core 581 + Desktop 165；`-Stage full` 69 项（本次复跑见 §22.5） |
| Debug / Release clean | ✅ | 双配置 0 警告 0 错误 |

### 22.5 测试与提交

改动全部落地之后、在同一份 HEAD 上复跑：

| 检查 | 结果 |
| --- | --- |
| `Muralis.Core.Tests`（Debug） | **581 通过，0 失败** |
| `Muralis.Desktop.Tests`（Debug） | **165 通过，0 失败** |
| 合计 | **746 通过，0 失败** |
| `-Stage full`（`artifacts/p4c/full-run-4c1.txt`） | **69 项，0 失败** |
| `-Stage dropbench` 优化前（`dropbench-before3.txt`） / 优化后（`dropbench-after2.txt`） | **各 13 项，0 失败** |
| `-Stage perf`（未受本次改动影响，`perf-run.txt`） | 12 项，0 失败 |
| Debug 全量构建 | **0 警告 0 错误** |
| Release 全量构建 | **0 警告 0 错误** |

`-Stage full` 里与本次改动关系最近的四条：`restore: they came back in the saved order`、`restore: the drag committed exactly once`、`restore: the new order was saved`、`restore: the reordered pins came back in the new order`——也就是说"拖动 → 落盘 → 重启后顺序正确"这条链在插桩之后仍然是同一份结果，优化没有拿走任何一条数据可靠性。

提交按"代码 / 记录"切：

| 提交 | 内容 |
| --- | --- |
| `perf: instrument the dock drop path and stop re-evaluating every pin's tooltip` | `Diagnostics/DropProfile.cs`（新）、`UI/Dock/DockLayoutMeter.cs`（新）、`PinnedAppViewItem.Update` 的 tooltip 修正、`PinnedAppsPresenter` / `PinnedZoneDrag` / `DockHost.xaml.cs` / `InteractionMotion` / `PinnedAppService` / `SettingsService` 的插桩、`tools/p4c-pinned-verify.ps1` 的 `dropbench` 阶段 |
| `docs: record the pinned dock drop performance gate` | 本节 + CHANGELOG（就是包含本表的那次提交，哈希见 `git log -1`） |

代码那一提交只做两件事：把量点插进去、把一处确认过的重复通知去掉。**去掉的那一处不改变任何外部行为**——`Tooltip` 的取值仍然由"名字或警告"决定，只是不再在两者都没变时重新 raise；`-Stage full` 里 `pins` 与 `restore` 两段的 31 条断言（含 tooltip 与 label 的 UIA 读数）全部照旧通过。除此之外，落点路径的逻辑一行未动。

验收产物（`artifacts/`，未入库）：`p4c/full-run-4c1.txt`（69 项）、`p4c/dropbench-before3.txt` 与 `p4c/dropbench-after2.txt`（各 13 项）、`p4c/drop-profile.jsonl`（原始逐段记录）。

### 22.6 已知限制 / 诚实记录

- **跟手读数仍然不稳定**：3 轮 walk 的中位落在 **1.95 / 2.34 / 5.08 ms/次**之间，最好一轮 0.39 ms，最差一轮 10.16 ms。原因是这次 walk **不落下**，但仍把 40 次合成指针移动灌给了整台机器，而这个 app 的桌面指针路由器、hover spring、Shelf 都在处理它们——所以这个数**不是拖动路径自己的成本，是它的上界**。要把它变成拖动路径自己的数，得在 move 路径上也插桩，本阶段没有做（本阶段的任务是落下）。
- **15.625 ms 的节拍是硬下限**：任何"某段花了多少 CPU"只要小于一个节拍就不可分辨。所以本阶段没有、也无法回答"提交里的哪一步最贵"到 1 ms 以下；能回答的是"没有哪一步超过一个节拍"。
- **没有做 Release 构建的基准**：基准跑在 Debug 上（与 §21.6 的 perf 阶段口径一致），因为要在同一份代码上做前后对比。绝对数字在 Release 下会更低，比例关系不变。
- **没有验证高刷 / 多屏 / 高 DPI 下的落下**，与 §21.7 相同的范围限制。
- **两条被数据推翻的猜测**（本阶段实际发生过）：(1)"每次 `AnimationsEnabled` 都 `new UISettings()` 很贵"——实测 0.048 ms/次，37 次/落下合计 2.6 ms 里大部分是它自己的测量开销；(2)"重排会重新提取图标"——`ui.project.icons.read` 在 24 次落下里是 0。两条都写进了插桩对照，这也是"不要根据猜测重构"的实测版。
- **落位动画那约 24 ms 的完成延迟没有解**：它在 storyboard 与 compositor 之间，不在应用的代码里（回程 0.1 ms）。要去掉它只能缩短动画时长，那是改设计令牌。

**Phase 4 状态**：Phase 4A（Design Foundation 与三分区 dock 外壳）、4B（Desktop Experience 架构）与 4C（Pinned Apps & Launcher）三段全部落地。dock 现在是一个真正的启动区：可以加 `.exe` 与 `.lnk`、拒绝重复、单击启动、拖动排序、重启恢复、目标消失时仍然留在那里并说得出原因，而且**从不碰用户桌面上的任何文件**。Pinned Apps 不依赖 Clean Desktop，在原生桌面与 Clean Desktop 下同样可用。

按 spec §45，**Phase 4C 到此为止，等待人工验收，不自行进入下一阶段**。

---

## 23. Desktop Experience UX Consolidation 落地记录：两种模式（2026-09-17）

Phase 4C 之后，产品里同时有四处在说"用户与桌面的关系"：壁纸页上的桌面模式三选一、设置页上独立的 Dock 卡片、托盘里两条桌面命令、以及 `settings.json` 里的 `DesktopExperience.Mode`。本节把它们收敛成两种模式。这一段必须在 Phase 4D（Nexus Motion Engine）之前完成——动效要挂在一个稳定的模式概念上。

**先说结论：产品现在只承认 Native 与 Muralis Mode 两种关系。这是"收敛"，不是"删 legacy"。** 冻结的 Phase 3 服务一行未改、recovery 代码一行未删、Dock / Shelf / Pinned Apps 的业务逻辑一行未动。用户可见的第三、第四种关系（Clean Desktop 开关、FullTakeoverExperimental）从产品里消失，只剩下转换器里"旧文件读得进来"的那几行读法。

### 23.1 产品模型：两个模式，一条分界

正式路线就此定下：

| 归 Windows / Explorer | 归 Muralis |
| --- | --- |
| 文件、Shell、桌面文件夹、文件关联、系统行为 | 壁纸、Dock、Desktop Shelf、Pinned Apps、Widgets、主题、动效、桌面组织 |

**Windows manages the content. Muralis manages the experience.**

两个模式各自是什么：

| 模式 | 桌面内容归谁 | Explorer 的桌面图标 | Muralis 层 |
| --- | --- | --- | --- |
| **Native** | Explorer | Explorer 画，一切不动 | 壁纸；dock 可用（§21 起它已是独立体验层，不依赖模式） |
| **Muralis Mode** | Explorer（Muralis 不碰任何文件） | 被隐藏（`IFolderView2::SetCurrentFolderFlags(FWF_NOICONS)`） | Dock（必开）+ Desktop Shelf + 壁纸 |

也就是说，两个模式区分的**不是"有没有 dock"**，而是**"Explorer 的图标在不在、桌面内容由谁呈现"**。这也解释了为什么退出 Muralis Mode 的第一件事是把图标还回去：桌面是用户的，Muralis 只是替它画。

撤下的名字与它们的读法（转换器的读取侧，见 §23.2）：

| 文件里写的 | 读成 | 为什么 |
| --- | --- | --- |
| `"Muralis"` | Muralis | 当前名 |
| `"CleanDesktop"` | Muralis | 旧名，同一个模式（干净桌面就是 Muralis Mode 的旧称） |
| `1` | Muralis | 旧数值枚举里 `CleanDesktop = 1`，新枚举里 `Muralis = 1` |
| `"Native"` / `0` | Native | 当前名 / 当前值 |
| `"FullTakeoverExperimental"`、`2`、其它数字、其它字符串、`null`、任何别的 JSON | Native | 撤下的接管不是目的地；不认识的输入一律 fail open，落在"Windows 什么都没变"上 |

### 23.2 落盘形状与迁移（schema 2 → 3）

- `AppSettings.CurrentSchemaVersion` = **3**；`DesktopExperienceService.RestoreAsync` 开头的 `UpgradeTheSavedShape()` 把老文件一次性抬到 3，让旧模式名在磁盘上不再存在，而不是每次读的时候翻译一遍。
- **marker 文件名与桌面文档版本一个都没动**：`clean-desktop-state.json`、`takeover-state.json`、`layout.json`（文档 v4）仍是原来的契约。本轮只动"产品怎么称呼它"，不动"底层怎么记它"。
- 迁移是 fail open 的：任何读不出来的值落到 Native，也就是"Windows 什么都没变"。没有哪条路径会因为一个坏值把用户的图标藏起来。

**顺手修掉的一个真 bug（属于本任务范围：migration 安全）**：`SettingsService` 的 `JsonSerializerOptions` 里，`Converters = { new JsonStringEnumConverter() }` 排在类型级 `[JsonConverter(typeof(DesktopExperienceModeJsonConverter))]` **之前**。`JsonSerializerOptions.Converters` 是**先**被咨询的，而 `JsonStringEnumConverter` 认领所有枚举类型——于是 `DesktopExperienceModeJsonConverter` 从来没有被调用过：旧文件里的 `"CleanDesktop"` 对通用转换器而言是一个不存在的枚举名，整份文件反序列化失败，`SettingsService` 吞掉异常、**整份 settings 静默回到默认值**。本次迁移第一次真的会读到旧名，所以这条路径必须修：把 `DesktopExperienceModeJsonConverter` 排在前面，并把"第一个认领类型的转换器胜出"写进注释。修完之后这条读法才有测试（§23.4）。

### 23.3 交付内容

**Core（`Muralis.Core`）**

- `DesktopExperienceMode` 收敛为 `{ Native, Muralis }`。撤下的名字**不在公开枚举里**，只存在于 `DesktopExperienceModeJsonConverter` 的读取分支。
- `DesktopExperienceStatus`：`IsCleanDesktopAvailable` → `IsMuralisAvailable`，`IsExperimentalTakeover` → `IsMuralis`。
- `DesktopExperienceService`：`RestoreAsync` 保留"启动时还欠 Explorer 就还给 Explorer"的那一次读，其余不变；进入 / 退出 / 失败路径的语义一行未动（失败一律落回 Native）。**改的是模式名与注释**。
- `AppSettings.CurrentSchemaVersion` 2 → 3；`SettingsService` 的转换器顺序修正。

**App（用户可见的收敛）**

- **设置页**：原来的 Dock 独立卡片并入新的「Muralis Mode」分组（卡片流不变、导航不变、其它页面不动）。分组里有分组说明 + Dock 开关；Muralis Mode 下 Dock 开关被锁（`CanChangeDockVisibility`）并显示 `Settings_Dock_Required` 说明。
- **动态壁纸页**：「你的桌面」与「边缘 Dock」两段删除（441 → **318** 行）；「桌面项目」画布卡片、它的 5 个入口与 URL 输入框原样保留。
- **托盘**：`MenuTurnOffDesktop` 删除，只留一条 `MenuRestoreDesktop`（走 `IDesktopExperienceService.ApplyAsync(Native)`），菜单固定 5 项。`RunDesktopChange` 现在也把失败说清楚：有 error 就记 Warning 并把消息带出来。
- **ViewModels**：`DesktopModeRow.cs`、`DesktopDockEdgeRow.cs` 删除；`DynamicWallpaperViewModel` 少 484 行；`SettingsViewModel` 增 `IsMuralisMode` / `CanChangeDockVisibility`，模式选项变成两项。
- **字符串**：两个 resx 各**删 46 条、加 6 条** `<data>`（新增的是 `Settings_Section_MuralisMode`、`Settings_Section_MuralisMode_Description`、`Settings_Dock_Required`、`Settings_DesktopExperience_Muralis`、`Settings_DesktopExperience_Muralis_Description`、`Settings_DesktopExperience_StateUnavailable`）。`Dynamic_Desktop_*` 只剩 5 条（`Refresh`、`Refresh_Hint`、`Status_ItemsAdded`、`Status_NothingNew`、`Status_RefreshFailed`），全部属于保留下来的「桌面项目」卡片。

**Harness**

- `tools/p3d-takeover-verify.ps1` **退役并删除**：它验的 88 + 62 项检查属于已经撤下产品路径的三态接管。
- **保留**：`tools/p3-common.ps1`（共享基建）、`tools/p3d-shell-api-probe.ps1`、`tools/p3d-shell-interop.ps1`（直接问 shell view 的探针——本次验收仍然用它读 Explorer 自己的图标状态）。
- `tools/p4c-pinned-verify.ps1` 的 `clean` 阶段改为种 `Mode: 'Muralis'` 与 `SchemaVersion = 3`。

### 23.4 测试与提交

新增 / 改动的测试：

| 测试文件 | 内容 |
| --- | --- |
| `Architecture/DesktopExperienceConsolidationTests.cs`（新，4 项） | 壁纸页不再是桌面设置页（7 个 token 在 XAML 与 VM 里都不存在，而 `Dynamic_Section_Canvas_Items` 仍在）；托盘只有一条回原生桌面的路；Dock 设置住在 Muralis Mode 分组里（`Settings_Section_Dock` 不得回来）；撤下的措辞已从字符串里消失 |
| `Models/DesktopExperienceModeTests.cs`（新，6 项） | 转换器的读表逐条：`CleanDesktop` → Muralis、`FullTakeoverExperimental` → Native、其它值 → Native；写出只有 `"Muralis"` / `"Native"`；以及"历史上写过的每一种形状都读得回来" |
| `Services/DesktopExperienceServiceTests.cs`（重写，+123/−35） | 两模式的状态机、失败落回 Native、`RestoreAsync` 的欠归还分支、`OnPhase3Changed` 只发布不落盘 |
| `Architecture/Phase4AArchitectureTests.cs` | 守卫从 `Phase3Takeover_RemainsPresentAsCompatibilityLayer` 改名为 `Phase3Takeover_RemainsPresentButIsNoLongerAProductMode`：冻结层的文件仍在，但公开枚举体里只有 `Native` 与 `Muralis` |
| `Services/SettingsServiceTests.cs` | 往返测试改用 `Muralis` |

同一份 HEAD 上复跑：**Core 602 + Desktop 165 = 767 通过，0 失败**；Debug 与 Release 双配置 **0 警告 0 错误**（Release 复跑输出：`0 个警告` / `0 个错误`，602 + 165）。

提交按"底层 / 界面 / 测试 / 记录"切：

| 提交 | 内容 |
| --- | --- |
| `a316883` `refactor: finish the two-mode desktop experience model` | Core：枚举与转换器、状态改名、schema 3、转换器顺序修正；退役的 `tools/p3d-takeover-verify.ps1` 随之删除（它的删除在上一轮就已 staged，一并落在这里） |
| `99b093d` `refactor: consolidate the desktop surfaces onto Muralis Mode` | App：设置页分组、壁纸页删两段、托盘并命令、删两个 Row VM、两个 resx |
| `579d08e` `test: lock the two-mode product model and fix the perf harness` | 两个新测试文件、服务测试重写、架构守卫改名、`p4c` 的 `clean` 阶段改种 `Muralis` 与 schema 3 |
| `docs: record the desktop experience consolidation` | 本节 + CHANGELOG（就是包含本表的那次提交，哈希见 `git log -1`） |

### 23.5 验收（运行时：两种模式走一圈）

验收落在"用户声称的每一件事都能在机器上读出来"，共四个互相独立的来源：UIA 树、`settings.json`、**Explorer 自己的 `IFolderView2` 图标 flags**、**真的把原生托盘菜单弹出来读**。

`%TEMP%\muralis\p5a_consolidation_ui.ps1 -Stage full`（临时脚本，不入库）**32 项检查，0 失败**：

| 阶段 | 项数 | 断言要点 |
| --- | --- | --- |
| probe | 4 | 进任何模式之前先读 Explorer 图标状态并留作基线；无残留 marker；没有已在跑的 Muralis |
| page | 5 | 7 个撤下的控件一个都不在（`DesktopModeSelector`、`DesktopModeState`、`CanvasDockEnabled`、`CanvasDockAutoHide`、`CanvasDockEdge`、`RestoreWindowsDesktop`、`DesktopModeRow`）；「你的桌面」「边缘 Dock」两段标题在 UIA 树里出现 **0 次**；「画布上的项目」卡片仍在且标题未变 |
| settings | 5 | 模式下拉在、**恰好两项**；Muralis Mode 分组在；Dock 开关在分组里；Native 下 `Settings_Dock_Required` 提示不在屏幕上 |
| modes | 9 | 切 Muralis → 文件落 `Muralis`、**Explorer 自己的 `FWF_NOICONS` 真的为真**、dock 窗口真的在、Dock 开关被锁、必开提示可见；切回 Native → 文件落 `Native`、`FWF_NOICONS` 回到假、Dock 开关交还给用户、无 marker 残留 |
| tray | 5 | 托盘宿主窗口在、菜单能开、**「还原 Windows 桌面」恰好 1 项**、5 项全部来自本应用的资源、菜单恰好等于某一语言的完整命令集 |
| clean | 4 | 关窗口真的退出（exit code 0）、无进程残留、无 marker 残留、**Explorer 图标与进入前逐位相同** |

回归闸门：`tools/p4c-pinned-verify.ps1 -Stage full` **69 项，0 失败**——dock、pins、Shelf、Clean Desktop 一条都没被这次收敛碰到。

**一处值得记下的方法**：「真的藏了图标」那条用 `IFolderView2` 问 shell 自己，而不是问应用——因为本次要证明的正是"用户看到的桌面真的变了"，应用自己的状态对象在这件事上没有资格作证。

### 23.6 已知偏差 / 诚实记录

1. **Dock 开关在 Native 下没有置灰，分组也没有收起。** spec 给的选项是"整组收起（或置灰）"，实际实现只做了：Muralis Mode 下锁住 Dock 开关并显示必开说明，Native 下开关可用。理由：§21 已经把 dock 做成**独立体验层**——Pinned Apps 不依赖 Clean Desktop，原生桌面下 dock 同样可用；在 Native 下把开关拿走会与 §21 的结论矛盾。整组收起没有做。
2. **「桌面项目」卡片现在编辑的是一份不再被挂载的画布。** 模式选择器删除之后，没有任何产品路径再调用 `IDesktopCanvasService.EnableAsync`，而桌面内容按 §15 / §16 由 Desktop Shelf 呈现。卡片按用户决定保留（只删前两段）。要处理它必须动 `DesktopModeService` / canvas，属本轮明令禁止，因此记录在案、不绕开、也不假装它不存在。
3. **`RestoreAsync` 仍然读一次 Phase 3 状态。** 因为"启动时欠着 Explorer 的图标"只可能由 Phase 3 的 marker 表达。产品不进入那条路，只是把它还清；这条读是 §20 的兼容承诺，不是产品模式。
4. **退役 harness 的托盘互操作搬进了临时验收脚本。** `P3Tray` 那段 Add-Type 只存在于被删的 `tools/p3d-takeover-verify.ps1` 里，而"托盘只剩一条还原命令"这条断言必须真的把原生菜单弹出来读，所以临时脚本自带这一段（在 `%TEMP%` 下，不入库）。
5. **本轮没做**：Nexus Motion Engine（Phase 4D）、Wallpaper Runtime；**Release 配置的 UI 巡检**（验收跑在 Debug 上，与 §21 / §22 口径一致）；高刷 / 多屏 / 高 DPI 下的模式切换。

**Phase 4 状态**：4A / 4B / 4C / 4C.1 与本节（模式收敛）全部落地。产品对用户只承认两种桌面关系，第三、第四种只剩"旧文件读得进来"这一件事。Phase 4D 的前置条件——一个稳定的模式概念——已经满足；按惯例**等待人工验收，不自行进入下一阶段**。

