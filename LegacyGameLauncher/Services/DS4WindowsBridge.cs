using System.Diagnostics;
using System.Xml;
using LegacyGameLauncher.Models;

namespace LegacyGameLauncher.Services;

public sealed class DS4WindowsInstall
{
    public string SettingsDir { get; set; } = "";
    public string? ExePath { get; set; }
    public bool Running { get; set; }
    public string BaseProfile { get; set; } = "Default";
    public string ProfilesDir => Path.Combine(SettingsDir, "Profiles");
    public string AutoProfilesFile => Path.Combine(SettingsDir, "Auto Profiles.xml");
}

/// <summary>
/// Gives a game its own DS4Windows face-button layout. Creates "LGL - &lt;game&gt;.xml" in DS4Windows' Profiles
/// folder (a copy of your usual profile with only the four face buttons remapped) and an Auto Profile entry for the
/// game's exe, so DS4Windows switches to it while the game runs and back afterwards. Format taken from the
/// DS4Windows source (ProfileDTO / AutoProfilesDTO). DS4Windows reads Auto Profiles.xml only at start-up, so it
/// is closed before the change and started again afterwards.
/// </summary>
public static class DS4WindowsBridge
{
    public const string Group = "padlayout";

    /// <summary>
    /// Layout presets: DS4 face button → Xbox 360 output button name as DS4Windows writes it, and optionally a
    /// different virtual output device (OutContType name, e.g. ViiperDS4).
    /// </summary>
    public static readonly Dictionary<string, (string Label, Dictionary<string, string>? Map, string? Output)> Presets = new()
    {
        ["Default"] = ("Xbox layout (DS4Windows default: Cross = A / button 1)", null, null),
        ["DirectInput"] = ("DirectInput / Logitech Rumblepad 2 layout - virtual DS4 (best for Rugby 08 / Cricket 07 PC)", new(), "ViiperDS4"),
        ["PlayStation"] = ("PlayStation-direct face buttons on the Xbox output (Square = 1, Cross = 2, Circle = 3, Triangle = 4)", new()
        {
            ["Square"] = "A Button", ["Cross"] = "B Button", ["Circle"] = "X Button", ["Triangle"] = "Y Button",
        }, null),
        ["SwapCrossCircle"] = ("Swap Cross and Circle only (Xbox output)", new()
        {
            ["Cross"] = "B Button", ["Circle"] = "A Button",
        }, null),
    };

