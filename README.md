# PCL Download Restore

恢复 Plain Craft Launcher 2 快照版中被移除的 Minecraft 下载相关功能，并保证该修复在 PCL 自更新后依然生效。

针对 **2.99.0.x** 快照线，兼容 2.99.0.0 与 2.99.0.1 两个构建。

---

## 它解决什么问题

官方快照版在若干位置把功能入口换成了占位或抛异常，但底层实现完整保留。本项目通过 IL 补丁把这些入口接回去：

| 功能 | 官方做法 | 本项目的处理 |
|---|---|---|
| 下载版本列表 | `PageDownloadInstall.LoaderInit` 里塞入假 `LoaderTask`（lambda 直接 throw） | 换回真 `DlClientListLoader`；遇到其他 `DlClientList*` 变体也强制改回 |
| 原版游戏入口 | `PageDownloadLeft..ctor` 追加 `ItemInstall.Visibility = Collapsed` 等 4 行 | 删除该指令块 |
| 整合包安装 | `ModModpack.ModpackInstall` 末尾统一 `throw`，所有 `Leave` 汇到此处 | 在该点重建 `switch(num)` 分派，并补回编译器丢弃的 `ldloc.0; ret` |
| 更新后复发 | PCL 自更新会用官方更新器覆盖主程序 | `UpdateRestart` 的更新器路径改为 `PCL-Restore.exe`，更新时先给更新器打补丁再让它替换主程序 |
| 离线皮肤（1.19.3+） | `pack_format` 用 `Version.Major` 分派、皮肤路径门槛写成 `>= new Version(19,0,3)` | 改为按 `Minor` 判定，两者在真实 `1.X.Y` 版本串下才成立 |

七个 `InstallPack*` 实现（CurseForge / HMCL / MMC / MCBBS / Modrinth / LauncherPack / Compress）在官方构建里一个不少，缺失的只是分派入口。

---

## 版本差异与根因

### 2.99.0.0

- `LoaderInit` 使用 `Ldstr "关闭下载"` + 假 `LoaderTask`
- `ModpackInstall` 末尾 throw，文案 `PCL 暂时不再提供 MC 下载功能`

### 2.99.0.1

- `LoaderInit` 恢复正常，改为在 `PageDownloadLeft..ctor` 隐藏 `ItemInstall` 与 `LabResource`，并把默认页改成 Mod
- 文案去掉「暂时」，变成 `PCL 不再提供 MC 下载功能`

文案变化会让按旧串匹配的补丁静默失效。本项目的匹配串是 `不再提供 MC 下载`，两版都能命中。

### 版本字段的坑

`Version.Vanilla` 是标准 `System.Version`，`"1.19.3"` 解析为 `Major=1, Minor=19, Build=3`。

因此 `switch (Vanilla.Major)` 在 1.x 上恒为 1，`Vanilla >= new Version(19,0,3)` 恒为假。真实主版本号在 `Minor` 上。

---

## 用法

### 直接使用

把 `PCL-Restore.exe` 放到 PCL 主程序同目录，双击即可（会自动打补丁并启动 PCL）。

### 命令行

```text
PCL-Restore.exe                     打补丁并启动 PCL
PCL-Restore.exe --apply [path]      对指定 exe 打补丁
PCL-Restore.exe --apply --quiet     静默模式
PCL-Restore.exe --silent            静默修补同目录主程序
PCL-Restore.exe --status            自检，弹窗显示版本/指纹/补丁状态
PCL-Restore.exe --update <pid> a b bool
                                    由 PCL 更新流程调用：先补丁更新器，再转交
```

`--status` 退出码：

| 码 | 含义 |
|---|---|
| 0 | 发布修复版，字节一致 |
| 1 | 版本不符或补丁缺失 |
| 2 | 版本与补丁正确，但字节与发布基准不同（典型：更新后自动重打过补丁） |

### 检查补丁状态

```powershell
.\Verify.exe "Plain Craft Launcher 2.exe"
```

输出三项修复的存在性，退出码为位掩码（1=下载 loader，2=防覆盖，4=皮肤）。

---

## 组件

| 项目 | 说明 |
|---|---|
| `src/PCLRestore` | 核心补丁器，含全部四类补丁与自更新拦截 |
| `src/Verify` | 补丁状态检测，输出位掩码 |
| `src/PatchSkin` | 离线皮肤 1.19.3+ 修复（独立功能，不打进主补丁流程） |
| `tools/Patcher` | 开发期 IL 导出与试补丁 |
| `tools/Restore2991` | 2.99.0.1 两处阉割点的提取与重建实现 |
| `tools/Inspect` | 程序集引用 / 类型 / 方法列表 |

`dist/` 内为 Windows x64 单文件产物，依赖 .NET 8 桌面运行时。

校验和见 `dist/SHA256SUMS.txt`。

---

## 构建

```powershell
dotnet publish src/PCLRestore -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o dist
```

其余项目同理，把路径换成对应目录。

依赖：.NET 8 SDK、dnlib 4.4.0（NuGet 自动还原）。

---

## 工作原理

补丁全部基于 dnlib 的 IL 重写，不修改官方文件之外的任何内容，每次写盘前保留 `.orig-backup`。

三处关键实现细节：

1. **不能替换异常处理边界上的指令对象**。`Ildstr/Newobj/Throw` 三条压在内层 `try` 的 `HandlerEnd` 上，直接替换列表元素会触发 dnlib 的 `removed instruction` 错误。做法是在其之前插入分派，让原 throw 成为不可达代码。

2. **`switch` 表不能复用同一 `Instruction` 对象指向多个 case**。dnlib 写出时会判定目标悬空，必须为每个索引创建独立指令。

3. **插入指令后需手动把短跳转升级为长跳转**。`SimplifyBranches()` 会重建指令对象、破坏异常边界，所以按映射表逐条改 `OpCode`。

---

## 兼容性

| PCL 版本 | 下载 loader | 原版入口 | 整合包 | 防覆盖 |
|---|---|---|---|---|
| 2.99.0.0 | ✓ | 无需 | ✓ | ✓ |
| 2.99.0.1 | ✓ | ✓ | ✓ | ✓ |
| 正式版 2.12.x / 2.13.x | 原本正常 | 原本正常 | 原本正常 | ✓ |

正式版线从未阉割，补丁器对其只做防覆盖处理。

---

## 许可

**本项目代码**以 MIT 发布，见 `LICENSE`。

**PCL 本身不是 MIT**。其仓库根目录的 `LICENCE` 文件被 GitHub 识别为 `NOASSERTION`，实际由《用户协议与免责声明》与《PCL 分发有限许可》共同约束。其中关键一条：分发许可只授予**「原样副本」**（未经任何方式改变的软件副本）的传播权，修改过的版本不在授权范围内。

因此本仓库**只发布方法，不发布成品**：

- 不包含 PCL 的可执行文件、反编译产物、资源或备份
- 不包含把 PCL 修改版整体打包的分发包
- `dist/` 内全部是工具自身产物，不含任何 PCL 文件

使用方式是自己合法取得 PCL 原版，再把工具放到同目录运行，修改发生在用户本机。

完整的条款引用与逐条对应说明见 `THIRD-PARTY-NOTICES.md`。

PCL 项目：<https://github.com/Meloong-Git/PCL>，版权归成都瓜皮龙科技有限公司。
