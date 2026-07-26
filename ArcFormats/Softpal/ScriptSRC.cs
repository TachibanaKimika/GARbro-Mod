//! \file       ScriptSRC.cs
//! \date       Sun Jul 26 2026
//! \brief      Softpal Sv20 bytecode script text extractor.
//
// Softpal bytecode parsing is adapted from VNTranslationTools.
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
using GameRes.Utility;

namespace GameRes.Formats.Softpal
{
    [Export(typeof(ScriptFormat))]
    public class SoftpalScriptFormat : GenericScriptFormat, IConfigurableScriptFormat
    {
        public const string FormatTag = "SRC/SOFTPAL";

        static readonly string[] s_text_modes = {
            ScriptTextMode.Filtered,
            ScriptTextMode.Raw,
            ScriptTextMode.Dump,
            ScriptTextMode.JsonLines,
        };

        public override string         Tag { get { return FormatTag; } }
        public override string Description { get { return "Softpal Sv20 bytecode script"; } }
        public override uint     Signature { get { return 0x30327653; } } // 'Sv20'

        public IEnumerable<string> TextModes { get { return s_text_modes; } }
        public string DefaultTextMode { get { return ScriptTextMode.Filtered; } }

        public SoftpalScriptFormat ()
        {
            Extensions = new[] { "src" };
        }

        public override bool IsScript (IBinaryStream file)
        {
            return SoftpalScript.IsScript (file);
        }

        public override Stream ConvertFrom (IBinaryStream file)
        {
            return ConvertFrom (file, ScriptTextMode.Filtered);
        }

