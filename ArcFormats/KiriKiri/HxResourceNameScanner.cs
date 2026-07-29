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
                }
                m_candidates.Walk (root, info.Scenario ? "scenes" : "psb");
            }
            return info;
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
            {
                int combinations = 0;
                foreach (var time in times.Take (64))
                {
                    foreach (var season in seasons.Take (64))
                    {
                        if (++combinations > 4096)
                            break;
                        var image = image_template.Replace ("TIME", time).Replace ("SEASON", season);
                        m_candidates.AddName (image + ".png");
                        m_candidates.AddName ("bgthum_" + image + ".jpg");
                    }
                    if (combinations > 4096)
                        break;
                }
            }
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
                m_candidates.AddName (name + ".pimg");
                m_candidates.AddName (name + "_censored.pimg");
                m_candidates.AddName ("savethum_" + name + ".jpg");
                m_candidates.AddName ("savethum_" + name + ".png");
                m_candidates.AddName (name + ".psb");
                m_candidates.AddName (name + "_censored.psb");
                m_candidates.AddName ("savethum_" + name + ".psb");
            }
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

        static List<List<string>> ParseCsv (string text)
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

        static string CleanCell (string value)
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
                    if (null == data || data.Length < 24
                        || data[0] != 'T' || data[1] != 'J' || data[2] != 'S' || data[3] != '/'
                        || (data[4] != 'n' && data[4] != '4')
                        || data[5] != 's' || data[6] != '0' || 0 != data[7])
                        return false;
                    if (ReadUInt16 (data, 12) != 0 || ReadUInt16 (data, 14) != 0)
                        return false;

                    byte[] object_data;
                    if ('4' == data[4])
                    {
                        int unpacked_size = ReadInt32 (data, 16);
                        if (unpacked_size <= 0 || unpacked_size > MaxTjsObjectLength)
                            return false;
                        object_data = new byte[unpacked_size];
                        int packed_length = data.Length - 24;
                        if (packed_length <= 0)
                            return false;
                        int decoded = LZ4Codec.Decode (
                            data, 20, packed_length, object_data, 0, object_data.Length);
                        if (decoded != object_data.Length)
                            return false;
                    }
                    else
                    {
                        object_data = new byte[data.Length-20];
                        Buffer.BlockCopy (data, 16, object_data, 0, object_data.Length);
                    }
                    var reader = new ValueReader (object_data);
                    root = reader.ReadValue (0);
                    return null != root;
                }
                catch
                {
                    root = null;
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

            sealed class ValueReader
            {
                readonly byte[] m_data;
                int m_position;

                public ValueReader (byte[] data)
                {
                    m_data = data;
                }

                public object ReadValue (int depth)
                {
                    if (depth > 128)
                        throw new InvalidFormatException ("TJS/ns0 object nesting is too deep.");
                    ushort type = ReadUInt16();
                    switch (type & 0xFF)
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

                string ReadString ()
                {
                    int length = ReadCount();
                    int byte_length = checked (length * 2);
                    Require (byte_length);
                    var value = Encoding.Unicode.GetString (m_data, m_position, byte_length);
                    m_position += byte_length;
                    return value;
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
