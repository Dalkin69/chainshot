// SPDX-License-Identifier: MIT
// Per-user settings in %APPDATA%\ChainShot.
//   state.json  current case and tag, SHARED with the PowerShell module (same keys):
//               a case selected in the app is the current case for Invoke-ChainShot and back.
//   gui.json    app-only preferences (recent cases, TSA URL, ...).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace ChainShot
{
    public sealed class SharedState
    {
        public string CasePath = "";
        public string Tag = "";

        public static string Dir
        {
            get
            {
                string over = Environment.GetEnvironmentVariable("CHAINSHOT_CONFIG_DIR"); // tests
                return !string.IsNullOrEmpty(over) ? over : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ChainShot");
            }
        }

        static string FilePath { get { return Path.Combine(Dir, "state.json"); } }

        public static SharedState Load()
        {
            var s = new SharedState();
            try
            {
                if (File.Exists(FilePath))
                {
                    var o = MiniJson.ParseObject(File.ReadAllText(FilePath, Encoding.UTF8));
                    s.CasePath = o.Str("CasePath");
                    s.Tag = o.Str("Tag");
                }
            }
            catch (Exception) { /* unreadable state: start empty */ }
            return s;
        }

        public void Save()
        {
            Directory.CreateDirectory(Dir);
            var o = new JsonObject();
            o["CasePath"] = CasePath ?? "";
            o["Tag"] = Tag ?? "";
            File.WriteAllText(FilePath, MiniJson.Serialize(o, true), Format.Utf8);
        }
    }

    public sealed class GuiSettings
    {
        public List<string> RecentCases = new List<string>();
        public List<string> RecentTags = new List<string>();
        public string TsaUrl = Sealer.DefaultTsa;
        public string Examiner = Environment.UserName;
        public string CasesRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ChainShot");
        public bool CopyAddedFiles = true;
        public bool CloseToTray = true;
        public bool BrowserLink = true;
        public bool TrayTipShown;
        public int Delay;

        static string FilePath { get { return Path.Combine(SharedState.Dir, "gui.json"); } }

        public static GuiSettings Load()
        {
            var g = new GuiSettings();
            try
            {
                if (!File.Exists(FilePath)) return g;
                var o = MiniJson.ParseObject(File.ReadAllText(FilePath, Encoding.UTF8));
                g.RecentCases = StrList(o["recent_cases"]);
                g.RecentTags = StrList(o["recent_tags"]);
                if (o.Str("tsa_url").Length > 0) g.TsaUrl = o.Str("tsa_url");
                if (o.Str("examiner").Length > 0) g.Examiner = o.Str("examiner");
                if (o.Str("cases_root").Length > 0) g.CasesRoot = o.Str("cases_root");
                if (o.Has("copy_added_files")) g.CopyAddedFiles = o.Str("copy_added_files") == "true";
                if (o.Has("close_to_tray")) g.CloseToTray = o.Str("close_to_tray") == "true";
                g.TrayTipShown = o.Str("tray_tip_shown") == "true";
                if (o.Has("browser_link")) g.BrowserLink = o.Str("browser_link") == "true";
                int d;
                if (int.TryParse(o.Str("delay"), out d)) g.Delay = Math.Max(0, Math.Min(60, d));
            }
            catch (Exception) { /* corrupted file: defaults */ }
            return g;
        }

        static List<string> StrList(object v)
        {
            var l = v as List<object>;
            return l == null ? new List<string>() : l.Select(x => Convert.ToString(x)).Where(x => !string.IsNullOrEmpty(x)).ToList();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(SharedState.Dir);
                var o = new JsonObject();
                o["recent_cases"] = RecentCases.Cast<object>().ToList();
                o["recent_tags"] = RecentTags.Cast<object>().ToList();
                o["tsa_url"] = TsaUrl;
                o["examiner"] = Examiner;
                o["cases_root"] = CasesRoot;
                o["copy_added_files"] = CopyAddedFiles;
                o["close_to_tray"] = CloseToTray;
                o["tray_tip_shown"] = TrayTipShown;
                o["browser_link"] = BrowserLink;
                o["delay"] = Delay.ToString();
                File.WriteAllText(FilePath, MiniJson.Serialize(o, true), Format.Utf8);
            }
            catch (Exception) { /* settings are a convenience */ }
        }

        public static void PushRecent(List<string> list, string value, int max)
        {
            if (string.IsNullOrEmpty(value)) return;
            list.RemoveAll(x => string.Equals(x, value, StringComparison.OrdinalIgnoreCase));
            list.Insert(0, value);
            if (list.Count > max) list.RemoveRange(max, list.Count - max);
        }
    }
}
