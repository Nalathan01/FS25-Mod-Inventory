using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Xml;

internal sealed class FolderContext
{
    public string Kind;
    public string Folder;
}

internal sealed class SharingSavegame
{
    public string Folder;
    public string Id;
    public string Name;
    public string MapId;
}

internal sealed class FolderEntry
{
    public string Name;
    public string Path;
    public bool IsDir;
    public string Kind;
    public long SizeBytes;
    public DateTime Added;
}

internal sealed class Candidate
{
    public string Name;
    public string Kind;
    public double SizeMB;
    public string Title;
    public string Version;
    public string Author;
    public bool HasIcon;
    public bool IsMap;
    public bool IsNew;
    public bool IsDir;
}

internal sealed class FolderPlan
{
    public string Folder;
    public string FolderKind;
    public string ArchiveFolder;
    public List<SharingSavegame> Sharing;
    public int TotalCount;
    public double TotalMB;
    public int KeptCount;
    public double KeptMB;
    public List<Candidate> Candidates;
    public Dictionary<string, List<string>> NeededBy;
}

internal static class FolderOps
{
    private const string GameRunningMessage = "Das Spiel laeuft noch. Bitte erst beenden, sonst ueberschreibt es die Aenderung beim naechsten Speichern.";

    public static FolderContext Context(string savegameFolder, string mapId)
    {
        string karteDir = Savegames.ResolveKarteDir(savegameFolder, mapId);
        FolderContext ctx = new FolderContext();
        if (!string.IsNullOrEmpty(karteDir) && Directory.Exists(karteDir))
        {
            ctx.Kind = "Karte";
            ctx.Folder = karteDir;
            return ctx;
        }
        ctx.Kind = "Spielordner";
        ctx.Folder = AppEnv.ConfiguredModsFolder();
        if (!string.IsNullOrEmpty(mapId) && mapId.Contains("."))
        {
            string mapMod = mapId.Split('.')[0];
            foreach (string candidate in AppEnv.CandidateModsFolders())
            {
                if (File.Exists(Path.Combine(candidate, mapMod + ".zip")) || Directory.Exists(Path.Combine(candidate, mapMod)))
                {
                    ctx.Folder = candidate;
                    break;
                }
            }
        }
        return ctx;
    }

    public static List<SharingSavegame> SavegamesSharing(FolderContext context)
    {
        List<SharingSavegame> result = new List<SharingSavegame>();
        foreach (string sf in Savegames.Folders())
        {
            SavegameInfo info = Savegames.Info(sf);
            if (info == null) continue;
            FolderContext other = Context(sf, info.MapId);
            if (!string.Equals(other.Folder, context.Folder, StringComparison.OrdinalIgnoreCase)) continue;
            SharingSavegame s = new SharingSavegame();
            s.Folder = sf;
            s.Id = info.Id;
            s.Name = info.SavegameName;
            s.MapId = info.MapId;
            result.Add(s);
        }
        return result;
    }

    public static List<FolderEntry> Entries(string folder)
    {
        List<FolderEntry> entries = new List<FolderEntry>();
        if (!Directory.Exists(folder)) return entries;
        foreach (string z in Directory.GetFiles(folder, "*.zip"))
        {
            if (!string.Equals(Path.GetExtension(z), ".zip", StringComparison.OrdinalIgnoreCase)) continue;
            FolderEntry e = new FolderEntry();
            e.Name = Path.GetFileNameWithoutExtension(z);
            e.Path = z;
            e.IsDir = false;
            e.Kind = NativeLinks.LinkCount(z) > 1 ? "Hardlink" : "Kopie";
            FileInfo fi = new FileInfo(z);
            e.SizeBytes = fi.Length;
            e.Added = fi.CreationTime > fi.LastWriteTime ? fi.CreationTime : fi.LastWriteTime;
            entries.Add(e);
        }
        foreach (string d in Directory.GetDirectories(folder))
        {
            string name = Path.GetFileName(d);
            if (name.StartsWith("_") || name.StartsWith(".")) continue;
            if (!File.Exists(Path.Combine(d, "modDesc.xml"))) continue;
            FolderEntry e = new FolderEntry();
            e.Name = name;
            e.Path = d;
            e.IsDir = true;
            e.Kind = AppEnv.IsReparsePoint(d) ? "Junction" : "Kopie";
            e.SizeBytes = Usage.DirectorySize(d, true);
            e.Added = Directory.GetCreationTime(d);
            entries.Add(e);
        }
        return entries;
    }

