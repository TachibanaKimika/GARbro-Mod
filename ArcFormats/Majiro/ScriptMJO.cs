//! \file       ScriptMJO.cs
//! \date       Mon May 18 2026
//! \brief      Majiro MJO bytecode script text extractor.
//
// Majiro bytecode parsing is adapted from VNTranslationTools.
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
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using GameRes.Utility;

namespace GameRes.Formats.Majiro
{
    [Export(typeof(ScriptFormat))]
    public class MjoScriptFormat : GenericScriptFormat, IConfigurableScriptFormat
    {
        public const string FormatTag = "MJO/Majiro";

        public override string         Tag { get { return FormatTag; } }
        public override string Description { get { return "Majiro game engine bytecode script"; } }
        public override uint     Signature { get { return 0x696A614D; } } // 'Maji'

        const uint PlainBytecodeSignature = 0x6C506A4D; // 'MjPl'
        static readonly string[] s_text_modes = { ScriptTextMode.Filtered, ScriptTextMode.Raw, ScriptTextMode.Dump };

        public IEnumerable<string> TextModes { get { return s_text_modes; } }
        public string DefaultTextMode { get { return ScriptTextMode.Filtered; } }

        public MjoScriptFormat ()
        {
            Extensions = new[] { "mjo" };
            Signatures = new[] { Signature, PlainBytecodeSignature };
        }

        public override bool IsScript (IBinaryStream file)
        {
            if (!file.Name.HasExtension (".mjo"))
                return false;
            long position = file.Position;
            try
            {
                return MjoScript.IsScript (file);
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
            var script = MjoScript.Read (file);
            var output = new MemoryStream();
            using (var writer = new StreamWriter (output, new UTF8Encoding (true), 0x400, true))
            {
                if (string.Equals (text_mode, ScriptTextMode.Dump, StringComparison.OrdinalIgnoreCase))
                {
                    script.WriteDump (writer);
                }
                else
                {
                    bool filter = !string.Equals (text_mode, ScriptTextMode.Raw, StringComparison.OrdinalIgnoreCase);
                    foreach (var line in script.ExtractText (filter))
                        writer.WriteLine (line.Text);
                }
            }
            output.Position = 0;
            return output;
        }

        public override Stream ConvertBack (IBinaryStream file)
        {
            throw new NotSupportedException();
        }

        public override ScriptData Read (string name, Stream file)
        {
            using (var input = BinaryStream.FromStream (file, name))
            {
                var script = MjoScript.Read (input);
                var data = new ScriptData();
                foreach (var line in script.ExtractText (true))
                    data.TextLines.Add (line);
                return data;
            }
        }
    }

    internal sealed class MjoScript
    {
        const int HeaderSize = 0x1C;
        const int CodeSizeFieldSize = 4;
        const int MaxFunctionCount = 0x100000;

        readonly byte[] m_data;
        readonly List<int> m_function_addrs = new List<int>();
        readonly List<MjoTextCodeRange> m_text_ranges = new List<MjoTextCodeRange>();
        readonly HashSet<int> m_seen_instructions = new HashSet<int>();
        readonly string m_name;

        int m_entry_point_addr;
        int m_line_count;
        int m_code_offset;
        int m_code_size;
        bool m_is_encrypted;
        string m_signature;

        MjoScript (byte[] data, string name)
        {
            m_data = data;
            m_name = name;
            ReadHeader();
            if (m_is_encrypted)
                Decrypt();
            ReadCode();
        }

        public static bool IsScript (IBinaryStream file)
        {
            if (file.Length < HeaderSize + CodeSizeFieldSize || file.Length > int.MaxValue)
                return false;
            try
            {
                file.Position = 0;
                var header = file.ReadBytes (HeaderSize);
                if (!HasKnownSignature (header))
                    return false;
                return IsSaneHeader (file, header);
            }
            catch
            {
                return false;
            }
        }

        public static MjoScript Read (IBinaryStream file)
        {
            if (file.Length < HeaderSize + CodeSizeFieldSize || file.Length > int.MaxValue)
                throw new InvalidFormatException();
            file.Position = 0;
            var data = file.ReadBytes ((int)file.Length);
            if (data.Length != file.Length)
                throw new EndOfStreamException();
            return new MjoScript (data, file.Name);
        }

