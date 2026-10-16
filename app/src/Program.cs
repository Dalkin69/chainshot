// SPDX-License-Identifier: MIT
// ChainShot.exe entry point: the graphical application without arguments, a command line
// for scripts otherwise (same commands as chainshot.py, plus `capture`).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace ChainShot
{
    static class Program
    {
        const string Usage = @"ChainShot {0} - timestamped, hash-chained screenshots and evidence journal

ChainShot.exe                         open the application
ChainShot.exe --minimized             start in the notification area (tray)

Command line (the current case is the one selected in the app or with Set-ChainShotCase):
  new-case <folder> --case <name> [--examiner <name>]
  capture  [--mode screen|all|window|region] [--rect x,y,w,h] [--delay <s>] [--tag <t>] [--note <n>] [--case <folder>]
  add      <file>... [--copy] [--tag <t>] [--note <n>] [--case <folder>]
  note     <text> [--tag <t>] [--case <folder>]
  tag      [<tag>]                    show or set the tag of the next entries ("" to clear)
  verify   [<folder>] [--json]
  seal     [<folder>] [--tsa [<url>]] [--tag <t>]
  report   [<folder>] [--out <file.html>] [--open]
  selftest <canonical-vectors.json>
  --version | --help

Exit codes: 0 ok, 1 verification failed, 2 error.
From PowerShell, pipe the output to wait for the result:  .\ChainShot.exe verify | Out-Host";

        [STAThread]
        static int Main(string[] args)
        {
            if (args.Length == 0 || args[0] == "--minimized" || args[0] == "--smoke-test") return RunGui(args);
            AttachOutput();
            try { return RunCli(args); }
            catch (CliError ex) { Console.Error.WriteLine("error: " + ex.Message); return 2; }
            catch (Exception ex) { Console.Error.WriteLine("error: " + ex.Message); return 2; }
            finally { Console.Out.Flush(); }
        }

        // ----------------------------------------------------------------------- GUI
        static int RunGui(string[] args)
        {
            Native.SetDpiAwareness();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            bool smoke = args.Length > 0 && args[0] == "--smoke-test";
            bool created;
            using (var mutex = new Mutex(true, smoke ? "Local\\ChainShot.App.Smoke" : "Local\\ChainShot.App", out created))
            {
                if (!created)
                {
                    // Already running: bring the existing window to the front.
                    Native.PostMessage(Native.HWND_BROADCAST, MainForm.WM_SHOWME, IntPtr.Zero, IntPtr.Zero);
                    return 0;
                }
                var form = new MainForm(!smoke && args.Length > 0 && args[0] == "--minimized");
                if (smoke)
                {
                    // --smoke-test <out.png> [--case <folder>]: CI check of the window, then exit.
                    AttachOutput();
                    var o = new Options(args.Skip(1).ToArray());
                    string png = o.Positional.FirstOrDefault() ?? "chainshot-smoke.png";
                    string casePath = o.Get("case");
                    if (casePath != null) form.OpenCase(casePath, false);
                    int code = 0;
                    form.Shown += async (s, e) =>
                    {
                        try
                        {
                            var result = await form.SmokeTest(Path.GetFullPath(png));
                            Console.Out.WriteLine(MiniJson.Serialize(result));
                        }
                        catch (Exception ex) { Console.Error.WriteLine("smoke test failed: " + ex); code = 1; }
                        finally { Console.Out.Flush(); form.Quit(); }
                    };
                    Application.Run(form);
                    return code;
                }
                Application.Run(form);
                mutex.ReleaseMutex();
            }
            return 0;
        }

        // ----------------------------------------------------------------------- CLI
        sealed class CliError : Exception { public CliError(string m) : base(m) { } }

        /// <summary>Tiny option parser: --name value, --flag, positionals.</summary>
        sealed class Options
        {
            public readonly List<string> Positional = new List<string>();
            readonly Dictionary<string, string> named = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            static readonly HashSet<string> Flags = new HashSet<string> { "copy", "json", "open" };

            public Options(string[] args)
            {
                for (int i = 0; i < args.Length; i++)
                {
                    string a = args[i];
                    if (!a.StartsWith("--")) { Positional.Add(a); continue; }
                    string name = a.Substring(2);
                    if (Flags.Contains(name)) { named[name] = "true"; continue; }
                    bool hasValue = i + 1 < args.Length && !args[i + 1].StartsWith("--");
                    if (name == "tsa") { named[name] = hasValue ? args[++i] : Sealer.DefaultTsa; continue; }
                    if (!hasValue) throw new CliError("--" + name + " needs a value");
                    named[name] = args[++i];
                }
            }

            public string Get(string name)
            {
                string v;
                return named.TryGetValue(name, out v) ? v : null;
            }

            public bool Has(string name) { return named.ContainsKey(name); }
        }

        static CaseFolder ResolveCase(string path)
        {
            if (string.IsNullOrEmpty(path)) path = SharedState.Load().CasePath;
            if (string.IsNullOrEmpty(path)) throw new CliError("no case selected: pass --case <folder> or open a case in ChainShot");
            if (!CaseFolder.IsCase(path)) throw new CliError("not a ChainShot case folder (no " + Format.CaseFileName + "): " + Path.GetFullPath(path));
            return CaseFolder.Open(path);
        }

        static string TagOrCurrent(Options o)
        {
            return o.Get("tag") ?? SharedState.Load().Tag;
        }

        static int RunCli(string[] args)
        {
            string cmd = args[0];
            var o = new Options(args.Skip(1).ToArray());
            var w = Console.Out;
            switch (cmd)
            {
                case "--version":
                case "version":
                    w.WriteLine(Format.ToolId);
                    return 0;
                case "--help":
                case "-h":
                case "/?":
                case "help":
                    w.WriteLine(Usage, AppInfo.Version);
                    return 0;

                case "selftest":
                {
                    if (o.Positional.Count != 1) throw new CliError("usage: selftest <canonical-vectors.json>");
                    int n;
                    var failures = SelfTest.RunVectors(o.Positional[0], out n);
                    foreach (string f in failures) w.WriteLine("FAIL " + f);
                    w.WriteLine(failures.Count == 0 ? "selftest: " + n + " vectors OK" : "selftest: " + failures.Count + " failure(s)");
                    return failures.Count == 0 ? 0 : 1;
                }

                case "new-case":
                case "init":
                {
                    if (o.Positional.Count != 1 || o.Get("case") == null) throw new CliError("usage: new-case <folder> --case <name> [--examiner <name>]");
                    var folder = CaseFolder.Create(o.Positional[0], o.Get("case"), o.Get("examiner") ?? Environment.UserName);
                    var st = SharedState.Load();
                    st.CasePath = folder.Root;
                    st.Save();
                    w.WriteLine("Case created: " + folder.Root + " (now the current case)");
                    return 0;
                }

                case "tag":
                {
                    var st = SharedState.Load();
                    if (o.Positional.Count > 0)
                    {
                        st.Tag = o.Positional[0].Trim();
                        st.Save();
                    }
                    w.WriteLine("Current tag: " + st.Tag);
                    return 0;
                }

                case "capture":
                {
                    var folder = ResolveCase(o.Get("case"));
                    CaptureMode mode;
                    switch ((o.Get("mode") ?? "screen").ToLowerInvariant())
                    {
                        case "screen": mode = CaptureMode.Screen; break;
                        case "all": case "allscreens": mode = CaptureMode.AllScreens; break;
                        case "window": mode = CaptureMode.Window; break;
                        case "region": mode = CaptureMode.Region; break;
                        default: throw new CliError("--mode must be screen, all, window or region");
                    }
                    Native.SetDpiAwareness();
                    int delay = o.Get("delay") != null ? int.Parse(o.Get("delay"), CultureInfo.InvariantCulture) : 0;
                    if (delay > 0) Thread.Sleep(delay * 1000);
                    System.Drawing.Rectangle? rect = null;
                    if (o.Get("rect") != null) rect = ScreenCapture.Parse(o.Get("rect"));
                    var e = ScreenCapture.Capture(folder, mode, TagOrCurrent(o), o.Get("note") ?? "", rect);
                    if (e == null) { w.WriteLine("Region capture cancelled."); return 1; }
                    w.WriteLine("#" + e.Str("seq") + " " + e.Str("file") + "  sha256 " + e.Str("sha256"));
                    return 0;
                }

                case "add":
                {
                    if (o.Positional.Count == 0) throw new CliError("usage: add <file>... [--copy] [--tag <t>] [--note <n>] [--case <folder>]");
                    var folder = ResolveCase(o.Get("case"));
                    foreach (string f in o.Positional)
                    {
                        var e = folder.AddFile(f, TagOrCurrent(o), o.Get("note") ?? "", o.Has("copy"));
                        w.WriteLine("#" + e.Str("seq") + " " + e.Str("file") + "  sha256 " + e.Str("sha256"));
                    }
                    return 0;
                }

                case "note":
                {
                    if (o.Positional.Count == 0) throw new CliError("usage: note <text> [--tag <t>] [--case <folder>]");
                    var folder = ResolveCase(o.Get("case"));
                    var e = folder.AddNote(string.Join(" ", o.Positional), TagOrCurrent(o));
                    w.WriteLine("#" + e.Str("seq") + " note recorded");
                    return 0;
                }

                case "verify":
                {
                    var folder = ResolveCase(o.Positional.FirstOrDefault() ?? o.Get("case"));
                    var res = Verifier.Verify(folder, true);
                    if (o.Has("json")) { w.WriteLine(MiniJson.Serialize(ToJson(folder, res), true)); return res.Ok ? 0 : 1; }
                    foreach (var c in res.Entries)
                    {
                        var e = c.Entry;
                        string what = e.Str("file").Length > 0 ? e.Str("file") : e.Str("note");
                        w.WriteLine("{0,5}  {1}  {2,-10}  {3,-10}  {4}", c.Seq, e.Str("utc"), e.Str("tag"), c.Ok ? "OK" : "PROBLEM", what);
                        foreach (string p in c.Problems) w.WriteLine("       -> " + p);
                    }
                    foreach (var s in res.Seals)
                    {
                        string ts = s.TimestampImprintOk.HasValue ? "  | RFC 3161 time " + s.TimestampTime + ", imprint " + (s.TimestampImprintOk.Value ? "OK" : "MISMATCH") : "";
                        w.WriteLine("Seal " + s.File + ": " + (s.Ok ? "OK" : string.Join(" / ", s.Problems)) + ts);
                    }
                    foreach (string x in res.Warnings) w.WriteLine("warning: " + x);
                    foreach (string x in res.Errors) w.WriteLine("ERROR: " + x);
                    w.WriteLine();
                    w.WriteLine("{0} entries, {1} problem(s), {2} seal(s).", res.Entries.Count, res.ProblemCount, res.Seals.Count);
                    if (File.Exists(folder.JournalPath)) w.WriteLine("SHA-256 of journal.jsonl: " + Format.Sha256Hex(File.ReadAllBytes(folder.JournalPath)));
                    w.WriteLine(res.Ok ? "RESULT: ALL EVIDENCE INTACT" : "RESULT: VERIFICATION FAILED");
                    return res.Ok ? 0 : 1;
                }

                case "seal":
                {
                    var folder = ResolveCase(o.Positional.FirstOrDefault() ?? o.Get("case"));
                    var r = Sealer.Seal(folder, o.Get("tag") ?? "SEAL", o.Get("tsa"));
                    w.WriteLine("Seal of entries 1-" + r.Entries + ": " + r.SealPath);
                    w.WriteLine("Seal SHA-256: " + r.Sha256);
                    if (o.Get("tsa") != null)
                    {
                        if (r.TimestampError != null) { Console.Error.WriteLine("RFC 3161 timestamp failed: " + r.TimestampError + " (the local seal is valid)"); return 1; }
                        w.WriteLine("RFC 3161 timestamp: " + r.TimestampTime + " (TSA " + o.Get("tsa") + ")");
                    }
                    w.WriteLine("Write the seal SHA-256 in your signed report or send it to your client separately.");
                    return 0;
                }

                case "report":
                {
                    var folder = ResolveCase(o.Positional.FirstOrDefault() ?? o.Get("case"));
                    var res = Verifier.Verify(folder, true);
                    string path = ChainShot.Report.Write(folder, res, o.Get("out"));
                    w.WriteLine("Report written: " + path + "  (" + (res.Ok ? "all verified" : "VERIFICATION FAILED") + ")");
                    if (o.Has("open")) MainForm.OpenInBrowser(path);
                    return res.Ok ? 0 : 1;
                }

                default:
                    throw new CliError("unknown command '" + cmd + "' (see --help)");
            }
        }

        static JsonObject ToJson(CaseFolder folder, VerifyResult res)
        {
            var o = new JsonObject();
            o["case"] = folder.Name;
            o["ok"] = res.Ok;
            o["entries"] = res.Entries.Select(c =>
            {
                var e = new JsonObject();
                e["seq"] = c.Seq.ToString(CultureInfo.InvariantCulture);
                e["ok"] = c.Ok;
                e["problems"] = c.Problems.Cast<object>().ToList();
                return (object)e;
            }).ToList();
            o["seals"] = res.Seals.Select(s =>
            {
                var e = new JsonObject();
                e["file"] = s.File;
                e["ok"] = s.Ok;
                e["problems"] = s.Problems.Cast<object>().ToList();
                e["timestamp"] = s.TimestampTime;
                return (object)e;
            }).ToList();
            o["warnings"] = res.Warnings.Cast<object>().ToList();
            o["errors"] = res.Errors.Cast<object>().ToList();
            return o;
        }

        /// <summary>
        /// ChainShot.exe is a Windows (GUI) program: when its output is not redirected, attach to
        /// the console of the parent (cmd, PowerShell) so the command line prints there.
        /// </summary>
        static void AttachOutput()
        {
            bool redirected = Native.HasStdOut();
            if (!redirected && !Native.AttachConsole(-1)) return; // started from Explorer: nowhere to print
            // UTF-8 output, so that accents survive pipes (python, PowerShell) and the console.
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true });
            Console.SetError(new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true });
            if (!redirected) Console.WriteLine(); // the prompt was already printed by the parent shell
        }
    }
}
