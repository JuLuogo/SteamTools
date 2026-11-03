# AGENTS.md — Watt Toolkit（Steam++）仓库 AI 协作约束

> **适用范围**：本仓库内的所有代码改动（`src/`、`build/`、`packaging/`、`doc/`、`.github/`）。
> **读者**：在本仓库工作的 AI Agent（DSH / Claude Code / Codex / Copilot / Cursor 等）与人类维护者。
> **事实基线**：分支 `develop`、`.NET SDK 11.0`、Avalonia `12.1.4`、ReactiveUI `23.2.28`。
> 本文件只写**能改变你决策**的内容。通用常识（「写好注释」「保持整洁」）不在此列；带 ⚠️ 的是本仓库特有的陷阱。
> 维护方式：改动构建系统、代码约定、程序入口时同步更新本文件，否则本文件会比 README 更容易骗人。

---

## 0. 30 秒速览

| 问题 | 答案 |
| --- | --- |
| 这是什么 | Watt Toolkit（原名 Steam++，更早叫 SteamTools）：跨平台 Steam 周边工具箱，本体是 C# 桌面应用 + 插件 |
| 语言 | **C#（唯一主体）**：`src/` 下 972 个 `.cs`；UI 用 **Avalonia XAML**（127 个 `.axaml`，**0 个 WPF `.xaml`**）；**本仓库没有任何 C++ 工程** |
| 解决方案 | `WattToolkit.slnx`（注意是 **slnx**；旧文档里的 `SteamToolsV2+.sln` 已不存在） |
| 目标框架 | `net11.0`；Windows 桌面 `net11.0-windows10.0.19041.0`（运行时最低 Win10 17763），macOS `net11.0-macos`。TFM 写在各 `src/TFM_NETX_*.props`，**不写在 csproj**（唯一例外：`BD.WTTS.Client.AppHost.csproj` 直接写，因为它不 Import TFM props） |
| 主程序 | `src/BD.WTTS.Client.Avalonia.App`（程序集名 **Steam++**）。发布出来的 `Steam++.exe` 其实是 net35 的 AppHost 启动器，真正逻辑在 `assemblies/Steam++.dll` |
| 怎么构建 | `dotnet build src/BD.WTTS.Client.Avalonia.App/BD.WTTS.Client.Avalonia.App.csproj -c Debug -f net11.0-windows10.0.19041.0` |
| 怎么测 | `dotnet test src/BD.WTTS.UnitTest/BD.WTTS.UnitTest.csproj -c Debug`（NUnit） |
| 怎么打包 | `src/BD.WTTS.Client.Tools.Publish`（可执行名 **`pub`**）；`packaging/` 下的脚本是 v2/v1 遗留（勿用），CI 打包走 `.github/workflows/publish.yml` |
| 最大坑 | ① `ref/` 下 17 个 git 子模块**只读**；② 52% 的 `.cs` 不写 `using`（全局 using 体系）；③ 所有 `BD.WTTS.*` 项目 `RootNamespace=BD.WTTS`，namespace 与目录**刻意不对齐**；④ 服务按 `AppServicesLevel` 分级注册，级别错了会让轻量进程（反代子进程等）启动失败 |

---

## 1. 项目定位

