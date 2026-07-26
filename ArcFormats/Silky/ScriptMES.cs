//! \file       ScriptMES.cs
//! \date       Sun Jul 26 2026
//! \brief      Silky's/AI6WIN MES and MAP script text extractors.
//
// Silky's bytecode parsing is adapted from VNTranslationTools.
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
using GameRes.Utility;

namespace GameRes.Formats.Silky
{
    [Export(typeof(ScriptFormat))]
    public class SilkysMesScriptFormat : GenericScriptFormat, IConfigurableScriptFormat
    {
        public const string FormatTag = "MES/SILKY'S";

        static readonly string[] s_text_modes = {
            ScriptTextMode.Filtered,
            ScriptTextMode.Raw,
            ScriptTextMode.Dump,
            ScriptTextMode.JsonLines,
        };

        public override string         Tag { get { return FormatTag; } }
        public override string Description { get { return "Silky's/AI6WIN bytecode script"; } }
        public override uint     Signature { get { return 0; } }

        public IEnumerable<string> TextModes { get { return s_text_modes; } }
        public string DefaultTextMode { get { return ScriptTextMode.Filtered; } }

        public SilkysMesScriptFormat ()
        {
            Extensions = new[] { "mes" };
        }

        public override bool IsScript (IBinaryStream file)
        {
            return SilkysMesScript.IsScript (file);
        }

        public override Stream ConvertFrom (IBinaryStream file)
        {
            return ConvertFrom (file, ScriptTextMode.Filtered);
        }

        public Stream ConvertFrom (IBinaryStream file, string text_mode)
        {
            var script = SilkysMesScript.Read (file);
            if (string.Equals (text_mode, ScriptTextMode.JsonLines, StringComparison.OrdinalIgnoreCase))
                return ScriptJsonLines.CreateStream (script.ExtractJsonEntries(), file.Name);

            var output = new MemoryStream();
            using (var writer = new StreamWriter (output, new UTF8Encoding (true), 0x400, true))
            {
                if (string.Equals (text_mode, ScriptTextMode.Dump, StringComparison.OrdinalIgnoreCase))
                {
                    script.WriteDump (writer);
                }
                else
                {
                    bool filtered = !string.Equals (text_mode, ScriptTextMode.Raw, StringComparison.OrdinalIgnoreCase);
                    foreach (var line in script.ExtractText (filtered))
                        writer.WriteLine (line.Text);
                }
            }
            return new BinMemoryStream (output.ToArray(), file.Name);
        }

        public override Stream ConvertBack (IBinaryStream file)
        {
            throw new NotSupportedException();
        }

        public override ScriptData Read (string name, Stream file)
        {
            using (var input = BinaryStream.FromStream (file, name))
            {
                var script = SilkysMesScript.Read (input);
                var data = new ScriptData();
                foreach (var line in script.ExtractText (true))
                    data.TextLines.Add (line);
                return data;
            }
        }
    }

    [Export(typeof(ScriptFormat))]
    public class SilkysMapScriptFormat : GenericScriptFormat, IConfigurableScriptFormat
    {
        public const string FormatTag = "MAP/SILKY'S";

        static readonly string[] s_text_modes = {
            ScriptTextMode.Filtered,
            ScriptTextMode.Raw,
            ScriptTextMode.Dump,
            ScriptTextMode.JsonLines,
        };

        public override string         Tag { get { return FormatTag; } }
        public override string Description { get { return "Silky's UTF-16 message map"; } }
        public override uint     Signature { get { return 0; } }

        public IEnumerable<string> TextModes { get { return s_text_modes; } }
        public string DefaultTextMode { get { return ScriptTextMode.Filtered; } }

        public SilkysMapScriptFormat ()
        {
            Extensions = new[] { "map" };
        }

        public override bool IsScript (IBinaryStream file)
        {
            return SilkysMapScript.IsScript (file);
        }

