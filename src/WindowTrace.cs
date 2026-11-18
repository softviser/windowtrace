// WindowTrace: lists every window that opens and closes, the process behind it, its command line,
// its user and the chain of parent processes that launched it.
// Single file, no dependencies. Builds with the C# 5 compiler that ships with Windows (see build.ps1),
// so only C# 5 syntax is used here.
// Self-test: WindowTrace.exe --selftest   (exit code 0 = pass)
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Management;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Windows.Forms;

[assembly: AssemblyTitle("WindowTrace")]
[assembly: AssemblyDescription("Shows which process opened every window, with its command line, user and parent chain.")]
[assembly: AssemblyCompany("Softviser Yazılım")]
[assembly: AssemblyProduct("WindowTrace")]
[assembly: AssemblyCopyright("© 2026 Softviser Yazılım")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

class PInfo
{
    public int Pid, ParentPid;
    public long Start;
    public bool Ok;
    public string Name = "", Cmd = "", User = "";
}

class WindowTrace : Form
{
    delegate void WinEventProc(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
    delegate bool EnumProc(IntPtr hwnd, IntPtr lp);

    [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventProc proc, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc proc, IntPtr lp);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hwnd, StringBuilder sb, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, StringBuilder sb, int max);
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder sb, ref int size);
    [DllImport("kernel32.dll")] static extern bool GetProcessTimes(IntPtr h, out long created, out long exited, out long kernel, out long user);
    [DllImport("ntdll.dll")] static extern int NtQueryInformationProcess(IntPtr h, int cls, IntPtr buf, int len, out int ret);
    [DllImport("advapi32.dll")] static extern bool OpenProcessToken(IntPtr h, uint access, out IntPtr token);

    const string DefaultHint = "Right-click: copy / export.  Double-click: details.  Grey: closed window.  Navy: process start.";
    static readonly string[] JsonKeys = { "opened", "closed", "duration", "process", "title", "commandLine", "launchedBy", "launcherCommandLine", "user", "parentChain", "windowClass" };

    readonly ListView list = new ListView();
    readonly CheckBox showAll = new CheckBox();
    readonly TextBox search = new TextBox();
    readonly ComboBox kind = new ComboBox();
    readonly Label hint = new Label();
    readonly ContextMenuStrip menu = new ContextMenuStrip();
    readonly List<ListViewItem> all = new List<ListViewItem>(); // every record regardless of the filter, newest first
    readonly Dictionary<IntPtr, ListViewItem> open = new Dictionary<IntPtr, ListViewItem>();
    WinEventProc proc; // must stay referenced, otherwise the GC collects the delegate and the hook crashes

    WindowTrace(bool admin, bool demo)
    {
        Text = "WindowTrace" + (admin || demo ? "" : "  —  limited mode (not running as administrator)");
        Size = new Size(1500, 650);
        try { Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
        catch { }

        list.Dock = DockStyle.Fill;
        list.View = View.Details;
        list.FullRowSelect = true;
        list.GridLines = true;
        list.HideSelection = false; // keep the selection visible while the toolbar has the focus
        string[] cols = { "Opened", "Closed", "Duration", "Process (PID)", "Window title", "Command line", "Launched by (PID)", "Launcher command line", "User", "Parent chain", "Window class" };
        int[] widths = { 90, 90, 70, 150, 200, 320, 150, 260, 130, 320, 130 };
        for (int i = 0; i < cols.Length; i++) list.Columns.Add(cols[i], widths[i]);
        list.DoubleClick += delegate
        {
            if (list.SelectedItems.Count == 0) return;
            var sb = new StringBuilder();
            for (int i = 0; i < list.Columns.Count; i++)
                sb.AppendLine(list.Columns[i].Text + ": " + list.SelectedItems[0].SubItems[i].Text).AppendLine();
            MessageBox.Show(sb.ToString(), "Details (Ctrl+C copies)");
        };

        var bar = new FlowLayoutPanel();
        bar.Dock = DockStyle.Top;
        bar.AutoSize = true;
        var clear = new Button();
        clear.Text = "Clear";
        clear.Click += delegate { all.Clear(); list.Items.Clear(); open.Clear(); };

        // copy and export work on the selected rows, or on every visible (filtered) row when nothing is selected
        menu.Items.Add("Copy as text  (Ctrl+C)", null, delegate { CopyRows(AsText); });
        menu.Items.Add("Copy as JSON", null, delegate { CopyRows(AsJson); });
        menu.Items.Add("Copy as AI prompt", null, delegate { CopyRows(AsPrompt); });
        menu.Items.Add("-");
        menu.Items.Add("Select all  (Ctrl+A)", null, delegate { SelectAll(); });
        menu.Items.Add("Export (txt / json / csv / prompt)…", null, delegate { Export(); });
        list.ContextMenuStrip = menu;
        list.KeyDown += delegate(object s, KeyEventArgs e)
        {
            if (e.Control && e.KeyCode == Keys.C) CopyRows(AsText);
            if (e.Control && e.KeyCode == Keys.A) SelectAll();
        };
        var copy = new Button();
        copy.Text = "Copy / Export ▾";
        copy.AutoSize = true;
        copy.Click += delegate { menu.Show(copy, 0, copy.Height); };

        var lbl = new Label();
        lbl.Text = "Search:";
        lbl.AutoSize = true;
        lbl.Padding = new Padding(10, 6, 0, 0);
        search.Width = 220;
        search.TextChanged += delegate { ApplyFilter(); };
        kind.DropDownStyle = ComboBoxStyle.DropDownList;
        kind.Width = 130;
        kind.Items.AddRange(new object[] { "All", "Windows", "Processes", "Closed windows" });
        kind.SelectedIndex = 0;
        kind.SelectedIndexChanged += delegate { ApplyFilter(); };

        showAll.Text = "Also capture tool windows (tooltips, menus)";
        showAll.AutoSize = true;
        hint.AutoSize = true;
        hint.Padding = new Padding(10, 6, 0, 0);
        hint.Text = admin || demo ? DefaultHint
            : "Limited mode: very short-lived processes can be missed and some command lines are hidden.";

        var elevate = new Button();
        elevate.Text = "Restart as administrator";
        elevate.AutoSize = true;
        elevate.Click += delegate
        {
            try { Process.Start(new ProcessStartInfo(Application.ExecutablePath) { Verb = "runas" }); Close(); }
            catch (Exception ex) { hint.Text = "Could not elevate: " + ex.Message; } // UAC prompt declined
        };

        string maker = "Softviser Yazılım";
        var about = new LinkLabel();
        about.Text = "WindowTrace " + new Version(Application.ProductVersion).ToString(3) + " by " + maker;
        about.LinkArea = new LinkArea(about.Text.Length - maker.Length, maker.Length);
        about.AutoSize = true;
        about.Padding = new Padding(10, 6, 0, 0);
        // through explorer.exe so the browser does not inherit our elevation
        about.LinkClicked += delegate { Process.Start("explorer.exe", "https://softviser.com.tr"); };

        bar.Controls.Add(clear);
        bar.Controls.Add(copy);
        bar.Controls.Add(lbl);
        bar.Controls.Add(search);
        bar.Controls.Add(kind);
        bar.Controls.Add(showAll);
        if (!admin && !demo) bar.Controls.Add(elevate);
        bar.Controls.Add(about);
        bar.Controls.Add(hint);
        bar.SetFlowBreak(about, true); // the hint gets its own line
        Controls.Add(list);
        Controls.Add(bar);

        if (demo) { LoadDemo(); return; }

        EnumWindows(delegate(IntPtr h, IntPtr lp) { AddWindow(h, true); return true; }, IntPtr.Zero);
        proc = OnWinEvent;
        // 0x8001 DESTROY, 0x8002 SHOW, 0x8003 HIDE; flag 2 = skip our own process
        SetWinEventHook(0x8001, 0x8003, IntPtr.Zero, proc, 0, 0, 2);
        Load += delegate { StartProcessTrace(); }; // BeginInvoke needs the window handle
    }

    void OnWinEvent(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != 0 || idChild != 0 || hwnd == IntPtr.Zero) return;
        try
        {
            if (evt == 0x8002) AddWindow(hwnd, false);
            else CloseWindow(hwnd);
        }
        catch { } // an exception escaping a native callback kills the process
    }

    void AddWindow(IntPtr hwnd, bool existing)
    {
        if (open.ContainsKey(hwnd) || !IsWindowVisible(hwnd) || GetAncestor(hwnd, 2) != hwnd) return;
        if (!showAll.Checked && (GetWindowLong(hwnd, -20) & 0x80) != 0) return; // WS_EX_TOOLWINDOW
        var sb = new StringBuilder(512);
        GetWindowText(hwnd, sb, sb.Capacity);
        string title = sb.ToString();
        if (existing && title.Length == 0) return;
        sb.Length = 0;
        GetClassName(hwnd, sb, sb.Capacity);

        int pid;
        GetWindowThreadProcessId(hwnd, out pid);
        PInfo p = Query(pid), parent;
        string chain = Chain(p, out parent);
        DateTime now = DateTime.Now;
        var it = AddRow(existing ? "(already open)" : now.ToString("HH:mm:ss.fff"), p, parent, chain, title, sb.ToString(), false);
        if (!existing) it.Tag = now;
        open[hwnd] = it;
    }

    void CloseWindow(IntPtr hwnd)
    {
        ListViewItem it;
        if (!open.TryGetValue(hwnd, out it)) return;
        open.Remove(hwnd);
        DateTime now = DateTime.Now;
        it.SubItems[1].Text = now.ToString("HH:mm:ss.fff");
        if (it.Tag != null) it.SubItems[2].Text = (now - (DateTime)it.Tag).TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture) + " s";
        it.ForeColor = Color.Gray;
        if (kind.SelectedIndex == 3) ApplyFilter(); // the row has just entered the "closed" filter
    }

    ListViewItem AddRow(string time, PInfo p, PInfo parent, string chain, string title, string cls, bool isProcess)
    {
        return AddItem(isProcess, time, "", "", p.Name + " (" + p.Pid + ")", title, p.Cmd,
            parent == null ? "" : parent.Name + " (" + parent.Pid + ")", parent == null ? "" : parent.Cmd, p.User, chain, cls);
    }

    ListViewItem AddItem(bool isProcess, params string[] cells)
    {
        var it = new ListViewItem(cells);
        if (isProcess) { it.Name = "proc"; it.ForeColor = Color.Navy; }
        else if (cells[1] != "") it.ForeColor = Color.Gray; // demo rows arrive already closed
        all.Insert(0, it);
        if (Match(it)) list.Items.Insert(0, it);
        return it;
    }

    // Consoles hosted by Windows Terminal show up as a WindowsTerminal.exe window, so the command that
    // actually ran is only visible as a process start. Win32_ProcessStartTrace is exact but needs admin
    // rights; without them we fall back to polling.
    void StartProcessTrace()
    {
        try
        {
            var w = new ManagementEventWatcher("SELECT ProcessID, ParentProcessID, ProcessName FROM Win32_ProcessStartTrace");
            w.EventArrived += delegate(object s, EventArrivedEventArgs e)
            {
                int pid = Convert.ToInt32(e.NewEvent["ProcessID"]), ppid = Convert.ToInt32(e.NewEvent["ParentProcessID"]);
                PInfo p = Query(pid);
                if (!p.Ok || p.ParentPid != ppid) // already gone: settle for the name and parent PID in the event
                {
                    p = new PInfo();
                    p.Pid = pid;
                    p.ParentPid = ppid;
                    p.Ok = true;
                    p.Name = Convert.ToString(e.NewEvent["ProcessName"]);
                    p.Cmd = "(exited before it could be read)";
                }
                ProcessStarted(p);
            };
            w.Start();
        }
        catch { StartProcessPolling(); }
    }

    // shortcut: a process that starts and exits between two polls is never seen;
    // "Restart as administrator" switches to the exact trace above.
    void StartProcessPolling()
    {
        var known = new HashSet<int>();
        foreach (Process p in Process.GetProcesses()) { known.Add(p.Id); p.Dispose(); }
        var t = new Timer();
        t.Interval = 250;
        t.Tick += delegate
        {
            var now = new HashSet<int>();
            foreach (Process p in Process.GetProcesses()) { now.Add(p.Id); p.Dispose(); }
            foreach (int pid in now) if (!known.Contains(pid)) ProcessStarted(Query(pid));
            known = now;
        };
        t.Start();
    }

    void ProcessStarted(PInfo p) // called from the WMI thread or the UI thread
    {
        string time = DateTime.Now.ToString("HH:mm:ss.fff");
        PInfo parent;
        string chain = Chain(p, out parent);
        BeginInvoke((MethodInvoker)delegate { AddRow(time, p, parent, chain, "(process started)", "", true); });
    }

    bool Match(ListViewItem it)
    {
        bool isProcess = it.Name == "proc";
        int k = kind.SelectedIndex; // 0 all, 1 windows, 2 processes, 3 closed windows
        if (k == 1 && isProcess || k == 2 && !isProcess || k == 3 && (isProcess || it.SubItems[1].Text == "")) return false;
        string q = search.Text.Trim();
        if (q.Length == 0) return true;
        foreach (ListViewItem.ListViewSubItem s in it.SubItems)
            // both comparisons: under a Turkish UI culture "I" does not match "i" (CMD.EXE vs cmd.exe),
            // and ordinal comparison does not match "İ" with "i"
            if (s.Text.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0 || s.Text.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        return false;
    }

    void ApplyFilter()
    {
        list.BeginUpdate();
        list.Items.Clear();
        foreach (ListViewItem it in all) if (Match(it)) list.Items.Add(it);
        list.EndUpdate();
        hint.Text = "Showing " + list.Items.Count + " of " + all.Count + " records";
    }

    List<ListViewItem> Rows()
    {
        var rows = new List<ListViewItem>();
        if (list.SelectedItems.Count > 0) foreach (ListViewItem it in list.SelectedItems) rows.Add(it);
        else foreach (ListViewItem it in list.Items) rows.Add(it);
        return rows;
    }

    void SelectAll()
    {
        list.BeginUpdate();
        foreach (ListViewItem it in list.Items) it.Selected = true;
        list.EndUpdate();
    }

    void CopyRows(Func<List<ListViewItem>, string> format)
    {
        List<ListViewItem> rows = Rows();
        if (rows.Count == 0) return;
        try { Clipboard.SetText(format(rows)); hint.Text = "Copied " + rows.Count + " record(s) to the clipboard"; }
        catch (ExternalException) { hint.Text = "The clipboard is busy, try again"; }
    }

    void Export()
    {
        List<ListViewItem> rows = Rows();
        if (rows.Count == 0) return;
        using (var d = new SaveFileDialog())
        {
            d.Filter = "Text (*.txt)|*.txt|JSON (*.json)|*.json|CSV for Excel (*.csv)|*.csv|AI prompt (*.md)|*.md";
            d.FileName = "windowtrace-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            if (d.ShowDialog(this) != DialogResult.OK) return;
            int f = d.FilterIndex;
            string text = f == 2 ? AsJson(rows) : f == 3 ? AsCsv(rows) : f == 4 ? AsPrompt(rows) : AsText(rows);
            try
            {
                File.WriteAllText(d.FileName, text, new UTF8Encoding(f == 3)); // BOM for CSV only: Excel needs it for non-ASCII text
                hint.Text = "Exported " + rows.Count + " record(s) to " + d.FileName;
            }
            catch (Exception ex) { MessageBox.Show(ex.Message, "Export failed"); }
        }
    }

    string AsText(List<ListViewItem> rows)
    {
        var sb = new StringBuilder();
        foreach (ListViewItem it in rows)
        {
            for (int i = 0; i < list.Columns.Count; i++)
                if (it.SubItems[i].Text != "") sb.AppendLine(list.Columns[i].Text + ": " + it.SubItems[i].Text);
            sb.AppendLine();
        }
        return sb.ToString();
    }

    static string AsJson(List<ListViewItem> rows)
    {
        var sb = new StringBuilder("[");
        for (int r = 0; r < rows.Count; r++)
        {
            sb.Append(r == 0 ? "\r\n  {" : ",\r\n  {");
            sb.Append("\r\n    \"type\": \"" + (rows[r].Name == "proc" ? "process" : "window") + "\"");
            for (int i = 0; i < JsonKeys.Length; i++)
                sb.Append(",\r\n    \"" + JsonKeys[i] + "\": \"" + Esc(rows[r].SubItems[i].Text) + "\"");
            sb.Append("\r\n  }");
        }
        return sb.Append("\r\n]").ToString();
    }

    static string Esc(string s)
    {
        var sb = new StringBuilder();
        foreach (char c in s)
        {
            if (c == '"' || c == '\\') sb.Append('\\').Append(c);
            else if (c < ' ') sb.Append("\\u" + ((int)c).ToString("x4"));
            else sb.Append(c);
        }
        return sb.ToString();
    }

    static string AsPrompt(List<ListViewItem> rows)
    {
        return "Windows keep opening and closing on their own on my Windows PC. Below are records captured by WindowTrace, "
            + "a tool that logs every window that appears and every process that starts.\r\n"
            + "Fields: type (window = a window that appeared, process = a process that started), process (name and PID), "
            + "commandLine, launchedBy (the parent process), launcherCommandLine, parentChain (ancestors, nearest first), user.\r\n\r\n"
            + "For each record, explain:\r\n"
            + "1. What program or command is this, and what does it do?\r\n"
            + "2. What triggers it (scheduled task, service, startup item, another application)?\r\n"
            + "3. Is it malicious or suspicious? Based on what?\r\n"
            + "4. How do I verify that, and how do I stop or disable it, step by step?\r\n\r\n"
            + "Records:\r\n" + AsJson(rows) + "\r\n";
    }

    string AsCsv(List<ListViewItem> rows)
    {
        string sep = CultureInfo.CurrentCulture.TextInfo.ListSeparator; // Excel expects the regional separator
        var sb = new StringBuilder();
        var cells = new List<string>();
        foreach (ColumnHeader c in list.Columns) cells.Add(c.Text);
        sb.AppendLine(string.Join(sep, cells));
        foreach (ListViewItem it in rows)
        {
            cells.Clear();
            foreach (ListViewItem.ListViewSubItem s in it.SubItems)
            {
                string t = s.Text;
                // the data comes from arbitrary processes: stop Excel from running a cell as a formula
                if (t.Length > 0 && "=+-@".IndexOf(t[0]) >= 0) t = "'" + t;
                cells.Add("\"" + t.Replace("\"", "\"\"") + "\"");
            }
            sb.AppendLine(string.Join(sep, cells));
        }
        return sb.ToString();
    }

    static PInfo Query(int pid)
    {
        var p = new PInfo();
        p.Pid = pid;
        IntPtr h = OpenProcess(0x1000, false, pid); // PROCESS_QUERY_LIMITED_INFORMATION
        if (h != IntPtr.Zero)
        {
            p.Ok = true;
            var sb = new StringBuilder(1024);
            int n = sb.Capacity;
            if (QueryFullProcessImageName(h, 0, sb, ref n)) p.Name = Path.GetFileName(sb.ToString());
            long c, e, k, u;
            if (GetProcessTimes(h, out c, out e, out k, out u)) p.Start = c;
            int size = 0x10100, ret; // a command line is at most 32767 chars, plus the UNICODE_STRING header
            IntPtr buf = Marshal.AllocHGlobal(size);
            // 0 = ProcessBasicInformation: the 6th pointer-sized field is the parent PID
            if (NtQueryInformationProcess(h, 0, buf, IntPtr.Size * 6, out ret) == 0)
                p.ParentPid = (int)Marshal.ReadIntPtr(buf, IntPtr.Size * 5);
            // 60 = ProcessCommandLineInformation (Windows 8.1+): UNICODE_STRING { ushort length; ...; pointer }
            if (NtQueryInformationProcess(h, 60, buf, size, out ret) == 0)
                p.Cmd = Marshal.PtrToStringUni(Marshal.ReadIntPtr(buf, IntPtr.Size), (ushort)Marshal.ReadInt16(buf) / 2);
            Marshal.FreeHGlobal(buf);
            IntPtr tok;
            if (OpenProcessToken(h, 8, out tok)) // TOKEN_QUERY
            {
                try { using (var id = new WindowsIdentity(tok)) p.User = id.Name; }
                catch { }
                CloseHandle(tok);
            }
            CloseHandle(h);
        }
        if (p.Name == "")
        {
            try { p.Name = Process.GetProcessById(pid).ProcessName; }
            catch { p.Name = "exited"; }
        }
        return p;
    }

    // shortcut: a parent that exited before its child showed a window is only reported as "exited";
    // naming it would need full process tracing (Sysmon / ETW) from boot.
    static string Chain(PInfo p, out PInfo parent)
    {
        parent = null;
        var sb = new StringBuilder();
        PInfo cur = p;
        for (int i = 0; i < 8 && cur.Ok && cur.ParentPid > 0; i++)
        {
            PInfo par = Query(cur.ParentPid);
            // a parent that started after its child means the PID was reused: the real parent is gone
            if (cur.Start != 0 && par.Start > cur.Start)
            {
                par = new PInfo();
                par.Pid = cur.ParentPid;
                par.Name = "exited";
            }
            if (i == 0) parent = par;
            if (sb.Length > 0) sb.Append(" ← ");
            sb.Append(par.Name + " (" + par.Pid + ")");
            cur = par;
        }
        return sb.ToString();
    }

    // Made-up rows for screenshots (--demo): nothing on this machine is read, so no private data leaks into them.
    void LoadDemo()
    {
        string user = @"DESKTOP-7Q2\alex", explorer = @"C:\Windows\Explorer.EXE", cmdTitle = @"C:\Windows\system32\cmd.exe";
        string sched = "svchost.exe (1420)", schedCmd = @"C:\Windows\system32\svchost.exe -k netsvcs -p -s Schedule";
        string schedChain = "svchost.exe (1420) ← services.exe (812) ← wininit.exe (688)";
        string bat = "C:\\Windows\\system32\\cmd.exe /c \"C:\\ProgramData\\Contoso\\Updater\\check.bat\"";
        string ps = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File \"C:\\ProgramData\\Contoso\\Updater\\check.ps1\"";
        string tray = "FabrikamTray.exe (6012)", trayCmd = "\"C:\\Program Files\\Fabrikam\\FabrikamTray.exe\" --background";
        string trayChain = "FabrikamTray.exe (6012) ← explorer.exe (5596) ← exited (4900)";

        AddItem(false, "(already open)", "", "", "explorer.exe (5596)", "Documents", explorer, "exited (4900)", "", user, "exited (4900)", "CabinetWClass");
        AddItem(false, "(already open)", "", "", "notepad.exe (9120)", "notes.txt - Notepad", "\"C:\\Windows\\system32\\notepad.exe\" C:\\Users\\alex\\Documents\\notes.txt", "explorer.exe (5596)", explorer, user, "explorer.exe (5596) ← exited (4900)", "Notepad");
        AddItem(true, "09:14:02.101", "", "", "cmd.exe (11824)", "(process started)", bat, sched, schedCmd, user, schedChain, "");
        AddItem(false, "09:14:02.137", "09:14:02.598", "0.461 s", "cmd.exe (11824)", cmdTitle, bat, sched, schedCmd, user, schedChain, "ConsoleWindowClass");
        AddItem(true, "09:14:02.190", "", "", "powershell.exe (12440)", "(process started)", ps, "cmd.exe (11824)", bat, user, "cmd.exe (11824) ← " + schedChain, "");
        AddItem(true, "09:15:40.498", "", "", "mspaint.exe (7340)", "(process started)", "\"C:\\Windows\\system32\\mspaint.exe\"", "explorer.exe (5596)", explorer, user, "explorer.exe (5596) ← exited (4900)", "");
        AddItem(false, "09:15:40.731", "", "", "mspaint.exe (7340)", "Untitled - Paint", "\"C:\\Windows\\system32\\mspaint.exe\"", "explorer.exe (5596)", explorer, user, "explorer.exe (5596) ← exited (4900)", "MSPaintApp");
        AddItem(true, "09:17:02.316", "", "", "wscript.exe (13008)", "(process started)", "\"C:\\Windows\\System32\\WScript.exe\" \"C:\\Users\\alex\\AppData\\Roaming\\Fabrikam\\sync.vbs\"", tray, trayCmd, user, trayChain, "");
        AddItem(true, "09:17:02.388", "", "", "cmd.exe (13060)", "(process started)", "cmd.exe /c ping -n 1 sync.fabrikam.example >nul", "wscript.exe (13008)", "\"C:\\Windows\\System32\\WScript.exe\" \"C:\\Users\\alex\\AppData\\Roaming\\Fabrikam\\sync.vbs\"", user, "wscript.exe (13008) ← " + trayChain, "");
        AddItem(false, "09:17:02.402", "09:17:02.712", "0.310 s", "cmd.exe (13060)", cmdTitle, "cmd.exe /c ping -n 1 sync.fabrikam.example >nul", "wscript.exe (13008)", "\"C:\\Windows\\System32\\WScript.exe\" \"C:\\Users\\alex\\AppData\\Roaming\\Fabrikam\\sync.vbs\"", user, "wscript.exe (13008) ← " + trayChain, "ConsoleWindowClass");
        AddItem(true, "09:19:02.094", "", "", "cmd.exe (14216)", "(process started)", bat, sched, schedCmd, user, schedChain, "");
        AddItem(false, "09:19:02.131", "09:19:02.577", "0.446 s", "cmd.exe (14216)", cmdTitle, bat, sched, schedCmd, user, schedChain, "ConsoleWindowClass");
        AddItem(true, "09:19:02.186", "", "", "powershell.exe (14380)", "(process started)", ps, "cmd.exe (14216)", bat, user, "cmd.exe (14216) ← " + schedChain, "");
    }

    [STAThread]
    static void Main(string[] args)
    {
        string mode = args.Length > 0 ? args[0] : "";
        bool test = mode == "--selftest", demo = mode == "--demo";
        bool admin = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        if (admin) try { Process.EnterDebugMode(); } catch { } // lets us read processes of other users and services

        Application.EnableVisualStyles();
        var f = new WindowTrace(admin, demo);
        if (demo)
        {
            // --demo [search|menu]: the states the screenshots are taken in (tools/make-screenshots.ps1)
            string shot = args.Length > 1 ? args[1] : "";
            f.TopMost = true;
            f.StartPosition = FormStartPosition.CenterScreen;
            f.Size = new Size(1440, 810);
            if (shot == "search") f.search.Text = "contoso";
            if (shot == "menu")
            {
                var m = new Timer();
                m.Interval = 600; // after the window is up, so the screenshot script finds the form and not the menu
                m.Tick += delegate
                {
                    m.Stop();
                    for (int i = 0; i < 3; i++) f.list.Items[i].Selected = true;
                    f.list.Select();
                    f.menu.AutoClose = false; // stay open even though the script's console has the focus
                    f.menu.Show(f.list, new Point(430, 60));
                };
                m.Start();
            }
        }
        if (test)
        {
            // open a short-lived console: its window must be seen opening and closing, and its process row
            // must carry the command line and name us as the launcher
            var t = new Timer();
            t.Interval = 1000; // give the process trace (started in Load) time to come up
            t.Tick += delegate
            {
                if (t.Interval == 1000)
                {
                    Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 2 127.0.0.1 >nul"));
                    t.Interval = 5000;
                    return;
                }
                bool win = false, proc = false;
                foreach (ListViewItem it in f.list.Items)
                {
                    var row = new List<string>();
                    foreach (ListViewItem.ListViewSubItem s in it.SubItems) row.Add(s.Text);
                    Console.WriteLine(string.Join(" | ", row));
                    if (it.Tag != null && it.SubItems[2].Text != "") win = true;
                    if (it.Name == "proc" && it.SubItems[5].Text.Contains("ping -n 2") && it.SubItems[6].Text.StartsWith("WindowTrace")) proc = true;
                }
                // filters and formats
                int total = f.list.Items.Count;
                f.kind.SelectedIndex = 1;
                bool filt = f.list.Items.Count > 0;
                foreach (ListViewItem it in f.list.Items) if (it.Name == "proc") filt = false;
                bool fmt = Esc("a\"b\\c\n") == "a\\\"b\\\\c\\u000a"
                    && AsJson(f.Rows()).Contains("\"type\": \"window\"")
                    && f.AsText(f.Rows()).Contains("Process (PID): ")
                    && f.AsCsv(f.Rows()).Split('\n').Length == f.list.Items.Count + 2;
                f.kind.SelectedIndex = 2;
                if (f.list.Items.Count == 0) filt = false;
                f.kind.SelectedIndex = 0;
                f.search.Text = "no-row-contains-this-text";
                if (f.list.Items.Count != 0) filt = false;
                f.search.Text = "";
                if (f.list.Items.Count != total) filt = false;
                Console.WriteLine("admin=" + admin + " window=" + win + " process=" + proc + " filter=" + filt + " format=" + fmt);
                // not Environment.Exit: a pending repaint would run after GDI+ has shut down and crash the process
                t.Stop();
                Environment.ExitCode = win && proc && filt && fmt ? 0 : 1;
                f.Close();
            };
            t.Start();
        }
        Application.Run(f);
    }
}