        static bool IsSaneHeader (IBinaryStream file, byte[] header)
        {
            int function_count = header.ToInt32 (0x18);
            if (function_count < 0 || function_count > MaxFunctionCount)
                return false;

            long code_size_offset = HeaderSize + 8L * function_count;
            long code_offset = code_size_offset + CodeSizeFieldSize;
            if (code_offset < HeaderSize || code_offset > file.Length)
                return false;
            file.Position = code_size_offset;
            int code_size = file.ReadInt32();
            if (code_size < 0 || code_offset + code_size != file.Length)
                return false;

            int entry_point = header.ToInt32 (0x10);
            if (code_size > 0 && (entry_point < 0 || entry_point >= code_size))
                return false;

            file.Position = HeaderSize;
            for (int i = 0; i < function_count; ++i)
            {
                file.ReadInt32();
                int addr = file.ReadInt32();
                if (addr < 0 || addr >= code_size)
                    return false;
            }
            return true;
        }

        public IEnumerable<ScriptLine> ExtractText (bool filter)
        {
            uint id = 0;
            foreach (var range in m_text_ranges)
            {
                if (filter)
                {
                    foreach (var text in GetFilteredStrings (range))
                        yield return new ScriptLine { Id = id++, Text = text };
                }
                else if (!string.IsNullOrEmpty (range.Text))
                {
                    yield return new ScriptLine { Id = id++, Text = range.Text };
                }
            }
        }

        public void WriteDump (TextWriter writer)
        {
            writer.WriteLine ("# Majiro MJO decoded dump");
            writer.WriteLine ("# This is not reconstructed source; it is a direct dump of decoded bytecode.");
            writer.WriteLine ();
            writer.WriteLine ("[Header]");
            writer.WriteLine ("Name={0}", m_name);
            writer.WriteLine ("Signature={0}", m_signature);
            writer.WriteLine ("Encrypted={0}", m_is_encrypted);
            writer.WriteLine ("EntryPoint=0x{0:X8}", m_entry_point_addr);
            writer.WriteLine ("LineCount={0}", m_line_count);
            writer.WriteLine ("FunctionCount={0}", m_function_addrs.Count);
            writer.WriteLine ("CodeOffset=0x{0:X8}", m_code_offset);
            writer.WriteLine ("CodeSize=0x{0:X8}", m_code_size);
            writer.WriteLine ();

            writer.WriteLine ("[Functions]");
            if (0 == m_function_addrs.Count)
            {
                writer.WriteLine ("<empty>");
            }
            else
            {
                for (int i = 0; i < m_function_addrs.Count; ++i)
                    writer.WriteLine ("#{0:D4}=0x{1:X8}", i, m_function_addrs[i]);
            }
            writer.WriteLine ();

            writer.WriteLine ("[Text]");
            if (0 == m_text_ranges.Count)
            {
                writer.WriteLine ("<empty>");
            }
            else
            {
                for (int i = 0; i < m_text_ranges.Count; ++i)
                {
                    var range = m_text_ranges[i];
                    writer.WriteLine ("#{0:D4} 0x{1:X8}+0x{2:X4} {3}", i, range.Offset, range.Length, range.Type);
                    writer.WriteLine (range.Text);
                    writer.WriteLine ();
                }
            }

            writer.WriteLine ("[Instructions]");
            using (var stream = new MemoryStream (m_data, m_code_offset, m_code_size, false))
            {
                var disasm = new MjoDisassembler (stream, m_code_offset);
                while (stream.Position < stream.Length)
                {
                    int offset = m_code_offset + (int)stream.Position;
                    try
                    {
                        var instr = disasm.ReadInstruction();
                        writer.WriteLine ("0x{0:X8}: {1:X4} {2}",
                                          offset, unchecked ((ushort)instr.Opcode),
                                          FormatOperands (instr.Operands));
                    }
                    catch (Exception X)
                    {
                        writer.WriteLine ("0x{0:X8}: <invalid: {1}>", offset, X.Message);
                        break;
                    }
                }
            }
        }

