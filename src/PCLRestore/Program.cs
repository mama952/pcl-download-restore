using System.Diagnostics;
using System.Reflection;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using WinForms = System.Windows.Forms;

namespace PCLRestore;

/// <summary>
/// Restores PCL's built-in download features and keeps them restored across
/// launcher self-updates.
///
/// Patches applied to any PCL executable:
///   1. PageDownloadInstall.LoaderInit  - the placeholder loader that only throws is
///      replaced with the real ModDownload.DlClientListLoader (原版游戏下载).
///   2. ModModpack.ModpackInstall(File) - the gutted body that throws right after
///      detecting the pack type is replaced with the original dispatch switch, so
///      CurseForge / Modrinth / HMCL / MMC / MCBBS / launcher-pack / plain-zip
///      installers are reachable again (整合包安装).
///   3. ModModpack.ModpackInstall()     - the "导入整合包 / 安装整合包" entry is
///      restored from a red hint back to a file picker + background install.
///   4. ModSecret.UpdateRestart         - the updater launch path is redirected to
///      this tool, so the freshly generated updater is itself patched before it
///      replaces the main executable. That closes the "update overwrites the fix" loop.
///
/// Modes:
///   (none)                     patch main exe in this folder, then start it
///   --apply [path]             patch only
///   --silent                   patch only, never start, never show UI
///   --status                   report whether the main exe needs a patch
///   --update &lt;pid&gt; a b bool     invoked by PCL: patch the updater, then run it
/// </summary>
internal static class Program
{
    private const string MainExeName = "Plain Craft Launcher 2.exe";
    private const string UpdaterRelPath = @"PCL\Plain Craft Launcher 2.exe";
    private const string RedirectName = "PCL-Restore.exe";
    private const string UpdaterPathLiteral = @"PCL\Plain Craft Launcher 2.exe";

    [STAThread]
    private static int Main(string[] args)
    {
        var baseDir = AppContext.BaseDirectory;

        try
        {
            if (args.Length > 0 && args[0] == "--update")
                return RunUpdater(args, baseDir);

            if (args.Length > 0 && args[0] == "--apply")
            {
                var quiet = args.Contains("--quiet");
                var rest = args.Skip(1).Where(a => a != "--quiet").ToArray();
                var target = rest.Length > 0 ? rest[0] : Path.Combine(baseDir, MainExeName);
                return ApplyTo(target, interactive: !quiet) ? 0 : 1;
            }

            if (args.Length > 0 && args[0] == "--silent")
            {
                ApplyTo(Path.Combine(baseDir, MainExeName), interactive: false);
                return 0;
            }

            if (args.Length > 0 && args[0] == "--status")
                return ShowStatus(Path.Combine(baseDir, MainExeName), baseDir);

            return LaunchMain(args, baseDir);
        }
        catch (Exception ex)
        {
            Notify("PCL 下载功能修复工具出错：" + Environment.NewLine + ex.Message);
            return 1;
        }
    }

    private static int LaunchMain(string[] args, string baseDir)
    {
        var main = Path.Combine(baseDir, MainExeName);
        if (!File.Exists(main))
        {
            Notify("未找到 " + MainExeName + "。" + Environment.NewLine +
                   "请把本工具放到 PCL 所在文件夹后再运行。");
            return 1;
        }

        ApplyTo(main, interactive: false);
        var psi = new ProcessStartInfo(main)
        {
            WorkingDirectory = baseDir,
            UseShellExecute = false,
            Arguments = JoinArgs(args)
        };
        Process.Start(psi);
        return 0;
    }

    /// <summary>
    /// PCL launches this tool instead of its updater. Patch the updater first, then
    /// hand it the original arguments untouched so the update proceeds normally.
    /// </summary>
    private static int RunUpdater(string[] args, string baseDir)
    {
        var updater = Path.Combine(baseDir, UpdaterRelPath);
        if (File.Exists(updater))
            ApplyTo(updater, interactive: false);

        if (!File.Exists(updater))
            return 1;

        var psi = new ProcessStartInfo(updater)
        {
            WorkingDirectory = Path.GetDirectoryName(updater) ?? baseDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            Arguments = JoinArgs(args)
        };
        Process.Start(psi);
        return 0;
    }

