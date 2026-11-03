# 子模块镜像说明（GitHub 组织 `stea-peroe`）

> 本文记录 `ref/` 下 git 子模块迁移到自有组织 `stea-peroe` 的结果、验证方式与后续维护流程。
> 迁移时间：2026-11-03；主仓分支 `develop`，HEAD `76b0a5fc2`。

---

## 1. 为什么这样做

本仓库通过 **76 处 `../../ref/` 的 `ProjectReference`** 直接编译子模块源码（不是 NuGet 包），并且 `src/Directory.Packages.props` 还 import 了 `ref/DirectoryPackages/Directory.Packages.props` 作为全仓包版本真源。因此子模块不是可有可无的依赖，而是**源码级**的一部分。

把这些仓库统一镜像到 `stea-peroe` 后：即使上游仓库改名/删除/转移，克隆与构建也不受影响。

---

## 2. 现状

- 顶层 `.gitmodules` 的 **17 条 URL 已全部指向 `https://github.com/stea-peroe/`**（本仓库内的改动）。
- 组织内共有 **23 个仓库**（17 个顶层 + 6 个仅由嵌套 `.gitmodules` 声明的仓库），其中 22 个是 fork、1 个是 wiki 镜像。
- 递归 gitlink 共 **27 处**，涉及 **27 组 (仓库, commit)**；**全部已校验存在于镜像仓库中**（见 §5）。

---

## 3. 镜像清单

| 上游仓库 | `stea-peroe` 镜像 | 类型 | 备注 |
| --- | --- | --- | --- |
| BeyondDimension/DirectoryPackages | `stea-peroe/DirectoryPackages` | fork | 7 处 gitlink 复用；顶层 pin 在 `net11` 分支 |
| BeyondDimension/ArchiSteamFarm | `stea-peroe/ArchiSteamFarm` | fork | 含 ASF-ui / wiki 两个嵌套子模块 |
| BeyondDimension/Avalonia.Image2 | `stea-peroe/Avalonia.Image2` | fork | pin 在 `net11` |
| BeyondDimension/Gameloop.Vdf | `stea-peroe/Gameloop.Vdf` | fork | |
| BeyondDimension/Steam4NET | `stea-peroe/Steam4NET` | fork | 仓库内含 26.5 MB 工具链二进制 |
| BeyondDimension/Titanium-Web-Proxy | `stea-peroe/Titanium-Web-Proxy` | fork | 未被 `src/` 引用 |
| BeyondDimension/SteamAchievementManager | `stea-peroe/SteamAchievementManager` | fork | 两个不同 pin |
| BeyondDimension/SteamClient | `stea-peroe/SteamClient` | fork | pin 在 `net11` |
| quamotion/dotnet-packaging | `stea-peroe/dotnet-packaging` | fork | 第三方原始仓库 |
| BeyondDimension/WTTS.MicroServices.ClientSDK | `stea-peroe/WTTS.MicroServices.ClientSDK` | fork | pin 在 `detached` 分支 |
| BeyondDimension/Common | `stea-peroe/Common` | fork | 两个 pin：`edaf67be`(net11) 与 `9872dff2`(dev8) |
| BeyondDimension/WinAuth | `stea-peroe/WinAuth` | fork | |
| BeyondDimension/Facepunch.Steamworks | `stea-peroe/Facepunch.Steamworks` | fork | 仓库内含 85.5 MB 误提交 dll |
| BeyondDimension/appcenter-sdk-dotnet | `stea-peroe/appcenter-sdk-dotnet` | fork | pin 在 `feature/support-any-v5.0.6-BD` |
| reactiveui/Fusillade | `stea-peroe/Fusillade` | fork | 第三方原始仓库 |
| BeyondDimension/Avalonia8 | `stea-peroe/Avalonia8` | fork | pin 在 `net11`；内含 Common/SteamKit/Protobufs |
| BeyondDimension/dotnetCampus.Ipc | `stea-peroe/dotnetCampus.Ipc` | fork | |
| BeyondDimension/ValveKeyValue | `stea-peroe/ValveKeyValue` | fork | 嵌套于 SteamClient |
| BeyondDimension/WTTS.Public | `stea-peroe/WTTS.Public` | fork | 嵌套于 WTTS.MicroServices.ClientSDK |
| BeyondDimension/SteamKit | `stea-peroe/SteamKit` | fork | 嵌套于 Avalonia8/ref/Common；pin 在 `fixs` |
| steamdatabase/protobufs → SteamTracking/Protobufs | `stea-peroe/Protobufs` | fork | 上游已改名，fork 时用新名 |
| JustArchiNET/ASF-ui | `stea-peroe/ASF-ui` | fork | 嵌套于 ArchiSteamFarm |
| JustArchiNET/ArchiSteamFarm.wiki | `stea-peroe/ArchiSteamFarm-wiki` | **镜像仓库** | wiki 不能用 fork API，见 §6 |

---

## 4. 新克隆怎么用

顶层 URL 已经是镜像地址，所以标准用法不变：