    public static FolderPlan Plan(string savegameFolder, bool detailed)
    {
        return Plan(savegameFolder, detailed, null);
    }

    public static FolderPlan Plan(string savegameFolder, bool detailed, ICollection<string> assumeDisabled)
    {
        SavegameInfo info = Savegames.Info(savegameFolder);
        if (info == null) throw new InvalidOperationException("Spielstand nicht lesbar: " + savegameFolder);
        FolderContext context = Context(savegameFolder, info.MapId);
        List<SharingSavegame> sharing = SavegamesSharing(context);
        List<FolderEntry> entries = Entries(context.Folder);
        Dictionary<string, FolderEntry> entryByName = new Dictionary<string, FolderEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (FolderEntry e in entries) entryByName[e.Name] = e;

        Dictionary<string, List<string>> neededBy = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        Queue<string> queue = new Queue<string>();
        foreach (SharingSavegame s in sharing)
        {
            List<string> names = Savegames.ActiveModNames(s.Folder);
            if (assumeDisabled != null && string.Equals(s.Folder, savegameFolder, StringComparison.OrdinalIgnoreCase)) names.RemoveAll(n => assumeDisabled.Contains(n));
            if (!string.IsNullOrEmpty(s.MapId)) names.Add(s.MapId.Split('.')[0]);
            names.AddRange(Usage.AlwaysProtected.Keys);
            foreach (string n in names)
            {
                List<string> list;
                if (!neededBy.TryGetValue(n, out list)) { list = new List<string>(); neededBy[n] = list; queue.Enqueue(n); }
                if (!list.Contains(s.Id)) list.Add(s.Id);
            }
        }
        while (queue.Count > 0)
        {
            string cur = queue.Dequeue();
            FolderEntry e;
            if (!entryByName.TryGetValue(cur, out e)) continue;
            ModProfile prof = Usage.Profile(cur, new ModLocation(e.Path, e.IsDir, true));
            foreach (string dep in prof.Dependencies)
            {
                List<string> list;
                if (!neededBy.TryGetValue(dep, out list)) { list = new List<string>(); neededBy[dep] = list; queue.Enqueue(dep); }
                string reason = "Abhaengigkeit von " + cur;
                if (!list.Contains(reason)) list.Add(reason);
            }
        }

        DateTime lastSave = DateTime.MinValue;
        foreach (SharingSavegame s in sharing)
        {
            try
            {
                DateTime t = File.GetLastWriteTime(Path.Combine(s.Folder, "careerSavegame.xml"));
                if (t > lastSave) lastSave = t;
            }
            catch (Exception) { }
        }

        List<Candidate> candidates = new List<Candidate>();
        long keptSize = 0;
        foreach (FolderEntry e in entries)
        {
            if (neededBy.ContainsKey(e.Name)) { keptSize += e.SizeBytes; continue; }
            Candidate row = new Candidate();
            row.Name = e.Name;
            row.Kind = e.Kind;
            row.SizeMB = Math.Round(e.SizeBytes / 1048576.0, 1);
            row.Title = e.Name;
            row.IsDir = e.IsDir;
            row.IsNew = lastSave > DateTime.MinValue && e.Added > lastSave;
            if (detailed)
            {
                ModProfile prof = Usage.Profile(e.Name, new ModLocation(e.Path, e.IsDir, true));
                row.Title = prof.Title;
                row.Version = prof.Version;
                row.Author = prof.Author;
                row.HasIcon = !string.IsNullOrEmpty(prof.IconFilename);
                row.IsMap = prof.IsMap;
            }
            if (row.IsMap) { keptSize += e.SizeBytes; continue; }
            candidates.Add(row);
        }

        string leaf = Path.GetFileName(context.Folder.TrimEnd('\\'));
        string archive;
        if (context.Kind == "Karte" && !string.IsNullOrEmpty(AppEnv.ModsRoot)) archive = Path.Combine(Path.Combine(AppEnv.ModsRoot, "_Archiv"), leaf);
        else archive = Path.Combine(Path.GetDirectoryName(context.Folder.TrimEnd('\\')), leaf + " (Archiv)");

        FolderPlan plan = new FolderPlan();
        plan.Folder = context.Folder;
        plan.FolderKind = context.Kind;
        plan.ArchiveFolder = archive;
        plan.Sharing = sharing;
        plan.TotalCount = entries.Count;
        plan.TotalMB = Math.Round(entries.Sum(x => x.SizeBytes) / 1048576.0, 1);
        plan.KeptCount = entries.Count-candidates.Count;
        plan.KeptMB = Math.Round(keptSize / 1048576.0, 1);
        plan.Candidates = candidates.OrderByDescending(c => c.SizeMB).ToList();
        plan.NeededBy = neededBy;
        return plan;
    }