        public Stream ConvertFrom (IBinaryStream file, string text_mode)
        {
            var script = SoftpalScript.Read (file);
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
                    bool normalize = !string.Equals (text_mode, ScriptTextMode.Raw, StringComparison.OrdinalIgnoreCase);
                    foreach (var line in script.ExtractText (normalize))
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
                var script = SoftpalScript.Read (input);
                var data = new ScriptData();
                foreach (var line in script.ExtractText (true))
                    data.TextLines.Add (line);
                return data;
            }
        }
    }

    internal sealed class SoftpalScript
    {
        const int MaxCodeLength = 0x10000000;
        const int MaxTextLength = 0x10000000;
        const int MaxPointCount = 0x100000;

        readonly byte[] m_code;
        readonly byte[] m_text;
        readonly string m_name;
        readonly List<int> m_label_offsets;
        readonly List<SoftpalStringRef> m_strings = new List<SoftpalStringRef>();

        SoftpalScript (byte[] code, byte[] text, string name, List<int> label_offsets)
        {
            m_code = code;
            m_text = text;
            m_name = name;
            m_label_offsets = label_offsets;

            using (var input = new MemoryStream (m_code, false))
            {
                var disasm = new SoftpalDisassembler (input, m_label_offsets);
                disasm.TextAddressEncountered += OnTextAddressEncountered;
                disasm.Disassemble();
            }
        }

        public static bool IsScript (IBinaryStream file)
        {
            long position = file.Position;
            try
            {
                if (!file.Name.HasExtension (".src"))
                    return false;
                Read (file);
                return true;
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

        public static SoftpalScript Read (IBinaryStream file)
        {
            if (!file.Name.HasExtension (".src")
                || file.Length < SoftpalDisassembler.CodeOffset
                || file.Length > MaxCodeLength)
            {
                throw new InvalidFormatException();
            }

            file.Position = 0;
            var code = file.ReadBytes ((int)file.Length);
            if (code.Length != (int)file.Length || !HasCodeSignature (code))
                throw new InvalidFormatException();

            string base_dir = VFS.GetDirectoryName (file.Name);
            string point_name = VFS.CombinePath (base_dir, "POINT.DAT");
            string text_name = VFS.CombinePath (base_dir, "TEXT.DAT");
            if (!VFS.FileExists (point_name) || !VFS.FileExists (text_name))
                throw new InvalidFormatException();

            byte[] point_data = ReadCompanion (point_name, 0x10 + 4 * MaxPointCount);
            byte[] text_data = ReadCompanion (text_name, MaxTextLength);
            if (text_data.Length < 0x10)
                throw new InvalidFormatException();
            var label_offsets = ReadPointData (point_data, code.Length);
            return new SoftpalScript (code, text_data, file.Name, label_offsets);
        }

        static bool HasCodeSignature (byte[] data)
        {
            return data.Length >= 4
                && data[0] == 'S' && data[1] == 'v'
                && data[2] == '2' && data[3] == '0';
        }

        static byte[] ReadCompanion (string name, int max_length)
        {
            using (var input = VFS.OpenBinaryStream (name))
            {
                if (input.Length < 0 || input.Length > max_length || input.Length > int.MaxValue)
                    throw new InvalidFormatException();
                input.Position = 0;
                var data = input.ReadBytes ((int)input.Length);
                if (data.Length != (int)input.Length)
                    throw new EndOfStreamException();
                return data;
            }
        }

        static List<int> ReadPointData (byte[] data, int code_length)
        {
            if (data.Length < 0x10 || 0 != ((data.Length - 0x10) & 3))
                throw new InvalidFormatException();
            string magic = Encoding.ASCII.GetString (data, 0, 0x10);
            if ("$POINT_LIST_****" != magic && "_POINT_LIST_****" != magic)
                throw new InvalidFormatException();

            int count = (data.Length - 0x10) / 4;
            if (count > MaxPointCount)
                throw new InvalidFormatException();
            var offsets = new List<int> (count);
            for (int pos = 0x10; pos < data.Length; pos += 4)
            {
                int relative_offset = LittleEndian.ToInt32 (data, pos);
                long absolute_offset = (long)SoftpalDisassembler.CodeOffset + relative_offset;
                if (absolute_offset < SoftpalDisassembler.CodeOffset || absolute_offset > code_length)
                    throw new InvalidFormatException();
                offsets.Add ((int)absolute_offset);
            }
            offsets.Reverse();
            return offsets;
        }

        void OnTextAddressEncountered (int operand_offset, SoftpalStringType type)
        {
            if (operand_offset < 0 || operand_offset > m_code.Length - 4)
                return;
            int address = LittleEndian.ToInt32 (m_code, operand_offset);
            long text_offset = (long)address + 4;
            if (text_offset < 0 || text_offset >= m_text.Length)
                return;
            m_strings.Add (new SoftpalStringRef (operand_offset, (int)text_offset, type));
        }

        public IEnumerable<ScriptLine> ExtractText (bool normalize)
        {
            uint id = 0;
            foreach (var item in m_strings)
            {
                string text;
                if (!TryReadString (item.TextOffset, normalize, out text) || string.IsNullOrEmpty (text))
                    continue;
                yield return new ScriptLine { Id = id++, Text = text };
            }
        }

        public IEnumerable<ScriptTextEntry> ExtractJsonEntries ()
        {
            var pending_names = new List<string>();
            foreach (var item in m_strings)
            {
                string text;
                if (!TryReadString (item.TextOffset, true, out text) || string.IsNullOrEmpty (text))
                    continue;
                if (item.Type == SoftpalStringType.CharacterName)
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

        public void WriteDump (TextWriter writer)
        {
            writer.WriteLine ("# Softpal Sv20 decoded script dump");
            writer.WriteLine ("# This is not reconstructed source; it lists decoded instructions and text references.");
            writer.WriteLine ();
            writer.WriteLine ("[Header]");
            writer.WriteLine ("Name={0}", m_name);
            writer.WriteLine ("CodeLength=0x{0:X8}", m_code.Length);
            writer.WriteLine ("TextLength=0x{0:X8}", m_text.Length);
            writer.WriteLine ("PointCount={0}", m_label_offsets.Count);
            writer.WriteLine ("TextRefCount={0}", m_strings.Count);
            writer.WriteLine ();
            writer.WriteLine ("[Instructions]");
            using (var input = new MemoryStream (m_code, false))
            {
                var disasm = new SoftpalDisassembler (input, m_label_offsets, writer);
                disasm.Disassemble();
            }
            writer.WriteLine ();
            writer.WriteLine ("[TextReferences]");
            for (int i = 0; i < m_strings.Count; ++i)
            {
                var item = m_strings[i];
                string text;
                if (!TryReadString (item.TextOffset, false, out text))
                    text = "<invalid>";
                writer.WriteLine ("#{0:D4} operand=0x{1:X8} text=0x{2:X8} type={3}",
                                  i, item.OperandOffset, item.TextOffset, item.Type);
                writer.WriteLine (text);
                writer.WriteLine ();
            }
        }

        bool TryReadString (int offset, bool normalize, out string text)
        {
            text = null;
            if (offset < 0 || offset >= m_text.Length)
                return false;
            int end = Array.IndexOf<byte> (m_text, 0, offset);
            if (end < offset)
                return false;
            text = Encodings.cp932.GetString (m_text, offset, end - offset);
            if (normalize)
                text = text.Replace ("<br>", "\r\n").Replace ("%0", "\u2665");
            return true;
        }
    }

    internal enum SoftpalStringType
    {
        CharacterName,
        Message,
    }

    internal struct SoftpalStringRef
    {
        public readonly int OperandOffset;
        public readonly int TextOffset;
        public readonly SoftpalStringType Type;

        public SoftpalStringRef (int operand_offset, int text_offset, SoftpalStringType type)
        {
            OperandOffset = operand_offset;
            TextOffset = text_offset;
            Type = type;
        }
    }

    internal sealed class SoftpalDisassembler
    {
        public const int CodeOffset = 0xC;
        const int MaxStackDepth = 0x10000;

        readonly Stream m_stream;
        readonly List<int> m_label_offsets;
        readonly BinaryReader m_reader;
        readonly TextWriter m_writer;
        readonly Dictionary<short, Action<SoftpalInstruction>> m_opcode_handlers;

        readonly Dictionary<int, UserMessageFunction> m_user_message_functions = new Dictionary<int, UserMessageFunction>();
        readonly Dictionary<int, SoftpalOperand> m_variables = new Dictionary<int, SoftpalOperand>();
        readonly Stack<SoftpalOperand> m_stack = new Stack<SoftpalOperand>();

        public SoftpalDisassembler (Stream stream, List<int> label_offsets, TextWriter writer = null)
        {
            m_stream = stream;
            m_label_offsets = label_offsets;
            m_reader = new BinaryReader (stream);
            m_writer = writer;
            m_opcode_handlers = new Dictionary<short, Action<SoftpalInstruction>> {
                { SoftpalOpcodes.Mov,             HandleMovInstruction },
                { SoftpalOpcodes.Push,            HandlePushInstruction },
                { SoftpalOpcodes.Call,            HandleCallInstruction },
                { SoftpalOpcodes.Syscall,         HandleSyscallInstruction },
                { SoftpalOpcodes.SelectAddChoice, HandleSelectChoiceInstruction },
            };

            if (m_stream.Length < CodeOffset
                || Encoding.ASCII.GetString (m_reader.ReadBytes (4)) != "Sv20")
            {
                throw new InvalidFormatException();
            }
        }

        public event Action<int, SoftpalStringType> TextAddressEncountered;

        public void Disassemble ()
        {
            FindUserMessageFunctions();

            m_stream.Position = CodeOffset;
            while (m_stream.Position < m_stream.Length)
            {
                var instruction = ReadInstruction();
                if (null != m_writer)
                    WriteInstruction (instruction);

                if (IsMessageInstruction (instruction))
                {
                    HandleMessageInstruction();
                }
                else
                {
                    Action<SoftpalInstruction> handler;
                    if (m_opcode_handlers.TryGetValue (instruction.Opcode, out handler))
                        handler (instruction);
                    else
                        ClearState();
                }
            }
        }

        void FindUserMessageFunctions ()
        {
            int current_function_offset = -1;
            int current_function_arg_count = -1;

            m_stream.Position = CodeOffset;
            while (m_stream.Position < m_stream.Length)
            {
                var instruction = ReadInstruction();

                if (IsMessageInstruction (instruction))
                {
                    if (current_function_offset >= 0 && current_function_arg_count >= 0 && m_stack.Count >= 4)
                    {
                        m_stack.Pop();
                        var name = m_stack.Pop();
                        var message = m_stack.Pop();
                        if (name.Type == SoftpalOperandType.Argument
                            && message.Type == SoftpalOperandType.Argument)
                        {
                            int name_arg_index = name.Value - 1;
                            int message_arg_index = message.Value - 1;
                            if (name_arg_index >= 0 && name_arg_index < current_function_arg_count
                                && message_arg_index >= 0 && message_arg_index < current_function_arg_count)
                            {
                                m_user_message_functions[current_function_offset] =
                                    new UserMessageFunction (current_function_arg_count, name_arg_index, message_arg_index);
                            }
                            current_function_offset = -1;
                            current_function_arg_count = -1;
                        }
                    }
                    ClearState();
                    continue;
                }

                switch (instruction.Opcode)
                {
                case SoftpalOpcodes.Enter:
                    current_function_offset = instruction.Offset;
                    current_function_arg_count = instruction.Operands[0].Value;
                    if (current_function_arg_count < 0 || current_function_arg_count > MaxStackDepth)
                    {
                        current_function_offset = -1;
                        current_function_arg_count = -1;
                    }
                    ClearState();
                    break;

                case SoftpalOpcodes.Mov:
                    if (instruction.Operands[0].Type == SoftpalOperandType.Variable)
                        HandleMovInstruction (instruction);
                    else
                        ClearState();
                    break;

                case SoftpalOpcodes.Push:
                    HandlePushInstruction (instruction);
                    break;

                case SoftpalOpcodes.Ret:
                    current_function_offset = -1;
                    current_function_arg_count = -1;
                    ClearState();
                    break;

                default:
                    ClearState();
                    break;
                }
            }
        }

        void HandleMovInstruction (SoftpalInstruction instruction)
        {
            if (instruction.Operands[0].Type == SoftpalOperandType.Variable)
                m_variables[instruction.Operands[0].Value] = instruction.Operands[1];
        }

        void HandlePushInstruction (SoftpalInstruction instruction)
        {
            SoftpalOperand operand;
            if (instruction.Operands[0].Type == SoftpalOperandType.Variable
                && m_variables.TryGetValue (instruction.Operands[0].Value, out operand))
            {
                Push (operand);
            }
            else
            {
                Push (instruction.Operands[0]);
            }
        }

        void HandleCallInstruction (SoftpalInstruction instruction)
        {
            try
            {
                if (instruction.Operands[0].Type != SoftpalOperandType.Literal)
                    return;
                int label_index = instruction.Operands[0].Value - 1;
                if (label_index < 0 || label_index >= m_label_offsets.Count)
                    return;

                UserMessageFunction message_function;
                int target_offset = m_label_offsets[label_index];
                if (!m_user_message_functions.TryGetValue (target_offset, out message_function)
                    || message_function.ArgumentCount < 0
                    || m_stack.Count < message_function.ArgumentCount)
                {
                    return;
                }

                var arguments = new List<SoftpalOperand> (message_function.ArgumentCount);
                for (int i = 0; i < message_function.ArgumentCount; ++i)
                    arguments.Add (m_stack.Pop());
                arguments.Reverse();

                var name = arguments[message_function.NameArgumentIndex];
                var message = arguments[message_function.MessageArgumentIndex];
                if (message.Type == SoftpalOperandType.Literal && message.Value >= 0)
                {
                    if (name.Type == SoftpalOperandType.Literal && name.Value >= 0)
                        OnTextAddressEncountered (name.Offset, SoftpalStringType.CharacterName);
                    OnTextAddressEncountered (message.Offset, SoftpalStringType.Message);
                }
            }
            finally
            {
                ClearState();
            }
        }

        void HandleSyscallInstruction (SoftpalInstruction instruction)
        {
            if (0x60002 == instruction.Operands[0].RawValue)
                HandleSelectChoiceInstruction (instruction);
            else
                m_stack.Clear();
        }

        void HandleMessageInstruction ()
        {
            try
            {
                if (m_stack.Count < 4)
                    return;

                m_stack.Pop();
                var name = m_stack.Pop();
                var message = m_stack.Pop();
                if (name.Type != SoftpalOperandType.Literal
                    || message.Type != SoftpalOperandType.Literal)
                {
                    return;
                }

                if (name.Value >= 0)
                    OnTextAddressEncountered (name.Offset, SoftpalStringType.CharacterName);
                if (message.Value >= 0)
                    OnTextAddressEncountered (message.Offset, SoftpalStringType.Message);
            }
            finally
            {
                ClearState();
            }
        }

        void HandleSelectChoiceInstruction (SoftpalInstruction instruction)
        {
            try
            {
                if (m_stack.Count < 1)
                    return;
                var choice = m_stack.Pop();
                if (choice.Type == SoftpalOperandType.Literal && choice.Value >= 0)
                    OnTextAddressEncountered (choice.Offset, SoftpalStringType.Message);
            }
            finally
            {
                ClearState();
            }
        }

        static bool IsMessageInstruction (SoftpalInstruction instruction)
        {
            switch (instruction.Opcode)
            {
            case SoftpalOpcodes.Text:
            case SoftpalOpcodes.TextW:
            case SoftpalOpcodes.TextA:
            case SoftpalOpcodes.TextWA:
            case SoftpalOpcodes.TextN:
            case SoftpalOpcodes.TextCat:
                return true;

            case SoftpalOpcodes.Syscall:
                switch (instruction.Operands[0].RawValue)
                {
                case 0x20002:
                case 0x2000F:
                case 0x20010:
                case 0x20011:
                case 0x20012:
                case 0x20013:
                    return true;
                default:
                    return false;
                }

            default:
                return false;
            }
        }

        SoftpalInstruction ReadInstruction ()
        {
            if (m_stream.Length - m_stream.Position < 4)
                throw new InvalidFormatException();
            int offset = (int)m_stream.Position;
            int raw_opcode = m_reader.ReadInt32();
            if ((raw_opcode >> 16) != 1)
                throw new InvalidFormatException();

            short opcode = unchecked ((short)raw_opcode);
            SoftpalOpcodeDescription description;
            if (!SoftpalOpcodes.Descriptions.TryGetValue (opcode, out description))
                throw new InvalidFormatException();
            long operand_size = 4L * description.OperandTypes.Length;
            if (operand_size > m_stream.Length - m_stream.Position)
                throw new InvalidFormatException();

            var instruction = new SoftpalInstruction (offset, opcode);
            for (int i = 0; i < description.OperandTypes.Length; ++i)
            {
                int operand_offset = (int)m_stream.Position;
                instruction.Operands.Add (new SoftpalOperand (operand_offset, m_reader.ReadInt32()));
            }
            return instruction;
        }

        void WriteInstruction (SoftpalInstruction instruction)
        {
            SoftpalOpcodeDescription description = SoftpalOpcodes.Descriptions[instruction.Opcode];
            string opcode_name = description.Name ?? instruction.Opcode.ToString ("X04");
            m_writer.Write ("{0:X08} {1}", instruction.Offset, opcode_name);

            for (int i = 0; i < instruction.Operands.Count; ++i)
            {
                m_writer.Write (0 == i ? " " : ", ");
                var operand = instruction.Operands[i];
                char type = description.OperandTypes[i];
                if ('l' == type && operand.Type == SoftpalOperandType.Literal)
                {
                    int label_index = operand.Value - 1;
                    if (label_index >= 0 && label_index < m_label_offsets.Count)
                        m_writer.Write ("#{0:X08}", m_label_offsets[label_index]);
                    else
                        m_writer.Write ("#<invalid:{0}>", operand.Value);
                }
                else if ('p' == type || 'l' == type)
                {
                    WriteOperand (operand);
                }
                else
                {
                    m_writer.Write ("0x{0:X}", operand.Value);
                }
            }

            m_writer.WriteLine ();
            if (instruction.Opcode == SoftpalOpcodes.Ret)
                m_writer.WriteLine ();
        }

        void WriteOperand (SoftpalOperand operand)
        {
            switch (operand.Type)
            {
            case SoftpalOperandType.Literal:
                m_writer.Write ("0x{0:X08}", operand.Value);
                break;
            case SoftpalOperandType.Variable:
                m_writer.Write ("var_{0}", operand.Value);
                break;
            case SoftpalOperandType.Argument:
                m_writer.Write ("arg_{0}", operand.Value);
                break;
            default:
                m_writer.Write ("{0}:[0x{1:X08}]", operand.Type, operand.Value);
                break;
            }
        }

        void Push (SoftpalOperand operand)
        {
            if (m_stack.Count >= MaxStackDepth)
                throw new InvalidFormatException();
            m_stack.Push (operand);
        }

        void ClearState ()
        {
            m_stack.Clear();
            m_variables.Clear();
        }

        void OnTextAddressEncountered (int offset, SoftpalStringType type)
        {
            var handler = TextAddressEncountered;
            if (null != handler)
                handler (offset, type);
        }

        sealed class UserMessageFunction
        {
            public readonly int ArgumentCount;
            public readonly int NameArgumentIndex;
            public readonly int MessageArgumentIndex;

            public UserMessageFunction (int argument_count, int name_argument_index, int message_argument_index)
            {
                ArgumentCount = argument_count;
                NameArgumentIndex = name_argument_index;
                MessageArgumentIndex = message_argument_index;
            }
        }
    }

    internal sealed class SoftpalInstruction
    {
        public readonly int Offset;
        public readonly short Opcode;
        public readonly List<SoftpalOperand> Operands = new List<SoftpalOperand>();

        public SoftpalInstruction (int offset, short opcode)
        {
            Offset = offset;
            Opcode = opcode;
        }
    }

    internal struct SoftpalOperand
    {
        public readonly int Offset;
        public readonly int RawValue;

        public SoftpalOperand (int offset, int raw_value)
        {
            Offset = offset;
            RawValue = raw_value;
        }

        public SoftpalOperandType Type { get { return (SoftpalOperandType)((RawValue >> 28) & 0xF); } }
        public int Value { get { return (RawValue << 4) >> 4; } }
    }

    internal enum SoftpalOperandType
    {
        Literal = 0,
        Variable = 4,
        Argument = 8,
    }

    internal struct SoftpalOpcodeDescription
    {
        public readonly string Name;
        public readonly string OperandTypes;

        public SoftpalOpcodeDescription (string name, string operand_types)
        {
            Name = name;
            OperandTypes = operand_types;
        }
    }

    internal static class SoftpalOpcodes
    {
        static SoftpalOpcodeDescription Describe (string name, string operands)
        {
            return new SoftpalOpcodeDescription (name, operands);
        }

        public static readonly Dictionary<short, SoftpalOpcodeDescription> Descriptions =
            new Dictionary<short, SoftpalOpcodeDescription> {
                { 0x0001, Describe ("mov", "pp") },
                { 0x0002, Describe ("add", "pp") },
                { 0x0003, Describe ("sub", "pp") },
                { 0x0004, Describe ("mul", "pp") },
                { 0x0005, Describe ("div", "pp") },
                { 0x0006, Describe ("binand", "pp") },
                { 0x0007, Describe ("binor", "pp") },
                { 0x0008, Describe ("binxor", "pp") },
                { 0x0009, Describe ("jmp", "l") },
                { 0x000A, Describe ("jz", "lp") },
                { 0x000B, Describe ("call", "l") },
                { 0x000C, Describe ("eq", "pp") },
                { 0x000D, Describe ("neq", "pp") },
                { 0x000E, Describe ("le", "pp") },
                { 0x000F, Describe ("ge", "pp") },
                { 0x0010, Describe ("lt", "pp") },
                { 0x0011, Describe ("gt", "pp") },
                { 0x0012, Describe ("logor", "pp") },
                { 0x0013, Describe ("logand", "pp") },
                { 0x0014, Describe ("not", "i") },
                { 0x0015, Describe ("exit", "") },
                { 0x0016, Describe ("nop", "") },
                { 0x0017, Describe ("syscall", "ii") },
                { 0x0018, Describe ("ret", "") },
                { 0x0019, Describe (null, "") },
                { 0x001A, Describe ("mod", "pp") },
                { 0x001B, Describe ("shl", "pp") },
                { 0x001C, Describe ("sar", "pp") },
                { 0x001D, Describe ("neg", "i") },
                { 0x001E, Describe ("pop", "p") },
                { 0x001F, Describe ("push", "p") },
                { 0x0020, Describe ("enter", "p") },
                { 0x0021, Describe ("leave", "p") },
                { 0x0023, Describe ("create_message", "") },
                { 0x0024, Describe ("get_message", "") },
                { 0x0025, Describe ("get_message_param", "") },
                { 0x0028, Describe ("se_load", "") },
                { 0x0029, Describe ("se_play", "") },
                { 0x002A, Describe ("se_play_ex", "") },
                { 0x002B, Describe ("se_stop", "") },
                { 0x002C, Describe ("se_set_volume", "") },
                { 0x002D, Describe ("se_get_volume", "") },
                { 0x002E, Describe ("se_unload", "") },
                { 0x002F, Describe ("se_wait", "") },
                { 0x0030, Describe ("set_se_info", "") },
                { 0x0031, Describe ("get_se_ex_volume", "") },
                { 0x0032, Describe ("set_se_ex_volume", "") },
                { 0x0033, Describe ("se_enable", "") },
                { 0x0034, Describe ("is_se_enable", "") },
                { 0x0035, Describe ("se_set_pan", "") },
                { 0x0036, Describe ("se_mute", "") },
                { 0x0038, Describe ("select_init", "") },
                { 0x0039, Describe ("select", "") },
                { 0x003A, Describe ("select_add_choice", "") },
                { 0x003B, Describe ("end_select", "") },
                { 0x003C, Describe ("select_clear", "") },
                { 0x003D, Describe ("select_set_offset", "") },
                { 0x003E, Describe ("select_set_process", "") },
                { 0x003F, Describe ("select_lock", "") },
                { 0x0040, Describe ("get_select_on_key", "") },
                { 0x0041, Describe ("get_select_pull_key", "") },
                { 0x0042, Describe ("get_select_push_key", "") },
                { 0x0044, Describe ("skip_set", "") },
                { 0x0045, Describe ("skip_is", "") },
                { 0x0046, Describe ("auto_set", "") },
                { 0x0047, Describe ("auto_is", "") },
                { 0x0048, Describe ("auto_set_time", "") },
                { 0x0049, Describe ("auto_get_time", "") },
                { 0x004A, Describe ("window_set_mode", "") },
                { 0x004B, Describe (null, "") },
                { 0x004C, Describe (null, "") },
                { 0x004D, Describe (null, "") },
                { 0x004E, Describe (null, "") },
                { 0x004F, Describe ("effect_enable_is", "") },
                { 0x0050, Describe ("cursor_pos_get", "") },
                { 0x0051, Describe ("time_get", "") },
                { 0x0052, Describe (null, "") },
                { 0x0053, Describe ("load_font", "") },
                { 0x0054, Describe ("unload_font", "") },
                { 0x0055, Describe ("set_font_type", "") },
                { 0x0056, Describe ("key_cancel", "") },
                { 0x0057, Describe ("set_font_color", "") },
                { 0x0058, Describe ("load_font_ex", "") },
                { 0x0059, Describe (null, "") },
                { 0x005A, Describe (null, "") },
                { 0x005B, Describe ("lpush", "") },
                { 0x005C, Describe ("lpop", "") },
                { 0x005D, Describe (null, "") },
                { 0x005E, Describe (null, "") },
                { 0x005F, Describe ("set_font_size", "") },
                { 0x0060, Describe ("get_font_size", "") },
                { 0x0061, Describe ("get_font_type", "") },
                { 0x0062, Describe ("set_font_effect", "") },
                { 0x0063, Describe ("get_font_effect", "") },
                { 0x0064, Describe ("get_pull_key", "") },
                { 0x0065, Describe ("get_on_key", "") },
                { 0x0066, Describe ("get_push_key", "") },
                { 0x0067, Describe ("input_clear", "") },
                { 0x0068, Describe ("change_window_size", "") },
                { 0x0069, Describe ("change_aspect_mode", "") },
                { 0x006A, Describe ("aspect_position_enable", "") },
                { 0x006B, Describe (null, "") },
                { 0x006C, Describe ("get_aspect_mode", "") },
                { 0x006D, Describe ("get_monitor_size", "") },
                { 0x006E, Describe ("get_window_pos", "") },
                { 0x006F, Describe ("get_system_metrics", "") },
                { 0x0070, Describe ("set_system_path", "") },
                { 0x0071, Describe ("set_allmosaicthumbnail", "") },
                { 0x0072, Describe ("enable_window_change", "") },
                { 0x0073, Describe ("is_enable_window_change", "") },
                { 0x0074, Describe ("set_cursor", "") },
                { 0x0075, Describe ("set_hide_cursor_time", "") },
                { 0x0076, Describe ("get_hide_cursor_time", "") },
                { 0x0077, Describe ("scene_skip", "") },
                { 0x0078, Describe ("cancel_scene_skip", "") },
                { 0x0079, Describe ("lsize", "") },
                { 0x007A, Describe ("get_async_key", "") },
                { 0x007B, Describe ("get_font_color", "") },
                { 0x007C, Describe ("get_current_date", "") },
                { 0x007D, Describe ("history_skip", "") },
                { 0x007E, Describe ("cancel_history_skip", "") },
                { 0x007F, Describe (null, "") },
                { 0x0081, Describe ("system_btn_set", "") },
                { 0x0082, Describe ("system_btn_release", "") },
                { 0x0083, Describe ("system_btn_enable", "") },
                { 0x0086, Describe ("text_init", "") },
                { 0x0087, Describe ("text_set_icon", "") },
                { 0x0088, Describe ("text", "") },
                { 0x0089, Describe ("text_hide", "") },
                { 0x008A, Describe ("text_show", "") },
                { 0x008B, Describe ("text_set_btn", "") },
                { 0x008C, Describe ("text_uninit", "") },
                { 0x008D, Describe ("text_set_rect", "") },
                { 0x008E, Describe ("text_clear", "") },
                { 0x008F, Describe (null, "") },
                { 0x0090, Describe ("text_get_time", "") },
                { 0x0091, Describe ("text_window_set_alpha", "") },
                { 0x0092, Describe ("text_voice_play", "") },
                { 0x0093, Describe (null, "") },
                { 0x0094, Describe ("text_set_icon_animation_time", "") },
                { 0x0095, Describe ("text_w", "") },
                { 0x0096, Describe ("text_a", "") },
                { 0x0097, Describe ("text_wa", "") },
                { 0x0098, Describe ("text_n", "") },
                { 0x0099, Describe ("text_cat", "") },
                { 0x009A, Describe ("set_history", "") },
                { 0x009B, Describe ("is_text_visible", "") },
                { 0x009C, Describe ("text_set_base", "") },
                { 0x009D, Describe ("enable_voice_cut", "") },
                { 0x009E, Describe ("is_voice_cut", "") },
                { 0x009F, Describe (null, "") },
                { 0x00A0, Describe (null, "") },
                { 0x00A1, Describe (null, "") },
                { 0x00A2, Describe ("text_set_color", "") },
                { 0x00A3, Describe ("text_redraw", "") },
                { 0x00A4, Describe ("set_text_mode", "") },
                { 0x00A5, Describe ("text_init_visualnovelmode", "") },
                { 0x00A6, Describe ("text_set_icon_mode", "") },
                { 0x00A7, Describe ("text_vn_br", "") },
                { 0x00A8, Describe (null, "") },
                { 0x00A9, Describe (null, "") },
                { 0x00AA, Describe (null, "") },
                { 0x00AB, Describe (null, "") },
                { 0x00AC, Describe ("tips_get_str", "") },
                { 0x00AD, Describe ("tips_get_param", "") },
                { 0x00AE, Describe ("tips_reset", "") },
                { 0x00AF, Describe ("tips_search", "") },
                { 0x00B0, Describe ("tips_set_color", "") },
                { 0x00B1, Describe ("tips_stop", "") },
                { 0x00B2, Describe ("tips_get_flag", "") },
                { 0x00B3, Describe ("tips_init", "") },
                { 0x00B4, Describe ("tips_pause", "") },
                { 0x00B6, Describe ("voice_play", "") },
                { 0x00B7, Describe ("voice_stop", "") },
                { 0x00B8, Describe ("voice_set_volume", "") },
                { 0x00B9, Describe ("voice_get_volume", "") },
                { 0x00BA, Describe ("set_voice_info", "") },
                { 0x00BB, Describe ("voice_enable", "") },
                { 0x00BC, Describe ("is_voice_enable", "") },
                { 0x00BD, Describe (null, "") },
                { 0x00BE, Describe ("bgv_play", "") },
                { 0x00BF, Describe ("bgv_stop", "") },
                { 0x00C0, Describe ("bgv_enable", "") },
                { 0x00C1, Describe ("get_voice_ex_volume", "") },
                { 0x00C2, Describe ("set_voice_ex_volume", "") },
                { 0x00C3, Describe ("voice_check_enable", "") },
                { 0x00C4, Describe ("voice_autopan_initialize", "") },
                { 0x00C5, Describe ("voice_autopan_enable", "") },
                { 0x00C6, Describe ("set_voice_autopan", "") },
                { 0x00C7, Describe ("is_voice_autopan_enable", "") },
                { 0x00C8, Describe ("voice_wait", "") },
                { 0x00C9, Describe ("bgv_pause", "") },
                { 0x00CA, Describe ("bgv_mute", "") },
                { 0x00CB, Describe ("set_bgv_volume", "") },
                { 0x00CC, Describe ("get_bgv_volume", "") },
                { 0x00CD, Describe ("set_bgv_auto_volume", "") },
                { 0x00CE, Describe ("voice_mute", "") },
                { 0x00CF, Describe ("voice_call", "") },
                { 0x00D0, Describe ("voice_call_clear", "") },
                { 0x00D2, Describe ("wait", "") },
                { 0x00D3, Describe ("wait_click", "") },
                { 0x00D4, Describe ("wait_sync_begin", "") },
                { 0x00D5, Describe ("wait_sync", "") },
                { 0x00D6, Describe ("wait_sync_end", "") },
                { 0x00D7, Describe (null, "") },
                { 0x00D8, Describe ("wait_clear", "") },
                { 0x00D9, Describe ("wait_click_no_anim", "") },
                { 0x00DA, Describe ("wait_sync_get_time", "") },
                { 0x00DB, Describe ("wait_time_push", "") },
                { 0x00DC, Describe ("wait_time_pop", "") },
            };

        public const short Mov = 0x0001;
        public const short Call = 0x000B;
        public const short Syscall = 0x0017;
        public const short Ret = 0x0018;
        public const short Push = 0x001F;
        public const short Enter = 0x0020;
        public const short SelectAddChoice = 0x003A;
        public const short Text = 0x0088;
        public const short TextW = 0x0095;
        public const short TextA = 0x0096;
        public const short TextWA = 0x0097;
        public const short TextN = 0x0098;
        public const short TextCat = 0x0099;
    }
}