    private static bool ApplyTo(string path, bool interactive)
    {
        if (!File.Exists(path))
        {
            if (interactive) Notify("文件不存在：" + path);
            return false;
        }

        try
        {
            bool changed = false;
            var tmp = path + ".restore-tmp";
            var mod = ModuleDefMD.Load(path);
            try
            {
                // Patch 1: real download-list loader.
                if (!IsLoaderInitPatched(mod) && PatchLoaderInitCore(mod))
                    changed = true;

                // Patch 1b: bring back the "install vanilla game" entry (2.99.0.1+).
                Diag("entry=" + IsDownloadEntryFixed(mod));
                if (FixDownloadEntry(mod))
                {
                    Diag("entry patched");
                    changed = true;
                }

                // Patch 2 + 3: modpack install dispatch and its dialog entry.
                Diag("loader=" + IsLoaderInitPatched(mod));
                if (PatchModpackDispatch(mod))
                {
                    Diag("dispatch patched");
                    changed = true;
                }
                else Diag("dispatch NOT patched");
                if (PatchModpackDialog(mod))
                {
                    Diag("dialog patched");
                    changed = true;
                }
                else Diag("dialog NOT patched");

                // Patch 4: keep the fix alive across self-updates.
                if (PatchUpdateRestart(mod))
                    changed = true;

                if (!changed)
                    return true;

                mod.Write(tmp);
            }
            finally
            {
                // Close the source image before replacing it in place.
                mod.Dispose();
            }

            File.Copy(tmp, path, overwrite: true);
            File.Delete(tmp);

            if (interactive && changed)
                Notify("已恢复 Minecraft 下载功能（含整合包安装）。" + Environment.NewLine +
                       "现在可以直接启动 PCL。");
            return true;
        }
        catch (Exception ex)
        {
            LogError(path, ex);
            if (interactive)
                Notify("补丁失败：" + Environment.NewLine + ex.Message + Environment.NewLine +
                       Environment.NewLine + "如果 PCL 正在运行，请先完全退出再试。");
            return false;
        }
    }