        public override Stream ConvertFrom (IBinaryStream file)
        {
            return ConvertFrom (file, ScriptTextMode.Filtered);
        }

        public Stream ConvertFrom (IBinaryStream file, string text_mode)
        {
            var script = SilkysMapScript.Read (file);
            if (string.Equals (text_mode, ScriptTextMode.JsonLines, StringComparison.OrdinalIgnoreCase))
                return ScriptJsonLines.CreateStream (script.ExtractJsonEntries(), file.Name);

            var output = new MemoryStream();
            using (var writer = new StreamWriter (output, new UTF8Encoding (true), 0x400, true))
            {
                if (string.Equals (text_mode, ScriptTextMode.Dump, StringComparison.OrdinalIgnoreCase))
                {
                    script.WriteDump (writer);
                }
                else
                {
                    foreach (var line in script.ExtractText())
                        writer.WriteLine (line.Text);
                }
            }
            return new BinMemoryStream (output.ToArray(), file.Name);
        }

        public override Stream ConvertBack (IBinaryStream file)
        {
            throw new NotSupportedException();
        }

        public override ScriptData Read (string name, Stream file)
        {
            using (var input = BinaryStream.FromStream (file, name))
            {
                var script = SilkysMapScript.Read (input);
                var data = new ScriptData();
                foreach (var line in script.ExtractText())
                    data.TextLines.Add (line);
                return data;
            }
        }
    }

    internal sealed class SilkysMesScript
    {
        const int MaxMessageCount = 0x100000;
        const byte EscapeLineBreak = 0;
        const byte EscapeRuby = 1;

        static readonly Regex s_inline_name = new Regex (
            @"^〈(?<name>.+?)〉：(?<message>.+)", RegexOptions.Singleline | RegexOptions.CultureInvariant);

        static readonly SilkysOpcodeSet s_ai6win = CreateAi6WinOpcodes();
        static readonly SilkysOpcodeSet s_silkys_plus = CreateSilkysPlusOpcodes();

        readonly byte[] m_data;
        readonly string m_name;
        readonly SilkysOpcodeSet m_opcodes;
        readonly List<SilkysInstruction> m_instructions = new List<SilkysInstruction>();
        readonly List<SilkysTextRange> m_text_ranges = new List<SilkysTextRange>();
        readonly int m_message_count;
        readonly int m_special_message_count;
        readonly int m_code_offset;

        SilkysMesScript (byte[] data, string name)
        {
            m_data = data;
            m_name = name;
            if (!TryReadHeader (data, out m_opcodes, out m_message_count,
                                out m_special_message_count, out m_code_offset))
                throw new InvalidFormatException();
            ReadCode();
        }

        public static bool IsScript (IBinaryStream file)
        {
            if (!file.Name.HasExtension (".mes") || file.Length > int.MaxValue)
                return false;
            long position = file.Position;
            try
            {
                SilkysMesScript script;
                return TryRead (file, out script);
            }
            finally
            {
                if (file.CanSeek)
                    file.Position = position;
            }
        }

        public static SilkysMesScript Read (IBinaryStream file)
        {
            if (!file.Name.HasExtension (".mes") || file.Length > int.MaxValue)
                throw new InvalidFormatException();
            SilkysMesScript script;
            if (!TryRead (file, out script))
                throw new InvalidFormatException();
            return script;
        }

        static bool TryRead (IBinaryStream file, out SilkysMesScript script)
        {
            script = null;
            if (file.Length < 9 || file.Length > int.MaxValue)
                return false;
            try
            {
                file.Position = 0;
                var data = file.ReadBytes ((int)file.Length);
                if (data.Length != file.Length)
                    return false;
                script = new SilkysMesScript (data, file.Name);
                return true;
            }
            catch
            {
                return false;
            }
        }

