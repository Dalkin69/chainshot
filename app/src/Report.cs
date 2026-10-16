// SPDX-License-Identifier: MIT
// HTML report, same layout as `chainshot.py report` and Export-ChainShotReport.
// The report is a convenience view: the journal and the files remain the evidence.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace ChainShot
{
    public static class Report
    {
        const string Css = @"
:root{--bg:#fbfaf7;--fg:#1d1d1b;--muted:#6b6a65;--line:#e3e0d8;--ok:#1f7a4d;--bad:#b3261e;--card:#fff;--accent:#2b4c7e}
@media (prefers-color-scheme:dark){:root{--bg:#161614;--fg:#ecebe6;--muted:#a3a19a;--line:#33322e;--ok:#5cc28f;--bad:#ff8a80;--card:#1f1f1c;--accent:#8fb3e8}}
*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--fg);font:15px/1.5 system-ui,-apple-system,""Segoe UI"",sans-serif}
main{max-width:1200px;margin:0 auto;padding:32px 16px}h1{font-size:28px;margin:0 0 4px}h2{font-size:18px;margin:32px 0 8px}
.muted{color:var(--muted)}.banner{padding:16px 20px;border-radius:8px;margin:20px 0;font-weight:600;border:1px solid}
.banner.ok{color:var(--ok);border-color:var(--ok)}.banner.bad{color:var(--bad);border-color:var(--bad)}
dl{display:grid;grid-template-columns:max-content 1fr;gap:4px 16px;margin:0}dt{color:var(--muted)}dd{margin:0}
table{width:100%;border-collapse:collapse;background:var(--card);border:1px solid var(--line)}th,td{padding:8px;border-bottom:1px solid var(--line);vertical-align:top;text-align:left}
th{font-size:12px;text-transform:uppercase;letter-spacing:.04em;color:var(--muted)}code{font:12px ui-monospace,Consolas,monospace;word-break:break-all}
td.nowrap code{white-space:nowrap}img.thumb{max-width:220px;max-height:140px;border:1px solid var(--line);border-radius:4px;display:block}
.st-ok{color:var(--ok);font-weight:600}.st-bad{color:var(--bad);font-weight:600}.wrap{overflow-x:auto}
footer{margin-top:40px;font-size:13px;color:var(--muted)}
";

        static string Esc(string s)
        {
            // Same escaping as Python's html.escape: non-ASCII text stays readable in the source.
            return (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&#x27;");
        }

        static string Rel(CaseFolder folder, string stored, string outDir)
        {
            string full = Path.GetFullPath(folder.Resolve(stored));
            var from = new Uri(outDir.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar);
            var to = new Uri(full);
            if (!string.Equals(from.Scheme, to.Scheme, StringComparison.OrdinalIgnoreCase) || from.IsUnc != to.IsUnc ||
                !string.Equals(Path.GetPathRoot(outDir), Path.GetPathRoot(full), StringComparison.OrdinalIgnoreCase))
                return to.AbsoluteUri;
            return from.MakeRelativeUri(to).ToString();
        }

        static string Status(IList<string> problems)
        {
            return problems.Count == 0 ? "<span class=\"st-ok\">OK</span>" : "<span class=\"st-bad\">" + Esc(string.Join(" / ", problems)) + "</span>";
        }

        public static string Render(CaseFolder folder, VerifyResult res, string outPath)
        {
            string outDir = Path.GetDirectoryName(Path.GetFullPath(outPath));
            var rows = new StringBuilder();
            foreach (var r in res.Entries)
            {
                var e = r.Entry;
                string f = e.Str("file");
                string thumb = "";
                string lower = f.ToLowerInvariant();
                if (f.Length > 0 && r.FileStatus == "ok" && (lower.EndsWith(".png") || lower.EndsWith(".jpg") || lower.EndsWith(".jpeg") || lower.EndsWith(".webp")))
                {
                    string href = Esc(Rel(folder, f, outDir));
                    thumb = "<a href=\"" + href + "\"><img class=\"thumb\" loading=\"lazy\" src=\"" + href + "\" alt=\"capture " + r.Seq + "\"></a>";
                }
                string ctx = string.Join(" ", new[] { e.Str("url"), e.Str("title") }.Where(x => x.Length > 0));
                rows.Append("<tr><td>").Append(r.Seq).Append("</td><td class='nowrap'><code>").Append(Esc(e.Str("utc")))
                    .Append("</code></td><td>").Append(Esc(e.Str("tag"))).Append("</td><td>").Append(Esc(e.Str("type")))
                    .Append("</td><td>").Append(thumb).Append("<code>").Append(Esc(f)).Append("</code><div class='muted'>").Append(Esc(ctx))
                    .Append("</div><div>").Append(Esc(e.Str("note"))).Append("</div></td><td><code>").Append(Esc(e.Str("sha256")))
                    .Append("</code></td><td>").Append(Status(r.Problems)).Append("</td></tr>");
            }
            var seals = new StringBuilder();
            foreach (var s in res.Seals)
            {
                string ts = s.TimestampImprintOk.HasValue
                    ? Esc(s.TimestampTime) + "<br>imprint " + (s.TimestampImprintOk.Value ? "OK" : "MISMATCH") + ", signature not checked (use chainshot.py verify --tsa-ca)"
                    : "none";
                seals.Append("<tr><td><code>").Append(Esc(s.File)).Append("</code></td><td>").Append(Esc(s.Utc)).Append("</td><td>")
                    .Append(s.Entries).Append("</td><td>").Append(ts).Append("</td><td>").Append(Status(s.Problems)).Append("</td></tr>");
            }
            if (seals.Length == 0) seals.Append("<tr><td colspan=5 class=muted>No seal yet: use <code>Seal</code> in ChainShot.</td></tr>");
            string journalSha = File.Exists(folder.JournalPath) ? Format.Sha256Hex(File.ReadAllBytes(folder.JournalPath)) : "";
            string banner = res.Ok
                ? "<div class=\"banner ok\">All evidence verified: files, hash chain and seals are intact.</div>"
                : "<div class=\"banner bad\">Verification FAILED: see the entries marked in red.</div>";
            string warn = string.Concat(res.Warnings.Concat(res.Errors).Select(w => "<p class='st-bad'>" + Esc(w) + "</p>"));
            string caseName = folder.Name.Length > 0 ? folder.Name : Path.GetFileName(folder.Root);

            var sb = new StringBuilder();
            sb.Append("<!doctype html>\n<html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">\n");
            sb.Append("<title>ChainShot report</title><style>").Append(Css).Append("</style></head><body><main>\n");
            sb.Append("<h1>Evidence report: ").Append(Esc(caseName)).Append("</h1>\n");
            sb.Append("<p class=\"muted\">Generated ").Append(Esc(Format.FormatUtc(DateTime.UtcNow))).Append(" by ").Append(Esc(Format.ToolId)).Append("</p>\n");
            sb.Append(banner).Append(warn).Append('\n');
            sb.Append("<dl><dt>Case folder</dt><dd><code>").Append(Esc(folder.Root)).Append("</code></dd>\n");
            sb.Append("<dt>Examiner</dt><dd>").Append(Esc(folder.Examiner)).Append("</dd><dt>Created</dt><dd>").Append(Esc(folder.Meta.Str("created_utc"))).Append("</dd>\n");
            sb.Append("<dt>Entries</dt><dd>").Append(res.Entries.Count).Append("</dd><dt>Journal SHA-256</dt><dd><code>").Append(journalSha).Append("</code></dd></dl>\n");
            sb.Append("<h2>Seals</h2><div class=\"wrap\"><table><thead><tr><th>Seal</th><th>Sealed (UTC)</th><th>Entries</th><th>RFC 3161</th><th>Status</th></tr></thead>\n");
            sb.Append("<tbody>").Append(seals).Append("</tbody></table></div>\n");
            sb.Append("<h2>Journal</h2><div class=\"wrap\"><table><thead><tr><th>#</th><th>UTC</th><th>Tag</th><th>Type</th><th>Evidence</th><th>SHA-256</th><th>Status</th></tr></thead>\n");
            sb.Append("<tbody>").Append(rows).Append("</tbody></table></div>\n");
            sb.Append("<footer><p>How to check this report independently: copy <code>chainshot.py</code> (standard Python 3.9+, no dependency) and run\n");
            sb.Append("<code>python chainshot.py verify &lt;case folder&gt;</code>. Each entry is chained to the previous one with SHA-256; editing,\n");
            sb.Append("removing or reordering any entry, file or timestamp breaks the chain. This report is a view: the journal and the files are the evidence.</p></footer>\n");
            sb.Append("</main></body></html>\n");
            return sb.ToString();
        }

        /// <summary>Writes report/index.html (or outPath) and returns its path.</summary>
        public static string Write(CaseFolder folder, VerifyResult res, string outPath)
        {
            string path = !string.IsNullOrEmpty(outPath) ? outPath : Path.Combine(folder.Root, "report", "index.html");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, Render(folder, res, path), Format.Utf8);
            return path;
        }
    }
}