        static IEnumerable<string> GetFilteredStrings (MjoTextCodeRange range)
        {
            if (range.Type == MjoTextCodeType.Ldstr)
            {
                if (!string.IsNullOrEmpty (range.Text))
                    yield return range.Text;
                yield break;
            }

            var match = Regex.Match (range.Text,
                @"^(?:(?<name>[^\u300c\u300d\r\n]+)\u300c(?<message>.+?)\u300d?(?:\r\n|$))+$",
                RegexOptions.Singleline);
            if (!match.Success)
            {
                if (!string.IsNullOrEmpty (range.Text))
                    yield return range.Text;
                yield break;
            }

            for (int i = 0; i < match.Groups["name"].Captures.Count; ++i)
            {
                yield return match.Groups["name"].Captures[i].Value;
                yield return match.Groups["message"].Captures[i].Value;
            }
        }

        void ReadHeader ()
        {
            if (!HasKnownSignature (m_data) || !IsSaneHeader (m_data, m_data.Length))
                throw new InvalidFormatException();

            m_signature = Encoding.ASCII.GetString (m_data, 0, 0x10).TrimEnd ('\0');
            m_is_encrypted = m_data.AsciiEqual (0, "MajiroObjX1.000\0");
            m_entry_point_addr = m_data.ToInt32 (0x10);
            m_line_count = m_data.ToInt32 (0x14);
            int function_count = m_data.ToInt32 (0x18);
            int function_table = HeaderSize;
            int code_size_offset = function_table + function_count * 8;
            m_code_size = m_data.ToInt32 (code_size_offset);
            m_code_offset = code_size_offset + CodeSizeFieldSize;

            for (int i = 0; i < function_count; ++i)
            {
                int offset = function_table + i * 8 + 4;
                int addr = m_data.ToInt32 (offset);
                if (addr >= 0 && addr < m_code_size)
                    m_function_addrs.Add (addr);
            }
        }

        static bool HasKnownSignature (byte[] data)
        {
            if (data.Length < 0x10)
                return false;
            return data.AsciiEqual (0, "MajiroObjV1.000\0")
                || data.AsciiEqual (0, "MajiroObjX1.000\0")
                || data.AsciiEqual (0, "MjPlainBytecode");
        }

        static bool IsSaneHeader (byte[] data, long file_size)
        {
            if (file_size < HeaderSize + CodeSizeFieldSize || file_size > int.MaxValue || data.Length < HeaderSize + CodeSizeFieldSize)
                return false;
            int function_count = data.ToInt32 (0x18);
            if (function_count < 0 || function_count > MaxFunctionCount)
                return false;

            long code_size_offset = HeaderSize + 8L * function_count;
            long code_offset = code_size_offset + CodeSizeFieldSize;
            if (code_offset < HeaderSize || code_offset > file_size || code_size_offset + 4 > data.Length)
                return false;
            int code_size = data.ToInt32 ((int)code_size_offset);
            if (code_size < 0 || code_offset + code_size != file_size)
                return false;

            int entry_point = data.ToInt32 (0x10);
            if (code_size > 0 && (entry_point < 0 || entry_point >= code_size))
                return false;

            if (code_size_offset > data.Length)
                return true;
            for (int i = 0; i < function_count; ++i)
            {
                int offset = HeaderSize + i * 8 + 4;
                if (offset + 4 > data.Length)
                    break;
                int addr = data.ToInt32 (offset);
                if (addr < 0 || addr >= code_size)
                    return false;
            }
            return true;
        }

        void Decrypt ()
        {
            m_data[9] = (byte)'V';
            for (int i = 0; i < m_code_size; ++i)
                m_data[m_code_offset+i] ^= GetEncryptionByte (i);
        }

        static byte GetEncryptionByte (int index)
        {
            uint value = Crc32.Table[(index >> 2) & 0xFF];
            return (byte)(value >> ((index & 3) * 8));
        }

