// SPDX-License-Identifier: MIT
// Screen capture with the evidence banner, identical to Invoke-ChainShot in the PowerShell
// module: the banner is drawn BELOW the captured pixels, never over them.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace ChainShot
{
    public enum CaptureMode { Screen, AllScreens, Window, Region }

    public static class ScreenCapture
    {
        public static Bitmap Grab(Rectangle bounds)
        {
            var bmp = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
            return bmp;
        }

        public static Bitmap AddBanner(Bitmap shot, IList<string> lines)
        {
            const int lineHeight = 26;
            int height = 12 + lineHeight * lines.Count;
            var output = new Bitmap(shot.Width, shot.Height + height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(output))
            using (var font = new Font("Consolas", 13, FontStyle.Bold, GraphicsUnit.Pixel))
            {
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.DrawImage(shot, 0, 0, shot.Width, shot.Height);
                g.FillRectangle(Brushes.Black, 0, shot.Height, shot.Width, height);
                for (int i = 0; i < lines.Count; i++)
                    g.DrawString(lines[i], font, i == 0 ? Brushes.Yellow : Brushes.Gainsboro, 10, shot.Height + 8 + i * lineHeight);
            }
            return output;
        }

        public static Rectangle Parse(string rect)
        {
            var p = rect.Split(',').Select(x => int.Parse(x.Trim(), CultureInfo.InvariantCulture)).ToArray();
            if (p.Length != 4) throw new FormatException("Rectangle must be x,y,width,height");
            return new Rectangle(p[0], p[1], p[2], p[3]);
        }

        /// <summary>
        /// Captures and journals a screenshot. Returns null when a region selection is cancelled.
        /// For Window mode, <paramref name="window"/> is the target (default: foreground window).
        /// </summary>
        public static JsonObject Capture(CaseFolder folder, CaptureMode mode, string tag, string note,
                                         Rectangle? fixedRect = null, IntPtr window = default(IntPtr))
        {
            Bitmap shot = null;
            Rectangle bounds;
            string title = "";
            var extra = new JsonObject();
            extra["mode"] = mode.ToString();
            if (fixedRect.HasValue)
            {
                bounds = fixedRect.Value;
                extra["mode"] = "Region";
            }
            else if (mode == CaptureMode.AllScreens) bounds = SystemInformation.VirtualScreen;
            else if (mode == CaptureMode.Window)
            {
                IntPtr h = window != IntPtr.Zero ? window : Native.GetForegroundWindow();
                if (h == IntPtr.Zero) throw new InvalidOperationException("No foreground window to capture.");
                bounds = Native.GetWindowBounds(h);
                // Keep only the part that is on screen (maximized windows overhang by a few pixels).
                bounds.Intersect(SystemInformation.VirtualScreen);
                title = Native.GetTitle(h);
                string process = "";
                try { process = Process.GetProcessById(Native.GetProcessId(h)).ProcessName; } catch (Exception) { }
                extra["process"] = process;
            }
            else if (mode == CaptureMode.Region)
            {
                Rectangle abs;
                shot = RegionSelector.Select(out abs);
                if (shot == null) return null;
                bounds = abs;
            }
            else bounds = Screen.FromPoint(Cursor.Position).Bounds;

            if (bounds.Width <= 0 || bounds.Height <= 0) throw new InvalidOperationException("Invalid capture area: " + bounds);
            DateTime utc = DateTime.UtcNow;
            if (shot == null) shot = Grab(bounds);
            extra["bounds"] = string.Format(CultureInfo.InvariantCulture, "{0},{1},{2},{3}", bounds.X, bounds.Y, bounds.Width, bounds.Height);

            // Banner number = journal entry number (entry 1 is the case creation note);
            // the capture number counts screenshots only.
            var journal = folder.ReadEntries();
            int seq = journal.Count + 1;
            int shotNo = journal.Count(e => e.Str("type") == "screenshot" || e.Str("type") == "browser") + 1;
            var lines = new List<string>
            {
                string.Format(CultureInfo.InvariantCulture, "#{0} (capture {7}) | {1} | {2} | {3} UTC ({4}) | {5} @ {6}",
                    seq, folder.Name, tag ?? "", utc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                    utc.ToLocalTime().ToString("HH:mm:ss zzz", CultureInfo.InvariantCulture), folder.Examiner, Environment.MachineName, shotNo),
            };
            if (title.Length > 0) lines.Add(string.Format("Window: {0} [{1}]", title, extra.Str("process")));
            if (!string.IsNullOrEmpty(note)) lines.Add("Note: " + note);

            string safeTag = Regex.Replace(tag ?? "", @"[^\w\-]", "_");
            if (safeTag.Length == 0) safeTag = "untagged";
            string name = string.Format(CultureInfo.InvariantCulture, "{0:D4}_{1}_{2}.png", seq, Format.FileStamp(utc), safeTag);
            string dir = Path.Combine(folder.Root, "captures");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, name);
            using (shot)
            using (var final = AddBanner(shot, lines))
                final.Save(path, ImageFormat.Png);

            var d = Format.DigestFile(path);
            extra["md5"] = d.Md5;
            return folder.Append(new Dictionary<string, string>
            {
                { "type", "screenshot" }, { "tag", tag ?? "" }, { "file", "captures/" + name },
                { "size", d.Size.ToString(CultureInfo.InvariantCulture) }, { "sha256", d.Sha256 }, { "title", title }, { "note", note ?? "" },
            }, extra, utc);
        }
    }

    /// <summary>Freezes the virtual screen and lets the user draw a rectangle (Esc cancels).</summary>
    sealed class RegionSelector : Form
    {
        readonly Bitmap frozen;
        readonly Rectangle virtualScreen;
        Point? start;
        Rectangle selection = Rectangle.Empty;
        bool done;

        RegionSelector(Bitmap frozen, Rectangle vs)
        {
            this.frozen = frozen;
            virtualScreen = vs;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            TopMost = true;
            ShowInTaskbar = false;
            Bounds = vs;
            Cursor = Cursors.Cross;
            KeyPreview = true;
            DoubleBuffered = true;
            Text = "ChainShot region";
        }

        Rectangle Result { get { return done ? selection : Rectangle.Empty; } }

        /// <summary>Returns the cropped bitmap and its virtual-screen rectangle, or null if cancelled.</summary>
        public static Bitmap Select(out Rectangle absolute)
        {
            absolute = Rectangle.Empty;
            Rectangle vs = SystemInformation.VirtualScreen;
            using (Bitmap frozen = ScreenCapture.Grab(vs))
            using (var f = new RegionSelector(frozen, vs))
            {
                f.ShowDialog();
                Rectangle sel = f.Result;
                if (sel.Width < 4 || sel.Height < 4) return null;
                absolute = new Rectangle(sel.X + vs.X, sel.Y + vs.Y, sel.Width, sel.Height);
                return frozen.Clone(sel, frozen.PixelFormat);
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Bounds = virtualScreen; // re-apply after a possible DPI change
            Activate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.DrawImage(frozen, 0, 0, frozen.Width, frozen.Height);
            using (var shade = new SolidBrush(Color.FromArgb(110, 0, 0, 0)))
            using (var region = new Region(new Rectangle(0, 0, frozen.Width, frozen.Height)))
            {
                bool any = selection.Width > 0 && selection.Height > 0;
                if (any) region.Exclude(selection);
                e.Graphics.FillRegion(shade, region);
                if (any)
                {
                    using (var pen = new Pen(Color.Gold, 2)) e.Graphics.DrawRectangle(pen, selection);
                    using (var font = new Font("Segoe UI", 9f))
                        e.Graphics.DrawString(selection.Width + " x " + selection.Height, font, Brushes.Gold, selection.X, Math.Max(0, selection.Y - 18));
                }
                else
                {
                    using (var font = new Font("Segoe UI", 12f, FontStyle.Bold))
                        e.Graphics.DrawString("Drag to select a region - Esc to cancel", font, Brushes.Gold, 24, 24);
                }
            }
        }

        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) start = e.Location; }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (!start.HasValue) return;
            var s = start.Value;
            selection = new Rectangle(Math.Min(s.X, e.X), Math.Min(s.Y, e.Y), Math.Abs(e.X - s.X), Math.Abs(e.Y - s.Y));
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (!start.HasValue) return;
            done = true;
            Close();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) { selection = Rectangle.Empty; Close(); }
        }
    }
}
