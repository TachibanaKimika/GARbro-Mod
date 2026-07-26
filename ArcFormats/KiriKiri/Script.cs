//! \file       Script.cs
//! \date       Mon May 18 2026
//! \brief      KiriKiri/KAG script text extractor.
//
// KiriKiri text descrambling is adapted from VNTranslationTools.
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
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.IO.Compression;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using GameRes.Formats.Emote;
using GameRes.Utility;

namespace GameRes.Formats.KiriKiri
{
    [Export(typeof(ScriptFormat))]
    public class KiriKiriScriptFormat : GenericScriptFormat, IConfigurableScriptFormat
    {
        public const string FormatTag = "KiriKiri/Script";

        public override string         Tag { get { return FormatTag; } }
        public override string Description { get { return "KiriKiri/KAG script"; } }
        public override uint     Signature { get { return 0; } }

        const uint PsbSignature = 0x00425350; // 'PSB'
        const uint ScrambledMode0Signature = 0xFF00FEFE;
        const uint ScrambledMode1Signature = 0xFF01FEFE;
        const uint ScrambledMode2Signature = 0xFF02FEFE;

        static readonly string[] s_text_modes = {
            ScriptTextMode.Filtered,
            ScriptTextMode.Raw,
            ScriptTextMode.Dump,
            ScriptTextMode.JsonLines,
        };

        static readonly Regex LineCommandRegex = new Regex (
            @"^\s*@(?<command>[^ ]+)(?: +(?<attrname>[^= ]+)(?: *= *(?<attrvalue>""(?:\\""|[^""])*""|'(?:\\'|[^'])*'|[^""' ]*))?)*",
            RegexOptions.Compiled);
        static readonly Regex InlineCommandRegex = new Regex (
            @"\[(?<command>[^\]' ]+)(?: +(?<attrname>[^\]= ]+)(?: *= *(?<attrvalue>""(?:\\""|[^""])*""|'(?:\\'|[^'])*'|[^\]""' ]*))?)* *\]",
            RegexOptions.Compiled);

        static readonly string[] NameCommands = { "nm", "set_title", "speaker", "Talk", "talk", "cn", "name", "名前" };
        static readonly string[] EnterNameCommands = { "ns" };
        static readonly string[] ExitNameCommands = { "nse" };
        static readonly string[] MessageCommands = { "sel01", "sel02", "sel03", "sel04", "AddSelect", "ruby" };
        static readonly string[] AllowedInlineCommands = { "r", "ruby", "ruby_c", "heart", "mruby", "・", "★" };

        enum KagStringType
        {
            CharacterName,
            Message,
        }

        struct KagString
        {
            public readonly string Text;
            public readonly KagStringType Type;

            public KagString (string text, KagStringType type)
            {
                Text = text;
                Type = type;
            }
        }

        public IEnumerable<string> TextModes { get { return s_text_modes; } }
        public string DefaultTextMode { get { return ScriptTextMode.Filtered; } }

        public KiriKiriScriptFormat ()
        {
            Extensions = new[] { "ks", "txt", "scn" };
            Signatures = new[] {
                0u,
                ScrambledMode0Signature,
                ScrambledMode1Signature,
                ScrambledMode2Signature,
                PsbSignature,
            };
        }

