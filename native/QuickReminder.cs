using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

sealed class QuickReminder : Form
{
    static readonly object ScheduleLock = new object();
    static bool initialized;
    readonly TextBox title = new TextBox { MaxLength = 300, Multiline = true, Dock = DockStyle.Fill };
    readonly DateTimePicker time = new DateTimePicker {
        Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy-MM-dd HH:mm:ss", Dock = DockStyle.Fill
    };
    readonly Button save = new Button { Text = "保存提醒", AutoSize = true };
    bool saving;

    public QuickReminder(string text)
    {
        Text = "文件轮盘 · 新建提醒";
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(440, 205);
        Font = new Font("Microsoft YaHei UI", 10);
        title.Text = text;
        time.Value = DateTime.Now.AddMinutes(5);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 5 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label { Text = "提醒内容", AutoSize = true });
        layout.Controls.Add(title);
        layout.Controls.Add(new Label { Text = "提醒时间", AutoSize = true, Margin = new Padding(3, 7, 3, 2) });
        layout.Controls.Add(time);
        var actions = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill };
        var cancel = new Button { Text = "取消", AutoSize = true, DialogResult = DialogResult.Cancel };
        cancel.Click += delegate { Close(); };
        var minute = new Button { Text = "1 分钟后", AutoSize = true };
        minute.Click += delegate { time.Value = DateTime.Now.AddMinutes(1); };
        actions.Controls.Add(save);
        actions.Controls.Add(cancel);
        actions.Controls.Add(minute);
        layout.Controls.Add(actions);
        Controls.Add(layout);
        CancelButton = cancel;
        save.Click += async delegate { await SaveReminder(); };
        Load += delegate
        {
            var area = Screen.FromPoint(Cursor.Position).WorkingArea;
            Location = new Point(area.Left + Math.Max(0, (area.Width - Width) / 2),
                area.Top + Math.Max(0, (area.Height - Height) / 2));
        };
        Shown += delegate { BringToFront(); Activate(); time.Focus(); };
    }

    async Task SaveReminder()
    {
        if (saving) return;
        var text = title.Text.Trim();
        var when = new DateTimeOffset(time.Value);
        if (text.Length == 0 || when <= DateTimeOffset.Now)
        {
            MessageBox.Show(this, "请输入提醒内容，并选择未来的时间。", Text);
            return;
        }
        saving = true;
        save.Enabled = false;
        try
        {
            await Task.Run(() => Schedule(text, when));
            saving = false;
            Close();
        }
        catch (Exception error)
        {
            saving = false;
            save.Enabled = true;
            MessageBox.Show(this, error.Message, "保存提醒失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (saving && e.CloseReason == CloseReason.UserClosing) e.Cancel = true;
        base.OnFormClosing(e);
    }

    static void Schedule(string text, DateTimeOffset when)
    {
        var script = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TaskBell", "Bell.ps1");
        if (!File.Exists(script))
            throw new FileNotFoundException("请在「轮盘设置」中重新启动监听，完成提醒组件部署。");
        string json = new JavaScriptSerializer().Serialize(new { title = text, remindAt = when.ToString("o") });
        string payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
        lock (ScheduleLock)
        {
            if (!initialized)
            {
                Run(script, "Install", "");
                initialized = true;
            }
            Run(script, "Save", payload);
        }
    }

    static void Run(string script, string action, string payload)
    {
        string powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        var options = new ProcessStartInfo(powershell, "-NoProfile -ExecutionPolicy Bypass -File \"" +
            script + "\" " + action + " " + payload) {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true,
            StandardErrorEncoding = Encoding.UTF8
        };
        using (var process = Process.Start(options))
        {
            string error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
                throw new InvalidOperationException(String.IsNullOrWhiteSpace(error) ? "Windows 计划任务未能保存提醒。" : error.Trim());
        }
    }
}
