using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using System.Xml;
using Microsoft.Win32;

internal static class AppEnv
{
    public static string DataDir;
    public static string ModsRoot;
    public static string MapsRoot;
    public static string GameSettingsPath;
    public static string GameInstallPath;

    public static readonly string[] CategoryFolders = new string[] { "Karten", "Fahrzeuge", "Geräte", "Gebäude", "Scripts", "Eigene Mods", "Sonstige" };

    public static readonly JavaScriptSerializer Json = CreateSerializer();

    private static JavaScriptSerializer CreateSerializer()
    {
        JavaScriptSerializer s = new JavaScriptSerializer();
        s.MaxJsonLength = int.MaxValue;
        s.RecursionLimit = 256;
        return s;
    }

    public static void Init()
    {
        string exeDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
        string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        GameSettingsPath = Path.Combine(documents, @"My Games\FarmingSimulator2025\gameSettings.xml");

        string settingsDir = null;
        foreach (string cand in new string[] { exeDir, Path.Combine(exeDir, "_Tools"), Path.GetDirectoryName(exeDir) })
        {
            if (!string.IsNullOrEmpty(cand) && File.Exists(Path.Combine(cand, "settings.json"))) { settingsDir = cand; break; }
        }

        string configuredInstall = null;
        if (settingsDir != null)
        {
            try
            {
                Dictionary<string, object> s = Json.DeserializeObject(File.ReadAllText(Path.Combine(settingsDir, "settings.json"))) as Dictionary<string, object>;
                if (s != null)
                {
                    ModsRoot = GetString(s, "ModsRoot");
                    string gsp = GetString(s, "GameSettingsPath");
                    if (!string.IsNullOrEmpty(gsp)) GameSettingsPath = gsp;
                    configuredInstall = GetString(s, "GameInstallPath");
                }
            }
            catch (Exception) { }
            DataDir = settingsDir;
        }
        if (DataDir == null)
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            DataDir = Path.Combine(local, "FS25 Mod Inventory");
            string oldDir = Path.Combine(local, "LS25 Mod Inventur");
            if (!Directory.Exists(DataDir) && Directory.Exists(oldDir))
            {
                try { Directory.Move(oldDir, DataDir); } catch (Exception) { }
            }
            string oldEdge = Path.Combine(local, "LS25ModInventur");
            if (Directory.Exists(oldEdge)) { try { Directory.Delete(oldEdge, true); } catch (Exception) { } }
        }
        Directory.CreateDirectory(DataDir);
        if (!string.IsNullOrEmpty(ModsRoot)) MapsRoot = Path.Combine(ModsRoot, "Karten");
        GameInstallPath = (!string.IsNullOrEmpty(configuredInstall) && Directory.Exists(configuredInstall)) ? configuredInstall : FindInstallPath();
    }

    public static string UiLanguage()
    {
        string lang = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        return (lang == "de" || lang == "fr") ? lang : "en";
    }

    public static string GetString(Dictionary<string, object> d, string key)
    {
        object v;
        if (d == null || !d.TryGetValue(key, out v) || v == null) return null;
        return Convert.ToString(v, CultureInfo.InvariantCulture);
    }

    public static bool GetBool(Dictionary<string, object> d, string key)
    {
        object v;
        if (d == null || !d.TryGetValue(key, out v) || v == null) return false;
        if (v is bool) return (bool)v;
        return string.Equals(Convert.ToString(v, CultureInfo.InvariantCulture), "true", StringComparison.OrdinalIgnoreCase);
    }

    public static List<Dictionary<string, object>> GetObjects(Dictionary<string, object> d, string key)
    {
        object v;
        if (d == null || !d.TryGetValue(key, out v)) return new List<Dictionary<string, object>>();
        return FlattenObjects(v);
    }

    public static List<Dictionary<string, object>> FlattenObjects(object value)
    {
        List<Dictionary<string, object>> result = new List<Dictionary<string, object>>();
        Stack<object> pending = new Stack<object>();
        pending.Push(value);
        while (pending.Count > 0)
        {
            object item = pending.Pop();
            if (item == null) continue;
            Dictionary<string, object> dict = item as Dictionary<string, object>;
            if (dict != null) { result.Add(dict); continue; }
            IList list = item as IList;
            if (list != null)
            {
                for (int i = list.Count; i > 0; i--) pending.Push(list[i-1]);
            }
        }
        return result;
    }

    public static List<string> GetStrings(Dictionary<string, object> d, string key)
    {
        List<string> result = new List<string>();
        object v;
        if (d == null || !d.TryGetValue(key, out v) || v == null) return result;
        IList list = v as IList;
        if (list == null) { result.Add(Convert.ToString(v, CultureInfo.InvariantCulture)); return result; }
        foreach (object o in list) if (o != null) result.Add(Convert.ToString(o, CultureInfo.InvariantCulture));
        return result;
    }

    public static void Log(string action, string details)
    {
        string line = "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "] " + action;
        if (!string.IsNullOrEmpty(details)) line += ": " + details;
        try { File.AppendAllText(Path.Combine(DataDir, "wartung.log"), line + Environment.NewLine, new UTF8Encoding(true)); }
        catch (Exception) { }
    }

    public static bool GameRunning()
    {
        bool running = false;
        foreach (Process p in Process.GetProcesses())
        {
            try { if (p.ProcessName.IndexOf("Farming", StringComparison.OrdinalIgnoreCase) >= 0) running = true; }
            catch (Exception) { }
            finally { p.Dispose(); }
        }
        return running;
    }

    public static string BackupFile(string path)
    {
        if (!File.Exists(path)) return null;
        string backupDir = Path.Combine(Path.GetDirectoryName(path), "_ModManagerBackups");
        Directory.CreateDirectory(backupDir);
        string stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);
        string backupPath = Path.Combine(backupDir, Path.GetFileName(path) + "." + stamp + ".bak");
        File.Copy(path, backupPath, true);
        return backupPath;
    }

    public static string Fingerprint(string path)
    {
        if (!File.Exists(path)) return "";
        FileInfo fi = new FileInfo(path);
        return fi.Length.ToString(CultureInfo.InvariantCulture) + "_" + fi.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture);
    }

    public static string ActiveModsDirectoryOverride()
    {
        if (!File.Exists(GameSettingsPath)) return null;
        try
        {
            XmlDocument doc = new XmlDocument();
            doc.Load(GameSettingsPath);
            XmlElement node = doc.SelectSingleNode("//modsDirectoryOverride") as XmlElement;
            if (node == null) return null;
            if (node.HasAttribute("active") && !string.Equals(node.GetAttribute("active").Trim(), "true", StringComparison.OrdinalIgnoreCase)) return null;
            string dir = node.GetAttribute("directory");
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return null;
            return dir;
        }
        catch (Exception) { return null; }
    }

    public static string DefaultModsFolder()
    {
        return Path.Combine(Path.GetDirectoryName(GameSettingsPath), "mods");
    }

    public static string ConfiguredModsFolder()
    {
        string overrideDir = ActiveModsDirectoryOverride();
        return !string.IsNullOrEmpty(overrideDir) ? overrideDir : DefaultModsFolder();
    }

    private static string lastLogPath;
    private static DateTime lastLogStamp;
    private static string lastLogFolder;

    public static string LastLoggedModsFolder()
    {
        string logPath = Path.Combine(Path.GetDirectoryName(GameSettingsPath), "log.txt");
        if (!File.Exists(logPath)) return null;
        try
        {
            DateTime stamp = File.GetLastWriteTimeUtc(logPath);
            if (logPath == lastLogPath && stamp == lastLogStamp) return lastLogFolder;
            string found = null;
            using (FileStream fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (StreamReader reader = new StreamReader(fs, Encoding.UTF8, true))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    int idx = line.IndexOf("Mod Directory:", StringComparison.Ordinal);
                    if (idx >= 0) found = line.Substring(idx + "Mod Directory:".Length).Trim();
                }
            }
            if (!string.IsNullOrEmpty(found))
            {
                found = found.Replace('/', '\\').TrimEnd('\\');
                if (!Directory.Exists(found)) found = null;
            }
            lastLogPath = logPath;
            lastLogStamp = stamp;
            lastLogFolder = found;
            return found;
        }
        catch (Exception) { return null; }
    }

    public static List<string> CandidateModsFolders()
    {
        List<string> result = new List<string>();
        foreach (string f in new string[] { ConfiguredModsFolder(), LastLoggedModsFolder(), DefaultModsFolder() })
        {
            if (string.IsNullOrEmpty(f)) continue;
            string norm = f.TrimEnd('\\');
            bool known = false;
            foreach (string r in result) if (string.Equals(r, norm, StringComparison.OrdinalIgnoreCase)) known = true;
            if (!known) result.Add(norm);
        }
        return result;
    }

    public static string FindInstallPath()
    {
        try
        {
            string steam = ReadRegistry(@"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath");
            if (string.IsNullOrEmpty(steam)) steam = ReadRegistry(@"SOFTWARE\Valve\Steam", "InstallPath");
            if (!string.IsNullOrEmpty(steam))
            {
                string vdf = Path.Combine(steam, @"steamapps\libraryfolders.vdf");
                if (File.Exists(vdf))
                {
                    foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s*\"([^\"]+)\""))
                    {
                        string candidate = Path.Combine(m.Groups[1].Value.Replace(@"\\", @"\"), @"steamapps\common\Farming Simulator 25");
                        if (Directory.Exists(candidate)) return candidate;
                    }
                }
            }
        }
        catch (Exception) { }
        foreach (string c in new string[] { @"C:\Program Files (x86)\Farming Simulator 25", @"C:\Program Files\Farming Simulator 25" })
        {
            if (Directory.Exists(c)) return c;
        }
        return null;
    }

    private static string ReadRegistry(string subKey, string name)
    {
        using (RegistryKey key = Registry.LocalMachine.OpenSubKey(subKey))
        {
            if (key == null) return null;
            object v = key.GetValue(name);
            return v == null ? null : v.ToString();
        }
    }

    public static bool IsReparsePoint(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint; }
        catch (Exception) { return false; }
    }

    public static void RemoveDirectoryLinkSafely(string path)
    {
        if (!IsReparsePoint(path)) throw new InvalidOperationException("'" + path + "' ist kein Junction/SymbolicLink, sondern ein echter Ordner. Wird NICHT geloescht.");
        Directory.Delete(path, false);
    }

    public static string ScopedSetupPath(string karteDir)
    {
        return Path.Combine(karteDir, ".modmanager_scoped_setup.json");
    }

    public static bool IsScopedSetup(string karteDir)
    {
        return File.Exists(ScopedSetupPath(karteDir));
    }

    public static void SetScopedSetup(string karteDir)
    {
        File.WriteAllText(ScopedSetupPath(karteDir), "{\"ScopedSetup\":true}", new UTF8Encoding(true));
    }

    public static ModTarget FindLibraryMod(string modName)
    {
        if (string.IsNullOrEmpty(ModsRoot) || !Directory.Exists(ModsRoot)) return null;
        List<ModTarget> matches = new List<ModTarget>();
        List<string> roots = new List<string>();
        roots.Add(ModsRoot);
        foreach (string f in CategoryFolders)
        {
            string dir = Path.Combine(ModsRoot, f);
            if (Directory.Exists(dir)) roots.Add(dir);
        }
        foreach (string root in roots)
        {
            string zip = Path.Combine(root, modName + ".zip");
            if (File.Exists(zip)) matches.Add(new ModTarget(zip, false));
            string dir = Path.Combine(root, modName);
            if (Directory.Exists(dir)) matches.Add(new ModTarget(dir, true));
        }
        return matches.Count == 1 ? matches[0] : null;
    }
}

internal sealed class ModTarget
{
    public readonly string Path;
    public readonly bool IsDir;

    public ModTarget(string path, bool isDir)
    {
        Path = path;
        IsDir = isDir;
    }
}