        public override bool IsScript (IBinaryStream file)
        {
            long position = file.Position;
            try
            {
                if (file.Name.HasExtension (".ks"))
                    return file.Length <= int.MaxValue;
                if (file.Name.HasExtension (".txt"))
                    return HasScrambledHeader (file);
                if (file.Name.HasExtension (".scn"))
                    return IsPsbScenario (file);
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
            bool dump = string.Equals (text_mode, ScriptTextMode.Dump, StringComparison.OrdinalIgnoreCase);
            if (dump)
            {
                if (file.Name.HasExtension (".scn"))
                    return CreateScenarioDump (file);
                return CreateKagDump (ReadTextScript (file), file.Name);
            }

            bool jsonl = string.Equals (text_mode, ScriptTextMode.JsonLines, StringComparison.OrdinalIgnoreCase);
            if (jsonl)
            {
                if (file.Name.HasExtension (".scn"))
                    return ScriptJsonLines.CreateStream (ReadScenarioEntries (file), file.Name);
                return ScriptJsonLines.CreateStream (ExtractKagEntries (ReadTextScript (file)), file.Name);
            }

            bool raw = string.Equals (text_mode, ScriptTextMode.Raw, StringComparison.OrdinalIgnoreCase);
            if (file.Name.HasExtension (".scn"))
                return ConvertScenario (file, !raw);
            var script = ReadTextScript (file);
            return CreateTextStream (raw ? script : ExtractKagText (script), file.Name);
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
                if (name.HasExtension (".scn"))
                {
                    foreach (var line in ReadScenarioLines (input, true))
                        script.TextLines.Add (new ScriptLine { Id = id++, Text = line });
                }
                else
                {
                    foreach (var line in ExtractKagLines (ReadTextScript (input)))
                        script.TextLines.Add (new ScriptLine { Id = id++, Text = line });
                }
                return script;
            }
        }

        static bool HasScrambledHeader (IBinaryStream file)
        {
            if (file.Length < 5)
                return false;
            var header = file.ReadHeader (5);
            return header[0] == 0xFE && header[1] == 0xFE
                && header[2] <= 2 && header[3] == 0xFF && header[4] == 0xFE;
        }

        static bool IsPsbScenario (IBinaryStream file)
        {
            if (file.Signature != PsbSignature || file.Length > int.MaxValue)
                return false;
            try
            {
                using (var reader = new PsbReader (file))
                {
                    if (!reader.ParseNonEncrypted())
                        return false;
                    return null != reader.GetRootKey<IList> ("scenes");
                }
            }
            catch
            {
                return false;
            }
        }

        static Stream ConvertScenario (IBinaryStream file, bool filter)
        {
            var output = new MemoryStream();
            using (var writer = new StreamWriter (output, new UTF8Encoding (true), 0x400, true))
            {
                foreach (var line in ReadScenarioLines (file, filter))
                    writer.WriteLine (line);
            }
            output.Position = 0;
            return output;
        }

        static Stream CreateScenarioDump (IBinaryStream file)
        {
            file.Position = 0;
            var output = new MemoryStream();
            using (var writer = new StreamWriter (output, new UTF8Encoding (true), 0x400, true))
            using (var reader = new PsbReader (file))
            {
                if (!reader.ParseNonEncrypted())
                    throw new InvalidFormatException();
                var scenes = reader.GetRootKey<IList> ("scenes");
                if (null == scenes)
                    throw new InvalidFormatException();

                writer.WriteLine ("# KiriKiri PSB scenario dump");
                writer.WriteLine ("# This is not reconstructed KAG source; it is a decoded PSB object dump.");
                writer.WriteLine ();
                writer.WriteLine ("[Header]");
                WritePsbProperty (writer, "Source", file.Name);
                WritePsbProperty (writer, "Version", reader.Version);
                WritePsbProperty (writer, "Encrypted", reader.IsEncrypted);
                WritePsbProperty (writer, "Name", reader.GetRootKey<string> ("name"));
                WritePsbProperty (writer, "Hash", reader.GetRootKey<string> ("hash"));
                WritePsbProperty (writer, "SceneCount", scenes.Count);
                WritePsbProperty (writer, "Outlines", reader.GetRootKey<IList> ("outlines"));
                writer.WriteLine ();

                for (int i = 0; i < scenes.Count; ++i)
                {
                    var scene = scenes[i] as IDictionary;
                    writer.WriteLine ("[Scene {0:D4}]", i);
                    if (null == scene)
                    {
                        WritePsbProperty (writer, "Value", scenes[i]);
                        writer.WriteLine ();
                        continue;
                    }

                    writer.WriteLine ("[Metadata]");
                    foreach (DictionaryEntry item in scene)
                    {
                        var key = item.Key as string;
                        if ("lines" == key || "texts" == key || "nexts" == key
                            || "postevals" == key || "selects" == key)
                            continue;
                        WritePsbProperty (writer, key ?? item.Key.ToString(), item.Value);
                    }
                    writer.WriteLine ();

                    WriteScenarioTexts (writer, GetValue<IList> (scene, "texts"));
                    WritePsbListSection (writer, "Selects", GetValue<IList> (scene, "selects"));
                    WritePsbListSection (writer, "Nexts", GetValue<IList> (scene, "nexts"));
                    WritePsbListSection (writer, "Postevals", GetValue<IList> (scene, "postevals"));
                    WritePsbListSection (writer, "Lines", GetValue<IList> (scene, "lines"));
                }
            }
            output.Position = 0;
            return new BinMemoryStream (output, file.Name);
        }

