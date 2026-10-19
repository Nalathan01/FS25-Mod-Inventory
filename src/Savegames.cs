using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Xml;

internal sealed class SavegameInfo
{
    public string Folder;
    public string Id;
    public string SavegameName;
    public string MapId;
    public string MapTitle;
    public double PlayTimeHours;
    public string FarmName;

    public string MapModName
    {
        get { return string.IsNullOrEmpty(MapId) ? null : MapId.Split('.')[0]; }
    }
}

internal static class Savegames
{
    public static string Root
    {
        get { return Path.GetDirectoryName(AppEnv.GameSettingsPath); }
    }

    public static List<string> Folders()
    {
        List<string> result = new List<string>();
        if (!Directory.Exists(Root)) return result;
        foreach (string dir in Directory.GetDirectories(Root, "savegame*"))
        {
            if (File.Exists(Path.Combine(dir, "careerSavegame.xml"))) result.Add(dir);
        }
        result.Sort(delegate (string a, string b) { return SlotNumber(a).CompareTo(SlotNumber(b)); });
        return result;
    }

    public static int SlotNumber(string folder)
    {
        string num = Path.GetFileName(folder).Substring("savegame".Length);
        int n;
        return int.TryParse(num, NumberStyles.None, CultureInfo.InvariantCulture, out n) ? n : int.MaxValue;
    }

    public static string FolderFor(string id)
    {
        if (id == null || !Regex.IsMatch(id, @"^savegame\d+$")) throw new InvalidOperationException("Ungueltiger Spielstand: " + id);
        string folder = Path.Combine(Root, id);
        if (!File.Exists(Path.Combine(folder, "careerSavegame.xml"))) throw new InvalidOperationException("Spielstand nicht gefunden: " + id);
        return folder;
    }

    public static SavegameInfo Info(string folder)
    {
        string careerPath = Path.Combine(folder, "careerSavegame.xml");
        if (!File.Exists(careerPath)) return null;
        XmlDocument career = new XmlDocument();
        try { career.Load(careerPath); }
        catch (Exception ex)
        {
            AppEnv.Log("Savegames", careerPath + " konnte nicht gelesen werden (beschaedigt oder gerade vom Spiel geschrieben), Spielstand wird uebersprungen: " + ex.Message);
            return null;
        }
        SavegameInfo info = new SavegameInfo();
        info.Folder = folder;
        info.Id = Path.GetFileName(folder);
        info.SavegameName = Text(career, "/careerSavegame/settings/savegameName");
        info.MapId = Text(career, "/careerSavegame/settings/mapId");
        info.MapTitle = Text(career, "/careerSavegame/settings/mapTitle");
        double playTime;
        if (double.TryParse(Text(career, "/careerSavegame/statistics/playTime"), NumberStyles.Float, CultureInfo.InvariantCulture, out playTime))
        {
            info.PlayTimeHours = Math.Round(playTime, 1);
        }
        string farmsPath = Path.Combine(folder, "farms.xml");
        if (File.Exists(farmsPath))
        {
            try
            {
                XmlDocument farms = new XmlDocument();
                farms.Load(farmsPath);
                foreach (XmlElement farm in farms.SelectNodes("/farms/farm"))
                {
                    string name = farm.GetAttribute("name");
                    if (!string.IsNullOrEmpty(name)) { info.FarmName = name; break; }
                }
            }
            catch (Exception) { }
        }
        return info;
    }

    private static string Text(XmlDocument doc, string xpath)
    {
        XmlNode node = doc.SelectSingleNode(xpath);
        return node == null ? null : node.InnerText;
    }

    public static List<string> ActiveModNames(string folder)
    {
        List<string> result = new List<string>();
        string careerPath = Path.Combine(folder, "careerSavegame.xml");
        if (!File.Exists(careerPath)) return result;
        try
        {
            XmlDocument doc = new XmlDocument();
            doc.Load(careerPath);
            foreach (XmlElement mod in doc.SelectNodes("/careerSavegame/mod"))
            {
                string name = mod.GetAttribute("modName");
                if (!string.IsNullOrEmpty(name)) result.Add(name);
            }
        }
        catch (Exception ex)
        {
            AppEnv.Log("Savegames", careerPath + " konnte nicht gelesen werden (beschaedigt oder gerade vom Spiel geschrieben): " + ex.Message);
        }
        return result;
    }

    public static string DataDir
    {
        get
        {
            string dir = Path.Combine(AppEnv.DataDir, "savegame_data");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string KarteOverride(string folder)
    {
        string path = Path.Combine(DataDir, Path.GetFileName(folder) + ".json");
        if (!File.Exists(path)) return null;
        try
        {
            Dictionary<string, object> data = AppEnv.Json.DeserializeObject(File.ReadAllText(path)) as Dictionary<string, object>;
            return AppEnv.GetString(data, "KarteOverride");
        }
        catch (Exception) { return null; }
    }

    public static string ResolveKarteDir(string folder, string mapId)
    {
        if (string.IsNullOrEmpty(AppEnv.MapsRoot) || !Directory.Exists(AppEnv.MapsRoot)) return null;
        string overrideName = KarteOverride(folder);
        if (!string.IsNullOrEmpty(overrideName))
        {
            string overrideDir = Path.Combine(AppEnv.MapsRoot, overrideName);
            if (Directory.Exists(overrideDir)) return overrideDir;
        }
        if (string.IsNullOrEmpty(mapId) || mapId.Trim().Length == 0) return null;
        HashSet<string> exclude = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string other in Folders())
        {
            if (string.Equals(other, folder, StringComparison.OrdinalIgnoreCase)) continue;
            string name = KarteOverride(other);
            if (!string.IsNullOrEmpty(name)) exclude.Add(name);
        }
        string modName = mapId.Split('.')[0];
        List<string> found = new List<string>();
        foreach (string dir in Directory.GetDirectories(AppEnv.MapsRoot))
        {
            if (exclude.Contains(Path.GetFileName(dir))) continue;
            if (File.Exists(Path.Combine(dir, modName + ".zip"))) found.Add(dir);
        }
        return found.Count == 1 ? found[0] : null;
    }
}