    public static string HistoryPath(string savegameFolder)
    {
        return Path.Combine(Savegames.DataDir, Path.GetFileName(savegameFolder) + ".modusage.json");
    }

    public static List<Dictionary<string, object>> History(string savegameFolder)
    {
        string path = HistoryPath(savegameFolder);
        if (!File.Exists(path)) return new List<Dictionary<string, object>>();
        try
        {
            return AppEnv.FlattenObjects(AppEnv.Json.DeserializeObject(File.ReadAllText(path)));
        }
        catch (Exception ex)
        {
            AppEnv.Log("ModUsage", path + " konnte nicht gelesen werden: " + ex.Message);
            return new List<Dictionary<string, object>>();
        }
    }

    private static void SaveHistoryEntry(string savegameFolder, Dictionary<string, object> entry)
    {
        List<Dictionary<string, object>> history = History(savegameFolder);
        history.Add(entry);
        File.WriteAllText(HistoryPath(savegameFolder), AppEnv.Json.Serialize(history), new UTF8Encoding(true));
    }

    private static string Now()
    {
        return DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    }

    public static List<string> Disable(string savegameFolder, List<string> modNames)
    {
        if (AppEnv.GameRunning()) throw new InvalidOperationException(GameRunningMessage);
        UsageResult usage = Usage.Analyze(savegameFolder);
        if (usage.FolderMismatch) throw new InvalidOperationException("Dieser Spielstand gehoert zu einem anderen Mods-Ordner als dem gerade im Spiel eingestellten.");
        HashSet<string> wanted = new HashSet<string>(modNames, StringComparer.OrdinalIgnoreCase);
        string[] protectedStates = new string[] { "Karte", "Benutzt", "Indirekt", "Abhaengigkeit", "Gesperrt", "Unbekannt", "DLC" };
        List<string> blocked = usage.Mods.Where(m => wanted.Contains(m.Name) && protectedStates.Contains(m.Status)).Select(m => m.Name).ToList();
        if (blocked.Count > 0) throw new InvalidOperationException("Diese Mods werden im Spielstand benutzt oder sind gesperrt und werden nicht deaktiviert: " + string.Join(", ", blocked.ToArray()));

        List<string> depBlocked = new List<string>();
        foreach (string keep in Savegames.ActiveModNames(savegameFolder))
        {
            if (wanted.Contains(keep)) continue;
            ModLocation loc = Usage.FindMod(keep, usage.ModsFolder);
            if (loc == null) continue;
            foreach (string dep in Usage.Profile(keep, loc).Dependencies)
            {
                string entry = dep + " (gebraucht von " + keep + ")";
                if (wanted.Contains(dep) && !depBlocked.Contains(entry)) depBlocked.Add(entry);
            }
        }
        if (depBlocked.Count > 0) throw new InvalidOperationException("Diese Mods werden von aktiv bleibenden Mods als Abhaengigkeit gebraucht: " + string.Join(", ", depBlocked.ToArray()));

        string careerPath = Path.Combine(savegameFolder, "careerSavegame.xml");
        string backupPath = AppEnv.BackupFile(careerPath);
        XmlDocument xml = new XmlDocument();
        xml.Load(careerPath);
        XmlElement root = xml.DocumentElement;
        List<object> removed = new List<object>();
        List<string> removedNames = new List<string>();
        foreach (string n in modNames)
        {
            XmlNode node = FindModNode(root, n);
            if (node == null) continue;
            Dictionary<string, object> r = new Dictionary<string, object>();
            r["Name"] = n;
            r["NodeXml"] = node.OuterXml;
            removed.Add(r);
            removedNames.Add(n);
            root.RemoveChild(node);
        }
        if (removed.Count == 0) return removedNames;
        xml.Save(careerPath);

        Dictionary<string, object> h = new Dictionary<string, object>();
        h["Date"] = Now();
        h["Type"] = "Savegame";
        h["Backup"] = backupPath;
        h["Mods"] = removed;
        SaveHistoryEntry(savegameFolder, h);
        AppEnv.Log("ModUsage", Path.GetFileName(savegameFolder) + ": " + removed.Count.ToString(CultureInfo.InvariantCulture) + " unbenutzte Mods deaktiviert: " + string.Join(", ", removedNames.ToArray()));
        return removedNames;
    }