        static Stream CreateKagDump (string script, string name)
        {
            var output = new MemoryStream();
            using (var writer = new StreamWriter (output, new UTF8Encoding (true), 0x400, true))
            using (var reader = new StringReader (script))
            {
                writer.WriteLine ("# KiriKiri/KAG decoded text dump");
                writer.WriteLine ("# This is decoded source text with diagnostic line numbers.");
                writer.WriteLine ();
                writer.WriteLine ("[Lines]");
                string line;
                int line_number = 1;
                while (null != (line = reader.ReadLine()))
                    writer.WriteLine ("{0:D6}: {1}", line_number++, line);
            }
            output.Position = 0;
            return new BinMemoryStream (output, name);
        }

        static void WriteScenarioTexts (TextWriter writer, IList texts)
        {
            writer.WriteLine ("[Texts]");
            if (null == texts || 0 == texts.Count)
            {
                writer.WriteLine ("<empty>");
                writer.WriteLine ();
                return;
            }

            for (int i = 0; i < texts.Count; ++i)
            {
                var text = texts[i] as IList;
                string real_name;
                string display_name;
                string message;
                bool has_message = TryGetScenarioText (text, out real_name, out display_name, out message);
                writer.Write ("#{0:D4}", i);
                if (has_message)
                {
                    writer.Write (" name=");
                    WritePsbValue (writer, display_name ?? real_name);
                    var voices = GetScenarioVoices (text);
                    if (null != voices)
                    {
                        writer.Write (" voice=");
                        WritePsbValue (writer, GetScenarioVoice (text));
                        writer.Write (" voiceData=");
                        WritePsbValue (writer, voices);
                    }
                }
                writer.WriteLine ();
                if (has_message)
                    writer.WriteLine (NormalizeScenarioText (message));
                writer.Write ("raw=");
                WritePsbValue (writer, texts[i]);
                writer.WriteLine ();
                writer.WriteLine ();
            }
        }

        static void WritePsbListSection (TextWriter writer, string name, IList values)
        {
            writer.WriteLine ("[{0}]", name);
            if (null == values || 0 == values.Count)
            {
                writer.WriteLine ("<empty>");
            }
            else
            {
                for (int i = 0; i < values.Count; ++i)
                {
                    writer.Write ("#{0:D4} ", i);
                    WritePsbValue (writer, values[i]);
                    writer.WriteLine ();
                }
            }
            writer.WriteLine ();
        }

        static void WritePsbProperty (TextWriter writer, string name, object value)
        {
            writer.Write (name);
            writer.Write ('=');
            WritePsbValue (writer, value);
            writer.WriteLine ();
        }

        static void WritePsbValue (TextWriter writer, object value)
        {
            if (null == value)
            {
                writer.Write ("null");
                return;
            }
            var text = value as string;
            if (null != text)
            {
                ScriptJsonLines.WriteJsonString (writer, text);
                return;
            }
            var dict = value as IDictionary;
            if (null != dict)
            {
                writer.Write ('{');
                bool first = true;
                foreach (DictionaryEntry item in dict)
                {
                    if (!first)
                        writer.Write (',');
                    first = false;
                    ScriptJsonLines.WriteJsonString (writer, item.Key.ToString());
                    writer.Write (':');
                    WritePsbValue (writer, item.Value);
                }
                writer.Write ('}');
                return;
            }
            var list = value as IList;
            if (null != list)
            {
                writer.Write ('[');
                for (int i = 0; i < list.Count; ++i)
                {
                    if (i > 0)
                        writer.Write (',');
                    WritePsbValue (writer, list[i]);
                }
                writer.Write (']');
                return;
            }
            var boolean = value as bool?;
            if (boolean.HasValue)
            {
                writer.Write (boolean.Value ? "true" : "false");
                return;
            }
            var formattable = value as IFormattable;
            if (null != formattable)
            {
                writer.Write (formattable.ToString (null, CultureInfo.InvariantCulture));
                return;
            }
            ScriptJsonLines.WriteJsonString (writer, value.ToString());
        }