```bash
git clone --recurse-submodules https://github.com/<you>/SteamTools.git
# 已有克隆：
git submodule sync --recursive
git submodule update --init --recursive
```

---

## 5. 校验

校验方式：对每个 gitlink 的 pinned commit 调 `GET /repos/stea-peroe/<name>/commits/<sha>`，返回 200 即代表该 commit 在镜像里可达。

```text
gitlink 总数            27
唯一 (仓库, commit)      27
校验结果                27/27 可达，0 missing（含 Protobufs@9ff05344、wiki@c56fc0c3）
```

以下 8 个 commit **只存在于非默认分支**，fork 时已使用 `default_branch_only: false` 保留全部分支：
`DirectoryPackages@net11`、`Common@net11`、`SteamClient@net11`、`Avalonia8@net11`、`Avalonia.Image2@net11`、`WTTS.MicroServices.ClientSDK@detached`、`appcenter-sdk-dotnet@feature/support-any-v5.0.6-BD`、`SteamKit@fixs`。

---

## 6. 两个特例

### 6.1 wiki 仓库

`JustArchiNET/ArchiSteamFarm.wiki` 是 wiki 仓库，**REST fork 接口返回 404**，且 GitHub **不允许创建以 `.wiki` 结尾的仓库**（422 `name cannot end in .wiki`）。

处理方式：创建普通仓库 `stea-peroe/ArchiSteamFarm-wiki`，用 mirror clone + mirror push 把 wiki 的完整历史推过去（`refs/heads/master` 为 `6201b2b2`，pinned commit `c56fc0c3` 已包含）。

### 6.2 上游改名的仓库

`steamdatabase/protobufs` 已重定向到 `SteamTracking/Protobufs`。对旧路径 `POST /forks` 会返回 `401 Requires authentication`（重定向丢失鉴权），**必须用新名字 fork**，得到 `stea-peroe/Protobufs`。

---

## 7. 嵌套子模块（未改写）

**范围**：6 个仓库只由嵌套 `.gitmodules` 声明 —— `ValveKeyValue`、`WTTS.Public`、`SteamKit`、`Protobufs`、`ASF-ui`、`ArchiSteamFarm.wiki`；另有 6 处嵌套引用同一个 `DirectoryPackages`。

**现状**：这些嵌套 URL 写死在**父级子模块的 pinned commit 里**，主仓无法覆盖。因此 `--recursive` 检出时，它们仍从 `BeyondDimension/`（或第三方）拉取 —— 这些上游仓库是 public 的，**检出与构建不受影响**，只是没有走镜像。

三种处理方式：

| 方案 | 做法 | 代价 |
| --- | --- | --- |
| A. 保持现状（默认） | 不改 | 嵌套依赖仍指向上游；上游若删除会断 |
| B. 本地重定向（推荐日常用） | `git config url."https://github.com/stea-peroe/".insteadOf "https://github.com/BeyondDimension/"` 等 | 零提交、可逆；仅对配置过的本地克隆生效 |
| C. 彻底改写（强交付态） | 自底向上在镜像仓库各提一次 commit 改写 `.gitmodules`，再逐层更新父级 gitlink | 约 10–15 个新提交，且这些 fork 分支会与上游分叉，后续 `⬆️ Update ref submodules` 合并时 `.gitmodules` 必然冲突 |

方案 B 的完整命令（一次覆盖 20 个经 StreamRedirect 的 URL）：

```bash
git config --local url."https://github.com/stea-peroe/".insteadOf "https://github.com/BeyondDimension/"
git config --local url."https://github.com/stea-peroe/".insteadOf "https://github.com/quamotion/"
git config --local url."https://github.com/stea-peroe/".insteadOf "https://github.com/reactiveui/"
git submodule sync --recursive
git submodule update --init --recursive
```

> 注意：`insteadOf` 按前缀**字符串替换**，多个规则时 git 选择最长匹配；改写后 `git remote -v` 仍显示原地址，属正常现象。

---

## 8. 与上游同步

`upstream` 远端（`https://github.com/BeyondDimension/SteamTools.git`）保持不变，下面的流程照旧可用：

```bash
git fetch upstream
git merge upstream/develop
git submodule sync --recursive      # 若上游改了 .gitmodules
git submodule update --init --recursive
```

⚠️ 上游每次推进子模块指针（提交信息 `⬆️ Update ref submodules`）后，指针仍指向 `BeyondDimension/*` 的 commit。
如果这些新 commit 尚未同步到镜像，`git submodule update` 会失败。此时二选一：
1. 同步镜像：`git -C ref/<name> fetch upstream && git -C ref/<name> push origin --all`（临时把 `upstream` 指向上游仓库）；
2. 或按 §7 方案 B 把该子模块临时指回上游。

---

## 9. 已知限制

- 镜像仓库 **不包含** issues / PR / releases / Actions secrets；fork 的 Actions 默认关闭。
- 可见性继承上游（全部 public），Free 组织下不能把 fork 改成 private。
- `ref/` 下的仓库各自有自己的 `.editorconfig` / CI，**不要在本仓库直接改其内容**（见 `AGENTS.md` §14）。