        void ReadCode ()
        {
            var remaining = new SortedSet<int>();
            AddAddress (remaining, m_entry_point_addr);
            foreach (var addr in m_function_addrs)
                AddAddress (remaining, addr);

            while (remaining.Count > 0)
            {
                int addr = remaining.Min;
                remaining.Remove (addr);
                if (addr < 0 || addr >= m_code_size)
                    continue;
                ReadCodeUntilRet (addr, remaining);
            }
        }

        static void AddAddress (SortedSet<int> set, int addr)
        {
            if (addr >= 0)
                set.Add (addr);
        }

        void ReadCodeUntilRet (int addr, SortedSet<int> remaining)
        {
            using (var stream = new MemoryStream (m_data, m_code_offset + addr, m_code_size - addr, false))
            {
                int stream_base = m_code_offset + addr;
                var disasm = new MjoDisassembler (stream, stream_base);
                disasm.RelativeAddressEncountered += offset =>
                {
                    int distance = m_data.ToInt32 (offset);
                    int target = offset - m_code_offset + 4 + distance;
                    if (target >= 0 && target < m_code_size)
                        remaining.Add (target);
                };

                var ldstr_ranges = new Stack<MjoTextCodeRange>();
                int text_start_offset = -1;
                string current_text = null;
                while (stream.Position < stream.Length)
                {
                    int instr_offset = stream_base + (int)stream.Position;
                    if (!m_seen_instructions.Add (instr_offset))
                        break;
                    var instr = disasm.ReadInstruction();

                    switch (instr.Opcode)
                    {
                    case MjoOpcodes.LdcI:
                    case MjoOpcodes.Proc:
                        break;

                    case MjoOpcodes.Ldstr:
                        ldstr_ranges.Push (new MjoTextCodeRange (instr_offset, (int)stream.Position + stream_base - instr_offset,
                                                                  instr.Operands[0] as string, MjoTextCodeType.Ldstr));
                        break;

                    case MjoOpcodes.Text:
                        if (text_start_offset < 0)
                            text_start_offset = instr_offset;
                        current_text += instr.Operands[0] as string;
                        break;

                    case MjoOpcodes.Line:
                        break;

                    case MjoOpcodes.Ctrl:
                        var ctrl = instr.Operands[0] as string;
                        if ("n" == ctrl)
                        {
                            if (text_start_offset < 0)
                                text_start_offset = instr_offset;
                            current_text += "\r\n";
                        }
                        else if ("d" == ctrl && ldstr_ranges.Count > 0)
                        {
                            if (text_start_offset < 0)
                                text_start_offset = ldstr_ranges.Peek().Offset;
                            current_text += ldstr_ranges.Pop().Text;
                        }
                        else
                        {
                            FlushTextRange (ldstr_ranges, text_start_offset, instr_offset, current_text);
                            text_start_offset = -1;
                            current_text = null;
                            ldstr_ranges.Clear();
                        }
                        break;

                    case MjoOpcodes.Callp:
                        if ((int)instr.Operands[0] == MjoSyscalls.Ruby && ldstr_ranges.Count >= 2)
                        {
                            string ruby_text = ldstr_ranges.Pop().Text;
                            if (text_start_offset < 0)
                                text_start_offset = ldstr_ranges.Peek().Offset;
                            string base_text = ldstr_ranges.Pop().Text;
                            current_text += string.Format ("[{0}/{1}]", base_text, ruby_text);
                            break;
                        }
                        goto default;

                    case MjoOpcodes.Ret:
                        FlushTextRange (ldstr_ranges, text_start_offset, instr_offset, current_text);
                        return;

                    default:
                        FlushTextRange (ldstr_ranges, text_start_offset, instr_offset, current_text);
                        if ((instr.Opcode == MjoOpcodes.Call || instr.Opcode == MjoOpcodes.Callp) && instr.Operands.Count >= 3)
                            ReadSyscall ((int)instr.Operands[0], (int)instr.Operands[2], ldstr_ranges);
                        text_start_offset = -1;
                        current_text = null;
                        ldstr_ranges.Clear();
                        break;
                    }
                }
                FlushTextRange (ldstr_ranges, text_start_offset, stream_base + (int)stream.Position, current_text);
            }
        }