        static IEnumerable<string> ReadScenarioLines (IBinaryStream file, bool filter)
        {
            file.Position = 0;
            using (var reader = new PsbReader (file))
            {
                if (!reader.ParseNonEncrypted())
                    throw new InvalidFormatException();
                var scenes = reader.GetRootKey<IList> ("scenes");
                if (null == scenes)
                    throw new InvalidFormatException();

                foreach (var scene_obj in scenes)
                {
                    var scene = scene_obj as IDictionary;
                    if (filter)
                    {
                        if (null == scene)
                            continue;
                        foreach (var line in GetTextStrings (scene))
                            yield return line;
                        foreach (var line in GetSelectStrings (scene))
                            yield return line;
                    }
                    else
                    {
                        foreach (var line in EnumerateStrings (scene_obj))
                            yield return line;
                    }
                }
            }
        }

        static IEnumerable<string> EnumerateStrings (object obj)
        {
            var text = obj as string;
            if (null != text)
            {
                text = NormalizeScenarioText (text);
                if (!string.IsNullOrWhiteSpace (text))
                    yield return text;
                yield break;
            }
            var list = obj as IList;
            if (null != list)
            {
                foreach (var item in list)
                {
                    foreach (var line in EnumerateStrings (item))
                        yield return line;
                }
                yield break;
            }
            var dict = obj as IDictionary;
            if (null != dict)
            {
                foreach (DictionaryEntry item in dict)
                {
                    foreach (var line in EnumerateStrings (item.Value))
                        yield return line;
                }
            }
        }

        static IEnumerable<string> GetTextStrings (IDictionary scene)
        {
            var texts = GetValue<IList> (scene, "texts");
            if (null == texts)
                yield break;

            foreach (var text_obj in texts)
            {
                var text = text_obj as IList;
                string real_name;
                string display_name;
                string message;
                if (!TryGetScenarioText (text, out real_name, out display_name, out message))
                    continue;
                if (!string.IsNullOrEmpty (real_name))
                    yield return NormalizeScenarioText (display_name ?? real_name);
                yield return NormalizeScenarioText (message);
            }
        }

        static IEnumerable<string> GetSelectStrings (IDictionary scene)
        {
            var selects = GetValue<IList> (scene, "selects");
            if (null == selects)
                yield break;

            foreach (var select_obj in selects)
            {
                var select = select_obj as IDictionary;
                if (null == select)
                    continue;

                string text = null;
                var languages = GetValue<IList> (select, "language");
                if (null != languages && languages.Count > 0)
                {
                    var language_select = languages[0] as IDictionary;
                    if (null != language_select)
                        text = GetValue<string> (language_select, "text");
                }
                if (null == text)
                    text = GetValue<string> (select, "text");
                if (null != text)
                    yield return NormalizeScenarioText (text);
            }
        }

        static IEnumerable<ScriptTextEntry> ReadScenarioEntries (IBinaryStream file)
        {
            file.Position = 0;
            using (var reader = new PsbReader (file))
            {
                if (!reader.ParseNonEncrypted())
                    throw new InvalidFormatException();
                var scenes = reader.GetRootKey<IList> ("scenes");
                if (null == scenes)
                    throw new InvalidFormatException();

                foreach (var scene_obj in scenes)
                {
                    var scene = scene_obj as IDictionary;
                    if (null == scene)
                        continue;
                    foreach (var entry in GetTextEntries (scene))
                        yield return entry;
                    foreach (var line in GetSelectStrings (scene))
                        yield return new ScriptTextEntry (line);
                }
            }
        }

