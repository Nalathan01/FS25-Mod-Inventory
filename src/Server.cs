using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

internal sealed class HistoryGroup
{
    public string Date;
    public DateTime Time;
    public string Kind;
    public List<string> SavegameMods = new List<string>();
    public List<Dictionary<string, object>> FolderItems = new List<Dictionary<string, object>>();
}

internal sealed class InventurServer
{
    private readonly string token = Guid.NewGuid().ToString("N");
    private readonly Dictionary<string, UsageResult> usageCache = new Dictionary<string, UsageResult>(StringComparer.OrdinalIgnoreCase);
    private readonly string uiLang = AppEnv.UiLanguage();
    private byte[] html;
    private DateTime lastPing = DateTime.Now;
    private bool firstPingSeen;
    private DateTime byeAt = DateTime.MinValue;
    private bool running = true;
    private bool restart;

    public void Run(int port, bool noBrowser, string urlFile)
    {
        html = ReadResource("index.html");
        Updater.CleanUp();
        Updater.CheckInBackground();
        if (port == 0) port = FreePort();
        string prefix = "http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture) + "/";
        HttpListener listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        listener.Start();
        string url = prefix + "?t=" + token;
        AppEnv.Log("ModInventory", "Gestartet auf " + prefix);

        if (!noBrowser) Browser.Open(url);
        else if (!string.IsNullOrEmpty(urlFile)) File.WriteAllText(urlFile, url, new UTF8Encoding(false));

        while (running)
        {
            Task<HttpListenerContext> task = listener.GetContextAsync();
            while (!task.Wait(1000))
            {
                double idle = DateTime.Now.Subtract(lastPing).TotalSeconds;
                bool closed = byeAt > DateTime.MinValue && lastPing <= byeAt && DateTime.Now.Subtract(byeAt).TotalSeconds > 6;
                if (closed || (firstPingSeen && idle > 180) || (!firstPingSeen && idle > 120)) { running = false; break; }
            }
            if (!running) break;
            Handle(task.Result);
        }
        listener.Stop();
        listener.Close();
        AppEnv.Log("ModInventory", "Beendet");
        if (restart) Process.Start(Assembly.GetExecutingAssembly().Location, noBrowser ? "-NoBrowser" + (string.IsNullOrEmpty(urlFile) ? "" : " -UrlFile \"" + urlFile + "\"") : "");
    }

