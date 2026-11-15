using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace LegacyGameLauncher.Services;

/// <summary>
/// Mounts and unmounts a disc image (.iso) the user owns, with Windows' own virtual-disk API - the same thing
/// Explorer's "Mount" does. Read-only; no extra drivers. Nothing about the image or any copy protection is changed.
/// </summary>
public static class DiscImage
{
    [StructLayout(LayoutKind.Sequential)]
    struct VIRTUAL_STORAGE_TYPE { public uint DeviceId; public Guid VendorId; }

    const uint VIRTUAL_STORAGE_TYPE_DEVICE_ISO = 1;
    static readonly Guid VendorMicrosoft = new("EC984AEC-A0F9-47e9-901F-71415A66345B");
    const uint VIRTUAL_DISK_ACCESS_READ = 0x000d0000;
    const uint VIRTUAL_DISK_ACCESS_ATTACH_RO = 0x00010000;
    const uint VIRTUAL_DISK_ACCESS_DETACH = 0x00040000;
    const uint VIRTUAL_DISK_ACCESS_GET_INFO = 0x00080000;
    const uint ATTACH_READ_ONLY = 0x1, ATTACH_PERMANENT_LIFETIME = 0x4;

    [DllImport("virtdisk.dll", CharSet = CharSet.Unicode)]
    static extern int OpenVirtualDisk(ref VIRTUAL_STORAGE_TYPE type, string path, uint access, uint flags, IntPtr parameters, out IntPtr handle);
    [DllImport("virtdisk.dll")]
    static extern int AttachVirtualDisk(IntPtr handle, IntPtr securityDescriptor, uint flags, uint providerFlags, IntPtr parameters, IntPtr overlapped);
    [DllImport("virtdisk.dll")]
    static extern int DetachVirtualDisk(IntPtr handle, uint flags, uint providerFlags);
    [DllImport("virtdisk.dll", CharSet = CharSet.Unicode)]
    static extern int GetVirtualDiskPhysicalPath(IntPtr handle, ref int size, StringBuilder path);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern uint QueryDosDeviceW(string device, StringBuilder target, int max);

    static IntPtr Open(string iso, uint access)
    {
        var t = new VIRTUAL_STORAGE_TYPE { DeviceId = VIRTUAL_STORAGE_TYPE_DEVICE_ISO, VendorId = VendorMicrosoft };
        int rc = OpenVirtualDisk(ref t, iso, access, 0, IntPtr.Zero, out var h);
        if (rc != 0) throw new Win32Exception(rc, $"Could not open the disc image: {new Win32Exception(rc).Message}");
        return h;
    }

    /// <summary>Drive letter ("D:") of the image if it is mounted, else null.</summary>
    public static string? MountedDrive(string iso)
    {
        IntPtr h;
        try { h = Open(iso, VIRTUAL_DISK_ACCESS_GET_INFO); } catch { return null; }
        try
        {
            int size = 520; var sb = new StringBuilder(260);
            if (GetVirtualDiskPhysicalPath(h, ref size, sb) != 0) return null; // not attached
            return DriveForDevice(sb.ToString());
        }
        finally { CloseHandle(h); }
    }

    /// <summary>Mounts read-only (unless already mounted). Returns (drive, mountedNow).</summary>
    public static (string Drive, bool MountedNow) Mount(string iso, int waitMs = 10000)
    {
        if (!File.Exists(iso)) throw new FileNotFoundException("Disc image not found.", iso);
        var existing = MountedDrive(iso);
        if (existing != null) return (existing, false);

        var h = Open(iso, VIRTUAL_DISK_ACCESS_ATTACH_RO | VIRTUAL_DISK_ACCESS_GET_INFO);
        try
        {
            int rc = AttachVirtualDisk(h, IntPtr.Zero, ATTACH_READ_ONLY | ATTACH_PERMANENT_LIFETIME, 0, IntPtr.Zero, IntPtr.Zero);
            if (rc != 0) throw new Win32Exception(rc, $"Windows could not mount the image: {new Win32Exception(rc).Message}");
        }
        finally { CloseHandle(h); }

        // The drive letter appears a moment after attaching.
        var until = DateTime.Now.AddMilliseconds(waitMs);
        while (DateTime.Now < until)
        {
            var d = MountedDrive(iso);
            if (d != null && Directory.Exists(d + "\\")) return (d, true);
            Thread.Sleep(250);
        }
        return (MountedDrive(iso) ?? "(no drive letter)", true);
    }

    public static void Unmount(string iso)
    {
        var h = Open(iso, VIRTUAL_DISK_ACCESS_DETACH);
        try
        {
            int rc = DetachVirtualDisk(h, 0, 0);
            if (rc != 0) throw new Win32Exception(rc, $"Could not unmount the image: {new Win32Exception(rc).Message}");
        }
        finally { CloseHandle(h); }
    }

    /// <summary>\\.\CDROM1 → the drive letter whose DOS device is \Device\CdRom1.</summary>
    static string? DriveForDevice(string physical)
    {
        var name = physical.Replace(@"\\.\", "", StringComparison.Ordinal);
        foreach (var di in DriveInfo.GetDrives())
        {
            if (di.DriveType != DriveType.CDRom) continue;
            var letter = di.Name.TrimEnd('\\');
            var sb = new StringBuilder(260);
            if (QueryDosDeviceW(letter, sb, sb.Capacity) == 0) continue;
            if (sb.ToString().EndsWith("\\" + name, StringComparison.OrdinalIgnoreCase)) return letter;
        }
        return null;
    }
}
