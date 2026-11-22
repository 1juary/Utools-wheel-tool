using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

sealed class DirectoryBranch
{
    public sealed class Level
    {
        public readonly string Parent;
        public readonly string[] Children;
        public int Offset;

        public Level(string parent)
        {
            Parent = parent;
            try
            {
                Children = Directory.GetDirectories(parent)
                    .OrderBy(Path.GetFileName, StringComparer.CurrentCultureIgnoreCase).ToArray();
            }
            catch (IOException) { Children = new string[0]; }
            catch (UnauthorizedAccessException) { Children = new string[0]; }
        }
    }

    readonly List<Level> levels = new List<Level>();
    public int Count { get { return levels.Count; } }
    public Level this[int index] { get { return levels[index]; } }
    public int IndexOf(Level level) { return levels.IndexOf(level); }

    public bool Contains(string path)
    {
        return levels.Any(level => StringComparer.OrdinalIgnoreCase.Equals(path, level.Parent));
    }

    public void Clear() { levels.Clear(); }

    public void CloseFrom(int index)
    {
        if (index < levels.Count) levels.RemoveRange(index, levels.Count - index);
    }

    public bool Expand(string path, bool root)
    {
        int childLevel = 0;
        if (!root)
        {
            int owner = levels.FindLastIndex(level => level.Children.Contains(path, StringComparer.OrdinalIgnoreCase));
            if (owner < 0) return false;
            childLevel = owner + 1;
        }

        if (childLevel < Count && StringComparer.OrdinalIgnoreCase.Equals(levels[childLevel].Parent, path))
            return false;

        bool changed = childLevel < Count;
        CloseFrom(childLevel);
        var next = new Level(path);
        if (next.Children.Length == 0) return changed;
        levels.Add(next);
        return true;
    }
}