        static IEnumerable<ScriptTextEntry> GetTextEntries (IDictionary scene)
        {
            var texts = GetValue<IList> (scene, "texts");
            if (null == texts)
                yield break;

            foreach (var text_obj in texts)
            {
                var text = text_obj as IList;
                string real_name;
                string display_name;
                string message;
                if (!TryGetScenarioText (text, out real_name, out display_name, out message))
                    continue;

                var entry = new ScriptTextEntry (NormalizeScenarioText (message));
                if (!string.IsNullOrEmpty (real_name))
                    entry.Names.Add (NormalizeScenarioText (display_name ?? real_name));
                entry.Voice = GetScenarioVoice (text);
                yield return entry;
            }
        }

        static bool TryGetScenarioText (IList text, out string real_name, out string display_name,
                                        out string message_text)
        {
            real_name = null;
            display_name = null;
            message_text = null;
            if (null == text || text.Count < 3)
                return false;

            real_name = GetString (text, 0);
            object message;
            if (text[1] is IList)
            {
                message = text[1];
            }
            else
            {
                display_name = GetString (text, 1);
                if (null == display_name && !string.IsNullOrEmpty (real_name) && real_name != "＠")
                    display_name = real_name;
                message = GetString (text, 2);
                if (null == message)
                    message = text[2];
            }

            var languages = message as IList;
            if (null != languages && languages.Count > 0)
            {
                var language_text = languages[0] as IList;
                if (null != language_text && language_text.Count >= 2)
                {
                    display_name = GetString (language_text, 0);
                    message = GetString (language_text, 1);
                }
            }
            message_text = message as string;
            return null != message_text;
        }

        static string GetScenarioVoice (IList text)
        {
            var voices = GetScenarioVoices (text);
            if (null == voices)
                return null;
            foreach (var voice_obj in voices)
            {
                var voice = voice_obj as IDictionary;
                if (null == voice)
                    continue;
                var id = GetValue<string> (voice, "voice");
                if (!string.IsNullOrEmpty (id))
                    return id;
            }
            return null;
        }

        static IList GetScenarioVoices (IList text)
        {
            if (null == text)
                return null;
            for (int i = 2; i < text.Count; ++i)
            {
                var voices = text[i] as IList;
                if (null == voices)
                    continue;
                foreach (var voice_obj in voices)
                {
                    var voice = voice_obj as IDictionary;
                    if (null != voice && !string.IsNullOrEmpty (GetValue<string> (voice, "voice")))
                        return voices;
                }
            }
            return null;
        }

        static T GetValue<T> (IDictionary dict, string key) where T : class
        {
            if (!dict.Contains (key))
                return null;
            return dict[key] as T;
        }

        static string GetString (IList list, int index)
        {
            if (index < 0 || index >= list.Count)
                return null;
            return list[index] as string;
        }

        static string NormalizeScenarioText (string text)
        {
            return text.Replace ("\\n", "\r\n");
        }

        static string ExtractKagText (string script)
        {
            var output = new MemoryStream();
            using (var writer = new StreamWriter (output, new UTF8Encoding (true), 0x400, true))
            {
                foreach (var line in ExtractKagLines (script))
                    writer.WriteLine (line);
            }
            return Encoding.UTF8.GetString (output.ToArray()).TrimStart ('\uFEFF');
        }

        static IEnumerable<string> ExtractKagLines (string script)
        {
            bool in_script = false;
            using (var reader = new StringReader (script))
            {
                string line;
                while (null != (line = reader.ReadLine()))
                {
                    var trimmed = line.TrimStart();
                    if (trimmed == "[iscript]" || trimmed == "@iscript"
                        || trimmed.StartsWith ("[macro", StringComparison.Ordinal)
                        || trimmed.StartsWith ("@macro", StringComparison.Ordinal))
                    {
                        in_script = true;
                        continue;
                    }
                    if (trimmed == "[endscript]" || trimmed == "@endscript"
                        || trimmed == "[endmacro]" || trimmed == "@endmacro")
                    {
                        in_script = false;
                        continue;
                    }
                    if (in_script || trimmed.StartsWith (";", StringComparison.Ordinal))
                        continue;

                    foreach (var text in ExtractKagLineStrings (line, trimmed))
                    {
                        var normalized = NormalizeKagText (text.Text);
                        if (!string.IsNullOrWhiteSpace (normalized))
                            yield return normalized;
                    }
                }
            }
        }