    private static int FreePort()
    {
        TcpListener l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    private static byte[] ReadResource(string name)
    {
        using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
        {
            if (s == null) throw new InvalidOperationException("Resource missing: " + name);
            using (MemoryStream ms = new MemoryStream())
            {
                s.CopyTo(ms);
                return ms.ToArray();
            }
        }
    }

    private void Handle(HttpListenerContext ctx)
    {
        HttpListenerRequest req = ctx.Request;
        string path = req.Url.AbsolutePath;
        try
        {
            if (path == "/" || path == "/index.html")
            {
                Send(ctx, 200, html, "text/html; charset=utf-8");
                return;
            }
            string given = req.Headers["X-Token"];
            if (string.IsNullOrEmpty(given)) given = req.QueryString["t"];
            if (given != token) { SendJson(ctx, 403, Obj("error", "Kein Zugriff")); return; }

            lastPing = DateTime.Now;
            if (path == "/api/ping")
            {
                firstPingSeen = true;
                Dictionary<string, object> r = Obj("ok", true);
                r["gameRunning"] = AppEnv.GameRunning();
                r["lang"] = uiLang;
                r["update"] = Updater.Available;
                r["version"] = Updater.CurrentText;
                SendJson(ctx, 200, r);
            }
            else if (path == "/api/savegames") SendJson(ctx, 200, SavegameList());
            else if (path == "/api/usage") SendJson(ctx, 200, UsageResponse(req.QueryString["sg"]));
            else if (path == "/api/disable")
            {
                Dictionary<string, object> body = ReadBody(req);
                string folder = Savegames.FolderFor(AppEnv.GetString(body, "sg"));
                SendJson(ctx, 200, Obj("changed", FolderOps.Disable(folder, AppEnv.GetStrings(body, "mods"))));
            }
            else if (path == "/api/restore")
            {
                Dictionary<string, object> body = ReadBody(req);
                string folder = Savegames.FolderFor(AppEnv.GetString(body, "sg"));
                List<string> mods = AppEnv.GetStrings(body, "mods");
                FolderOps.RestoreFolder(folder, mods);
                SendJson(ctx, 200, Obj("changed", FolderOps.RestoreSavegame(folder, mods)));
            }
            else if (path == "/api/undo")
            {
                Dictionary<string, object> body = ReadBody(req);
                string folder = Savegames.FolderFor(AppEnv.GetString(body, "sg"));
                SendJson(ctx, 200, Undo(folder, AppEnv.GetString(body, "date"), AppEnv.GetStrings(body, "mods")));
            }
            else if (path == "/api/folder") SendJson(ctx, 200, FolderResponse(req.QueryString["sg"], req.QueryString["detail"] == "1"));
            else if (path == "/api/folder/preview")
            {
                Dictionary<string, object> body = ReadBody(req);
                string folder = Savegames.FolderFor(AppEnv.GetString(body, "sg"));
                SendJson(ctx, 200, FolderOps.Preview(folder, AppEnv.GetStrings(body, "mods")));
            }
            else if (path == "/api/folder/clean")
            {
                Dictionary<string, object> body = ReadBody(req);
                string folder = Savegames.FolderFor(AppEnv.GetString(body, "sg"));
                SendJson(ctx, 200, FolderOps.Cleanup(folder, AppEnv.GetStrings(body, "mods"), AppEnv.GetString(body, "copyAction")));
            }
            else if (path == "/api/folder/restore")
            {
                Dictionary<string, object> body = ReadBody(req);
                string folder = Savegames.FolderFor(AppEnv.GetString(body, "sg"));
                Dictionary<string, object> r = FolderOps.RestoreFolder(folder, AppEnv.GetStrings(body, "mods"));
                Dictionary<string, object> outObj = Obj("changed", r["Restored"]);
                outObj["failed"] = r["Failed"];
                SendJson(ctx, 200, outObj);
            }
            else if (path.StartsWith("/icon/"))
            {
                string modName = Uri.UnescapeDataString(path.Substring("/icon/".Length));
                string png = IconPath(req.QueryString["sg"], modName);
                if (png != null) Send(ctx, 200, File.ReadAllBytes(png), "image/png");
                else SendJson(ctx, 404, Obj("error", "kein Icon"));
            }
            else if (path == "/api/update")
            {
                Updater.Install();
                SendJson(ctx, 200, Obj("ok", true));
                restart = true;
                running = false;
            }
            else if (path == "/api/bye")
            {
                byeAt = DateTime.Now;
                SendJson(ctx, 200, Obj("ok", true));
            }
            else if (path == "/api/browsers") SendJson(ctx, 200, BrowserList());
            else if (path == "/api/browser")
            {
                Dictionary<string, object> body = ReadBody(req);
                string id = AppEnv.GetString(body, "id");
                if (string.IsNullOrEmpty(id)) id = Browser.Auto;
                Browser.Choice = id;
                AppEnv.Log("Browser", "Auswahl: " + id);
                SendJson(ctx, 200, Obj("ok", true));
                restart = AppEnv.GetBool(body, "reopen");
                if (restart) running = false;
            }
            else if (path == "/api/quit")
            {
                SendJson(ctx, 200, Obj("ok", true));
                running = false;
            }
            else SendJson(ctx, 404, Obj("error", "Unbekannt"));
        }
        catch (Exception ex)
        {
            AppEnv.Log("ModInventory", "Fehler bei " + path + ": " + ex.Message);
            try { SendJson(ctx, 500, Obj("error", ex.Message)); } catch (Exception) { }
        }
    }

    private static Dictionary<string, object> BrowserList()
    {
        List<object> list = new List<object>();
        foreach (BrowserInfo b in Browser.Installed())
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["Id"] = b.Id;
            d["Name"] = b.Name;
            d["AppWindow"] = b.AppWindow;
            list.Add(d);
        }
        BrowserInfo def = Browser.DefaultBrowser();
        Dictionary<string, object> r = Obj("list", list);
        r["choice"] = Browser.Choice;
        r["defaultName"] = def == null ? null : def.Name;
        r["defaultApp"] = def != null && def.AppWindow;
        return r;
    }

