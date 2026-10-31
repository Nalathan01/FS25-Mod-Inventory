using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

internal sealed class ModLocation
{
    public string Path;
    public bool IsDir;
    public bool InFolder;

    public ModLocation(string path, bool isDir, bool inFolder)
    {
        Path = path;
        IsDir = isDir;
        InFolder = inFolder;
    }
}

internal sealed class ModProfile
{
    public string Name;
    public string Title;
    public bool IsMap;
    public int StoreItemCount;
    public int VehicleItemCount;
    public bool HasScripts;
    public bool HasGlobalScripts;
    public List<string> FillTypes = new List<string>();
    public List<string> Dependencies = new List<string>();
    public long SizeBytes;
    public bool Readable;
    public string Version;
    public string Author;
    public string IconFilename;
}

internal sealed class ModRow
{
    public string Name;
    public string Title;
    public string Status;
    public bool Active;
    public List<string> Reasons;
    public string Warning;
    public int StoreItemCount;
    public bool HasScripts;
    public double SizeMB;
    public string Version;
    public string Author;
    public string IconFilename;
    public string ModPath;
    public bool ModIsDir;
}

internal sealed class UsageResult
{
    public SavegameInfo Savegame;
    public string ModsFolder;
    public string FolderKind;
    public bool FolderMismatch;
    public List<ModRow> Mods;
    public List<string> UnassignedFiles;
}

internal sealed class ObjectRef
{
    public string File;
    public string Label;
}

internal sealed class StockInfo
{
    public double Liters;
    public List<string> Files = new List<string>();
}

internal sealed class Evidence
{
    public Dictionary<string, List<ObjectRef>> Objects = new Dictionary<string, List<ObjectRef>>(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int> References = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> ElementNames = new HashSet<string>();
    public Dictionary<string, StockInfo> Stock = new Dictionary<string, StockInfo>(StringComparer.OrdinalIgnoreCase);
}

internal static class Usage
{
    private static readonly string[] ObjectFiles = new string[] { "placeables.xml", "vehicles.xml", "items.xml", "handTools.xml" };

    public static readonly Dictionary<string, string> AlwaysProtected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        { "FS25_precisionFarming", "immer gesperrt, fuehrt Bodenkarten fuer alle Felder im Spielstand" }
    };

    private static readonly HashSet<string> VanillaFiles = new HashSet<string>(new string[] {
        "careerSavegame.xml", "placeables.xml", "vehicles.xml", "items.xml", "handTools.xml", "farms.xml", "farmland.xml",
        "fields.xml", "economy.xml", "environment.xml", "missions.xml", "npc.xml", "players.xml", "sales.xml",
        "densityMapHeight.xml", "treePlant.xml", "treeMarker.xml", "guidedTour.xml", "navigationSystem.xml",
        "onCreateObjects.xml", "snow_state.xml", "weed_growthState.xml", "stone_growthState.xml",
        "densityMap_fruits_growthState.xml", "collectibles.xml",
        "destructibleMapObjectSystem.xml", "steam_autocloud.vdf"
    }, StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, ModProfile> ProfileCache = new Dictionary<string, ModProfile>(StringComparer.OrdinalIgnoreCase);

    public static string NormalizedName(string text)
    {
        string t = text.ToLowerInvariant();
        t = Regex.Replace(t, "^fs25_", "");
        t = Regex.Replace(t, @"\.xml$", "");
        t = Regex.Replace(t, "_crossplay$", "");
        return Regex.Replace(t, "[^a-z0-9]", "");
    }

    public static bool NameMatch(string candidate, string modName)
    {
        string a = NormalizedName(candidate);
        string b = NormalizedName(modName);
        if (a.Length == 0 || b.Length < 6) return a == b && a.Length > 0;
        return a.Contains(b);
    }

    public static string ReadZipText(ZipArchive zip, string entryPath)
    {
        string normalized = entryPath.Replace('\\', '/').TrimStart('/');
        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            if (!string.Equals(entry.FullName.Replace('\\', '/'), normalized, StringComparison.OrdinalIgnoreCase)) continue;
            using (StreamReader reader = new StreamReader(entry.Open(), Encoding.UTF8, true))
            {
                return reader.ReadToEnd();
            }
        }
        return null;
    }

