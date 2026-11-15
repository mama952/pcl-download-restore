using System;
using System.Collections.Generic;
using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

// Restores features removed in PCL 2.99.0.1:
//   A. PageDownloadLeft .ctor hides ItemInstall / LabResource and defaults to the Mod page.
//   B. ModModpack.ModpackInstall ends in an unconditional throw instead of dispatching
//      to the InstallPack* implementations (all seven are still present).
internal static class Program
{
    private static string F(object? o) => o switch
    {
        null => "",
        IMethod m => m.FullName,
        IField f => f.FullName,
        string s => "\"" + s + "\"",
        Instruction t => $"IL_{t.Offset:X4}",
        _ => o.ToString() ?? ""
    };

    private static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("usage: Restore2991 <exe> <dump|patch> [out]");
            return 2;
        }

        var mod = ModuleDefMD.Load(args[0]);
        if (args[1] == "dump") { Dump(mod); return 0; }

        int rc = 0;
        rc |= FixDownloadPage(mod) ? 0 : 1;
        rc |= FixModpackInstall(mod) ? 0 : 1;

        var outp = args.Length > 2 ? args[2] : args[0] + ".fixed";
        var opts = new dnlib.DotNet.Writer.ModuleWriterOptions(mod);
        opts.MetadataOptions.Flags |= dnlib.DotNet.Writer.MetadataFlags.KeepOldMaxStack;
        mod.Write(outp, opts);
        Console.WriteLine($"written: {outp}");
        return rc == 0 ? 0 : 1;
    }

    // ---- A: restore the "install vanilla" entry -------------------------------
    private static bool FixDownloadPage(ModuleDefMD mod)
    {
        var t = mod.GetTypes().FirstOrDefault(x => x.Name == "PageDownloadLeft");
        var ctor = t?.Methods.FirstOrDefault(x => x.Name == ".ctor");
        var body = ctor?.Body;
        if (body == null) { Console.WriteLine("[A] ctor not found"); return false; }

        var ins = body.Instructions;

        // Locate the injected block: set_ItemInstall.Visibility ... set_ItemMod.Checked
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
        if (start < 0 || end < 0) { Console.WriteLine("[A] injected block not found"); return false; }

        // Walk back one instruction: the block starts with its own ldarg.0.
        if (start - 1 >= 0 && ins[start - 1].OpCode == OpCodes.Ldarg_0) start--;

        // Refuse if anything outside the span branches in.
        var span = new HashSet<Instruction>();
        for (int i = start; i <= end; i++) span.Add(ins[i]);
        for (int i = 0; i < ins.Count; i++)
        {
            if (i >= start && i <= end) continue;
            if (ins[i].Operand is Instruction tgt && span.Contains(tgt))
            {
                Console.WriteLine("[A] branch into span, aborting");
                return false;
            }
        }

        for (int i = end; i >= start; i--) ins.RemoveAt(i);
        body.UpdateInstructionOffsets();
        Console.WriteLine($"[A] removed cripple block: {end - start + 1} instructions");
        return true;
    }

    // ---- B: restore modpack dispatch -----------------------------------------
    private static bool FixModpackInstall(ModuleDefMD mod)
    {
        var type = mod.GetTypes().FirstOrDefault(x => x.Name == "ModModpack");
        var method = type?.Methods.FirstOrDefault(x => x.Name == "ModpackInstall" && x.Parameters.Count == 3);
        var body = method?.Body;
        if (body == null) { Console.WriteLine("[B] ModpackInstall not found"); return false; }

        var ins = body.Instructions;

        // Find the injected throw: Ldstr <restriction text> ; Newobj Exception(string) ; Throw
        int throwIdx = -1;
        for (int i = 0; i + 2 < ins.Count; i++)
        {
            if (ins[i].OpCode == OpCodes.Ldstr && ins[i].Operand is string s
                && s.Contains("不再提供 MC 下载"))
            {
                if (ins[i + 1].OpCode == OpCodes.Newobj && ins[i + 2].OpCode == OpCodes.Throw)
                {
                    throwIdx = i;
                    break;
                }
            }
        }
        if (throwIdx < 0) { Console.WriteLine("[B] injected throw not found"); return false; }

        // Resolve the seven implementations.
        IMethod Need(string name, int argc)
        {
            var md = type.Methods.FirstOrDefault(x => x.Name == name && x.Parameters.Count == argc);
            if (md == null) throw new InvalidOperationException($"{name}/{argc} missing");
            return md;
        }
        var cf = Need("InstallPackCurseForge", 5);
        var mr = Need("InstallPackModrinth", 5);
        var hmcl = Need("InstallPackHMCL", 3);
        var mmc = Need("InstallPackMMC", 3);
        var mcbbs = Need("InstallPackMCBBS", 4);
        var lp = Need("InstallPackLauncherPack", 3);
        var cmp = Need("InstallPackCompress", 2);

        // Append the shared return point after the existing Endfinally.
        int endFinally = -1;
        for (int i = ins.Count - 1; i >= 0; i--)
            if (ins[i].OpCode == OpCodes.Endfinally) { endFinally = i; break; }
        if (endFinally < 0) { Console.WriteLine("[B] Endfinally not found"); return false; }

        var retPoint = Instruction.Create(OpCodes.Ldloc_0);
        var retInstr = Instruction.Create(OpCodes.Ret);
        ins.Insert(endFinally + 1, retPoint);
        ins.Insert(endFinally + 2, retInstr);

        // Build dispatch at the throw site.
        //   ldloc.s V_4 (num)
        //   switch (L0,L1,L2,L3,L4,LDF,LDF,LDF,LDF,L9)
        var l0 = Instruction.Create(OpCodes.Ldarg_0);
        var l1 = Instruction.Create(OpCodes.Ldarg_0);
        var l2 = Instruction.Create(OpCodes.Ldarg_0);
        var l3 = Instruction.Create(OpCodes.Ldarg_0);
        var l4 = Instruction.Create(OpCodes.Ldarg_0);
        // Indices 5..8 must not share a single Instruction object with 9:
        // dnlib's switch writer mishandles repeated references.
        var l5 = Instruction.Create(OpCodes.Ldarg_0);
        var l6 = Instruction.Create(OpCodes.Ldarg_0);
        var l7 = Instruction.Create(OpCodes.Ldarg_0);
        var l8 = Instruction.Create(OpCodes.Ldarg_0);
        var l9 = Instruction.Create(OpCodes.Ldarg_0);

        var block = new List<Instruction>();
        block.Add(Instruction.Create(OpCodes.Ldloc, method.Body.Variables[4]));
        block.Add(Instruction.Create(OpCodes.Switch, new[] { l0, l1, l2, l3, l4, l5, l6, l7, l8, l9 }));

        // default -> InstallPackCompress(File, Archive)
        block.AddRange(new[]
        {
            Instruction.Create(OpCodes.Ldarg_0),
            Instruction.Create(OpCodes.Ldloc, method.Body.Variables[1]),
            Instruction.Create(OpCodes.Call, cmp),
            Instruction.Create(OpCodes.Stloc_0),
            Instruction.Create(OpCodes.Leave, retPoint)
        });

        void Emit(Instruction label, IMethod target, bool wantBase, bool wantInstance, bool wantLogo)
        {
            block.Add(label);
            block.Add(Instruction.Create(OpCodes.Ldarg_0));                                  // File
            block.Add(Instruction.Create(OpCodes.Ldloc, method.Body.Variables[1]));          // Archive
            if (wantBase)
                block.Add(Instruction.Create(OpCodes.Ldloc, method.Body.Variables[2]));      // BaseFolder
            if (wantInstance)
                block.Add(Instruction.Create(OpCodes.Ldarg_1));                              // InstanceName
            if (wantLogo)
                block.Add(Instruction.Create(OpCodes.Ldarg_2));                              // Logo
            block.Add(Instruction.Create(OpCodes.Call, target));
            block.Add(Instruction.Create(OpCodes.Stloc_0));
            block.Add(Instruction.Create(OpCodes.Leave, retPoint));
        }

        Emit(l0, cf, true, true, true);
        Emit(l1, hmcl, true, false, false);
        Emit(l2, mmc, true, false, false);
        Emit(l3, mcbbs, true, true, false);
        Emit(l4, mr, true, true, true);
        // Unrecognised ids land on Compress(File, Archive). Each index gets its
        // own instruction sequence so no Instruction object is shared.
        Emit(l5, cmp, false, false, false);
        Emit(l6, cmp, false, false, false);
        Emit(l7, cmp, false, false, false);
        Emit(l8, cmp, false, false, false);
        Emit(l9, lp, true, false, false);

        // Insert the dispatch immediately BEFORE the injected throw, leaving the
        // three original instructions untouched (unreachable). They may sit on an
        // exception-handler boundary, and dnlib rejects removing those.
        for (int i = block.Count - 1; i >= 0; i--) ins.Insert(throwIdx, block[i]);

        // Widening the body can push existing short branches out of range.
        // Convert them in place instead of calling SimplifyBranches, which
        // rebuilds instructions and would break exception-handler boundaries.
        int widened = 0;
        foreach (var i in ins)
        {
            if (i.Operand is Instruction)
            {
                var lg = LongOf(i.OpCode);
                if (lg.Code != i.OpCode.Code) { i.OpCode = lg; widened++; }
            }
        }
        body.UpdateInstructionOffsets();
        Console.WriteLine($"[B] restored dispatch: {block.Count} instructions, widened {widened} short branches");

        // ---- integrity check: every referenced instruction must be in the body ----
        var set = new HashSet<Instruction>(ins);
        int bad = 0;
        foreach (var i in ins)
        {
            if (i.Operand is Instruction t && !set.Contains(t))
            {
                Console.WriteLine($"  !! dangling branch target from {i.OpCode.Code} -> IL_{t.Offset:X4}");
                bad++;
            }
            if (i.Operand is Instruction[] arr)
                foreach (var t2 in arr)
                    if (!set.Contains(t2)) { Console.WriteLine($"  !! dangling switch target IL_{t2.Offset:X4}"); bad++; }
        }
        foreach (var h in body.ExceptionHandlers)
        {
            foreach (var (name, t) in new[] { ("TryStart", h.TryStart), ("TryEnd", h.TryEnd), ("HandlerStart", h.HandlerStart), ("HandlerEnd", h.HandlerEnd), ("FilterStart", h.FilterStart) })
            {
                if (t != null && !set.Contains(t))
                {
                    Console.WriteLine($"  !! handler {name} not in body: IL_{t.Offset:X4}");
                    bad++;
                }
            }
        }
        Console.WriteLine(bad == 0 ? "[B] integrity OK" : $"[B] integrity FAILED ({bad})");
        return true;
    }

    private static OpCode LongOf(OpCode c) => c.Code switch
    {
        Code.Br_S => OpCodes.Br,
        Code.Brfalse_S => OpCodes.Brfalse,
        Code.Brtrue_S => OpCodes.Brtrue,
        Code.Beq_S => OpCodes.Beq,
        Code.Bge_S => OpCodes.Bge,
        Code.Bgt_S => OpCodes.Bgt,
        Code.Ble_S => OpCodes.Ble,
        Code.Blt_S => OpCodes.Blt,
        Code.Bne_Un_S => OpCodes.Bne_Un,
        Code.Bge_Un_S => OpCodes.Bge_Un,
        Code.Bgt_Un_S => OpCodes.Bgt_Un,
        Code.Ble_Un_S => OpCodes.Ble_Un,
        Code.Blt_Un_S => OpCodes.Blt_Un,
        Code.Leave_S => OpCodes.Leave,
        _ => c
    };

    private static void Dump(ModuleDefMD mod)
    {
        var t = mod.GetTypes().FirstOrDefault(x => x.Name == "PageDownloadLeft");
        Console.WriteLine("=== PageDownloadLeft .ctor ===");
        foreach (var i in t?.Methods.First(x => x.Name == ".ctor").Body!.Instructions ?? new List<Instruction>())
            Console.WriteLine($"  IL_{i.Offset:X4}  {i.OpCode.Code,-14} {F(i.Operand)}");

        var mm = mod.GetTypes().FirstOrDefault(x => x.Name == "ModModpack")
            .Methods.FirstOrDefault(x => x.Name == "ModpackInstall" && x.Parameters.Count == 3);
        Console.WriteLine("\n=== ModpackInstall tail ===");
        var ins = mm!.Body!.Instructions;
        for (int k = Math.Max(0, ins.Count - 14); k < ins.Count; k++)
            Console.WriteLine($"  [{k}] IL_{ins[k].Offset:X4}  {ins[k].OpCode.Code,-14} {F(ins[k].Operand)}");
    }
}