        void FlushTextRange (Stack<MjoTextCodeRange> ldstr_ranges, int text_start_offset, int text_end_offset, string current_text)
        {
            if (string.IsNullOrWhiteSpace (current_text))
                return;
            while (ldstr_ranges.Count > 0)
            {
                var prev_range = ldstr_ranges.Pop();
                text_end_offset = prev_range.Offset;
            }
            if (text_start_offset < 0 || text_end_offset <= text_start_offset)
                return;
            m_text_ranges.Add (new MjoTextCodeRange (text_start_offset, text_end_offset - text_start_offset,
                                                     current_text.Trim(), MjoTextCodeType.Text));
        }

        void ReadSyscall (int name_hash, int num_args, Stack<MjoTextCodeRange> ldstr_ranges)
        {
            switch (name_hash)
            {
            case MjoSyscalls.Select1:
                AddChoices (ldstr_ranges, num_args);
                break;

            case MjoSyscalls.Select2:
                if (ldstr_ranges.Count < 2)
                    return;
                ldstr_ranges.Pop();
                ldstr_ranges.Pop();
                AddChoices (ldstr_ranges, num_args - 2);
                break;

            case MjoSyscalls.SelectMenu:
                AddChoices (ldstr_ranges, num_args / 2);
                break;

            case MjoSyscalls.OkMessageBox:
            case MjoSyscalls.YesNoMessageBox:
                if (ldstr_ranges.Count > 0)
                    m_text_ranges.Add (ldstr_ranges.Pop());
                break;
            }
        }

        void AddChoices (Stack<MjoTextCodeRange> ldstr_ranges, int count)
        {
            if (count <= 0 || ldstr_ranges.Count < count)
                return;
            var choices = new List<MjoTextCodeRange> (count);
            for (int i = 0; i < count; ++i)
                choices.Add (ldstr_ranges.Pop());
            choices.Reverse();
            m_text_ranges.AddRange (choices);
        }

        static string FormatOperands (IEnumerable<object> operands)
        {
            var values = operands.Select (FormatOperand);
            return string.Join (", ", values.ToArray());
        }

        static string FormatOperand (object value)
        {
            var text = value as string;
            if (null != text)
                return "\"" + text.Replace ("\\", "\\\\").Replace ("\"", "\\\"").Replace ("\r", "\\r").Replace ("\n", "\\n") + "\"";
            if (value is int)
                return string.Format ("0x{0:X8}", unchecked ((uint)(int)value));
            if (value is float)
                return ((float)value).ToString ("R", System.Globalization.CultureInfo.InvariantCulture);
            return value != null ? value.ToString() : "";
        }
    }

    internal sealed class MjoDisassembler
    {
        readonly Stream m_stream;
        readonly BinaryReader m_reader;
        readonly int m_base_offset;

        public event Action<int> RelativeAddressEncountered;

        public MjoDisassembler (Stream stream, int base_offset)
        {
            m_stream = stream;
            m_reader = new BinaryReader (stream, Encodings.cp932);
            m_base_offset = base_offset;
        }

        public MjoInstruction ReadInstruction ()
        {
            short opcode = m_reader.ReadInt16();
            var operands = ReadOperands (opcode);
            return new MjoInstruction (opcode, operands);
        }

        List<object> ReadOperands (short opcode)
        {
            string template;
            if (!MjoOpcodes.OperandTemplates.TryGetValue (opcode, out template))
                throw new InvalidFormatException (string.Format ("Unknown MJO opcode 0x{0:X4}", unchecked ((ushort)opcode)));

            var operands = new List<object>();
            foreach (char operand_type in template)
            {
                switch (operand_type)
                {
                case 't':
                    {
                        int count = m_reader.ReadUInt16();
                        for (int i = 0; i < count; ++i)
                            operands.Add ((int)m_reader.ReadByte());
                        break;
                    }
                case 's':
                    operands.Add (ReadString());
                    break;

                case 'f':
                    operands.Add ((int)m_reader.ReadUInt16());
                    break;

                case 'h':
                    operands.Add (m_reader.ReadInt32());
                    break;

                case 'o':
                    operands.Add ((int)m_reader.ReadInt16());
                    break;

                case '0':
                case 'i':
                    operands.Add (m_reader.ReadInt32());
                    break;

                case 'r':
                    operands.Add (m_reader.ReadSingle());
                    break;

                case 'a':
                    operands.Add ((int)m_reader.ReadUInt16());
                    break;

                case 'j':
                    OnRelativeAddress();
                    operands.Add (m_reader.ReadInt32());
                    break;

                case 'l':
                    operands.Add ((int)m_reader.ReadUInt16());
                    break;

                case 'c':
                    {
                        int count = m_reader.ReadUInt16();
                        for (int i = 0; i < count; ++i)
                        {
                            OnRelativeAddress();
                            operands.Add (m_reader.ReadInt32());
                        }
                        break;
                    }
                }
            }
            return operands;
        }

