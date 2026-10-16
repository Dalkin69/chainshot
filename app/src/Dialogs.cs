// SPDX-License-Identifier: MIT
// Small dialogs: new case, text input.

using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace ChainShot
{
    static class Ui
    {
        public static readonly Color Navy = Color.FromArgb(0x1F, 0x2A, 0x44);
        public static readonly Color Gold = Color.FromArgb(0xE8, 0xB9, 0x4A);
        public static readonly Color Ok = Color.FromArgb(0x1F, 0x7A, 0x4D);
        public static readonly Color Bad = Color.FromArgb(0xB3, 0x26, 0x1E);
        public static readonly Color Muted = Color.FromArgb(0x6B, 0x6A, 0x65);

        static Icon icon;

        public static Icon AppIcon
        {
            get
            {
                if (icon == null)
                {
                    try { icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (Exception) { }
                    if (icon == null) icon = SystemIcons.Application;
                }
                return icon;
            }
        }

        public static Label Label(string text, bool bold = false)
        {
            return new Label
            {
                Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 3, 3),
                Font = bold ? new Font(SystemFonts.MessageBoxFont, FontStyle.Bold) : SystemFonts.MessageBoxFont,
            };
        }

        public static void Error(IWin32Window owner, string message)
        {
            MessageBox.Show(owner, message, "ChainShot", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>Creates a case folder: name, examiner and location.</summary>
    sealed class NewCaseDialog : Form
    {
        readonly TextBox caseBox = new TextBox { Width = 380 };
        readonly TextBox examinerBox = new TextBox { Width = 380 };
        readonly TextBox rootBox = new TextBox { Width = 300 };
        readonly Label preview = new Label { AutoSize = true, ForeColor = Ui.Muted, MaximumSize = new Size(470, 0) };
        public string CaseName { get { return caseBox.Text.Trim(); } }
        public string Examiner { get { return examinerBox.Text.Trim(); } }
        public string CasesRoot { get { return rootBox.Text.Trim(); } }
        public string FolderPath { get; private set; }

        public NewCaseDialog(string examiner, string root)
        {
            Text = "New case";
            Icon = Ui.AppIcon;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Font = SystemFonts.MessageBoxFont;
            Padding = new Padding(12);

            examinerBox.Text = examiner;
            rootBox.Text = root;
            var browse = new Button { Text = "Browse...", AutoSize = true };
            browse.Click += (s, e) =>
            {
                using (var d = new FolderBrowserDialog { Description = "Folder where case folders are created", SelectedPath = rootBox.Text })
                    if (d.ShowDialog(this) == DialogResult.OK) rootBox.Text = d.SelectedPath;
            };
            var rootRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
            rootRow.Controls.Add(rootBox);
            rootRow.Controls.Add(browse);

            var grid = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Top };
            grid.Controls.Add(Ui.Label("Case name / reference"), 0, 0);
            grid.Controls.Add(caseBox, 1, 0);
            grid.Controls.Add(Ui.Label("Examiner"), 0, 1);
            grid.Controls.Add(examinerBox, 1, 1);
            grid.Controls.Add(Ui.Label("Create in"), 0, 2);
            grid.Controls.Add(rootRow, 1, 2);
            grid.Controls.Add(preview, 1, 3);

            var ok = new Button { Text = "Create case", AutoSize = true, DialogResult = DialogResult.None };
            var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
            ok.Click += (s, e) => Accept();
            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Bottom, Padding = new Padding(0, 10, 0, 0) };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(ok);
            AcceptButton = ok;
            CancelButton = cancel;

            var outer = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill };
            outer.Controls.Add(grid);
            outer.Controls.Add(buttons);
            Controls.Add(outer);

            caseBox.TextChanged += (s, e) => UpdatePreview();
            rootBox.TextChanged += (s, e) => UpdatePreview();
            UpdatePreview();
        }

        public static string Slug(string name)
        {
            string s = Regex.Replace(name.Trim(), @"[^\w\-]+", "-").Trim('-');
            if (s.Length > 60) s = s.Substring(0, 60).TrimEnd('-');
            return s.Length == 0 ? "case" : s;
        }

        string ComputeFolder()
        {
            if (CasesRoot.Length == 0) return "";
            return Path.Combine(CasesRoot, Slug(CaseName) + "-" + DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
        }

        void UpdatePreview()
        {
            string f = "";
            try { f = ComputeFolder(); } catch (ArgumentException) { }
            preview.Text = f.Length > 0 ? "Folder: " + f : "";
        }

        void Accept()
        {
            if (CaseName.Length == 0) { Ui.Error(this, "Enter a case name or reference."); caseBox.Focus(); return; }
            if (Examiner.Length == 0) { Ui.Error(this, "Enter the examiner name."); examinerBox.Focus(); return; }
            string folder;
            try { folder = ComputeFolder(); }
            catch (ArgumentException) { Ui.Error(this, "Invalid folder."); return; }
            if (folder.Length == 0) { Ui.Error(this, "Choose where to create the case."); return; }
            // Never reuse an existing case folder: add a counter.
            string candidate = folder;
            for (int i = 2; CaseFolder.IsCase(candidate) || (Directory.Exists(candidate) && Directory.EnumerateFileSystemEntries(candidate).Any()); i++)
                candidate = folder + "-" + i;
            FolderPath = candidate;
            DialogResult = DialogResult.OK;
        }
    }

    /// <summary>One-line or multi-line text prompt.</summary>
    sealed class InputDialog : Form
    {
        readonly TextBox box;

        InputDialog(string title, string prompt, string value, bool multiline)
        {
            Text = title;
            Icon = Ui.AppIcon;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Font = SystemFonts.MessageBoxFont;
            Padding = new Padding(12);
            TopMost = true;

            box = new TextBox { Width = 420, Text = value ?? "", Multiline = multiline, AcceptsReturn = multiline, ScrollBars = multiline ? ScrollBars.Vertical : ScrollBars.None };
            if (multiline) box.Height = 110;
            var ok = new Button { Text = "OK", AutoSize = true, DialogResult = DialogResult.OK };
            var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(0, 8, 0, 0) };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(ok);
            if (!multiline) AcceptButton = ok;
            CancelButton = cancel;
            var layout = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill };
            layout.Controls.Add(new Label { Text = prompt, AutoSize = true, MaximumSize = new Size(420, 0), Margin = new Padding(3, 0, 3, 6) });
            layout.Controls.Add(box);
            layout.Controls.Add(buttons);
            Controls.Add(layout);
        }

        /// <summary>Returns the text, or null when cancelled.</summary>
        public static string Ask(IWin32Window owner, string title, string prompt, string value = "", bool multiline = false)
        {
            using (var d = new InputDialog(title, prompt, value, multiline))
            {
                d.Shown += (s, e) => { d.Activate(); d.box.Focus(); d.box.SelectAll(); };
                return d.ShowDialog(owner) == DialogResult.OK ? d.box.Text : null;
            }
        }
    }
}