    public static Dictionary<string, object> Activate(string savegameFolder, List<string> modNames)
    {
        if (AppEnv.GameRunning()) throw new InvalidOperationException(GameRunningMessage);
        SavegameInfo info = Savegames.Info(savegameFolder);
        if (info == null) throw new InvalidOperationException("Spielstand nicht lesbar: " + savegameFolder);
        UsageResult usage = Usage.Analyze(savegameFolder);
        if (usage.FolderMismatch) throw new InvalidOperationException("Dieser Spielstand gehoert zu einem anderen Mods-Ordner als dem gerade im Spiel eingestellten.");
        string folder = Context(savegameFolder, info.MapId).Folder;
        HashSet<string> active = new HashSet<string>(Savegames.ActiveModNames(savegameFolder), StringComparer.OrdinalIgnoreCase);

        List<string> order = new List<string>();
        HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        List<string> missing = new List<string>();
        Queue<string> queue = new Queue<string>(modNames);
        while (queue.Count > 0)
        {
            string n = queue.Dequeue();
            if (active.Contains(n) || !seen.Add(n)) continue;
            string zip = Path.Combine(folder, n + ".zip");
            if (!File.Exists(zip))
            {
                if (Directory.Exists(Path.Combine(folder, n))) throw new InvalidOperationException("Entpackte Mods lassen sich nur im Spiel einschalten: " + n);
                if (!n.StartsWith("pdlc_", StringComparison.OrdinalIgnoreCase)) missing.Add(n);
                continue;
            }
            order.Add(n);
            foreach (string dep in Usage.Profile(n, new ModLocation(zip, false, true)).Dependencies) queue.Enqueue(dep);
        }
        if (missing.Count > 0) throw new InvalidOperationException("Diese Abhaengigkeiten fehlen im Mods-Ordner: " + string.Join(", ", missing.ToArray()));
        if (order.Count == 0) return Obj("changed", new List<string>());

        string careerPath = Path.Combine(savegameFolder, "careerSavegame.xml");
        AppEnv.BackupFile(careerPath);
        XmlDocument xml = new XmlDocument();
        xml.PreserveWhitespace = true;
        xml.Load(careerPath);
        XmlElement root = xml.DocumentElement;
        XmlNode anchor = null;
        foreach (XmlNode child in root.ChildNodes)
        {
            XmlElement el = child as XmlElement;
            if (el != null && el.LocalName == "mod") anchor = el;
        }
        string indent = "\n    ";
        if (anchor != null && anchor.PreviousSibling != null && anchor.PreviousSibling.NodeType == XmlNodeType.Whitespace) indent = anchor.PreviousSibling.Value;
        foreach (string n in order)
        {
            string zip = Path.Combine(folder, n + ".zip");
            ModProfile prof = Usage.Profile(n, new ModLocation(zip, false, true));
            XmlElement el = xml.CreateElement("mod");
            el.SetAttribute("modName", n);
            el.SetAttribute("title", prof.Title ?? n);
            el.SetAttribute("version", prof.Version ?? "1.0.0.0");
            el.SetAttribute("required", "false");
            el.SetAttribute("fileHash", GameHash(zip, n));
            XmlNode ws = xml.CreateWhitespace(indent);
            if (anchor != null)
            {
                root.InsertAfter(ws, anchor);
                root.InsertAfter(el, ws);
            }
            else
            {
                root.AppendChild(ws);
                root.AppendChild(el);
            }
            anchor = el;
        }
        xml.Save(careerPath);
        AppEnv.Log("ModUsage", Path.GetFileName(savegameFolder) + ": " + order.Count.ToString(CultureInfo.InvariantCulture) + " Mods eingeschaltet: " + string.Join(", ", order.ToArray()));
        return Obj("changed", order);
    }