        string ReadString ()
        {
            int length = m_reader.ReadUInt16();
            if (0 == length)
                return string.Empty;
            if (length < 0 || length > m_stream.Length - m_stream.Position)
                throw new EndOfStreamException();
            var bytes = m_reader.ReadBytes (length - 1);
            if (bytes.Length != length - 1 || 0 != m_reader.ReadByte())
                throw new InvalidFormatException();
            return Encodings.cp932.GetString (bytes);
        }

        void OnRelativeAddress ()
        {
            var handler = RelativeAddressEncountered;
            if (null != handler)
                handler (m_base_offset + (int)m_stream.Position);
        }
    }

    internal struct MjoInstruction
    {
        public readonly short Opcode;
        public readonly List<object> Operands;

        public MjoInstruction (short opcode, List<object> operands)
        {
            Opcode = opcode;
            Operands = operands;
        }
    }

    internal struct MjoTextCodeRange
    {
        public readonly int Offset;
        public readonly int Length;
        public readonly string Text;
        public readonly MjoTextCodeType Type;

        public MjoTextCodeRange (int offset, int length, string text, MjoTextCodeType type)
        {
            Offset = offset;
            Length = length;
            Text = text ?? "";
            Type = type;
        }
    }

    internal enum MjoTextCodeType
    {
        Ldstr,
        Text,
    }

    internal static class MjoSyscalls
    {
        public const int Ruby = 0x3198FD01;
        public const int Select1 = 0x0A7A489C;
        public const int Select2 = 0x57F252DB;
        public const int SelectMenu = 0x4B22CC66;
        public const int OkMessageBox = unchecked ((int)0xE72D9F52);
        public const int YesNoMessageBox = unchecked ((int)0xEFA43BBD);
    }

