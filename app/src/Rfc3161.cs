// SPDX-License-Identifier: MIT
// RFC 3161 time-stamp requests and responses with a minimal DER encoder/decoder,
// so that ChainShot.exe needs no extra assembly. Same logic as python/chainshot.py.

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace ChainShot
{
    public sealed class TimestampInfo
    {
        public int Status;
        public string Policy;
        public string HashAlgorithm;
        public string HashedMessage;
        public string Serial;
        public string GenTime;
    }

    public static class Rfc3161
    {
        const string OidSha256 = "2.16.840.1.101.3.4.2.1";
        const string OidTstInfo = "1.2.840.113549.1.9.16.1.4";

        // ------------------------------------------------------------- encoding
        static byte[] Len(int n)
        {
            if (n < 0x80) return new[] { (byte)n };
            var b = new List<byte>();
            while (n > 0) { b.Insert(0, (byte)(n & 0xFF)); n >>= 8; }
            b.Insert(0, (byte)(0x80 | b.Count));
            return b.ToArray();
        }

        static byte[] Tlv(byte tag, byte[] content)
        {
            var ms = new MemoryStream();
            ms.WriteByte(tag);
            var l = Len(content.Length);
            ms.Write(l, 0, l.Length);
            ms.Write(content, 0, content.Length);
            return ms.ToArray();
        }

        static byte[] Concat(params byte[][] parts)
        {
            var ms = new MemoryStream();
            foreach (var p in parts) ms.Write(p, 0, p.Length);
            return ms.ToArray();
        }

        static byte[] Integer(ulong v)
        {
            var b = new List<byte>();
            do { b.Insert(0, (byte)(v & 0xFF)); v >>= 8; } while (v > 0);
            if ((b[0] & 0x80) != 0) b.Insert(0, 0); // keep it positive
            return Tlv(0x02, b.ToArray());
        }

        static byte[] Oid(string dotted)
        {
            var parts = Array.ConvertAll(dotted.Split('.'), long.Parse);
            var body = new List<byte> { (byte)(40 * parts[0] + parts[1]) };
            for (int i = 2; i < parts.Length; i++)
            {
                long p = parts[i];
                var enc = new List<byte> { (byte)(p & 0x7F) };
                p >>= 7;
                while (p > 0) { enc.Insert(0, (byte)(0x80 | (p & 0x7F))); p >>= 7; }
                body.AddRange(enc);
            }
            return Tlv(0x06, body.ToArray());
        }

        /// <summary>TimeStampReq for a SHA-256 digest, with nonce, asking for the TSA certificate.</summary>
        public static byte[] BuildRequest(byte[] digest, ulong nonce)
        {
            byte[] alg = Tlv(0x30, Concat(Oid(OidSha256), new byte[] { 0x05, 0x00 }));
            byte[] imprint = Tlv(0x30, Concat(alg, Tlv(0x04, digest)));
            return Tlv(0x30, Concat(Integer(1), imprint, Integer(nonce), new byte[] { 0x01, 0x01, 0xFF }));
        }

        public static byte[] Request(string url, byte[] digest)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var nonceBytes = new byte[8];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(nonceBytes);
            ulong nonce = BitConverter.ToUInt64(nonceBytes, 0) & 0x7FFFFFFFFFFFFFFF;
            byte[] body = BuildRequest(digest, nonce);
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "POST";
            req.ContentType = "application/timestamp-query";
            req.UserAgent = Format.ToolId;
            req.Timeout = 30000;
            req.ContentLength = body.Length;
            using (var s = req.GetRequestStream()) s.Write(body, 0, body.Length);
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var rs = resp.GetResponseStream())
            using (var ms = new MemoryStream())
            {
                rs.CopyTo(ms);
                return ms.ToArray();
            }
        }

        // ------------------------------------------------------------- decoding
        struct Node { public byte Tag; public int Start; public int End; }

        static Node Read(byte[] d, int pos, out int next)
        {
            if (pos + 2 > d.Length) throw new InvalidDataException("truncated DER");
            byte tag = d[pos];
            int len = d[pos + 1];
            pos += 2;
            if ((len & 0x80) != 0)
            {
                int nb = len & 0x7F;
                if (nb == 0 || nb > 4 || pos + nb > d.Length) throw new InvalidDataException("unsupported DER length");
                len = 0;
                for (int i = 0; i < nb; i++) len = (len << 8) | d[pos + i];
                pos += nb;
            }
            if (len < 0 || pos + len > d.Length) throw new InvalidDataException("truncated DER");
            next = pos + len;
            return new Node { Tag = tag, Start = pos, End = pos + len };
        }

        static List<Node> Children(byte[] d, Node n)
        {
            var list = new List<Node>();
            int pos = n.Start;
            while (pos < n.End) { int next; list.Add(Read(d, pos, out next)); pos = next; }
            return list;
        }

        static byte[] Slice(byte[] d, Node n)
        {
            var b = new byte[n.End - n.Start];
            Buffer.BlockCopy(d, n.Start, b, 0, b.Length);
            return b;
        }

        static string DecodeOid(byte[] b)
        {
            var parts = new List<long> { b[0] / 40, b[0] % 40 };
            long v = 0;
            for (int i = 1; i < b.Length; i++)
            {
                v = (v << 7) | (uint)(b[i] & 0x7F);
                if ((b[i] & 0x80) == 0) { parts.Add(v); v = 0; }
            }
            return string.Join(".", parts);
        }

        public static TimestampInfo ParseResponse(byte[] d)
        {
            int next;
            var top = Children(d, Read(d, 0, out next));
            var statusInt = Children(d, top[0])[0];
            int status = 0;
            foreach (byte b in Slice(d, statusInt)) status = (status << 8) | b;
            if (status != 0 && status != 1) throw new InvalidDataException("TSA refused the request (status " + status + ")");
            if (top.Count < 2) throw new InvalidDataException("no timestamp token in response");
            // ContentInfo -> [0] -> SignedData -> encapContentInfo -> [0] -> OCTET STRING (TSTInfo)
            var ci = Children(d, top[1]);
            var sd = Children(d, Children(d, ci[1])[0]);
            var encap = Children(d, sd[2]);
            if (DecodeOid(Slice(d, encap[0])) != OidTstInfo) throw new InvalidDataException("token does not contain a TSTInfo");
            byte[] tst = Slice(d, Children(d, encap[1])[0]);
            var f = Children(tst, Read(tst, 0, out next));
            var imprint = Children(tst, f[2]);
            var alg = Children(tst, imprint[0]);
            string g = Encoding.ASCII.GetString(Slice(tst, f[4]));
            string gen = g.Substring(0, 4) + "-" + g.Substring(4, 2) + "-" + g.Substring(6, 2) + "T" + g.Substring(8, 2) + ":" +
                         g.Substring(10, 2) + ":" + g.Substring(12, 2) + (g.Length > 15 && g[14] == '.' ? g.Substring(14, g.Length - 15) : "") + "Z";
            return new TimestampInfo
            {
                Status = status,
                Policy = DecodeOid(Slice(tst, f[1])),
                HashAlgorithm = DecodeOid(Slice(tst, alg[0])),
                HashedMessage = Format.Hex(Slice(tst, imprint[1])),
                Serial = Format.Hex(Slice(tst, f[3])),
                GenTime = gen,
            };
        }
    }
}