        static IEnumerable<ScriptTextEntry> ExtractKagEntries (string script)
        {
            bool in_script = false;
            var pending_names = new List<string>();
            using (var reader = new StringReader (script))
            {
                string line;
                while (null != (line = reader.ReadLine()))
                {
                    var trimmed = line.TrimStart();
                    if (trimmed == "[iscript]" || trimmed == "@iscript"
                        || trimmed.StartsWith ("[macro", StringComparison.Ordinal)
                        || trimmed.StartsWith ("@macro", StringComparison.Ordinal))
                    {
                        in_script = true;
                        continue;
                    }
                    if (trimmed == "[endscript]" || trimmed == "@endscript"
                        || trimmed == "[endmacro]" || trimmed == "@endmacro")
                    {
                        in_script = false;
                        continue;
                    }
                    if (in_script || trimmed.StartsWith (";", StringComparison.Ordinal))
                        continue;

                    foreach (var text in ExtractKagLineStrings (line, trimmed))
                    {
                        var normalized = NormalizeKagText (text.Text);
                        if (string.IsNullOrWhiteSpace (normalized))
                            continue;
                        if (text.Type == KagStringType.CharacterName)
                        {
                            pending_names.Add (normalized);
                            continue;
                        }

                        var entry = new ScriptTextEntry (normalized);
                        entry.Names.AddRange (pending_names);
                        pending_names.Clear();
                        yield return entry;
                    }
                }
            }
        }

        static IEnumerable<KagString> ExtractKagLineStrings (string line, string trimmed)
        {
            if (trimmed.StartsWith ("@", StringComparison.Ordinal))
            {
                foreach (var text in ExtractLineCommandStrings (line))
                    yield return text;
                yield break;
            }
            if (trimmed.StartsWith ("*", StringComparison.Ordinal))
            {
                int pipe = line.IndexOf ('|');
                if (pipe >= 0 && pipe + 1 < line.Length)
                    yield return new KagString (line.Substring (pipe + 1), KagStringType.Message);
                yield break;
            }
            if (trimmed.StartsWith ("#", StringComparison.Ordinal))
            {
                int name_pos = line.IndexOf ('#') + 1;
                if (name_pos > 0 && name_pos < line.Length)
                    yield return new KagString (line.Substring (name_pos), KagStringType.CharacterName);
                yield break;
            }
            foreach (var text in ExtractMessageStrings (line))
                yield return text;
        }

        static IEnumerable<KagString> ExtractLineCommandStrings (string line)
        {
            if (line == "@r")
            {
                yield return new KagString ("\r\n", KagStringType.Message);
                yield break;
            }

            var command = LineCommandRegex.Match (line);
            if (!command.Success)
                yield break;

            var name = command.Groups["command"].Value;
            if (ContainsString (NameCommands, name))
            {
                foreach (var value in GetJapaneseAttributeValues (command))
                    yield return new KagString (value, KagStringType.CharacterName);
            }
            else if (ContainsString (MessageCommands, name))
            {
                foreach (var value in GetJapaneseAttributeValues (command))
                    yield return new KagString (value, KagStringType.Message);
            }
        }

        static IEnumerable<KagString> ExtractMessageStrings (string line)
        {
            int segment_start = 0;
            foreach (Match command in InlineCommandRegex.Matches (line))
            {
                string name = command.Groups["command"].Value;
                if (ContainsString (AllowedInlineCommands, name))
                    continue;

                if (command.Index > segment_start)
                    yield return new KagString (line.Substring (segment_start, command.Index - segment_start), KagStringType.Message);

                bool is_name_bracket = name.StartsWith ("【", StringComparison.Ordinal) && name.EndsWith ("】", StringComparison.Ordinal);
                if (is_name_bracket && name.Length > 2)
                    yield return new KagString (name.Substring (1, name.Length - 2), KagStringType.CharacterName);

                if (ContainsString (NameCommands, name) || is_name_bracket)
                {
                    foreach (var value in GetJapaneseAttributeValues (command))
                        yield return new KagString (value, KagStringType.CharacterName);
                }
                else if (ContainsString (MessageCommands, name))
                {
                    foreach (var value in GetJapaneseAttributeValues (command))
                        yield return new KagString (value, KagStringType.Message);
                }
                else if (ContainsString (EnterNameCommands, name) || ContainsString (ExitNameCommands, name))
                {
                    // State affects replacement in translation tooling, but extraction only needs text.
                }

                segment_start = command.Index + command.Length;
            }

            if (segment_start < line.Length)
                yield return new KagString (line.Substring (segment_start), KagStringType.Message);
        }

