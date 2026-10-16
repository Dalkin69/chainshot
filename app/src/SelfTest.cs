// SPDX-License-Identifier: MIT
// `ChainShot.exe selftest <canonical-vectors.json>`: checks this implementation against the
// shared vectors (tests/vectors), so the C# hashing can never drift from the other tools.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ChainShot
{
    public static class SelfTest
    {
        /// <summary>Returns the list of failures (empty when everything matches).</summary>
        public static List<string> RunVectors(string vectorsPath, out int count)
        {
            var failures = new List<string>();
            var doc = MiniJson.ParseObject(File.ReadAllText(vectorsPath, Encoding.UTF8));
            if (doc.Str("genesis") != Format.Genesis) failures.Add("genesis differs");
            var fields = doc["fields"] as List<object>;
            if (fields == null || string.Join(",", fields) != string.Join(",", Format.ChainedFields)) failures.Add("chained field list differs");
            string prev = Format.Genesis;
            count = 0;
            foreach (object o in (List<object>)doc["vectors"])
            {
                var v = (JsonObject)o;
                var entry = (JsonObject)v["entry"];
                count++;
                if (Format.Canonical(entry) != v.Str("canonical")) failures.Add("vector " + count + ": canonical string differs");
                if (Format.EntryHash(entry) != v.Str("entry_hash")) failures.Add("vector " + count + ": entry hash differs");
                prev = Format.NextChain(prev, entry);
                if (prev != v.Str("chain")) failures.Add("vector " + count + ": chain differs");
            }
            if (count == 0) failures.Add("no vectors found");
            return failures;
        }
    }
}