    private static string GameHash(string zipPath, string modName)
    {
        using (MD5 md5 = MD5.Create())
        using (FileStream fs = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
        {
            byte[] buffer = new byte[1 << 20];
            int read;
            while ((read = fs.Read(buffer, 0, buffer.Length)) > 0) md5.TransformBlock(buffer, 0, read, null, 0);
            byte[] name = Encoding.ASCII.GetBytes(modName);
            md5.TransformFinalBlock(name, 0, name.Length);
            StringBuilder sb = new StringBuilder();
            foreach (byte b in md5.Hash) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }
    }

    private static Dictionary<string, object> Obj(string key, object value)
    {
        Dictionary<string, object> d = new Dictionary<string, object>();
        d[key] = value;
        return d;
    }

    private static XmlNode FindModNode(XmlElement root, string modName)
    {
        foreach (XmlNode child in root.ChildNodes)
        {
            XmlElement el = child as XmlElement;
            if (el != null && el.LocalName == "mod" && el.GetAttribute("modName") == modName) return el;
        }
        return null;
    }

    public static List<string> RestoreSavegame(string savegameFolder, List<string> modNames)
    {
        if (AppEnv.GameRunning()) throw new InvalidOperationException(GameRunningMessage);
        Dictionary<string, string> nodesByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Dictionary<string, object> h in History(savegameFolder))
        {
            if (AppEnv.GetString(h, "Type") == "Folder") continue;
            foreach (Dictionary<string, object> m in AppEnv.GetObjects(h, "Mods"))
            {
                string name = AppEnv.GetString(m, "Name");
                if (!string.IsNullOrEmpty(name)) nodesByName[name] = AppEnv.GetString(m, "NodeXml");
            }
        }
        string careerPath = Path.Combine(savegameFolder, "careerSavegame.xml");
        AppEnv.BackupFile(careerPath);
        XmlDocument xml = new XmlDocument();
        xml.Load(careerPath);
        XmlElement root = xml.DocumentElement;
        List<string> restored = new List<string>();
        foreach (string n in modNames)
        {
            string nodeXml;
            if (!nodesByName.TryGetValue(n, out nodeXml) || string.IsNullOrEmpty(nodeXml)) continue;
            if (FindModNode(root, n) != null) continue;
            XmlDocumentFragment frag = xml.CreateDocumentFragment();
            frag.InnerXml = nodeXml;
            root.AppendChild(frag);
            restored.Add(n);
        }
        if (restored.Count > 0)
        {
            xml.Save(careerPath);
            AppEnv.Log("ModUsage", Path.GetFileName(savegameFolder) + ": " + restored.Count.ToString(CultureInfo.InvariantCulture) + " Mods wieder aktiviert: " + string.Join(", ", restored.ToArray()));
        }
        return restored;
    }