        static bool TryReadHeader (byte[] data, out SilkysOpcodeSet opcodes, out int message_count,
                                   out int special_message_count, out int code_offset)
        {
            opcodes = null;
            message_count = 0;
            special_message_count = 0;
            code_offset = 0;
            if (data.Length < 9)
                return false;

            message_count = LittleEndian.ToInt32 (data, 0);
            if (message_count <= 0 || message_count > MaxMessageCount)
                return false;

            long ai6_code_offset = 4L + 4L * message_count;
            if (ai6_code_offset <= data.Length - 5)
            {
                int first_message = LittleEndian.ToInt32 (data, 4);
                long marker_offset = ai6_code_offset + first_message;
                if (first_message >= 0 && marker_offset >= ai6_code_offset
                    && marker_offset <= data.Length - 5)
                {
                    int marker = (int)marker_offset;
                    if (data[marker] == 0x19
                        && data[marker+1] == 0
                        && data[marker+2] == 0
                        && data[marker+3] == 0
                        && data[marker+4] == 0)
                    {
                        opcodes = s_ai6win;
                        code_offset = (int)ai6_code_offset;
                        return IsSaneAddressTable (data, 4, message_count, code_offset);
                    }
                }
            }

            special_message_count = LittleEndian.ToInt32 (data, 4);
            if (special_message_count < 0 || special_message_count > MaxMessageCount)
                return false;
            long total_count = (long)message_count + special_message_count;
            if (total_count > MaxMessageCount)
                return false;
            long plus_code_offset = 8L + 4L * total_count;
            if (plus_code_offset >= data.Length)
                return false;
            opcodes = s_silkys_plus;
            code_offset = (int)plus_code_offset;
            return IsSaneAddressTable (data, 8, (int)total_count, code_offset);
        }

        static bool IsSaneAddressTable (byte[] data, int table_offset, int count, int code_offset)
        {
            if (count <= 0 || table_offset < 0 || code_offset <= table_offset || code_offset >= data.Length)
                return false;
            if ((long)table_offset + 4L * count != code_offset)
                return false;
            int code_size = data.Length - code_offset;
            for (int i = 0; i < count; ++i)
            {
                int address = LittleEndian.ToInt32 (data, table_offset + 4 * i);
                if (address < 0 || address >= code_size)
                    return false;
            }
            return true;
        }

        void ReadCode ()
        {
            int position = m_code_offset;
            while (position < m_data.Length)
                m_instructions.Add (ReadInstruction (ref position));
            if (position != m_data.Length || 0 == m_instructions.Count)
                throw new InvalidFormatException();
            FindTextRanges();
        }

        SilkysInstruction ReadInstruction (ref int position)
        {
            int offset = position;
            byte opcode = ReadByte (ref position);
            string template;
            if (!m_opcodes.OperandTemplates.TryGetValue (opcode, out template))
                throw new InvalidFormatException();

            var operands = new List<object> (template.Length);
            foreach (char type in template)
            {
                switch (type)
                {
                case 'b':
                    operands.Add (ReadByte (ref position));
                    break;

                case 'i':
                    operands.Add (ReadInt32 (ref position));
                    break;

                case 'a':
                    int address = ReadInt32 (ref position);
                    if (address < 0 || address > m_data.Length - m_code_offset)
                        throw new InvalidFormatException();
                    operands.Add (address);
                    break;

                case 's':
                case 't':
                    int text_offset = position;
                    int end = Array.IndexOf<byte> (m_data, 0, position);
                    if (end < position)
                        throw new InvalidFormatException();
                    position = end + 1;
                    operands.Add (new SilkysTextRange {
                        Offset = text_offset,
                        Length = position - text_offset,
                        Type = SilkysTextType.Internal,
                    });
                    break;

                default:
                    throw new InvalidFormatException();
                }
            }
            return new SilkysInstruction {
                Offset = offset,
                EndOffset = position,
                Opcode = opcode,
                Operands = operands,
            };
        }

