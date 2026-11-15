using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LegacyGameLauncher.Services;

public sealed class LaunchRequest
{
    public string ExePath { get; set; } = "";
    public string Arguments { get; set; } = "";
    public string WorkingDirectory { get; set; } = "";
    public long AffinityMask { get; set; }
    public string Priority { get; set; } = "Normal";
}

public sealed class LaunchResult
{
    public bool Started { get; set; }
    public string? Error { get; set; }
    public int Win32Error { get; set; }
    public int Pid { get; set; }
    public long? ExitCode { get; set; }
    public DateTime StartedLocal { get; set; }
    public DateTime? MainExitedLocal { get; set; }
    public DateTime? AllExitedLocal { get; set; }
    public bool Elevated { get; set; }
    public string AffinityResult { get; set; } = "not requested";
    public string PriorityResult { get; set; } = "Normal (default)";
    public List<string> ProcessTree { get; set; } = new();
    public string WorkingDirectory { get; set; } = "";
    /// <summary>True when child processes kept running after the main exe exited (normal for SafeDisc and some launchers).</summary>
    public bool ChildrenOutlivedMain { get; set; }
}

/// <summary>
/// Starts the game SUSPENDED, applies CPU affinity + priority before any game code runs (so engines that size
/// thread pools from the core count see the limit), puts it in a job object to follow child processes, then resumes.
/// </summary>
public static class GameProcess
{
    const uint CREATE_SUSPENDED = 0x4;
    const uint INFINITE_WAIT_SLICE = 2000;
    public const int ERROR_ELEVATION_REQUIRED = 740;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFO
    {
        public int cb; public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CreateProcessW(string? app, StringBuilder cmd, IntPtr pa, IntPtr ta, bool inherit, uint flags, IntPtr env, string? cwd, ref STARTUPINFO si, out PROCESS_INFORMATION pi);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetProcessAffinityMask(IntPtr h, UIntPtr mask);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetPriorityClass(IntPtr h, uint cls);
    [DllImport("kernel32.dll", SetLastError = true)] static extern uint ResumeThread(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)] static extern uint WaitForSingleObject(IntPtr h, uint ms);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetExitCodeProcess(IntPtr h, out uint code);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern IntPtr CreateJobObjectW(IntPtr attr, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr proc);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool QueryInformationJobObject(IntPtr job, int cls, IntPtr info, int len, IntPtr ret);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool TerminateProcess(IntPtr h, uint code);

    static uint PriorityClass(string p) => p switch
    {
        "BelowNormal" => 0x4000, "AboveNormal" => 0x8000, "High" => 0x80, _ => 0x20,
    };

    public static LaunchResult Run(LaunchRequest r, Action<string>? log = null)
    {
        var res = new LaunchResult { Elevated = Environment.IsPrivilegedProcess };
        var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>() };
        var cmd = new StringBuilder($"\"{r.ExePath}\" {r.Arguments}".TrimEnd());
        // Always start in the exe's own folder: many old games (and loaders such as SafeDiscLoader2) open files by relative path.
        var cwd = string.IsNullOrWhiteSpace(r.WorkingDirectory) ? Path.GetDirectoryName(r.ExePath) : r.WorkingDirectory;
        res.WorkingDirectory = cwd ?? "";
        if (!CreateProcessW(r.ExePath, cmd, IntPtr.Zero, IntPtr.Zero, false, CREATE_SUSPENDED, IntPtr.Zero, cwd, ref si, out var pi))
        {
            res.Win32Error = Marshal.GetLastWin32Error();
            res.Error = new Win32Exception(res.Win32Error).Message;
            return res;
        }
        res.Started = true;
        res.Pid = pi.dwProcessId;
        res.StartedLocal = DateTime.Now;

        var job = CreateJobObjectW(IntPtr.Zero, null);
        bool inJob = job != IntPtr.Zero && AssignProcessToJobObject(job, pi.hProcess);

