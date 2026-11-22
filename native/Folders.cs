using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;

class Settings
{
    public string[] Manual { get; set; }
}

class Visit
{
    public double Score { get; set; }
    public DateTime Last { get; set; }
}

class Folders
{
    public const int MaxDirectoryTargets = 6;
    public static readonly string Home = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UToolsWheel");
    static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
    readonly Dictionary<string, Visit> visits = new Dictionary<string, Visit>(StringComparer.OrdinalIgnoreCase);
    HashSet<string> previous = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    static string History { get { return Path.Combine(Home, "history.json"); } }

    public Folders()
    {
        var saved = Read<Dictionary<string, Visit>>(History);
        if (saved != null)
            foreach (var item in saved) visits[item.Key] = item.Value;
    }

    public static T Read<T>(string path) where T : class
    {
        try { return Json.Deserialize<T>(File.ReadAllText(path)); }
        catch (IOException) { return null; }
        catch (ArgumentException) { return null; }
        catch (InvalidOperationException) { return null; }
    }

    static double Score(Visit visit, DateTime now)
    {
        return visit.Score * Math.Pow(0.5, Math.Max(0, (now - visit.Last).TotalDays) / 14);
    }

    public void Observe(IEnumerable<string> paths)
    {
        var current = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        var now = DateTime.UtcNow;
        bool changed = false;
        foreach (var path in current.Except(previous, StringComparer.OrdinalIgnoreCase))
        {
            Visit existing;
            double score = visits.TryGetValue(path, out existing) ? Score(existing, now) : 0;
            visits[path] = new Visit { Score = score + 1, Last = now };
            changed = true;
        }
        previous = current;
        if (!changed) return;
        foreach (var path in visits.OrderByDescending(x => Score(x.Value, now)).Skip(300).Select(x => x.Key).ToArray())
            visits.Remove(path);
        try
        {
            Directory.CreateDirectory(Home);
            var temporary = History + ".tmp";
            File.WriteAllText(temporary, Json.Serialize(visits));
            if (File.Exists(History)) File.Replace(temporary, History, null);
            else File.Move(temporary, History);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public string[] Targets(IEnumerable<string> shellPaths)
    {
        var settings = Read<Settings>(Path.Combine(Home, "settings.json"));
        var now = DateTime.UtcNow;
        return Rank(settings == null ? null : settings.Manual, shellPaths,
            visits.OrderByDescending(x => Score(x.Value, now)).Select(x => x.Key));
    }

    public static string[] Rank(string[] manual, IEnumerable<string> shellPaths, IEnumerable<string> history)
    {
        return (manual ?? new string[0]).Concat(shellPaths.Concat(history))
            .Where(path => !String.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxDirectoryTargets).ToArray();
    }
}