        byte ReadByte (ref int position)
        {
            if (position < 0 || position >= m_data.Length)
                throw new InvalidFormatException();
            return m_data[position++];
        }

        int ReadInt32 (ref int position)
        {
            if (position < 0 || position > m_data.Length - 4)
                throw new InvalidFormatException();
            int value = BigEndian.ToInt32 (m_data, position);
            position += 4;
            return value;
        }

        void FindTextRanges ()
        {
            var stack = new Stack<object>();
            int message_start = -1;
            bool in_ruby = false;

            for (int i = 0; i < m_instructions.Count; ++i)
            {
                var instruction = m_instructions[i];
                HandleMessageInstruction (i, instruction, ref message_start, ref in_ruby);
                HandleNameInstruction (instruction, stack);
            }
            if (message_start >= 0 && message_start < m_data.Length)
            {
                m_text_ranges.Add (new SilkysTextRange {
                    Offset = message_start,
                    Length = m_data.Length - message_start,
                    Type = SilkysTextType.Message,
                });
            }
        }

        void HandleMessageInstruction (int index, SilkysInstruction instruction,
                                       ref int message_start, ref bool in_ruby)
        {
            byte opcode = instruction.Opcode;
            if (opcode == m_opcodes.Message1 || opcode == m_opcodes.Message2)
            {
                if (message_start < 0)
                    message_start = instruction.Offset;
            }
            else if (opcode == m_opcodes.EscapeSequence)
            {
                if ((byte)instruction.Operands[0] == EscapeRuby)
                    in_ruby = true;
            }
            else if (opcode == m_opcodes.Yield && in_ruby)
            {
                in_ruby = false;
            }
            else if (opcode == m_opcodes.PushInt
                     && index + 1 < m_instructions.Count
                     && m_instructions[index+1].Offset == instruction.EndOffset
                     && m_instructions[index+1].Opcode == m_opcodes.LineNumber)
            {
            }
            else if (opcode == m_opcodes.LineNumber
                     || opcode == m_opcodes.Nop1
                     || opcode == m_opcodes.Nop2)
            {
            }
            else
            {
                if (message_start >= 0)
                {
                    m_text_ranges.Add (new SilkysTextRange {
                        Offset = message_start,
                        Length = instruction.Offset - message_start,
                        Type = SilkysTextType.Message,
                    });
                }
                message_start = -1;
                in_ruby = false;
            }
        }

        void HandleNameInstruction (SilkysInstruction instruction, Stack<object> stack)
        {
            byte opcode = instruction.Opcode;
            if (opcode == m_opcodes.PushInt || opcode == m_opcodes.PushString)
            {
                stack.Push (instruction.Operands[0]);
            }
            else if (opcode == m_opcodes.Add && stack.Count >= 2)
            {
                object value1 = stack.Pop();
                object value2 = stack.Pop();
                if (value1 is int && value2 is int)
                    stack.Push (unchecked ((int)value1 + (int)value2));
            }
            else if (opcode == m_opcodes.Syscall && stack.Count == 3)
            {
                object function = stack.Pop();
                object exec = stack.Pop();
                object name = stack.Pop();
                if (function is int && exec is int && name is SilkysTextRange
                    && IsNameSyscall ((int)function, (int)exec))
                {
                    var range = (SilkysTextRange)name;
                    m_text_ranges.Add (new SilkysTextRange {
                        Offset = range.Offset - 1,
                        Length = range.Length + 1,
                        Type = SilkysTextType.CharacterName,
                    });
                }
                stack.Clear();
            }
            else
            {
                stack.Clear();
            }
        }

        bool IsNameSyscall (int function, int exec)
        {
            foreach (var syscall in m_opcodes.NameSyscalls)
            {
                if (syscall.Function == function && syscall.Exec == exec)
                    return true;
            }
            return false;
        }