    public static DS4WindowsInstall? Find()
    {
        var running = Process.GetProcessesByName("DS4Windows").FirstOrDefault();
        string? exe = null;
        try { exe = running?.MainModule?.FileName; } catch { }
        var candidates = new List<string>
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DS4WindowsNexus"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DS4Windows"),
        };
        if (exe != null) candidates.Insert(0, Path.GetDirectoryName(exe)!); // portable mode keeps settings next to the exe
        foreach (var dir in candidates)
        {
            if (!File.Exists(Path.Combine(dir, "Profiles.xml")) || !Directory.Exists(Path.Combine(dir, "Profiles"))) continue;
            var inst = new DS4WindowsInstall { SettingsDir = dir, ExePath = exe, Running = running != null };
            try
            {
                var doc = new XmlDocument(); doc.Load(Path.Combine(dir, "Profiles.xml"));
                var c1 = doc.DocumentElement?.SelectSingleNode("Controller1")?.InnerText;
                if (!string.IsNullOrWhiteSpace(c1) && File.Exists(Path.Combine(inst.ProfilesDir, c1 + ".xml"))) inst.BaseProfile = c1;
            }
            catch { }
            return inst;
        }
        return null;
    }

    public static string ProfileName(GameEntry g)
    {
        var safe = string.Concat(g.DisplayName.Select(ch => char.IsLetterOrDigit(ch) || ch is ' ' or '-' or '_' ? ch : ' ')).Trim();
        return "LGL - " + (safe.Length > 0 ? safe : "Game");
    }

    static readonly Dictionary<string, string> DpadToLeftStick = new()
    {
        ["DpadUp"] = "Left Y-Axis-", ["DpadDown"] = "Left Y-Axis+", ["DpadLeft"] = "Left X-Axis-", ["DpadRight"] = "Left X-Axis+",
    };

    /// <summary>Copy of the base profile with the chosen mapping changes (face buttons, D-pad) and touchpad option.</summary>
    public static string BuildProfileXml(DS4WindowsInstall inst, Dictionary<string, string> faceMap, bool touchpadOff = false, bool dpadAsStick = false, string? output = null)
    {
        var map = new Dictionary<string, string>(faceMap);
        if (dpadAsStick) foreach (var kv in DpadToLeftStick) map[kv.Key] = kv.Value;
        var doc = new XmlDocument { PreserveWhitespace = false };
        doc.Load(Path.Combine(inst.ProfilesDir, inst.BaseProfile + ".xml"));
        var root = doc.DocumentElement ?? throw new InvalidOperationException("The DS4Windows profile is empty.");
        if (output != null)
        {
            // Virtual output device for this game, e.g. ViiperDS4: the game then sees a DS4 with DirectInput's
            // classic numbering (buttons 1-14, right stick on Z/Rz, L2/R2 as buttons 7/8, D-pad as a hat).
            var od = root.SelectSingleNode("OutputContDevice") ?? root.AppendChild(doc.CreateElement("OutputContDevice"))!;
            od.InnerText = output;
        }
        if (touchpadOff)
        {
            // TouchpadOutMode.None - the touchpad no longer moves the Windows mouse (DS4Windows' default is Mouse).
            var tp = root.SelectSingleNode("TouchpadOutputMode") ?? root.AppendChild(doc.CreateElement("TouchpadOutputMode"))!;
            tp.InnerText = "None";
        }
        var control = root.SelectSingleNode("Control") ?? root.AppendChild(doc.CreateElement("Control"))!;
        var button = control.SelectSingleNode("Button") ?? control.AppendChild(doc.CreateElement("Button"))!;
        foreach (var (ds4, x360) in map)
        {
            var node = button.SelectSingleNode(ds4) ?? button.AppendChild(doc.CreateElement(ds4))!;
            node.InnerText = x360;
            // A key/macro binding on the same control would take priority - drop it for these four buttons.
            foreach (var other in new[] { "Key", "Macro", "KeyType", "Extras" })
                if (control.SelectSingleNode($"{other}/{ds4}") is XmlNode n) n.ParentNode!.RemoveChild(n);
        }
        return ToUtf8(doc);
    }

    /// <summary>Serialises with a utf-8 declaration (a StringWriter would declare utf-16 and DS4Windows couldn't load the file).</summary>
    static string ToUtf8(XmlDocument doc)
    {
        var enc = new System.Text.UTF8Encoding(false);
        using var ms = new MemoryStream();
        using (var xw = XmlWriter.Create(ms, new XmlWriterSettings { Indent = true, Encoding = enc })) doc.Save(xw);
        return enc.GetString(ms.ToArray());
    }

    /// <summary>Auto Profiles.xml with this exe's entry set to <paramref name="profile"/> (or removed when null).</summary>
    public static string BuildAutoProfilesXml(DS4WindowsInstall inst, string exePath, string? profile)
    {
        var doc = new XmlDocument();
        if (File.Exists(inst.AutoProfilesFile)) doc.Load(inst.AutoProfilesFile);
        if (doc.DocumentElement == null)
        {
            doc.AppendChild(doc.CreateXmlDeclaration("1.0", "utf-8", null));
            doc.AppendChild(doc.CreateElement("Programs"));
        }
        var root = doc.DocumentElement!;
        foreach (XmlElement p in root.SelectNodes("Program")!.Cast<XmlElement>().ToList())
            if (string.Equals(p.GetAttribute("path"), exePath, StringComparison.OrdinalIgnoreCase)) root.RemoveChild(p);
        if (profile != null)
        {
            var p = doc.CreateElement("Program");
            p.SetAttribute("path", exePath);
            p.SetAttribute("title", "");
            for (int i = 1; i <= 8; i++) { var c = doc.CreateElement("Controller" + i); c.InnerText = profile; p.AppendChild(c); }
            var t = doc.CreateElement("TurnOff"); t.InnerText = "False"; p.AppendChild(t);
            root.AppendChild(p);
        }
        return ToUtf8(doc);
    }

    /// <summary>Closes DS4Windows if it's running. Returns the exe path to start again, or null.</summary>
    public static async Task<string?> StopAsync(DS4WindowsInstall inst)
    {
        var procs = Process.GetProcessesByName("DS4Windows");
        if (procs.Length == 0) return null;
        string? exe = inst.ExePath;
        foreach (var p in procs)
        {
            try { exe ??= p.MainModule?.FileName; } catch { }
            try { p.CloseMainWindow(); } catch { }
        }
        for (int i = 0; i < 20 && procs.Any(p => { try { return !p.HasExited; } catch { return false; } }); i++) await Task.Delay(250);
        foreach (var p in procs) { try { if (!p.HasExited) p.Kill(); } catch { } }
        await Task.Delay(500);
        return exe;
    }

    public static void Start(string? exe)
    {
        if (exe != null && File.Exists(exe))
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe)! });
    }

    /// <summary>Writes the profile + auto-profile entry. Files are backed up / journaled for undo.</summary>
    public static async Task<(bool Ok, string Log)> ApplyAsync(GameEntry g, string preset, System.Windows.Window? owner)
    {
        var inst = Find();
        if (inst == null) return (false, "DS4Windows settings not found. Start DS4Windows once, then try again.");
        if (!Presets.TryGetValue(preset, out var p)) return (false, "Unknown layout.");
        var face = p.Map ?? new Dictionary<string, string>();
        if (face.Count == 0 && p.Output == null && !g.PadTouchpadOff && !g.PadDpadAsStick) return await RemoveAsync(g, owner);

        var name = ProfileName(g);
        var log = new List<string>();
        AppPaths.Ensure();
        var stagedProfile = Path.Combine(AppPaths.Staging, $"ds4w-{Guid.NewGuid():N}.xml");
        File.WriteAllText(stagedProfile, BuildProfileXml(inst, face, g.PadTouchpadOff, g.PadDpadAsStick, p.Output));

        var restartExe = await StopAsync(inst);
        if (restartExe != null) log.Add("Closed DS4Windows to update its settings.");
        try
        {
            var stagedAuto = Path.Combine(AppPaths.Staging, $"ds4w-auto-{Guid.NewGuid():N}.xml");
            // Output-changing layouts are switched by the launcher before launch (see PrepareForLaunchAsync);
            // an Auto Profile would re-plug the virtual pad after the game has started, so it's removed for those.
            File.WriteAllText(stagedAuto, BuildAutoProfilesXml(inst, g.ExePath, p.Output != null ? null : name));
            var ops = new List<FileOp>();
            var target = Path.Combine(inst.ProfilesDir, name + ".xml");
            var rec = ChangeJournal.PlanPlaceFile(g, Group, stagedProfile, target, ops, $"DS4Windows profile \"{name}\"");
            // Keep one safety copy of Auto Profiles.xml; removal edits just this game's entry, so other games stay intact.
            if (File.Exists(inst.AutoProfilesFile))
                ops.Add(new FileOp { Op = "Copy", Source = inst.AutoProfilesFile, Target = ChangeJournal.NewBackupPath(g, inst.AutoProfilesFile) });
            ops.Add(new FileOp { Op = "Copy", Source = stagedAuto, Target = inst.AutoProfilesFile });
            var (ok, err) = await FileOpRunner.RunAsync(ops, "Update DS4Windows profile settings", owner);
            if (!ok) return (false, "FAILED: " + err);
            if (rec != null) g.Changes.Add(rec);
            g.PadLayout = preset;
            g.PadLayoutDs4wDir = inst.SettingsDir;
            var changes = face.Select(kv => $"{kv.Key} → {kv.Value.Replace(" Button", "")}").ToList();
            if (p.Output == "ViiperDS4") changes.Insert(0, "output as a virtual DS4 (DirectInput numbering: Square 1, Cross 2, Circle 3, Triangle 4, L2/R2 = 7/8, Share 9, Options 10)");
            if (g.PadDpadAsStick) changes.Add("D-pad → left stick");
            if (g.PadTouchpadOff) changes.Add("touchpad no longer moves the mouse");
            log.Add($"Created DS4Windows profile \"{name}\" from \"{inst.BaseProfile}\" with: {string.Join(", ", changes)}.");
            log.Add(p.Output != null
                ? $"This layout changes DS4Windows' virtual controller, so the launcher switches DS4Windows to it just before starting {Path.GetFileName(g.ExePath)} (and back afterwards). Start the game from the launcher to use it."
                : $"Added an Auto Profile so DS4Windows uses it while {Path.GetFileName(g.ExePath)} runs, and switches back afterwards.");
        }
        finally
        {
            if (restartExe != null) { Start(restartExe); log.Add("Started DS4Windows again."); }
            else log.Add("DS4Windows wasn't running - start it before playing.");
        }
        return (true, string.Join("\n", log));
    }

    /// <summary>Removes this game's auto-profile entry and its profile file.</summary>
    public static async Task<(bool Ok, string Log)> RemoveAsync(GameEntry g, System.Windows.Window? owner)
    {
        var log = new List<string>();
        var inst = Find();
        string? restartExe = null;
        if (inst != null)
        {
            restartExe = await StopAsync(inst);
            try
            {
                if (File.Exists(inst.AutoProfilesFile))
                {
                    File.WriteAllText(inst.AutoProfilesFile, BuildAutoProfilesXml(inst, g.ExePath, null));
                    log.Add("Removed the game's DS4Windows Auto Profile entry.");
                }
            }
            catch (Exception ex) { log.Add("Could not edit Auto Profiles.xml: " + ex.Message); }
        }
        var (ok, ulog) = await ChangeJournal.UndoAsync(g, Group, owner);
        log.Add(ulog);
        g.PadLayout = "Default";
        if (restartExe != null) { Start(restartExe); log.Add("Restarted DS4Windows."); }
        return (ok, string.Join("\n", log));
    }

    // ---------------------------------------------------------------- switching around a launch

    /// <summary>
    /// Layouts that change DS4Windows' virtual output device (e.g. to a virtual DS4) can't use Auto Profiles: DS4Windows
    /// switches only after the game's window appears, unplugging and re-plugging the virtual pad a few seconds later -
    /// after the game has already looked for controllers. Those are switched by the launcher before the game starts.
    /// </summary>
    public static bool NeedsPreLaunchSwitch(GameEntry g) =>
        g.Changes.Any(c => c.Group == Group) && Presets.TryGetValue(g.PadLayout, out var p) && p.Output != null;

    public sealed class SwitchToken
    {
        public DS4WindowsInstall Inst = new();
        public string? Exe;
        public Dictionary<string, string> Previous = new();
    }

    static Dictionary<string, string> SetControllerProfiles(DS4WindowsInstall inst, Func<string, string, string> pick)
    {
        var file = Path.Combine(inst.SettingsDir, "Profiles.xml");
        var doc = new XmlDocument(); doc.Load(file);
        var before = new Dictionary<string, string>();
        for (int i = 1; i <= 8; i++)
        {
            if (doc.DocumentElement?.SelectSingleNode("Controller" + i) is not XmlNode n) continue;
            before[n.Name] = n.InnerText;
            n.InnerText = pick(n.Name, n.InnerText);
        }
        File.WriteAllText(file, ToUtf8(doc));
        return before;
    }

    static string LogFile(DS4WindowsInstall inst) => Path.Combine(inst.SettingsDir, "Logs", "ds4windows_log.txt");

    /// <summary>Waits for DS4Windows to log that a virtual controller was plugged in after <paramref name="since"/>.</summary>
    static async Task<bool> WaitForVirtualPadAsync(DS4WindowsInstall inst, DateTime since, TimeSpan timeout)
    {
        var until = DateTime.Now + timeout;
        while (DateTime.Now < until)
        {
            try
            {
                using var fs = new FileStream(LogFile(inst), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var sr = new StreamReader(fs);
                foreach (var line in sr.ReadToEnd().Split('\n').Reverse().Take(80))
                {
                    if (!line.Contains("Plugging in virtual", StringComparison.OrdinalIgnoreCase)) continue;
                    if (DateTime.TryParse(line.Split('|')[0], out var t) && t >= since.AddSeconds(-1)) return true;
                }
            }
            catch { }
            await Task.Delay(400);
        }
        return false;
    }

    /// <summary>Before launch: make DS4Windows use the game's profile and wait until its virtual pad is plugged in.</summary>
    public static async Task<SwitchToken?> PrepareForLaunchAsync(GameEntry g, Action<string> log)
    {
        var inst = Find();
        if (inst == null) { log("Button layout: DS4Windows settings not found - skipped."); return null; }
        if (!inst.Running) { log("Button layout: DS4Windows isn't running - start it before launching to use the game's layout."); return null; }
        var name = ProfileName(g);
        if (!File.Exists(Path.Combine(inst.ProfilesDir, name + ".xml"))) { log($"Button layout: profile \"{name}\" is missing - re-apply it on the Controllers tab."); return null; }

        log($"Button layout: switching DS4Windows to \"{name}\" before the game starts (the virtual controller is re-plugged)…");
        var token = new SwitchToken { Inst = inst };
        token.Exe = await StopAsync(inst);
        // An Auto Profile for this exe (left by an older version) would re-plug the pad after the game starts.
        try
        {
            if (File.Exists(inst.AutoProfilesFile) && File.ReadAllText(inst.AutoProfilesFile).Contains(g.ExePath, StringComparison.OrdinalIgnoreCase))
            {
                File.WriteAllText(inst.AutoProfilesFile, BuildAutoProfilesXml(inst, g.ExePath, null));
                log("Button layout: removed this game's DS4Windows Auto Profile (it would re-plug the pad mid-start).");
            }
        }
        catch { }
        token.Previous = SetControllerProfiles(inst, (_, _) => name);
        var started = DateTime.Now;
        Start(token.Exe);
        if (await WaitForVirtualPadAsync(inst, started, TimeSpan.FromSeconds(30)))
        {
            await Task.Delay(1500); // let Windows finish adding the device
            log("Button layout: DS4Windows is using the game's profile and the virtual controller is plugged in.");
        }
        else log("Button layout: WARNING - DS4Windows didn't report a plugged-in virtual controller within 30 s. Is the pad connected?");
        return token;
    }

    /// <summary>After the game exits: put DS4Windows back on the profiles it used before.</summary>
    public static async Task RestoreAfterLaunchAsync(SwitchToken? token, Action<string> log)
    {
        if (token == null) return;
        try
        {
            var running = Process.GetProcessesByName("DS4Windows").Length > 0;
            var exe = running ? await StopAsync(token.Inst) ?? token.Exe : null;
            SetControllerProfiles(token.Inst, (node, current) => token.Previous.TryGetValue(node, out var v) ? v : current);
            if (running) Start(exe);
            log("Button layout: DS4Windows is back on your normal profile.");
        }
        catch (Exception ex) { log("Button layout: couldn't restore DS4Windows' profile: " + ex.Message); }
    }

    /// <summary>For "Undo all": drop the auto-profile entry (DS4Windows picks it up on its next start).</summary>
    public static void RemoveEntryQuiet(GameEntry g)
    {
        try
        {
            var inst = Find();
            if (inst == null || !File.Exists(inst.AutoProfilesFile) || Process.GetProcessesByName("DS4Windows").Length > 0) return;
            File.WriteAllText(inst.AutoProfilesFile, BuildAutoProfilesXml(inst, g.ExePath, null));
        }
        catch { }
    }
}