        static IEnumerable<string> GetJapaneseAttributeValues (Match command)
        {
            foreach (Capture capture in command.Groups["attrvalue"].Captures)
            {
                string value = UnquoteAttributeValue (capture.Value);
                if (ContainsJapaneseText (value))
                    yield return value;
            }
        }

        static string NormalizeKagText (string text)
        {
            text = ConvertKirikiriRubyToPlain (text);
            text = text.Replace ("@r", "\r\n");
            text = text.Replace ("[l]", "|");
            text = text.Replace ("[r]", "\r\n");
            return text;
        }

        static string ConvertKirikiriRubyToPlain (string text)
        {
            var commands = InlineCommandRegex.Matches (text);
            for (int i = commands.Count - 1; i >= 0; --i)
            {
                var command = commands[i];
                if (command.Groups["command"].Value != "ruby")
                    continue;
                string ruby = GetAttributeValue (command, "text");
                if (null == ruby)
                    continue;
                string chars = GetAttributeValue (command, "char");
                int text_length;
                string base_text = null;
                if (null == chars)
                    text_length = 1;
                else if (!int.TryParse (chars, out text_length))
                    base_text = chars;

                if (text_length > 0)
                {
                    if (command.Index + command.Length + text_length > text.Length)
                        continue;
                    base_text = text.Substring (command.Index + command.Length, text_length);
                }
                if (null != base_text)
                    text = text.Substring (0, command.Index) + "[" + base_text + "/" + ruby + "]"
                         + text.Substring (command.Index + command.Length + text_length);
            }
            return text;
        }

        static string GetAttributeValue (Match command, string name)
        {
            var names = command.Groups["attrname"].Captures;
            var values = command.Groups["attrvalue"].Captures;
            for (int i = 0; i < names.Count && i < values.Count; ++i)
            {
                if (names[i].Value == name)
                    return UnquoteAttributeValue (values[i].Value);
            }
            return null;
        }

        static string UnquoteAttributeValue (string value)
        {
            if (value.Length >= 2 && value[0] == '"' && value[value.Length-1] == '"')
                return value.Substring (1, value.Length - 2).Replace ("\\\"", "\"");
            if (value.Length >= 2 && value[0] == '\'' && value[value.Length-1] == '\'')
                return value.Substring (1, value.Length - 2).Replace ("\\'", "'");
            return value;
        }

        static bool ContainsString (string[] list, string value)
        {
            for (int i = 0; i < list.Length; ++i)
            {
                if (list[i] == value)
                    return true;
            }
            return false;
        }

        static bool ContainsJapaneseText (string text)
        {
            if (string.IsNullOrEmpty (text))
                return false;
            foreach (char c in text)
            {
                if ((c >= '\u3040' && c <= '\u30FF')
                    || (c >= '\u3400' && c <= '\u9FFF')
                    || (c >= '\uF900' && c <= '\uFAFF')
                    || (c >= '\uFF66' && c <= '\uFF9F'))
                    return true;
            }
            return false;
        }

        static string ReadTextScript (IBinaryStream file)
        {
            var data = ReadAllBytes (file);
            int offset = 0;
            int count = data.Length;
            if (IsScrambled (data, offset, count))
            {
                data = Descramble (data, offset, count);
                offset = 0;
                count = data.Length;
            }
            var encoding = GuessEncoding (data, offset, count);
            int preamble = GetPreambleLength (data, offset, count, encoding);
            return encoding.GetString (data, offset + preamble, count - preamble);
        }

        static byte[] ReadAllBytes (IBinaryStream file)
        {
            if (file.Length > int.MaxValue)
                throw new FileSizeException();
            file.Position = 0;
            var data = file.ReadBytes ((int)file.Length);
            if (data.Length != file.Length)
                throw new EndOfStreamException();
            return data;
        }