        public IEnumerable<ScriptLine> ExtractText (bool filtered)
        {
            uint id = 0;
            foreach (var range in m_text_ranges)
            {
                string text = DecodeText (range);
                if (string.IsNullOrEmpty (text))
                    continue;
                string inline_name;
                string message;
                if (filtered && range.Type == SilkysTextType.Message
                    && TrySplitInlineName (text, out inline_name, out message))
                {
                    yield return new ScriptLine { Id = id++, Text = inline_name };
                    if (!string.IsNullOrEmpty (message))
                        yield return new ScriptLine { Id = id++, Text = message };
                }
                else
                {
                    yield return new ScriptLine { Id = id++, Text = text };
                }
            }
        }

        public IEnumerable<ScriptTextEntry> ExtractJsonEntries ()
        {
            var pending_names = new List<string>();
            foreach (var range in m_text_ranges)
            {
                string text = DecodeText (range);
                if (range.Type == SilkysTextType.CharacterName)
                {
                    if (!string.IsNullOrWhiteSpace (text))
                        pending_names.Add (text);
                    continue;
                }

                string inline_name;
                string message;
                if (!TrySplitInlineName (text, out inline_name, out message))
                    message = text;
                if (string.IsNullOrWhiteSpace (message))
                {
                    pending_names.Clear();
                    continue;
                }

                var entry = new ScriptTextEntry (message);
                foreach (var name in pending_names)
                {
                    if (!entry.Names.Contains (name))
                        entry.Names.Add (name);
                }
                if (!string.IsNullOrWhiteSpace (inline_name) && !entry.Names.Contains (inline_name))
                    entry.Names.Add (inline_name);
                pending_names.Clear();
                yield return entry;
            }
        }

        static bool TrySplitInlineName (string text, out string name, out string message)
        {
            var match = s_inline_name.Match (text ?? string.Empty);
            if (match.Success)
            {
                name = match.Groups["name"].Value;
                message = match.Groups["message"].Value;
                return true;
            }
            name = null;
            message = text;
            return false;
        }

        string DecodeText (SilkysTextRange range)
        {
            var result = new StringBuilder();
            int range_end = checked (range.Offset + range.Length);
            foreach (var instruction in m_instructions)
            {
                if (instruction.Offset < range.Offset)
                    continue;
                if (instruction.Offset >= range_end)
                    break;
                if (instruction.EndOffset > range_end)
                    throw new InvalidFormatException();

                byte opcode = instruction.Opcode;
                if (opcode == m_opcodes.PushString
                    || (opcode == m_opcodes.Message1 && !m_opcodes.IsMessage1Obfuscated)
                    || opcode == m_opcodes.Message2)
                {
                    result.Append (DecodeString ((SilkysTextRange)instruction.Operands[0]));
                }
                else if (opcode == m_opcodes.Message1 && m_opcodes.IsMessage1Obfuscated)
                {
                    result.Append (DecodeObfuscatedString ((SilkysTextRange)instruction.Operands[0]));
                }
                else if (opcode == m_opcodes.EscapeSequence)
                {
                    byte escape = (byte)instruction.Operands[0];
                    if (EscapeLineBreak == escape)
                        result.Append ("\r\n");
                    else if (EscapeRuby == escape)
                        result.Append ('[');
                    else
                        throw new InvalidFormatException();
                }
                else if (opcode == m_opcodes.Yield)
                {
                    result.Append (']');
                }
            }
            return result.ToString();
        }

        string DecodeString (SilkysTextRange range)
        {
            if (range.Length <= 1 || range.Offset < 0 || range.Offset > m_data.Length - range.Length)
                return string.Empty;
            return Encodings.cp932.GetString (m_data, range.Offset, range.Length - 1);
        }

