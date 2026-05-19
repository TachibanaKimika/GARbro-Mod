//! \file       ScriptWhale.cs
//! \date       Mon May 18 2026
//! \brief      Whale engine plain text script extractor.
//
// Whale text parsing is adapted from VNTranslationTools.
// Copyright (c) 2021 arcusmaximus
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.

using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace GameRes.Formats.NonColor
{
    [Export(typeof(ScriptFormat))]
    public class WhaleScriptFormat : GenericScriptFormat, IConfigurableScriptFormat
    {
        public const string FormatTag = "TXT/Whale";

        public override string         Tag { get { return FormatTag; } }
        public override string Description { get { return "Whale engine text script"; } }
        public override uint     Signature { get { return 0; } }

        static readonly string[] s_text_modes = { ScriptTextMode.Filtered, ScriptTextMode.Raw, ScriptTextMode.JsonLines };

        public IEnumerable<string> TextModes { get { return s_text_modes; } }
        public string DefaultTextMode { get { return ScriptTextMode.Filtered; } }

        public WhaleScriptFormat ()
        {
            Extensions = new[] { "txt" };
        }

        public override bool IsScript (IBinaryStream file)
        {
            if (!file.Name.HasExtension (".txt") || file.Length > int.MaxValue)
                return false;
            long position = file.Position;
            try
            {
                return WhaleScript.IsWhaleScript (WhaleScript.ReadText (file));
            }
            catch
            {
                return false;
            }
            finally
            {
                if (file.CanSeek)
                    file.Position = position;
            }
        }

        public override Stream ConvertFrom (IBinaryStream file)
        {
            return ConvertFrom (file, ScriptTextMode.Filtered);
        }

        public Stream ConvertFrom (IBinaryStream file, string text_mode)
        {
            var script = WhaleScript.ReadText (file);
            if (string.Equals (text_mode, ScriptTextMode.Raw, StringComparison.OrdinalIgnoreCase))
                return WhaleScript.CreateTextStream (script, file.Name);
            if (string.Equals (text_mode, ScriptTextMode.JsonLines, StringComparison.OrdinalIgnoreCase))
                return WhaleScript.CreateJsonLinesStream (script, file.Name);
            return WhaleScript.CreateTextStream (WhaleScript.ExtractText (script), file.Name);
        }

        public override Stream ConvertBack (IBinaryStream file)
        {
            throw new NotSupportedException();
        }

        public override ScriptData Read (string name, Stream file)
        {
            using (var input = BinaryStream.FromStream (file, name))
            {
                var script = new ScriptData();
                uint id = 0;
                foreach (var line in WhaleScript.ExtractLines (WhaleScript.ReadText (input)))
                    script.TextLines.Add (new ScriptLine { Id = id++, Text = line });
                return script;
            }
        }
    }

    internal static class WhaleScript
    {
        enum StringType
        {
            CharacterName,
            Message,
        }

        struct ScriptString
        {
            public readonly string Text;
            public readonly StringType Type;

            public ScriptString (string text, StringType type)
            {
                Text = text;
                Type = type;
            }
        }

        static readonly string[] MessageCommands = { "CS", "MS.HS" };
        const string DisplayMarker = "[テキスト表示]";

        static readonly Regex DialogueRe = new Regex (
            @"^(?:【(?<name>.+?)(?:,\w+)*】)?(?:「(?<message>.+?)」?|(?<message>（(?:.+?)）?))$",
            RegexOptions.Compiled);
        static readonly Regex SelectRe = new Regex (
            @"^SELECT\s+(?:""(?<choice>[^"",]+),\*\w+""[,\s]*)+$",
            RegexOptions.Compiled);
        static readonly Regex CommandRe = new Regex (
            @"^[A-Z0-9\.]+\s+(?:(?:""(?<arg>[^""]+)""|(?<arg>[^,\s]+))[,\s]*)+$",
            RegexOptions.Compiled);
        static readonly Regex VoiceRe = new Regex (
            @"voice\\[^\s""]+?\.ogg",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static string ReadText (IBinaryStream file)
        {
            if (file.Length > int.MaxValue)
                throw new FileSizeException();
            file.Position = 0;
            var data = file.ReadBytes ((int)file.Length);
            if (data.Length != file.Length)
                throw new EndOfStreamException();
            var encoding = GuessEncoding (data);
            int preamble = GetPreambleLength (data, encoding);
            return CleanupText (encoding.GetString (data, preamble, data.Length - preamble));
        }

        public static bool IsWhaleScript (string script)
        {
            int text_count = 0;
            int whale_markers = 0;
            using (var reader = new StringReader (script))
            {
                string line;
                while (null != (line = reader.ReadLine()))
                {
                    if (string.IsNullOrWhiteSpace (line))
                        continue;
                    if (line.StartsWith ("*", StringComparison.Ordinal))
                    {
                        ++whale_markers;
                        continue;
                    }
                    if (line.IndexOf (DisplayMarker, StringComparison.Ordinal) >= 0
                        || line.StartsWith ("SELECT ", StringComparison.Ordinal) || IsMessageCommand (line))
                        ++whale_markers;
                    foreach (var text in ExtractLineStrings (line))
                    {
                        if (!string.IsNullOrWhiteSpace (text.Text))
                            ++text_count;
                    }
                    if (text_count >= 2 && whale_markers > 0)
                        return true;
                }
            }
            return text_count >= 4;
        }

        public static string ExtractText (string script)
        {
            var output = new MemoryStream();
            using (var writer = new StreamWriter (output, new UTF8Encoding (true), 0x400, true))
            {
                foreach (var line in ExtractLines (script))
                    writer.WriteLine (line);
            }
            return Encoding.UTF8.GetString (output.ToArray()).TrimStart ('\uFEFF');
        }

        public static IEnumerable<string> ExtractLines (string script)
        {
            List<ScriptTextEntry> records;
            if (TryExtractDisplayRecords (script, out records))
            {
                foreach (var record in records)
                {
                    if (string.IsNullOrWhiteSpace (record.Message))
                        continue;
                    foreach (var name in record.Names)
                        yield return name;
                    yield return record.Message;
                }
                yield break;
            }

            using (var reader = new StringReader (script))
            {
                string line;
                while (null != (line = reader.ReadLine()))
                {
                    if (string.IsNullOrWhiteSpace (line) || line.StartsWith ("*", StringComparison.Ordinal))
                        continue;
                    foreach (var text in ExtractLineStrings (line))
                    {
                        if (!string.IsNullOrWhiteSpace (text.Text))
                            yield return NormalizeText (text.Text);
                    }
                }
            }
        }

        static IEnumerable<ScriptString> ExtractStrings (string script)
        {
            List<ScriptTextEntry> records;
            if (TryExtractDisplayRecords (script, out records))
            {
                foreach (var record in records)
                {
                    if (string.IsNullOrWhiteSpace (record.Message))
                        continue;
                    foreach (var name in record.Names)
                        yield return new ScriptString (name, StringType.CharacterName);
                    yield return new ScriptString (record.Message, StringType.Message);
                }
                yield break;
            }

            using (var reader = new StringReader (script))
            {
                string line;
                while (null != (line = reader.ReadLine()))
                {
                    if (string.IsNullOrWhiteSpace (line) || line.StartsWith ("*", StringComparison.Ordinal))
                        continue;
                    foreach (var text in ExtractLineStrings (line))
                    {
                        if (!string.IsNullOrWhiteSpace (text.Text))
                            yield return new ScriptString (NormalizeText (text.Text), text.Type);
                    }
                }
            }
        }

        static bool TryExtractDisplayRecords (string script, out List<ScriptTextEntry> records)
        {
            records = new List<ScriptTextEntry>();
            var lines = ReadCleanLines (script);
            for (int i = 0; i < lines.Count; ++i)
            {
                var line = lines[i];
                if (string.IsNullOrWhiteSpace (line) || line.StartsWith ("*", StringComparison.Ordinal))
                    continue;

                int marker_pos = line.IndexOf (DisplayMarker, StringComparison.Ordinal);
                if (marker_pos < 0)
                    continue;

                var record = new ScriptTextEntry();
                ParseDisplayCommand (line.Substring (marker_pos + DisplayMarker.Length), record);

                var message = new List<string>();
                while (++i < lines.Count)
                {
                    line = lines[i];
                    if (string.IsNullOrWhiteSpace (line))
                    {
                        if (message.Count > 0)
                            break;
                        continue;
                    }
                    if (line.StartsWith ("*", StringComparison.Ordinal))
                        continue;
                    if (line.IndexOf (DisplayMarker, StringComparison.Ordinal) >= 0)
                    {
                        --i;
                        break;
                    }

                    message.Add (line);
                    if (IsQuotedMessageComplete (message))
                        break;
                }

                record.Message = NormalizeDisplayMessage (message);
                if (record.Names.Count > 0 || !string.IsNullOrWhiteSpace (record.Voice)
                    || !string.IsNullOrWhiteSpace (record.Message))
                    records.Add (record);
            }
            return records.Count > 0;
        }

        static List<string> ReadCleanLines (string script)
        {
            var lines = new List<string>();
            using (var reader = new StringReader (script))
            {
                string line;
                while (null != (line = reader.ReadLine()))
                    lines.Add (CleanupLine (line));
            }
            return lines;
        }

        static void ParseDisplayCommand (string text, ScriptTextEntry record)
        {
            text = text.Trim();
            if (0 == text.Length)
                return;

            var voice = VoiceRe.Match (text);
            if (voice.Success)
            {
                record.Voice = voice.Value;
                text = text.Remove (voice.Index, voice.Length).Trim();
            }

            if (text.Length > 0 && !text.StartsWith ("「", StringComparison.Ordinal)
                && ContainsJapaneseText (text))
                record.Names.Add (text);
        }

        static bool IsQuotedMessageComplete (IList<string> lines)
        {
            if (0 == lines.Count)
                return false;
            var first = lines[0].TrimStart();
            if (!first.StartsWith ("「", StringComparison.Ordinal))
                return false;
            var last = lines[lines.Count-1].TrimEnd();
            return last.EndsWith ("」", StringComparison.Ordinal);
        }

        static string NormalizeDisplayMessage (IList<string> lines)
        {
            if (0 == lines.Count)
                return "";

            var builder = new StringBuilder();
            for (int i = 0; i < lines.Count; ++i)
            {
                if (i > 0)
                    builder.Append ("\r\n");
                builder.Append (lines[i]);
            }
            var text = builder.ToString();
            text = text.Trim();
            if (text.Length >= 2 && text[0] == '「' && text[text.Length-1] == '」')
                text = text.Substring (1, text.Length-2);
            return NormalizeText (text);
        }

        static IEnumerable<ScriptString> ExtractLineStrings (string line)
        {
            line = CleanupLine (line);
            if (0 == line.Length)
                yield break;

            if (line[0] > 0xFF)
            {
                foreach (var text in ExtractMessage (line))
                    yield return text;
                yield break;
            }

            if (line.StartsWith ("SELECT ", StringComparison.Ordinal))
            {
                foreach (var text in ExtractSelect (line))
                    yield return text;
                yield break;
            }

            if (IsMessageCommand (line))
            {
                foreach (var text in ExtractCommand (line))
                    yield return text;
            }
        }

        static IEnumerable<ScriptString> ExtractMessage (string line)
        {
            var match = DialogueRe.Match (line);
            if (!match.Success)
            {
                yield return new ScriptString (line, StringType.Message);
                yield break;
            }

            var name = match.Groups["name"];
            if (name.Success)
                yield return new ScriptString (name.Value, StringType.CharacterName);
            yield return new ScriptString (match.Groups["message"].Value, StringType.Message);
        }

        static IEnumerable<ScriptString> ExtractSelect (string line)
        {
            var match = SelectRe.Match (line);
            if (!match.Success)
                yield break;
            foreach (Capture capture in match.Groups["choice"].Captures)
                yield return new ScriptString (capture.Value, StringType.Message);
        }

        static IEnumerable<ScriptString> ExtractCommand (string line)
        {
            var match = CommandRe.Match (line);
            if (!match.Success)
                yield break;

            foreach (Capture capture in match.Groups["arg"].Captures)
            {
                if (!ContainsJapaneseText (capture.Value))
                    continue;
                foreach (var text in ExtractMessage (capture.Value))
                    yield return text;
            }
        }

        static bool IsMessageCommand (string line)
        {
            for (int i = 0; i < MessageCommands.Length; ++i)
            {
                if (line.StartsWith (MessageCommands[i] + " ", StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        static string NormalizeText (string text)
        {
            return text.Replace ("[n]", "\r\n");
        }

        static string CleanupText (string text)
        {
            if (string.IsNullOrEmpty (text))
                return text;

            var output = new StringBuilder (text.Length);
            using (var reader = new StringReader (text))
            {
                string line;
                while (null != (line = reader.ReadLine()))
                {
                    line = CleanupLine (line);
                    output.AppendLine (line);
                }
            }
            return output.ToString();
        }

        static string CleanupLine (string line)
        {
            if (string.IsNullOrEmpty (line))
                return "";

            if (!ContainsInlineControl (line))
                return line;

            line = StripControlChars (line).Trim();
            if (0 == line.Length)
                return "";

            int command_pos = FindCommandPosition (line);
            if (command_pos > 0)
                line = line.Substring (command_pos).TrimStart();
            return CollapseSpaces (line);
        }

        static bool ContainsInlineControl (string line)
        {
            foreach (char c in line)
            {
                if (IsInlineControl (c))
                    return true;
            }
            return false;
        }

        static string StripControlChars (string line)
        {
            var output = new StringBuilder (line.Length);
            bool space_pending = false;
            foreach (char c in line)
            {
                if (IsInlineControl (c))
                {
                    space_pending = true;
                    continue;
                }
                if (space_pending && output.Length > 0 && !char.IsWhiteSpace (output[output.Length-1]))
                    output.Append (' ');
                space_pending = false;
                output.Append (c);
            }
            return output.ToString();
        }

        static bool IsInlineControl (char c)
        {
            return (c < ' ' && c != '\t') || c == '\x7F';
        }

        static string CollapseSpaces (string line)
        {
            var output = new StringBuilder (line.Length);
            bool was_space = false;
            foreach (char c in line)
            {
                if (char.IsWhiteSpace (c))
                {
                    if (!was_space)
                        output.Append (' ');
                    was_space = true;
                }
                else
                {
                    output.Append (c);
                    was_space = false;
                }
            }
            return output.ToString().Trim();
        }

        static int FindCommandPosition (string line)
        {
            int result = StartsWithCommand (line) ? 0 : -1;
            if (0 == result)
                return result;

            for (int i = 0; i < MessageCommands.Length; ++i)
                result = MinPositive (result, FindToken (line, MessageCommands[i] + " "));
            result = MinPositive (result, FindToken (line, "SELECT "));
            return result;
        }

        static bool StartsWithCommand (string line)
        {
            if (line.StartsWith ("SELECT ", StringComparison.Ordinal))
                return true;
            return IsMessageCommand (line);
        }

        static int FindToken (string line, string token)
        {
            int start = 0;
            for (;;)
            {
                int pos = line.IndexOf (token, start, StringComparison.Ordinal);
                if (pos < 0)
                    return -1;
                if (0 == pos || !IsCommandChar (line[pos-1]))
                    return pos;
                start = pos + token.Length;
            }
        }

        static int MinPositive (int left, int right)
        {
            if (right < 0)
                return left;
            if (left < 0 || right < left)
                return right;
            return left;
        }

        static bool IsCommandChar (char c)
        {
            return char.IsLetterOrDigit (c) || c == '.' || c == '_';
        }

        static bool ContainsJapaneseText (string text)
        {
            if (string.IsNullOrEmpty (text))
                return false;
            foreach (char c in text)
            {
                if (c >= 0x3000)
                    return true;
            }
            return false;
        }

        static Encoding GuessEncoding (byte[] data)
        {
            if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
                return Encoding.UTF8;
            if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE)
                return Encoding.Unicode;
            if (data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF)
                return Encoding.BigEndianUnicode;
            return Encodings.cp932;
        }

        static int GetPreambleLength (byte[] data, Encoding encoding)
        {
            var preamble = encoding.GetPreamble();
            if (preamble.Length == 0 || data.Length < preamble.Length)
                return 0;
            for (int i = 0; i < preamble.Length; ++i)
            {
                if (data[i] != preamble[i])
                    return 0;
            }
            return preamble.Length;
        }

        public static Stream CreateTextStream (string text, string name)
        {
            var output = new MemoryStream();
            using (var writer = new StreamWriter (output, new UTF8Encoding (true), 0x400, true))
                writer.Write (text);
            return new BinMemoryStream (output, name);
        }

        public static Stream CreateJsonLinesStream (string script, string name)
        {
            List<ScriptTextEntry> records;
            if (TryExtractDisplayRecords (script, out records))
                return ScriptJsonLines.CreateStream (records, name);
            return ScriptJsonLines.CreateStream (BuildJsonEntries (ExtractStrings (script)), name);
        }

        static IEnumerable<ScriptTextEntry> BuildJsonEntries (IEnumerable<ScriptString> strings)
        {
            var pending_names = new List<string>();
            foreach (var str in strings)
            {
                if (str.Type == StringType.CharacterName)
                {
                    pending_names.Add (str.Text);
                    continue;
                }

                var entry = new ScriptTextEntry (str.Text);
                entry.Names.AddRange (pending_names);
                yield return entry;
                pending_names.Clear();
            }
        }
    }
}