        static bool IsScrambled (byte[] data, int offset, int count)
        {
            return count >= 5
                && data[offset] == 0xFE && data[offset+1] == 0xFE
                && data[offset+2] <= 2 && data[offset+3] == 0xFF && data[offset+4] == 0xFE;
        }

        static byte[] Descramble (byte[] data, int offset, int count)
        {
            switch (data[offset+2])
            {
            case 0:
                return DescrambleMode0 (data, offset, count);
            case 1:
                return DescrambleMode1 (data, offset, count);
            case 2:
                return Decompress (data, offset, count);
            default:
                throw new NotSupportedException();
            }
        }

        static byte[] DescrambleMode0 (byte[] data, int offset, int count)
        {
            var output = new byte[count - 3];
            Buffer.BlockCopy (data, offset + 3, output, 0, output.Length);
            for (int i = 2; i + 1 < output.Length; i += 2)
            {
                if (output[i+1] == 0 && output[i] < 0x20)
                    continue;
                output[i+1] ^= (byte)(output[i] & 0xFE);
                output[i] ^= 1;
            }
            return output;
        }

        static byte[] DescrambleMode1 (byte[] data, int offset, int count)
        {
            var output = new byte[count - 3];
            Buffer.BlockCopy (data, offset + 3, output, 0, output.Length);
            for (int i = 2; i + 1 < output.Length; i += 2)
            {
                int c = output[i] | (output[i+1] << 8);
                c = ((c & 0xAAAA) >> 1) | ((c & 0x5555) << 1);
                output[i] = (byte)c;
                output[i+1] = (byte)(c >> 8);
            }
            return output;
        }

        static byte[] Decompress (byte[] data, int offset, int count)
        {
            const int header_size = 5 + 8 + 8 + 2;
            if (count < header_size)
                throw new InvalidFormatException();

            long compressed_length = LittleEndian.ToInt64 (data, offset + 5);
            long unpacked_length = LittleEndian.ToInt64 (data, offset + 13);
            if (compressed_length < 2 || unpacked_length < 0 || unpacked_length > int.MaxValue - 2)
                throw new InvalidFormatException();

            int packed_offset = offset + header_size;
            int packed_count = count - header_size;
            if (compressed_length - 2 > packed_count)
                throw new InvalidFormatException();

            var output = new byte[2 + (int)unpacked_length];
            output[0] = 0xFF;
            output[1] = 0xFE;
            using (var input = new MemoryStream (data, packed_offset, packed_count))
            using (var deflate = new DeflateStream (input, CompressionMode.Decompress))
            {
                int dst = 2;
                while (dst < output.Length)
                {
                    int read = deflate.Read (output, dst, output.Length - dst);
                    if (0 == read)
                        throw new EndOfStreamException();
                    dst += read;
                }
            }
            return output;
        }

        static Encoding GuessEncoding (byte[] data, int offset, int count)
        {
            if (count >= 3 && data[offset] == 0xEF && data[offset+1] == 0xBB && data[offset+2] == 0xBF)
                return Encoding.UTF8;
            if (count >= 2 && data[offset] == 0xFF && data[offset+1] == 0xFE)
                return Encoding.Unicode;
            if (count >= 2 && data[offset] == 0xFE && data[offset+1] == 0xFF)
                return Encoding.BigEndianUnicode;
            return Encodings.cp932;
        }

        static int GetPreambleLength (byte[] data, int offset, int count, Encoding encoding)
        {
            var preamble = encoding.GetPreamble();
            if (preamble.Length == 0 || count < preamble.Length)
                return 0;
            for (int i = 0; i < preamble.Length; ++i)
            {
                if (data[offset+i] != preamble[i])
                    return 0;
            }
            return preamble.Length;
        }

        static Stream CreateTextStream (string text, string name)
        {
            var output = new MemoryStream();
            using (var writer = new StreamWriter (output, new UTF8Encoding (true), 0x400, true))
                writer.Write (text);
            return new BinMemoryStream (output, name);
        }
    }
}