        string DecodeObfuscatedString (SilkysTextRange range)
        {
            int input = range.Offset;
            int input_end = checked (range.Offset + range.Length - 1);
            if (range.Length <= 1 || input < 0 || input_end > m_data.Length)
                return string.Empty;
            var output = new byte[2 * (range.Length - 1)];
            int output_pos = 0;
            while (input < input_end)
            {
                byte b = m_data[input++];
                if ((b >= 0x81 && b < 0xA0) || (b >= 0xE0 && b < 0xF0))
                {
                    if (input >= input_end)
                        throw new InvalidFormatException();
                    output[output_pos++] = b;
                    output[output_pos++] = m_data[input++];
                }
                else
                {
                    int c = b - 0x7D62;
                    output[output_pos++] = (byte)(c >> 8);
                    output[output_pos++] = (byte)c;
                }
            }
            return Encodings.cp932.GetString (output, 0, output_pos);
        }

        public void WriteDump (TextWriter writer)
        {
            writer.WriteLine ("# Silky's/AI6WIN MES decoded dump");
            writer.WriteLine ("# This is not reconstructed source; it is a direct dump of decoded bytecode.");
            writer.WriteLine ();
            writer.WriteLine ("[Header]");
            writer.WriteLine ("Name={0}", m_name);
            writer.WriteLine ("Variant={0}", m_opcodes.Name);
            writer.WriteLine ("MessageCount={0}", m_message_count);
            writer.WriteLine ("SpecialMessageCount={0}", m_special_message_count);
            writer.WriteLine ("CodeOffset=0x{0:X8}", m_code_offset);
            writer.WriteLine ("CodeSize=0x{0:X8}", m_data.Length - m_code_offset);
            writer.WriteLine ();

            writer.WriteLine ("[Text]");
            for (int i = 0; i < m_text_ranges.Count; ++i)
            {
                var range = m_text_ranges[i];
                writer.WriteLine ("#{0:D4} 0x{1:X8}+0x{2:X4} {3}",
                                  i, range.Offset, range.Length, range.Type);
                writer.WriteLine (DecodeText (range));
                writer.WriteLine ();
            }

            writer.WriteLine ("[Instructions]");
            for (int i = 0; i < m_instructions.Count; ++i)
            {
                var instruction = m_instructions[i];
                writer.Write ("#{0:D5} 0x{1:X8} opcode=0x{2:X2}",
                              i, instruction.Offset, instruction.Opcode);
                foreach (var operand in instruction.Operands)
                {
                    if (operand is byte)
                        writer.Write (" byte=0x{0:X2}", (byte)operand);
                    else if (operand is int)
                        writer.Write (" int=0x{0:X8}", unchecked ((uint)(int)operand));
                    else if (operand is SilkysTextRange)
                    {
                        var text = (SilkysTextRange)operand;
                        writer.Write (" text=0x{0:X8}+0x{1:X4}", text.Offset, text.Length);
                    }
                }
                writer.WriteLine ();
            }
        }

        static SilkysOpcodeSet CreateAi6WinOpcodes ()
        {
            var templates = CreateCommonTemplates();
            templates[0x0A] = "s";
            templates[0x0B] = "s";
            templates[0x1A] = "a";
            templates[0x1B] = "b";
            templates[0xFC] = "";
            templates[0xFD] = "";
            templates[0xFE] = "";
            templates[0xFF] = "";
            return new SilkysOpcodeSet {
                Name = "AI6WIN",
                Yield = 0x00,
                Add = 0x34,
                EscapeSequence = 0x1B,
                Message1 = 0x0A,
                Message2 = 0x0B,
                PushInt = 0x32,
                PushString = 0x33,
                Syscall = 0x18,
                LineNumber = 0xFF,
                Nop1 = 0xFC,
                Nop2 = 0xFD,
                OperandTemplates = templates,
                NameSyscalls = new[] { new SilkysNameSyscall { Function = 31, Exec = 15 } },
            };
        }