    public static Dictionary<string, object> Preview(string savegameFolder, List<string> modNames)
    {
        HashSet<string> selected = new HashSet<string>(modNames, StringComparer.OrdinalIgnoreCase);
        FolderPlan plan = Plan(savegameFolder, false, selected);
        HashSet<string> candidates = new HashSet<string>(plan.Candidates.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> sgNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (SharingSavegame s in plan.Sharing) sgNames[s.Id] = s.Name + " (" + s.Id + ")";
        List<string> removable = new List<string>();
        List<object> kept = new List<object>();
        foreach (string n in modNames)
        {
            if (candidates.Contains(n)) { removable.Add(n); continue; }
            List<string> needed;
            if (!plan.NeededBy.TryGetValue(n, out needed)) continue;
            List<string> savegames = new List<string>();
            List<string> mods = new List<string>();
            foreach (string reason in needed)
            {
                string sgName;
                if (sgNames.TryGetValue(reason, out sgName)) { if (!savegames.Contains(sgName)) savegames.Add(sgName); }
                else if (reason.StartsWith("Abhaengigkeit von ")) mods.Add(reason.Substring("Abhaengigkeit von ".Length));
            }
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["Name"] = n;
            d["Savegames"] = savegames;
            d["Mods"] = mods;
            kept.Add(d);
        }
        Dictionary<string, object> result = new Dictionary<string, object>();
        result["removable"] = removable;
        result["kept"] = kept;
        result["sharing"] = plan.Sharing.Count;
        return result;
    }

    public static Dictionary<string, object> Cleanup(string savegameFolder, List<string> modNames, string copyAction)
    {
        if (AppEnv.GameRunning()) throw new InvalidOperationException("Das Spiel laeuft noch. Bitte erst beenden, solange es die Mods geoeffnet hat, lassen sie sich nicht sicher entfernen.");
        if (copyAction != "Move" && copyAction != "Keep") throw new InvalidOperationException("Unbekannte Aktion fuer echte Kopien: " + copyAction);
        FolderPlan plan = Plan(savegameFolder, false);
        Dictionary<string, Candidate> byName = new Dictionary<string, Candidate>(StringComparer.OrdinalIgnoreCase);
        foreach (Candidate c in plan.Candidates) byName[c.Name] = c;
        List<object> items = new List<object>();
        List<string> removedNames = new List<string>();
        List<string> itemLog = new List<string>();
        List<object> skipped = new List<object>();

        foreach (string n in modNames)
        {
            if (!byName.ContainsKey(n))
            {
                List<string> needed;
                string why = plan.NeededBy.TryGetValue(n, out needed) ? "wird noch gebraucht: " + string.Join(", ", needed.ToArray()) : "nicht im Mods-Ordner";
                skipped.Add(Reason(n, why));
                continue;
            }
            string zipPath = Path.Combine(plan.Folder, n + ".zip");
            string dirPath = Path.Combine(plan.Folder, n);
            try
            {
                string kind;
                if (File.Exists(zipPath))
                {
                    List<string> links = NativeLinks.Links(zipPath).Where(l => !string.Equals(l, zipPath, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (links.Count > 0)
                    {
                        File.Delete(zipPath);
                        kind = "Hardlink";
                        items.Add(Item(n, kind, zipPath, links[0]));
                    }
                    else if (copyAction == "Move")
                    {
                        Directory.CreateDirectory(plan.ArchiveFolder);
                        string dest = Path.Combine(plan.ArchiveFolder, n + ".zip");
                        if (File.Exists(dest) || Directory.Exists(dest)) throw new InvalidOperationException("Im Archiv liegt schon eine Datei mit diesem Namen: " + dest);
                        File.Move(zipPath, dest);
                        kind = "Kopie";
                        items.Add(Item(n, kind, zipPath, dest));
                    }
                    else
                    {
                        skipped.Add(Reason(n, "echte Kopie, bleibt laut Auswahl liegen"));
                        continue;
                    }
                }
                else if (Directory.Exists(dirPath))
                {
                    if (AppEnv.IsReparsePoint(dirPath))
                    {
                        string target = NativeLinks.JunctionTarget(dirPath);
                        AppEnv.RemoveDirectoryLinkSafely(dirPath);
                        kind = "Junction";
                        items.Add(Item(n, kind, dirPath, target));
                    }
                    else if (copyAction == "Move")
                    {
                        Directory.CreateDirectory(plan.ArchiveFolder);
                        string dest = Path.Combine(plan.ArchiveFolder, n);
                        if (File.Exists(dest) || Directory.Exists(dest)) throw new InvalidOperationException("Im Archiv liegt schon ein Ordner mit diesem Namen: " + dest);
                        Directory.Move(dirPath, dest);
                        kind = "Kopie";
                        items.Add(Item(n, kind, dirPath, dest));
                    }
                    else
                    {
                        skipped.Add(Reason(n, "echte Kopie, bleibt laut Auswahl liegen"));
                        continue;
                    }
                }
                else continue;
                removedNames.Add(n);
                itemLog.Add(n + " (" + kind + ")");
            }
            catch (Exception ex)
            {
                skipped.Add(Reason(n, ex.Message));
            }
        }

        bool scopedSet = false;
        if (items.Count > 0 && plan.FolderKind == "Karte" && !AppEnv.IsScopedSetup(plan.Folder))
        {
            AppEnv.SetScopedSetup(plan.Folder);
            scopedSet = true;
        }
        if (items.Count > 0)
        {
            Dictionary<string, object> h = new Dictionary<string, object>();
            h["Date"] = Now();
            h["Type"] = "Folder";
            h["Folder"] = plan.Folder;
            h["ScopedSet"] = scopedSet;
            h["Items"] = items;
            SaveHistoryEntry(savegameFolder, h);
            AppEnv.Log("ModUsage", Path.GetFileName(savegameFolder) + ": " + items.Count.ToString(CultureInfo.InvariantCulture) + " Mods aus " + plan.Folder + " genommen: " + string.Join(", ", itemLog.ToArray()));
        }
        Dictionary<string, object> result = new Dictionary<string, object>();
        result["removed"] = removedNames;
        result["skipped"] = skipped;
        result["scopedSet"] = scopedSet;
        return result;
    }

    private static Dictionary<string, object> Item(string name, string kind, string path, string source)
    {
        Dictionary<string, object> d = new Dictionary<string, object>();
        d["Name"] = name;
        d["Kind"] = kind;
        d["Path"] = path;
        d["Source"] = source;
        return d;
    }

    private static Dictionary<string, object> Reason(string name, string reason)
    {
        Dictionary<string, object> d = new Dictionary<string, object>();
        d["Name"] = name;
        d["Reason"] = reason;
        return d;
    }

    public static Dictionary<string, object> RestoreFolder(string savegameFolder, List<string> modNames)
    {
        if (AppEnv.GameRunning()) throw new InvalidOperationException("Das Spiel laeuft noch. Bitte erst beenden.");
        Dictionary<string, Dictionary<string, object>> latest = new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
        foreach (Dictionary<string, object> h in History(savegameFolder))
        {
            if (AppEnv.GetString(h, "Type") != "Folder") continue;
            foreach (Dictionary<string, object> it in AppEnv.GetObjects(h, "Items"))
            {
                string name = AppEnv.GetString(it, "Name");
                if (!string.IsNullOrEmpty(name)) latest[name] = it;
            }
        }
        List<string> restored = new List<string>();
        List<object> failed = new List<object>();
        foreach (string n in modNames)
        {
            Dictionary<string, object> it;
            if (!latest.TryGetValue(n, out it)) continue;
            string path = AppEnv.GetString(it, "Path");
            string source = AppEnv.GetString(it, "Source");
            string kind = AppEnv.GetString(it, "Kind");
            if (File.Exists(path) || Directory.Exists(path)) { restored.Add(n); continue; }
            try
            {
                if (string.IsNullOrEmpty(source) || !(File.Exists(source) || Directory.Exists(source))) throw new InvalidOperationException("Quelle nicht mehr vorhanden: " + source);
                if (kind == "Hardlink") NativeLinks.CreateHardLink(path, source);
                else if (kind == "Junction") NativeLinks.CreateJunction(path, source);
                else if (kind == "Kopie")
                {
                    if (Directory.Exists(source)) Directory.Move(source, path);
                    else File.Move(source, path);
                }
                restored.Add(n);
            }
            catch (Exception ex)
            {
                failed.Add(Reason(n, ex.Message));
            }
        }
        if (restored.Count > 0) AppEnv.Log("ModUsage", Path.GetFileName(savegameFolder) + ": " + restored.Count.ToString(CultureInfo.InvariantCulture) + " Mods in den Mods-Ordner zurueckgelegt: " + string.Join(", ", restored.ToArray()));
        Dictionary<string, object> result = new Dictionary<string, object>();
        result["Restored"] = restored;
        result["Failed"] = failed;
        return result;
    }
}
