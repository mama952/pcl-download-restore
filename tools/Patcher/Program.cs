using System;
using System.Collections.Generic;
using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

internal static class Program
{
    private static string Fmt(object? op)
    {
        return op switch
        {
            null => "",
            IMethod m => m.FullName,
            IField f => f.FullName,
            IType t => t.FullName,
            string s => "\"" + s + "\"",
            _ => op.ToString() ?? ""
        };
    }

    private static void Dump(ModuleDefMD mod, string typeName, string methodName)
    {
        var t = mod.GetTypes().FirstOrDefault(x => x.Name == typeName);
        if (t == null)
        {
            Console.WriteLine($"### TYPE NOT FOUND: {typeName}");
            return;
        }
        var methods = t.Methods.Where(x => x.Name == methodName).ToList();
        if (methods.Count == 0)
        {
            Console.WriteLine($"### METHOD NOT FOUND: {typeName}::{methodName}");
            return;
        }
        foreach (var m in methods)
        {
            Console.WriteLine($"===== {t.FullName}::{m.Name}  sig={m.MethodSig}");
            if (!m.HasBody)
            {
                Console.WriteLine("  <no body>");
                continue;
            }
            Console.WriteLine($"  exceptionHandlers={m.Body.ExceptionHandlers.Count} maxStack={m.Body.MaxStack}");
            foreach (var i in m.Body.Instructions)
                Console.WriteLine($"  IL_{i.Offset:X4}  {i.OpCode.Code,-14} {Fmt(i.Operand)}");
        }
    }

    // Replaces the fake "关闭下载" LoaderTask construction with a direct reference
    // to the real ModDownload.DlClientListLoader field (stack effect is identical).
    private static int PatchLoaderInit(ModuleDefMD mod)
    {
        var type = mod.GetTypes().FirstOrDefault(x => x.Name == "PageDownloadInstall");
        if (type == null) { Console.WriteLine("FATAL: PageDownloadInstall not found"); return 1; }
        var method = type.Methods.FirstOrDefault(x => x.Name == "LoaderInit");
        if (method == null || !method.HasBody) { Console.WriteLine("FATAL: LoaderInit not found"); return 1; }

        var body = method.Body;
        var instrs = body.Instructions;

        int start = -1, end = -1;
        for (int i = 0; i < instrs.Count; i++)
        {
            if (start < 0 && instrs[i].OpCode == OpCodes.Ldstr && instrs[i].Operand is string s && s == "关闭下载")
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
        if (start < 0 || end < 0)
        {
            Console.WriteLine($"FATAL: pattern not found (start={start} end={end})");
            return 1;
        }
        Console.WriteLine($"replacing instructions [{start}..{end}]  IL_{instrs[start].Offset:X4}..IL_{instrs[end].Offset:X4}");

        // Any branch target inside the replaced range would make removal unsafe.
        var range = new HashSet<Instruction>();
        for (int i = start; i <= end; i++) range.Add(instrs[i]);
        for (int i = 0; i < instrs.Count; i++)
        {
            if (i >= start && i <= end) continue;
            var op = instrs[i].Operand;
            if (op is Instruction target && range.Contains(target))
            {
                Console.WriteLine($"FATAL: external branch at IL_{instrs[i].Offset:X4} targets replaced range");
                return 1;
            }
        }

        var modDownload = mod.GetTypes().FirstOrDefault(x => x.Name == "ModDownload");
        if (modDownload == null) { Console.WriteLine("FATAL: ModDownload not found"); return 1; }
        var field = modDownload.Fields.FirstOrDefault(f => f.Name == "DlClientListLoader");
        if (field == null) { Console.WriteLine("FATAL: DlClientListLoader not found"); return 1; }
        Console.WriteLine($"field: {field.FullName}  type={field.FieldSig}");

        for (int i = end; i >= start; i--) instrs.RemoveAt(i);
        instrs.Insert(start, Instruction.Create(OpCodes.Ldsfld, field));

        body.UpdateInstructionOffsets();
        Console.WriteLine("patched LoaderInit");
        return 0;
    }

    // Redirects the updater launch through the restore tool so the freshly built
    // updater is itself patched before it replaces the main executable.
    private static bool PatchUpdateRestart(ModuleDefMD mod, string redirectTo)
    {
        var type = mod.GetTypes().FirstOrDefault(x => x.Name == "ModSecret");
        if (type == null) { Console.WriteLine("FATAL: ModSecret not found"); return false; }
        var method = type.Methods.FirstOrDefault(x => x.Name == "UpdateRestart");
        if (method == null || !method.HasBody) { Console.WriteLine("FATAL: UpdateRestart not found"); return false; }

        var hits = method.Body.Instructions
            .Where(i => i.OpCode == OpCodes.Ldstr && i.Operand is string s && s == @"PCL\Plain Craft Launcher 2.exe")
            .ToList();
        if (hits.Count != 1)
        {
            Console.WriteLine($"FATAL: expected exactly 1 updater-path literal, found {hits.Count}");
            return false;
        }
        hits[0].Operand = redirectTo;
        Console.WriteLine($"UpdateRestart: updater path -> \"{redirectTo}\"");
        return true;
    }

    private static bool IsLoaderInitPatched(ModuleDefMD mod)
    {
        var type = mod.GetTypes().FirstOrDefault(x => x.Name == "PageDownloadInstall");
        var method = type?.Methods.FirstOrDefault(x => x.Name == "LoaderInit");
        if (method?.Body == null) return false;
        return method.Body.Instructions.Any(i => i.OpCode == OpCodes.Ldsfld
            && i.Operand is IField f && f.Name == "DlClientListLoader");
    }

    private static IMethod GetExceptionCtor(ModuleDefMD mod)
    {
        var typeRef = mod.CorLibTypes.GetTypeRef("System", "Exception");
        var td = typeRef.ResolveTypeDef();
        return td?.Methods.FirstOrDefault(m => m.Name == ".ctor"
            && m.MethodSig?.Params.Count == 1
            && m.MethodSig.Params[0].FullName == "System.String");
    }

    private static IField FindDeadLoaderField(ModuleDefMD mod)
    {
        // Any static field of the same LoaderTask<T> type that the page never feeds
        // with a real payload reproduces "no version list" without new type refs.
        var dlType = mod.GetTypes().FirstOrDefault(x => x.Name == "ModDownload");
        return dlType?.Fields.FirstOrDefault(f => f.Name == "DlClientListBmclapiLoader");
    }

    private static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("usage: Patcher <assembly> <dump|dump-update|patch|patch2> [outPath]");
            return 2;
        }
        var mod = ModuleDefMD.Load(args[0]);
        Console.WriteLine($"module: {mod.Name}  types={mod.GetTypes().Count()}");