        if (r.AffinityMask != 0)
        {
            res.AffinityResult = SetProcessAffinityMask(pi.hProcess, (UIntPtr)(ulong)r.AffinityMask)
                ? $"applied mask 0x{r.AffinityMask:X} ({BitOps(r.AffinityMask)} logical CPUs: {CpuList(r.AffinityMask)}) before start; child processes inherit it"
                : $"FAILED ({new Win32Exception(Marshal.GetLastWin32Error()).Message})";
        }
        if (r.Priority != "Normal")
        {
            res.PriorityResult = SetPriorityClass(pi.hProcess, PriorityClass(r.Priority))
                ? r.Priority : $"FAILED to set {r.Priority} ({new Win32Exception(Marshal.GetLastWin32Error()).Message})";
        }

        ResumeThread(pi.hThread);
        CloseHandle(pi.hThread);
        log?.Invoke($"Started PID {pi.dwProcessId} in \"{cwd}\". Affinity: {res.AffinityResult}. Priority: {res.PriorityResult}.");

        // Follow the whole process tree two ways: the job object, and parent-PID links (catches children that
        // broke away from the job, e.g. SafeDisc's ~e5.0001.exe helper in %TEMP%).
        var seen = new HashSet<string>();
        var tree = new HashSet<int> { pi.dwProcessId };
        var startTimes = new Dictionary<int, DateTime>();
        void Note(int pid)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                if (!startTimes.ContainsKey(pid)) startTimes[pid] = p.StartTime;
                var label = $"{p.ProcessName}.exe (PID {pid})";
                if (seen.Add(label)) log?.Invoke($"Process in game tree: {label}");
            }
            catch { }
        }
        List<int> Alive()
        {
            var alive = new List<int>();
            if (inJob) foreach (var pid in JobPids(job)) { tree.Add(pid); }
            foreach (var (pid, parent) in ProcessParents())
                if (tree.Contains(parent)) tree.Add(pid);
            foreach (var pid in tree)
            {
                if (pid == pi.dwProcessId && res.MainExitedLocal == null) continue;
                try
                {
                    using var p = Process.GetProcessById(pid);
                    // Guard against PID reuse: only count a process started after the launch.
                    if (p.StartTime >= res.StartedLocal.AddSeconds(-1) && !p.HasExited) { alive.Add(pid); Note(pid); }
                }
                catch { }
            }
            return alive;
        }

        while (WaitForSingleObject(pi.hProcess, INFINITE_WAIT_SLICE) != 0) Alive();
        GetExitCodeProcess(pi.hProcess, out var code);
        res.ExitCode = code;
        res.MainExitedLocal = DateTime.Now;
        CloseHandle(pi.hProcess);
        log?.Invoke($"Main process exited with code {ExitCodes.Describe(code)} after {(res.MainExitedLocal.Value - res.StartedLocal).TotalSeconds:0.0}s.");

        // Some games (and SafeDisc) start a second process and exit the first; keep following until everything is gone.
        var left = Alive();
        if (left.Count > 0)
        {
            res.ChildrenOutlivedMain = true;
            log?.Invoke($"{left.Count} child process(es) still running - following them.");
            while (left.Count > 0) { Thread.Sleep(1000); left = Alive(); }
        }
        res.AllExitedLocal = DateTime.Now;
        res.ProcessTree = seen.ToList();
        if (job != IntPtr.Zero) CloseHandle(job);
        return res;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct PROCESSENTRY32W
    {
        public int dwSize, cntUsage, th32ProcessID; public IntPtr th32DefaultHeapID; public int th32ModuleID, cntThreads, th32ParentProcessID, pcPriClassBase, dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr CreateToolhelp32Snapshot(uint flags, int pid);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool Process32FirstW(IntPtr snap, ref PROCESSENTRY32W e);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool Process32NextW(IntPtr snap, ref PROCESSENTRY32W e);

    /// <summary>(pid, parent pid) for every running process. Repeated until no new descendants appear.</summary>
    static List<(int Pid, int Parent)> ProcessParents()
    {
        var list = new List<(int, int)>();
        var snap = CreateToolhelp32Snapshot(0x2 /* TH32CS_SNAPPROCESS */, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return list;
        try
        {
            var e = new PROCESSENTRY32W { dwSize = Marshal.SizeOf<PROCESSENTRY32W>() };
            if (Process32FirstW(snap, ref e))
                do list.Add((e.th32ProcessID, e.th32ParentProcessID)); while (Process32NextW(snap, ref e));
        }
        finally { CloseHandle(snap); }
        // Order so parents come before children: a few passes in Alive() are cheaper than sorting by tree depth.
        return list.Concat(list).Concat(list).ToList();
    }

    static List<int> JobPids(IntPtr job)
    {
        const int JobObjectBasicProcessIdList = 3;
        int size = 8 + 256 * IntPtr.Size;
        var buf = Marshal.AllocHGlobal(size);
        try
        {
            if (!QueryInformationJobObject(job, JobObjectBasicProcessIdList, buf, size, IntPtr.Zero)) return new();
            int count = Marshal.ReadInt32(buf, 4);
            var list = new List<int>();
            for (int i = 0; i < count; i++) list.Add((int)Marshal.ReadIntPtr(buf, 8 + i * IntPtr.Size));
            return list;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    static int BitOps(long m) => System.Numerics.BitOperations.PopCount((ulong)m);
    static string CpuList(long m) => string.Join(",", Enumerable.Range(0, 64).Where(i => ((m >> i) & 1) != 0));

    /// <summary>Starts the game through an elevated copy of this exe (only when the game needs admin).</summary>
    public static async Task<LaunchResult> RunElevatedAsync(LaunchRequest r)
    {
        AppPaths.Ensure();
        var file = Path.Combine(AppPaths.Staging, $"launch-{Guid.NewGuid():N}.json");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(r, Json.Options);
        File.WriteAllBytes(file, bytes);
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        try
        {
            var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas", ArgumentList = { "--launch", file, hash } };
            using var p = Process.Start(psi)!;
            await p.WaitForExitAsync();
            var resFile = file + ".result";
            if (File.Exists(resFile))
                return JsonSerializer.Deserialize<LaunchResult>(File.ReadAllText(resFile), Json.Options) ?? new LaunchResult { Error = "Empty result" };
            return new LaunchResult { Error = $"Elevated helper exited with {p.ExitCode} without a result." };
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return new LaunchResult { Error = "Cancelled at the UAC prompt." };
        }
        finally
        {
            try { File.Delete(file); File.Delete(file + ".result"); } catch { }
        }
    }

    public static int RunLaunchHelper(string file, string expectedHash)
    {
        LaunchResult res;
        try
        {
            var bytes = File.ReadAllBytes(file);
            if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Launch request was modified after approval; nothing was started.");
            var req = JsonSerializer.Deserialize<LaunchRequest>(bytes, Json.Options)!;
            res = Run(req);
        }
        catch (Exception ex) { res = new LaunchResult { Error = ex.Message }; }
        File.WriteAllText(file + ".result", JsonSerializer.Serialize(res, Json.Options));
        return res.Started ? 0 : 1;
    }
}

public static class ExitCodes
{
    static readonly Dictionary<uint, string> Known = new()
    {
        [0] = "success / normal exit",
        [1] = "generic error",
        [0xC0000005] = "ACCESS_VIOLATION (crash - often graphics driver/wrapper, resolution or DRM related)",
        [0xC0000135] = "DLL_NOT_FOUND (a required DLL is missing - check the dependency list)",
        [0xC0000139] = "ENTRYPOINT_NOT_FOUND (wrong DLL version)",
        [0xC000007B] = "INVALID_IMAGE_FORMAT (32/64-bit DLL mismatch in the game folder)",
        [0xC0000142] = "DLL_INIT_FAILED",
        [0xC0000409] = "STACK_BUFFER_OVERRUN / fail-fast",
        [0xC0000374] = "HEAP_CORRUPTION",
        [0xC00000FD] = "STACK_OVERFLOW",
        [0xC000001D] = "ILLEGAL_INSTRUCTION",
        [0xC0000094] = "INTEGER_DIVIDE_BY_ZERO (classic symptom of too many CPU cores or a very fast timer)",
        [0xE06D7363] = "unhandled C++ exception",
        [0x80000003] = "BREAKPOINT",
        [0x40010004] = "terminated by debugger/system",
        [0xC000013A] = "terminated with Ctrl+C / closed",
    };

    public static string Describe(uint code) =>
        Known.TryGetValue(code, out var d) ? $"0x{code:X8} ({code}) = {d}" : $"0x{code:X8} ({code})";
}
