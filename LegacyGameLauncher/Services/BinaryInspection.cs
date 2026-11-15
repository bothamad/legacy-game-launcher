using System.Text;
using System.Text.RegularExpressions;

namespace LegacyGameLauncher.Services;

public sealed class PeInfo
{
    public bool IsValid { get; set; }
    public string? Error { get; set; }
    public ushort Machine { get; set; }
    public List<string> Imports { get; } = new();
    public List<string> SectionNames { get; } = new();
    public bool ManifestRequiresAdmin { get; set; }

    public bool Is32Bit => Machine == 0x014C;
    public bool Is64Bit => Machine == 0x8664;
    public string ArchName => Machine switch
    {
        0x014C => "x86 (32-bit)",
        0x8664 => "x64 (64-bit)",
        0xAA64 => "ARM64",
        _ => IsValid ? $"unknown (0x{Machine:X4})" : "not a valid PE file",
    };
}

/// <summary>Minimal PE reader: architecture, import table, section names, and a manifest elevation hint.</summary>
public static class PeReader
{
    public static PeInfo Read(string path)
    {
        try { return Parse(File.ReadAllBytes(path)); }
        catch (Exception ex) { return new PeInfo { Error = ex.Message }; }
    }

    /// <summary>Reads just enough of the header to get the machine type (cheap; for DLL validation).</summary>
    public static ushort ReadMachine(string path)
    {
        using var fs = File.OpenRead(path);
        var head = new byte[4096];
        int n = fs.Read(head, 0, head.Length);
        if (n < 0x40 || head[0] != 'M' || head[1] != 'Z') return 0;
        int pe = BitConverter.ToInt32(head, 0x3C);
        if (pe < 0 || pe + 6 > n) return 0;
        if (BitConverter.ToUInt32(head, pe) != 0x00004550) return 0;
        return BitConverter.ToUInt16(head, pe + 4);
    }

    public static PeInfo Parse(byte[] d)
    {
        var info = new PeInfo();
        try
        {
            if (d.Length < 0x40 || d[0] != 'M' || d[1] != 'Z') { info.Error = "No MZ header"; return info; }
            int pe = BitConverter.ToInt32(d, 0x3C);
            if (pe <= 0 || pe + 24 > d.Length || BitConverter.ToUInt32(d, pe) != 0x00004550) { info.Error = "No PE header"; return info; }
            info.Machine = BitConverter.ToUInt16(d, pe + 4);
            int nSec = BitConverter.ToUInt16(d, pe + 6);
            int optSize = BitConverter.ToUInt16(d, pe + 20);
            int opt = pe + 24;
            ushort magic = BitConverter.ToUInt16(d, opt);
            int dataDirs = magic == 0x20B ? opt + 112 : opt + 96;
            int secTable = opt + optSize;
            info.IsValid = true;

            var sections = new List<(uint va, uint vsize, uint raw, uint rawSize)>();
            for (int i = 0; i < nSec && secTable + i * 40 + 40 <= d.Length; i++)
            {
                int s = secTable + i * 40;
                info.SectionNames.Add(Encoding.ASCII.GetString(d, s, 8).TrimEnd('\0'));
                sections.Add((BitConverter.ToUInt32(d, s + 12), BitConverter.ToUInt32(d, s + 8),
                              BitConverter.ToUInt32(d, s + 20), BitConverter.ToUInt32(d, s + 16)));
            }

            int RvaToOffset(uint rva)
            {
                foreach (var (va, vsize, raw, rawSize) in sections)
                {
                    uint size = Math.Max(vsize, rawSize);
                    if (rva >= va && rva < va + size) return (int)(rva - va + raw);
                }
                return -1;
            }

            uint importRva = BitConverter.ToUInt32(d, dataDirs + 8);
            int off = importRva == 0 ? -1 : RvaToOffset(importRva);
            for (int i = 0; off > 0 && i < 512 && off + 20 <= d.Length; i++, off += 20)
            {
                uint nameRva = BitConverter.ToUInt32(d, off + 12);
                if (nameRva == 0 && BitConverter.ToUInt32(d, off) == 0) break;
                int no = RvaToOffset(nameRva);
                if (no <= 0 || no >= d.Length) continue;
                int end = Array.IndexOf(d, (byte)0, no, Math.Min(260, d.Length - no));
                if (end > no) info.Imports.Add(Encoding.ASCII.GetString(d, no, end - no).ToLowerInvariant());
            }

            info.ManifestRequiresAdmin = d.AsSpan().IndexOf("requireAdministrator"u8) >= 0;
        }
        catch (Exception ex) { info.Error = ex.Message; }
        return info;
    }
}

/// <summary>Finds DLL names referenced as strings inside binaries (catches LoadLibrary use and packed imports).</summary>
public static class BinaryScan
{
    static readonly Regex DllRef = new(
        @"(d3dx9_\d{2}|d3dx10_\d{2}|d3dcompiler_\d{2}|xinput1_\d|xinput9_1_0|xaudio2_\d|x3daudio1_\d|xactengine\d_\d{1,2}|dinput8?|msvcr\d{2,3}|msvcp\d{2,3}|openal32|dplayx|binkw32|d3d9|d3d8|ddraw)\.dll",
        RegexOptions.Compiled);

    public static string ToLowerLatin1(byte[] data)
    {
        var copy = new byte[data.Length];
        for (int i = 0; i < data.Length; i++)
        {
            byte b = data[i];
            copy[i] = b is >= (byte)'A' and <= (byte)'Z' ? (byte)(b + 32) : b;
        }
        return Encoding.Latin1.GetString(copy);
    }

    public static HashSet<string> ReferencedDlls(string lowerText)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in DllRef.Matches(lowerText)) set.Add(m.Value);
        return set;
    }
}

/// <summary>Everything we learn about a game's binaries in one pass.</summary>
public sealed class GameScan
{
    public PeInfo Exe { get; init; } = new();
    public HashSet<string> Referenced { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>DLLs referenced by the main exe only (installer/helper DLLs in the folder can mention other APIs).</summary>
    public HashSet<string> ExeReferenced { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string ExeLowerText { get; init; } = "";
    public List<string> ScannedFiles { get; } = new();

    public static GameScan Run(string exePath)
    {
        var bytes = File.ReadAllBytes(exePath);
        var lower = BinaryScan.ToLowerLatin1(bytes);
        var scan = new GameScan { Exe = PeReader.Parse(bytes), ExeLowerText = lower };
        foreach (var i in scan.Exe.Imports) scan.ExeReferenced.Add(i);
        foreach (var r in BinaryScan.ReferencedDlls(lower)) scan.ExeReferenced.Add(r);
        scan.Referenced.UnionWith(scan.ExeReferenced);
        scan.ScannedFiles.Add(Path.GetFileName(exePath));

        // Engine DLLs next to the exe often import d3dx9/xinput themselves.
        var dir = Path.GetDirectoryName(exePath)!;
        foreach (var f in Directory.EnumerateFiles(dir, "*.dll").Take(80))
        {
            try
            {
                var fi = new FileInfo(f);
                if (fi.Length > 40_000_000) continue;
                var b = File.ReadAllBytes(f);
                var pe = PeReader.Parse(b);
                foreach (var i in pe.Imports) scan.Referenced.Add(i);
                foreach (var r in BinaryScan.ReferencedDlls(BinaryScan.ToLowerLatin1(b))) scan.Referenced.Add(r);
                scan.ScannedFiles.Add(fi.Name);
            }
            catch { }
        }
        // Don't count DLLs that just reference themselves / ship in the game folder as "needs system copy" here;
        // DependencyChecker decides that.
        return scan;
    }
}
