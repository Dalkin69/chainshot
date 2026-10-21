// SPDX-License-Identifier: MIT
// Tiny launcher for the PowerShell tray application. Install-ChainShotShortcut compiles it next to
// the module with the C# compiler of the .NET Framework (part of Windows), so the Start menu,
// the Startup folder and Task Manager show "ChainShot" with its icon instead of "powershell.exe".
// It only starts Windows PowerShell with the module of its own folder, then exits.
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;

[assembly: AssemblyTitle("ChainShot")]
[assembly: AssemblyProduct("ChainShot")]
[assembly: AssemblyDescription("ChainShot tray (PowerShell module)")]
[assembly: AssemblyCompany("ChainShot contributors")]
[assembly: AssemblyCopyright("MIT license")]
[assembly: AssemblyVersion("1.3.3.0")]
[assembly: AssemblyFileVersion("1.3.3.0")]

static class ChainShotTray
{
    [STAThread]
    static int Main()
    {
        string dir = AppDomain.CurrentDomain.BaseDirectory;
        string manifest = Path.Combine(dir, "ChainShot.psd1");
        string ps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");
        string args = "-NoProfile -STA -WindowStyle Hidden -ExecutionPolicy Bypass -Command \"Import-Module '"
            + manifest.Replace("'", "''") + "'; Start-ChainShotTray\"";
        var psi = new ProcessStartInfo(ps, args) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = dir };
        Process.Start(psi);
        return 0;
    }
}
