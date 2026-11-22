using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.VisualBasic.FileIO;

class Program
{
    public const string StopName = @"Local\UToolsWheel.Stop";
    [STAThread]
    static void Main(string[] args)
    {
        try
        {
            if (args.Length > 0)
            {
                if (args[0] == "--file-wheel")
                {
                    var files = args.Length == 2 ?
                        Folders.Read<string[]>(args[1]) : null;
                    Environment.ExitCode = ChooseFileWheel(files);
                }
                else Command(args[0]);
                return;
            }
            using (var single = new Mutex(false, @"Local\UToolsWheel.Instance"))
            {
                try { if (!single.WaitOne(0)) return; }
                catch (AbandonedMutexException) { }
                using (var stop = new EventWaitHandle(false, EventResetMode.AutoReset, StopName))
                {
                    Application.EnableVisualStyles();
                    Application.Run(new Listener(stop));
                }
            }
        }
        catch (Exception error)
        {
            try
            {
                Directory.CreateDirectory(Folders.Home);
                File.WriteAllText(Path.Combine(Folders.Home, "error.txt"), error.ToString());
            }
            catch (IOException) { }
            Environment.ExitCode = 1;
        }
    }

    static int ChooseFileWheel(string[] files)
    {
        Application.EnableVisualStyles();
        var folders = new Folders();
        using (var wheel = new Wheel())
        using (var timer = new System.Windows.Forms.Timer { Interval = 25 })
        using (var hook = new WheelScrollHook(wheel.QueueScroll))
        {
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            bool wasDown = Listener.KeyDown(1);
            timer.Tick += delegate
            {
                bool down = Listener.KeyDown(1);
                if (elapsed.Elapsed.TotalSeconds >= 45 || Listener.KeyDown(27) ||
                    (down && !wasDown && !wheel.ContainsScreenPoint(Cursor.Position)))
                    wheel.Close();
                else wheel.ApplyScroll();
                wasDown = down;
            };
            var targets = folders.Targets(Listener.ShellFolders(true));
            if (targets.Length == 0) return 11;
            wheel.Probe(Cursor.Position, targets, true, files);
            timer.Start();
            Application.Run(wheel);
            return 0;
        }
    }

    static void Command(string command)
    {
        if (command == "--stop")
        {
            EventWaitHandle signal;
            if (EventWaitHandle.TryOpenExisting(StopName, out signal))
                using (signal) signal.Set();
            return;
        }
        if (command != "--enable-startup" && command != "--disable-startup")
            throw new ArgumentException("Unknown command");
        var shortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup, Environment.SpecialFolderOption.Create), "UToolsWheel.lnk");
        if (command == "--disable-startup") { File.Delete(shortcut); return; }
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
        dynamic link = shell.CreateShortcut(shortcut);
        link.TargetPath = Application.ExecutablePath;
        link.WorkingDirectory = Path.GetDirectoryName(Application.ExecutablePath);
        link.Description = "uTools 文件轮盘";
        link.Save();
    }
}

