using System;
using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

// Reports which PCL patches are present in a given executable.
// Exit code: bitmask of applied fixes (1=download, 2=anti-overwrite, 4=skin).
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length != 1)
        {
            Console.WriteLine("usage: Verify <exe>");
            return 0;
        }

        var mod = ModuleDefMD.Load(args[0]);
        int mask = 0;

        // [1] download loader restored
        var pd = mod.GetTypes().FirstOrDefault(x => x.Name == "PageDownloadInstall");
        var li = pd?.Methods.FirstOrDefault(x => x.Name == "LoaderInit");
        var used = li?.Body?.Instructions
            .Where(i => i.OpCode == OpCodes.Ldsfld && i.Operand is IField f
                        && f.DeclaringType?.Name == "ModDownload" && f.Name.StartsWith("DlClientList"))
            .Select(i => ((IField)i.Operand).Name).Distinct().ToList() ?? new();
        bool downloadFixed = used.Contains("DlClientListLoader") && used.Count == 1;
        if (downloadFixed) mask |= 1;
        Console.WriteLine($"[1] 下载 loader : {(used.Count == 0 ? "(无)" : string.Join(",", used))} -> {(downloadFixed ? "已修复" : "未修复")}");

        // [2] updater redirected to PCL-Restore
        var ms = mod.GetTypes().FirstOrDefault(x => x.Name == "ModSecret");
        var ur = ms?.Methods.FirstOrDefault(x => x.Name == "UpdateRestart");
        bool hookFixed = ur?.Body?.Instructions.Any(i => i.OpCode == OpCodes.Ldstr
            && i.Operand is string s && s == "PCL-Restore.exe") == true;
        if (hookFixed) mask |= 2;
        Console.WriteLine($"[2] 更新防覆盖: {(hookFixed ? "已生效" : "未生效")}");

        // [3] skin fix
        var ml = mod.GetTypes().FirstOrDefault(x => x.Name == "ModLaunch");
        var me = ml?.Methods.FirstOrDefault(x => x.Body != null && x.Body.Instructions.Any(
            i => i.OpCode == OpCodes.Ldstr && i.Operand is string s && s.Contains("自定义皮肤资源包")));
        int dispatchMinor = 0, gateFixed = 0;
        if (me != null)
        {
            var ins = me.Body.Instructions;
            for (int i = 0; i < ins.Count; i++)
            {
                if (ins[i].OpCode != OpCodes.Callvirt) continue;
                if (ins[i].Operand is not IMethod mm) continue;
                if (mm.DeclaringType?.FullName != "System.Version") continue;

                if (mm.Name == "get_Minor")
                {
                    bool dispatch = false;
                    for (int j = i + 1; j <= i + 4 && j < ins.Count; j++)
                    {
                        if (ins[j].OpCode != OpCodes.Ldc_I4_6) continue;
                        for (int k = j + 1; k <= j + 2 && k < ins.Count; k++)
                            if (ins[k].OpCode == OpCodes.Sub && k + 1 < ins.Count
                                && ins[k + 1].OpCode == OpCodes.Switch) dispatch = true;
                    }
                    if (dispatch) dispatchMinor++;

                    bool has19 = false, hasClt = false;
                    for (int j = i + 1; j < Math.Min(ins.Count, i + 6); j++)
                    {
                        if (ins[j].OpCode == OpCodes.Ldc_I4_S && ins[j].Operand is sbyte sb && sb == 19) has19 = true;
                        if (ins[j].OpCode == OpCodes.Clt) hasClt = true;
                    }
                    if (has19 && hasClt) gateFixed++;
                }
            }
        }
        bool skinFixed = dispatchMinor > 0 && gateFixed > 0;
        if (skinFixed) mask |= 4;
        Console.WriteLine($"[3] 皮肤修复   : pack_format={dispatchMinor} 门槛={gateFixed} -> {(skinFixed ? "已修复" : "未修复")}");

        Console.WriteLine($"MASK={mask}");
        return mask;
    }
}
