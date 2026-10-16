// SPDX-License-Identifier: MIT
// ChainShot main window: case management, captures, journal view, verification, seals,
// report; tray icon and global hotkeys (Ctrl+Alt+P / W / R).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ChainShot
{
    sealed class MainForm : Form
    {
        public static readonly int WM_SHOWME = Native.RegisterWindowMessage("ChainShot.App.ShowMe");
        const int HotScreen = 1, HotWindow = 2, HotRegion = 3;

        readonly GuiSettings settings = GuiSettings.Load();
        readonly SharedState state = SharedState.Load();
        CaseFolder current;
        bool quitting, busy, startHidden;
        DateTime? lastFullVerify;

        // header
        readonly Panel header = new Panel { Dock = DockStyle.Top, Height = 74, BackColor = Ui.Navy, Padding = new Padding(16, 10, 16, 8) };
        readonly Label caseTitle = new Label { AutoSize = true, ForeColor = Color.White, Font = new Font("Segoe UI Semibold", 15f), Location = new Point(14, 8) };
        readonly Label caseInfo = new Label { AutoSize = true, ForeColor = Color.FromArgb(0xC9, 0xD1, 0xE3), Font = new Font("Segoe UI", 9f), Location = new Point(16, 44) };
        readonly Label chip = new Label { AutoSize = true, Font = new Font("Segoe UI Semibold", 9.5f), Padding = new Padding(8, 4, 8, 4), Anchor = AnchorStyles.Top | AnchorStyles.Right };

        // capture bar
        readonly ComboBox tagBox = new ComboBox { Width = 110, DropDownStyle = ComboBoxStyle.DropDown };
        readonly TextBox noteBox = new TextBox { Width = 330 };
        readonly NumericUpDown delayBox = new NumericUpDown { Width = 48, Minimum = 0, Maximum = 60 };
        readonly List<Control> caseControls = new List<Control>();

        // journal
        readonly DataGridView grid = new DataGridView();
        readonly Panel emptyPanel = new Panel { Dock = DockStyle.Fill, BackColor = SystemColors.Window };
        readonly FlowLayoutPanel recentPanel = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };

        // status
        readonly ToolStripStatusLabel statusText = new ToolStripStatusLabel { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
        readonly ToolStripStatusLabel hotkeyText = new ToolStripStatusLabel();

        readonly NotifyIcon tray = new NotifyIcon();
        readonly ToolStripMenuItem recentMenu = new ToolStripMenuItem("Recent cases");
        readonly ToolTip tips = new ToolTip();
        HotkeyWindow hotkeys;
        FileSystemWatcher watcher;
        readonly Timer reloadTimer = new Timer { Interval = 400 };
        public readonly List<string> HotkeyFailures = new List<string>();

        public MainForm(bool startHidden)
        {
            this.startHidden = startHidden;
            Text = "ChainShot";
            Icon = Ui.AppIcon;
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = SystemFonts.MessageBoxFont;
            MinimumSize = new Size(820, 480);
            Size = new Size(1180, 720);
            StartPosition = FormStartPosition.CenterScreen;
            AllowDrop = true;
            KeyPreview = true;

            BuildMenu();
            BuildHeader();
            var bar = BuildCaptureBar();
            var actions = BuildActionBar();
            BuildGrid();
            BuildEmptyPanel();
            var status = new StatusStrip();
            status.Items.Add(statusText);
            status.Items.Add(hotkeyText);

            Controls.Add(grid);
            Controls.Add(emptyPanel);
            Controls.Add(actions);
            Controls.Add(bar);
            Controls.Add(header);
            Controls.Add(MainMenuStrip);
            Controls.Add(status);

            BuildTray();
            reloadTimer.Tick += (s, e) => { reloadTimer.Stop(); ReloadJournal(false); };
            DragEnter += (s, e) => { if (current != null && e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy; };
            DragDrop += (s, e) => { var files = e.Data.GetData(DataFormats.FileDrop) as string[]; if (files != null) AddFiles(files); };

            delayBox.Value = settings.Delay;
            tagBox.Text = state.Tag;
            RefreshTagList();
            string start = state.CasePath;
            if (CaseFolder.IsCase(start)) OpenCase(start, false); else ShowCase(null);
        }

        // ------------------------------------------------------------------ layout
        ToolStripMenuItem Item(string text, EventHandler click, Keys keys = Keys.None, string shortcutText = null)
        {
            var i = new ToolStripMenuItem(text, null, click) { ShortcutKeys = keys };
            if (shortcutText != null) i.ShortcutKeyDisplayString = shortcutText;
            return i;
        }

        void BuildMenu()
        {
            var menu = new MenuStrip();
            var file = new ToolStripMenuItem("&File");
            file.DropDownItems.Add(Item("&New case...", (s, e) => NewCase(), Keys.Control | Keys.N));
            file.DropDownItems.Add(Item("&Open case...", (s, e) => BrowseCase(), Keys.Control | Keys.O));
            file.DropDownItems.Add(recentMenu);
            file.DropDownItems.Add(new ToolStripSeparator());
            file.DropDownItems.Add(CaseItem(Item("Open case &folder", (s, e) => OpenFolder())));
            file.DropDownItems.Add(CaseItem(Item("Export journal as &CSV...", (s, e) => ExportCsv())));
            file.DropDownItems.Add(new ToolStripSeparator());
            file.DropDownItems.Add(Item("E&xit", (s, e) => Quit()));
            file.DropDownOpening += (s, e) => RefreshRecentMenu();

            var cap = new ToolStripMenuItem("&Capture");
            cap.DropDownItems.Add(CaseItem(Item("&Screen under the mouse", (s, e) => DoCapture(CaptureMode.Screen, false), Keys.None, "Ctrl+Alt+P")));
            cap.DropDownItems.Add(CaseItem(Item("Active &window", (s, e) => DoCapture(CaptureMode.Window, false), Keys.None, "Ctrl+Alt+W")));
            cap.DropDownItems.Add(CaseItem(Item("&Region...", (s, e) => DoCapture(CaptureMode.Region, false), Keys.None, "Ctrl+Alt+R")));
            cap.DropDownItems.Add(CaseItem(Item("&All screens", (s, e) => DoCapture(CaptureMode.AllScreens, false))));
            cap.DropDownItems.Add(new ToolStripSeparator());
            cap.DropDownItems.Add(CaseItem(Item("Hash and add &files...", (s, e) => PickFiles(), Keys.Control | Keys.F)));
            cap.DropDownItems.Add(CaseItem(Item("Add &note...", (s, e) => AddNote(), Keys.Control | Keys.T)));

            var ev = new ToolStripMenuItem("&Evidence");
            ev.DropDownItems.Add(CaseItem(Item("&Verify everything", (s, e) => VerifyFull(true), Keys.F5)));
            ev.DropDownItems.Add(CaseItem(Item("&Seal the journal", (s, e) => Seal(false))));
            ev.DropDownItems.Add(CaseItem(Item("Seal + RFC 3161 &timestamp", (s, e) => Seal(true))));
            ev.DropDownItems.Add(CaseItem(Item("HTML &report", (s, e) => MakeReport(), Keys.Control | Keys.R)));

            var opt = new ToolStripMenuItem("&Options");
            var copy = new ToolStripMenuItem("&Copy added files into the case folder") { Checked = settings.CopyAddedFiles, CheckOnClick = true };
            copy.CheckedChanged += (s, e) => { settings.CopyAddedFiles = copy.Checked; settings.Save(); };
            var toTray = new ToolStripMenuItem("&Keep running in the tray when closed") { Checked = settings.CloseToTray, CheckOnClick = true };
            toTray.CheckedChanged += (s, e) => { settings.CloseToTray = toTray.Checked; settings.Save(); };
            var startup = new ToolStripMenuItem("Start with &Windows") { Checked = IsStartupEnabled(), CheckOnClick = true };
            startup.CheckedChanged += (s, e) => SetStartup(startup.Checked);
            opt.DropDownItems.Add(copy);
            opt.DropDownItems.Add(toTray);
            opt.DropDownItems.Add(startup);
            opt.DropDownItems.Add(new ToolStripSeparator());
            opt.DropDownItems.Add(Item("Default &examiner...", (s, e) =>
            {
                string v = InputDialog.Ask(this, "Examiner", "Examiner name used for new cases:", settings.Examiner);
                if (!string.IsNullOrWhiteSpace(v)) { settings.Examiner = v.Trim(); settings.Save(); }
            }));
            opt.DropDownItems.Add(Item("&Timestamp authority (TSA)...", (s, e) =>
            {
                string v = InputDialog.Ask(this, "Timestamp authority", "RFC 3161 time-stamp server URL (default " + Sealer.DefaultTsa + "):", settings.TsaUrl);
                if (v != null) { settings.TsaUrl = v.Trim().Length > 0 ? v.Trim() : Sealer.DefaultTsa; settings.Save(); }
            }));
            opt.DropDownItems.Add(Item("Add ChainShot to the &Start menu", (s, e) => CreateStartMenuShortcut(true)));

            var help = new ToolStripMenuItem("&Help");
            help.DropDownItems.Add(Item("&Documentation", (s, e) => OpenUrl(AppInfo.RepoUrl + "#readme")));
            help.DropDownItems.Add(Item("How to &verify a case independently", (s, e) => OpenUrl(AppInfo.RepoUrl + "/blob/main/docs/verification.md")));
            help.DropDownItems.Add(Item("Journal &format", (s, e) => OpenUrl(AppInfo.RepoUrl + "/blob/main/docs/journal-format.md")));
            help.DropDownItems.Add(new ToolStripSeparator());
            help.DropDownItems.Add(Item("&About ChainShot", (s, e) => MessageBox.Show(this,
                "ChainShot " + AppInfo.Version + "\n\nTimestamped, hash-chained screenshots and evidence journal.\n" +
                "MIT License. Built with the help of AI (Claude, Anthropic).\n\n" + AppInfo.RepoUrl,
                "About ChainShot", MessageBoxButtons.OK, MessageBoxIcon.Information)));

            menu.Items.AddRange(new ToolStripItem[] { file, cap, ev, opt, help });
            MainMenuStrip = menu;
        }

        ToolStripMenuItem CaseItem(ToolStripMenuItem item)
        {
            item.Tag = "case";
            return item;
        }

        void BuildHeader()
        {
            chip.Location = new Point(header.Width - 200, 22);
            header.Controls.Add(caseTitle);
            header.Controls.Add(caseInfo);
            header.Controls.Add(chip);
            header.Resize += (s, e) => PlaceChip();
            chip.SizeChanged += (s, e) => PlaceChip();
            chip.Cursor = Cursors.Hand;
            chip.Click += (s, e) => { if (current != null) VerifyFull(true); };
            tips.SetToolTip(chip, "Click to verify every file, the hash chain and the seals");
        }

        void PlaceChip()
        {
            chip.Location = new Point(header.ClientSize.Width - chip.Width - 16, (header.ClientSize.Height - chip.Height) / 2);
        }

        Button Btn(string text, EventHandler click, string tip, bool needsCase = true)
        {
            var b = new Button { Text = text, AutoSize = true, Margin = new Padding(3, 3, 3, 3), Padding = new Padding(6, 2, 6, 2) };
            b.Click += click;
            if (tip != null) tips.SetToolTip(b, tip);
            if (needsCase) caseControls.Add(b);
            return b;
        }

        Control BuildCaptureBar()
        {
            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(10, 8, 10, 4), WrapContents = true };
            bar.Controls.Add(Ui.Label("Tag"));
            bar.Controls.Add(tagBox);
            bar.Controls.Add(Ui.Label("Note for the next capture"));
            bar.Controls.Add(noteBox);
            bar.Controls.Add(Ui.Label("Delay"));
            bar.Controls.Add(delayBox);
            bar.Controls.Add(Ui.Label("s"));
            var screen = Btn("Screen", (s, e) => DoCapture(CaptureMode.Screen, false), "Capture the screen under the mouse (Ctrl+Alt+P anywhere)");
            var window = Btn("Window", (s, e) => DoCapture(CaptureMode.Window, false), "Capture the window you were using before ChainShot (Ctrl+Alt+W captures the active window)");
            var region = Btn("Region", (s, e) => DoCapture(CaptureMode.Region, false), "Draw a rectangle on a frozen copy of the screen (Ctrl+Alt+R)");
            screen.Font = window.Font = region.Font = new Font(Font, FontStyle.Bold);
            screen.Margin = new Padding(14, 3, 3, 3);
            bar.Controls.Add(screen);
            bar.Controls.Add(window);
            bar.Controls.Add(region);
            tips.SetToolTip(tagBox, "Tag of the next captures, e.g. the question or step number (P17). Shared with the PowerShell module.");
            tips.SetToolTip(noteBox, "Written in the banner and the journal of the next capture, then cleared");
            tips.SetToolTip(delayBox, "Seconds to wait before a capture started from this window");
            tagBox.Validated += (s, e) => SaveTag();
            tagBox.SelectedIndexChanged += (s, e) => SaveTag();
            delayBox.ValueChanged += (s, e) => { settings.Delay = (int)delayBox.Value; settings.Save(); };
            caseControls.Add(tagBox);
            caseControls.Add(noteBox);
            return bar;
        }

        Control BuildActionBar()
        {
            var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(10, 4, 10, 6) };
            bar.Controls.Add(Btn("Add files...", (s, e) => PickFiles(), "Hash files and add them to the journal (or drop files on this window)"));
            bar.Controls.Add(Btn("Add note...", (s, e) => AddNote(), "Record an action or an observation in the journal"));
            var verify = Btn("Verify", (s, e) => VerifyFull(true), "Re-hash every file and check the chain and the seals");
            verify.Margin = new Padding(18, 3, 3, 3);
            bar.Controls.Add(verify);
            bar.Controls.Add(Btn("Seal", (s, e) => Seal(false), "Freeze the current journal in a seal file whose SHA-256 you can write in your report"));
            bar.Controls.Add(Btn("Seal + timestamp", (s, e) => Seal(true), "Seal and get an RFC 3161 timestamp from a trusted time-stamp authority (internet)"));
            bar.Controls.Add(Btn("HTML report", (s, e) => MakeReport(), "Generate report/index.html and open it in your web browser"));
            bar.Controls.Add(Btn("Open folder", (s, e) => OpenFolder(), "Open the case folder in Explorer"));
            return bar;
        }

        void BuildGrid()
        {
            grid.Dock = DockStyle.Fill;
            grid.ReadOnly = true;
            grid.AllowUserToAddRows = grid.AllowUserToDeleteRows = grid.AllowUserToResizeRows = false;
            grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            grid.RowHeadersVisible = false;
            grid.BackgroundColor = SystemColors.Window;
            grid.BorderStyle = BorderStyle.None;
            grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None;
            grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            grid.AllowDrop = true;
            grid.DragEnter += (s, e) => GridDragEnter(e);
            grid.DragDrop += (s, e) => GridDragDrop(e);
            AddCol("seq", "#", 46, DataGridViewContentAlignment.MiddleRight);
            AddCol("utc", "UTC", 170);
            AddCol("tag", "Tag", 70);
            AddCol("type", "Type", 80);
            AddCol("evidence", "Evidence", 330).AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            AddCol("sha", "SHA-256", 150).DefaultCellStyle.Font = new Font("Consolas", 8.5f);
            AddCol("status", "Status", 200);
            grid.CellDoubleClick += (s, e) => { if (e.RowIndex >= 0) OpenEntry(EntryAt(e.RowIndex)); };
            grid.CellFormatting += FormatCell;

            var menu = new ContextMenuStrip();
            menu.Items.Add("Open", null, (s, e) => OpenEntry(SelectedEntry()));
            menu.Items.Add("Show in folder", null, (s, e) => ShowInFolder(SelectedEntry()));
            menu.Items.Add("Copy SHA-256", null, (s, e) => { var x = SelectedEntry(); if (x != null && x.Str("sha256").Length > 0) Clipboard.SetText(x.Str("sha256")); });
            menu.Items.Add("Entry details", null, (s, e) => ShowDetails(SelectedEntry()));
            grid.ContextMenuStrip = menu;
            grid.CellMouseDown += (s, e) => { if (e.Button == MouseButtons.Right && e.RowIndex >= 0) { grid.ClearSelection(); grid.Rows[e.RowIndex].Selected = true; } };
        }

        DataGridViewColumn AddCol(string name, string header, int width, DataGridViewContentAlignment align = DataGridViewContentAlignment.MiddleLeft)
        {
            var c = new DataGridViewTextBoxColumn { Name = name, HeaderText = header, Width = width, SortMode = DataGridViewColumnSortMode.NotSortable };
            c.DefaultCellStyle.Alignment = align;
            grid.Columns.Add(c);
            return c;
        }

        void BuildEmptyPanel()
        {
            var box = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Anchor = AnchorStyles.None };
            box.Controls.Add(new Label { Text = "No case open", AutoSize = true, Font = new Font("Segoe UI Semibold", 16f), ForeColor = Ui.Navy, Margin = new Padding(3, 3, 3, 4) });
            box.Controls.Add(new Label
            {
                Text = "A case is a folder holding your captures, the files you hash and the journal that chains them.",
                AutoSize = true, ForeColor = Ui.Muted, Margin = new Padding(3, 0, 3, 14),
            });
            var buttons = new FlowLayoutPanel { AutoSize = true, Margin = Padding.Empty };
            var create = new Button { Text = "New case...", AutoSize = true, Font = new Font(Font, FontStyle.Bold), Padding = new Padding(10, 4, 10, 4) };
            create.Click += (s, e) => NewCase();
            var open = new Button { Text = "Open case...", AutoSize = true, Padding = new Padding(10, 4, 10, 4) };
            open.Click += (s, e) => BrowseCase();
            buttons.Controls.Add(create);
            buttons.Controls.Add(open);
            box.Controls.Add(buttons);
            box.Controls.Add(recentPanel);
            var center = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 1 };
            center.Controls.Add(box, 0, 0);
            emptyPanel.Controls.Add(center);
        }

        void BuildTray()
        {
            tray.Icon = Ui.AppIcon;
            tray.Text = "ChainShot";
            var m = new ContextMenuStrip();
            m.Items.Add("Open ChainShot", null, (s, e) => ShowFromTray()).Font = new Font(m.Font, FontStyle.Bold);
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(new ToolStripMenuItem("Capture screen", null, (s, e) => DoCapture(CaptureMode.Screen, true)) { ShortcutKeyDisplayString = "Ctrl+Alt+P" });
            m.Items.Add(new ToolStripMenuItem("Capture window", null, (s, e) => DoCapture(CaptureMode.Window, true)) { ShortcutKeyDisplayString = "Ctrl+Alt+W" });
            m.Items.Add(new ToolStripMenuItem("Capture region", null, (s, e) => DoCapture(CaptureMode.Region, true)) { ShortcutKeyDisplayString = "Ctrl+Alt+R" });
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("Set tag...", null, (s, e) =>
            {
                string t = InputDialog.Ask(null, "ChainShot", "Tag for the next captures (e.g. P17):", tagBox.Text);
                if (t != null) { tagBox.Text = t.Trim(); SaveTag(); }
            });
            m.Items.Add("Add note...", null, (s, e) => AddNote());
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("Verify", null, (s, e) => VerifyFull(true));
            m.Items.Add("HTML report", null, (s, e) => MakeReport());
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("Exit", null, (s, e) => Quit());
            tray.ContextMenuStrip = m;
            tray.DoubleClick += (s, e) => ShowFromTray();
            tray.BalloonTipClicked += (s, e) => ShowFromTray();
            tray.Visible = true;
        }

        // --------------------------------------------------------------- lifecycle
        protected override void SetVisibleCore(bool value)
        {
            if (startHidden && !IsHandleCreated) { CreateHandle(); value = false; }
            base.SetVisibleCore(value);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (hotkeys != null) return;
            hotkeys = new HotkeyWindow();
            hotkeys.Pressed += id => DoCapture(id == HotWindow ? CaptureMode.Window : id == HotRegion ? CaptureMode.Region : CaptureMode.Screen, true);
            if (!hotkeys.Register(HotScreen, Keys.P)) HotkeyFailures.Add("Ctrl+Alt+P");
            if (!hotkeys.Register(HotWindow, Keys.W)) HotkeyFailures.Add("Ctrl+Alt+W");
            if (!hotkeys.Register(HotRegion, Keys.R)) HotkeyFailures.Add("Ctrl+Alt+R");
            if (HotkeyFailures.Count == 0)
                hotkeyText.Text = "Hotkeys: Ctrl+Alt+P screen, W window, R region";
            else
            {
                hotkeyText.Text = "Hotkeys unavailable: " + string.Join(", ", HotkeyFailures);
                hotkeyText.ForeColor = Ui.Bad;
                hotkeyText.ToolTipText = "Another program already uses them (the ChainShot PowerShell tray?). Close it and restart ChainShot.";
            }
            if (startHidden) Notify("ChainShot is running in the tray. " + hotkeyText.Text, ToolTipIcon.Info);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_SHOWME) ShowFromTray();
            base.WndProc(ref m);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            SaveTag();
            if (!quitting && settings.CloseToTray && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                if (!settings.TrayTipShown)
                {
                    Notify("ChainShot keeps running here: hotkeys stay active. Right-click the icon to exit. " +
                           "To keep the icon visible: Settings > Personalization > Taskbar > Other system tray icons.", ToolTipIcon.Info);
                    settings.TrayTipShown = true;
                    settings.Save();
                }
                return;
            }
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            if (hotkeys != null) hotkeys.Dispose();
            if (watcher != null) watcher.Dispose();
            tray.Visible = false;
            tray.Dispose();
            base.OnFormClosed(e);
        }

        public void Quit()
        {
            quitting = true;
            Close();
        }

        void ShowFromTray()
        {
            startHidden = false;
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
        }

        void Notify(string text, ToolTipIcon icon)
        {
            tray.BalloonTipTitle = current != null ? "ChainShot - " + current.Name : "ChainShot";
            tray.BalloonTipText = text;
            tray.BalloonTipIcon = icon;
            tray.ShowBalloonTip(4000);
        }

        void SetStatus(string text, bool error = false)
        {
            statusText.Text = text;
            statusText.ForeColor = error ? Ui.Bad : SystemColors.ControlText;
        }

        void Inform(string text, bool error = false)
        {
            SetStatus(text, error);
            if (!Visible) Notify(text, error ? ToolTipIcon.Error : ToolTipIcon.Info);
        }

        void SetBusy(bool value, string message = null)
        {
            busy = value;
            UseWaitCursor = value;
            foreach (var c in caseControls) c.Enabled = !value && current != null;
            if (message != null) SetStatus(message);
        }

        // ------------------------------------------------------------------- cases
        void NewCase()
        {
            using (var d = new NewCaseDialog(settings.Examiner, settings.CasesRoot))
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    var folder = CaseFolder.Create(d.FolderPath, d.CaseName, d.Examiner);
                    settings.Examiner = d.Examiner;
                    settings.CasesRoot = d.CasesRoot;
                    settings.Save();
                    OpenCase(folder.Root, true);
                    SetStatus("Case created: " + folder.Root);
                }
                catch (Exception ex) { Ui.Error(this, "Could not create the case:\n" + ex.Message); }
            }
        }

        void BrowseCase()
        {
            using (var d = new FolderBrowserDialog { Description = "Select a ChainShot case folder (it contains chainshot-case.json)", ShowNewFolderButton = false })
            {
                if (current != null) d.SelectedPath = Path.GetDirectoryName(current.Root);
                if (d.ShowDialog(this) != DialogResult.OK) return;
                if (!CaseFolder.IsCase(d.SelectedPath)) { Ui.Error(this, "This folder is not a ChainShot case (no chainshot-case.json)."); return; }
                OpenCase(d.SelectedPath, true);
            }
        }

        public void OpenCase(string path, bool remember)
        {
            CaseFolder folder;
            try { folder = CaseFolder.Open(path); }
            catch (Exception ex) { Ui.Error(this, ex.Message); return; }
            current = folder;
            state.CasePath = folder.Root;
            SaveState();
            if (remember || !settings.RecentCases.Contains(folder.Root))
            {
                GuiSettings.PushRecent(settings.RecentCases, folder.Root, 10);
                settings.Save();
            }
            ShowCase(folder);
            WatchJournal();
            ReloadJournal(true);
            VerifyFull(false);
        }

        void ShowCase(CaseFolder folder)
        {
            bool has = folder != null;
            grid.Visible = has;
            emptyPanel.Visible = !has;
            foreach (var c in caseControls) c.Enabled = has;
            foreach (ToolStripItem top in MainMenuStrip.Items)
                foreach (var sub in ((ToolStripMenuItem)top).DropDownItems.OfType<ToolStripMenuItem>())
                    if ((sub.Tag as string) == "case") sub.Enabled = has;
            if (!has)
            {
                caseTitle.Text = "ChainShot";
                caseInfo.Text = "Create or open a case to start capturing evidence.";
                SetChip("No case", Ui.Muted);
                RefreshRecentPanel();
                Text = "ChainShot";
                tray.Text = "ChainShot - no case open";
                return;
            }
            caseTitle.Text = folder.Name;
            Text = folder.Name + " - ChainShot";
            string t = "ChainShot - " + folder.Name;
            tray.Text = t.Length > 63 ? t.Substring(0, 63) : t;
        }

        void UpdateInfo(int entries)
        {
            if (current == null) return;
            caseInfo.Text = string.Format("{0}   |   Examiner: {1}   |   {2} entries   |   Created {3}",
                current.Root, current.Examiner, entries, current.Meta.Str("created_utc"));
        }

        void SetChip(string text, Color color)
        {
            chip.Text = text;
            chip.BackColor = color;
            chip.ForeColor = Color.White;
        }

        void RefreshRecentMenu()
        {
            recentMenu.DropDownItems.Clear();
            foreach (string p in settings.RecentCases.Where(CaseFolder.IsCase))
            {
                string path = p;
                recentMenu.DropDownItems.Add(path, null, (s, e) => OpenCase(path, true));
            }
            recentMenu.Enabled = recentMenu.DropDownItems.Count > 0;
        }

        void RefreshRecentPanel()
        {
            recentPanel.Controls.Clear();
            var recent = settings.RecentCases.Where(CaseFolder.IsCase).Take(6).ToList();
            if (recent.Count == 0) return;
            recentPanel.Controls.Add(new Label { Text = "Recent cases", AutoSize = true, ForeColor = Ui.Muted, Margin = new Padding(3, 18, 3, 4) });
            foreach (string p in recent)
            {
                string path = p;
                var link = new LinkLabel { Text = path, AutoSize = true, Margin = new Padding(3, 2, 3, 2) };
                link.LinkClicked += (s, e) => OpenCase(path, true);
                recentPanel.Controls.Add(link);
            }
        }

        void RefreshTagList()
        {
            string text = tagBox.Text;
            tagBox.Items.Clear();
            foreach (string t in settings.RecentTags) tagBox.Items.Add(t);
            tagBox.Text = text;
        }

        void SaveTag()
        {
            string t = tagBox.Text.Trim();
            var fresh = SharedState.Load(); // the PowerShell module may have changed the case
            if (fresh.Tag == t && state.Tag == t) return;
            state.Tag = t;
            SaveState();
        }

        void SaveState()
        {
            try
            {
                var fresh = SharedState.Load();
                fresh.CasePath = state.CasePath;
                fresh.Tag = state.Tag;
                fresh.Save();
            }
            catch (Exception ex) { SetStatus("Could not save settings: " + ex.Message, true); }
        }

        // ----------------------------------------------------------------- journal
        void WatchJournal()
        {
            if (watcher != null) watcher.Dispose();
            watcher = new FileSystemWatcher(current.Root, Format.JournalName) { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size, SynchronizingObject = this };
            watcher.Changed += (s, e) => { reloadTimer.Stop(); reloadTimer.Start(); };
            watcher.EnableRaisingEvents = true;
        }

        JsonObject EntryAt(int row)
        {
            return row >= 0 && row < grid.Rows.Count ? grid.Rows[row].Tag as JsonObject : null;
        }

        JsonObject SelectedEntry()
        {
            return grid.SelectedRows.Count > 0 ? grid.SelectedRows[0].Tag as JsonObject : null;
        }

        /// <summary>Reloads the journal and checks the chain and the seals (files are re-hashed by VerifyFull).</summary>
        void ReloadJournal(bool scrollToEnd)
        {
            if (current == null) return;
            VerifyResult quick;
            try { quick = Verifier.Verify(current, false); }
            catch (Exception ex) { SetStatus(ex.Message, true); return; }
            bool atEnd = grid.Rows.Count == 0 || grid.FirstDisplayedScrollingRowIndex + grid.DisplayedRowCount(false) >= grid.Rows.Count - 1;
            grid.SuspendLayout();
            grid.Rows.Clear();
            foreach (var c in quick.Entries)
            {
                var e = c.Entry;
                string evidence = e.Str("file");
                string extra = string.Join(" | ", new[] { e.Str("title"), e.Str("url"), e.Str("note") }.Where(x => x.Length > 0));
                if (extra.Length > 0) evidence = evidence.Length > 0 ? evidence + "  -  " + extra : extra;
                string sha = e.Str("sha256");
                int i = grid.Rows.Add(e.Str("seq"), e.Str("utc").Replace("T", " "), e.Str("tag"), e.Str("type"), evidence,
                                      sha.Length > 16 ? sha.Substring(0, 16) + "..." : sha, c.Ok ? "chain OK" : string.Join(" / ", c.Problems));
                var row = grid.Rows[i];
                row.Tag = e;
                row.Cells["sha"].ToolTipText = sha;
                row.Cells["evidence"].ToolTipText = evidence;
            }
            grid.ResumeLayout();
            if ((scrollToEnd || atEnd) && grid.Rows.Count > 0) { try { grid.FirstDisplayedScrollingRowIndex = grid.Rows.Count - 1; } catch (InvalidOperationException) { } }
            UpdateInfo(quick.Entries.Count);
            if (!quick.Ok) SetChip("ALTERED - " + quick.ProblemCount + " problem(s)", Ui.Bad);
            else if (lastFullVerify.HasValue) SetChip("Chain OK - files verified " + lastFullVerify.Value.ToString("HH:mm"), Ui.Ok);
            else SetChip("Chain OK - files not verified yet", Ui.Gold);
        }

        void FormatCell(object sender, DataGridViewCellFormattingEventArgs e)
        {
            if (e.RowIndex < 0 || grid.Columns[e.ColumnIndex].Name != "status") return;
            string v = Convert.ToString(e.Value);
            e.CellStyle.ForeColor = v == "OK" || v == "chain OK" ? Ui.Ok : Ui.Bad;
            e.CellStyle.SelectionForeColor = e.CellStyle.ForeColor == Ui.Bad ? Color.FromArgb(0xFF, 0xB4, 0xAB) : SystemColors.HighlightText;
        }

        async void VerifyFull(bool showResult)
        {
            if (current == null || busy) return;
            var folder = current;
            SetBusy(true, "Verifying every file, the hash chain and the seals...");
            VerifyResult res = null;
            try { res = await Task.Run(() => Verifier.Verify(folder, true)); }
            catch (Exception ex) { Inform("Verification error: " + ex.Message, true); }
            finally { SetBusy(false); }
            if (res == null || folder != current) return;
            ReloadJournal(false);
            foreach (DataGridViewRow row in grid.Rows)
            {
                int idx = row.Index;
                if (idx < res.Entries.Count) row.Cells["status"].Value = res.Entries[idx].Ok ? "OK" : string.Join(" / ", res.Entries[idx].Problems);
            }
            if (res.Ok)
            {
                lastFullVerify = DateTime.Now;
                SetChip("All evidence intact - verified " + lastFullVerify.Value.ToString("HH:mm"), Ui.Ok);
                Inform(string.Format("Verified: {0} entries, {1} seal(s), all files match their SHA-256.", res.Entries.Count, res.Seals.Count));
            }
            else
            {
                lastFullVerify = null;
                SetChip("VERIFICATION FAILED - " + res.ProblemCount + " problem(s)", Ui.Bad);
                var lines = res.Errors.Concat(res.Entries.Where(x => !x.Ok).Select(x => "#" + x.Seq + ": " + string.Join(" / ", x.Problems)))
                                      .Concat(res.Seals.Where(x => !x.Ok).Select(x => x.File + ": " + string.Join(" / ", x.Problems))).ToList();
                Inform("VERIFICATION FAILED: " + lines.FirstOrDefault(), true);
                if (showResult)
                    MessageBox.Show(this, "The case does not verify:\n\n" + string.Join("\n", lines.Take(15)) + (lines.Count > 15 ? "\n..." : ""),
                                    "ChainShot - verification failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        // ------------------------------------------------------------------ capture
        async void DoCapture(CaptureMode mode, bool fromHotkey)
        {
            if (busy) return;
            if (current == null)
            {
                if (fromHotkey && !Visible) Notify("No case open: open ChainShot and create or open a case.", ToolTipIcon.Warning);
                else Ui.Error(this, "Create or open a case first.");
                return;
            }
            string tag = tagBox.Text.Trim();
            string note = noteBox.Text.Trim();
            SaveTag();
            int delay = fromHotkey ? 0 : (int)delayBox.Value;
            bool restore = !fromHotkey && Visible && WindowState != FormWindowState.Minimized;
            SetBusy(true);
            JsonObject entry = null;
            try
            {
                if (restore) Hide(); // never capture ChainShot itself when a button was clicked
                if (delay > 0 && mode != CaptureMode.Region)
                    Notify(string.Format("{0} capture in {1} s{2}", mode, delay, mode == CaptureMode.Window ? ": click the window to capture" : ""), ToolTipIcon.Info);
                await Task.Delay(fromHotkey ? 150 : Math.Max(400, delay * 1000));
                IntPtr target = IntPtr.Zero;
                if (mode == CaptureMode.Window)
                {
                    target = Native.GetForegroundWindow();
                    if (target == Handle || target == IntPtr.Zero || !Native.IsWindowVisible(target))
                        throw new InvalidOperationException("No other window is active. Set a delay and click the window to capture, or use Ctrl+Alt+W.");
                }
                entry = ScreenCapture.Capture(current, mode, tag, note, null, target);
            }
            catch (Exception ex) { Inform("Capture failed: " + ex.Message, true); }
            finally
            {
                if (restore) { Show(); Activate(); }
                SetBusy(false);
            }
            if (entry == null) { if (mode == CaptureMode.Region) SetStatus("Region capture cancelled."); return; }
            noteBox.Clear();
            if (tag.Length > 0) { GuiSettings.PushRecent(settings.RecentTags, tag, 15); settings.Save(); RefreshTagList(); }
            ReloadJournal(true);
            Inform(string.Format("#{0} {1} saved - SHA-256 {2}...", entry.Str("seq"), entry.Str("file"), entry.Str("sha256").Substring(0, 16)));
        }

        // ------------------------------------------------------------ files, notes
        void GridDragEnter(DragEventArgs e)
        {
            if (current != null && e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy;
        }

        void GridDragDrop(DragEventArgs e)
        {
            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files != null) AddFiles(files);
        }

        void PickFiles()
        {
            if (current == null) return;
            using (var d = new OpenFileDialog { Multiselect = true, Title = "Files to hash and add to the journal" })
                if (d.ShowDialog(this) == DialogResult.OK) AddFiles(d.FileNames);
        }

        async void AddFiles(string[] paths)
        {
            if (current == null || busy) return;
            var files = new List<string>();
            foreach (string p in paths)
            {
                if (File.Exists(p)) files.Add(p);
                else if (Directory.Exists(p)) files.AddRange(Directory.GetFiles(p, "*", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
            }
            if (files.Count == 0) return;
            string tag = tagBox.Text.Trim();
            string note = noteBox.Text.Trim();
            bool copy = settings.CopyAddedFiles;
            long total = files.Sum(f => new FileInfo(f).Length);
            if (copy && total > 500L * 1024 * 1024)
            {
                var r = MessageBox.Show(this, string.Format("{0} file(s), {1:N0} MB.\n\nCopy them into the case folder?\nYes = copy and hash the copy, No = hash them where they are.",
                                        files.Count, total / (1024 * 1024)), "ChainShot", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                if (r == DialogResult.Cancel) return;
                copy = r == DialogResult.Yes;
            }
            var folder = current;
            SetBusy(true, "Hashing " + files.Count + " file(s)...");
            var errors = new List<string>();
            int added = 0;
            try
            {
                await Task.Run(() =>
                {
                    string root = folder.Root.TrimEnd('\\') + "\\";
                    foreach (string f in files)
                    {
                        try
                        {
                            bool inside = Path.GetFullPath(f).StartsWith(root, StringComparison.OrdinalIgnoreCase);
                            folder.AddFile(f, tag, note, copy && !inside);
                            added++;
                        }
                        catch (Exception ex) { errors.Add(Path.GetFileName(f) + ": " + ex.Message); }
                    }
                });
            }
            finally { SetBusy(false); }
            if (added > 0) noteBox.Clear();
            ReloadJournal(true);
            Inform(string.Format("{0} file(s) hashed and added{1}.", added, errors.Count > 0 ? ", " + errors.Count + " failed" : ""), errors.Count > 0);
            if (errors.Count > 0) Ui.Error(this, string.Join("\n", errors.Take(15)));
        }

        void AddNote()
        {
            if (current == null) { Ui.Error(this, "Create or open a case first."); return; }
            string text = InputDialog.Ask(Visible ? this : null, "Add note", "Action or observation to record in the journal (tag " +
                                          (tagBox.Text.Trim().Length > 0 ? tagBox.Text.Trim() : "none") + "):", noteBox.Text, true);
            if (string.IsNullOrWhiteSpace(text)) return;
            try
            {
                var e = current.AddNote(text.Trim(), tagBox.Text.Trim());
                if (noteBox.Text.Trim() == text.Trim()) noteBox.Clear();
                ReloadJournal(true);
                Inform("#" + e.Str("seq") + " note recorded.");
            }
            catch (Exception ex) { Inform("Note failed: " + ex.Message, true); }
        }

        // ------------------------------------------------------- seal and report
        async void Seal(bool timestamp)
        {
            if (current == null || busy) return;
            var folder = current;
            string tsa = timestamp ? settings.TsaUrl : null;
            SetBusy(true, timestamp ? "Sealing and requesting a timestamp from " + tsa + "..." : "Sealing...");
            SealResult r = null;
            try { r = await Task.Run(() => Sealer.Seal(folder, "SEAL", tsa)); }
            catch (Exception ex) { Ui.Error(this, "Seal refused: " + ex.Message + "\n\nRun Verify to see what changed."); }
            finally { SetBusy(false); }
            if (r == null) return;
            ReloadJournal(true);
            try { Clipboard.SetText(r.Sha256); } catch (Exception) { }
            string msg = string.Format("Seal of entries 1-{0} written:\n{1}\n\nSHA-256 (copied to the clipboard):\n{2}\n\n", r.Entries, r.SealPath, r.Sha256);
            if (timestamp)
                msg += r.TimestampError == null ? "RFC 3161 timestamp: " + r.TimestampTime + " (" + tsa + ")\n\n"
                                                : "The timestamp could not be obtained: " + r.TimestampError + "\nThe local seal is valid; try again later.\n\n";
            msg += "Write this SHA-256 in your signed report or send it to your client separately: it proves the journal was not changed afterwards.";
            SetStatus("Sealed " + r.Entries + " entries.", r.TimestampError != null);
            MessageBox.Show(this, msg, "ChainShot - seal", MessageBoxButtons.OK, r.TimestampError == null ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }

        async void MakeReport()
        {
            if (current == null || busy) return;
            var folder = current;
            SetBusy(true, "Verifying and writing the HTML report...");
            string path = null;
            bool ok = false;
            try
            {
                path = await Task.Run(() =>
                {
                    var res = Verifier.Verify(folder, true);
                    ok = res.Ok;
                    return ChainShot.Report.Write(folder, res, null);
                });
            }
            catch (Exception ex) { Inform("Report failed: " + ex.Message, true); }
            finally { SetBusy(false); }
            if (path == null) return;
            Inform("Report written: " + path + (ok ? "" : " (VERIFICATION FAILED)"), !ok);
            OpenInBrowser(path);
        }

        void ExportCsv()
        {
            if (current == null) return;
            using (var d = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = "journal-export.csv", InitialDirectory = current.Root })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    var cols = Format.ChainedFields.Concat(new[] { "local", "chain" }).ToArray();
                    var lines = new List<string> { string.Join(",", cols) };
                    foreach (var e in current.ReadEntries())
                        lines.Add(string.Join(",", cols.Select(c => Csv(e.Str(c)))));
                    File.WriteAllText(d.FileName, string.Join("\r\n", lines) + "\r\n", new System.Text.UTF8Encoding(true));
                    SetStatus("CSV written (view only, the journal of record is journal.jsonl): " + d.FileName);
                }
                catch (Exception ex) { Ui.Error(this, ex.Message); }
            }
        }

        static string Csv(string v)
        {
            return v.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
        }

        // ------------------------------------------------------------------ shell
        void OpenFolder()
        {
            if (current != null) Start("explorer.exe", "\"" + current.Root + "\"");
        }

        void OpenEntry(JsonObject e)
        {
            if (e == null) return;
            if (e.Str("file").Length == 0) { ShowDetails(e); return; }
            string p = current.Resolve(e.Str("file"));
            if (!File.Exists(p)) { Ui.Error(this, "File missing: " + p); return; }
            if (p.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".tsr", StringComparison.OrdinalIgnoreCase)) { ShowInFolder(e); return; }
            Start(p, null);
        }

        void ShowInFolder(JsonObject e)
        {
            if (e == null || current == null) return;
            string p = e.Str("file").Length > 0 ? current.Resolve(e.Str("file")) : current.JournalPath;
            Start("explorer.exe", "/select,\"" + p + "\"");
        }

        void ShowDetails(JsonObject e)
        {
            if (e == null) return;
            var lines = e.Where(kv => kv.Key != "extra").Select(kv => kv.Key + " = " + Convert.ToString(kv.Value)).ToList();
            var extra = e["extra"] as JsonObject;
            if (extra != null) lines.AddRange(extra.Select(kv => "extra." + kv.Key + " = " + Convert.ToString(kv.Value)));
            MessageBox.Show(this, string.Join("\n", lines), "Entry #" + e.Str("seq"), MessageBoxButtons.OK, MessageBoxIcon.None);
        }

        void Start(string file, string args)
        {
            try { Process.Start(new ProcessStartInfo(file, args ?? "") { UseShellExecute = true }); }
            catch (Exception ex) { Ui.Error(this, ex.Message); }
        }

        void OpenUrl(string url)
        {
            Start(url, null);
        }

        /// <summary>
        /// Opens a local HTML file with the web browser registered for https links: the .html
        /// association may point to an editor (VS Code, Notepad++).
        /// </summary>
        public static void OpenInBrowser(string path)
        {
            string uri = new Uri(Path.GetFullPath(path)).AbsoluteUri;
            try
            {
                string progId = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice", "ProgId", null) as string;
                if (!string.IsNullOrEmpty(progId))
                {
                    string cmd = Registry.GetValue(@"HKEY_CLASSES_ROOT\" + progId + @"\shell\open\command", "", null) as string;
                    if (!string.IsNullOrEmpty(cmd))
                    {
                        cmd = cmd.Trim();
                        string exe = cmd.StartsWith("\"") ? cmd.Substring(1, cmd.IndexOf('"', 1) - 1) : cmd.Split(' ')[0];
                        if (File.Exists(exe)) { Process.Start(exe, "\"" + uri + "\""); return; }
                    }
                }
            }
            catch (Exception) { /* fall back to the file association */ }
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }

        // ----------------------------------------------------------- integration
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        static bool IsStartupEnabled()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue("ChainShot") != null;
        }

        void SetStartup(bool enable)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (enable) k.SetValue("ChainShot", "\"" + Application.ExecutablePath + "\" --minimized");
                    else k.DeleteValue("ChainShot", false);
                }
                SetStatus(enable ? "ChainShot will start in the tray when you sign in." : "ChainShot will not start with Windows.");
            }
            catch (Exception ex) { Ui.Error(this, ex.Message); }
        }

        /// <summary>Start menu shortcut with the ChainShot icon, so Windows search shows the right icon.</summary>
        public static string CreateStartMenuShortcut(bool interactive)
        {
            string lnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "ChainShot.lnk");
            try
            {
                Type t = Type.GetTypeFromProgID("WScript.Shell");
                object shell = Activator.CreateInstance(t);
                object sc = t.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
                Type st = sc.GetType();
                st.InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty, null, sc, new object[] { Application.ExecutablePath });
                st.InvokeMember("WorkingDirectory", System.Reflection.BindingFlags.SetProperty, null, sc, new object[] { Path.GetDirectoryName(Application.ExecutablePath) });
                st.InvokeMember("IconLocation", System.Reflection.BindingFlags.SetProperty, null, sc, new object[] { Application.ExecutablePath + ",0" });
                st.InvokeMember("Description", System.Reflection.BindingFlags.SetProperty, null, sc, new object[] { "ChainShot - timestamped, hash-chained forensic screenshots" });
                st.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, sc, null);
                if (interactive) MessageBox.Show("ChainShot was added to the Start menu:\n" + lnk + "\n\nIf you move ChainShot.exe, add it again.", "ChainShot", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return lnk;
            }
            catch (Exception ex)
            {
                if (interactive) MessageBox.Show("Could not create the shortcut: " + ex.Message, "ChainShot", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return null;
            }
        }

        // -------------------------------------------------------------- smoke test
        /// <summary>Used by CI: shows the window, saves a screenshot of it and returns a summary.</summary>
        public async Task<JsonObject> SmokeTest(string pngPath)
        {
            await Task.Delay(1500);
            Activate();
            await Task.Delay(500);
            Rectangle b = Bounds;
            using (var bmp = new Bitmap(b.Width, b.Height))
            {
                bool grabbed = false;
                try
                {
                    using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(b.Location, Point.Empty, b.Size);
                    grabbed = true;
                }
                catch (Exception) { }
                if (!grabbed) DrawToBitmap(bmp, new Rectangle(Point.Empty, b.Size));
                bmp.Save(pngPath, System.Drawing.Imaging.ImageFormat.Png);
            }
            var o = new JsonObject();
            o["rows"] = grid.Rows.Count.ToString(CultureInfo.InvariantCulture);
            o["case"] = current != null ? current.Root : "";
            o["chip"] = chip.Text;
            o["hotkeys_failed"] = HotkeyFailures.Cast<object>().ToList();
            o["menu_items"] = MainMenuStrip.Items.OfType<ToolStripMenuItem>().Sum(m => m.DropDownItems.Count).ToString(CultureInfo.InvariantCulture);
            o["tray_items"] = tray.ContextMenuStrip.Items.Count.ToString(CultureInfo.InvariantCulture);
            return o;
        }
    }
}
