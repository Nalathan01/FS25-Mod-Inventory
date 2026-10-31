using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.Win32;

internal sealed class BrowserInfo
{
    public string Id;
    public string Name;
    public string Exe;
    public bool AppWindow;
}

internal static class Browser
{
    public const string Auto = "auto";
    public const string Default = "default";

    private static readonly string[] AppCapable = new string[] { "msedge", "chrome", "brave", "vivaldi", "chromium", "thorium" };

    private static string PrefPath
    {
        get { return Path.Combine(AppEnv.DataDir, "browser.txt"); }
    }

    public static string Choice
    {
        get
        {
            try
            {
                if (File.Exists(PrefPath))
                {
                    string v = File.ReadAllText(PrefPath).Trim();
                    if (v.Length > 0) return v;
                }
            }
            catch (Exception) { }
            return Auto;
        }
        set { File.WriteAllText(PrefPath, value ?? Auto, new UTF8Encoding(false)); }
    }

    public static List<BrowserInfo> Installed()
    {
        List<BrowserInfo> list = new List<BrowserInfo>();
        HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        RegistryKey[] roots = new RegistryKey[] { Registry.CurrentUser, Registry.LocalMachine };
        string[] bases = new string[] { @"SOFTWARE\Clients\StartMenuInternet", @"SOFTWARE\WOW6432Node\Clients\StartMenuInternet" };
        foreach (RegistryKey root in roots)
        {
            foreach (string b in bases)
            {
                try
                {
                    using (RegistryKey key = root.OpenSubKey(b))
                    {
                        if (key == null) continue;
                        foreach (string sub in key.GetSubKeyNames())
                        {
                            BrowserInfo info = Read(key, sub);
                            if (info == null || !seen.Add(info.Exe)) continue;
                            list.Add(info);
                        }
                    }
                }
                catch (Exception) { }
            }
        }
        list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return list;
    }

    private static BrowserInfo Read(RegistryKey parent, string sub)
    {
        using (RegistryKey key = parent.OpenSubKey(sub))
        {
            if (key == null) return null;
            string exe = null;
            using (RegistryKey cmd = key.OpenSubKey(@"shell\open\command"))
            {
                if (cmd != null) exe = ExeFromCommand(cmd.GetValue("") as string);
            }
            if (exe == null || !File.Exists(exe)) return null;
            if (Path.GetFileNameWithoutExtension(exe).Equals("iexplore", StringComparison.OrdinalIgnoreCase)) return null;
            string name = null;
            using (RegistryKey caps = key.OpenSubKey("Capabilities"))
            {
                if (caps != null) name = caps.GetValue("ApplicationName") as string;
            }
            if (string.IsNullOrEmpty(name) || name.StartsWith("@")) name = key.GetValue("") as string;
            if (string.IsNullOrEmpty(name)) name = sub;
            return Create(exe, name.Trim());
        }
    }

    private static BrowserInfo Create(string exe, string name)
    {
        BrowserInfo info = new BrowserInfo();
        info.Exe = exe;
        info.Id = exe;
        info.Name = name;
        info.AppWindow = Array.IndexOf(AppCapable, Path.GetFileNameWithoutExtension(exe).ToLowerInvariant()) >= 0;
        return info;
    }

    private static string ExeFromCommand(string command)
    {
        if (string.IsNullOrEmpty(command)) return null;
        command = Environment.ExpandEnvironmentVariables(command.Trim());
        if (command.StartsWith("\""))
        {
            int end = command.IndexOf('"', 1);
            return end > 1 ? command.Substring(1, end-1) : null;
        }
        int exeEnd = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exeEnd > 0 ? command.Substring(0, exeEnd + 4) : null;
    }

    public static BrowserInfo DefaultBrowser()
    {
        try
        {
            string progId = null;
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\http\UserChoice"))
            {
                if (key != null) progId = key.GetValue("ProgId") as string;
            }
            if (string.IsNullOrEmpty(progId)) return null;
            using (RegistryKey cmd = Registry.ClassesRoot.OpenSubKey(progId + @"\shell\open\command"))
            {
                string exe = cmd == null ? null : ExeFromCommand(cmd.GetValue("") as string);
                if (exe == null || !File.Exists(exe)) return null;
                foreach (BrowserInfo b in Installed())
                {
                    if (string.Equals(b.Exe, exe, StringComparison.OrdinalIgnoreCase)) return b;
                }
                return Create(exe, Path.GetFileNameWithoutExtension(exe));
            }
        }
        catch (Exception) { return null; }
    }

    private static string EdgePath()
    {
        foreach (string var in new string[] { "ProgramFiles(x86)", "ProgramFiles" })
        {
            string root = Environment.GetEnvironmentVariable(var);
            if (string.IsNullOrEmpty(root)) continue;
            string candidate = Path.Combine(root, @"Microsoft\Edge\Application\msedge.exe");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    public static void Open(string url)
    {
        string choice = Choice;
        BrowserInfo target = null;
        if (choice == Default) target = DefaultBrowser();
        else if (choice != Auto)
        {
            foreach (BrowserInfo b in Installed())
            {
                if (string.Equals(b.Id, choice, StringComparison.OrdinalIgnoreCase)) { target = b; break; }
            }
        }
        if (target == null && choice != Default)
        {
            string edge = EdgePath();
            if (edge != null) target = Create(edge, "Microsoft Edge");
        }
        try
        {
            if (target == null) { Process.Start(url); return; }
            ProcessStartInfo psi;
            if (target.AppWindow)
            {
                string profile = Path.GetFileNameWithoutExtension(target.Exe).ToLowerInvariant();
                if (profile == "msedge") profile = "edge";
                string profileDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"FS25ModInventory\" + profile);
                psi = new ProcessStartInfo(target.Exe, "--app=" + url + " --user-data-dir=\"" + profileDir + "\" --window-size=1480,940 --no-first-run --no-default-browser-check");
            }
            else
            {
                psi = new ProcessStartInfo(target.Exe, "\"" + url + "\"");
            }
            psi.UseShellExecute = false;
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            AppEnv.Log("Browser", "Start fehlgeschlagen: " + ex.Message);
            Process.Start(url);
        }
    }
}
