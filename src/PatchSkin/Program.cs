using System;
using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

internal static class Program
{
    private static string Fmt(object? op) => op switch
    {
        null => "",
        IMethod m => m.FullName,
        IField f => f.FullName,
        string s => "\"" + s + "\"",
        _ => op.ToString() ?? ""
    };

    private static MethodDef? FindSkinMethod(ModuleDefMD mod)
    {
        var t = mod.GetTypes().FirstOrDefault(x => x.Name == "ModLaunch");
        return t?.Methods.FirstOrDefault(x => x.Body != null &&
            x.Body.Instructions.Any(i => i.OpCode == OpCodes.Ldstr
                && i.Operand is string s && s.Contains("自定义皮肤资源包")));
    }

    private static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("usage: PatchSkin <exe> <dump|patch> [out]");
            return 2;
        }

        var mod = ModuleDefMD.Load(args[0]);
        var method = FindSkinMethod(mod);
        if (method?.Body == null)
        {
            Console.WriteLine("FATAL: skin resourcepack builder not found");
            return 1;
        }
        Console.WriteLine($"target method: {method.Name}  instrs={method.Body.Instructions.Count}");

        if (args[1] == "dump")
        {
            DumpSkinRegion(method);
            return 0;
        }

        if (args[1] != "patch")
        {
            Console.WriteLine("unknown mode");
            return 2;
        }

        var body = method.Body;
        var instrs = body.Instructions;

        var majorRef = FindGetter(mod, "System.Version", "get_Major");
        var minorRef = FindGetter(mod, "System.Version", "get_Minor");
        if (majorRef == null || minorRef == null)
        {
            Console.WriteLine("FATAL: cannot resolve Version getters");
            return 1;
        }

        // Fix 1: rewrite ONLY the pack_format dispatch. Its shape is unique:
        //     callvirt get_Major ; ldc.i4.6 ; sub ; switch(...)
        // Touching every get_Major in this method would corrupt unrelated
        // version checks that legitimately test Major. Minor carries the real
        // MC major number because MC versions parse as "1.X.Y".
        int minorCount = 0;
        for (int i = 0; i < instrs.Count; i++)
        {
            var ins = instrs[i];
            if (ins.OpCode != OpCodes.Callvirt) continue;
            if (ins.Operand is not IMethod m) continue;
            if (m.Name != "get_Major" || m.DeclaringType?.FullName != "System.Version") continue;

            // Confirm the dispatch pattern follows within a few instructions
            // (a Stloc/Ldloc pair sits between get_Major and ldc.i4.6).
            bool isDispatch = false;
            for (int j = i + 1; j <= i + 4 && j < instrs.Count; j++)
            {
                if (instrs[j].OpCode != OpCodes.Ldc_I4_6) continue;
                for (int k = j + 1; k <= j + 2 && k < instrs.Count; k++)
                {
                    if (instrs[k].OpCode == OpCodes.Sub
                        && k + 1 < instrs.Count && instrs[k + 1].OpCode == OpCodes.Switch)
                    {
                        isDispatch = true;
                    }
                }
            }
            if (!isDispatch) continue;

            ins.Operand = minorRef;
            minorCount++;
        }
        Console.WriteLine($"fix1: pack_format dispatch get_Major -> get_Minor  ({minorCount} site(s))");

        // Fix 2: the "1.19.3+" gate compares against new Version(19,0,3), which a
        // real "1.19.3" can never satisfy. Rewrite it to test Minor >= 19.
        int gateCount = ReplaceVersionGate(method, minorRef);
        Console.WriteLine($"fix2: 1.19.3+ skin-path gate rewritten  ({gateCount} site(s))");

        body.UpdateInstructionOffsets();

        var outp = args.Length > 2 ? args[2] : args[0] + ".skinfixed";
        var opts = new dnlib.DotNet.Writer.ModuleWriterOptions(mod);
        opts.MetadataOptions.Flags |= dnlib.DotNet.Writer.MetadataFlags.KeepOldMaxStack;
        mod.Write(outp, opts);
        Console.WriteLine($"written: {outp}");
        return 0;
    }

    private static IMethod? FindGetter(ModuleDefMD mod, string type, string name)
    {
        foreach (var r in mod.GetMemberRefs())
        {
            if (r is MemberRef mr && mr.Name == name && mr.Class?.FullName == type)
                return mr;
        }
        foreach (var tr in mod.GetTypeRefs().Where(t => t.FullName == type))
        {
            var td = tr.ResolveTypeDef();
            var m = td?.Methods.FirstOrDefault(x => x.Name == name);
            if (m != null) return m;
        }
        return null;
    }

    /// <summary>
    /// Finds "new Version(19,0,3)" followed by Version::op_GreaterThanOrEqual and
    /// replaces the comparison with (Minor >= 19). Real MC versions parse as
    /// Major=1, Minor=19, Build=3 so Minor is the meaningful field.
    /// </summary>
    private static int ReplaceVersionGate(MethodDef method, IMethod minorRef)
    {
        var instrs = method.Body.Instructions;
        int fixedCount = 0;
        for (int i = 0; i < instrs.Count; i++)
        {
            if (instrs[i].OpCode != OpCodes.Call) continue;
            if (instrs[i].Operand is not IMethod m) continue;
            if (m.Name != "op_GreaterThanOrEqual") continue;
            if (m.DeclaringType?.FullName != "System.Version") continue;

            int ctorIdx = -1;
            for (int j = i - 1; j >= 0 && j > i - 16; j--)
            {
                if (instrs[j].OpCode == OpCodes.Newobj && instrs[j].Operand is IMethod ctor
                    && ctor.DeclaringType?.FullName == "System.Version")
                {
                    ctorIdx = j;
                    break;
                }
            }
            if (ctorIdx < 0) continue;

            bool is1903 = false;
            for (int k = Math.Max(0, ctorIdx - 6); k < ctorIdx; k++)
            {
                if (instrs[k].OpCode == OpCodes.Ldc_I4_S && instrs[k].Operand is sbyte sb && sb == 19)
                    is1903 = true;
                if (instrs[k].OpCode == OpCodes.Ldc_I4 && instrs[k].Operand is int v && v == 19)
                    is1903 = true;
            }
            if (!is1903) continue;

            // The three argument pushes sit BEFORE Newobj: [ctorIdx-3 .. ctorIdx-1].
            // Start the replacement there so no stray literals are left on the stack.
            int argsStart = ctorIdx;
            for (int k = ctorIdx - 1; k >= 0 && k >= ctorIdx - 3; k--)
            {
                var oc = instrs[k].OpCode;
                if (oc == OpCodes.Ldc_I4 || oc == OpCodes.Ldc_I4_S || oc == OpCodes.Ldc_I4_0
                    || oc == OpCodes.Ldc_I4_1 || oc == OpCodes.Ldc_I4_2 || oc == OpCodes.Ldc_I4_3
                    || oc == OpCodes.Ldc_I4_4 || oc == OpCodes.Ldc_I4_5 || oc == OpCodes.Ldc_I4_6
                    || oc == OpCodes.Ldc_I4_7 || oc == OpCodes.Ldc_I4_8 || oc == OpCodes.Ldc_I4_M1)
                {
                    argsStart = k;
                }
            }

            // Stack before: ..., Vanilla (System.Version)
            // Rewrite [argsStart .. i] to: callvirt get_Minor ; ldc.i4.s 19 ; clt ; ldc.i4.0 ; ceq
            for (int k = i; k >= argsStart; k--) instrs.RemoveAt(k);
            instrs.Insert(argsStart, Instruction.Create(OpCodes.Callvirt, minorRef));
            instrs.Insert(argsStart + 1, Instruction.Create(OpCodes.Ldc_I4_S, (sbyte)19));
            instrs.Insert(argsStart + 2, Instruction.Create(OpCodes.Clt));
            instrs.Insert(argsStart + 3, Instruction.Create(OpCodes.Ldc_I4_0));
            instrs.Insert(argsStart + 4, Instruction.Create(OpCodes.Ceq));
            fixedCount++;
            i = argsStart + 4;
        }
        return fixedCount;
    }

    private static void DumpSkinRegion(MethodDef method)
    {
        var instrs = method.Body.Instructions;
        int marker = instrs.IndexOf(instrs.First(x => x.OpCode == OpCodes.Ldstr
            && x.Operand is string s && s.Contains("自定义皮肤资源包")));
        int from = Math.Max(0, marker - 70);
        int to = Math.Min(instrs.Count - 1, marker + 60);
        for (int i = from; i <= to; i++)
        {
            var ins = instrs[i];
            Console.WriteLine($"  IL_{ins.Offset:X4}  {ins.OpCode.Code,-14} {Fmt(ins.Operand)}");
        }
    }
}