    private static string ZipRootName(ZipArchive zip, string entryPath)
    {
        if (zip == null || string.IsNullOrEmpty(entryPath)) return null;
        string normalized = entryPath.Replace('\\', '/').TrimStart('/');
        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            if (!string.Equals(entry.FullName.Replace('\\', '/'), normalized, StringComparison.OrdinalIgnoreCase)) continue;
            using (Stream st = entry.Open()) return RootName(st);
        }
        return null;
    }

    private static string FileRootName(string path)
    {
        if (!File.Exists(path)) return null;
        using (Stream st = File.OpenRead(path)) return RootName(st);
    }

    private static string RootName(Stream st)
    {
        try
        {
            XmlReaderSettings settings = new XmlReaderSettings();
            settings.DtdProcessing = DtdProcessing.Ignore;
            using (XmlReader reader = XmlReader.Create(st, settings))
            {
                reader.MoveToContent();
                string type = (reader.GetAttribute("type") ?? "").ToLowerInvariant();
                if (reader.LocalName == "vehicle" && (type.Contains("pallet") || type.Contains("bigbag"))) return "pallet";
                return reader.LocalName;
            }
        }
        catch (Exception) { return null; }
    }

    public static ModLocation FindMod(string modName, string folder)
    {
        if (!string.IsNullOrEmpty(folder))
        {
            string zip = Path.Combine(folder, modName + ".zip");
            if (File.Exists(zip)) return new ModLocation(zip, false, true);
            string dir = Path.Combine(folder, modName);
            if (File.Exists(Path.Combine(dir, "modDesc.xml"))) return new ModLocation(dir, true, true);
        }
        ModTarget target = AppEnv.FindLibraryMod(modName);
        if (target != null) return new ModLocation(target.Path, target.IsDir, false);
        return null;
    }

    public static ModProfile Profile(string modName, ModLocation location)
    {
        if (location == null) return LoadProfile(modName, null);
        string fp = location.IsDir ? AppEnv.Fingerprint(Path.Combine(location.Path, "modDesc.xml")) : AppEnv.Fingerprint(location.Path);
        string key = location.Path + "|" + fp;
        ModProfile cached;
        if (!ProfileCache.TryGetValue(key, out cached))
        {
            cached = LoadProfile(modName, location);
            ProfileCache[key] = cached;
        }
        return cached;
    }

    private static ModProfile LoadProfile(string modName, ModLocation location)
    {
        ModProfile prof = new ModProfile();
        prof.Name = modName;
        prof.Title = modName;
        if (location == null) return prof;

        ZipArchive zip = null;
        try
        {
            string content;
            if (location.IsDir)
            {
                content = File.ReadAllText(Path.Combine(location.Path, "modDesc.xml"));
                prof.SizeBytes = DirectorySize(location.Path, false);
            }
            else
            {
                prof.SizeBytes = new FileInfo(location.Path).Length;
                zip = ZipFile.OpenRead(location.Path);
                content = ReadZipText(zip, "modDesc.xml");
            }
            if (string.IsNullOrEmpty(content)) return prof;
            XmlDocument xml = new XmlDocument();
            xml.LoadXml(content);
            XmlElement md = xml.SelectSingleNode("/modDesc") as XmlElement;
            if (md == null) return prof;
            prof.Readable = true;
            prof.IsMap = md.SelectSingleNode("maps") != null;
            string title = LocalizedTitle(md);
            if (!string.IsNullOrEmpty(title)) prof.Title = title;
            prof.Version = TrimmedText(md, "version");
            prof.Author = TrimmedText(md, "author");
            prof.IconFilename = TrimmedText(md, "iconFilename");
            prof.StoreItemCount = md.SelectNodes("storeItems/storeItem").Count;
            foreach (XmlElement item in md.SelectNodes("storeItems/storeItem[@xmlFilename]"))
            {
                string root = location.IsDir ? FileRootName(Path.Combine(location.Path, item.GetAttribute("xmlFilename"))) : ZipRootName(zip, item.GetAttribute("xmlFilename"));
                if (root == "vehicle" || root == "handTool") prof.VehicleItemCount++;
            }
            int scripts = md.SelectNodes("extraSourceFiles/sourceFile").Count
                + md.SelectNodes("specializations/specialization").Count
                + md.SelectNodes("placeableSpecializations/specialization").Count
                + md.SelectNodes("handToolSpecializations/specialization").Count;
            prof.HasScripts = scripts > 0;
            prof.HasGlobalScripts = md.SelectNodes("extraSourceFiles/sourceFile").Count > 0;
            foreach (XmlNode dep in md.SelectNodes("dependencies/dependency"))
            {
                string d = dep.InnerText.Trim();
                if (d.Length > 0) prof.Dependencies.Add(d);
            }

            HashSet<string> fillTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (XmlElement ftNode in md.SelectNodes("fillTypes[@filename]"))
            {
                string ftFile = ftNode.GetAttribute("filename");
                string ftText = null;
                if (location.IsDir)
                {
                    string ftPath = Path.Combine(location.Path, ftFile);
                    if (File.Exists(ftPath)) ftText = File.ReadAllText(ftPath);
                }
                else if (zip != null)
                {
                    ftText = ReadZipText(zip, ftFile);
                }
                if (string.IsNullOrEmpty(ftText)) continue;
                try
                {
                    XmlDocument ftXml = new XmlDocument();
                    ftXml.LoadXml(ftText);
                    foreach (XmlElement n in ftXml.SelectNodes("//fillTypes/fillType[@name]"))
                    {
                        string name = n.GetAttribute("name").ToUpperInvariant();
                        if (fillTypes.Add(name)) prof.FillTypes.Add(name);
                    }
                }
                catch (Exception) { }
            }
        }
        catch (Exception ex)
        {
            AppEnv.Log("ModUsage", "modDesc.xml von " + modName + " konnte nicht gelesen werden: " + ex.Message);
        }
        finally
        {
            if (zip != null) zip.Dispose();
        }
        return prof;
    }

    private static string LocalizedTitle(XmlElement md)
    {
        XmlNode titleNode = md.SelectSingleNode("title");
        if (titleNode == null) return null;
        List<string> langs = new List<string>();
        foreach (string l in new string[] { CultureInfo.CurrentCulture.TwoLetterISOLanguageName, "de", "en" })
        {
            if (!langs.Contains(l)) langs.Add(l);
        }
        foreach (string lang in langs)
        {
            XmlNode node = titleNode.SelectSingleNode(lang);
            if (node != null && node.InnerText.Trim().Length > 0) return node.InnerText.Trim();
        }
        return null;
    }

    private static string TrimmedText(XmlElement md, string name)
    {
        XmlNode node = md.SelectSingleNode(name);
        if (node == null) return null;
        string t = node.InnerText.Trim();
        return t.Length == 0 ? null : t;
    }

    public static long DirectorySize(string dir, bool includeHidden)
    {
        long total = 0;
        Stack<string> pending = new Stack<string>();
        pending.Push(dir);
        while (pending.Count > 0)
        {
            string current = pending.Pop();
            try
            {
                foreach (string f in Directory.GetFiles(current))
                {
                    try
                    {
                        FileInfo fi = new FileInfo(f);
                        if (!includeHidden && (fi.Attributes & FileAttributes.Hidden) == FileAttributes.Hidden) continue;
                        total += fi.Length;
                    }
                    catch (Exception) { }
                }
                foreach (string d in Directory.GetDirectories(current))
                {
                    if (!includeHidden && (File.GetAttributes(d) & FileAttributes.Hidden) == FileAttributes.Hidden) continue;
                    pending.Push(d);
                }
            }
            catch (Exception) { }
        }
        return total;
    }

    private static Evidence CollectEvidence(string savegameFolder)
    {
        Evidence ev = new Evidence();
        Regex moddirStart = new Regex(@"^\$moddir\$([^/\\]+)", RegexOptions.IgnoreCase);
        Regex moddirAny = new Regex(@"\$moddir\$([^/\\""]+)");
        foreach (string fileName in ObjectFiles)
        {
            string path = Path.Combine(savegameFolder, fileName);
            if (!File.Exists(path)) continue;
            XmlDocument xml = new XmlDocument();
            try { xml.Load(path); }
            catch (Exception ex)
            {
                AppEnv.Log("ModUsage", path + " konnte nicht gelesen werden: " + ex.Message);
                continue;
            }
            if (xml.DocumentElement == null) continue;
            foreach (XmlNode child in xml.DocumentElement.ChildNodes)
            {
                XmlElement obj = child as XmlElement;
                if (obj == null) continue;
                string modName = obj.GetAttribute("modName");
                string file = obj.GetAttribute("filename");
                if (string.IsNullOrEmpty(modName))
                {
                    Match m = moddirStart.Match(file);
                    if (m.Success) modName = m.Groups[1].Value;
                }
                if (string.IsNullOrEmpty(modName)) continue;
                List<ObjectRef> list;
                if (!ev.Objects.TryGetValue(modName, out list)) { list = new List<ObjectRef>(); ev.Objects[modName] = list; }
                ObjectRef r = new ObjectRef();
                r.File = fileName;
                r.Label = string.IsNullOrEmpty(file) ? obj.LocalName : Path.GetFileNameWithoutExtension(file);
                list.Add(r);
            }

            foreach (Match m in moddirAny.Matches(xml.OuterXml))
            {
                string n = m.Groups[1].Value;
                int count;
                ev.References.TryGetValue(n, out count);
                ev.References[n] = count + 1;
            }

            foreach (XmlNode el in xml.SelectNodes("//*")) ev.ElementNames.Add(el.LocalName);

            foreach (XmlElement node in xml.SelectNodes("//*[@fillType and @fillLevel]"))
            {
                double level;
                if (!double.TryParse(node.GetAttribute("fillLevel"), NumberStyles.Float, CultureInfo.InvariantCulture, out level)) continue;
                if (level <= 0) continue;
                string ft = node.GetAttribute("fillType").ToUpperInvariant();
                StockInfo s;
                if (!ev.Stock.TryGetValue(ft, out s)) { s = new StockInfo(); ev.Stock[ft] = s; }
                s.Liters += level;
                if (!s.Files.Contains(fileName)) s.Files.Add(fileName);
            }
        }
        return ev;
    }

    private static List<string> ForeignFiles(string savegameFolder)
    {
        List<string> result = new List<string>();
        try
        {
            foreach (string f in Directory.GetFiles(savegameFolder))
            {
                string name = Path.GetFileName(f);
                if (string.Equals(Path.GetExtension(f), ".xml", StringComparison.OrdinalIgnoreCase) && !VanillaFiles.Contains(name)) result.Add(name);
            }
        }
        catch (Exception) { }
        return result;
    }

    public static UsageResult Analyze(string savegameFolder)
    {
        SavegameInfo info = Savegames.Info(savegameFolder);
        if (info == null) throw new InvalidOperationException("Spielstand nicht lesbar: " + savegameFolder);
        string mapModName = info.MapModName;
        FolderContext context = FolderOps.Context(savegameFolder, info.MapId);
        string folder = context.Folder;

        List<string> activeList = Savegames.ActiveModNames(savegameFolder);
        HashSet<string> active = new HashSet<string>(activeList, StringComparer.OrdinalIgnoreCase);
        Evidence ev = CollectEvidence(savegameFolder);
        List<string> foreignFiles = ForeignFiles(savegameFolder);
        string salesPath = Path.Combine(savegameFolder, "sales.xml");
        string salesText = File.Exists(salesPath) ? File.ReadAllText(salesPath).Replace('\\', '/') : null;

        List<string> allNames = new List<string>();
        HashSet<string> allSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string n in activeList) if (allSet.Add(n)) allNames.Add(n);
        foreach (string n in ev.Objects.Keys) if (allSet.Add(n)) allNames.Add(n);

        Dictionary<string, ModLocation> locations = new Dictionary<string, ModLocation>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, ModProfile> profiles = new Dictionary<string, ModProfile>(StringComparer.OrdinalIgnoreCase);
        foreach (string n in allNames)
        {
            locations[n] = FindMod(n, folder);
            profiles[n] = Profile(n, locations[n]);
        }

        Dictionary<string, List<string>> reasons = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> status = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string n in allNames) reasons[n] = new List<string>();

        foreach (string n in allNames)
        {
            if (AlwaysProtected.ContainsKey(n)) { status[n] = "Gesperrt"; reasons[n].Add(AlwaysProtected[n]); continue; }
            if (string.Equals(n, mapModName, StringComparison.OrdinalIgnoreCase) || profiles[n].IsMap) { status[n] = "Karte"; reasons[n].Add("Karte dieses Spielstands"); continue; }
            List<ObjectRef> objs;
            if (ev.Objects.TryGetValue(n, out objs))
            {
                status[n] = "Benutzt";
                List<string> files = new List<string>();
                foreach (ObjectRef o in objs) if (!files.Contains(o.File)) files.Add(o.File);
                foreach (string file in files)
                {
                    List<ObjectRef> inFile = objs.Where(o => o.File == file).ToList();
                    List<string> labelOrder = new List<string>();
                    Dictionary<string, int> labelCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    foreach (ObjectRef o in inFile)
                    {
                        int c;
                        if (!labelCount.TryGetValue(o.Label, out c)) labelOrder.Add(o.Label);
                        labelCount[o.Label] = c + 1;
                    }
                    IEnumerable<string> top = labelOrder.OrderByDescending(l => labelCount[l]).Take(4)
                        .Select(l => labelCount[l] > 1 ? labelCount[l].ToString(CultureInfo.InvariantCulture) + "x " + l : l);
                    reasons[n].Add(inFile.Count.ToString(CultureInfo.InvariantCulture) + " in " + file + ": " + string.Join(", ", top.ToArray()));
                }
                continue;
            }
            int refs;
            if (ev.References.TryGetValue(n, out refs))
            {
                status[n] = "Benutzt";
                reasons[n].Add(refs.ToString(CultureInfo.InvariantCulture) + " Verweise in Fahrzeug/Objektdaten");
                continue;
            }
            List<string> usedFillTypes = profiles[n].FillTypes.Where(ft => ev.Stock.ContainsKey(ft)).ToList();
            if (usedFillTypes.Count > 0)
            {
                status[n] = "Indirekt";
                foreach (string ft in usedFillTypes)
                {
                    StockInfo s = ev.Stock[ft];
                    reasons[n].Add("Fuelltyp " + ft + " mit " + Math.Round(s.Liters).ToString(CultureInfo.InvariantCulture) + " l Bestand (" + string.Join(", ", s.Files.ToArray()) + ")");
                }
            }
        }

        Queue<string> queue = new Queue<string>();
        foreach (string n in allNames)
        {
            string st;
            if (status.TryGetValue(n, out st) && (st == "Karte" || st == "Benutzt" || st == "Indirekt" || st == "Gesperrt")) queue.Enqueue(n);
        }
        HashSet<string> visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (queue.Count > 0)
        {
            string cur = queue.Dequeue();
            if (!visited.Add(cur)) continue;
            ModProfile curProfile;
            if (!profiles.TryGetValue(cur, out curProfile)) continue;
            foreach (string dep in curProfile.Dependencies)
            {
                if (!allSet.Contains(dep)) continue;
                if (!status.ContainsKey(dep)) status[dep] = "Abhaengigkeit";
                if (status[dep] == "Abhaengigkeit") reasons[dep].Add("benoetigt von " + cur);
                queue.Enqueue(dep);
            }
        }

        foreach (string n in allNames)
        {
            if (status.ContainsKey(n)) continue;
            ModProfile p = profiles[n];
            if (n.StartsWith("pdlc_", StringComparison.OrdinalIgnoreCase)) { status[n] = "DLC"; reasons[n].Add("offizieller DLC, wird vom Spiel selbst verwaltet"); continue; }
            if (!p.Readable)
            {
                status[n] = "Unbekannt";
                reasons[n].Add(locations[n] != null ? "modDesc.xml nicht lesbar" : "nicht im Kartenordner und nicht in der Bibliothek gefunden");
                continue;
            }
            string current = n;
            List<string> dataFiles = foreignFiles.Where(f => NameMatch(f, current)).ToList();
            List<string> dataElements = ev.ElementNames.Where(e => NameMatch(e, current)).ToList();
            bool hasData = dataFiles.Count > 0 || dataElements.Count > 0;
            if (p.StoreItemCount == 0 || (p.HasScripts && hasData))
            {
                status[n] = "Script";
                if (p.StoreItemCount > 0) reasons[n].Add(p.StoreItemCount.ToString(CultureInfo.InvariantCulture) + " Shop-Artikel ungenutzt, aber die Scripts des Mods haben Daten im Spielstand");
                if (dataFiles.Count > 0) reasons[n].Add("eigene Daten im Spielstand: " + string.Join(", ", dataFiles.ToArray()));
                if (dataElements.Count > 0) reasons[n].Add("Daten an Fahrzeugen/Objekten: <" + string.Join(">, <", dataElements.ToArray()) + ">");
                if (dataFiles.Count == 0 && dataElements.Count == 0) reasons[n].Add("keine eigenen Daten im Spielstand gefunden");
                continue;
            }
            status[n] = p.VehicleItemCount > 0 ? "Fahrzeug" : (p.HasGlobalScripts ? "Script" : "Unbenutzt");
            reasons[n].Add(p.StoreItemCount.ToString(CultureInfo.InvariantCulture) + " Shop-Artikel, keiner davon im Spielstand");
            if (salesText != null && salesText.Contains("/" + n + "/")) reasons[n].Add("hat gerade ein Angebot im Gebrauchtmarkt, das beim Deaktivieren verschwindet");
            if (p.HasScripts) reasons[n].Add("enthaelt zusaetzlich Scripts, vor dem Deaktivieren pruefen");
        }

        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (string n in allNames)
            {
                string st = status[n];
                if (st != "Script" && st != "Unbekannt" && st != "DLC") continue;
                if (!active.Contains(n)) continue;
                foreach (string dep in profiles[n].Dependencies)
                {
                    if (!allSet.Contains(dep)) continue;
                    if (status[dep] == "Unbenutzt" || status[dep] == "Script" || status[dep] == "Fahrzeug")
                    {
                        status[dep] = "Abhaengigkeit";
                        reasons[dep].Clear();
                        changed = true;
                    }
                    string reason = "benoetigt von " + n;
                    if (status[dep] == "Abhaengigkeit" && !reasons[dep].Contains(reason)) reasons[dep].Add(reason);
                }
            }
        }

        bool mismatch = false;
        if (context.Kind == "Spielordner")
        {
            if (!string.IsNullOrEmpty(mapModName) && allSet.Contains(mapModName) && !(locations[mapModName] != null && locations[mapModName].InFolder)) mismatch = true;
            List<string> activeMods = activeList.Where(a => !a.StartsWith("pdlc_", StringComparison.OrdinalIgnoreCase)).ToList();
            int notInFolder = activeMods.Count(a => !(locations.ContainsKey(a) && locations[a] != null && locations[a].InFolder));
            if (notInFolder >= 5 && notInFolder >= activeMods.Count / 4.0) mismatch = true;
        }

        List<ModRow> rows = new List<ModRow>();
        foreach (string n in allNames)
        {
            ModProfile p = profiles[n];
            bool isActive = active.Contains(n);
            string warning = null;
            if (!mismatch && isActive)
            {
                List<string> missingDeps = p.Dependencies.Where(d => !d.StartsWith("pdlc_", StringComparison.OrdinalIgnoreCase) && !active.Contains(d)).ToList();
                if (missingDeps.Count > 0) warning = "braucht " + string.Join(", ", missingDeps.ToArray()) + ", im Spielstand aber nicht aktiv";
            }
            string st = status[n];
            if (!mismatch && warning == null && (st == "Benutzt" || st == "Indirekt" || st == "Abhaengigkeit"))
            {
                if (!isActive) warning = "im Spielstand benutzt, aber nicht aktiv: Objekte gehen beim naechsten Speichern verloren";
                else if (locations[n] == null) warning = "im Spielstand benutzt, aber Mod-Datei fehlt: Objekte gehen beim naechsten Speichern verloren";
                else if (!string.IsNullOrEmpty(folder) && !locations[n].InFolder) warning = "im Spielstand benutzt, liegt aber nicht im Mods-Ordner dieses Spielstands";
            }
            ModRow row = new ModRow();
            row.Name = n;
            row.Title = p.Title;
            row.Status = st;
            row.Active = isActive;
            row.Reasons = reasons[n];
            row.Warning = warning;
            row.StoreItemCount = p.StoreItemCount;
            row.HasScripts = p.HasScripts;
            row.SizeMB = Math.Round(p.SizeBytes / 1048576.0, 1);
            row.Version = p.Version;
            row.Author = p.Author;
            row.IconFilename = p.IconFilename;
            row.ModPath = locations[n] != null ? locations[n].Path : null;
            row.ModIsDir = locations[n] != null && locations[n].IsDir;
            rows.Add(row);
        }

        List<string> unassigned = foreignFiles.Where(f => !allNames.Any(n => NameMatch(f, n))).ToList();

        UsageResult result = new UsageResult();
        result.Savegame = info;
        result.ModsFolder = folder;
        result.FolderKind = context.Kind;
        result.FolderMismatch = mismatch;
        result.Mods = rows.OrderBy(r => r.Status, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
        result.UnassignedFiles = unassigned;
        return result;
    }
}