    internal static class MjoOpcodes
    {
        public static readonly Dictionary<short, string> OperandTemplates = new Dictionary<short, string>
        {
            { 0x0100, "" }, { 0x0101, "" }, { 0x0108, "" }, { 0x0109, "" },
            { 0x0110, "" }, { 0x0118, "" }, { 0x0119, "" }, { 0x011A, "" },
            { 0x0120, "" }, { 0x0121, "" }, { 0x0128, "" }, { 0x0130, "" },
            { 0x0138, "" }, { 0x0139, "" }, { 0x013A, "" }, { 0x0140, "" },
            { 0x0141, "" }, { 0x0142, "" }, { 0x0148, "" }, { 0x0149, "" },
            { 0x014A, "" }, { 0x0150, "" }, { 0x0151, "" }, { 0x0152, "" },
            { 0x0158, "" }, { 0x0159, "" }, { 0x015A, "" }, { 0x015B, "" },
            { 0x015C, "" }, { 0x015D, "" }, { 0x0160, "" }, { 0x0161, "" },
            { 0x0162, "" }, { 0x0163, "" }, { 0x0164, "" }, { 0x0165, "" },
            { 0x0168, "" }, { 0x0170, "" }, { 0x0178, "" }, { 0x0180, "" },
            { 0x0188, "" }, { 0x0190, "" }, { 0x0191, "" }, { 0x0198, "" },
            { 0x01A0, "" }, { 0x01A1, "" }, { 0x01A8, "" }, { 0x01A9, "" },
            { 0x01B0, "fho" }, { 0x01B1, "fho" }, { 0x01B2, "fho" }, { 0x01B3, "fho" },
            { 0x01B4, "fho" }, { 0x01B5, "fho" }, { 0x01B8, "fho" }, { 0x01B9, "fho" },
            { 0x01C0, "fho" }, { 0x01C1, "fho" }, { 0x01C8, "fho" }, { 0x01D0, "fho" },
            { 0x01D1, "fho" }, { 0x01D2, "fho" }, { 0x01D8, "fho" }, { 0x01D9, "fho" },
            { 0x01E0, "fho" }, { 0x01E8, "fho" }, { 0x01F0, "fho" }, { 0x01F8, "fho" },
            { 0x0200, "fho" }, { 0x0210, "fho" }, { 0x0211, "fho" }, { 0x0212, "fho" },
            { 0x0213, "fho" }, { 0x0214, "fho" }, { 0x0215, "fho" }, { 0x0218, "fho" },
            { 0x0219, "fho" }, { 0x0220, "fho" }, { 0x0221, "fho" }, { 0x0228, "fho" },
            { 0x0230, "fho" }, { 0x0231, "fho" }, { 0x0232, "fho" }, { 0x0238, "fho" },
            { 0x0239, "fho" }, { 0x0240, "fho" }, { 0x0248, "fho" }, { 0x0250, "fho" },
            { 0x0258, "fho" }, { 0x0260, "fho" }, { 0x0270, "fho" }, { 0x0271, "fho" },
            { 0x0272, "fho" }, { 0x0278, "fho" }, { 0x0279, "fho" }, { 0x0280, "fho" },
            { 0x0281, "fho" }, { 0x0288, "fho" }, { 0x0290, "fho" }, { 0x0291, "fho" },
            { 0x0292, "fho" }, { 0x0298, "fho" }, { 0x0299, "fho" }, { 0x02A0, "fho" },
            { 0x02A8, "fho" }, { 0x02B0, "fho" }, { 0x02B8, "fho" }, { 0x02C0, "fho" },
            { 0x02D0, "fho" }, { 0x02D1, "fho" }, { 0x02D2, "fho" }, { 0x02D8, "fho" },
            { 0x02D9, "fho" }, { 0x02E0, "fho" }, { 0x02E1, "fho" }, { 0x02E8, "fho" },
            { 0x02F0, "fho" }, { 0x02F1, "fho" }, { 0x02F2, "fho" }, { 0x02F8, "fho" },
            { 0x02F9, "fho" }, { 0x0300, "fho" }, { 0x0308, "fho" }, { 0x0310, "fho" },
            { 0x0318, "fho" }, { 0x0320, "fho" }, { 0x0800, "i" }, { 0x0801, "s" },
            { 0x0802, "fho" }, { 0x0803, "r" }, { 0x080F, "h0a" }, { 0x0810, "h0a" },
            { 0x0829, "t" }, { 0x082B, "" }, { 0x082C, "j" }, { 0x082D, "j" },
            { 0x082E, "j" }, { 0x082F, "" }, { 0x0830, "j" }, { 0x0831, "j" },
            { 0x0832, "j" }, { 0x0833, "j" }, { 0x0834, "ha" }, { 0x0835, "ha" },
            { 0x0836, "t" }, { 0x0837, "fho" }, { 0x0838, "j" }, { 0x0839, "j" },
            { 0x083A, "l" }, { 0x083B, "j" }, { 0x083C, "j" }, { 0x083D, "j" },
            { 0x083E, "" }, { 0x083F, "" }, { 0x0840, "s" }, { 0x0841, "" },
            { 0x0842, "s" }, { 0x0843, "j" }, { 0x0844, "" }, { 0x0845, "j" },
            { 0x0846, "" }, { 0x0847, "j" }, { 0x0850, "c" },
        };

        public const short Text = 0x0840;
        public const short Proc = 0x0841;
        public const short Ctrl = 0x0842;
        public const short Line = 0x083A;
        public const short LdcI = 0x0800;
        public const short Ldstr = 0x0801;
        public const short Call = 0x080F;
        public const short Callp = 0x0810;
        public const short Ret = 0x082B;
    }
}
