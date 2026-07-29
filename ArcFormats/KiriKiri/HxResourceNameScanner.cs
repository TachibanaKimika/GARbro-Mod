//! \file       HxResourceNameScanner.cs
//! \date       2026 Jul 29
//! \brief      Hx v4 resource metadata name discovery.
//
// Copyright (C) 2026 by GARbro-Mod-Onachi contributors
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to
// deal in the Software without restriction, including without limitation the
// rights to use, copy, modify, merge, publish, distribute, sublicense, and/or
// sell copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
// FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS
// IN THE SOFTWARE.
//

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using GameRes.Compression;
using GameRes.Formats.Emote;
using K4os.Compression.LZ4;
using Newtonsoft.Json.Linq;

namespace GameRes.Formats.KiriKiri
{
    internal struct HxResourceScanInfo
    {
        public bool Parsed;
        public bool Scenario;
    }

    /// <summary>
    /// Reads name-bearing resources without extracting or renaming game files.
    /// Every discovered candidate is still checked against the actual Hx index
    /// by <see cref="HxNameGenerator.CandidateCollector"/>.
    /// </summary>
    internal sealed class HxResourceNameScanner
    {
        const int MaxTextLength = 8 * 1024 * 1024;
        const int MaxPsbMetadataLength = 64 * 1024 * 1024;
        const int MaxTjsObjectLength = 64 * 1024 * 1024;
        const uint TjsSignature = 0x2F534A54; // 'TJS/'