        static SilkysOpcodeSet CreateSilkysPlusOpcodes ()
        {
            var templates = CreateCommonTemplates();
            templates[0x0A] = "s";
            templates[0x0B] = "t";
            templates[0x1A] = "i";
            templates[0x1B] = "a";
            templates[0x1C] = "b";
            templates[0xFA] = "";
            templates[0xFB] = "";
            templates[0xFC] = "";
            templates[0xFD] = "";
            templates[0xFE] = "";
            templates[0xFF] = "";
            return new SilkysOpcodeSet {
                Name = "Silky's+",
                Yield = 0x00,
                Add = 0x34,
                EscapeSequence = 0x1C,
                Message1 = 0x0A,
                Message2 = 0x0B,
                PushInt = 0x32,
                PushString = 0x33,
                Syscall = 0x18,
                LineNumber = 0xFF,
                Nop1 = 0xFC,
                Nop2 = 0xFD,
                IsMessage1Obfuscated = true,
                OperandTemplates = templates,
                NameSyscalls = new[] {
                    new SilkysNameSyscall { Function = 29, Exec = 11 },
                    new SilkysNameSyscall { Function = 29, Exec = 15 },
                },
            };
        }

        static Dictionary<byte, string> CreateCommonTemplates ()
        {
            return new Dictionary<byte, string> {
                { 0x00, "" },
                { 0x01, "" },
                { 0x02, "" },
                { 0x03, "" },
                { 0x04, "" },
                { 0x05, "" },
                { 0x06, "" },
                { 0x07, "" },
                { 0x08, "" },
                { 0x09, "" },
                { 0x0C, "" },
                { 0x0D, "" },
                { 0x0E, "" },
                { 0x0F, "" },
                { 0x10, "" },
                { 0x11, "" },
                { 0x12, "" },
                { 0x13, "" },
                { 0x14, "a" },
                { 0x15, "a" },
                { 0x16, "a" },
                { 0x17, "" },
                { 0x18, "" },
                { 0x19, "i" },
                { 0x32, "i" },
                { 0x33, "s" },
                { 0x34, "" },
                { 0x35, "" },
                { 0x36, "" },
                { 0x37, "" },
                { 0x38, "" },
                { 0x39, "" },
                { 0x3A, "" },
                { 0x3B, "" },
                { 0x3C, "" },
                { 0x3D, "" },
                { 0x3E, "" },
                { 0x3F, "" },
                { 0x40, "" },
                { 0x41, "" },
                { 0x42, "" },
                { 0x43, "" },
            };
        }
    }

    internal sealed class SilkysMapScript
    {
        const int MaxMessageCount = 0x100000;
        static readonly Encoding s_utf16 = Encoding.Unicode.WithFatalFallback();

        readonly byte[] m_data;
        readonly string m_name;
        readonly List<SilkysMapRecord> m_records;

        SilkysMapScript (byte[] data, string name)
        {
            m_data = data;
            m_name = name;
            m_records = ReadRecords (data);
        }

        public static bool IsScript (IBinaryStream file)
        {
            if (!file.Name.HasExtension (".map") || file.Length > int.MaxValue)
                return false;
            long position = file.Position;
            try
            {
                SilkysMapScript script;
                return TryRead (file, out script);
            }
            finally
            {
                if (file.CanSeek)
                    file.Position = position;
            }
        }

        public static SilkysMapScript Read (IBinaryStream file)
        {
            if (!file.Name.HasExtension (".map") || file.Length > int.MaxValue)
                throw new InvalidFormatException();
            SilkysMapScript script;
            if (!TryRead (file, out script))
                throw new InvalidFormatException();
            return script;
        }

        static bool TryRead (IBinaryStream file, out SilkysMapScript script)
        {
            script = null;
            if (file.Length < 14 || file.Length > int.MaxValue)
                return false;
            try
            {
                file.Position = 0;
                var data = file.ReadBytes ((int)file.Length);
                if (data.Length != file.Length)
                    return false;
                script = new SilkysMapScript (data, file.Name);
                return true;
            }
            catch
            {
                return false;
            }
        }

