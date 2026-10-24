using System;
using System.Collections.Generic;
using System.Globalization;
using System.Management;
using System.Text;

/// <summary>
/// MEV蛟龙控制台的内置 WMI 桥。
/// 为什么是 .NET Framework 而不是主程序的 .NET 8:
///   本机厂商方法 MiInterface 在 .NET 8 的 System.Management/CIM 直连下都会报"无效的对象",
///   只有 .NET Framework 的 System.Management(PowerShell 5.1 使用的同一实现)能稳定调用。
/// 为什么不用常驻 PowerShell:
///   powershell.exe 常驻约 100MB, 这个桥约 20MB。
///
/// 协议(逐行, stdin/stdout):
///   PING                -> OK PONG
///   GET  <cmd-hex>      -> OK <32-byte-hex>
///   SET  <cmd-hex> <payload bytes...> -> OK <32-byte-hex>
///   QUIT                -> OK BYE
/// </summary>
internal static class Bridge
{
    private const string Ns = @"root\WMI";
    private const string Inst = @"MICommonInterface.InstanceName='ACPI\PNP0C14\MIFS_0'";
    private const byte VerbGet = 0xFA;
    private const byte VerbSet = 0xFB;

    private static byte[] Send(byte type, byte cmd, byte[] payload)
    {
        var pkt = new byte[32];
        pkt[1] = type;
        pkt[3] = cmd;
        if (payload != null)
        {
            for (int i = 0; i < payload.Length && i < 28; i++)
                pkt[4 + i] = payload[i];
        }

        using (var o = new ManagementObject(Ns, Inst, null))
        using (var inParams = o.GetMethodParameters("MiInterface"))
        {
            inParams["InData"] = pkt;
            using (var res = o.InvokeMethod("MiInterface",
                (ManagementBaseObject)inParams, new InvokeMethodOptions()))
            {
                foreach (PropertyData p in res.Properties)
                {
                    // Framework 自带的 csc 是 C# 5, 不能用 is byte[] bytes 模式变量
                    if (p.Name == "OutData" && p.Value is byte[])
                        return (byte[])p.Value;
                }
            }
        }
        return null;
    }

    private static string Hex(byte[] data)
    {
        var sb = new StringBuilder(data.Length * 2);
        foreach (byte b in data) sb.Append(b.ToString("X2"));
        return sb.ToString();
    }

    public static int Main()
    {
        Console.Out.WriteLine("OK READY");
        Console.Out.Flush();

        string line;
        while ((line = Console.In.ReadLine()) != null)
        {
            line = line.Trim();
            if (line.Length == 0) continue;

            string[] parts = line.Split(' ');
            try
            {
                switch (parts[0].ToUpperInvariant())
                {
                    case "PING":
                        Console.Out.WriteLine("OK PONG");
                        break;
                    case "QUIT":
                        Console.Out.WriteLine("OK BYE");
                        return 0;
                    case "GET":
                    case "SET":
                    {
                        byte cmd = byte.Parse(parts[1], NumberStyles.HexNumber);
                        byte[] payload = null;
                        if (parts[0].ToUpperInvariant() == "SET")
                        {
                            var list = new List<byte>();
                            for (int i = 2; i < parts.Length; i++)
                                list.Add(byte.Parse(parts[i], NumberStyles.HexNumber));
                            payload = list.ToArray();
                        }
                        byte[] data = Send(parts[0].ToUpperInvariant() == "GET" ? VerbGet : VerbSet, cmd, payload);
                        if (data == null) throw new InvalidOperationException("no OutData");
                        Console.Out.WriteLine("OK " + Hex(data));
                        break;
                    }
                    default:
                        Console.Out.WriteLine("ERR unknown verb");
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.Out.WriteLine("ERR " + ex.Message.Replace('\r', ' ').Replace('\n', ' '));
            }
            Console.Out.Flush();
        }
        return 0;
    }
}
