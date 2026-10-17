// SPDX-License-Identifier: MIT
// Browser link: the ChainShot extension saves its captures into the case open in ChainShot.
//
// The browser starts `ChainShot.exe chrome-extension://<id>/` (Chrome native messaging) and
// exchanges length-prefixed JSON messages on stdin/stdout. Same protocol as
// `chainshot.py native-host`, specified in docs/browser-link.md.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ChainShot
{
    public static class BrowserLink
    {
        public const string HostName = "com.chainshot.host";
        public const int Protocol = 2; // 2: "format": "mhtml" page archives
        const int MaxPngBytes = 60 * 1024 * 1024;

        /// <summary>Extensions allowed to talk to ChainShot: the ID fixed by the "key" of
        /// extension/manifest.json (unpacked install), then the store IDs once published.</summary>
        public static readonly string[] ExtensionIds = { "dcccfnbiglhpbcdhbmokfilfgpfnmikf" };

        /// <summary>Firefox add-on ID (tools/build_extension.py).</summary>
        public const string GeckoId = "chainshot@dalkin69";

        static readonly string[] BrowserKeys =
        {
            @"Software\Google\Chrome", @"Software\Chromium", @"Software\Microsoft\Edge",
            @"Software\BraveSoftware\Brave-Browser", @"Software\Vivaldi",
        };

        // ------------------------------------------------------------ registration
        static string ManifestPath { get { return Path.Combine(SharedState.Dir, HostName + ".json"); } }
        static string FirefoxManifestPath { get { return Path.Combine(SharedState.Dir, HostName + ".firefox.json"); } }
        const string FirefoxKey = @"Software\Mozilla\NativeMessagingHosts\" + HostName;

        /// <summary>True when Firefox started us: arguments are the manifest path and the add-on ID.</summary>
        public static bool IsFirefoxLaunch(string[] args)
        {
            return args.Length >= 2 && args[0].EndsWith(".json", StringComparison.OrdinalIgnoreCase) && args[1] == GeckoId;
        }

        /// <summary>Writes the host manifest next to the settings and registers it for every
        /// Chromium-based browser (current user only).</summary>
        public static string Register(string exePath, IEnumerable<string> extraIds = null)
        {
            Directory.CreateDirectory(SharedState.Dir);
            var ids = ExtensionIds.Concat(extraIds ?? new string[0]).Distinct().ToList();
            var manifest = new JsonObject();
            manifest["name"] = HostName;
            manifest["description"] = "ChainShot - save browser captures into the current case";
            manifest["path"] = exePath;
            manifest["type"] = "stdio";
            manifest["allowed_origins"] = ids.Select(i => (object)("chrome-extension://" + i + "/")).ToList();
            File.WriteAllText(ManifestPath, MiniJson.Serialize(manifest, true) + "\n", Format.Utf8);
            foreach (string k in BrowserKeys)
                using (var key = Registry.CurrentUser.CreateSubKey(k + @"\NativeMessagingHosts\" + HostName))
                    key.SetValue("", ManifestPath);
            // Firefox: same host, its own manifest format (allowed_extensions).
            var ff = new JsonObject();
            ff["name"] = HostName;
            ff["description"] = manifest["description"];
            ff["path"] = exePath;
            ff["type"] = "stdio";
            ff["allowed_extensions"] = new List<object> { GeckoId };
            File.WriteAllText(FirefoxManifestPath, MiniJson.Serialize(ff, true) + "\n", Format.Utf8);
            using (var key = Registry.CurrentUser.CreateSubKey(FirefoxKey)) key.SetValue("", FirefoxManifestPath);
            return ManifestPath;
        }

        public static void Unregister()
        {
            foreach (string k in BrowserKeys)
                Registry.CurrentUser.DeleteSubKeyTree(k + @"\NativeMessagingHosts\" + HostName, false);
            Registry.CurrentUser.DeleteSubKeyTree(FirefoxKey, false);
            try { File.Delete(ManifestPath); } catch (IOException) { }
            try { File.Delete(FirefoxManifestPath); } catch (IOException) { }
        }

        /// <summary>True when Chrome's key points to a manifest that launches this executable.</summary>
        public static bool IsRegisteredFor(string exePath)
        {
            try
            {
                string m = Registry.GetValue(@"HKEY_CURRENT_USER\" + BrowserKeys[0] + @"\NativeMessagingHosts\" + HostName, "", null) as string;
                if (string.IsNullOrEmpty(m) || !File.Exists(m)) return false;
                return string.Equals(MiniJson.ParseObject(File.ReadAllText(m, Encoding.UTF8)).Str("path"), exePath, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception) { return false; }
        }

        // ---------------------------------------------------------------- protocol
        public static JsonObject ReadMessage(Stream input)
        {
            var head = ReadExactly(input, 4);
            if (head == null) return null;
            int length = BitConverter.ToInt32(head, 0);
            if (length < 0 || length > 64 * 1024 * 1024) throw new InvalidDataException("message too large");
            var body = ReadExactly(input, length);
            if (body == null) throw new InvalidDataException("truncated message");
            return MiniJson.ParseObject(Encoding.UTF8.GetString(body));
        }

        static byte[] ReadExactly(Stream s, int n)
        {
            var buf = new byte[n];
            int got = 0;
            while (got < n)
            {
                int r = s.Read(buf, got, n - got);
                if (r <= 0) return null; // end of input (or a truncated message)
                got += r;
            }
            return buf;
        }

        public static void WriteMessage(Stream output, JsonObject message)
        {
            byte[] body = Encoding.UTF8.GetBytes(MiniJson.Serialize(message));
            output.Write(BitConverter.GetBytes(body.Length), 0, 4);
            output.Write(body, 0, body.Length);
            output.Flush();
        }

        /// <summary>Message loop: one reply per message until the browser closes stdin.</summary>
        public static int Serve(Stream input, Stream output)
        {
            while (true)
            {
                JsonObject msg;
                try { msg = ReadMessage(input); }
                catch (Exception ex)
                {
                    WriteMessage(output, Error("bad message: " + ex.Message));
                    return 2;
                }
                if (msg == null) return 0;
                JsonObject reply;
                try { reply = Handle(msg); }
                catch (Exception ex) { reply = Error(ex.Message); }
                WriteMessage(output, reply);
            }
        }

        static JsonObject Error(string message)
        {
            var o = new JsonObject();
            o["ok"] = false;
            o["error"] = message;
            return o;
        }

        static CaseFolder CurrentCase()
        {
            string path = SharedState.Load().CasePath;
            if (string.IsNullOrEmpty(path)) throw new InvalidOperationException("no case is open in ChainShot: open or create one first");
            if (!CaseFolder.IsCase(path)) throw new InvalidOperationException("the current case is not a ChainShot case folder: " + path);
            return CaseFolder.Open(path);
        }

        static JsonObject CaseInfo(CaseFolder folder)
        {
            var o = new JsonObject();
            o["ok"] = true;
            o["protocol"] = Protocol;
            o["tool"] = Format.ToolId;
            o["host"] = Environment.MachineName;
            o["tag"] = SharedState.Load().Tag;
            if (folder == null) { o["case"] = null; return o; }
            var entries = folder.ReadEntries();
            var c = new JsonObject();
            c["name"] = folder.Name;
            c["path"] = folder.Root;
            c["examiner"] = folder.Examiner;
            c["entries"] = entries.Count;
            c["next_seq"] = entries.Count + 1;
            c["next_capture"] = entries.Count(e => e.Str("type") == "screenshot" || e.Str("type") == "browser") + 1;
            o["case"] = c;
            return o;
        }

        static readonly Regex UtcPattern = new Regex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$");

        public static JsonObject Handle(JsonObject msg)
        {
            string kind = msg.Str("type");
            if (kind == "hello")
            {
                try { return CaseInfo(CurrentCase()); }
                catch (InvalidOperationException ex)
                {
                    var o = CaseInfo(null);
                    o["warning"] = ex.Message;
                    return o;
                }
            }
            if (kind == "set_tag")
            {
                var st = SharedState.Load();
                st.Tag = Format.Sanitize(msg.Str("tag")).Trim();
                st.Save();
                var o = new JsonObject();
                o["ok"] = true;
                o["tag"] = st.Tag;
                return o;
            }
            var folder = CurrentCase();
            string source = Format.Sanitize(msg.Str("source"));
            if (source.Length == 0) source = Format.ToolId;
            var via = new JsonObject();
            via["via"] = Format.ToolId;

            if (kind == "note")
            {
                string text = Format.Sanitize(msg.Str("text")).Trim();
                if (text.Length == 0) throw new ArgumentException("empty note");
                var e = folder.Append(new Dictionary<string, string> { { "type", "note" }, { "tag", msg.Has("tag") ? msg.Str("tag") : SharedState.Load().Tag }, { "note", text }, { "source", source } }, via);
                return Ok(e, "seq", "utc", "chain");
            }
            if (kind == "verify")
            {
                var res = Verifier.Verify(folder, true);
                var o = new JsonObject();
                o["ok"] = true;
                o["intact"] = res.Ok;
                o["entries"] = res.Entries.Count;
                o["seals"] = res.Seals.Count;
                o["problems"] = res.Entries.Where(x => !x.Ok).Select(x => (object)("#" + x.Seq + ": " + string.Join(" / ", x.Problems)))
                    .Concat(res.Seals.Where(x => !x.Ok).Select(x => (object)(x.File + ": " + string.Join(" / ", x.Problems))))
                    .Concat(res.Errors.Cast<object>()).Take(20).ToList();
                return o;
            }
            if (kind == "capture")
            {
                string fmt = msg.Has("format") ? msg.Str("format").ToLowerInvariant() : "png";
                if (fmt != "png" && fmt != "mhtml") throw new ArgumentException("unsupported capture format: " + fmt);
                byte[] png;
                try { png = Convert.FromBase64String(msg.Has("png_base64") ? msg.Str("png_base64") : msg.Str("data_base64")); }
                catch (FormatException) { throw new ArgumentException("the capture is not valid base64"); }
                if (png.Length > MaxPngBytes) throw new ArgumentException("the capture is too large");
                byte[] magic = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
                if (fmt == "png" && (png.Length < 8 || !magic.SequenceEqual(png.Take(8))))
                    throw new ArgumentException("the capture is not a PNG image");
                if (fmt == "mhtml")
                {
                    string head = Encoding.ASCII.GetString(png, 0, Math.Min(png.Length, 4096));
                    if (head.IndexOf("MIME-Version", StringComparison.Ordinal) < 0 && !head.StartsWith("From:", StringComparison.Ordinal))
                        throw new ArgumentException("the page archive is not an MHTML document");
                }
                DateTime when = DateTime.UtcNow;
                string utc = msg.Str("utc");
                DateTime t;
                if (UtcPattern.IsMatch(utc) && DateTime.TryParseExact(utc, "yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out t) && Math.Abs((t - when).TotalSeconds) <= 600)
                    when = t; // the capture time, when the browser's clock agrees with ours
                string tag = Format.Sanitize(msg.Str("tag"));
                using (folder.Lock())
                {
                    var entries = folder.ReadEntries();
                    int seq = entries.Count + 1;
                    // A banner shows the entry number: it must still be the next one (not needed for archives).
                    if (fmt == "png" && msg.Str("expected_seq") != seq.ToString(CultureInfo.InvariantCulture))
                    {
                        // The banner drawn by the extension shows another number: it redraws and resends.
                        var o = CaseInfo(folder);
                        o["ok"] = false;
                        o["retry"] = true;
                        o["error"] = "the journal changed (next entry is #" + seq + ")";
                        return o;
                    }
                    string safeTag = Regex.Replace(tag, @"[^\w\-]", "_");
                    if (safeTag.Length == 0) safeTag = "untagged";
                    string name = string.Format(CultureInfo.InvariantCulture, "{0:D4}_{1}_{2}.{3}", seq, Format.FileStamp(when), safeTag, fmt);
                    string dir = Path.Combine(folder.Root, "captures");
                    Directory.CreateDirectory(dir);
                    string path = Path.Combine(dir, name);
                    using (var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write)) fs.Write(png, 0, png.Length);
                    var d = Format.DigestFile(path);
                    var extra = new JsonObject();
                    var given = msg["extra"] as JsonObject;
                    if (given != null)
                        foreach (string k in new[] { "viewport", "user_agent", "mode" })
                            if (given.Has(k)) extra[k] = Format.Sanitize(given.Str(k));
                    extra["md5"] = d.Md5;
                    extra["via"] = Format.ToolId;
                    var e = folder.Append(new Dictionary<string, string>
                    {
                        { "type", "browser" }, { "tag", tag }, { "file", "captures/" + name }, { "size", d.Size.ToString(CultureInfo.InvariantCulture) },
                        { "sha256", d.Sha256 }, { "url", msg.Str("url") }, { "title", msg.Str("title") }, { "note", msg.Str("note") }, { "source", source },
                    }, extra, when, true);
                    return Ok(e, "seq", "utc", "file", "sha256", "chain");
                }
            }
            throw new ArgumentException("unknown message type: " + kind);
        }

        static JsonObject Ok(JsonObject entry, params string[] keys)
        {
            var o = new JsonObject();
            o["ok"] = true;
            var e = new JsonObject();
            foreach (string k in keys) e[k] = entry.Str(k);
            o["entry"] = e;
            return o;
        }
    }
}
