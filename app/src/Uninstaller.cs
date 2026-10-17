// SPDX-License-Identifier: MIT
// Removes what ChainShot.exe added to the computer (current user). Case folders are never touched.

using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;

namespace ChainShot
{
    public static class Uninstaller
    {
        /// <summary>Returns a description of each item removed.</summary>
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

            string lnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "ChainShot.lnk");
            if (File.Exists(lnk)) { File.Delete(lnk); done.Add(lnk); }

            if (!keepSettings && Directory.Exists(SharedState.Dir))
            {
                foreach (string f in new[] { "gui.json", "state.json" })
                {
                    string p = Path.Combine(SharedState.Dir, f);
                    if (File.Exists(p)) { File.Delete(p); done.Add(p); }
                }
                try { if (Directory.GetFileSystemEntries(SharedState.Dir).Length == 0) Directory.Delete(SharedState.Dir); }
                catch (IOException) { }
            }
            return done;
        }
    }
}
