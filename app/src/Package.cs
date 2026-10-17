// SPDX-License-Identifier: MIT
// Evidence package: one zip with the case folder, chainshot.py (the independent verifier,
// embedded in ChainShot.exe), VERIFY.txt and SHA256SUMS.txt. Same layout as
// `chainshot.py package`.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;

namespace ChainShot
{
    public static class Package
    {
        const string VerifyTemplate = @"ChainShot evidence package
==========================

Case        : {case}
Examiner    : {examiner}
Packaged    : {utc} by {tool}
Journal     : {entries} entries
              SHA-256 of journal.jsonl: {journal_sha}
{seal_block}
How to verify (Python 3.9 or later, nothing to install)
-------------------------------------------------------
1. Check that no file of this package changed:
       sha256sum -c SHA256SUMS.txt                (Linux, macOS, Git Bash)
       Get-FileHash -Algorithm SHA256 <file>      (Windows PowerShell)
2. Check the evidence, the hash chain and the seals:
       python chainshot.py verify ""{folder}""
   The last line must read: RESULT: ALL EVIDENCE INTACT
3. Compare the seal SHA-256 above with the one written in the signed report
   or sent separately by the examiner. They must be identical.
{tsa_hint}
Documentation: https://github.com/Dalkin69/chainshot/blob/main/docs/verification.md

---

Verification (Python 3.9+, rien a installer) : 1) sha256sum -c SHA256SUMS.txt
2) python chainshot.py verify ""{folder}"" doit afficher RESULT: ALL EVIDENCE INTACT
3) le SHA-256 du scelle ci-dessus doit etre celui du rapport signe.
";

        public sealed class Result
        {
            public string Path;
            public string Sha256;
            public int Files;
        }

        /// <summary>chainshot.py embedded at build time (app/ChainShot.csproj).</summary>
        public static byte[] Verifier()
        {
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("chainshot.py"))
            {
                if (s == null) throw new InvalidOperationException("chainshot.py is not embedded in this build");
                using (var ms = new MemoryStream()) { s.CopyTo(ms); return ms.ToArray(); }
            }
        }

        public static string DefaultPath(CaseFolder folder)
        {
            string name = Path.GetFileName(folder.Root.TrimEnd('\\', '/'));
            return Path.Combine(Path.GetDirectoryName(folder.Root.TrimEnd('\\', '/')),
                                name + "-package-" + DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture) + ".zip");
        }

        /// <summary>Writes the zip (the case must verify) and records it in the journal.</summary>
        public static Result Create(CaseFolder folder, string outPath)
        {
            var res = ChainShot.Verifier.Verify(folder, true);
            if (!res.Ok) throw new InvalidOperationException("The case does not verify; fix it before packaging (run Verify).");
            var entries = folder.ReadEntries();
            JsonObject seal = null, ts = null;
            foreach (var e in entries)
            {
                if (e.Str("type") == "seal") { seal = e; ts = null; }
                else if (e.Str("type") == "timestamp" && seal != null) ts = e;
            }
            string sealBlock;
            if (seal != null)
            {
                int sealSeq = int.Parse(seal.Str("seq"), CultureInfo.InvariantCulture);
                sealBlock = "Last seal   : " + seal.Str("file") + " (entries 1-" + (sealSeq - 1) + ")\n" +
                            "              SHA-256: " + seal.Str("sha256") + "\n";
                if (ts != null) sealBlock += "              " + ts.Str("note") + " - TSA " + ts.Str("url") + "\n";
                if (sealSeq < entries.Count - (ts != null ? 1 : 0)) sealBlock += "              (entries were added after this seal)\n";
            }
            else sealBlock = "Last seal   : none - the examiner did not seal this journal\n";

            string folderName = Path.GetFileName(folder.Root.TrimEnd('\\', '/'));
            string text = VerifyTemplate
                .Replace("{case}", folder.Name).Replace("{examiner}", folder.Examiner)
                .Replace("{utc}", Format.FormatUtc(DateTime.UtcNow)).Replace("{tool}", Format.ToolId)
                .Replace("{entries}", entries.Count.ToString(CultureInfo.InvariantCulture))
                .Replace("{journal_sha}", Format.Sha256Hex(File.ReadAllBytes(folder.JournalPath)))
                .Replace("{seal_block}", sealBlock).Replace("{folder}", folderName)
                .Replace("{tsa_hint}", ts != null ? "   For the RFC 3161 timestamp signature: python chainshot.py verify \"" + folderName + "\" --tsa-ca <TSA CA .pem>\n" : "");
            text = text.Replace("\r\n", "\n").Replace("\n", "\r\n");

            string full = Path.GetFullPath(outPath);
            Directory.CreateDirectory(Path.GetDirectoryName(full));
            string tmp = full + ".part";
            if (File.Exists(tmp)) File.Delete(tmp);
            var sums = new List<string>();
            int count = 0;
            string root = folder.Root.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            using (var zip = ZipFile.Open(tmp, ZipArchiveMode.Create, Format.Utf8))
            {
                var files = Directory.GetFiles(folder.Root, "*", SearchOption.AllDirectories)
                    .Where(f => Path.GetFileName(f) != ".journal.lock")
                    .Select(f => new { Full = f, Rel = f.Substring(root.Length).Replace('\\', '/') })
                    .OrderBy(f => f.Rel, StringComparer.Ordinal);
                foreach (var f in files)
                {
                    string arc = folderName + "/" + f.Rel;
                    zip.CreateEntryFromFile(f.Full, arc, CompressionLevel.Optimal);
                    sums.Add(Format.DigestFile(f.Full).Sha256 + "  " + arc);
                    count++;
                }
                AddBytes(zip, "chainshot.py", Verifier(), sums);
                AddBytes(zip, "VERIFY.txt", Format.Utf8.GetBytes(text), sums);
                AddBytes(zip, "SHA256SUMS.txt", Format.Utf8.GetBytes(string.Join("\n", sums) + "\n"), null);
            }
            if (File.Exists(full)) File.Delete(full);
            File.Move(tmp, full);
            string sha = Format.DigestFile(full).Sha256;
            folder.Append(new Dictionary<string, string>
            {
                { "type", "note" }, { "tag", "PACKAGE" }, { "note", "Package created: " + Path.GetFileName(full) + " SHA-256 " + sha },
            }, null);
            return new Result { Path = full, Sha256 = sha, Files = count };
        }

        static void AddBytes(ZipArchive zip, string name, byte[] data, List<string> sums)
        {
            var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
            using (var s = entry.Open()) s.Write(data, 0, data.Length);
            if (sums != null) sums.Add(Format.Sha256Hex(data) + "  " + name);
        }
    }
}