static class GestureLog
{
    public static void Write(string stage)
    {
        try
        {
            Directory.CreateDirectory(Folders.Home);
            string file = Path.Combine(Folders.Home, "gesture.log");
            if (File.Exists(file) && new FileInfo(file).Length > 65536) File.Delete(file);
            File.AppendAllText(file, DateTime.Now.ToString("HH:mm:ss.fff") + " " + stage + Environment.NewLine);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

class Listener : ApplicationContext
{
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
    internal static bool KeyDown(int key) { return GetAsyncKeyState(key) < 0; }
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder name, int size);

    readonly EventWaitHandle stop;
    readonly System.Windows.Forms.Timer input = new System.Windows.Forms.Timer { Interval = 25 };
    readonly System.Windows.Forms.Timer browse = new System.Windows.Forms.Timer { Interval = 1700 };
    readonly Folders folders = new Folders();
    readonly Wheel wheel = new Wheel();
    readonly WheelScrollHook scrollHook;
    volatile string[] directoryTargets = new string[0];
    int refreshingFolders;
    bool wasDown, candidate, attempted;
    string pendingReminder;
    Point origin, still;
    Point? releasePoint;
    long stillSince, probeSince;

    public Listener(EventWaitHandle stop)
    {
        this.stop = stop;
        scrollHook = new WheelScrollHook(wheel.QueueScroll, RecordTextRelease);
        wheel.TextReminderRequested += text => pendingReminder = text;
        input.Tick += Tick;
        browse.Tick += delegate
        {
            if (!wheel.Visible && !KeyDown(1)) RefreshFolders();
        };
        GestureLog.Write("listener " + Path.GetFileName(Application.ExecutablePath));
        input.Start();
        browse.Start();
        RefreshFolders();
    }

    void RefreshFolders()
    {
        if (Interlocked.Exchange(ref refreshingFolders, 1) != 0) return;
        var worker = new Thread(() =>
        {
            try
            {
                if (directoryTargets.Length == 0) directoryTargets = folders.Targets(new string[0]);
                var quickAccess = ShellFolders(true);
                directoryTargets = folders.Targets(quickAccess);
                folders.Observe(ShellFolders(false));
                directoryTargets = folders.Targets(quickAccess);
            }
            catch (Exception error) { GestureLog.Write("folder refresh failed: " + error.GetType().Name); }
            finally { Interlocked.Exchange(ref refreshingFolders, 0); }
        }) { IsBackground = true };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
    }

    static bool ExplorerForeground()
    {
        var name = new System.Text.StringBuilder(128);
        GetClassName(GetForegroundWindow(), name, name.Capacity);
        return new[] { "CabinetWClass", "ExploreWClass", "Progman", "WorkerW" }.Contains(name.ToString());
    }

    internal static string[] ShellFolders(bool quickAccess)
    {
        var paths = new List<string>();
        try
        {
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application"));
            if (quickAccess)
            {
                dynamic folder = shell.NameSpace("shell:::{679f85cb-0220-4080-b29b-5540cc05aab6}");
                if (folder != null)
                    foreach (dynamic item in folder.Items())
                        if (item.IsFolder) paths.Add((string)item.Path);
            }
            else
            {
                foreach (dynamic window in shell.Windows())
                {
                    try
                    {
                        if (String.Equals(Path.GetFileName((string)window.FullName), "explorer.exe", StringComparison.OrdinalIgnoreCase))
                        {
                            string path = window.Document.Folder.Self.Path;
                            if (Path.IsPathRooted(path) && !path.StartsWith("::")) paths.Add(path);
                        }
                    }
                    catch (COMException) { }
                    catch (UnauthorizedAccessException) { }
                    catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { }
                }
            }
        }
        catch (COMException) { }
        catch (UnauthorizedAccessException) { }
        catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { }
        return paths.ToArray();
    }

    static bool Moved(Point a, Point b, int pixels)
    {
        long x = a.X - b.X, y = a.Y - b.Y;
        return x * x + y * y >= pixels * pixels;
    }

    void Tick(object sender, EventArgs e)
    {
        if (stop.WaitOne(0)) { ExitThread(); return; }
        wheel.ApplyScroll();
        if (pendingReminder != null)
        {
            string text = pendingReminder;
            pendingReminder = null;
            wheel.Hide();
            var editor = new QuickReminder(text);
            editor.Shown += delegate { GestureLog.Write("editor shown"); };
            editor.Show();
        }
        ObservePointer(KeyDown(1), KeyDown(27), Cursor.Position, CurrentMilliseconds());
    }

    static long CurrentMilliseconds()
    {
        return DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
    }

    void RecordTextRelease()
    {
        var cursor = Cursor.Position;
        var now = CurrentMilliseconds();
        wheel.RecordTextRelease(cursor);
        releasePoint = cursor;
        probeSince = now;
        wasDown = false;
        candidate = false;
    }

    void ObservePointer(bool down, bool escape, Point cursor, long now)
    {
        if (wheel.Visible && (wheel.HasText || wheel.Choosing) && escape)
        {
            wheel.Hide();
            candidate = false;
            wasDown = down;
            return;
        }
        if (wheel.Choosing)
        {
            if (!down || wasDown || wheel.ContainsScreenPoint(cursor))
            {
                wasDown = down;
                candidate = false;
                return;
            }
            // Handle an outside press as the start of the next gesture below.
        }
        if (!down)
        {
            if (wasDown)
            {
                probeSince = now;
                releasePoint = cursor;
            }
            if (wheel.Visible && now - probeSince > 175)
            {
                // Give OLE the first chance to drop, then use the pointer captured at release.
                if (!wheel.FinishReleasedText(releasePoint)) wheel.Hide();
                releasePoint = null;
            }
        }
        else if (!wasDown)
        {
            wheel.Hide();
            candidate = true;
            attempted = false;
            releasePoint = null;
            origin = still = cursor;
            stillSince = now;
        }
        else if (candidate && !attempted)
        {
            if (Moved(still, cursor, 5)) { still = cursor; stillSince = now; }
            if (Moved(origin, cursor, 12) && now - stillSince >= 500)
            {
                attempted = true;
                probeSince = now;
                bool explorer = ExplorerForeground();
                var targets = explorer ? directoryTargets : new string[0];
                if (GetAsyncKeyState(1) < 0) wheel.Probe(cursor, targets);
            }
        }
        else if (wheel.Visible && !wheel.HasFiles && !wheel.HasText && now - probeSince > 400) wheel.Hide();
        wasDown = down;
    }

    protected override void ExitThreadCore()
    {
        input.Dispose();
        browse.Dispose();
        scrollHook.Dispose();
        wheel.Dispose();
        base.ExitThreadCore();
    }
}

class Wheel : Form
{
    const string Reminder = ":taskbell";
    const int SizePx = 344;
    const int Radius = 165;
    const int Center = SizePx / 2;
    const int Hole = 49;
    string[] targets = new string[0];
    const int PanelWidth = 182;
    const int RowHeight = 30;
    const int VisibleRows = 8;
    readonly DirectoryBranch branch = new DirectoryBranch();
    readonly List<MenuRow> rows = new List<MenuRow>();
    readonly System.Windows.Forms.Timer dwell = new System.Windows.Forms.Timer { Interval = 330 };
    readonly System.Windows.Forms.Timer animation = new System.Windows.Forms.Timer { Interval = 15 };
    readonly System.Diagnostics.Stopwatch transition = new System.Diagnostics.Stopwatch();
    const int AnimationMs = 145;
    int animatedLevel = -1;
    int animationDirection;
    readonly Dictionary<DirectoryBranch.Level, int> scrollDeltas = new Dictionary<DirectoryBranch.Level, int>();
    Point? scrollPosition;
    MenuRow activeRow;
    string hovered;
    string hoverKey;
    Action pending;
    string[] held;
    string heldText;
    string draggedText;
    Point? textReleasePoint;
    bool textDragCanceled;
    bool reminderPressed;
    public event Action<string> TextReminderRequested;
    bool move;
    bool centerPressed;
    bool openRight;
    int columns;
    Point centerPoint;
    Rectangle screen;
    public bool Choosing { get { return held != null || heldText != null; } }
    bool shortcut;
    bool reminderHovered;

    sealed class MenuRow
    {
        public Rectangle Bounds;
        public int Level;
        public bool Header;
        public string Path;
        public string Text;
        public Action Navigate;
    }
    public bool HasFiles { get; private set; }
    public bool HasText { get; private set; }

    public Wheel()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        AllowDrop = true;
        DoubleBuffered = true;
        ClientSize = new Size(SizePx, SizePx);
        BackColor = Color.Magenta;
        TransparencyKey = BackColor;
        animation.Tick += delegate
        {
            if (transition.ElapsedMilliseconds >= AnimationMs) StopAnimation();
            Invalidate();
        };
        dwell.Tick += delegate
        {
            dwell.Stop();
            var action = pending;
            pending = null;
            if (Visible && action != null) action();
        };
    }

    protected override bool ShowWithoutActivation { get { return true; } }
    protected override CreateParams CreateParams
    {
        get { var options = base.CreateParams; options.ExStyle |= 0x08000000; return options; }
    }

    public void Probe(Point cursor, string[] folders, bool shortcut = false, string[] files = null)
    {
        GestureLog.Write("probe");
        targets = folders.Take(Folders.MaxDirectoryTargets).ToArray();
        this.shortcut = shortcut;
        HasFiles = false;
        HasText = false;
        draggedText = null;
        textReleasePoint = null;
        textDragCanceled = false;
        held = files != null && files.Length > 0 ? files : null;
        heldText = null;
        move = false;
        ClearHover();
        branch.Clear();
        rows.Clear();
        Opacity = shortcut ? 1 : 0.02;
        screen = Screen.FromPoint(cursor).Bounds;
        ClientSize = new Size(SizePx, SizePx);
        Location = new Point(Math.Max(screen.Left, Math.Min(cursor.X - Center, screen.Right - SizePx)),
            Math.Max(screen.Top, Math.Min(cursor.Y - Center, screen.Bottom - SizePx)));
        centerPoint = new Point(Left + Center, Top + Center);
        Invalidate();
        Show();
        Update();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        if (!Visible)
        {
            held = null;
            heldText = null;
            draggedText = null;
            textReleasePoint = null;
            textDragCanceled = false;
            reminderPressed = false;
            centerPressed = false;
            scrollDeltas.Clear();
            scrollPosition = null;
            ClearHover();
            StopAnimation();
        }
        base.OnVisibleChanged(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (dwell != null) dwell.Dispose();
            if (animation != null) animation.Dispose();
        }
        base.Dispose(disposing);
    }

    void ClearHover()
    {
        dwell.Stop();
        pending = null;
        hovered = hoverKey = null;
        activeRow = null;
        reminderHovered = false;
    }

    public bool ContainsScreenPoint(Point point)
    {
        var local = PointToClient(point);
        return IsCenter(local) || SectorAt(local) != null || RowAt(local) != null;
    }

    Point LocalCenter { get { return PointToClient(centerPoint); } }

    bool IsCenter(Point point)
    {
        var center = LocalCenter;
        int x = point.X - center.X, y = point.Y - center.Y;
        return x * x + y * y < Hole * Hole;
    }

    string RootAt(Point point)
    {
        string sector = SectorAt(point);
        return sector == Reminder ? null : sector;
    }

    string SectorAt(Point point)
    {
        if (targets.Length == 0) return null;
        var center = LocalCenter;
        double x = point.X - center.X, y = point.Y - center.Y;
        double distance = x * x + y * y;
        if (distance < Hole * Hole || distance > Radius * Radius) return null;
        double angle = (Math.Atan2(y, x) * 180 / Math.PI + 450) % 360;
        return targets[(int)(angle / (360.0 / targets.Length))];
    }

    MenuRow RowAt(Point point) { return rows.Find(row => row.Bounds.Contains(point)); }

    static string NameOf(string path)
    {
        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));
        return String.IsNullOrEmpty(name) ? path : name;
    }

    void LayoutBranch()
    {
        int leftSpace = centerPoint.X - Center - screen.Left;
        int rightSpace = screen.Right - (centerPoint.X + Center);
        openRight = rightSpace >= leftSpace;
        columns = Math.Max(1, Math.Max(leftSpace, rightSpace) / PanelWidth);
        int visibleCount = Math.Min(columns, branch.Count);
        int extension = visibleCount * PanelWidth;
        int left = centerPoint.X - Center - (openRight ? 0 : extension);
        left = Math.Max(screen.Left, Math.Min(left, screen.Right - SizePx - extension));
        Bounds = new Rectangle(left, centerPoint.Y - Center, SizePx + extension, SizePx);

        rows.Clear();
        int first = branch.Count - visibleCount;
        var center = LocalCenter;
        for (int i = first; i < branch.Count; i++)
        {
            var level = branch[i];
            int offset = i - first;
            int x = openRight ? center.X + Radius + offset * PanelWidth : center.X - Radius - (offset + 1) * PanelWidth;
            int y = 0;
            int index = i;
            bool canGoBack = i == first && first > 0;
            string title = (canGoBack ? "‹  " : "") + NameOf(level.Parent);
            AddRow(ref y, x, index, title, null,
                canGoBack ? (Action)(() => { ClearHover(); branch.CloseFrom(first - 1); LayoutBranch(); }) : null, true);
            foreach (string child in level.Children.Skip(level.Offset).Take(VisibleRows))
                AddRow(ref y, x, index, NameOf(child), child, null);
        }
        Invalidate();
    }

    void AddRow(ref int y, int x, int level, string label, string path, Action navigate, bool header = false)
    {
        rows.Add(new MenuRow {
            Bounds = new Rectangle(x, y, PanelWidth, RowHeight),
            Level = level, Header = header, Path = path, Text = label, Navigate = navigate
        });
        y += RowHeight;
    }

    public bool QueueScroll(int delta)
    {
        if (!Visible || (!shortcut && !HasFiles) || delta == 0) return false;
        // Hook coordinates are physical pixels. Cursor.Position uses the same DPI context as this form.
        return QueueScrollAt(PointToClient(Cursor.Position), delta);
    }

    bool QueueScrollAt(Point point, int delta)
    {
        var row = RowAt(point);
        if (row == null) return false;
        var level = branch[row.Level];
        int accumulated;
        scrollDeltas.TryGetValue(level, out accumulated);
        scrollDeltas[level] = accumulated + delta;
        return true;
    }

    public void ApplyScroll()
    {
        if (scrollDeltas.Count == 0) return;
        foreach (var queued in scrollDeltas.ToArray())
        {
            int index = branch.IndexOf(queued.Key);
            if (index < 0) { scrollDeltas.Remove(queued.Key); continue; }
            int steps = queued.Value / 120;
            if (steps == 0) continue;
            int remainder = queued.Value % 120;
            if (remainder == 0) scrollDeltas.Remove(queued.Key);
            else scrollDeltas[queued.Key] = remainder;
            ScrollRows(index, -steps);
        }
    }

    internal static int ClampOffset(int offset, int delta, int count)
    {
        return Math.Max(0, Math.Min(offset + delta, Math.Max(0, count - VisibleRows)));
    }

    void ScrollRows(int index, int delta)
    {
        if (index < 0 || index >= branch.Count) return;
        var level = branch[index];
        int offset = ClampOffset(level.Offset, delta, level.Children.Length);
        if (offset == level.Offset) return;
        ClearHover();
        scrollPosition = Cursor.Position;
        animationDirection = Math.Sign(offset - level.Offset);
        level.Offset = offset;
        if (index + 1 < branch.Count &&
            !level.Children.Skip(offset).Take(VisibleRows).Contains(branch[index + 1].Parent, StringComparer.OrdinalIgnoreCase))
            branch.CloseFrom(index + 1);
        LayoutBranch();
        activeRow = RowAt(PointToClient(Cursor.Position));
        hovered = activeRow == null ? null : activeRow.Path;
        animatedLevel = index;
        transition.Restart();
        animation.Start();
    }

    void StopAnimation()
    {
        animation.Stop();
        transition.Reset();
        animatedLevel = -1;
    }
    void Track(Point point)
    {
        var row = RowAt(point);
        string path = row == null ? RootAt(point) : row.Path;
        string key = row != null && row.Navigate != null ? "@" + row.Bounds.Location : path;
        activeRow = row;
        hovered = path;
        reminderHovered = HasText && row == null && SectorAt(point) == Reminder;
        if (scrollPosition.HasValue)
        {
            var screenPoint = PointToScreen(point);
            var lastScroll = scrollPosition.Value;
            if (Math.Abs(screenPoint.X - lastScroll.X) <= 2 && Math.Abs(screenPoint.Y - lastScroll.Y) <= 2)
            {
                Invalidate();
                return;
            }
            scrollPosition = null;
        }
        if (animatedLevel != (row == null ? -2 : row.Level) && hoverKey != key)
        {
            hoverKey = key;
            dwell.Stop();
            pending = row != null && row.Navigate != null ? row.Navigate :
                path == null ? null : (Action)(() =>
                {
                    if (branch.Expand(path, row == null)) LayoutBranch();
                });
            if (pending != null) dwell.Start();
        }
        Invalidate();
    }

    protected override void OnDragEnter(DragEventArgs e) { UpdateDrag(e); }
    protected override void OnDragOver(DragEventArgs e) { UpdateDrag(e); }
    protected override void OnDragLeave(EventArgs e)
    {
        if (HasText && !Choosing && Listener.KeyDown(27)) textDragCanceled = true;
        ClearHover();
        Invalidate();
    }

    static bool AcceptsFiles(DragEventArgs e)
    {
        return (e.AllowedEffect & DragDropEffects.Copy) != 0 && e.Data.GetDataPresent(DataFormats.FileDrop);
    }

    internal static string ReadDraggedText(IDataObject data)
    {
        if (data.GetDataPresent(DataFormats.FileDrop) ||
            data.GetDataPresent("UniformResourceLocator") || data.GetDataPresent("UniformResourceLocatorW"))
            return null;
        string text = data.GetData(DataFormats.UnicodeText) as string ??
            data.GetData(DataFormats.Text) as string;
        if (String.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim();
        if (text.Length <= 300) return text;
        return text.Substring(0, Char.IsHighSurrogate(text[299]) ? 299 : 300);
    }

    void UpdateDrag(DragEventArgs e)
    {
        if (shortcut || Choosing)
        {
            e.Effect = DragDropEffects.None;
            return;
        }
        if (!HasFiles && !HasText)
        {
            HasFiles = targets.Length > 0 && AcceptsFiles(e);
            if (HasFiles) GestureLog.Write("file entered");
            draggedText = !HasFiles && (e.AllowedEffect & DragDropEffects.Copy) != 0 ?
                ReadDraggedText(e.Data) : null;
            HasText = draggedText != null;
            if (!HasFiles && !HasText)
            {
                e.Effect = DragDropEffects.None;
                Hide();
                return;
            }
            if (HasText)
            {
                GestureLog.Write("text entered");
                targets = new[] { Reminder };
            }
        }
        Opacity = 1;
        var point = PointToClient(new Point(e.X, e.Y));
        Track(point);
        e.Effect = hovered != null || IsCenter(point) || reminderHovered ?
            DragDropEffects.Copy : DragDropEffects.None;
    }

    protected override void OnDragDrop(DragEventArgs e)
    {
        e.Effect = DragDropEffects.None;
        if (shortcut || Choosing) return;
        if (HasText)
        {
            GestureLog.Write("OLE drop");
            if (FinishReleasedText(new Point(e.X, e.Y))) e.Effect = e.AllowedEffect & DragDropEffects.Copy;
            else Hide();
            return;
        }
        if (!HasFiles || !AcceptsFiles(e)) return;
        var paths = e.Data.GetData(DataFormats.FileDrop) as string[];
        if (paths == null || paths.Length == 0) { Hide(); return; }
        var point = PointToClient(new Point(e.X, e.Y));
        if (IsCenter(point))
        {
            held = paths;
            centerPressed = false;
            ClearHover();
            Invalidate();
            return;
        }
        var row = RowAt(point);
        var target = row == null ? RootAt(point) : row.Path;
        bool moving = move;
        if (target != null)
        {
            e.Effect = DragDropEffects.Copy;
            BeginInvoke(new Action(() => TransferInBackground(paths, target, moving)));
        }
        Hide();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (shortcut || Choosing) Track(e.Location);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        if (shortcut || Choosing) { ClearHover(); Invalidate(); }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        centerPressed = held != null && e.Button == MouseButtons.Left && IsCenter(e.Location);
        reminderPressed = heldText != null && e.Button == MouseButtons.Left && SectorAt(e.Location) == Reminder;
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (shortcut && !Choosing && e.Button == MouseButtons.Left)
        {
            ChooseShortcut(e.Location);
            return;
        }
        if (!Choosing || e.Button != MouseButtons.Left) return;
        if (heldText != null)
        {
            bool open = reminderPressed && SectorAt(e.Location) == Reminder;
            reminderPressed = false;
            if (open) OpenTextReminder(heldText);
            return;
        }
        bool toggle = centerPressed && IsCenter(e.Location);
        centerPressed = false;
        if (IsCenter(e.Location))
        {
            if (toggle)
            {
                move = !move;
                Invalidate();
            }
            return;
        }
        var row = RowAt(e.Location);
        if (row != null && row.Navigate != null)
        {
            ClearHover();
            row.Navigate();
            return;
        }
        var target = row == null ? RootAt(e.Location) : row.Path;
        if (target == null) return;
        var paths = held;
        bool moving = move;
        Hide();
        if (shortcut)
        {
            Transfer(paths, target, moving);
            Close();
        }
        else TransferInBackground(paths, target, moving);
    }

    internal bool RecordTextRelease(Point screenPoint)
    {
        if (!Visible || Choosing || draggedText == null || textDragCanceled) return false;
        textReleasePoint = screenPoint;
        return true;
    }

    internal bool FinishReleasedText(Point? screenPoint)
    {
        var actual = textReleasePoint ?? screenPoint;
        if (HasText && !Choosing) GestureLog.Write(textDragCanceled ? "release canceled" :
            textReleasePoint.HasValue ? "release from hook" : actual.HasValue ? "release from event" : "release missing");
        return !textDragCanceled && actual.HasValue && FinishTextDrop(PointToClient(actual.Value));
    }

    bool FinishTextDrop(Point point)
    {
        if (Choosing || draggedText == null) return false;
        bool keep = IsCenter(point);
        if (!keep && SectorAt(point) != Reminder)
        {
            GestureLog.Write("release outside wheel");
            return false;
        }
        string text = draggedText;
        draggedText = null;
        if (keep)
        {
            GestureLog.Write("text retained");
            heldText = text;
            ClearHover();
            Invalidate();
        }
        else OpenTextReminder(text);
        return true;
    }

    void OpenTextReminder(string title)
    {
        // Let the source finish its OLE Drop before hiding the registered drop target.
        heldText = null;
        GestureLog.Write("editor requested");
        var requested = TextReminderRequested;
        if (requested != null) requested(title);
    }

    void ChooseShortcut(Point point)
    {
        var row = RowAt(point);
        if (row != null && row.Navigate != null) { row.Navigate(); return; }
        string choice = row == null ? SectorAt(point) : row.Path;
        if (choice == null && !IsCenter(point)) return;
        if (choice != null)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(choice) { UseShellExecute = true });
            }
            catch (System.ComponentModel.Win32Exception error)
            {
                MessageBox.Show(error.Message, "文件轮盘");
            }
        }
        Close();
    }
    static void TransferInBackground(string[] paths, string target, bool move)
    {
        var worker = new Thread(() => Transfer(paths, target, move)) { IsBackground = true };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
    }

    static void Transfer(string[] paths, string target, bool move)
    {
        try
        {
            if (!Directory.Exists(target)) throw new DirectoryNotFoundException("目标文件夹已不存在。");
            var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var source in paths)
            {
                string absolute = Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar);
                string name = Path.GetFileName(absolute);
                if (String.IsNullOrEmpty(name)) throw new IOException("无法处理磁盘根目录。");
                string destination = Path.Combine(target, name);
                if (!destinations.Add(destination) || File.Exists(destination) || Directory.Exists(destination))
                    throw new IOException("目标内已有同名项目：" + name);
                if (Directory.Exists(source) && Path.GetFullPath(destination).StartsWith(absolute + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("不能将文件夹放进它自己的子目录。");
            }
            foreach (var source in paths)
            {
                string destination = Path.Combine(target, Path.GetFileName(Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar)));
                if (move)
                {
                    if (Directory.Exists(source)) FileSystem.MoveDirectory(source, destination, UIOption.AllDialogs, UICancelOption.ThrowException);
                    else FileSystem.MoveFile(source, destination, UIOption.AllDialogs, UICancelOption.ThrowException);
                }
                else if (Directory.Exists(source)) FileSystem.CopyDirectory(source, destination, UIOption.AllDialogs, UICancelOption.ThrowException);
                else FileSystem.CopyFile(source, destination, UIOption.AllDialogs, UICancelOption.ThrowException);
            }
            try
            {
                if (paths.Length > 0)
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe",
                        "/n, \"" + new DirectoryInfo(target).FullName.TrimEnd('\\') + "\\.\"") { UseShellExecute = false });
                GestureLog.Write(move ? "file move completed" : "file copy completed");
            }
            catch (Exception error)
            {
                MessageBox.Show("文件操作已完成，但无法打开目标文件夹。\n" + error.Message, "文件轮盘");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            MessageBox.Show(error.Message, move ? "文件轮盘 · 移动未完成" : "文件轮盘 · 复制未完成", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    static GraphicsPath Rounded(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        int size = 2 * radius;
        path.AddArc(bounds.Left, bounds.Top, size, size, 180, 90);
        path.AddArc(bounds.Right - size, bounds.Top, size, size, 270, 90);
        path.AddArc(bounds.Right - size, bounds.Bottom - size, size, size, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - size, size, size, 90, 90);
        path.CloseFigure();
        return path;
    }

    static GraphicsPath Sector(Point origin, float start, float span)
    {
        const int innerRadius = Hole + 7;
        const int outerRadius = Radius - 2;
        var path = new GraphicsPath();
        path.AddArc(origin.X - outerRadius, origin.Y - outerRadius, outerRadius * 2, outerRadius * 2, start, span);
        path.AddArc(origin.X - innerRadius, origin.Y - innerRadius, innerRadius * 2, innerRadius * 2, start + span, -span);
        path.CloseFigure();
        return path;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var graphics = e.Graphics;
        if (!shortcut && !HasFiles && !HasText)
        {
            // Text drags start with no directory targets. Paint a hit surface before checking the list.
            graphics.Clear(Color.FromArgb(31, 44, 61));
            return;
        }
        if (targets.Length == 0) return;
        var origin = LocalCenter;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        var surfaceColor = Color.FromArgb(248, 250, 253);
        var accentColor = Color.FromArgb(33, 91, 218);
        var textColor = Color.FromArgb(24, 39, 65);
        using (var surface = new SolidBrush(surfaceColor))
        using (var accent = new SolidBrush(accentColor))
        using (var expanded = new SolidBrush(Color.FromArgb(225, 236, 255)))
        using (var text = new SolidBrush(textColor))
        using (var secondary = new SolidBrush(Color.FromArgb(83, 103, 137)))
        using (var muted = new SolidBrush(Color.FromArgb(116, 130, 155)))
        using (var outline = new Pen(Color.FromArgb(208, 220, 238)))
        using (var label = new Font("Microsoft YaHei UI", 10))
        using (var strong = new Font("Microsoft YaHei UI", 11, FontStyle.Bold))
        using (var small = new Font("Microsoft YaHei UI", 8.5f))
        using (var centered = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
        using (var aligned = new StringFormat(centered) { Alignment = StringAlignment.Near })
        {
            // Keep the color key off antialiased edges by drawing an opaque base first.
            var face = new Rectangle(origin.X - Radius, origin.Y - Radius, Radius * 2, Radius * 2);
            graphics.SmoothingMode = SmoothingMode.None;
            graphics.FillEllipse(surface, face);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var rim = Rectangle.Inflate(face, -2, -2);
            graphics.DrawEllipse(outline, rim);

            float span = 360f / targets.Length;
            for (int i = 0; i < targets.Length; i++)
            {
                float start = -90 + i * span;
                string path = targets[i];
                bool reminder = path == Reminder;
                bool pointed = reminder ? reminderHovered : path == hovered && activeRow == null;
                bool opened = !reminder && branch.Contains(path);
                using (var sector = Sector(origin, start + 0.4f, span - 0.8f))
                {
                    graphics.FillPath(pointed ? accent : opened ? expanded : surface, sector);
                    if (!pointed) graphics.DrawPath(outline, sector);
                }
                double radians = (start + span / 2) * Math.PI / 180;
                float x = origin.X + (float)Math.Cos(radians) * 109;
                float y = origin.Y + (float)Math.Sin(radians) * 109;
                graphics.DrawString(reminder ? "提醒" : NameOf(path), label,
                    pointed ? Brushes.White : text,
                    new RectangleF(x - 47, y - 12, 94, 24), centered);
            }

            graphics.FillEllipse(accent, origin.X - Hole, origin.Y - Hole, 2 * Hole, 2 * Hole);
            graphics.DrawString(HasText ? "文本" : shortcut && !Choosing ? "文件" : move ? "移动" : "复制", strong, Brushes.White,
                new RectangleF(origin.X - Hole, origin.Y - 22, 2 * Hole, 24), centered);
            graphics.DrawString(HasText ? (heldText != null ? "点击提醒" : reminderHovered ? "松手提醒" : "松手保留") : shortcut && !Choosing ? "点击关闭" : Choosing ? "点击切换" : "松手保留", small, Brushes.White,
                new RectangleF(origin.X - Hole, origin.Y + 2, 2 * Hole, 23), centered);

            double progress = Math.Min(1, transition.Elapsed.TotalMilliseconds / AnimationMs);
            double eased = 1 - Math.Pow(1 - progress, 3);
            foreach (var column in rows.GroupBy(row => row.Level))
            {
                var columnBounds = Rectangle.Inflate(Rectangle.Union(column.First().Bounds, column.Last().Bounds), -2, 0);
                using (var panel = Rounded(columnBounds, 12))
                {
                    graphics.SmoothingMode = SmoothingMode.None;
                    graphics.FillPath(surface, panel);
                    graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    using (var border = Rounded(Rectangle.Inflate(columnBounds, -2, -2), 10))
                        graphics.DrawPath(outline, border);
                }
                foreach (var row in column)
                {
                    bool selected = row == activeRow && (row.Path != null || row.Navigate != null);
                    bool opened = row.Path != null && branch.Contains(row.Path);
                    var card = Rectangle.Inflate(row.Bounds, -6, -2);
                    if (selected || opened)
                        using (var highlight = Rounded(card, 7))
                            graphics.FillPath(selected ? accent : expanded, highlight);

                    var caption = new RectangleF(card.X + 10, card.Y, card.Width - 20, card.Height);
                    bool sliding = row.Level == animatedLevel && row.Path != null;
                    if (sliding) caption.Y += (float)((1 - eased) * 11 * animationDirection);
                    var ink = selected ? Brushes.White : row.Header || row.Path != null ? text : row.Navigate != null ? secondary : muted;
                    var state = graphics.Save();
                    graphics.SetClip(row.Bounds, CombineMode.Intersect);
                    if (sliding && !selected)
                    {
                        using (var fade = new SolidBrush(Color.FromArgb((int)(130 + 125 * eased), textColor)))
                            graphics.DrawString(row.Text, label, fade, caption, aligned);
                    }
                    else graphics.DrawString(row.Text, row.Header ? strong : label, ink, caption, aligned);
                    graphics.Restore(state);
                }
            }
        }
    }
}
