// SPDX-License-Identifier: MIT
// Everything ChainShot.exe adds to the computer (current user only), and how it is removed.
// ChainShot has no installer: it is listed in Settings > Apps > Installed apps as soon as it has
// changed something on the computer (browser link, start with Windows, Start menu shortcut), so that
// it can be removed from there like any other program. Case folders are never touched.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ChainShot
{
    public static class Uninstaller
    {
        public const string AppsKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\ChainShot";
        const string PowerShellAppsKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\ChainShot.PowerShell";

        /// <summary>Files of the release zip, deleted with ChainShot.exe when they are alone in its folder.</summary>
        static readonly string[] ZipFiles = { "ChainShot.exe", "ChainShot.exe.config", "README.md", "LICENSE", "PRIVACY.md" };

        static string StartMenuShortcut
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "ChainShot.lnk"); }
        }

        /// <summary>"winget" or "scoop" when a package manager installed this copy (it then owns the
        /// executable and its own uninstall entry), otherwise null.</summary>
        public static string PackageManager(string exePath)
        {
            string p = (exePath ?? "").Replace('/', '\\');
            if (p.IndexOf(@"\Microsoft\WinGet\", StringComparison.OrdinalIgnoreCase) >= 0) return "winget";
            if (p.IndexOf(@"\scoop\apps\", StringComparison.OrdinalIgnoreCase) >= 0) return "scoop";
            return null;
        }

        static bool StartsWithWindows()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(MainForm.RunKey)) return k != null && k.GetValue("ChainShot") != null;
        }

        static bool HasBrowserLink()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Google\Chrome\NativeMessagingHosts\" + BrowserLink.HostName)) if (k != null) return true;
            using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Mozilla\NativeMessagingHosts\" + BrowserLink.HostName)) return k != null;
        }

        /// <summary>True when ChainShot has changed something on this computer besides its settings.</summary>
        public static bool HasSystemChanges()
        {
            return StartsWithWindows() || HasBrowserLink() || File.Exists(StartMenuShortcut);
        }

        /// <summary>Adds, updates or removes the "Installed apps" entry so it matches what ChainShot changed
        /// and where ChainShot.exe now is. Package managers keep their own entry.</summary>
        public static void SyncAppsEntry(string exePath)
        {
            if (PackageManager(exePath) != null || !HasSystemChanges())
            {
                Registry.CurrentUser.DeleteSubKeyTree(AppsKey, false);
                return;
            }
            using (var k = Registry.CurrentUser.CreateSubKey(AppsKey))
            {
                string q = "\"" + exePath + "\"";
                k.SetValue("DisplayName", AppInfo.Name);
                k.SetValue("DisplayVersion", AppInfo.Version);
                k.SetValue("Publisher", "ChainShot contributors");
                k.SetValue("DisplayIcon", exePath + ",0");
                k.SetValue("InstallLocation", Path.GetDirectoryName(exePath));
                k.SetValue("URLInfoAbout", "https://dalkin69.github.io/chainshot/");
                k.SetValue("HelpLink", AppInfo.RepoUrl + "/issues");
                k.SetValue("Comments", "Timestamped, hash-chained forensic screenshots. Uninstalling never touches case folders.");
                k.SetValue("UninstallString", q + " uninstall --gui");
                k.SetValue("QuietUninstallString", q + " uninstall");
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                if (k.GetValue("InstallDate") == null) k.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
                try { k.SetValue("EstimatedSize", (int)Math.Max(1, new FileInfo(exePath).Length / 1024), RegistryValueKind.DWord); }
                catch (IOException) { }
            }
        }

        /// <summary>What <see cref="Run"/> would remove right now, for the confirmation dialog.</summary>
        public static List<string> Preview(bool keepSettings)
        {
            var items = new List<string>();
            if (StartsWithWindows()) items.Add("start with Windows");
            if (HasBrowserLink()) items.Add("browser link (registry entries and manifests in %APPDATA%\\ChainShot)");
            if (File.Exists(StartMenuShortcut)) items.Add("Start menu shortcut");
            using (var k = Registry.CurrentUser.OpenSubKey(AppsKey)) if (k != null) items.Add("entry in Settings > Apps > Installed apps");
            if (!keepSettings && SettingsFiles().Any()) items.Add("ChainShot settings (recent cases, current case and tag)");
            return items;
        }

        static IEnumerable<string> SettingsFiles()
        {
            return new[] { "gui.json", "state.json" }.Select(f => Path.Combine(SharedState.Dir, f)).Where(File.Exists);
        }

        /// <summary>Removes everything ChainShot.exe added. Returns a description of each item removed.</summary>
        public static List<string> Run(bool keepSettings)
        {
            var done = new List<string>();
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(MainForm.RunKey, true))
                    if (k != null && k.GetValue("ChainShot") != null) { k.DeleteValue("ChainShot", false); done.Add(@"HKCU\" + MainForm.RunKey + @"\ChainShot (start with Windows)"); }
            }
            catch (Exception ex) { done.Add("start with Windows: " + ex.Message); }

            try { BrowserLink.Unregister(); done.Add("browser link (NativeMessagingHosts registry keys and manifests)"); }
            catch (Exception ex) { done.Add("browser link: " + ex.Message); }

            try { if (File.Exists(StartMenuShortcut)) { File.Delete(StartMenuShortcut); done.Add(StartMenuShortcut); } }
            catch (Exception ex) { done.Add("Start menu shortcut: " + ex.Message); }

            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(AppsKey)) if (k != null) done.Add(@"HKCU\" + AppsKey + " (Installed apps)");
                Registry.CurrentUser.DeleteSubKeyTree(AppsKey, false);
            }
            catch (Exception ex) { done.Add("Installed apps entry: " + ex.Message); }

            if (!keepSettings)
            {
                foreach (string p in SettingsFiles().ToList())
                {
                    try { File.Delete(p); done.Add(p); }
                    catch (Exception ex) { done.Add(p + ": " + ex.Message); }
                }
                try { if (Directory.Exists(SharedState.Dir) && Directory.GetFileSystemEntries(SharedState.Dir).Length == 0) Directory.Delete(SharedState.Dir); }
                catch (IOException) { }
            }
            return done;
        }

        /// <summary>Folder of the PowerShell module when it is installed too (it has its own uninstaller).</summary>
        public static string PowerShellModule()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(PowerShellAppsKey)) if (k != null) return k.GetValue("InstallLocation") as string ?? "installed";
            string d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), @"WindowsPowerShell\Modules\ChainShot");
            return Directory.Exists(d) ? d : null;
        }

        /// <summary>Stops the other ChainShot.exe processes started from the same file (tray, browser host).</summary>
        public static int StopOtherInstances(string exePath)
        {
            int n = 0;
            foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exePath)))
            {
                try
                {
                    if (p.Id == Process.GetCurrentProcess().Id) continue;
                    if (!string.Equals(p.MainModule.FileName, exePath, StringComparison.OrdinalIgnoreCase)) continue;
                    p.Kill(); p.WaitForExit(5000); n++;
                }
                catch (Exception) { /* other user, already gone */ }
                finally { p.Dispose(); }
            }
            return n;
        }

        /// <summary>Deletes ChainShot.exe a few seconds after this process exits, with the other files of the
        /// release zip and the folder itself when nothing else is in it. Returns what will be deleted.</summary>
        public static List<string> ScheduleSelfDelete(string exePath)
        {
            string dir = Path.GetDirectoryName(exePath);
            var entries = Directory.GetFileSystemEntries(dir).Select(Path.GetFileName).ToList();
            bool alone = entries.All(e => ZipFiles.Contains(e, StringComparer.OrdinalIgnoreCase)) && !IsKnownFolder(dir);
            var files = alone ? entries.Select(e => Path.Combine(dir, e)).ToList() : new List<string> { exePath };
            string cmd = "ping -n 4 127.0.0.1 >nul & del /f /q " + string.Join(" ", files.Select(f => "\"" + f + "\""));
            if (alone) cmd += " & rmdir \"" + dir + "\"";
            var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/d /c \"" + cmd + "\"")
            {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.GetTempPath(),
            };
            using (Process.Start(psi)) { }
            if (alone) files.Add(dir);
            return files;
        }

        static bool IsKnownFolder(string dir)
        {
            string d = dir.TrimEnd('\\');
            var known = new[]
            {
                Environment.SpecialFolder.Desktop, Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.UserProfile,
                Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData,
            }.Select(f => Environment.GetFolderPath(f).TrimEnd('\\')).ToList();
            known.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"));
            return known.Any(k => string.Equals(k, d, StringComparison.OrdinalIgnoreCase)) || Path.GetPathRoot(dir).TrimEnd('\\') == d;
        }

        /// <summary>Uninstall with dialogs: from Options > Uninstall, and from Settings > Apps > Installed apps.</summary>
        public static bool Interactive(IWin32Window owner)
        {
            const string title = "Uninstall ChainShot";
            string exe = Application.ExecutablePath;
            string manager = PackageManager(exe);
            var items = Preview(false);
            string list = items.Count == 0 ? "  (nothing besides ChainShot.exe)" : "  - " + string.Join("\n  - ", items);
            var r = MessageBox.Show(owner,
                "This removes from this computer, for the current user:\n\n" + list + "\n\n" +
                "Your case folders and their evidence are NOT touched.\n\nContinue?",
                title, MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (r != DialogResult.OK) return false;

            StopOtherInstances(exe);
            var done = Run(false);
            string text = done.Count == 0 ? "Nothing to remove." : "Removed:\n" + string.Join("\n", done);
            string module = PowerShellModule();
            string moduleNote = module == null ? "" :
                "\n\nThe ChainShot PowerShell module is installed too. It has its own entry in Settings > Apps > Installed apps " +
                "(ChainShot PowerShell module), or run Uninstall-ChainShot.ps1.";

            if (manager != null)
            {
                string cmd = manager == "winget" ? "winget uninstall Dalkin69.ChainShot" : "scoop uninstall chainshot";
                MessageBox.Show(owner, text + "\n\nChainShot was installed with " + manager + ". To remove the program itself, run:\n\n  " + cmd + moduleNote,
                    title, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return true;
            }
            var del = MessageBox.Show(owner, text + moduleNote + "\n\nAlso delete ChainShot.exe now?\n" + exe,
                title, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (del == DialogResult.Yes)
            {
                try { ScheduleSelfDelete(exe); }
                catch (Exception ex) { MessageBox.Show(owner, "Could not delete ChainShot.exe: " + ex.Message, title, MessageBoxButtons.OK, MessageBoxIcon.Error); }
            }
            else
            {
                try { using (Process.Start("explorer.exe", "/select,\"" + exe + "\"")) { } } catch (Exception) { }
            }
            return true;
        }
    }
}
