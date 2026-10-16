// SPDX-License-Identifier: MIT
// ChainShot journal format v1 - C# implementation used by ChainShot.exe.
// Must hash exactly like python/chainshot.py, the PowerShell module and the browser
// extension: docs/journal-format.md is the specification and
// tests/vectors/canonical-vectors.json the executable contract (see SelfTest).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace ChainShot
{
    public static class Format
    {
        public const string Version = "1";
        public const string JournalName = "journal.jsonl";
        public const string CaseFileName = "chainshot-case.json";
        public static readonly string Genesis = new string('0', 64);
        public static readonly string[] ChainedFields =
        {
            "v", "seq", "utc", "type", "tag", "file", "size", "sha256",
            "examiner", "host", "source", "url", "title", "note",
        };
        public static readonly string[] EntryTypes = { "screenshot", "browser", "file", "note", "seal", "timestamp" };
        public static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

        public static string ToolId
        {
            get { return "chainshot-app/" + AppInfo.Version; }
        }

        /// <summary>CR and LF are replaced by one space each; no other normalisation.</summary>
        public static string Sanitize(string value)
        {
            if (value == null) return "";
            return value.Replace("\r", " ").Replace("\n", " ");
        }

        public static string Canonical(JsonObject entry)
        {
            return string.Join("\n", ChainedFields.Select(f => f + "=" + Sanitize(entry.Str(f))));
        }

        public static string Hex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (byte b in bytes) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        public static string Sha256Hex(byte[] data)
        {
            using (var sha = SHA256.Create()) return Hex(sha.ComputeHash(data));
        }

        public static string Sha256Hex(string text)
        {
            return Sha256Hex(Utf8.GetBytes(text));
        }

        public static string EntryHash(JsonObject entry)
        {
            return Sha256Hex(Canonical(entry));
        }

        /// <summary>chain(n) = SHA-256( chain(n-1) + entryHash(n) ).</summary>
        public static string NextChain(string previous, JsonObject entry)
        {
            return Sha256Hex(previous + EntryHash(entry));
        }

        public static string FormatUtc(DateTime utc)
        {
            return utc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        }

        public static string FormatLocal(DateTime utc)
        {
            return utc.ToLocalTime().ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);
        }

        public static string FileStamp(DateTime utc)
        {
            return utc.ToUniversalTime().ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture);
        }

        public sealed class FileDigest
        {
            public string Sha256;
            public string Md5;
            public long Size;
        }

        public static FileDigest DigestFile(string path)
        {
            using (var sha = SHA256.Create())
            using (var md5 = MD5.Create())
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var buffer = new byte[1 << 20];
                long size = 0;
                int n;
                while ((n = fs.Read(buffer, 0, buffer.Length)) > 0)
                {
                    sha.TransformBlock(buffer, 0, n, null, 0);
                    md5.TransformBlock(buffer, 0, n, null, 0);
                    size += n;
                }
                sha.TransformFinalBlock(new byte[0], 0, 0);
                md5.TransformFinalBlock(new byte[0], 0, 0);
                return new FileDigest { Sha256 = Hex(sha.Hash), Md5 = Hex(md5.Hash), Size = size };
            }
        }
    }

    /// <summary>A ChainShot case folder: metadata, journal, captures, seals.</summary>
    public sealed class CaseFolder
    {
        public string Root { get; private set; }
        public JsonObject Meta { get; private set; }

        public string JournalPath { get { return Path.Combine(Root, Format.JournalName); } }
        public string Name { get { return Meta.Str("case"); } }
        public string Examiner { get { return Meta.Str("examiner"); } }

        CaseFolder(string root, JsonObject meta)
        {
            Root = root;
            Meta = meta;
        }

        public static bool IsCase(string path)
        {
            return !string.IsNullOrEmpty(path) && File.Exists(Path.Combine(path, Format.CaseFileName));
        }

        public static CaseFolder Open(string path)
        {
            string root = Path.GetFullPath(path);
            string metaPath = Path.Combine(root, Format.CaseFileName);
            if (!File.Exists(metaPath)) throw new InvalidOperationException("Not a ChainShot case folder (no " + Format.CaseFileName + "): " + root);
            return new CaseFolder(root, MiniJson.ParseObject(File.ReadAllText(metaPath, Encoding.UTF8)));
        }

        public static CaseFolder Create(string path, string caseName, string examiner)
        {
            string root = Path.GetFullPath(path);
            if (File.Exists(Path.Combine(root, Format.CaseFileName))) throw new InvalidOperationException("A case already exists in " + root);
            Directory.CreateDirectory(Path.Combine(root, "captures"));
            var meta = new JsonObject();
            meta["format"] = "chainshot-case";
            meta["v"] = Format.Version;
            meta["case"] = caseName;
            meta["examiner"] = examiner ?? "";
            meta["created_utc"] = Format.FormatUtc(DateTime.UtcNow);
            meta["tool"] = Format.ToolId;
            File.WriteAllText(Path.Combine(root, Format.CaseFileName), MiniJson.Serialize(meta, true) + "\n", Format.Utf8);
            var folder = new CaseFolder(root, meta);
            folder.Append(new Dictionary<string, string> { { "type", "note" }, { "tag", "INIT" }, { "note", "Case created: " + caseName } }, null);
            return folder;
        }

        public List<JsonObject> ReadEntries()
        {
            var list = new List<JsonObject>();
            if (!File.Exists(JournalPath)) return list;
            int lineNo = 0;
            foreach (string raw in File.ReadAllLines(JournalPath, Encoding.UTF8))
            {
                lineNo++;
                string line = raw.TrimEnd('\r');
                if (line.Trim().Length == 0) continue;
                try { list.Add(MiniJson.ParseObject(line)); }
                catch (FormatException ex) { throw new InvalidDataException(Format.JournalName + " line " + lineNo + ": invalid JSON (" + ex.Message + ")"); }
            }
            return list;
        }

        public string Resolve(string stored)
        {
            return Path.IsPathRooted(stored) ? stored : Path.Combine(Root, stored.Replace('/', Path.DirectorySeparatorChar));
        }

        /// <summary>Paths inside the case are stored relative with '/', others absolute.</summary>
        public string StoredPath(string fullPath)
        {
            string root = Root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string full = Path.GetFullPath(fullPath);
            if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return full.Substring(root.Length).Replace('\\', '/');
            return full;
        }

        /// <summary>Appends an entry; seq, utc, identity fields and chain are computed here.</summary>
        public JsonObject Append(IDictionary<string, string> fields, JsonObject extra, DateTime? utcNow = null)
        {
            string type;
            if (!fields.TryGetValue("type", out type) || Array.IndexOf(Format.EntryTypes, type) < 0)
                throw new ArgumentException("Unknown entry type: " + type);
            using (AcquireLock())
            {
                var existing = ReadEntries();
                string prev = existing.Count > 0 ? existing[existing.Count - 1].Str("chain") : Format.Genesis;
                DateTime now = utcNow ?? DateTime.UtcNow;
                var entry = new JsonObject();
                foreach (string f in Format.ChainedFields) entry[f] = "";
                entry["v"] = Format.Version;
                entry["seq"] = (existing.Count + 1).ToString(CultureInfo.InvariantCulture);
                entry["utc"] = Format.FormatUtc(now);
                entry["examiner"] = Examiner;
                entry["host"] = Environment.MachineName;
                entry["source"] = Format.ToolId;
                foreach (var kv in fields)
                    if (Array.IndexOf(Format.ChainedFields, kv.Key) >= 0) entry[kv.Key] = Format.Sanitize(kv.Value);
                entry["local"] = Format.FormatLocal(now);
                if (extra != null && extra.Count > 0) entry["extra"] = extra;
                entry["chain"] = Format.NextChain(prev, entry);
                File.AppendAllText(JournalPath, MiniJson.Serialize(entry) + "\n", Format.Utf8);
                return entry;
            }
        }

        IDisposable AcquireLock()
        {
            string lockPath = Path.Combine(Root, ".journal.lock");
            for (int i = 0; i < 50; i++)
            {
                try { return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                catch (IOException) { Thread.Sleep(100); }
            }
            throw new IOException("Journal is locked by another process: " + lockPath);
        }

        /// <summary>SHA-256 of the first n lines of the journal file, as raw bytes.</summary>
        public string JournalPrefixSha256(int n)
        {
            byte[] data = File.Exists(JournalPath) ? File.ReadAllBytes(JournalPath) : new byte[0];
            if (n == 0) return Format.Sha256Hex(new byte[0]);
            int seen = 0;
            for (int i = 0; i < data.Length; i++)
            {
                if (data[i] != (byte)'\n') continue;
                if (++seen == n)
                {
                    var prefix = new byte[i + 1];
                    Buffer.BlockCopy(data, 0, prefix, 0, i + 1);
                    return Format.Sha256Hex(prefix);
                }
            }
            throw new InvalidDataException("Journal has fewer than " + n + " lines");
        }

        public JsonObject AddFile(string path, string tag, string note, bool copy)
        {
            string src = Path.GetFullPath(path);
            if (!File.Exists(src)) throw new FileNotFoundException("Not a file", src);
            string target = src;
            if (copy)
            {
                string dir = Path.Combine(Root, "files");
                Directory.CreateDirectory(dir);
                target = Path.Combine(dir, Path.GetFileName(src));
                if (File.Exists(target)) throw new IOException("Already in case: " + target);
                File.Copy(src, target);
            }
            var d = Format.DigestFile(target);
            var extra = new JsonObject();
            extra["md5"] = d.Md5;
            return Append(new Dictionary<string, string>
            {
                { "type", "file" }, { "tag", tag ?? "" }, { "file", StoredPath(target) },
                { "size", d.Size.ToString(CultureInfo.InvariantCulture) }, { "sha256", d.Sha256 }, { "note", note ?? "" },
            }, extra);
        }

        public JsonObject AddNote(string text, string tag)
        {
            if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("Empty note");
            return Append(new Dictionary<string, string> { { "type", "note" }, { "tag", tag ?? "" }, { "note", text } }, null);
        }
    }

    // ---------------------------------------------------------------- verification
    public sealed class EntryCheck
    {
        public int Seq;
        public JsonObject Entry;
        public List<string> Problems = new List<string>();
        public string FileStatus = "";
        public bool Ok { get { return Problems.Count == 0; } }
    }

    public sealed class SealCheck
    {
        public string File;
        public int Entries;
        public string Utc = "";
        public List<string> Problems = new List<string>();
        public string TimestampTime = "";
        public bool? TimestampImprintOk;
        public bool Ok { get { return Problems.Count == 0; } }
    }

    public sealed class VerifyResult
    {
        public List<EntryCheck> Entries = new List<EntryCheck>();
        public List<SealCheck> Seals = new List<SealCheck>();
        public List<string> Warnings = new List<string>();
        public List<string> Errors = new List<string>();
        public bool Ok { get { return Errors.Count == 0 && Entries.All(e => e.Ok) && Seals.All(s => s.Ok); } }
        public int ProblemCount { get { return Entries.Count(e => !e.Ok) + Seals.Count(s => !s.Ok) + Errors.Count; } }
    }

    public static class Verifier
    {
        public static VerifyResult Verify(CaseFolder folder, bool checkFiles = true)
        {
            var res = new VerifyResult();
            List<JsonObject> entries;
            try { entries = folder.ReadEntries(); }
            catch (Exception ex) { res.Errors.Add(ex.Message); return res; }
            if (entries.Count == 0) { res.Errors.Add("Empty journal"); return res; }

            string prev = Format.Genesis, lastUtc = "";
            var chains = new List<string>();
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                var c = new EntryCheck { Seq = i + 1, Entry = e };
                if (e.Str("v") != Format.Version) c.Problems.Add("unsupported format version");
                if (e.Str("seq") != (i + 1).ToString(CultureInfo.InvariantCulture)) c.Problems.Add("sequence is '" + e.Str("seq") + "', expected " + (i + 1));
                if (e.Str("chain") != Format.NextChain(prev, e)) c.Problems.Add("CHAIN BROKEN (entry edited, removed or reordered)");
                string utc = e.Str("utc");
                if (lastUtc.Length > 0 && string.CompareOrdinal(utc, lastUtc) < 0) res.Warnings.Add("entry " + (i + 1) + ": timestamp earlier than previous entry");
                if (utc.Length > 0) lastUtc = utc;
                string file = e.Str("file");
                if (file.Length > 0 && checkFiles)
                {
                    string p = folder.Resolve(file);
                    if (!File.Exists(p)) { c.Problems.Add("FILE MISSING"); c.FileStatus = "missing"; }
                    else
                    {
                        var d = Format.DigestFile(p);
                        if (d.Sha256 != e.Str("sha256")) { c.Problems.Add("FILE HASH MISMATCH (file modified)"); c.FileStatus = "modified"; }
                        else if (e.Str("size").Length > 0 && d.Size.ToString(CultureInfo.InvariantCulture) != e.Str("size")) { c.Problems.Add("SIZE MISMATCH"); c.FileStatus = "modified"; }
                        else c.FileStatus = "ok";
                    }
                }
                res.Entries.Add(c);
                prev = e.Str("chain");
                chains.Add(prev);
            }

            string sealDir = Path.Combine(folder.Root, "seals");
            if (Directory.Exists(sealDir))
            {
                foreach (string sp in Directory.GetFiles(sealDir, "seal-*.json").OrderBy(x => x, StringComparer.Ordinal))
                    res.Seals.Add(VerifySeal(folder, sp, chains));
            }
            return res;
        }

        static SealCheck VerifySeal(CaseFolder folder, string sealPath, List<string> chains)
        {
            var check = new SealCheck { File = Path.GetFileName(sealPath) };
            try
            {
                var seal = MiniJson.ParseObject(File.ReadAllText(sealPath, Encoding.UTF8));
                int n = int.Parse(seal.Str("entries"), CultureInfo.InvariantCulture);
                check.Entries = n;
                check.Utc = seal.Str("utc");
                if (n > chains.Count || (n > 0 && chains[n - 1] != seal.Str("last_chain")))
                    check.Problems.Add("last_chain does not match the journal");
                else if (folder.JournalPrefixSha256(n) != seal.Str("journal_prefix_sha256"))
                    check.Problems.Add("journal prefix hash differs (journal rewritten)");
            }
            catch (Exception ex) { check.Problems.Add("unreadable seal: " + ex.Message); return check; }

            string tsr = Path.ChangeExtension(sealPath, ".tsr");
            if (File.Exists(tsr))
            {
                try
                {
                    var info = Rfc3161.ParseResponse(File.ReadAllBytes(tsr));
                    check.TimestampTime = info.GenTime;
                    check.TimestampImprintOk = info.HashedMessage == Format.Sha256Hex(File.ReadAllBytes(sealPath));
                    if (check.TimestampImprintOk == false) check.Problems.Add("timestamp does not certify this seal (imprint mismatch)");
                }
                catch (Exception ex) { check.Problems.Add("timestamp unreadable: " + ex.Message); }
            }
            return check;
        }
    }

    // ---------------------------------------------------------------------- seals
    public sealed class SealResult
    {
        public string SealPath;
        public string Sha256;
        public int Entries;
        public string TimestampTime;
        public string TimestampError;
    }

    public static class Sealer
    {
        public const string DefaultTsa = "https://freetsa.org/tsr";

        /// <summary>Seals the journal; with tsaUrl, also requests an RFC 3161 timestamp.
        /// A failed timestamp leaves a valid local seal and is reported in TimestampError.</summary>
        public static SealResult Seal(CaseFolder folder, string tag, string tsaUrl)
        {
            var check = Verifier.Verify(folder);
            if (!check.Ok) throw new InvalidOperationException("The journal does not verify; refusing to seal it.");
            var entries = folder.ReadEntries();
            int n = entries.Count;
            DateTime now = DateTime.UtcNow;
            var seal = new JsonObject();
            // Keys in alphabetical order, like the reference implementation (not required by the format).
            seal["case"] = folder.Name;
            seal["entries"] = n.ToString(CultureInfo.InvariantCulture);
            seal["format"] = "chainshot-seal";
            seal["journal_prefix_sha256"] = folder.JournalPrefixSha256(n);
            seal["last_chain"] = n > 0 ? entries[n - 1].Str("chain") : Format.Genesis;
            seal["sealed_by"] = folder.Examiner;
            seal["tool"] = Format.ToolId;
            seal["utc"] = Format.FormatUtc(now);
            seal["v"] = Format.Version;
            string dir = Path.Combine(folder.Root, "seals");
            Directory.CreateDirectory(dir);
            string name = "seal-" + Format.FileStamp(now);
            string sealPath = Path.Combine(dir, name + ".json");
            File.WriteAllText(sealPath, MiniJson.Serialize(seal, true) + "\n", Format.Utf8);
            var d = Format.DigestFile(sealPath);
            folder.Append(new Dictionary<string, string>
            {
                { "type", "seal" }, { "tag", tag ?? "SEAL" }, { "file", "seals/" + name + ".json" },
                { "size", d.Size.ToString(CultureInfo.InvariantCulture) }, { "sha256", d.Sha256 }, { "note", "Seal of entries 1-" + n },
            }, null);
            var result = new SealResult { SealPath = sealPath, Sha256 = d.Sha256, Entries = n };

            if (!string.IsNullOrEmpty(tsaUrl))
            {
                try
                {
                    byte[] digest = new byte[32];
                    for (int i = 0; i < 32; i++) digest[i] = Convert.ToByte(d.Sha256.Substring(2 * i, 2), 16);
                    byte[] tsr = Rfc3161.Request(tsaUrl, digest);
                    var info = Rfc3161.ParseResponse(tsr);
                    if (info.HashedMessage != d.Sha256) throw new InvalidDataException("TSA answered for a different hash");
                    string tsrPath = Path.Combine(dir, name + ".tsr");
                    File.WriteAllBytes(tsrPath, tsr);
                    var td = Format.DigestFile(tsrPath);
                    folder.Append(new Dictionary<string, string>
                    {
                        { "type", "timestamp" }, { "tag", tag ?? "SEAL" }, { "file", "seals/" + name + ".tsr" },
                        { "size", td.Size.ToString(CultureInfo.InvariantCulture) }, { "sha256", td.Sha256 },
                        { "url", tsaUrl }, { "note", "RFC 3161 time " + info.GenTime },
                    }, null);
                    result.TimestampTime = info.GenTime;
                }
                catch (Exception ex) { result.TimestampError = ex.Message; }
            }
            return result;
        }
    }
}