        static readonly Regex ObfuscatedNameRe = new Regex (
            @"^(?:[0-9A-Fa-f]{16}|[0-9A-Fa-f]{64})(?:\.[^.]*)?$",
            RegexOptions.Compiled);
        static readonly Regex AssignmentRe = new Regex (
            @"(?<key>bgm|bgv|bg_voice|live|liveout|loopvoice|loopvoicelist|lse|lse2|se|se2|sound|sysse|voice|playvoice|image|event|ev|stand|stage|background|bg|clip|icon|stamp|scenario|scene|script|storage)"
          + @"\s*(?:=>|:|=)\s*(?:(?<quote>[""'])(?<quoted>.*?)\k<quote>|(?<bare>[A-Za-z0-9_@+\-./\\|]+))",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        static readonly Regex BaseStageAssignmentRe = new Regex (
            @"\b(?<key>prefix|image)\b\s*(?:=>|:|=)\s*(?<quote>[""'])(?<value>.*?)\k<quote>",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        static readonly Regex MovieRe = new Regex (
            @"\[(?:sysmovie|edmovie)\s+file=(?<name>[^\s\]]+)\s*\]",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        static readonly Regex StandFileRe = new Regex (
            @"\bfilename\s*:\s*(?<quote>[""'])(?<name>.*?)\k<quote>",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        static readonly Regex VoiceStemRe = new Regex (
            @"^[A-Za-z][A-Za-z0-9]*_(?:[A-Za-z0-9_]+)$",
            RegexOptions.Compiled);

        readonly HxNameGenerator.CandidateCollector m_candidates;
        readonly List<DeferredPbd> m_deferred_pbd = new List<DeferredPbd>();
        readonly List<DeferredText> m_deferred_text = new List<DeferredText>();
        int m_deferred_text_length;

        public HxResourceNameScanner (HxNameGenerator.CandidateCollector candidates)
        {
            m_candidates = candidates;
        }

        public HxResourceScanInfo Scan (IBinaryStream input, string source_name, string name_hash = null)
        {
            var info = new HxResourceScanInfo();
            if (null == input || input.Length <= 0)
                return info;

            source_name = ResolveSourceName (source_name, name_hash);
            if (IsUsefulPlainName (source_name))
                m_candidates.AddName (source_name);

            if (input.Signature == HxNameGenerator.PsbSignature)
                return ScanPsb (input, source_name);

            if ((input.Signature & 0x00FFFFFFu) == 0x0066646Du)
            {
                byte[] psb_data;
                if (TryDecompressMdf (input, out psb_data))
                {
                    using (var psb = BinaryStream.FromArray (psb_data, source_name))
                        return ScanPsb (psb, source_name);
                }
                return info;
            }

            if (input.Signature == TjsSignature)
            {
                if (input.Length > MaxTjsObjectLength || input.Length > int.MaxValue)
                    return info;
                input.Position = 0;
                var tjs_data = input.ReadBytes ((int)input.Length);
                object tjs_root;
                if (TjsNs0Reader.TryRead (tjs_data, out tjs_root))
                {
                    info.Parsed = true;
                    m_candidates.Walk (tjs_root, "pbd");
                    ScanPbdObject (source_name, name_hash, tjs_root);
                }
                return info;
            }

            if (input.Length > MaxTextLength || input.Length > int.MaxValue)
                return info;
            input.Position = 0;
            var data = input.ReadBytes ((int)input.Length);
            var text = TryDecodeText (data);
            if (null == text)
                return info;
            info.Parsed = true;
            ScanText (source_name, text);
            if (!IsUsefulPlainName (source_name) && !string.IsNullOrEmpty (name_hash)
                && m_deferred_text.Count < 1024
                && m_deferred_text_length <= MaxPsbMetadataLength-text.Length)
            {
                m_deferred_text.Add (new DeferredText {
                    SourceName = source_name,
                    NameHash = name_hash,
                    Text = text,
                });
                m_deferred_text_length += text.Length;
            }
            return info;
        }

        public void Complete ()
        {
            for (int pass = 0; pass < 4; ++pass)
            {
                bool resolved_any = false;
                foreach (var item in m_deferred_text)
                {
                    if (item.Resolved)
                        continue;
                    var source_name = ResolveSourceName (item.SourceName, item.NameHash);
                    if (!IsUsefulPlainName (source_name))
                        continue;
                    ScanText (source_name, item.Text);
                    item.Resolved = true;
                    resolved_any = true;
                }
                if (!resolved_any)
                    break;
            }
            foreach (var item in m_deferred_pbd)
            {
                var source_name = ResolveSourceName (item.SourceName, item.NameHash);
                if (!IsUsefulPlainName (source_name))
                    continue;
                AddPbdDerivedNames (source_name, item.Root);
            }
            m_deferred_text.Clear();
            m_deferred_text_length = 0;
            m_deferred_pbd.Clear();
        }

        string ResolveSourceName (string source_name, string name_hash)
        {
            string resolved;
            if (!string.IsNullOrEmpty (name_hash)
                && m_candidates.TryResolveName (name_hash, out resolved))
                return resolved;
            return source_name;
        }

        HxResourceScanInfo ScanPsb (IBinaryStream input, string source_name)
        {
            var info = new HxResourceScanInfo();
            if (input.Length > int.MaxValue || !HasReasonablePsbMetadata (input))
                return info;
            input.Position = 0;
            using (var reader = new PsbReader (input))
            {
                if (!reader.ParseNonEncrypted())
                    return info;
                var root = reader.GetRoot();
                if (null == root)
                    return info;
                info.Parsed = true;
                info.Scenario = root.Contains ("scenes");
                if (info.Scenario)
                {
                    var scenario_name = reader.GetRootKey<string> ("name");
                    if (!string.IsNullOrWhiteSpace (scenario_name))
                        m_candidates.AddName (scenario_name + ".scn");
                    ScanScenarioTree (root);
                }
                m_candidates.Walk (root, info.Scenario ? "scenes" : "psb");
            }
            return info;
        }

        void ScanScenarioTree (object value)
        {
            var dict = value as IDictionary;
            if (null != dict)
            {
                ScanScenarioItem (dict);
                var voice = GetString (dict, "voice");
                if (!string.IsNullOrWhiteSpace (voice))
                    m_candidates.AddAudioVariants (voice, false);

                var phonechat = GetValue (dict, "phonechat") as IList;
                if (null != phonechat)
                {
                    foreach (var item in phonechat)
                    {
                        var chat = item as IDictionary;
                        if (null == chat)
                            continue;
                        var icon = GetString (chat, "icon");
                        if (!string.IsNullOrWhiteSpace (icon))
                            m_candidates.AddName ("chaticon_" + icon + ".png");
                        var stamp = GetString (chat, "stamp");
                        m_candidates.AddName (
                            string.IsNullOrWhiteSpace (stamp)
                                ? "None.png" : stamp + ".png");
                    }
                }

                var loop_voices = GetValue (dict, "loopVoiceList") as IList;
                if (null != loop_voices)
                {
                    foreach (var item in loop_voices)
                    {
                        var loop_voice = item as IDictionary;
                        var name = null != loop_voice ? GetString (loop_voice, "voice") : null;
                        if (!string.IsNullOrWhiteSpace (name))
                            m_candidates.AddAudioVariants (name, false);
                    }
                }
                foreach (DictionaryEntry item in dict)
                    ScanScenarioTree (item.Value);
                return;
            }

            var list = value as IList;
            if (null == list)
                return;
            for (int i = 0; i < list.Count; ++i)
            {
                var directive = list[i] as string;
                if (("voice".Equals (directive, StringComparison.OrdinalIgnoreCase)
                     || "playvoice".Equals (directive, StringComparison.OrdinalIgnoreCase))
                    && i+1 < list.Count)
                {
                    var voice = list[i+1] as string;
                    if (!string.IsNullOrWhiteSpace (voice))
                        m_candidates.AddAudioVariants (voice, false);
                }
                ScanScenarioTree (list[i]);
            }
        }

        void ScanScenarioItem (IDictionary item)
        {
            var name = GetString (item, "name");
            var class_name = GetString (item, "class");
            var redraw = GetValue (item, "redraw") as IDictionary;
            var replay = GetValue (item, "replay") as IDictionary;

            if (IsOneOf (name, "bgm", "live", "liveout") && null != replay)
            {
                var file = GetString (replay, "filename");
                if (!string.IsNullOrWhiteSpace (file))
                    m_candidates.AddAudioVariants (file, true);
            }
            else if (IsOneOf (name, "lse", "lse2", "se", "se2") && null != replay)
            {
                var file = GetString (replay, "filename");
                if (!string.IsNullOrWhiteSpace (file))
                {
                    foreach (var part in file.Split ('|'))
                        m_candidates.AddAudioVariants (part, false);
                }
            }
            else if ("stage".Equals (name, StringComparison.OrdinalIgnoreCase)
                     && null != redraw)
            {
                var file = GetNestedString (redraw, "imageFile", "file");
                if (!string.IsNullOrWhiteSpace (file))
                {
                    m_candidates.AddName (file + ".png");
                    m_candidates.AddName ("bgthum_" + file + ".jpg");
                }
            }

            if (IsOneOf (class_name, "msgwin", "character"))
            {
                var stand = null != redraw
                    ? GetNestedString (redraw, "imageFile", "file")
                    : GetNestedString (item, "stand", "file");
                if (!string.IsNullOrWhiteSpace (stand)
                    && stand.EndsWith (".stand", StringComparison.OrdinalIgnoreCase))
                    m_candidates.AddName (stand);
                var clip = null != redraw ? GetNestedString (redraw, "clip", "image") : null;
                if (!string.IsNullOrWhiteSpace (clip))
                    m_candidates.AddName (clip + ".png");
            }
            else if ("event".Equals (class_name, StringComparison.OrdinalIgnoreCase)
                     && null != redraw)
            {
                if ("ev".Equals (name, StringComparison.OrdinalIgnoreCase))
                {
                    var file = GetNestedString (redraw, "imageFile", "file");
                    if (!string.IsNullOrWhiteSpace (file))
                        m_candidates.AddName (file + ".png");
                }
                else if ("bg_voice".Equals (name, StringComparison.OrdinalIgnoreCase))
                {
                    var storage = GetNestedString (redraw, "imageFile", "file", "storage");
                    if (!string.IsNullOrWhiteSpace (storage))
                        m_candidates.AddName (storage);
                }
            }
            else if ("phonechat".Equals (class_name, StringComparison.OrdinalIgnoreCase)
                     && "phonescreen".Equals (name, StringComparison.OrdinalIgnoreCase)
                     && null != redraw)
            {
                var file = GetNestedString (redraw, "imageFile", "file");
                if (!string.IsNullOrWhiteSpace (file))
                    m_candidates.AddName (file + ".tlg");
            }
            else if ("sdlayer".Equals (class_name, StringComparison.OrdinalIgnoreCase)
                     && null != redraw)
            {
                var file = GetNestedString (redraw, "imageFile", "file");
                if (!string.IsNullOrWhiteSpace (file))
                    m_candidates.AddName (file + ".png");
            }
            else if (IsOneOf (class_name, "event2", "stage2") && null != redraw)
            {
                var file = GetNestedString (redraw, "clip", "image")
                    ?? GetNestedString (redraw, "imageFile", "file");
                if (!string.IsNullOrWhiteSpace (file))
                    m_candidates.AddName (file + ".png");
            }
        }

        static bool IsOneOf (string value, params string[] candidates)
        {
            return !string.IsNullOrEmpty (value)
                && candidates.Any (x => x.Equals (value, StringComparison.OrdinalIgnoreCase));
        }

        static object GetValue (IDictionary dict, string key)
        {
            if (null == dict || string.IsNullOrEmpty (key))
                return null;
            if (dict.Contains (key))
                return dict[key];
            foreach (DictionaryEntry item in dict)
            {
                if (key.Equals (item.Key as string, StringComparison.OrdinalIgnoreCase))
                    return item.Value;
            }
            return null;
        }

        static string GetString (IDictionary dict, string key)
        {
            return GetValue (dict, key) as string;
        }

        static string GetNestedString (IDictionary dict, params string[] keys)
        {
            object value = dict;
            foreach (var key in keys)
            {
                var current = value as IDictionary;
                if (null == current)
                    return null;
                value = GetValue (current, key);
            }
            return value as string;
        }

        static bool HasReasonablePsbMetadata (IBinaryStream input)
        {
            try
            {
                if (input.Length < 0x24)
                    return false;
                input.Position = 0x20;
                var chunk_data = input.ReadInt32();
                return chunk_data >= 0x28 && chunk_data <= MaxPsbMetadataLength
                    && chunk_data <= input.Length;
            }
            catch
            {
                return false;
            }
        }

        static bool TryDecompressMdf (IBinaryStream input, out byte[] data)
        {
            data = null;
            try
            {
                if (input.Length < 9 || input.Length > int.MaxValue)
                    return false;
                input.Position = 4;
                int unpacked_size = input.ReadInt32();
                if (unpacked_size <= 0 || unpacked_size > MaxPsbMetadataLength)
                    return false;
                using (var packed = new StreamRegion (input.AsStream, 8, input.Length-8, true))
                using (var zlib = new ZLibStream (packed, CompressionMode.Decompress))
                {
                    data = new byte[unpacked_size];
                    int offset = 0;
                    while (offset < data.Length)
                    {
                        int read = zlib.Read (data, offset, data.Length-offset);
                        if (0 == read)
                            break;
                        offset += read;
                    }
                    if (offset != data.Length)
                    {
                        data = null;
                        return false;
                    }
                }
                return true;
            }
            catch
            {
                data = null;
                return false;
            }
        }

        void ScanText (string source_name, string text)
        {
            m_candidates.ProcessString (text, "text");
            foreach (Match match in AssignmentRe.Matches (text))
            {
                var value = match.Groups["quoted"].Success
                    ? match.Groups["quoted"].Value
                    : match.Groups["bare"].Value;
                m_candidates.ProcessString (value, match.Groups["key"].Value);
            }

            var base_name = Path.GetFileName (source_name ?? string.Empty);
            var lower_name = base_name.ToLowerInvariant();
            bool is_base_stage = "base.stage" == lower_name
                || (text.IndexOf ("TIME", StringComparison.Ordinal) >= 0
                    && text.IndexOf ("SEASON", StringComparison.Ordinal) >= 0
                    && text.IndexOf ("stages", StringComparison.OrdinalIgnoreCase) >= 0);
            if (is_base_stage)
                ScanBaseStage (text);

            if (lower_name.EndsWith (".stand", StringComparison.Ordinal)
                || StandFileRe.IsMatch (text))
                ScanStand (text);

            if ("replay.ks" == lower_name || MovieRe.IsMatch (text))
                ScanMovies (text);

            bool named_csv = lower_name.EndsWith (".csv", StringComparison.Ordinal);
            bool looks_csv = named_csv || LooksLikeCsv (text);
            if (looks_csv)
                ScanCsv (lower_name, text);
        }

        void ScanBaseStage (string text)
        {
            JObject root;
            if (TryParseBaseStage (text, out root))
            {
                var parsed_times = ReadPrefixes (root["times"]);
                var parsed_seasons = ReadPrefixes (root["seasons"]);
                parsed_times.Add (string.Empty);
                parsed_seasons.Add (string.Empty);
                var stages = root["stages"] as JObject;
                if (null != stages)
                {
                    foreach (var stage in stages.Properties())
                    {
                        var image = Convert.ToString (stage.Value["image"],
                                                      CultureInfo.InvariantCulture);
                        AddStageImages (image, parsed_times, parsed_seasons);
                    }
                    return;
                }
            }

            var time_section = SliceSection (text, "times", "seasons", "stages");
            var season_section = SliceSection (text, "seasons", "stages");
            var stage_section = SliceSection (text, "stages");
            var times = FindAssignedValues (time_section, "prefix");
            var seasons = FindAssignedValues (season_section, "prefix");
            var images = FindAssignedValues (stage_section, "image");
            if (0 == images.Count)
                images = FindAssignedValues (text, "image");
            times.Add (string.Empty);
            seasons.Add (string.Empty);

            foreach (var image_template in images.Take (512))
                AddStageImages (image_template, times, seasons);
        }

        void AddStageImages (string image_template, IEnumerable<string> times,
                             IEnumerable<string> seasons)
        {
            if (string.IsNullOrWhiteSpace (image_template))
                return;
            int combinations = 0;
            foreach (var time in times.Take (64))
            {
                foreach (var season in seasons.Take (64))
                {
                    if (++combinations > 4096)
                        break;
                    if (null == time || null == season)
                        continue;
                    var image = image_template.Replace ("TIME", time).Replace ("SEASON", season);
                    m_candidates.AddName (image + ".png");
                    m_candidates.AddName ("bgthum_" + image + ".jpg");
                }
                if (combinations > 4096)
                    break;
            }
        }

        static HashSet<string> ReadPrefixes (JToken value)
        {
            var result = new HashSet<string> (StringComparer.Ordinal);
            var dict = value as JObject;
            if (null == dict)
                return result;
            foreach (var item in dict.Properties())
            {
                var prefix = item.Value["prefix"];
                if (null != prefix && JTokenType.Null != prefix.Type)
                    result.Add (Convert.ToString (prefix, CultureInfo.InvariantCulture));
            }
            return result;
        }

        static bool TryParseBaseStage (string text, out JObject root)
        {
            root = null;
            try
            {
                var converted = ConvertBaseStageToJson (text);
                root = JObject.Parse (converted);
                return null != root;
            }
            catch
            {
                root = null;
                return false;
            }
        }

        static string ConvertBaseStageToJson (string text)
        {
            var output = new StringBuilder (text.Length);
            var stack = new Stack<bool>();
            bool quoted = false;
            bool escaped = false;
            for (int i = 0; i < text.Length; ++i)
            {
                char c = text[i];
                if (quoted)
                {
                    output.Append (c);
                    if (escaped)
                        escaped = false;
                    else if ('\\' == c)
                        escaped = true;
                    else if ('"' == c)
                        quoted = false;
                    continue;
                }
                if ('"' == c)
                {
                    quoted = true;
                    output.Append (c);
                    continue;
                }
                if ('/' == c && i+1 < text.Length && '/' == text[i+1])
                {
                    while (i < text.Length && '\r' != text[i] && '\n' != text[i])
                        ++i;
                    if (i < text.Length)
                        output.Append (text[i]);
                    continue;
                }
                if ('%' == c && i+1 < text.Length && '[' == text[i+1])
                {
                    output.Append ('{');
                    stack.Push (true);
                    ++i;
                    continue;
                }
                if ('[' == c)
                {
                    output.Append ('[');
                    stack.Push (false);
                    continue;
                }
                if (']' == c)
                {
                    if (0 == stack.Count)
                        throw new FormatException ("Unmatched base.stage bracket.");
                    output.Append (stack.Pop() ? '}' : ']');
                    continue;
                }
                if ('=' == c && i+1 < text.Length && '>' == text[i+1])
                {
                    output.Append (':');
                    ++i;
                    continue;
                }
                if (IsWordAt (text, i, "void"))
                {
                    output.Append ("null");
                    i += 3;
                    continue;
                }
                output.Append (c);
            }
            if (0 != stack.Count || quoted)
                throw new FormatException ("Incomplete base.stage expression.");
            var converted = Regex.Replace (
                output.ToString(), @"(:\s*)([A-Za-z_]\w*)\b",
                match => match.Groups[1].Value + "\"" + match.Groups[2].Value + "\"");
            return Regex.Replace (
                converted, @"(?<=[{,])(\s*)([A-Za-z_]\w*)(\s*):",
                match => match.Groups[1].Value + "\"" + match.Groups[2].Value
                    + "\"" + match.Groups[3].Value + ":");
        }

        static bool IsWordAt (string text, int index, string word)
        {
            if (index < 0 || index + word.Length > text.Length
                || 0 != string.CompareOrdinal (text, index, word, 0, word.Length))
                return false;
            bool left = 0 == index || !IsIdentifierChar (text[index-1]);
            int end = index + word.Length;
            bool right = end == text.Length || !IsIdentifierChar (text[end]);
            return left && right;
        }

        static string SliceSection (string text, string start_name, params string[] end_names)
        {
            int start = IndexOfWord (text, start_name, 0);
            if (start < 0)
                return string.Empty;
            int end = text.Length;
            foreach (var end_name in end_names)
            {
                int candidate = IndexOfWord (text, end_name, start + start_name.Length);
                if (candidate >= 0)
                    end = Math.Min (end, candidate);
            }
            return text.Substring (start, end-start);
        }

        static int IndexOfWord (string text, string word, int start)
        {
            for (;;)
            {
                int index = text.IndexOf (word, start, StringComparison.OrdinalIgnoreCase);
                if (index < 0)
                    return -1;
                bool left = 0 == index || !IsIdentifierChar (text[index-1]);
                int right_index = index + word.Length;
                bool right = right_index >= text.Length || !IsIdentifierChar (text[right_index]);
                if (left && right)
                    return index;
                start = index + word.Length;
            }
        }

        static bool IsIdentifierChar (char c)
        {
            return char.IsLetterOrDigit (c) || '_' == c;
        }

        static HashSet<string> FindAssignedValues (string text, string key)
        {
            var values = new HashSet<string> (StringComparer.Ordinal);
            foreach (Match match in BaseStageAssignmentRe.Matches (text ?? string.Empty))
            {
                if (key.Equals (match.Groups["key"].Value, StringComparison.OrdinalIgnoreCase))
                    values.Add (match.Groups["value"].Value);
            }
            return values;
        }

        void ScanStand (string text)
        {
            foreach (Match match in StandFileRe.Matches (text))
            {
                var name = match.Groups["name"].Value;
                m_candidates.AddName (name + ".pbd");
                m_candidates.AddName (name + ".sinfo");
                m_candidates.AddName (name + "_0.pbd");
                m_candidates.AddName (name + "_0.sinfo");
            }
        }

        void ScanMovies (string text)
        {
            var names = new HashSet<string> (StringComparer.OrdinalIgnoreCase) { "op" };
            foreach (Match match in MovieRe.Matches (text))
                names.Add (match.Groups["name"].Value);
            var extensions = new[] { ".mp4", ".wmv" };
            var languages = new[] { "en_", "cn_", "tw_" };
            foreach (var name in names)
            {
                var forms = new[] { name, name + "1080", name + "_1080", name + "720p" };
                foreach (var form in forms)
                {
                    foreach (var extension in extensions)
                    {
                        m_candidates.AddName (form + extension);
                        foreach (var language in languages)
                            m_candidates.AddName (language + form + extension);
                    }
                }
            }
        }

        void ScanCsv (string lower_name, string text)
        {
            var rows = ParseCsv (text);
            bool cglist = lower_name.IndexOf ("cglist", StringComparison.Ordinal) >= 0;
            bool soundlist = lower_name.IndexOf ("soundlist", StringComparison.Ordinal) >= 0;
            bool charvoice = lower_name.IndexOf ("charvoice", StringComparison.Ordinal) >= 0;
            bool image_diff = lower_name.IndexOf ("imagediffmap", StringComparison.Ordinal) >= 0;
            bool savelist = lower_name.IndexOf ("savelist", StringComparison.Ordinal) >= 0;
            bool scenelist = lower_name.IndexOf ("scenelist", StringComparison.Ordinal) >= 0;
            bool bgv = Path.GetFileNameWithoutExtension (lower_name).StartsWith (
                "bgv", StringComparison.Ordinal);
            if (!charvoice && text.IndexOf ("DEFAULT", StringComparison.OrdinalIgnoreCase) >= 0)
                charvoice = true;

            foreach (var row in rows)
            {
                foreach (var cell in row)
                    m_candidates.ProcessString (cell, "text");
                if (0 == row.Count)
                    continue;
                var first = CleanCell (row[0]);
                if (string.IsNullOrEmpty (first) || first.StartsWith ("#", StringComparison.Ordinal))
                    continue;

                bool inferred_cg = first.StartsWith ("thum_", StringComparison.OrdinalIgnoreCase);
                if (cglist || inferred_cg)
                    AddCgListRow (row);

                bool inferred_sound = first.StartsWith ("bgm", StringComparison.OrdinalIgnoreCase)
                    || first.StartsWith ("song", StringComparison.OrdinalIgnoreCase);
                if (soundlist || inferred_sound)
                    m_candidates.AddAudioVariants (first, true);

                if (charvoice && row.Count > 1)
                {
                    var voice = CleanCell (row[1]);
                    int separator = voice.IndexOf ('_');
                    if (separator > 0)
                        m_candidates.AddSystemVoicePrefix (voice.Substring (0, separator));
                }

                bool inferred_diff = row.Count > 1
                    && (row[1].IndexOf ('|') >= 0
                        || row[1].StartsWith ("ev", StringComparison.OrdinalIgnoreCase));
                if (image_diff || inferred_diff)
                    AddImageDiffRow (row);

                if (savelist || first.StartsWith ("savethum_", StringComparison.OrdinalIgnoreCase))
                    AddSaveListRow (first);

                if (scenelist || first.IndexOf ('|') >= 0
                    || first.StartsWith ("movthum_", StringComparison.OrdinalIgnoreCase))
                    AddSceneListRow (first);

                if ((bgv || LooksLikeBgvRow (row)) && row.Count > 2)
                    m_candidates.AddAudioVariants (CleanCell (row[2]), false);
            }
        }

        void AddCgListRow (IList<string> row)
        {
            var thumbnail = CleanCell (row[0]).ToLowerInvariant();
            if (string.IsNullOrEmpty (thumbnail) || thumbnail.IndexOf (':') >= 0)
                return;
            AddCensoredImageSet (thumbnail);
            if (thumbnail.StartsWith ("thum_", StringComparison.Ordinal))
            {
                m_candidates.AddName ("save" + thumbnail + ".jpg");
                m_candidates.AddName ("save" + thumbnail + ".png");
                m_candidates.AddName ("save" + thumbnail + ".psb");
            }

            var cg_name = thumbnail.Replace ("thum_", string.Empty);
            if (thumbnail.StartsWith ("thum_ev", StringComparison.Ordinal))
            {
                foreach (var raw_diff in row.Skip (1))
                {
                    foreach (var part in raw_diff.Replace ("*", string.Empty).Split ('|'))
                    {
                        var diff = CleanCell (part);
                        if (!diff.StartsWith (cg_name, StringComparison.Ordinal))
                            continue;
                        for (int length = cg_name.Length; length <= diff.Length; ++length)
                            AddEventDiffSet (diff.Substring (0, length));
                    }
                }
            }
            else if (thumbnail.StartsWith ("thum_sd", StringComparison.Ordinal))
            {
                var sd_name = thumbnail.Substring (5);
                m_candidates.AddName (sd_name + ".mtn");
                m_candidates.AddName (sd_name + ".psb");
                foreach (var raw_diff in row.Skip (1))
                {
                    var diff = CleanCell (raw_diff);
                    if (string.IsNullOrEmpty (diff))
                        continue;
                    m_candidates.AddName (diff + ".jpg");
                    m_candidates.AddName (diff + ".png");
                    m_candidates.AddName (diff + ".asd");
                    m_candidates.AddName (diff + ".psb");
                }
            }
        }

        void AddCensoredImageSet (string name)
        {
            if (string.IsNullOrWhiteSpace (name))
                return;
            m_candidates.AddName (name + ".jpg");
            m_candidates.AddName (name + ".png");
            m_candidates.AddName (name + "_censored.jpg");
            m_candidates.AddName (name + "_censored.png");
            m_candidates.AddName (name + ".psb");
            m_candidates.AddName (name + "_censored.psb");
        }

        void AddEventDiffSet (string name)
        {
            m_candidates.AddName (name + ".pimg");
            m_candidates.AddName (name + "_censored.pimg");
            m_candidates.AddName ("thum_" + name + ".png");
            m_candidates.AddName ("thum_" + name + ".jpg");
            m_candidates.AddName ("thum_" + name + "_censored.png");
            m_candidates.AddName ("thum_" + name + "_censored.jpg");
            m_candidates.AddName ("savethum_" + name + ".png");
            m_candidates.AddName ("savethum_" + name + ".jpg");
            m_candidates.AddName (name + ".psb");
            m_candidates.AddName (name + "_censored.psb");
            m_candidates.AddName ("thum_" + name + ".psb");
            m_candidates.AddName ("thum_" + name + "_censored.psb");
            m_candidates.AddName ("savethum_" + name + ".psb");
            m_candidates.AddName (name + ".png");
        }

        void AddImageDiffRow (IList<string> row)
        {
            if (row.Count < 2)
                return;
            var value = CleanCell (row[1]);
            if (string.IsNullOrEmpty (value))
                return;
            int extension = value.IndexOf ('.');
            if (extension >= 0)
            {
                var names = value.Substring (0, extension).Split ('|');
                var suffix = value.Substring (extension);
                foreach (var name in names)
                    m_candidates.AddName (name + suffix);
                return;
            }
            foreach (var name in value.Split ('|'))
            {
                if (string.IsNullOrWhiteSpace (name))
                    continue;
                AddImageDiffStem (name);
            }
            if (value.IndexOf ('|') >= 0)
                AddImageDiffStem (value);
        }

        void AddImageDiffStem (string name)
        {
            m_candidates.AddName (name + ".pimg");
            m_candidates.AddName (name + "_censored.pimg");
            m_candidates.AddName ("savethum_" + name + ".jpg");
            m_candidates.AddName ("savethum_" + name + ".png");
            m_candidates.AddName (name + ".psb");
            m_candidates.AddName (name + "_censored.psb");
            m_candidates.AddName ("savethum_" + name + ".psb");
        }

        void AddSaveListRow (string name)
        {
            var thumbnail = name.Replace ("savethum_", "thum_");
            m_candidates.AddName (name + ".jpg");
            m_candidates.AddName (name + ".png");
            m_candidates.AddName (name + ".psb");
            m_candidates.AddName (thumbnail + ".jpg");
            m_candidates.AddName (thumbnail + ".png");
            m_candidates.AddName (thumbnail + ".psb");
        }

        void AddSceneListRow (string value)
        {
            if (value.IndexOf (':') >= 0)
                return;
            foreach (var name in value.Split ('|'))
                AddCensoredImageSet (CleanCell (name));
        }

        static bool LooksLikeBgvRow (IList<string> row)
        {
            if (row.Count < 3)
                return false;
            var value = CleanCell (row[2]);
            return VoiceStemRe.IsMatch (value)
                && (value.StartsWith ("bgv", StringComparison.OrdinalIgnoreCase)
                    || value.Count (c => '_' == c) >= 2);
        }

        static bool LooksLikeCsv (string text)
        {
            int newline = text.IndexOfAny (new[] { '\r', '\n' });
            if (newline < 0)
                return false;
            int separator = text.IndexOfAny (new[] { ',', '\t' });
            return separator >= 0 && separator < Math.Min (text.Length, newline + 1024);
        }

        internal static List<List<string>> ParseCsv (string text)
        {
            var rows = new List<List<string>>();
            var row = new List<string>();
            var value = new StringBuilder();
            bool quoted = false;
            for (int i = 0; i < text.Length; ++i)
            {
                char c = text[i];
                if ('"' == c)
                {
                    if (quoted && i+1 < text.Length && '"' == text[i+1])
                    {
                        value.Append ('"');
                        ++i;
                    }
                    else
                        quoted = !quoted;
                    continue;
                }
                if (!quoted && (',' == c || '\t' == c))
                {
                    row.Add (value.ToString());
                    value.Length = 0;
                    continue;
                }
                if (!quoted && ('\r' == c || '\n' == c))
                {
                    if ('\r' == c && i+1 < text.Length && '\n' == text[i+1])
                        ++i;
                    row.Add (value.ToString());
                    value.Length = 0;
                    rows.Add (row);
                    row = new List<string>();
                    if (rows.Count >= 100000)
                        break;
                    continue;
                }
                value.Append (c);
            }
            if (value.Length > 0 || row.Count > 0)
            {
                row.Add (value.ToString());
                rows.Add (row);
            }
            return rows;
        }

        internal static string CleanCell (string value)
        {
            return (value ?? string.Empty).Trim().TrimStart ('\uFEFF');
        }

        void ScanPbdObject (string source_name, string name_hash, object root)
        {
            if (IsUsefulPlainName (source_name)
                && source_name.EndsWith (".pbd", StringComparison.OrdinalIgnoreCase))
            {
                AddPbdDerivedNames (source_name, root);
            }
            else if (!string.IsNullOrEmpty (name_hash))
            {
                m_deferred_pbd.Add (new DeferredPbd {
                    SourceName = source_name,
                    NameHash = name_hash,
                    Root = root,
                });
            }
        }

        void AddPbdDerivedNames (string source_name, object root)
        {
            var stem = Path.GetFileNameWithoutExtension (source_name);
            if (string.IsNullOrEmpty (stem))
                return;
            bool character_thumbnail = stem.IndexOf ("chthum", StringComparison.OrdinalIgnoreCase) >= 0;
            WalkPbd (root, stem, character_thumbnail);
        }

        void WalkPbd (object value, string stem, bool character_thumbnail)
        {
            var dict = value as IDictionary;
            if (null != dict)
            {
                foreach (DictionaryEntry item in dict)
                {
                    var key = item.Key as string;
                    if ("layer_id".Equals (key, StringComparison.OrdinalIgnoreCase)
                        && null != item.Value)
                    {
                        var layer = Convert.ToString (item.Value, CultureInfo.InvariantCulture);
                        if (!string.IsNullOrWhiteSpace (layer))
                            m_candidates.AddName (stem + "_" + layer + ".tlg");
                    }
                    if (character_thumbnail)
                    {
                        var image = item.Value as string;
                        if (!string.IsNullOrWhiteSpace (image))
                        {
                            if (Path.HasExtension (image))
                                m_candidates.AddName (image);
                            else
                                m_candidates.AddName (image + ".png");
                        }
                    }
                    WalkPbd (item.Value, stem, character_thumbnail);
                }
                return;
            }
            var list = value as IList;
            if (null == list)
                return;
            foreach (var item in list)
                WalkPbd (item, stem, character_thumbnail);
        }

        static string TryDecodeText (byte[] data)
        {
            if (null == data || 0 == data.Length)
                return null;
            Encoding encoding;
            int offset = 0;
            if (data.Length >= 2 && 0xFF == data[0] && 0xFE == data[1])
            {
                encoding = Encoding.Unicode;
                offset = 2;
            }
            else if (data.Length >= 2 && 0xFE == data[0] && 0xFF == data[1])
            {
                encoding = Encoding.BigEndianUnicode;
                offset = 2;
            }
            else if (data.Length >= 3 && 0xEF == data[0] && 0xBB == data[1] && 0xBF == data[2])
            {
                encoding = new UTF8Encoding (false, true);
                offset = 3;
            }
            else
            {
                int sample = Math.Min (data.Length, 4096);
                int even_zero = 0;
                int odd_zero = 0;
                for (int i = 0; i < sample; ++i)
                {
                    if (0 == data[i])
                    {
                        if (0 == (i & 1))
                            ++even_zero;
                        else
                            ++odd_zero;
                    }
                }
                if (odd_zero > sample / 8 && odd_zero > even_zero * 3)
                    encoding = Encoding.Unicode;
                else if (even_zero > sample / 8 && even_zero > odd_zero * 3)
                    encoding = Encoding.BigEndianUnicode;
                else
                    encoding = new UTF8Encoding (false, true);
            }

            string text;
            try
            {
                text = encoding.GetString (data, offset, data.Length-offset);
            }
            catch (DecoderFallbackException)
            {
                try
                {
                    text = Encoding.GetEncoding (932,
                        EncoderFallback.ExceptionFallback,
                        DecoderFallback.ExceptionFallback).GetString (data, offset, data.Length-offset);
                }
                catch
                {
                    return null;
                }
            }
            text = text.TrimStart ('\uFEFF');
            if (!LooksLikeText (text))
                return null;
            return text;
        }

        static bool LooksLikeText (string text)
        {
            if (string.IsNullOrWhiteSpace (text))
                return false;
            int sample = Math.Min (text.Length, 4096);
            int controls = 0;
            for (int i = 0; i < sample; ++i)
            {
                char c = text[i];
                if (char.IsControl (c) && '\r' != c && '\n' != c && '\t' != c)
                    ++controls;
            }
            return controls * 50 <= sample;
        }

        internal static bool IsUsefulPlainName (string name)
        {
            if (string.IsNullOrWhiteSpace (name) || !Path.HasExtension (name))
                return false;
            return IsUsefulPlainPath (name);
        }

        internal static bool IsUsefulPlainPath (string path)
        {
            if (string.IsNullOrWhiteSpace (path))
                return false;
            var parts = path.Replace ('\\', '/').Split (
                new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            return parts.Length > 0 && parts.All (part => !ObfuscatedNameRe.IsMatch (part));
        }

        sealed class DeferredPbd
        {
            public string SourceName;
            public string NameHash;
            public object Root;
        }

        sealed class DeferredText
        {
            public string SourceName;
            public string NameHash;
            public string Text;
            public bool Resolved;
        }

        static class TjsNs0Reader
        {
            public static bool TryRead (byte[] data, out object root)
            {
                root = null;
                try
                {
                    if (null == data || data.Length < 22
                        || data[0] != 'T' || data[1] != 'J' || data[2] != 'S' || data[3] != '/'
                        || (data[4] != 'n' && data[4] != '4')
                        || data[5] != 's' || data[6] != '0' || 0 != data[7])
                        return false;
                    uint seed = ReadUInt32 (data, 8);
                    ushort crypt = ReadUInt16 (data, 12);
                    int iv_length = ReadUInt16 (data, 14);
                    int payload_offset = 16+iv_length;
                    if (payload_offset > data.Length || payload_offset == data.Length)
                        return false;
                    var iv = new byte[iv_length];
                    if (iv_length > 0)
                        Buffer.BlockCopy (data, 16, iv, 0, iv.Length);
                    var payload = new byte[data.Length-payload_offset];
                    Buffer.BlockCopy (data, payload_offset, payload, 0, payload.Length);
                    if (0 != crypt)
                    {
                        byte[] decrypted;
                        if (!TjsNs0Crypt.TryDecrypt (payload, seed, crypt, iv, out decrypted))
                            return false;
                        payload = decrypted;
                    }

                    byte[] object_data;
                    if ('4' == data[4])
                    {
                        if (!TryDecodeLz4Stream (payload, out object_data)
                            && !TryDecodeSizePrefixedLz4 (payload, out object_data))
                            return false;
                    }
                    else
                        object_data = payload;
                    var reader = new ValueReader (object_data, seed);
                    root = reader.ReadValue (0);
                    return reader.VerifyChecksum() && null != root;
                }
                catch
                {
                    root = null;
                    return false;
                }
            }

            static bool TryDecodeLz4Stream (byte[] data, out byte[] output_data)
            {
                output_data = null;
                try
                {
                    const int dictionary_capacity = 0x10000;
                    var dictionary = new byte[dictionary_capacity];
                    int dictionary_length = 0;
                    int position = 0;
                    using (var output = new MemoryStream())
                    {
                        while (position < data.Length)
                        {
                            if (position > data.Length-2)
                                return false;
                            int packed_length = ReadUInt16 (data, position);
                            position += 2;
                            if (packed_length <= 0 || packed_length > data.Length-position)
                                return false;

                            var block = new byte[dictionary_capacity];
                            int decoded;
                            if (dictionary_length > 0)
                            {
                                decoded = LZ4Codec.Decode (
                                    data, position, packed_length,
                                    block, 0, block.Length,
                                    dictionary, 0, dictionary_length);
                            }
                            else
                            {
                                decoded = LZ4Codec.Decode (
                                    data, position, packed_length,
                                    block, 0, block.Length);
                            }
                            if (decoded <= 0 || output.Length > MaxTjsObjectLength-decoded)
                                return false;
                            output.Write (block, 0, decoded);
                            UpdateDictionary (
                                dictionary, ref dictionary_length, block, decoded);
                            position += packed_length;
                        }
                        if (output.Length < 6)
                            return false;
                        output_data = output.ToArray();
                        return true;
                    }
                }
                catch
                {
                    output_data = null;
                    return false;
                }
            }

            static void UpdateDictionary (
                byte[] dictionary, ref int dictionary_length, byte[] block, int block_length)
            {
                if (block_length >= dictionary.Length)
                {
                    Buffer.BlockCopy (
                        block, block_length-dictionary.Length,
                        dictionary, 0, dictionary.Length);
                    dictionary_length = dictionary.Length;
                    return;
                }

                int retained = Math.Min (dictionary_length, dictionary.Length-block_length);
                if (retained > 0)
                {
                    Buffer.BlockCopy (
                        dictionary, dictionary_length-retained,
                        dictionary, 0, retained);
                }
                Buffer.BlockCopy (block, 0, dictionary, retained, block_length);
                dictionary_length = retained+block_length;
            }

            static bool TryDecodeSizePrefixedLz4 (byte[] data, out byte[] output_data)
            {
                output_data = null;
                try
                {
                    if (data.Length < 6)
                        return false;
                    int unpacked_size = ReadInt32 (data, 0);
                    if (unpacked_size < 6 || unpacked_size > MaxTjsObjectLength)
                        return false;
                    int packed_length = data.Length-4;
                    if (packed_length <= 0)
                        return false;
                    output_data = new byte[unpacked_size];
                    int decoded = LZ4Codec.Decode (
                        data, 4, packed_length, output_data, 0, output_data.Length);
                    if (decoded != output_data.Length)
                    {
                        output_data = null;
                        return false;
                    }
                    return true;
                }
                catch
                {
                    output_data = null;
                    return false;
                }
            }

            static ushort ReadUInt16 (byte[] data, int offset)
            {
                return (ushort)(data[offset] | data[offset+1] << 8);
            }

            static int ReadInt32 (byte[] data, int offset)
            {
                return data[offset] | data[offset+1] << 8
                    | data[offset+2] << 16 | data[offset+3] << 24;
            }

            static uint ReadUInt32 (byte[] data, int offset)
            {
                return (uint)(data[offset] | data[offset+1] << 8
                    | data[offset+2] << 16 | data[offset+3] << 24);
            }

            sealed class ValueReader
            {
                readonly byte[] m_data;
                uint m_seed;
                int m_position;

                public ValueReader (byte[] data, uint seed)
                {
                    m_data = data;
                    m_seed = (seed ^ seed >> 24) & 0x00FFFFFFu;
                }

                public object ReadValue (int depth)
                {
                    if (depth > 128)
                        throw new InvalidFormatException ("TJS/ns0 object nesting is too deep.");
                    ushort type = ReadUInt16();
                    byte type_code = (byte)(type & 0xFF);
                    byte actual_check = (byte)(type >> 8);
                    if (actual_check != GetCheckByte (type_code))
                        throw new InvalidFormatException ("TJS/ns0 value check failed.");
                    switch (type_code)
                    {
                    case 0:
                        return null;
                    case 2:
                        return ReadString();
                    case 4:
                        return ReadInt64();
                    case 5:
                        return BitConverter.Int64BitsToDouble (ReadInt64());
                    case 0x81:
                        {
                            int count = ReadCount();
                            var list = new ArrayList (count);
                            for (int i = 0; i < count; ++i)
                                list.Add (ReadValue (depth+1));
                            return list;
                        }
                    case 0xC1:
                        {
                            int count = ReadCount();
                            var dict = new Dictionary<string, object> (count);
                            for (int i = 0; i < count; ++i)
                                dict[ReadString()] = ReadValue (depth+1);
                            return dict;
                        }
                    default:
                        throw new InvalidFormatException ("Unsupported TJS/ns0 value type.");
                    }
                }

                public bool VerifyChecksum ()
                {
                    if (m_position != m_data.Length-4)
                        return false;
                    uint expected = GetFinalChecksum();
                    return expected == ReadUInt32() && m_position == m_data.Length;
                }

                string ReadString ()
                {
                    int length = ReadCount();
                    int byte_length = checked (length * 2);
                    Require (byte_length);
                    var value = Encoding.Unicode.GetString (m_data, m_position, byte_length);
                    m_position += byte_length;
                    return value;
                }

                byte GetCheckByte (byte type_code)
                {
                    var seed = BitConverter.GetBytes (m_seed);
                    if (0 != type_code)
                    {
                        CalculateRound (seed);
                        m_seed = BitConverter.ToUInt32 (seed, 0);
                    }
                    return seed[2];
                }

                uint GetFinalChecksum ()
                {
                    var seed = BitConverter.GetBytes (m_seed);
                    CalculateRound (seed);
                    CalculateRound (seed);
                    CalculateRound (seed);
                    byte tmp = seed[0];
                    seed[0] = seed[2];
                    seed[2] = tmp;
                    return BitConverter.ToUInt32 (seed, 0);
                }

                static void CalculateRound (byte[] seed)
                {
                    byte a = (byte)(seed[0] ^ (byte)(seed[0] * 2));
                    byte b = a;
                    b >>= 2;
                    b ^= seed[2];
                    b >>= 3;
                    b ^= seed[2];
                    b ^= a;
                    seed[0] = seed[1];
                    seed[1] = seed[2];
                    seed[2] = b;
                }

                int ReadCount ()
                {
                    uint value = ReadUInt32();
                    if (value > 1000000)
                        throw new InvalidFormatException ("Invalid TJS/ns0 collection size.");
                    return (int)value;
                }

                ushort ReadUInt16 ()
                {
                    Require (2);
                    ushort value = (ushort)(m_data[m_position] | m_data[m_position+1] << 8);
                    m_position += 2;
                    return value;
                }

                uint ReadUInt32 ()
                {
                    Require (4);
                    uint value = (uint)(m_data[m_position] | m_data[m_position+1] << 8
                        | m_data[m_position+2] << 16 | m_data[m_position+3] << 24);
                    m_position += 4;
                    return value;
                }

                long ReadInt64 ()
                {
                    Require (8);
                    long value = BitConverter.ToInt64 (m_data, m_position);
                    m_position += 8;
                    return value;
                }

                void Require (int count)
                {
                    if (count < 0 || m_position > m_data.Length-count)
                        throw new EndOfStreamException();
                }
            }
        }
    }
}
