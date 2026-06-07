//! \file       ScriptWS2.cs
//! \date       Mon Jun 08 2026
//! \brief      AdvHD WS2 bytecode script text extractor.
//
// AdvHD bytecode parsing is adapted from VNTranslationTools.
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
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace GameRes.Formats.AdvHD
{
    [Export(typeof(ArchiveFormat))]
    public class Ws2Opener : ArchiveFormat
    {
        public override string         Tag { get { return AdvHdScript.FormatTag; } }
        public override string Description { get { return "AdvHD WS2 bytecode script"; } }
        public override uint     Signature { get { return 0; } }
        public override bool  IsHierarchic { get { return false; } }
        public override bool      CanWrite { get { return false; } }

        public Ws2Opener ()
        {
            Extensions = new[] { "ws2" };
            ContainedFormats = new[] { "TXT" };
        }

        public override ArcFile TryOpen (ArcView file)
        {
            if (!file.Name.HasExtension (".ws2"))
                return null;
            using (var input = file.CreateStream())
            {
                if (!AdvHdScript.IsScript (input))
                    return null;
            }

            string basename = Path.GetFileNameWithoutExtension (file.Name);
            var text_entry = Create<Ws2Entry> (basename+".filtered.txt");
            text_entry.Content = Ws2EntryContent.FilteredText;
            text_entry.Offset = 0;
            text_entry.Size = (uint)Math.Min (file.MaxOffset, uint.MaxValue);

            var raw_entry = Create<Ws2Entry> (basename+".raw.txt");
            raw_entry.Content = Ws2EntryContent.RawText;
            raw_entry.Offset = 0;
            raw_entry.Size = text_entry.Size;

            var dump_entry = Create<Ws2Entry> (basename+".dump.txt");
            dump_entry.Content = Ws2EntryContent.Dump;
            dump_entry.Offset = 0;
            dump_entry.Size = text_entry.Size;

            var json_entry = Create<Ws2Entry> (basename+".jsonl");
            json_entry.Content = Ws2EntryContent.JsonLines;
            json_entry.Offset = 0;
            json_entry.Size = text_entry.Size;

            return new ArcFile (file, this, new Entry[] { text_entry, raw_entry, dump_entry, json_entry });
        }

        public override Stream OpenEntry (ArcFile arc, Entry entry)
        {
            var went = entry as Ws2Entry;
            using (var input = arc.File.CreateStream())
            {
                var script = AdvHdScript.Read (input);
                if (null != went && went.Content == Ws2EntryContent.Dump)
                    return script.CreateDumpStream (entry.Name);
                if (null != went && went.Content == Ws2EntryContent.RawText)
                    return script.CreateTextStream (false, entry.Name);
                if (null != went && went.Content == Ws2EntryContent.JsonLines)
                    return ScriptJsonLines.CreateStream (script.ExtractJsonEntries(), entry.Name);
                return script.CreateTextStream (true, entry.Name);
            }
        }
    }

    internal enum Ws2EntryContent
    {
        FilteredText,
        RawText,
        Dump,
        JsonLines,
    }

    internal class Ws2Entry : Entry
    {
        public Ws2EntryContent Content;
    }

    [Export(typeof(ScriptFormat))]
    public class Ws2ScriptFormat : GenericScriptFormat, IConfigurableScriptFormat
    {
        public override string         Tag { get { return AdvHdScript.FormatTag; } }
        public override string Description { get { return "AdvHD WS2 bytecode script"; } }
        public override uint     Signature { get { return 0; } }

        static readonly string[] s_text_modes = { ScriptTextMode.Filtered, ScriptTextMode.Raw, ScriptTextMode.Dump, ScriptTextMode.JsonLines };

        public IEnumerable<string> TextModes { get { return s_text_modes; } }
        public string DefaultTextMode { get { return ScriptTextMode.Filtered; } }

        public Ws2ScriptFormat ()
        {
            Extensions = new[] { "ws2" };
        }

        public override bool IsScript (IBinaryStream file)
        {
            return AdvHdScript.IsScript (file);
        }

        public override Stream ConvertFrom (IBinaryStream file)
        {
            return ConvertFrom (file, ScriptTextMode.Filtered);
        }

        public Stream ConvertFrom (IBinaryStream file, string text_mode)
        {
            var script = AdvHdScript.Read (file);
            if (string.Equals (text_mode, ScriptTextMode.Dump, StringComparison.OrdinalIgnoreCase))
                return script.CreateDumpStream (file.Name);
            if (string.Equals (text_mode, ScriptTextMode.JsonLines, StringComparison.OrdinalIgnoreCase))
                return ScriptJsonLines.CreateStream (script.ExtractJsonEntries(), file.Name);

            bool filter = !string.Equals (text_mode, ScriptTextMode.Raw, StringComparison.OrdinalIgnoreCase);
            return script.CreateTextStream (filter, file.Name);
        }

        public override Stream ConvertBack (IBinaryStream file)
        {
            throw new NotSupportedException();
        }

        public override ScriptData Read (string name, Stream file)
        {
            using (var input = BinaryStream.FromStream (file, name))
            {
                var script = AdvHdScript.Read (input);
                var data = new ScriptData();
                foreach (var line in script.ExtractText (true))
                    data.TextLines.Add (line);
                return data;
            }
        }
    }

    internal sealed class AdvHdScript
    {
        public const string FormatTag = "WS2/AdvHD";

        static readonly string[] NameControlCodes = { "%LC", "%LF", "%LR" };
        static readonly Regex MessageControlCodes = new Regex (@"(?:%\w+)+$", RegexOptions.Compiled);

        readonly byte[] m_data;
        readonly string m_name;
        readonly List<AdvHdTextRange> m_text_ranges;
        readonly int m_version;
        readonly int m_instruction_count;

        AdvHdScript (byte[] data, string name, int version, int instruction_count, List<AdvHdTextRange> text_ranges)
        {
            m_data = data;
            m_name = name;
            m_version = version;
            m_instruction_count = instruction_count;
            m_text_ranges = text_ranges;
        }

        public static bool IsScript (IBinaryStream file)
        {
            long position = file.Position;
            try
            {
                if (!file.Name.HasExtension (".ws2") || file.Length <= 8 || file.Length > int.MaxValue)
                    return false;
                var script = Read (file);
                return script.m_instruction_count > 0 && script.m_text_ranges.Count > 0;
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

        public static AdvHdScript Read (IBinaryStream file)
        {
            if (!file.Name.HasExtension (".ws2") || file.Length <= 8 || file.Length > int.MaxValue)
                throw new InvalidFormatException();
            file.Position = 0;
            var data = file.ReadBytes ((int)file.Length);
            if (data.Length != file.Length)
                throw new EndOfStreamException();

            for (int version = 1; version <= 3; ++version)
            {
                try
                {
                    using (var stream = new MemoryStream (data, false))
                    {
                        var disasm = CreateDisassembler (version, stream);
                        disasm.Disassemble();
                        if (disasm.InstructionCount > 0 && disasm.TextRanges.Count > 0)
                            return new AdvHdScript (data, file.Name, version, disasm.InstructionCount, disasm.TextRanges);
                    }
                }
                catch
                {
                    continue;
                }
            }
            throw new InvalidFormatException();
        }

        static AdvHdDisassembler CreateDisassembler (int version, Stream input)
        {
            switch (version)
            {
            case 1: return new AdvHdDisassemblerV1 (input);
            case 2: return new AdvHdDisassemblerV2 (input);
            case 3: return new AdvHdDisassemblerV3 (input);
            default: throw new ArgumentOutOfRangeException ("version");
            }
        }

        public IEnumerable<ScriptLine> ExtractText (bool filter)
        {
            uint id = 0;
            foreach (var range in m_text_ranges)
            {
                string text = ReadString (range);
                if (filter)
                    text = FilterText (text, range.Type);
                if (!string.IsNullOrWhiteSpace (text))
                    yield return new ScriptLine { Id = id++, Text = text };
            }
        }

        public IEnumerable<ScriptTextEntry> ExtractJsonEntries ()
        {
            var pending_names = new List<string>();
            foreach (var range in m_text_ranges)
            {
                string text = FilterText (ReadString (range), range.Type);
                if (string.IsNullOrWhiteSpace (text))
                    continue;

                if (range.Type == AdvHdTextType.CharacterName)
                {
                    pending_names.Add (text);
                    continue;
                }

                var entry = new ScriptTextEntry (text);
                entry.Names.AddRange (pending_names);
                pending_names.Clear();
                yield return entry;
            }
        }

        public Stream CreateTextStream (bool filter, string name)
        {
            var output = new MemoryStream();
            using (var writer = new StreamWriter (output, new UTF8Encoding (true), 0x400, true))
            {
                foreach (var line in ExtractText (filter))
                    writer.WriteLine (line.Text);
            }
            output.Position = 0;
            return new BinMemoryStream (output, name);
        }

        public Stream CreateDumpStream (string name)
        {
            var output = new MemoryStream();
            using (var writer = new StreamWriter (output, new UTF8Encoding (true), 0x400, true))
                WriteDump (writer);
            output.Position = 0;
            return new BinMemoryStream (output, name);
        }

        void WriteDump (TextWriter writer)
        {
            writer.WriteLine ("# AdvHD WS2 decoded script dump");
            writer.WriteLine ("# This is not reconstructed source; it is a direct bytecode disassembly.");
            writer.WriteLine ();
            writer.WriteLine ("[Header]");
            writer.WriteLine ("Name={0}", m_name);
            writer.WriteLine ("Version={0}", m_version);
            writer.WriteLine ("InstructionCount={0}", m_instruction_count);
            writer.WriteLine ("TextRangeCount={0}", m_text_ranges.Count);
            writer.WriteLine ();

            writer.WriteLine ("[Text]");
            for (int i = 0; i < m_text_ranges.Count; ++i)
            {
                var range = m_text_ranges[i];
                writer.WriteLine ("#{0:D4} 0x{1:X8}+0x{2:X4} {3}", i, range.Offset, range.Length, range.Type);
                writer.WriteLine (ReadString (range));
                writer.WriteLine ();
            }

            writer.WriteLine ("[Instructions]");
            using (var stream = new MemoryStream (m_data, false))
            {
                var disasm = CreateDisassembler (m_version, stream);
                foreach (var line in disasm.DisassembleToText())
                    writer.WriteLine (line);
            }
        }

        string ReadString (AdvHdTextRange range)
        {
            if (range.Offset < 0 || range.Offset >= m_data.Length || range.Length <= 1)
                return "";
            int count = Math.Min (range.Length - 1, m_data.Length - range.Offset);
            return Encodings.cp932.GetString (m_data, range.Offset, count);
        }

        static string FilterText (string text, AdvHdTextType type)
        {
            switch (type)
            {
            case AdvHdTextType.CharacterName:
                for (int i = 0; i < NameControlCodes.Length; ++i)
                    text = text.Replace (NameControlCodes[i], "");
                break;

            case AdvHdTextType.Message:
                text = MessageControlCodes.Replace (text, "");
                text = text.Replace ("\\n", "\r\n");
                break;
            }
            return text;
        }
    }

    internal enum AdvHdTextType
    {
        Internal,
        CharacterName,
        Message,
    }

    internal struct AdvHdTextRange
    {
        public readonly int Offset;
        public readonly int Length;
        public readonly AdvHdTextType Type;

        public AdvHdTextRange (int offset, int length, AdvHdTextType type)
        {
            Offset = offset;
            Length = length;
            Type = type;
        }
    }

    internal struct AdvHdInstruction
    {
        public readonly int Offset;
        public readonly byte Opcode;
        public readonly List<object> Operands;

        public AdvHdInstruction (int offset, byte opcode, List<object> operands)
        {
            Offset = offset;
            Opcode = opcode;
            Operands = operands;
        }
    }

    internal struct AdvHdAddress
    {
        public readonly int OperandOffset;
        public readonly int Address;

        public AdvHdAddress (int operand_offset, int address)
        {
            OperandOffset = operand_offset;
            Address = address;
        }
    }

    internal abstract class AdvHdDisassembler
    {
        readonly Stream m_stream;
        readonly BinaryReader m_reader;
        readonly Dictionary<byte, string> m_operand_templates;
        readonly Dictionary<byte, Action<List<object>>> m_opcode_handlers;
        readonly List<AdvHdTextRange> m_text_ranges = new List<AdvHdTextRange>();

        protected AdvHdDisassembler (Stream input, Dictionary<byte, string> operand_templates)
        {
            m_stream = input;
            m_reader = new BinaryReader (input);
            m_operand_templates = operand_templates;
            m_opcode_handlers = new Dictionary<byte, Action<List<object>>> {
                { 0x0F, HandleChoiceScreen },
                { 0x14, HandleMessage },
                { 0x15, HandleCharacterName },
            };
        }

        public int InstructionCount { get; private set; }
        public List<AdvHdTextRange> TextRanges { get { return m_text_ranges; } }

        public void Disassemble ()
        {
            m_stream.Position = 0;
            while (m_stream.Position < m_stream.Length - 8)
            {
                var instruction = ReadInstruction();
                ++InstructionCount;
                Action<List<object>> handler;
                if (m_opcode_handlers.TryGetValue (instruction.Opcode, out handler))
                    handler (instruction.Operands);
            }
        }

        public IEnumerable<string> DisassembleToText ()
        {
            m_stream.Position = 0;
            while (m_stream.Position < m_stream.Length - 8)
            {
                var instruction = ReadInstruction();
                Action<List<object>> handler;
                if (m_opcode_handlers.TryGetValue (instruction.Opcode, out handler))
                    handler (instruction.Operands);
                yield return string.Format ("{0:X08}: {1:X02} {2}",
                                            instruction.Offset, instruction.Opcode, FormatOperands (instruction.Operands));
            }
        }

        AdvHdInstruction ReadInstruction ()
        {
            int offset = (int)m_stream.Position;
            byte opcode = m_reader.ReadByte();
            string operand_template;
            if (!m_operand_templates.TryGetValue (opcode, out operand_template))
                throw new InvalidFormatException (string.Format ("Unknown AdvHD WS2 opcode 0x{0:X2}", opcode));

            return new AdvHdInstruction (offset, opcode, ReadOperands (operand_template));
        }

        List<object> ReadOperands (string template)
        {
            var operands = new List<object>();
            for (int i = 0; i < template.Length; ++i)
            {
                char type = template[i];
                if (i + 1 < template.Length && template[i+1] == '*')
                {
                    ++i;
                    int count = m_reader.ReadByte();
                    for (int j = 0; j < count; ++j)
                        operands.Add (ReadOperand (type));
                }
                else
                {
                    operands.Add (ReadOperand (type));
                }
            }
            return operands;
        }

        object ReadOperand (char type)
        {
            switch (type)
            {
            case 'b':
                return m_reader.ReadByte();

            case 'h':
                return m_reader.ReadInt16();

            case 'i':
                return m_reader.ReadInt32();

            case 'a':
                {
                    int offset = (int)m_stream.Position;
                    int address = m_reader.ReadInt32();
                    if (address < 0 || address >= m_stream.Length)
                        throw new InvalidFormatException();
                    return new AdvHdAddress (offset, address);
                }

            case 'f':
                return m_reader.ReadSingle();

            case 's':
                {
                    int offset = (int)m_stream.Position;
                    SkipZeroTerminatedString();
                    int length = (int)m_stream.Position - offset;
                    return new AdvHdTextRange (offset, length, AdvHdTextType.Internal);
                }

            default:
                throw new InvalidFormatException();
            }
        }

        void SkipZeroTerminatedString ()
        {
            while (m_stream.Position < m_stream.Length)
            {
                if (0 == m_reader.ReadByte())
                    return;
            }
            throw new EndOfStreamException();
        }

        void HandleCharacterName (List<object> operands)
        {
            AddTextRange ((AdvHdTextRange)operands[0], AdvHdTextType.CharacterName);
        }

        void HandleMessage (List<object> operands)
        {
            AddTextRange ((AdvHdTextRange)operands[2], AdvHdTextType.Message);
        }

        void HandleChoiceScreen (List<object> operands)
        {
            int count = (byte)operands[0];
            for (int i = 0; i < count; ++i)
            {
                var choice_operands = ReadOperands ("hsbh");
                operands.AddRange (choice_operands);
                AddTextRange ((AdvHdTextRange)choice_operands[1], AdvHdTextType.Message);

                var jump = ReadInstruction();
                operands.AddRange (jump.Operands);
            }
        }

        void AddTextRange (AdvHdTextRange range, AdvHdTextType type)
        {
            if (range.Length > 1)
                m_text_ranges.Add (new AdvHdTextRange (range.Offset, range.Length, type));
        }

        string FormatOperands (IEnumerable<object> operands)
        {
            var builder = new StringBuilder();
            bool first = true;
            foreach (var operand in operands)
            {
                if (!first)
                    builder.Append (", ");
                builder.Append (FormatOperand (operand));
                first = false;
            }
            return builder.ToString();
        }

        string FormatOperand (object operand)
        {
            if (operand is AdvHdAddress)
            {
                var address = (AdvHdAddress)operand;
                return string.Format ("0x{0:X}", address.Address);
            }
            if (operand is AdvHdTextRange)
            {
                var range = (AdvHdTextRange)operand;
                return "\"" + ReadString (range).Replace ("\\", "\\\\").Replace ("\"", "\\\"")
                                               .Replace ("\r", "\\r").Replace ("\n", "\\n") + "\"";
            }
            if (operand is int)
                return string.Format ("0x{0:X}", (int)operand);
            if (operand is float)
                return ((float)operand).ToString (CultureInfo.InvariantCulture);
            return null != operand ? operand.ToString() : "";
        }

        string ReadString (AdvHdTextRange range)
        {
            long position = m_stream.Position;
            try
            {
                m_stream.Position = range.Offset;
                var bytes = new byte[range.Length > 0 ? range.Length - 1 : 0];
                int read = m_stream.Read (bytes, 0, bytes.Length);
                if (read != bytes.Length)
                    throw new EndOfStreamException();
                return Encodings.cp932.GetString (bytes);
            }
            finally
            {
                m_stream.Position = position;
            }
        }
    }

    internal sealed class AdvHdDisassemblerV1 : AdvHdDisassembler
    {
        static readonly Dictionary<byte, string> OperandTemplates = new Dictionary<byte, string> {
            { 0x00, "" },
            { 0x01, "bhfaa" },
            { 0x02, "a" },
            { 0x04, "s" },
            { 0x05, "" },
            { 0x06, "a" },
            { 0x07, "s" },
            { 0x08, "b" },
            { 0x09, "bhf" },
            { 0x0A, "hf" },
            { 0x0B, "hb" },
            { 0x0C, "hbh*" },
            { 0x0D, "hhf" },
            { 0x0E, "hhb" },
            { 0x0F, "b" },
            { 0x11, "sf" },
            { 0x12, "sbs" },
            { 0x13, "" },
            { 0x14, "iss" },
            { 0x15, "s" },
            { 0x16, "b" },
            { 0x17, "" },
            { 0x18, "bs" },
            { 0x19, "" },
            { 0x1A, "s" },
            { 0x1B, "b" },
            { 0x1C, "ssh" },
            { 0x1D, "h" },
            { 0x1E, "ssffhhb" },
            { 0x1F, "sf" },
            { 0x20, "sfh" },
            { 0x21, "shhh" },
            { 0x22, "sb" },
            { 0x28, "ssffhhbhhb" },
            { 0x29, "sf" },
            { 0x2A, "sfh" },
            { 0x2B, "s" },
            { 0x2C, "s" },
            { 0x2D, "sb" },
            { 0x2E, "" },
            { 0x2F, "shf" },
            { 0x32, "s" },
            { 0x33, "ssbb" },
            { 0x34, "ssbb" },
            { 0x35, "ssbbb" },
            { 0x36, "sfffffffbb" },
            { 0x37, "s" },
            { 0x38, "sb" },
            { 0x39, "sbbh*" },
            { 0x3A, "sbb" },
            { 0x3B, "sshhhffffffff" },
            { 0x3C, "s" },
            { 0x3D, "h" },
            { 0x3E, "" },
            { 0x3F, "s*" },
            { 0x40, "ssb" },
            { 0x41, "sb" },
            { 0x42, "sh" },
            { 0x43, "s" },
            { 0x44, "ssb" },
            { 0x45, "shffff" },
            { 0x46, "shbffff" },
            { 0x47, "sshbbfffffhf" },
            { 0x48, "sshbbs" },
            { 0x49, "sss" },
            { 0x4A, "ss" },
            { 0x4B, "shhffff" },
            { 0x4C, "shhbffff" },
            { 0x4D, "sshhbbfffffhf" },
            { 0x4E, "sshhbbs" },
            { 0x4F, "sshs" },
            { 0x50, "ssh" },
            { 0x51, "sshfb" },
            { 0x52, "ssfhfbs" },
            { 0x53, "ss" },
            { 0x54, "sss" },
            { 0x55, "ss" },
            { 0x56, "sbhfffffffffffbffffbhshssf" },
            { 0x57, "sh" },
            { 0x58, "ss" },
            { 0x59, "ssh" },
            { 0x5A, "sh*" },
            { 0x5B, "shb" },
            { 0x5C, "s" },
            { 0x5D, "ssb" },
            { 0x5E, "sff" },
            { 0x64, "b" },
            { 0x65, "hbffbs" },
            { 0x66, "s" },
            { 0x67, "bbhfffffb" },
            { 0x68, "b" },
            { 0x6E, "ss" },
            { 0x6F, "s" },
            { 0x70, "sh" },
            { 0x71, "" },
            { 0x72, "shhs" },
            { 0x73, "ssh" },
            { 0xFA, "" },
            { 0xFB, "b" },
            { 0xFC, "h" },
            { 0xFD, "" },
            { 0xFE, "s" },
            { 0xFF, "" },
        };

        public AdvHdDisassemblerV1 (Stream input) : base (input, OperandTemplates)
        {
        }
    }

    internal sealed class AdvHdDisassemblerV2 : AdvHdDisassembler
    {
        static readonly Dictionary<byte, string> OperandTemplates = new Dictionary<byte, string> {
            { 0x00, "" },
            { 0x01, "bhfaa" },
            { 0x02, "a" },
            { 0x04, "s" },
            { 0x05, "" },
            { 0x06, "a" },
            { 0x07, "s" },
            { 0x08, "b" },
            { 0x09, "bhf" },
            { 0x0A, "hf" },
            { 0x0B, "hb" },
            { 0x0C, "hbh*" },
            { 0x0D, "hhf" },
            { 0x0E, "hhb" },
            { 0x0F, "b" },
            { 0x11, "sf" },
            { 0x12, "sbs" },
            { 0x13, "" },
            { 0x14, "iss" },
            { 0x15, "s" },
            { 0x16, "b" },
            { 0x17, "" },
            { 0x18, "bs" },
            { 0x19, "" },
            { 0x1A, "s" },
            { 0x1B, "b" },
            { 0x1C, "sshb" },
            { 0x1D, "h" },
            { 0x1E, "ssffhhb" },
            { 0x1F, "sf" },
            { 0x20, "sfh" },
            { 0x21, "shhh" },
            { 0x22, "sb" },
            { 0x28, "ssffhhbhhb" },
            { 0x29, "sf" },
            { 0x2A, "sfh" },
            { 0x2B, "s" },
            { 0x2C, "s" },
            { 0x2D, "sb" },
            { 0x2E, "" },
            { 0x2F, "shf" },
            { 0x32, "s" },
            { 0x33, "ssbb" },
            { 0x34, "ssbb" },
            { 0x35, "ssbbb" },
            { 0x36, "sfffffffbb" },
            { 0x37, "s" },
            { 0x38, "sb" },
            { 0x39, "sbbh*" },
            { 0x3A, "sbb" },
            { 0x3B, "sshhhffffffff" },
            { 0x3C, "s" },
            { 0x3D, "h" },
            { 0x3E, "" },
            { 0x3F, "s*" },
            { 0x40, "ssb" },
            { 0x41, "sb" },
            { 0x42, "sh" },
            { 0x43, "s" },
            { 0x44, "ssb" },
            { 0x45, "shffff" },
            { 0x46, "shbffff" },
            { 0x47, "sshbbfffffhf" },
            { 0x48, "sshbbs" },
            { 0x49, "sss" },
            { 0x4A, "ss" },
            { 0x4B, "shhffff" },
            { 0x4C, "shhbffff" },
            { 0x4D, "sshhbbfffffhf" },
            { 0x4E, "sshhbbs" },
            { 0x4F, "sshs" },
            { 0x50, "ssh" },
            { 0x51, "sshfb" },
            { 0x52, "ssfhfbs" },
            { 0x53, "ss" },
            { 0x54, "sss" },
            { 0x55, "ss" },
            { 0x56, "sbhfffffffffffbffffbhshssf" },
            { 0x57, "sh" },
            { 0x58, "ss" },
            { 0x59, "ssh" },
            { 0x5A, "sh*" },
            { 0x5B, "shb" },
            { 0x5C, "s" },
            { 0x5D, "ssb" },
            { 0x5E, "sff" },
            { 0x5F, "s" },
            { 0x60, "hhhh" },
            { 0x61, "bffff" },
            { 0x62, "s" },
            { 0x63, "sb" },
            { 0x64, "b" },
            { 0x65, "hbffbs" },
            { 0x66, "s" },
            { 0x67, "bbhfffffb" },
            { 0x68, "b" },
            { 0x69, "sbbfffffhf" },
            { 0x6A, "shbbs" },
            { 0x6E, "ss" },
            { 0x6F, "s" },
            { 0x70, "sh" },
            { 0x71, "" },
            { 0x72, "shhs" },
            { 0x73, "ssh" },
            { 0x74, "ss" },
            { 0x75, "ss" },
            { 0x78, "ssbb" },
            { 0x79, "ssf" },
            { 0x7A, "ssfbbs" },
            { 0x7B, "ss" },
            { 0x7C, "ssf" },
            { 0x7D, "sf" },
            { 0x7E, "s" },
            { 0xC8, "" },
            { 0xC9, "sshhh" },
            { 0xCA, "ss" },
            { 0xCB, "sbb" },
            { 0xCC, "" },
            { 0xCD, "sssssfb" },
            { 0xCE, "b" },
            { 0xCF, "ssf" },
            { 0xD0, "sh" },
            { 0xD1, "sh" },
            { 0xD2, "s" },
            { 0xD3, "s" },
            { 0xD4, "shh" },
            { 0xF8, "" },
            { 0xF9, "bs" },
            { 0xFA, "" },
            { 0xFB, "b" },
            { 0xFC, "h" },
            { 0xFD, "" },
            { 0xFE, "s" },
            { 0xFF, "" },
        };

        public AdvHdDisassemblerV2 (Stream input) : base (input, OperandTemplates)
        {
        }
    }

    internal sealed class AdvHdDisassemblerV3 : AdvHdDisassembler
    {
        static readonly Dictionary<byte, string> OperandTemplates = new Dictionary<byte, string> {
            { 0x00, "" },
            { 0x01, "bhfaa" },
            { 0x02, "a" },
            { 0x04, "s" },
            { 0x05, "" },
            { 0x06, "a" },
            { 0x07, "s" },
            { 0x08, "b" },
            { 0x09, "bhf" },
            { 0x0A, "hf" },
            { 0x0B, "hb" },
            { 0x0C, "hbh*" },
            { 0x0D, "hhf" },
            { 0x0E, "hhb" },
            { 0x0F, "b" },
            { 0x11, "sbf" },
            { 0x12, "sbs" },
            { 0x13, "" },
            { 0x14, "issb" },
            { 0x15, "sb" },
            { 0x16, "bb" },
            { 0x17, "" },
            { 0x18, "bs" },
            { 0x19, "" },
            { 0x1A, "s" },
            { 0x1B, "b" },
            { 0x1C, "sshb" },
            { 0x1D, "h" },
            { 0x1E, "ssffhhbf" },
            { 0x1F, "sf" },
            { 0x20, "sfh" },
            { 0x21, "shhh" },
            { 0x22, "sb" },
            { 0x28, "ssffhhbhhbf" },
            { 0x29, "sf" },
            { 0x2A, "sfh" },
            { 0x2B, "s" },
            { 0x2C, "s" },
            { 0x2D, "sb" },
            { 0x2E, "" },
            { 0x2F, "shf" },
            { 0x32, "s" },
            { 0x33, "ssbb" },
            { 0x34, "ssbb" },
            { 0x35, "ssbbb" },
            { 0x36, "sfffffffbb" },
            { 0x37, "s" },
            { 0x38, "sb" },
            { 0x39, "sbbh*" },
            { 0x3A, "sbb" },
            { 0x3B, "sshhhffffffff" },
            { 0x3C, "s" },
            { 0x3D, "h" },
            { 0x3E, "" },
            { 0x3F, "s*" },
            { 0x40, "ssb" },
            { 0x41, "sb" },
            { 0x42, "sh" },
            { 0x43, "s" },
            { 0x44, "ssb" },
            { 0x45, "shffff" },
            { 0x46, "shbffff" },
            { 0x47, "sshbbfffffhf" },
            { 0x48, "sshbbs" },
            { 0x49, "sss" },
            { 0x4A, "ss" },
            { 0x4B, "shhffff" },
            { 0x4C, "shhbffff" },
            { 0x4D, "sshhbbfffffhf" },
            { 0x4E, "sshhbbs" },
            { 0x4F, "sshs" },
            { 0x50, "ssh" },
            { 0x51, "sshfb" },
            { 0x52, "ssfhfbs" },
            { 0x53, "ss" },
            { 0x54, "sss" },
            { 0x55, "ss" },
            { 0x56, "sbhfffffffffffbffffbhshssf" },
            { 0x57, "sh" },
            { 0x58, "ss" },
            { 0x59, "ssh" },
            { 0x5A, "sh*" },
            { 0x5B, "shb" },
            { 0x5C, "s" },
            { 0x5D, "ssb" },
            { 0x5E, "sff" },
            { 0x5F, "s" },
            { 0x60, "hhhh" },
            { 0x61, "bffff" },
            { 0x62, "s" },
            { 0x63, "sb" },
            { 0x64, "b" },
            { 0x65, "hbffbs" },
            { 0x66, "s" },
            { 0x67, "bbhfffffb" },
            { 0x68, "b" },
            { 0x69, "sbbfffffhf" },
            { 0x6A, "shbbs" },
            { 0x6B, "ss" },
            { 0x6C, "sff" },
            { 0x6E, "ss" },
            { 0x6F, "s" },
            { 0x70, "sh" },
            { 0x71, "" },
            { 0x72, "shhs" },
            { 0x73, "ssh" },
            { 0x74, "ss" },
            { 0x75, "ss" },
            { 0x78, "ssbbb" },
            { 0x79, "ssf" },
            { 0x7A, "ssfbbs" },
            { 0x7B, "ss" },
            { 0x7C, "ssf" },
            { 0x7D, "sf" },
            { 0x7E, "s" },
            { 0x7F, "sfffff" },
            { 0x80, "s" },
            { 0x81, "sbsffb" },
            { 0x82, "ssf" },
            { 0x83, "ssff" },
            { 0x84, "sssfhf" },
            { 0x85, "ssbf" },
            { 0x86, "sfff" },
            { 0x87, "sf" },
            { 0x88, "sssfhf" },
            { 0x8C, "sssbb" },
            { 0x8D, "issbbs" },
            { 0x8E, "issbbs" },
            { 0x8F, "ss" },
            { 0x90, "s" },
            { 0x96, "hffff" },
            { 0x97, "hbffff" },
            { 0x98, "shbbfffffhf" },
            { 0x99, "shbbs" },
            { 0x9A, "" },
            { 0x9B, "s" },
            { 0x9C, "ss" },
            { 0x9D, "s" },
            { 0x9E, "sb" },
            { 0x9F, "sb" },
            { 0xC8, "" },
            { 0xC9, "sshhhh" },
            { 0xCA, "ss" },
            { 0xCB, "sbb" },
            { 0xCC, "" },
            { 0xCD, "sssssfb" },
            { 0xCE, "b" },
            { 0xCF, "ssf" },
            { 0xD0, "sh" },
            { 0xD1, "sh" },
            { 0xD2, "s" },
            { 0xD3, "s" },
            { 0xD4, "shh" },
            { 0xE6, "ii" },
            { 0xE7, "" },
            { 0xE8, "" },
            { 0xF0, "b" },
            { 0xF8, "" },
            { 0xF9, "bs" },
            { 0xFA, "" },
            { 0xFB, "b" },
            { 0xFC, "h" },
            { 0xFD, "" },
            { 0xFE, "s" },
            { 0xFF, "" },
        };

        public AdvHdDisassemblerV3 (Stream input) : base (input, OperandTemplates)
        {
        }
    }
}