        static List<SilkysMapRecord> ReadRecords (byte[] data)
        {
            if (data.Length < 14)
                throw new InvalidFormatException();
            int count = LittleEndian.ToInt32 (data, 0);
            if (count <= 0 || count > MaxMessageCount)
                throw new InvalidFormatException();
            long table_end_long = 4L + 8L * count;
            if (table_end_long > data.Length - 2)
                throw new InvalidFormatException();
            int table_end = (int)table_end_long;

            var records = new List<SilkysMapRecord> (count);
            for (int i = 0; i < count; ++i)
            {
                int entry_offset = 4 + 8 * i;
                int index = LittleEndian.ToInt32 (data, entry_offset);
                int text_offset = LittleEndian.ToInt32 (data, entry_offset + 4);
                if (text_offset < table_end || text_offset > data.Length - 2)
                    throw new InvalidFormatException();
                int end = FindUtf16End (data, text_offset);
                string text = s_utf16.GetString (data, text_offset, end - text_offset);
                records.Add (new SilkysMapRecord {
                    Index = index,
                    Offset = text_offset,
                    Length = end + 2 - text_offset,
                    Text = text,
                });
            }
            return records;
        }

        static int FindUtf16End (byte[] data, int offset)
        {
            for (int position = offset; position <= data.Length - 2; position += 2)
            {
                if (0 == data[position] && 0 == data[position+1])
                    return position;
            }
            throw new InvalidFormatException();
        }

        public IEnumerable<ScriptLine> ExtractText ()
        {
            uint id = 0;
            foreach (var record in m_records)
            {
                if (!string.IsNullOrEmpty (record.Text))
                    yield return new ScriptLine { Id = id++, Text = record.Text };
            }
        }

        public IEnumerable<ScriptTextEntry> ExtractJsonEntries ()
        {
            foreach (var record in m_records)
            {
                if (!string.IsNullOrWhiteSpace (record.Text))
                    yield return new ScriptTextEntry (record.Text);
            }
        }

        public void WriteDump (TextWriter writer)
        {
            writer.WriteLine ("# Silky's MAP decoded dump");
            writer.WriteLine ();
            writer.WriteLine ("[Header]");
            writer.WriteLine ("Name={0}", m_name);
            writer.WriteLine ("MessageCount={0}", m_records.Count);
            writer.WriteLine ("FileSize=0x{0:X8}", m_data.Length);
            writer.WriteLine ();
            writer.WriteLine ("[Messages]");
            for (int i = 0; i < m_records.Count; ++i)
            {
                var record = m_records[i];
                writer.WriteLine ("#{0:D4} index={1} 0x{2:X8}+0x{3:X4}",
                                  i, record.Index, record.Offset, record.Length);
                writer.WriteLine (record.Text);
                writer.WriteLine ();
            }
        }
    }

    internal sealed class SilkysOpcodeSet
    {
        public string Name;
        public byte Yield;
        public byte Add;
        public byte EscapeSequence;
        public byte Message1;
        public byte Message2;
        public byte PushInt;
        public byte PushString;
        public byte Syscall;
        public byte LineNumber;
        public byte Nop1;
        public byte Nop2;
        public bool IsMessage1Obfuscated;
        public Dictionary<byte, string> OperandTemplates;
        public SilkysNameSyscall[] NameSyscalls;
    }

    internal struct SilkysNameSyscall
    {
        public int Function;
        public int Exec;
    }

    internal enum SilkysTextType
    {
        Internal,
        Message,
        CharacterName,
    }

    internal struct SilkysTextRange
    {
        public int Offset;
        public int Length;
        public SilkysTextType Type;
    }

    internal sealed class SilkysInstruction
    {
        public int Offset;
        public int EndOffset;
        public byte Opcode;
        public List<object> Operands;
    }

    internal sealed class SilkysMapRecord
    {
        public int Index;
        public int Offset;
        public int Length;
        public string Text;
    }
}