- 一款 **Steam 周边工具箱**：网络加速（本地反代 + hosts 修改）、账号切换、库存/成就管理、本地令牌（2FA）、自动挂卡（ASF）、游戏工具（无边框窗口化）等。
- 架构核心是**插件化**：默认功能全部是插件（`src/BD.WTTS.Client.Plugins.*`），可单独删除/禁用；插件通过 MEF（`System.Composition`）在运行时被发现。
- 本项目是 [BeyondDimension/SteamTools](https://github.com/BeyondDimension/SteamTools) 的 fork（`origin` = 个人 fork，`upstream` = 官方）。**上游主干是 develop；不要随意把 develop 合到 main 或反向变基。**
- 上游会周期性整体推进 `ref/` 子模块指针，提交信息形如 `⬆️ Update ref submodules`。

## 2. 技术栈（改代码前必须知道的事实）

| 领域 | 选型 | 落地位置 |
| --- | --- | --- |
| 运行时 / 语言 | .NET 11（`LangVersion=latest`、`Nullable=enable`、`ImplicitUsings=enable`） | `global.json`、`src/Directory.Build.props` |
| UI | Avalonia 12.1.4 + FluentAvalonia 3（公开类型带 `FA*` 前缀，本仓库用别名映射回旧名） | `src/BD.WTTS.Client.Avalonia`、`src/FluentAvaloniaAliases.cs` |
| MVVM | **ReactiveUI 23 + ReactiveUI.Fody**（`[Reactive]`、`ReactiveCommand`、`WhenActivated`） | `src/BD.WTTS.Client/UI/ViewModels` |
| 插件 | MEF（`System.Composition`）+ `PluginBase<T>` + `IPlugin` | `src/BD.WTTS.Client/Plugins`、各插件 `Plugins/Plugin.cs` |
| 进程模型 | 主进程 + **加速器反代子进程** `Steam++.Accelerator` + IPC；应用更新在**主进程内**完成（`IAppUpdateService`，退出后由 `Upgrade.cmd` 替换文件）。`Plugins.Update` 只有 11 行占位、**不参与发布** | `src/BD.WTTS.Client.IPC`、`ref/dotnetCampus.Ipc` |
| HTTP / 反代 | **YARP.ReverseProxy + 自写 Kestrel 中间件** + 自签根证书（`CN=SteamTools Certificate`）；Titanium-Web-Proxy 只存在于 `ref/` 子模块，**没有参与编译** | `src/BD.WTTS.Client.Plugins.Accelerator*` |
| 数据 | SQLite（`sqlite-net-pcl` + `BD.Common.Repositories`）、JSON 设置（`BD.Common.Settings.V4`） | `src/BD.WTTS.Client/Repositories`、`Settings` |
| 日志 | **NLog 静态门面 `Log`**（`const string TAG` + `Log.Error(TAG, ex, "msg")`），不是以 `ILogger<T>` 为主 | `src/BD.WTTS.Client/Logging` |
| 序列化 | `System.Text.Json`（别名 `SJsonSerializer`）+ MessagePack | `src/ImplicitUsings.JsonProperty.cs` |
| 测试 | NUnit 4.6.1 + Moq + NUnit3TestAdapter | `src/BD.WTTS.UnitTest` |
| 本地化 | `.resx`（9 个语言 + 自动生成的 `Strings.Designer.cs`），Crowdin 同步 | `src/BD.WTTS.Client/Resources/`、`crowdin.yml` |

### 2.1 进程与启动链（理解它才能改对启动相关代码）

```text
Steam++.exe                  net35 的 C# AppHost（src/BD.WTTS.Client.AppHost，程序集名 Steam++）
  │  检查本机 .NET 11 运行时，用 hostfxr 在【同一进程内】启动 CLR
  └─> assemblies/Steam++.dll   BD.WTTS.Program.CustomEntryPoint
        └─ Startup（src/BD.WTTS.Client/Startup/Startup.*.cs，14 个 partial 文件）
             ├─ 按 AppServicesLevel 决定注册哪些服务（Min / UI / Steam / Hosts / HttpProxy / IPCRoot …）
             ├─ PluginsCore.InitPlugins()  发现并加载 modules/<Plugin>/*.dll
             ├─ 单实例管道（IPC）
             └─ Avalonia App（src/BD.WTTS.Client.Avalonia.App）
```

- **不要在 `Startup` 里无条件 `AddSingleton` 业务服务**：服务按 `AppServicesLevel` 分层注册（`Startup.Host.cs`），轻量进程只注册 `Min` 级别，注册多了会让它们启动失败或显著变慢。
- 需要「仅主进程」的逻辑用 `IsMainProcess` 判断，不要用 `Environment.ProcessPath` 猜。
- **单实例**：`Startup.SingleInstancePipeline` 用命名管道（名字含 `InformationalVersion` + 进程路径的 Crc32）；非首实例通过管道发 `show`/`shutdown`/`proxy` 指令后退出。新增启动参数要走这条管道，不要另起一套互斥。
- **提权**：两份 `app.manifest` 都是 `requireAdministrator`，进程本身就是管理员；需要「额外一次管理员动作」时靠 `-clt sudo` 拉起管理员 IPC 子进程（`module=Administrator`）或 `runas`，不是重启整个应用。

---

## 3. 仓库地图

```text
WattToolkit.slnx            # 解决方案入口（slnx 格式）
Directory.Build.targets     # ⚠️ 在仓库根，不在 src/：Windows 裁剪 -macos TFM、注入 FluentAvaloniaAliases、替换 SteamClient 的 BD.Common
global.json / NuGet.Config  # SDK 11.0.0（rollForward latestMajor）+ avalonia-nightly feed
src/
  BD.WTTS.Client/              # 核心：服务、仓储、设置、ViewModels、插件抽象（TFM: WITH_ALL）
  BD.WTTS.Client.Avalonia/     # Avalonia 视图层：Views、Styles、Converters、Assets（TFM: WITH_DESKTOP）
  BD.WTTS.Client.Avalonia.App/ # 主程序入口（AssemblyName=Steam++）
  BD.WTTS.Client.AppHost/      # net35 启动器（Steam++.exe）
  BD.WTTS.Client.AppHost.Bridge/  # MSIX/Desktop Bridge 版宿主（Steam++.csproj，net472）
  BD.WTTS.Client.IPC/          # 进程间通信契约
  BD.WTTS.Client.Plugins.*/    # 7 个功能插件 + ReverseProxy 子进程 + Update（Update 不参与发布）
  BD.WTTS.Client.Tools.*/      # 发布工具 pub、HostsTest、开源库清单
  BD.WTTS.Generators/          # Roslyn 源生成器（netstandard2.0，疑似已不参与编译）
  BD.WTTS.UnitTest/            # NUnit 测试
  Avalonia.*/                  # 内嵌 Avalonia fork 壳工程（友元/手动裁剪用，双 TFM net10.0;net11.0）
  ImplicitUsings.*.cs          # 全局 using 库（被各 csproj 逐项 Compile Include）
  AssemblyInfo.cs / .Constants.cs / .Xaml.cs / Utils.cs / FluentAvaloniaAliases.cs
ref/                        # ⚠️ 17 个 git 子模块，只读上游源码（BD.Common、SteamClient、Avalonia8、WinAuth…）
res/                        # 图标、品牌图、截图
build/                      # Linux 用户侧运行脚本（随产物分发）+ 过时的 macOS/AOT 脚本
packaging/                  # ⚠️ 全部是 v2/v1 遗留，已失效
doc/                        # file-system.md、publish.md、program-file-structure/（Windows.md 是空文件）
.github/workflows/          # dotnet.yml = 构建+测试（Win/Linux）、macos.yml = macOS（手动/定时，需 Xcode 27）、publish.yml = 未签名预览包
```

---

## 4. 构建

### 4.1 前置

1. **.NET SDK 11.0**（`global.json` 要求 `11.0.0`，`rollForward: latestMajor`、`allowPrerelease: true`）。若 `dotnet --version` 报「A compatible .NET SDK was not found」，先装 SDK，**不要改 `global.json` 迁就本机**。
2. 子模块必须已检出：`git submodule status` 不应出现 `-` 前缀。**除 `git submodule update --init` 外不要做任何子模块写操作**（见 §14）。
3. 还原需要访问 `https://pkgs.dev.azure.com/AvaloniaUI/...`（`NuGet.Config` 里的 avalonia-nightly feed）。

### 4.2 推荐命令（由快到慢，够用于绝大多数改动）

```powershell
# ① 核心库（不含 UI，能暴露绝大多数编译错误）
dotnet build src/BD.WTTS.Client/BD.WTTS.Client.csproj -c Debug -f net11.0 --nologo

# ② 主程序，只构建 Windows 桌面 TFM（日常内循环，强烈推荐）
dotnet build src/BD.WTTS.Client.Avalonia.App/BD.WTTS.Client.Avalonia.App.csproj -c Debug -f net11.0-windows10.0.19041.0 --nologo

# ③ 单元测试
dotnet test src/BD.WTTS.UnitTest/BD.WTTS.UnitTest.csproj -c Debug

# ④ 全解决方案（慢，改跨项目公共代码时才用）
dotnet build WattToolkit.slnx -c Debug
```

⚠️ 不要在 Windows 上直接构建「多桌面 TFM」目标：`src/TFM_NETX_WITH_DESKTOP.props` 会同时列出 `net11.0-macos`，而 macOS workload 通常没装。根 `Directory.Build.targets` 会自动剔掉 `-macos`，前提是**没有**设 `BuildAllTargetFrameworks=true`；要显式收窄用 `-p:WttsDesktopTargetFrameworks=net11.0-windows10.0.19041.0`。

### 4.3 构建期真正会失败的规则（其余规范靠自觉）

`src/.editorconfig` 把 `IDE0055`、`IDE1006`、`SA1137`、`CS8618`、`CS8604`、`CA1829` 设为 error，**但 `src/` 下既没有 `EnforceCodeStyleInBuild` 也没有 StyleCop 包**，所以：

| 规则 | `dotnet build` 真的会报错吗 | 结论 |
| --- | --- | --- |
| `CS8618` / `CS8604`（可空性） | ✅ 会（编译器诊断） | 可空性必须写对；`null!` 只在确知非空且无法避免时用 |
| `CA1829`（用 `Count` 属性代替 `.Count()`） | ✅ 会 | 用 `.Length` / `.Count` |
| `IDE0055`（格式）/ `IDE1006`（命名）/ `SA*`（StyleCop） | ❌ 不会 | 但**仍要遵守**，这是既有风格；不要因为「编译过了」就提交格式不一致的代码 |

### 4.4 构建相关文件的职责

| 文件 | 作用 |
| --- | --- |
| `global.json` | 固定 SDK 11.0.0 |
| `NuGet.Config` | 只额外加了 avalonia-nightly 源（其余走 nuget.org 默认源） |
| `src/Directory.Build.props` | `DotNet_Version=11.0`、`Nullable`/`ImplicitUsings`、`IsTrimmable`、CPM 开关；**关闭全部 `GenerateAssembly*Attribute`**（程序集元数据只能在 `src/AssemblyInfo.cs`）；链接 `AssemblyInfo.cs` + `ImplicitUsings.BCL/Common.cs` |
| `Directory.Build.targets`（仓库根） | ① Windows 上剔除 `-macos`/`-maccatalyst` TFM；② 给 `BD.WTTS.Client.Avalonia` 与插件注入 `FluentAvaloniaAliases.cs`（**排除** `Accelerator.ReverseProxy` 与 `Update`）；③ 把 `ref/SteamClient` 的 `BD.Common` 包引用换成 `ref/Common` 源码引用 |
| `src/Directory.Packages.props` | 只有两行有效内容：`Import` 集中版本表 + 覆写 `ReactiveUI.Avalonia=12.0.3` |
| `ref/DirectoryPackages/Directory.Packages.props` | **唯一包版本真源**（1099 行，子模块） |

---

## 5. 运行与调试

- **开发期直接跑主程序**：`dotnet run --project src/BD.WTTS.Client.Avalonia.App`（Windows 下 Debug 配置 `OutputType=Exe`，便于看控制台）。
- **发布后的进程结构**：`Steam++.exe`（net35 AppHost）→ `assemblies/Steam++.dll`；插件在 `modules/<Plugin>/`；原生库在 `native/<rid>/`；自带运行时在 `dotnet/`。
- **需要管理员权限**：两份 `app.manifest` 都是 `requireAdministrator`。调试加速器（hosts/证书/WinDivert）时必须以管理员身份启动 IDE 或终端。
- **用户数据目录**（不要硬编码路径，用 `FileSystem`/`IOPath` 抽象）：
  - Windows **安装版**：`%LocalAppData%/Steam++`；**便携版**（注册表无 InstPath 匹配）用 `<程序目录>/AppData` 与 `<程序目录>/Cache`（日志在对应 Cache 下的 `Logs`；安装版也可为 `%Tmp%/Steam++/Logs`）
  - macOS：`~/Library/Steam++`
  - Linux：`$XDG_DATA_HOME/Steam++`（日志 `$XDG_CACHE_HOME/Steam++/Logs`）
  - 权威表：`doc/file-system.md`
- **Linux 启动链**：`Steam++.sh` 先设 `DOTNET_ROOT=<root>/dotnet`，再 `exec dotnet assemblies/Steam++.dll`（脚本源在 `build/linux/Steam++.sh`）。
- **Debug 辅助**：`Utils.cs` 的 `ProjectUtils.ProjPath` 会向上找 `*.slnx` 定位仓库根，被插件热加载、发布工具、测试用来定位 `res/`；只在 Debug/工具项目里链接。

---

## 6. 打包与发布

**唯一现役入口**：`src/BD.WTTS.Client.Tools.Publish`（`AssemblyName=pub`，System.CommandLine）。

```powershell
dotnet build src/BD.WTTS.Client.Tools.Publish/BD.WTTS.Client.Tools.Publish.csproj -c Release
src/BD.WTTS.Client.Tools.Publish/bin/Release/net11.0/pub.exe run --rids win-x64
```

- ⚠️ `pub run` 会**先删除再重建** `src/BD.WTTS.Client.Avalonia.App/bin/Release/Publish/`，不要在有未保存产物时执行。
- `run` 的步骤：逐 RID `dotnet publish` → 发布 net35 AppHost → 发布 7 个插件到 `modules/` → 复制 .NET 运行时到 `dotnet/` → 签名 → 出 `.msix`/`.msixbundle` → 压缩 `.7z`（Win/mac）或 `.tar.gz`（Linux）→ 写 `<root>.json` 清单。
- 其它子命令：`install --deb/--rpm`、`nsis`（依赖仓库外的 `NSIS-Build`）、`compressed --7z/--zip/--gz/--zstd`、`scan`、`launch`、`hash`、`csproj`、`bs`、`gcert`、`macos-universal-binary`（未实现完整）。
- **当前不产出**：`.msi`、`.pkg`（代码被注释）、`.dmg`、`AppImage`。`doc/publish.md` 的产物清单是早期计划，**以代码为准**。
- **Linux 打包前置**：需要仓库外的 `src/dotnet-Runtime/linux-<arch>`（本仓库不含），否则复制运行时会失败。

### 6.1 ⚠️ 不要相信的「构建 / CI / 打包」文件（照抄必失败）

| 文件 | 为什么不能用 |
| --- | --- |
| ~~`.github/workflows/dotnet.yml`、`publish.yml`、`xamarin.yml`~~ | **2026-11-03 已重写/清理**：`dotnet.yml` = Windows+Linux 构建与测试（macOS 为 advisory）、`publish.yml` = 未签名预览包；`xamarin.yml`/`sync-gitee.yml`/`CI.yml.bak` 已删除。历史版本曾整体引用已删除的 `SteamToolsV2+.sln`、`tests/*`、`src/ST.Client.Desktop.Avalonia.App` |
| `packaging/build.ps1`、`packaging/build.cmd`、`packaging/build.v1.ps1` | v2/v1 遗留，路径与 TFM 全过期 |
| `build/macos/build.sh` | 个人机脚本，硬编码本机路径 + `net8.0-macos` |
| `build/publish_apphost_linux_x64.sh` | `net7.0` + NativeAOT 实验 |
| `src/README.md` 正文 | 第 5–110 行整体被 `<!-- TODO -->` 注释，是 v2 结构；只有「命名空间/文件夹」「DI 扩展类」两节仍有效 |
| `doc/program-file-structure/Windows.md` | **0 字节空文件**；Linux/macOS 两份的模块名也已与 `AssemblyInfo.Constants.cs` 不一致 |
| `packaging/Info.plist` | 版本号停在 2.8.3 |

---

## 7. 代码规范（硬约束）

### 7.1 排版（`src/.editorconfig`，`root=true`，**只管 `src/` 树**）

| 项 | 规则 |
| --- | --- |
| 编码 | UTF-8 |
| C# 缩进 | **4 空格**（不用 tab），`tab_width=4` |
| C# 换行 | **CRLF**；`insert_final_newline=false`（文件末尾**不强制**空行） |
| `.axaml` / `.xaml` / `.csproj` / `.props` / `.xml` / `.resx` | **TAB 缩进**（最容易写错的一条：C# 用空格、XML 类用 tab） |
| `.json` / `.js` | 2 空格 |
| `.sh` | LF + 末尾换行 |
| 大括号 | **Allman**（`csharp_new_line_before_open_brace=all`），`catch`/`else`/`finally` 前换行 |
| 换行位置 | 运算符置于行首（`beginning_of_line`） |
| 单行块 | 保持单行（`csharp_preserve_single_line_blocks/statements=true`） |
| 空格 | 逗号后空、点前后不空、控制流关键字后空、二元运算符两侧空 |

### 7.2 C# 语言约定

- **`var` 无条件使用**（三条 `csharp_style_var_*` 全为 true）。
- **文件作用域命名空间**：`namespace BD.WTTS.Foo;`，不要块级 `{ }`。
- **不写 `this.`**：字段访问一律省略（`dotnet_style_qualification_for_field=false`）。
- **表达式体只用于** 属性 / 访问器 / 索引器 / lambda；**方法、构造函数、运算符不要用 `=>`**。
- **访问修饰符可省略**（`dotnet_style_require_accessibility_modifiers=never`、`IDE0040=none`）；公开 API 建议显式写。
- **单行 `if` 可省略大括号**（`csharp_prefer_braces=false`、`IDE0011=none`）；不要为了风格把已有多行块改成单行。
- **修饰符顺序**：`public private protected internal static extern new virtual abstract sealed override readonly unsafe volatile async`。
- **能 `readonly` 就 `readonly`**；优先模式匹配、`??`、`?.`、索引/范围、`throw` 表达式。
- **`using` 写在 namespace 之外**，且**不排序、不分组、System 不优先**（`dotnet_sort_system_directives_first=false`）。**不要为了「整洁」重排 using**，只会产生无意义 diff。

### 7.3 命名

- 接口：`I` + PascalCase；类型/方法/属性/事件：PascalCase（规则本体是 `suggestion`，但全仓一致）。
- **私有字段：`_` 前缀 + 驼峰**（`_ProgramPath`、`_alreadyTracing`）。
- **日志 TAG 常量**：`const string TAG = "XxxS";`（惯例是类名 + `S`），见 `ReverseProxyServiceImpl.cs`。
- 缩写词按单词处理：`IpcPath`、`HttpProxy`、`SteamId`。
- 页面/窗口命名成对：`XxxPage.axaml` + `XxxPageViewModel`、`XxxWindow.axaml` + `XxxWindowViewModel`。

### 7.4 using 与全局 using（**最容易写错的地方**）

- 全局 using **不是** SDK 的 `<Using>`，而是 **27 个共享源文件 `src/ImplicitUsings.*.cs`**，由各 csproj **逐项 `<Compile Include="../ImplicitUsings.Xxx.cs" />`** 链接。
- **52%（505/972）的 `.cs` 完全没有任何 `using`**。新文件默认**不要写 using**，直接使用即可。
- 只有 3 种情况才手写 using：
  1. **别名**：`using AppResources = BD.WTTS.Client.Resources.Strings;`（48 个文件这样写）、`using SJsonSerializer = System.Text.Json.JsonSerializer;`
  2. 全局未覆盖的命名空间（如 `Avalonia.Platform`、`System.Reactive.Disposables.Fluent`、`BD.WTTS.Helpers`）
  3. 同名类型消歧义
- ❌ **禁止为一个新命名空间去改/新增 `src/ImplicitUsings.*.cs`**：它被 15–20 个 csproj 共享，加一行就全局生效，还要同步改每个 csproj 的 Compile Include。

### 7.5 注释与文档

- **不要求文件头**（`file_header_template=unset`、`SA1633=silent`）；不要新增 license header。
- 公开 API 的 `<summary>` 用中文写；内部实现只在「为什么」不明显时写注释，不写「是什么」。
- 保留既有的 `// ReSharper disable once Xxx` / `#pragma warning disable XXX` 成对结构，**不要顺手删除**。
- 改动过的注释若已过时，必须同步更新（本仓库大量注释是唯一文档）。

---

## 8. 目录与命名空间

- **所有 `BD.WTTS.*` 项目都设 `RootNamespace=BD.WTTS`**，namespace **不含** `Client`/`Plugins` 段。
- **namespace 与文件夹刻意不对齐**（`dotnet_style_namespace_match_folder=false`、`IDE0130=none`）：`src/BD.WTTS.Client/UI/ViewModels/...` 与插件的 `UI/ViewModels/...` **共用 `BD.WTTS.UI.ViewModels`**。新增文件请跟随**同目录已有文件的 namespace**，不要新造 `BD.WTTS.Client.UI...` 之类分支。
- partial 拆文件用**点分后缀**：`Startup.Host.cs`、`Xxx.props.cs`、`Xxx.BaseType.cs`、`Xxx.Plugin.cs`。
- 核心库目录（`src/BD.WTTS.Client`）：`App/ Attributes/ Columns/ Constants/ Diagnostics/ Entities/ Enums/ Extensions/{Platform,Security,ServiceCollection,Steam}/ FileSystem/ Helpers/ Logging/ Mappers/ Migrations/ Models/ Plugins/ Repositories/ Resources/ Services/ Services.Implementation/{Platform/{Windows,Linux,MacCatalyst}}/ Settings/ Startup/ UI/ViewModels/{Abstractions,Items,Pages,Windows}/`。
- 视图层（`src/BD.WTTS.Client.Avalonia`）：`Behaviors/ Converters/ Extensions/ServiceCollection/ Helpers/ Markup/ Services.Implementation/ UI/Assets/{Fonts,Icons,Logos}/ UI/Input/ UI/Styling/{Controls,Theme}/ UI/Views/{Abstractions,Controls,Pages,Windows}/`。
- ❌ **不要往 `src/` 根目录加零散 `.cs`**：根目录文件靠 csproj 的 `Compile Include` 显式链接，没被链接 = 不存在（例：`src/AboutAppInfoPopup.cs` 是 Xamarin 时代残留，**没有任何 csproj 引用**，不要「修复」它）。

---

## 9. MVVM / UI 约定

- **只用 ReactiveUI + ReactiveUI.Fody**。❌ 不要引入 CommunityToolkit.Mvvm、`[ObservableProperty]` 或其它 MVVM 源生成器（全仓 0 命中）。
- 可观察属性三种写法：`[Reactive]`（202 处，需 `FodyWeavers.xml` + `PackageReference ReactiveUI.Fody`）、手写 `RaiseAndSetIfChanged`（52 处）、`ReactiveCommand`（166 处）。**优先 `[Reactive]`**。
- **ViewModel 基类**：`ViewModelBase`（实现 `IActivatableViewModel`、`IDisposable`）；页面用 `PageBase<TViewModel>` / `ReactiveUserControl<T>`，配合 `this.SetViewModel<T>(useCache)`。
- **非持久化成员必须打 5 连忽略特性**：`[IgnoreDataMember, MPIgnore, MP2Ignore, N_JsonIgnore, S_JsonIgnore]`（否则会被序列化进设置/缓存）。
- **生命周期**：初始化放 `WhenActivated` 或 `Activation()`，清理注册进 `CompositeDisposable` / `.DisposeWith(disposables)`；不要 new 了 `CompositeDisposable` 却不 dispose。
- **XAML 编译绑定默认开启**（`AvaloniaUseCompiledBindingsByDefault=true`，Client.Avalonia 与全部插件）：`DataTemplate`/绑定必须能静态推断类型，需要写 `x:DataType`；元素引用用 `#name`，不要用 WPF 的 `ElementName`。
- **XAML 命名空间别名固定**（定义在 `src/AssemblyInfo.Xaml.cs`）：`spp=https://steampp.net/ui`、`s=https://steampp.net/services`、`ms=https://steampp.net/settings`、`ui=using:FluentAvalonia.UI.Controls`。新增映射也要改这个文件。
- **导航**：FluentAvalonia `Frame` + `NavigationService.NavigateToType`，目标页由 `MenuTabItemViewModel.PageType` 指定。⚠️ **本仓库没有 ViewLocator**，只有 `NavigationService.GetViewModelToPageContent` 里的少量硬编码映射 —— 新增页面必须显式登记 `PageType`（或走 `SetViewModel<T>`），不要指望按命名约定自动解析。
- **启动引导**：启动任务在 `MainWindow.axaml.cs` 的 `AppSplashScreen.RunTasksCore` 里按顺序执行，新增「启动时要做的事」应加到这里而不是构造函数。
- **FluentAvalonia 3 的 `FA*` 前缀**：在 Client.Avalonia 与插件项目里**写旧名**（`ContentDialog`、`Frame`、`Symbol`、`TaskDialog`），别名在 `src/FluentAvaloniaAliases.cs`；其它项目没有别名，必须写全名。
- 静态资源（图片/图标/字体）放 `UI/Assets/**`，通过 `<AvaloniaResource Include="UI/Assets/**/*" />` 打包。

---

## 10. 服务、DI、日志、异常、异步

- ⚠️ **本仓库没有 Generic Host**：没有 `IHost`/`IStartup`/`AddXTTS`。启动是 `new ServiceCollection()` 手工注册，再由静态门面 `System.Ioc`（`ref/Common/src/BD.Common/Ioc.cs`）取服务；注册顺序见 `Startup.Host.cs`：配置委托 → `ConfigureDemandServices` → `ConfigureRequiredServices` → 每个插件的 Demand/Required 两段。
- **DI 扩展类一律写在 `namespace Microsoft.Extensions.DependencyInjection;`**，类名 `XxxServiceCollectionExtensions`，方法 `AddXxx(this IServiceCollection ...)`（`src/README.md` 中唯一仍然有效的约定）。
- 接口放 `Services/`，实现放 `Services.Implementation/`（点分目录，不是 `ServicesImplementation`）；平台相关放 `Services.Implementation/Platform/{Windows,Linux,MacCatalyst}`。
- **日志**：`Log.Info(TAG, "msg")` / `Log.Error(TAG, ex, "msg {Arg}", arg)`；每个类顶部 `const string TAG = "XxxS";`。**不要**在新代码里注入 `ILogger<T>`（存量 30 个文件是历史）。
- **异常**：可恢复用 `try/catch` + 日志 + 用户可读提示（`MessageBox` / `IWindowManager`）；全局兜底在 `Startup.GlobalExceptionHandler.cs`，**不要在 UI 线程抛未处理异常**。
- **空值**：用 `x.ThrowIsNull()` 而不是 `x!`（全仓惯例）；确知非空且无法避免时才放行 `null!`。
- **异步**：`ConfigureAwait(false)` **只用于库层/底层**（当前 18 个文件 50 处），**UI 层不要加**（需要回到 UI 线程）。
- 事件订阅、定时器、`ReactiveCommand.ThrownExceptions` 必须处理或显式忽略并记日志。

---

## 11. 插件开发（新增 / 修改插件）

插件 = 独立项目 `src/BD.WTTS.Client.Plugins.<Name>`，结构模板见 `Plugins.GameAccount`：

```text
<Name>/
  Plugins/Plugin.cs              # [CompositionExport(typeof(IPlugin))] : PluginBase<Plugin>, IPlugin
  Enums/ Extensions/ Helpers/ Models/ Services/ Services.Implementation/
  Settings/{Abstractions}/       # ⚠️ 部分 Settings 类是源生成产物，只改 *.BaseType.cs 手写侧
  UI/Assets/ UI/ViewModels/{Pages,Windows}/ UI/Views/{Pages,Windows}/
  Properties/Resources.{resx,Designer.cs}
  FodyWeavers.xml                # <Weavers><ReactiveUI /></Weavers>（用 [Reactive] 时必须）
  BD.WTTS.Client.Plugins.<Name>.csproj
```

csproj 必写：`RootNamespace=BD.WTTS`、`DefineConstants=WTTS_PLUGIN;WTTS_PLUGIN_<NAME>`、`AvaloniaUseCompiledBindingsByDefault=true`、自己的 `ImplicitUsings.*` 列表、`PackageReference ReactiveUI.Fody`、ProjectReference 到 `BD.WTTS.Client` + `BD.WTTS.Client.Avalonia`，并在**末尾** `<Import Project="../TFM_NETX_WITH_DESKTOP.props" />`。

发布形态：`modules/<Name>/BD.WTTS.Client.Plugins.<Name>.dll` —— 发布工具对每个插件**只拷贝单个 dll**，插件项目里新增的非默认资产需要在 `pub` 的复制逻辑里补上。加载由 `PluginsCore` + `AssemblyLoadContext` 完成（被禁用的插件用可卸载 ALC），靠 MEF `[CompositionExport(typeof(IPlugin))]` 发现，**没有插件清单 json**。

`Plugin.cs` 必须实现：`Id`（GUID，登记在 `src/AssemblyInfo.Constants.cs` 的 `XxxId` 常量）、`Name`（`Strings.Xxx`）、`UniqueEnglishName`（= 模块常量名）、`Description`、`Icon`、`GetMenuTabItems()`、`ConfigureDemandServices` / `ConfigureRequiredServices`、`OnCommandRun`。

❌ 不要新增「隐式插件」（把功能直接塞进 Client 而不建插件项目）；❌ 不要动 `Directory.Build.targets` 的别名例外名单；❌ 不要靠改 `PluginsCore` 的加载顺序去「修」某个插件的初始化问题。

---

## 12. 本地化

- 资源文件：`src/BD.WTTS.Client/Resources/Strings.resx`（简体中文，**默认/回退语言**）+ `Strings.{en,es,it,ja,ko,ru,zh-Hant}.resx`；`Strings.Designer.cs` 是**自动生成**的，**不要手改**。
- 新增文案：① 在 `Strings.resx` 加 key（PascalCase，可用 `Xxx_Yyy` 前缀）；② 重新生成 Designer（resx 设计器或构建时生成）；③ 其它语言交给 Crowdin（`crowdin.yml`），**不要手写翻译占位**。
- 代码里用 `Strings.Xxx` 或别名 `AppResources.Xxx`；带参用 `Strings.Xxx.Format(args)`；XAML 里用 `Res` 绑定。
- ⚠️ 删除/重命名 resx key 会破坏 XAML 绑定与其它语言文件，**属于破坏性变更**，必须全仓 grep。

---

## 13. 依赖与包管理

- **集中包管理（CPM）已开启**：`PackageReference` **一律不写 `Version`**。版本只能加在 `ref/DirectoryPackages/Directory.Packages.props`（子模块），或在 `src/Directory.Packages.props` 用 `Update` 覆写（目前只有 `ReactiveUI.Avalonia` 一例）。
- **不要给 `BD.Common` / `BD.SteamClient` 加 `PackageReference`**：它们已改为 `ref/Common`、`ref/SteamClient` 的**源码 ProjectReference**（`src/BD.WTTS.Client.csproj` 里对应的包引用是刻意注释掉的）。
- 新增第三方库前先确认：① 是否已有同名能力（Avalonia/Common 里通常有）；② 是否支持 net11.0；③ 是否会与 trim/AOT 冲突（`IsTrimmable=true`）。
- **不要引入第二套** MVVM / 日志 / 序列化 / HTTP 库。

---

## 14. 子模块（`ref/`）—— 只读

- `ref/` 下 17 个 git 子模块（递归 23 个唯一仓库）是**上游源码**，被 `src/` 里 **76 处 `../../ref/` 的 `ProjectReference`** 直接编译；`src/Directory.Packages.props` 甚至 import 了 `ref/DirectoryPackages/Directory.Packages.props` 作为全仓版本真源。
- ❌ **禁止**：直接修改 `ref/` 下任何文件、执行 `git submodule update`（会把指针/工作区弄脏）、改 `.gitmodules`（除非任务明确要求迁移子模块）、把子模块的代码风格搬进 `src/`。
- ✅ 改动子模块的正确流程：在**子模块自己的上游仓库**改 → 推送 → 在主仓库更新指针（上游惯例是一次提交推多个指针，提交信息 `⬆️ Update ref submodules`）。
- ⚠️ 8 个子模块的 pinned commit **只存在于非默认分支**（`net11` / `detached` / `fixs` / `feature/support-any-v5.0.6-BD`）。任何 fork / 镜像 / 换远端操作都必须保留全部分支，否则检出会失败。
- ✅ **已镜像到自有组织 `stea-peroe`**：顶层 `.gitmodules` 的 17 条 URL 全部指向 `https://github.com/stea-peroe/`；递归 27 处 gitlink 对应的 27 组 (仓库, commit) 已逐一校验可达。清单、校验方式、嵌套处理与上游同步流程见 [doc/submodule-mirror.md](doc/submodule-mirror.md)。
- ⚠️ **6 个仓库只由嵌套 `.gitmodules` 声明**（ValveKeyValue、WTTS.Public、SteamKit、Protobufs、ASF-ui、ASF wiki），其 URL 写死在父级子模块的 pinned commit 里，`--recursive` 检出时仍走上游；要全走镜像，用 `git config url."https://github.com/stea-peroe/".insteadOf ...` 零提交重定向，**不要**逐个改写嵌套 `.gitmodules`（会把父级 gitlink 逐层带偏，产生十几个上游无法合并的提交）。
- 上游推进指针（`⬆️ Update ref submodules`）后，新 commit 若尚未同步到镜像，`git submodule update` 会失败；同步做法见 [doc/submodule-mirror.md](doc/submodule-mirror.md) §8。

---

## 15. 生成代码（只读，不要手改）

| 路径 / 文件 | 生成者 |
| --- | --- |
| 所有 `*.Designer.cs`（如 `Resources/Strings.Designer.cs`） | resx 设计器 / `ResXFileCodeGenerator` |
| `src/BD.WTTS.Client/Settings/**/*.cs` 与插件 `Settings/**`（文件头写明「此代码由 BD.Common.Settings.V4.SourceGenerator.Tools 源生成」） | 源生成器，输入是 `build/settings_v4_app.json`；**要加设置项就改同目录的 `*.BaseType.cs`** 手写侧 |
| `src/BD.WTTS.Client/Services.Implementation/Platform/Windows/Interop/Structures/Generated/**` | 文件头写明「This file was generated, please do not edit it directly.」 |
| `obj/`、`bin/` | 构建产物，已被 `.gitignore` 忽略 |

---

## 16. 测试

- 唯一测试项目：`src/BD.WTTS.UnitTest`（NUnit 4.6.1 + Moq；`DefineConstants=UNIT_TEST`，从而跳过全部程序集特性）。
- **组织方式特殊**：除了 ProjectReference，它还**直接 `Compile Include` 生产源码**（如 `CertGenerator.cs`、`Utils.cs`）。要测 internal/未被引用的类，照此把文件链接进来，而不是改动生产代码可见性。
- 命名：`public sealed class XxxTest`，`namespace BD.WTTS.UnitTest;`；断言用 constraint 模型 `Assert.That(x, Is.Not.Null, "msg")`，多断言用 `Assert.Multiple`。
- 命令：`dotnet test src/BD.WTTS.UnitTest/BD.WTTS.UnitTest.csproj -c Debug`。
- ❌ CI 里的 `tests/ST.Client.UnitTest/...` 路径**已不存在**，不要照抄。
- 新增行为优先补测试；纯 UI 布局改动可不加，但要在说明里写手动验证步骤。

---

## 17. 提交与 PR

- **提交信息格式**（`.github/copilot-instructions.md`，近 300 条历史中 89% 遵守）：`<emoji><空格><一行简短中文摘要>`，可带 issue 号。
  - ✅ `🐛 修复加速器端口绑定失败时未提示的问题 #4151`
  - ✅ `⬆️ .NET 11 RC3`
  - ✅ `🌐 补充本地化的错误提示资源`
  - ❌ `🪲Attempt by method 'CompiledAvaloniaXaml...' failed.`（emoji 后缺空格、摘要是异常原文 —— 当前 HEAD 就是这个反面样例）
  - ❌ `update`、`fix`、`修改` 这类无信息摘要
- 常用 emoji：🐛/🪲 修 bug、✨ 新功能、🔧 配置、🛠️ 工具链/构建、🎨 格式/重构、💄 UI、⬆️ 依赖升级、🔖 版本、🚧 进行中、♻️ 重构、🌐 本地化、🔥 删除、🧹 清理。
- **一条提交只做一件事**；不要在同一提交里混入「顺手格式化」（本仓库 using 不排序，格式化收益为负）。
- **不要提交**：`bin/`、`obj/`、`*.user`、本地 `.pfx`/`.snk`、任何 token / AppSecret / 证书口令。仓库里已有的 `*.snk` 是上游公开的签名密钥，不要替换。
- 不要 `git push --force` 到 `develop`；不要重写上游已有提交。
- PR 描述写清：改了什么、为什么、如何验证（命令 + 手动步骤）、是否影响 `ref/` 指针。

---

## 18. Agent 工作流（强约束）

1. **先读再改**：改任何文件前，先 grep/read 同目录相邻文件，确认既有的 namespace、基类、日志、DI、特性组合。
2. **最小改动**：只改与任务直接相关的文件。不做「顺手重构」「统一风格」「补全缺失注释」。
3. **不要跨子模块改**：需要子模块行为变化时停下来向用户说明（要改上游仓库并更新指针）。
4. **不要跑重型命令**：`dotnet build WattToolkit.slnx`、`pub run`、`git submodule update`、全量测试只在任务确实需要且用户同意时执行；默认用 §4.2 的单项目构建验证。
5. **验证要具体**：报告「已验证」时必须给出实际执行的命令与结果；未执行的写「未验证」。**不要声称跑过你其实没跑的命令。**
6. **不要在仓库里留临时文件**：脚本、日志、审查报告放仓库外或明确告知用户；不要新增未被引用的 `.md`/`.cs`。
7. **不要改版本号/程序集元数据**（`src/AssemblyInfo.Constants.cs`），除非任务就是发版。发版时还要同步 `src/BD.WTTS.Client.AppHost/Program.cs` 顶部硬编码的 `dotnet_version_*`（当前 11.0.0-rc.3.26522.148）。
8. **不确定就问**：涉及删除文件、改 `.gitmodules`、改 CI、改签名/发布流程、跨 5 个以上文件的重构时，先向用户确认。
9. **回答用中文**（与仓库文档一致），代码标识符保持英文。

---

## 19. 提交前自检清单

- [ ] `dotnet build` 针对**改动所在项目**通过（不是整个 slnx）
- [ ] 新增/改动文件符合 §7（C# 4 空格、XML 类 tab、Allman、file-scoped namespace、无 `this.`）
- [ ] 新文件没有多余 `using`（全局 using 已覆盖）
- [ ] 非持久化成员补齐了 5 连忽略特性
- [ ] 没有改 `ref/`、`*.Designer.cs`、`Settings/*` 生成侧、`Interop/**/Generated/`
- [ ] `PackageReference` 没写 `Version`
- [ ] 新增文案已进 `Strings.resx`，没有硬编码中文字符串
- [ ] 没有新增 C++ 工程、没有引入第二套 MVVM/日志库
- [ ] `git status` 里没有 `bin/`、`obj/`、临时报告、token
- [ ] 提交信息符合 `<emoji> <一行摘要>`

---

## 附录 A：常用命令速查

```powershell
# 环境
dotnet --version                # 需要 11.x
git submodule status            # 确认没有 - 前缀

# 构建（按项目、按单 TFM）
dotnet build src/BD.WTTS.Client/BD.WTTS.Client.csproj -c Debug -f net11.0
dotnet build src/BD.WTTS.Client.Avalonia.App/BD.WTTS.Client.Avalonia.App.csproj -c Debug -f net11.0-windows10.0.19041.0

# 运行 / 测试
dotnet run --project src/BD.WTTS.Client.Avalonia.App
dotnet test src/BD.WTTS.UnitTest/BD.WTTS.UnitTest.csproj -c Debug

# 发布（会删除并重建 bin/Release/Publish）
dotnet build src/BD.WTTS.Client.Tools.Publish/BD.WTTS.Client.Tools.Publish.csproj -c Release
src/BD.WTTS.Client.Tools.Publish/bin/Release/net11.0/pub.exe run --rids win-x64
```

## 附录 B：关键文件索引

| 想了解 | 看这里 |
| --- | --- |
| 版本号 / 产品名 / 模块 GUID | `src/AssemblyInfo.Constants.cs` |
| 程序集元数据 | `src/AssemblyInfo.cs` |
| XAML 命名空间映射 | `src/AssemblyInfo.Xaml.cs` |
| 启动流水线 | `src/BD.WTTS.Client/Startup/Startup.{cs,Host.cs,OnStartup.cs,SingleInstancePipeline.cs}` |
| 服务级别 | `src/BD.WTTS.Client/Enums/AppServicesLevel.cs` |
| 插件加载 | `src/BD.WTTS.Client/Plugins/PluginsCore.cs` |
| 插件契约 | `src/BD.WTTS.Client/Plugins/Abstractions/IPlugin.cs`、`PluginBase.cs` |
| 用户数据目录 | `doc/file-system.md` + `src/BD.WTTS.Client/FileSystem/` |
| 子模块镜像 / 上游同步 | `doc/submodule-mirror.md` |
| 加速器原理 / 域名清单 | `doc/accelerator.md` |
| 产物目录结构 | `doc/program-file-structure/{Linux,macOS}.md`（Windows.md 为空，看 `pub` 源码） |
| 发布编排 | `src/BD.WTTS.Client.Tools.Publish/Commands/**` |
| 集中包版本 | `ref/DirectoryPackages/Directory.Packages.props` |
| 代码风格 | `src/.editorconfig` |

---

*本文件由仓库审查生成（审查基线为 `76b0a5fc2`，子模块迁移提交后依然适用）。若发现与代码不符，以代码为准并修正本文件。*