    private static Dictionary<string, object> Obj(string key, object value)
    {
        Dictionary<string, object> d = new Dictionary<string, object>();
        d[key] = value;
        return d;
    }

    private static Dictionary<string, object> ReadBody(HttpListenerRequest req)
    {
        using (StreamReader reader = new StreamReader(req.InputStream, Encoding.UTF8))
        {
            string text = reader.ReadToEnd();
            if (string.IsNullOrEmpty(text)) return new Dictionary<string, object>();
            return AppEnv.Json.DeserializeObject(text) as Dictionary<string, object>;
        }
    }

    private static void Send(HttpListenerContext ctx, int status, byte[] body, string contentType)
    {
        HttpListenerResponse res = ctx.Response;
        res.StatusCode = status;
        res.ContentType = contentType;
        res.Headers.Add("Cache-Control", contentType == "image/png" ? "max-age=86400" : "no-store");
        res.ContentLength64 = body.Length;
        res.OutputStream.Write(body, 0, body.Length);
        res.OutputStream.Close();
    }

    private static void SendJson(HttpListenerContext ctx, int status, object obj)
    {
        Send(ctx, status, Encoding.UTF8.GetBytes(AppEnv.Json.Serialize(obj)), "application/json; charset=utf-8");
    }

    private List<object> SavegameList()
    {
        List<object> list = new List<object>();
        foreach (string sf in Savegames.Folders())
        {
            SavegameInfo info = Savegames.Info(sf);
            if (info == null) continue;
            string mapMod = info.MapModName;
            FolderContext ctx = FolderOps.Context(sf, info.MapId);
            bool otherFolder = false;
            if (ctx.Kind == "Spielordner" && !string.IsNullOrEmpty(mapMod) && info.MapId.Contains("."))
            {
                otherFolder = !(File.Exists(Path.Combine(ctx.Folder, mapMod + ".zip")) || Directory.Exists(Path.Combine(ctx.Folder, mapMod)));
            }
            string saveDate = null;
            try { saveDate = File.GetLastWriteTime(Path.Combine(sf, "careerSavegame.xml")).ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture); } catch (Exception) { }
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["Id"] = info.Id;
            d["Name"] = info.SavegameName;
            d["MapTitle"] = info.MapTitle;
            d["MapModName"] = mapMod;
            d["FarmName"] = info.FarmName;
            d["PlayTimeHours"] = info.PlayTimeHours;
            d["SaveDate"] = saveDate;
            d["ActiveMods"] = Savegames.ActiveModNames(sf).Count;
            d["OtherFolder"] = otherFolder;
            list.Add(d);
        }
        return list;
    }

    private static List<HistoryGroup> HistoryGroups(List<Dictionary<string, object>> history)
    {
        List<HistoryGroup> groups = new List<HistoryGroup>();
        HistoryGroup last = null;
        foreach (Dictionary<string, object> h in history)
        {
            string dateText = AppEnv.GetString(h, "Date");
            DateTime date;
            if (!DateTime.TryParseExact(dateText, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out date)) continue;
            bool isFolder = AppEnv.GetString(h, "Type") == "Folder";
            if (isFolder && last != null && last.Kind == "Savegame" && date.Subtract(last.Time).TotalSeconds <= 30)
            {
                last.Kind = "Both";
                last.FolderItems = AppEnv.GetObjects(h, "Items");
                continue;
            }
            HistoryGroup g = new HistoryGroup();
            g.Date = dateText;
            g.Time = date;
            g.Kind = isFolder ? "Folder" : "Savegame";
            if (isFolder) g.FolderItems = AppEnv.GetObjects(h, "Items");
            else foreach (Dictionary<string, object> m in AppEnv.GetObjects(h, "Mods")) g.SavegameMods.Add(AppEnv.GetString(m, "Name"));
            groups.Add(g);
            last = g;
        }
        return groups;
    }

    private static Dictionary<string, object> Undo(string folder, string date, List<string> mods)
    {
        HistoryGroup group = HistoryGroups(FolderOps.History(folder)).LastOrDefault(g => g.Date == date);
        if (group == null) throw new InvalidOperationException("Verlaufseintrag nicht gefunden: " + date);
        List<string> fileMods = mods.Where(n => group.FolderItems.Any(i => string.Equals(AppEnv.GetString(i, "Name"), n, StringComparison.OrdinalIgnoreCase))).ToList();
        List<string> sgMods = mods.Where(n => group.SavegameMods.Contains(n, StringComparer.OrdinalIgnoreCase)).ToList();
        List<object> failed = new List<object>();
        if (fileMods.Count > 0) failed = (List<object>)FolderOps.RestoreFolder(folder, fileMods)["Failed"];
        if (sgMods.Count > 0) FolderOps.RestoreSavegame(folder, sgMods);
        HashSet<string> failedNames = new HashSet<string>(failed.Select(f => AppEnv.GetString((Dictionary<string, object>)f, "Name")), StringComparer.OrdinalIgnoreCase);
        Dictionary<string, object> r = Obj("changed", mods.Where(n => !failedNames.Contains(n)).ToList());
        r["failed"] = failed;
        return r;
    }

    private Dictionary<string, object> UsageResponse(string id)
    {
        string folder = Savegames.FolderFor(id);
        UsageResult usage = Usage.Analyze(folder);
        usageCache[id] = usage;
        HashSet<string> activeNow = new HashSet<string>(Savegames.ActiveModNames(folder), StringComparer.OrdinalIgnoreCase);

        List<object> history = new List<object>();
        foreach (HistoryGroup g in HistoryGroups(FolderOps.History(folder)))
        {
            List<string> names = new List<string>();
            foreach (string n in g.SavegameMods) if (!string.IsNullOrEmpty(n) && !names.Contains(n)) names.Add(n);
            foreach (Dictionary<string, object> it in g.FolderItems)
            {
                string n = AppEnv.GetString(it, "Name");
                if (!string.IsNullOrEmpty(n) && !names.Contains(n)) names.Add(n);
            }
            List<object> mods = new List<object>();
            foreach (string n in names)
            {
                Dictionary<string, object> item = g.FolderItems.FirstOrDefault(i => string.Equals(AppEnv.GetString(i, "Name"), n, StringComparison.OrdinalIgnoreCase));
                bool activeOk = !g.SavegameMods.Contains(n, StringComparer.OrdinalIgnoreCase) || activeNow.Contains(n);
                string itemPath = item == null ? null : AppEnv.GetString(item, "Path");
                bool fileOk = item == null || (!string.IsNullOrEmpty(itemPath) && (File.Exists(itemPath) || Directory.Exists(itemPath)));
                Dictionary<string, object> m = Obj("Name", n);
                m["Restored"] = activeOk && fileOk;
                mods.Add(m);
            }
            Dictionary<string, object> h = Obj("Date", g.Date);
            h["Kind"] = g.Kind;
            h["Mods"] = mods;
            history.Add(h);
        }

        List<object> rows = new List<object>();
        foreach (ModRow r in usage.Mods)
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["Name"] = r.Name;
            d["Title"] = r.Title;
            d["Status"] = r.Status;
            d["Active"] = r.Active;
            d["Reasons"] = r.Reasons;
            d["Warning"] = r.Warning;
            d["StoreItemCount"] = r.StoreItemCount;
            d["HasScripts"] = r.HasScripts;
            d["SizeMB"] = r.SizeMB;
            d["Version"] = r.Version;
            d["Author"] = r.Author;
            d["HasIcon"] = !string.IsNullOrEmpty(r.IconFilename) && !string.IsNullOrEmpty(r.ModPath);
            rows.Add(d);
        }

        Dictionary<string, object> res = new Dictionary<string, object>();
        res["Id"] = id;
        res["Name"] = usage.Savegame.SavegameName;
        res["MapTitle"] = usage.Savegame.MapTitle;
        res["MapModName"] = usage.Savegame.MapModName;
        res["PlayTimeHours"] = usage.Savegame.PlayTimeHours;
        res["KarteName"] = string.IsNullOrEmpty(usage.ModsFolder) ? null : Path.GetFileName(usage.ModsFolder.TrimEnd('\\'));
        res["ModsFolder"] = usage.ModsFolder;
        res["FolderMismatch"] = usage.FolderMismatch;
        res["GameRunning"] = AppEnv.GameRunning();
        res["Mods"] = rows;
        res["UnassignedFiles"] = usage.UnassignedFiles;
        res["History"] = history;
        return res;
    }

    private static Dictionary<string, object> FolderResponse(string id, bool detailed)
    {
        string folder = Savegames.FolderFor(id);
        FolderPlan plan = FolderOps.Plan(folder, detailed);
        List<object> sharing = new List<object>();
        foreach (SharingSavegame s in plan.Sharing)
        {
            Dictionary<string, object> d = Obj("Id", s.Id);
            d["Name"] = s.Name;
            sharing.Add(d);
        }
        List<object> candidates = new List<object>();
        foreach (Candidate c in plan.Candidates)
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["Name"] = c.Name;
            d["Kind"] = c.Kind;
            d["SizeMB"] = c.SizeMB;
            d["Title"] = c.Title;
            d["Version"] = c.Version;
            d["Author"] = c.Author;
            d["HasIcon"] = c.HasIcon;
            d["IsMap"] = c.IsMap;
            candidates.Add(d);
        }
        Dictionary<string, object> res = new Dictionary<string, object>();
        res["Folder"] = plan.Folder;
        res["FolderKind"] = plan.FolderKind;
        res["ArchiveFolder"] = plan.ArchiveFolder;
        res["Scoped"] = plan.FolderKind == "Karte" && AppEnv.IsScopedSetup(plan.Folder);
        res["Sharing"] = sharing;
        res["TotalCount"] = plan.TotalCount;
        res["TotalMB"] = plan.TotalMB;
        res["KeptCount"] = plan.KeptCount;
        res["KeptMB"] = plan.KeptMB;
        res["Candidates"] = candidates;
        return res;
    }

    private string IconPath(string id, string modName)
    {
        string basePreview = Icons.BaseMapPreviewPng(modName);
        if (basePreview != null) return basePreview;
        UsageResult cached;
        if (id != null && usageCache.TryGetValue(id, out cached))
        {
            ModRow row = cached.Mods.FirstOrDefault(m => string.Equals(m.Name, modName, StringComparison.OrdinalIgnoreCase));
            if (row != null && row.ModPath != null) return Icons.ModIconPng(row.ModPath, row.ModIsDir, row.IconFilename);
        }
        string folder = Savegames.FolderFor(id);
        SavegameInfo info = Savegames.Info(folder);
        FolderContext ctx = FolderOps.Context(folder, info == null ? null : info.MapId);
        ModLocation location = Usage.FindMod(modName, ctx.Folder);
        if (location == null) return null;
        ModProfile prof = Usage.Profile(modName, location);
        return Icons.ModIconPng(location.Path, location.IsDir, prof.IconFilename);
    }
}