    private static void Diag(string message)
    {
        if (Environment.GetEnvironmentVariable("PCL_RESTORE_DEBUG") != "1") return;
        try
        {
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "PCL-Restore.diag.log"),
                $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}");
        }
        catch
        {
        }
    }

    private static void LogError(string target, Exception ex)
    {
        try
        {
            var log = Path.Combine(Path.GetTempPath(), "PCL-Restore.error.log");
            File.AppendAllText(log,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] target={target}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // never let diagnostics break the tool
        }
    }

    // ---- release verification --------------------------------------------------

    // SHA-256 of the exact executable shipped as the fixed build.
    private const string ExpectedFixedSha256 =
        "C2ED28BF707B91733A44B4EA056C17DDC29FF752E7E178032080028D6095993B";

    // SHA-256 of the pristine 2.99.0.0 executable before any patching.
    private const string ExpectedPristineSha256 =
        "5179CEB369FA3BEE26ACB223D782690A13B530BB135506B9A59AB16515915094";

    private const string ExpectedVersion = "2.99.0.0";

    private static string Sha256Of(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha = System.Security.Cryptography.SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream));
    }

    /// <summary>
    /// Reports independent facts so the answer survives an update: which build the
    /// file is, whether it is bit-identical to the shipped fix, and which patches
    /// are currently applied.
    /// Exit codes: 0 = shipped fix, intact and patched;
    ///             2 = right build, patched, but not bit-identical (e.g. re-patched after an update);
    ///             1 = wrong build or a patch is missing.
    /// </summary>
    private static int ShowStatus(string mainPath, string baseDir)
    {
        var lines = new List<string>();

        if (!File.Exists(mainPath))
        {
            Notify("未找到 " + MainExeName + Environment.NewLine +
                   "请把本工具放在 PCL 所在文件夹后再运行。");
            return 1;
        }

        var version = FileVersionInfo.GetVersionInfo(mainPath).FileVersion ?? "(未知)";
        var hash = Sha256Of(mainPath);
        var length = new FileInfo(mainPath).Length;

        var loaderPatched = false;
        var modpackDispatch = false;
        var modpackDialog = false;
        var updaterRedirected = false;
        try
        {
            using var mod = ModuleDefMD.Load(mainPath);
            loaderPatched = IsLoaderInitPatched(mod);
            modpackDispatch = IsModpackDispatchPatched(mod);
            modpackDialog = IsModpackDialogPatched(mod);
            updaterRedirected = IsUpdaterRedirected(mod);
        }
        catch (Exception ex)
        {
            lines.Add("读取程序结构失败：" + ex.Message);
        }

        var allPatched = loaderPatched && modpackDispatch && modpackDialog;

        lines.Add($"版本号：{version}");
        lines.Add($"大小：{length:N0} 字节");
        lines.Add($"SHA-256：{hash}");

        int exitCode;
        if (hash == ExpectedFixedSha256)
        {
            lines.Add("");
            lines.Add("✓ 这就是发布的修复版，且文件未被改动。");
            exitCode = 0;
        }
        else if (hash == ExpectedPristineSha256)
        {
            lines.Add("");
            lines.Add("✗ 这是未修复的原始 2.99.0.0，下载功能仍是关闭状态。");
            exitCode = 1;
        }
        else
        {
            lines.Add("");
            lines.Add("! 文件与发布版不一致（可能被更新覆盖或再次打过补丁）。");
            exitCode = allPatched ? 2 : 1;
        }

        lines.Add("");
        lines.Add(loaderPatched ? "✓ 原版游戏下载：已恢复" : "✗ 原版游戏下载：未恢复（占位 loader 仍在）");
        lines.Add(modpackDispatch ? "✓ 整合包安装：已恢复" : "✗ 整合包安装：未恢复（安装入口仍直接报错）");
        lines.Add(modpackDialog ? "✓ 整合包导入入口：已恢复" : "✗ 整合包导入入口：未恢复（仍是红色提示）");
        lines.Add(updaterRedirected
            ? "✓ 更新防覆盖：已生效"
            : "✗ 更新防覆盖：未生效（更新后会退回官方版）");

        if (version != ExpectedVersion)
            lines.Add(Environment.NewLine + $"注意：版本号不是预期的 {ExpectedVersion}。");

        Notify(string.Join(Environment.NewLine, lines));
        return exitCode;
    }

    private static bool IsUpdaterRedirected(ModuleDefMD mod)
    {
        var type = mod.GetTypes().FirstOrDefault(x => x.Name == "ModSecret");
        var method = type?.Methods.FirstOrDefault(x => x.Name == "UpdateRestart");
        return method?.Body != null
               && method.Body.Instructions.Any(i => i.OpCode == OpCodes.Ldstr
                   && i.Operand is string s && s == RedirectName);
    }

    // ---- patch 1: restore the real download-list loader -------------------------

    private static bool PatchLoaderInitCore(ModuleDefMD mod)
    {
        var type = mod.GetTypes().FirstOrDefault(x => x.Name == "PageDownloadInstall");
        var method = type?.Methods.FirstOrDefault(x => x.Name == "LoaderInit");
        var body = method?.Body;
        if (body == null)
            return false;

        var instrs = body.Instructions;

        // Resolve the genuine loader field once.
        var modDownload = mod.GetTypes().FirstOrDefault(x => x.Name == "ModDownload");
        var realLoader = modDownload?.Fields.FirstOrDefault(f => f.Name == "DlClientListLoader");
        if (realLoader == null)
            return false;

        // Case A: the crippled placeholder pattern (Ldstr "关闭下载" -> new LoaderTask).
        int start = -1, end = -1;
        for (int i = 0; i < instrs.Count; i++)
        {
            if (start < 0 && instrs[i].OpCode == OpCodes.Ldstr
                && instrs[i].Operand is string s && s == "关闭下载")
            {
                start = i;
                continue;
            }
            if (start >= 0 && instrs[i].OpCode == OpCodes.Newobj && instrs[i].Operand is IMethod im
                && im.Name == ".ctor" && im.DeclaringType?.Name.StartsWith("LoaderTask") == true)
            {
                end = i;
                break;
            }
        }

        if (start >= 0 && end >= 0)
        {
            // Refuse to cut when anything outside the span branches into it.
            var span = new HashSet<Instruction>();
            for (int i = start; i <= end; i++) span.Add(instrs[i]);
            for (int i = 0; i < instrs.Count; i++)
            {
                if (i >= start && i <= end) continue;
                if (instrs[i].Operand is Instruction t && span.Contains(t))
                    return false;
            }

            for (int i = end; i >= start; i--) instrs.RemoveAt(i);
            instrs.Insert(start, Instruction.Create(OpCodes.Ldsfld, realLoader));
            body.UpdateInstructionOffsets();
            return true;
        }

        // Case B: some other LoaderTask<...DlClientListResult> field is wired in.
        // Repoint any such reference at the real loader so variants are still fixed.
        var swapped = false;
        foreach (var ins in instrs)
        {
            if (ins.OpCode != OpCodes.Ldsfld) continue;
            if (ins.Operand is not IField f) continue;
            if (f == realLoader) continue;
            if (f.DeclaringType?.Name != "ModDownload") continue;
            if (!f.Name.StartsWith("DlClientList")) continue;
            if (f.FieldSig?.Type?.FullName != realLoader.FieldSig?.Type?.FullName) continue;
            ins.Operand = realLoader;
            swapped = true;
        }
        if (swapped)
        {
            body.UpdateInstructionOffsets();
            return true;
        }

        return false;
    }

    private static bool IsLoaderInitPatched(ModuleDefMD mod)
    {
        var type = mod.GetTypes().FirstOrDefault(x => x.Name == "PageDownloadInstall");
        var method = type?.Methods.FirstOrDefault(x => x.Name == "LoaderInit");
        if (method?.Body == null)
            return false;

        // Patched means: the method wires the genuine DlClientListLoader and no
        // other DlClientList* loader substitutes for it.
        var modDownload = mod.GetTypes().FirstOrDefault(x => x.Name == "ModDownload");
        var realLoader = modDownload?.Fields.FirstOrDefault(f => f.Name == "DlClientListLoader");
        if (realLoader == null)
            return false;

        var usesReal = false;
        foreach (var ins in method.Body.Instructions)
        {
            if (ins.OpCode != OpCodes.Ldsfld || ins.Operand is not IField f) continue;
            if (f == realLoader) { usesReal = true; continue; }
            if (f.DeclaringType?.Name == "ModDownload" && f.Name.StartsWith("DlClientList"))
                return false;
        }
        return usesReal;
    }

    // ---- patch 1b: restore the "install vanilla game" entry --------------------

    /// <summary>
    /// 2.99.0.1 hides the vanilla-install entry in PageDownloadLeft's constructor:
    ///   ItemInstall.Visibility = Collapsed;
    ///   LabResource.Visibility = Collapsed;
    ///   PageID = DownloadMod;  ItemMod.Checked = true;
    /// The page itself and its loader are intact, so removing this block brings the
    /// entry back. Idempotent: a build without the block is already fine.
    /// </summary>
    private static bool FixDownloadEntry(ModuleDefMD mod)
    {
        var type = mod.GetTypes().FirstOrDefault(x => x.Name == "PageDownloadLeft");
        var ctor = type?.Methods.FirstOrDefault(x => x.Name == ".ctor");
        var body = ctor?.Body;
        if (body == null) return false;

        var ins = body.Instructions;

        int start = -1, end = -1;
        for (int i = 0; i < ins.Count; i++)
        {
            if (start < 0 && ins[i].OpCode == OpCodes.Callvirt && ins[i].Operand is IMethod m
                && m.Name == "get_ItemInstall")
            {
                start = i;
                continue;
            }
            if (start >= 0 && ins[i].OpCode == OpCodes.Callvirt && ins[i].Operand is IMethod m2
                && m2.Name == "set_Checked")
            {
                end = i;
                break;
            }
        }
        if (start < 0 || end < 0) return false;   // already restored

        if (start - 1 >= 0 && ins[start - 1].OpCode == OpCodes.Ldarg_0) start--;

        var span = new HashSet<Instruction>();
        for (int i = start; i <= end; i++) span.Add(ins[i]);
        for (int i = 0; i < ins.Count; i++)
        {
            if (i >= start && i <= end) continue;
            if (ins[i].Operand is Instruction tgt && span.Contains(tgt))
            {
                Diag("entry: branch into span, skip");
                return false;
            }
        }

        for (int i = end; i >= start; i--) ins.RemoveAt(i);
        body.UpdateInstructionOffsets();
        Diag($"entry: removed {end - start + 1} instructions");
        return true;
    }

    private static bool IsDownloadEntryFixed(ModuleDefMD mod)
    {
        var type = mod.GetTypes().FirstOrDefault(x => x.Name == "PageDownloadLeft");
        var ctor = type?.Methods.FirstOrDefault(x => x.Name == ".ctor");
        return ctor?.Body != null
               && !ctor.Body.Instructions.Any(i => i.OpCode == OpCodes.Callvirt
                   && i.Operand is IMethod m && m.Name == "set_Visibility"
                   && m.DeclaringType?.FullName == "System.Windows.UIElement");
    }

    // ---- patch 2: restore the modpack install dispatch -------------------------

    // 2.99.0.0 said "PCL 暂时不再提供 MC 下载功能"; 2.99.0.1 dropped "暂时".
    // Match only the shared part so both builds work and future rewording survives.
    private const string DisabledHintFragment = "不再提供 MC 下载";

    private static TypeDef FindModModpack(ModuleDefMD mod) =>
        mod.GetTypes().FirstOrDefault(x => x.Name == "ModModpack");

    private static MethodDef FindModpackInstallWithArgs(ModuleDefMD mod) =>
        FindModModpack(mod)?.Methods.FirstOrDefault(x => x.Name == "ModpackInstall" && x.MethodSig.Params.Count == 3);

    private static MethodDef FindModpackInstallNoArgs(ModuleDefMD mod) =>
        FindModModpack(mod)?.Methods.FirstOrDefault(x => x.Name == "ModpackInstall" && x.MethodSig.Params.Count == 0);

    /// <summary>
    /// The official 2.99 build replaced the type-dispatch switch of
    /// ModpackInstall(File, InstanceName, Logo) with a single throw. Every pack type
    /// still sets the local "num", every "leave" still jumps to that throw, so the
    /// fix is to rebuild the switch at exactly that spot and append the epilogue the
    /// compiler dropped.
    /// </summary>
    private static bool PatchModpackDispatch(ModuleDefMD mod)
    {
        var type = FindModModpack(mod);
        var method = FindModpackInstallWithArgs(mod);
        var body = method?.Body;
        if (type == null || body == null)
        {
            Diag("dispatch: type/body null");
            return false;
        }
        if (IsModpackDispatchPatched(mod))
        {
            Diag("dispatch: already patched");
            return false;
        }

        var ins = body.Instructions;

        // Locate: ldstr "为遵守…" ; newobj Exception::.ctor(string) ; throw
        int idx = -1;
        for (int i = 0; i + 2 < ins.Count; i++)
        {
            if (ins[i].OpCode == OpCodes.Ldstr && ins[i].Operand is string s && s.Contains(DisabledHintFragment)
                && ins[i + 1].OpCode == OpCodes.Newobj && ins[i + 1].Operand is IMethod ctor
                && ctor.Name == ".ctor" && ctor.DeclaringType?.Name == "Exception"
                && ins[i + 2].OpCode == OpCodes.Throw)
            {
                idx = i;
                break;
            }
        }
        if (idx < 0)
        {
            Diag("dispatch: throw site not found");
            return false;
        }

        // Locals: 0 = LoaderCombo<string> (result), 1 = ZipArchive, 2 = base folder,
        //         4 = num (int). Anything else means an unexpected build; bail out.
        if (body.Variables.Count < 10)
        {
            Diag("dispatch: locals=" + body.Variables.Count);
            return false;
        }
        var resultLocal = body.Variables[0];
        var archiveLocal = body.Variables[1];
        var baseFolderLocal = body.Variables[2];
        var numLocal = body.Variables[4];
        if (archiveLocal.Type.FullName != "System.IO.Compression.ZipArchive") { Diag("dispatch: local1=" + archiveLocal.Type.FullName); return false; }
        if (baseFolderLocal.Type.FullName != "System.String") { Diag("dispatch: local2=" + baseFolderLocal.Type.FullName); return false; }
        if (numLocal.Type.FullName != "System.Int32") { Diag("dispatch: local4=" + numLocal.Type.FullName); return false; }

        // Reuse the logger call and log-path literal already present in this method.
        var loggerInfo = ins.Select(i => i.Operand).OfType<IMethod>()
            .FirstOrDefault(m => m.Name == "Info" && m.DeclaringType?.Name == "Logger");
        var logPath = ins.Where(i => i.OpCode == OpCodes.Ldstr && i.Operand is string s && s.Contains("ModModpack.vb"))
            .Select(i => (string)i.Operand).FirstOrDefault();
        if (loggerInfo == null || logPath == null)
        {
            Diag("dispatch: logger=" + (loggerInfo != null) + " path=" + (logPath != null));
            return false;
        }

        MethodDef Installer(string name, int argc) =>
            type.Methods.FirstOrDefault(x => x.Name == name && x.MethodSig.Params.Count == argc);

        var curseForge = Installer("InstallPackCurseForge", 5);
        var hmcl = Installer("InstallPackHMCL", 3);
        var mmc = Installer("InstallPackMMC", 3);
        var mcbbs = Installer("InstallPackMCBBS", 4);
        var modrinth = Installer("InstallPackModrinth", 5);
        var launcherPack = Installer("InstallPackLauncherPack", 3);
        var compress = Installer("InstallPackCompress", 2);
        if (curseForge == null || hmcl == null || mmc == null || mcbbs == null ||
            modrinth == null || launcherPack == null || compress == null)
        {
            Diag("dispatch: installer missing");
            return false;
        }

        // Epilogue lives after the outer finally, so "leave" runs the ZipArchive dispose.
        var epilogue = Instruction.Create(OpCodes.Ldloc, resultLocal);
        var epilogueRet = Instruction.Create(OpCodes.Ret);

        var dispatch = new List<Instruction>();
        var sw = Instruction.Create(OpCodes.Switch, new Instruction[10]);
        dispatch.Add(sw);

        var brDefault = Instruction.Create(OpCodes.Br, (Instruction)null);
        dispatch.Add(brDefault);

        Instruction CaseStart = null;
        void AddCase(string label, MethodDef target, bool withInstance, bool withLogo)
        {
            var first = Instruction.Create(OpCodes.Ldstr, label);
            CaseStart = first;
            dispatch.Add(first);
            dispatch.Add(Instruction.Create(OpCodes.Ldc_I4_0));          // LogBehavior.Info
            dispatch.Add(Instruction.Create(OpCodes.Ldstr, logPath));
            dispatch.Add(Instruction.Create(OpCodes.Call, loggerInfo));
            dispatch.Add(Instruction.Create(OpCodes.Ldarg_0));           // File
            dispatch.Add(Instruction.Create(OpCodes.Ldloc, archiveLocal));
            dispatch.Add(Instruction.Create(OpCodes.Ldloc, baseFolderLocal));
            if (withInstance) dispatch.Add(Instruction.Create(OpCodes.Ldarg_1));
            if (withLogo) dispatch.Add(Instruction.Create(OpCodes.Ldarg_2));
            dispatch.Add(Instruction.Create(OpCodes.Call, target));
            dispatch.Add(Instruction.Create(OpCodes.Stloc, resultLocal));
            dispatch.Add(Instruction.Create(OpCodes.Leave, epilogue));
        }

        var targets = new Instruction[10];
        AddCase("整合包种类：CurseForge", curseForge, true, true); targets[0] = CaseStart;
        AddCase("整合包种类：HMCL", hmcl, false, false); targets[1] = CaseStart;
        AddCase("整合包种类：MMC", mmc, false, false); targets[2] = CaseStart;
        AddCase("整合包种类：MCBBS", mcbbs, true, false); targets[3] = CaseStart;
        AddCase("整合包种类：Modrinth", modrinth, true, true); targets[4] = CaseStart;
        AddCase("整合包种类：带启动器的压缩包", launcherPack, false, false); targets[9] = CaseStart;

        // default (unrecognised) -> treat as a plain compressed pack
        var defaultStart = Instruction.Create(OpCodes.Ldstr, "整合包种类：未能识别，假定为压缩包");
        dispatch.Add(defaultStart);
        dispatch.Add(Instruction.Create(OpCodes.Ldc_I4_0));
        dispatch.Add(Instruction.Create(OpCodes.Ldstr, logPath));
        dispatch.Add(Instruction.Create(OpCodes.Call, loggerInfo));
        dispatch.Add(Instruction.Create(OpCodes.Ldarg_0));
        dispatch.Add(Instruction.Create(OpCodes.Ldloc, archiveLocal));
        dispatch.Add(Instruction.Create(OpCodes.Call, compress));
        dispatch.Add(Instruction.Create(OpCodes.Stloc, resultLocal));
        dispatch.Add(Instruction.Create(OpCodes.Leave, epilogue));

        brDefault.Operand = defaultStart;
        for (int i = 0; i < targets.Length; i++)
            if (targets[i] == null) targets[i] = defaultStart;
        sw.Operand = targets;

        // Rewrite the throw in place: the instruction object stays, so every "leave"
        // that targeted it now lands on the switch.
        ins[idx].OpCode = OpCodes.Ldloc;
        ins[idx].Operand = numLocal;
        ins.RemoveAt(idx + 2);   // throw
        ins.RemoveAt(idx + 1);   // newobj Exception
        for (int i = 0; i < dispatch.Count; i++)
            ins.Insert(idx + 1 + i, dispatch[i]);

        // Append the epilogue the compiler dropped, and keep it outside the finally.
        ins.Add(epilogue);
        ins.Add(epilogueRet);
        foreach (var h in body.ExceptionHandlers)
            if (h.HandlerEnd == null)
                h.HandlerEnd = epilogue;

        body.UpdateInstructionOffsets();
        return true;
    }

    private static bool IsModpackDispatchPatched(ModuleDefMD mod)
    {
        var method = FindModpackInstallWithArgs(mod);
        var body = method?.Body;
        if (body == null) return false;

        var hasSwitch = body.Instructions.Any(i => i.OpCode == OpCodes.Switch);
        if (!hasSwitch) return false;

        var called = body.Instructions.Where(i => i.OpCode == OpCodes.Call && i.Operand is IMethod)
            .Select(i => (IMethod)i.Operand)
            .Where(m => m.DeclaringType?.Name == "ModModpack")
            .Select(m => m.Name.String)
            .ToHashSet();

        return called.Contains("InstallPackCurseForge")
               && called.Contains("InstallPackModrinth")
               && called.Contains("InstallPackHMCL")
               && called.Contains("InstallPackMMC")
               && called.Contains("InstallPackMCBBS")
               && called.Contains("InstallPackLauncherPack")
               && called.Contains("InstallPackCompress")
               && !body.Instructions.Any(i => i.OpCode == OpCodes.Ldstr
                   && i.Operand is string s && s.Contains(DisabledHintFragment));
    }

    // ---- patch 3: restore the "导入整合包" entry --------------------------------

    /// <summary>
    /// ModpackInstall() was reduced to a red hint. Rebuild it as: file picker ->
    /// hand the path to the existing closure -> run it on a worker thread.
    /// </summary>
    private static bool PatchModpackDialog(ModuleDefMD mod)
    {
        var type = FindModModpack(mod);
        var method = FindModpackInstallNoArgs(mod);
        var body = method?.Body;
        if (type == null || body == null)
        {
            Diag("dialog: type/body null");
            return false;
        }
        if (IsModpackDialogPatched(mod))
        {
            Diag("dialog: already patched");
            return false;
        }

        var closureType = type.NestedTypes.FirstOrDefault(t => t.Name.String.StartsWith("_Closure$__0-0"));
        var closureCtor = closureType?.Methods.FirstOrDefault(m => m.IsConstructor && !m.IsStatic);
        var closureField = closureType?.Fields.FirstOrDefault(f => f.Name == "$VB$Local_File");
        var closureRun = closureType?.Methods.FirstOrDefault(m => m.Name.String.StartsWith("_Lambda$__0"));
        if (closureType == null || closureCtor == null || closureField == null || closureRun == null)
        {
            Diag($"dialog: closure type={closureType != null} ctor={closureCtor != null} field={closureField != null} run={closureRun != null}");
            return false;
        }

        var modBase = mod.GetTypes().FirstOrDefault(t => t.Name == "ModBase");
        var runInThread = modBase?.Methods.FirstOrDefault(m => m.Name == "RunInThread"
            && m.MethodSig.Params.Count == 1 && m.MethodSig.Params[0].FullName == "System.Action");
        if (runInThread == null)
        {
            Diag("dialog: RunInThread not found");
            return false;
        }

        var winFormsRef = mod.GetAssemblyRef("System.Windows.Forms");
        if (winFormsRef == null)
        {
            Diag("dialog: no WinForms ref");
            return false;
        }

        var ofdType = new TypeRefUser(mod, "System.Windows.Forms", "OpenFileDialog", winFormsRef);
        var dialogResultType = new TypeRefUser(mod, "System.Windows.Forms", "DialogResult", winFormsRef);
        var actionType = new TypeRefUser(mod, "System", "Action", mod.CorLibTypes.AssemblyRef);

        var ofdCtor = new MemberRefUser(mod, ".ctor", MethodSig.CreateInstance(mod.CorLibTypes.Void), ofdType);
        var setTitle = new MemberRefUser(mod, "set_Title",
            MethodSig.CreateInstance(mod.CorLibTypes.Void, mod.CorLibTypes.String), ofdType);
        var setFilter = new MemberRefUser(mod, "set_Filter",
            MethodSig.CreateInstance(mod.CorLibTypes.Void, mod.CorLibTypes.String), ofdType);
        var setCheckFile = new MemberRefUser(mod, "set_CheckFileExists",
            MethodSig.CreateInstance(mod.CorLibTypes.Void, mod.CorLibTypes.Boolean), ofdType);
        var showDialog = new MemberRefUser(mod, "ShowDialog",
            MethodSig.CreateInstance(new ValueTypeSig(dialogResultType)), ofdType);
        var getFileName = new MemberRefUser(mod, "get_FileName",
            MethodSig.CreateInstance(mod.CorLibTypes.String), ofdType);
        var actionCtor = new MemberRefUser(mod, ".ctor",
            MethodSig.CreateInstance(mod.CorLibTypes.Void, mod.CorLibTypes.Object, mod.CorLibTypes.IntPtr), actionType);

        var closureLocal = new Local(closureType.ToTypeSig());
        var dlgLocal = new Local(new ClassSig(ofdType));
        var fileLocal = new Local(mod.CorLibTypes.String);

        var retTarget = Instruction.Create(OpCodes.Ret);

        var code = new List<Instruction>
        {
            Instruction.Create(OpCodes.Newobj, closureCtor),
            Instruction.Create(OpCodes.Stloc, closureLocal),
            Instruction.Create(OpCodes.Newobj, ofdCtor),
            Instruction.Create(OpCodes.Stloc, dlgLocal),
            Instruction.Create(OpCodes.Ldloc, dlgLocal),
            Instruction.Create(OpCodes.Ldstr, "选择整合包压缩文件"),
            Instruction.Create(OpCodes.Callvirt, setTitle),
            Instruction.Create(OpCodes.Ldloc, dlgLocal),
            Instruction.Create(OpCodes.Ldstr, "整合包文件|*.zip;*.mrpack;*.rar|所有文件|*.*"),
            Instruction.Create(OpCodes.Callvirt, setFilter),
            Instruction.Create(OpCodes.Ldloc, dlgLocal),
            Instruction.Create(OpCodes.Ldc_I4_1),
            Instruction.Create(OpCodes.Callvirt, setCheckFile),
            Instruction.Create(OpCodes.Ldloc, dlgLocal),
            Instruction.Create(OpCodes.Callvirt, showDialog),
            Instruction.Create(OpCodes.Ldc_I4_1),          // DialogResult.OK
            Instruction.Create(OpCodes.Bne_Un, retTarget),
            Instruction.Create(OpCodes.Ldloc, dlgLocal),
            Instruction.Create(OpCodes.Callvirt, getFileName),
            Instruction.Create(OpCodes.Stloc, fileLocal),
            Instruction.Create(OpCodes.Ldloc, closureLocal),
            Instruction.Create(OpCodes.Ldloc, fileLocal),
            Instruction.Create(OpCodes.Stfld, closureField),
            Instruction.Create(OpCodes.Ldloc, closureLocal),
            Instruction.Create(OpCodes.Ldftn, closureRun),
            Instruction.Create(OpCodes.Newobj, actionCtor),
            Instruction.Create(OpCodes.Call, runInThread),
            retTarget
        };

        body.Instructions.Clear();
        body.ExceptionHandlers.Clear();
        body.Variables.Clear();
        body.Variables.Add(closureLocal);
        body.Variables.Add(dlgLocal);
        body.Variables.Add(fileLocal);
        foreach (var i in code) body.Instructions.Add(i);
        body.MaxStack = 8;
        body.UpdateInstructionOffsets();
        return true;
    }

    private static bool IsModpackDialogPatched(ModuleDefMD mod)
    {
        var method = FindModpackInstallNoArgs(mod);
        var body = method?.Body;
        if (body == null) return false;

        return !body.Instructions.Any(i => i.OpCode == OpCodes.Ldstr
                   && i.Operand is string s && s.Contains(DisabledHintFragment))
               && body.Instructions.Any(i => i.OpCode == OpCodes.Newobj && i.Operand is IMethod m
                   && m.DeclaringType?.Name == "OpenFileDialog");
    }

    // ---- patch 4: keep the fix alive across self-updates -----------------------

    private static bool PatchUpdateRestart(ModuleDefMD mod)
    {
        var type = mod.GetTypes().FirstOrDefault(x => x.Name == "ModSecret");
        var method = type?.Methods.FirstOrDefault(x => x.Name == "UpdateRestart");
        var body = method?.Body;
        if (body == null)
            return false;

        var hits = body.Instructions
            .Where(i => i.OpCode == OpCodes.Ldstr && i.Operand is string s && s == UpdaterPathLiteral)
            .ToList();

        if (hits.Count == 0)
            return false;
        if (hits.Count != 1)
            return false;

        hits[0].Operand = RedirectName;
        return true;
    }

    // ---- helpers ---------------------------------------------------------------

    private static string JoinArgs(string[] args)
    {
        return string.Join(" ", args.Select(Quote));
    }

    private static string Quote(string value)
    {
        if (value.Length == 0) return "\"\"";
        return value.IndexOfAny(new[] { ' ', '\t', '"' }) >= 0
            ? "\"" + value.Replace("\"", "\\\"") + "\""
            : value;
    }

    private static void Notify(string message)
    {
        WinForms.MessageBox.Show(message, "PCL 下载功能修复", WinForms.MessageBoxButtons.OK,
            WinForms.MessageBoxIcon.Information);
    }
}