        switch (args[1])
        {
            case "dump":
                Dump(mod, "PageDownloadInstall", "LoaderInit");
                Console.WriteLine();
                Dump(mod, "ModModpack", "ModpackInstall");
                return 0;

            case "dump-update":
                Dump(mod, "ModSecret", "UpdateRestart");
                return 0;

            case "unpatch":
            {
                // Revert both patches, simulating an official build that would
                // overwrite the fix. Used to verify auto-repair behaviour.
                var loaderType = mod.GetTypes().FirstOrDefault(x => x.Name == "PageDownloadInstall");
                var loaderInit = loaderType?.Methods.FirstOrDefault(x => x.Name == "LoaderInit");
                var modified = false;
                if (loaderInit?.Body != null)
                {
                    var body = loaderInit.Body;
                    var fld = body.Instructions.FirstOrDefault(i => i.OpCode == OpCodes.Ldsfld
                        && i.Operand is IField f && f.Name == "DlClientListLoader");
                    if (fld != null)
                    {
                        var currentField = fld.Operand as IField;
                        var deadLoader = FindDeadLoaderField(mod);
                        if (deadLoader == null)
                        {
                            Console.WriteLine("LoaderInit: no substitute field found");
                        }
                        else if (currentField?.FieldSig?.Type?.FullName != deadLoader.FieldSig?.Type?.FullName)
                        {
                            Console.WriteLine($"LoaderInit: substitute type mismatch ({deadLoader.FieldSig?.Type?.FullName})");
                        }
                        else
                        {
                            fld.Operand = deadLoader;
                            modified = true;
                            Console.WriteLine($"LoaderInit: pointed at '{deadLoader.Name}' instead of real loader");
                        }
                    }
                }

                var secret = mod.GetTypes().FirstOrDefault(x => x.Name == "ModSecret");
                var updater = secret?.Methods.FirstOrDefault(x => x.Name == "UpdateRestart");
                if (updater?.Body != null)
                {
                    var hit = updater.Body.Instructions.FirstOrDefault(i => i.OpCode == OpCodes.Ldstr
                        && i.Operand is string s && s == "PCL-Restore.exe");
                    if (hit != null)
                    {
                        hit.Operand = @"PCL\Plain Craft Launcher 2.exe";
                        modified = true;
                        Console.WriteLine("UpdateRestart: reverted to default updater path");
                    }
                }

                if (!modified) { Console.WriteLine("nothing to revert"); return 1; }
                var outp = args.Length > 2 ? args[2] : args[0] + ".unpatched";
                mod.Write(outp);
                Console.WriteLine($"written: {outp}");
                return 0;
            }

            case "patch":
            {
                var rc = PatchLoaderInit(mod);
                if (rc != 0) return rc;
                var outPath = args.Length > 2 ? args[2] : args[0] + ".patched";
                mod.Write(outPath);
                Console.WriteLine($"written: {outPath}");
                return 0;
            }

            case "patch2":
            {
                Console.WriteLine($"already patched (LoaderInit): {IsLoaderInitPatched(mod)}");
                if (!IsLoaderInitPatched(mod) && PatchLoaderInit(mod) != 0) return 1;
                if (!PatchUpdateRestart(mod, "PCL-Restore.exe")) return 1;
                var outPath = args.Length > 2 ? args[2] : args[0] + ".patched";
                mod.Write(outPath);
                Console.WriteLine($"written: {outPath}");
                return 0;
            }

            default:
                Console.WriteLine("unknown mode");
                return 2;
        }
    }
}
