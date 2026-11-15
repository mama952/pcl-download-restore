using System;
using System.Collections.Generic;
using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

// Small inspection helper used while developing the modpack patch.
// usage:
//   Inspect <exe> refs                 -> list assembly refs
//   Inspect <exe> type <Name>          -> list methods of a type (short names + signatures)
//   Inspect <exe> il <Type>::<Method>  -> dump IL with locals and exception handlers
//   Inspect <exe> find <text>          -> find ldstr operands containing text
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length < 3)
        {
            Console.WriteLine("usage: Inspect <exe> refs|type <name>|il <Type>::<Method>|find <text>");
            return 1;
        }

        var mod = ModuleDefMD.Load(args[0]);
        switch (args[1])
        {
            case "refs":
                foreach (var r in mod.GetAssemblyRefs())
                    Console.WriteLine($"{r.Name} {r.Version}");
                break;

            case "type":
            {
                var t = mod.GetTypes().FirstOrDefault(x => x.Name == args[2] || x.FullName == args[2]);
                if (t == null) { Console.WriteLine("type not found"); return 2; }
                Console.WriteLine($"type {t.FullName} base={t.BaseType?.FullName}");
                foreach (var m in t.Methods)
                    Console.WriteLine($"  {(m.IsStatic ? "static " : "")}{m.Name} :: {m.MethodSig}");
                foreach (var f in t.Fields)
                    Console.WriteLine($"  field {f.Name} :: {f.FieldSig}");
                break;
            }

            case "il":
            {
                var parts = args[2].Split(new[] { "::" }, StringSplitOptions.None);
                var t = mod.GetTypes().FirstOrDefault(x => x.Name == parts[0] || x.FullName == parts[0]);
                var ms = t?.Methods.Where(x => x.Name == parts[1]).ToList();
                if (ms == null || ms.Count == 0) { Console.WriteLine("method not found"); return 2; }
                foreach (var m in ms)
                    Dump(m);
                break;
            }

            case "res":
            {
                foreach (var r in mod.Resources)
                    Console.WriteLine($"resource {r.Name} ({r.ResourceType})");
                break;
            }

            case "mref":
            {
                foreach (var t in mod.GetTypes())
                {
                    if (!t.Name.String.Contains(args[2]) && !t.FullName.Contains(args[2])) continue;
                    foreach (var m in t.Methods)
                        Console.WriteLine($"def {t.FullName}::{m.Name} {m.MethodSig}");
                }
                foreach (var mr in mod.GetMemberRefs())
                {
                    if (mr.Name.String.Contains(args[2]))
                        Console.WriteLine($"ref {mr.DeclaringType?.FullName}::{mr.Name} {mr.MethodSig}");
                }
                break;
            }

            case "find":
            {
                foreach (var t in mod.GetTypes())
                foreach (var m in t.Methods)
                {
                    if (m.Body == null) continue;
                    if (m.Body.Instructions.Any(i => i.OpCode == OpCodes.Ldstr
                        && i.Operand is string s && s.Contains(args[2])))
                        Console.WriteLine($"{t.Name}::{m.Name} {m.MethodSig}");
                }
                break;
            }
        }

        return 0;
    }

    private static void Dump(MethodDef m)
    {
        var b = m.Body;
        Console.WriteLine($"===== {m.DeclaringType.Name}::{m.Name} {m.MethodSig}");
        Console.WriteLine($"  maxStack={b.MaxStack}");
        for (int i = 0; i < b.Variables.Count; i++)
            Console.WriteLine($"  local V_{i} :: {b.Variables[i].Type.FullName}");
        foreach (var h in b.ExceptionHandlers)
        {
            var tryStart = h.TryStart == null ? "-" : $"IL_{h.TryStart.Offset:X4}";
            var tryEnd = h.TryEnd == null ? "-" : $"IL_{h.TryEnd.Offset:X4}";
            var hStart = h.HandlerStart == null ? "-" : $"IL_{h.HandlerStart.Offset:X4}";
            var hEnd = h.HandlerEnd == null ? "null" : $"IL_{h.HandlerEnd.Offset:X4}";
            Console.WriteLine($"  handler {h.HandlerType} try {tryStart}..{tryEnd} handler {hStart}..{hEnd} catch={h.CatchType?.FullName}");
        }
        foreach (var i in b.Instructions)
            Console.WriteLine($"  IL_{i.Offset:X4}  {i.OpCode.Name,-12} {Format(i.Operand)}");
    }

    private static string Format(object operand)
    {
        return operand switch
        {
            null => "",
            string s => "\"" + s + "\"",
            Instruction t => $"IL_{t.Offset:X4}",
            Instruction[] arr => string.Join(",", arr.Select(x => $"IL_{x.Offset:X4}")),
            IMethod mm => mm.FullName,
            IField ff => ff.FullName,
            _ => operand.ToString()
        };
    }
}
