//! \file       ScriptBGI.cs
//! \date       Mon May 18 2026
//! \brief      BGI/Ethornell bytecode script text extractor.
//
// BGI/Ethornell bytecode parsing is adapted from VNTranslationTools.
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

namespace GameRes.Formats.BGI
{
    [Export(typeof(ScriptFormat))]
    public class BgiScriptFormat : GenericScriptFormat, IConfigurableScriptFormat
    {
        public const string FormatTag = "BGI/Script";
        public const string V1Magic = "BurikoCompiledScriptVer1.00\0";

        const uint V1Signature = 0x69727542; // 'Buri'
        static readonly string[] s_text_modes = { ScriptTextMode.Filtered, ScriptTextMode.Raw, ScriptTextMode.Dump, ScriptTextMode.JsonLines };

        public override string         Tag { get { return FormatTag; } }
        public override string Description { get { return "BGI/Ethornell bytecode script"; } }
        public override uint     Signature { get { return V1Signature; } }

        public IEnumerable<string> TextModes { get { return s_text_modes; } }
        public string DefaultTextMode { get { return ScriptTextMode.Filtered; } }

        public BgiScriptFormat ()
        {
            Extensions = new[] { "_bp" };
            Signatures = new[] { Signature, 0u };
        }

        public override bool IsScript (IBinaryStream file)
        {
            return BgiScript.IsScript (file);
        }

        public override Stream ConvertFrom (IBinaryStream file)
        {
            return ConvertFrom (file, ScriptTextMode.Filtered);
        }

        public Stream ConvertFrom (IBinaryStream file, string text_mode)
        {
            var script = BgiScript.Read (file);
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
                    bool include_internal = string.Equals (text_mode, ScriptTextMode.Raw, StringComparison.OrdinalIgnoreCase);
                    foreach (var line in script.ExtractText (include_internal))
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
                var script = BgiScript.Read (input);
                var data = new ScriptData();
                foreach (var line in script.ExtractText (false))
                    data.TextLines.Add (line);
                return data;
            }
        }

        internal static bool HasV1Signature (ArcView.Frame view, long offset, uint size)
        {
            return size >= V1Magic.Length && view.AsciiEqual (offset, V1Magic);
        }
    }

    internal sealed class BgiScript
    {
        readonly byte[] m_data;
        readonly string m_name;
        readonly List<BgiStringRef> m_strings = new List<BgiStringRef>();

        int m_code_offset;
        int m_code_length;
        int m_version;

        BgiScript (byte[] data, string name)
        {
            m_data = data;
            m_name = name;
            ReadCode();
        }

        public static bool IsScript (IBinaryStream file)
        {
            long position = file.Position;
            try
            {
                if (file.Length < 2 || file.Length > int.MaxValue)
                    return false;
                file.Position = 0;
                bool has_v1_magic = StartsWithV1Magic (file);
                if (!has_v1_magic && !IsV0ScriptName (file.Name))
                    return false;
                file.Position = 0;
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

        public static BgiScript Read (IBinaryStream file)
        {
            if (file.Length < 2 || file.Length > int.MaxValue)
                throw new InvalidFormatException();
            file.Position = 0;
            var data = file.ReadBytes ((int)file.Length);
            if (data.Length != (int)file.Length)
                throw new EndOfStreamException();
            return new BgiScript (data, file.Name);
        }

        static bool IsV0ScriptName (string name)
        {
            return name.HasExtension ("._bp");
        }

        static bool StartsWithV1Magic (IBinaryStream file)
        {
            if (file.Length < BgiScriptFormat.V1Magic.Length)
                return false;
            var header = file.ReadBytes (BgiScriptFormat.V1Magic.Length);
            file.Position = 0;
            return StartsWithV1Magic (header);
        }

        static bool StartsWithV1Magic (byte[] data)
        {
            if (data.Length < BgiScriptFormat.V1Magic.Length)
                return false;
            for (int i = 0; i < BgiScriptFormat.V1Magic.Length; ++i)
            {
                if (data[i] != (byte)BgiScriptFormat.V1Magic[i])
                    return false;
            }
            return true;
        }

        void ReadCode ()
        {
            using (var stream = new MemoryStream (m_data, false))
            {
                BgiDisassembler disasm;
                if (StartsWithV1Magic (m_data))
                {
                    m_version = 1;
                    disasm = new BgiV1Disassembler (stream);
                }
                else
                {
                    m_version = 0;
                    disasm = new BgiV0Disassembler (stream);
                }
                m_code_offset = disasm.CodeOffset;
                disasm.StringAddressEncountered += OnStringAddressEncountered;
                disasm.Disassemble();
                m_code_length = checked ((int)stream.Position - m_code_offset);
                if (m_code_length < 0)
                    throw new InvalidFormatException();
            }
        }

        void OnStringAddressEncountered (int operand_offset, int address, BgiStringType type)
        {
            long text_offset = (long)m_code_offset + address;
            if (text_offset < 0 || text_offset >= m_data.Length)
                return;
            m_strings.Add (new BgiStringRef (operand_offset, (int)text_offset, type));
        }

        public IEnumerable<ScriptLine> ExtractText (bool include_internal)
        {
            uint id = 0;
            foreach (var item in m_strings)
            {
                if (!include_internal
                    && (item.Type == BgiStringType.Internal || item.Type == BgiStringType.Voice))
                    continue;
                string text;
                if (!TryReadString (item.TextOffset, out text) || string.IsNullOrEmpty (text))
                    continue;
                yield return new ScriptLine { Id = id++, Text = text };
            }
        }

        public IEnumerable<ScriptTextEntry> ExtractJsonEntries ()
        {
            var pending_names = new List<string>();
            string pending_voice = null;
            foreach (var item in m_strings)
            {
                if (item.Type == BgiStringType.Internal)
                    continue;
                string text;
                if (!TryReadString (item.TextOffset, out text) || string.IsNullOrEmpty (text))
                    continue;
                if (item.Type == BgiStringType.Voice)
                {
                    pending_voice = text;
                    continue;
                }
                if (item.Type == BgiStringType.CharacterName)
                {
                    pending_names.Add (text);
                    continue;
                }

                var entry = new ScriptTextEntry (text);
                entry.Names.AddRange (pending_names);
                entry.Voice = pending_voice;
                pending_names.Clear();
                pending_voice = null;
                yield return entry;
            }
        }

        public void WriteDump (TextWriter writer)
        {
            writer.WriteLine ("# BGI/Ethornell decoded script dump");
            writer.WriteLine ("# This is not reconstructed source; it lists decoded string references.");
            writer.WriteLine ();
            writer.WriteLine ("[Header]");
            writer.WriteLine ("Name={0}", m_name);
            writer.WriteLine ("Version={0}", m_version);
            writer.WriteLine ("CodeOffset=0x{0:X8}", m_code_offset);
            writer.WriteLine ("CodeLength=0x{0:X8}", m_code_length);
            writer.WriteLine ("StringRefCount={0}", m_strings.Count);
            writer.WriteLine ();
            writer.WriteLine ("[Strings]");
            for (int i = 0; i < m_strings.Count; ++i)
            {
                var item = m_strings[i];
                string text;
                if (!TryReadString (item.TextOffset, out text))
                    text = "<invalid>";
                writer.WriteLine ("#{0:D4} operand=0x{1:X8} text=0x{2:X8} type={3}",
                                  i, item.OperandOffset, item.TextOffset, item.Type);
                writer.WriteLine (text);
                writer.WriteLine ();
            }
        }

        bool TryReadString (int offset, out string text)
        {
            text = null;
            if (offset < 0 || offset >= m_data.Length)
                return false;
            int end = Array.IndexOf<byte> (m_data, 0, offset);
            if (end < offset)
                return false;
            text = Encodings.cp932.GetString (m_data, offset, end - offset);
            return true;
        }
    }

    internal enum BgiStringType
    {
        CharacterName,
        Message,
        Voice,
        Internal,
    }

    internal struct BgiStringRef
    {
        public readonly int OperandOffset;
        public readonly int TextOffset;
        public readonly BgiStringType Type;

        public BgiStringRef (int operand_offset, int text_offset, BgiStringType type)
        {
            OperandOffset = operand_offset;
            TextOffset = text_offset;
            Type = type;
        }
    }

    internal abstract class BgiDisassembler
    {
        protected readonly BinaryReader m_reader;
        protected int m_largest_code_address_operand;

        protected BgiDisassembler (Stream input)
        {
            m_reader = new BinaryReader (input);
        }

        public abstract int CodeOffset { get; }
        public abstract void Disassemble ();

        public delegate void CodeAddressHandler (int offset, int address);
        public delegate void StringAddressHandler (int offset, int address, BgiStringType type);

        public event CodeAddressHandler CodeAddressEncountered;
        public event StringAddressHandler StringAddressEncountered;

        protected void ReadOperands (string template)
        {
            foreach (char c in template)
            {
                switch (c)
                {
                case 'h':
                    m_reader.ReadInt16();
                    break;

                case 'i':
                    m_reader.ReadInt32();
                    break;

                case 'c':
                    ReadCodeAddress();
                    break;

                case 'n':
                    ReadStringAddress (BgiStringType.CharacterName);
                    break;

                case 'm':
                    ReadStringAddress (BgiStringType.Message);
                    break;

                case 'z':
                    SkipInlineStringOperand();
                    break;

                default:
                    throw new InvalidFormatException();
                }
            }
        }

        protected void ReadCodeAddress ()
        {
            int offset = (int)m_reader.BaseStream.Position;
            int address = m_reader.ReadInt32();
            OnCodeAddressEncountered (offset, address);
        }

        protected void ReadStringAddress (BgiStringType type)
        {
            int offset = (int)m_reader.BaseStream.Position;
            int address = m_reader.ReadInt32();
            OnStringAddressEncountered (offset, address, type);
        }

        protected void SkipInlineStringOperand ()
        {
            while (m_reader.BaseStream.Position < m_reader.BaseStream.Length)
            {
                if (0 == m_reader.ReadByte())
                    return;
            }
            throw new EndOfStreamException();
        }

        protected bool IsEmptyString (int address)
        {
            long position = m_reader.BaseStream.Position;
            long offset = (long)CodeOffset + address;
            if (offset < 0 || offset >= m_reader.BaseStream.Length)
                return true;
            m_reader.BaseStream.Position = offset;
            bool is_empty = 0 == m_reader.ReadByte();
            m_reader.BaseStream.Position = position;
            return is_empty;
        }

        protected string ReadStringAtAddress (int address)
        {
            long position = m_reader.BaseStream.Position;
            long offset = (long)CodeOffset + address;
            if (offset < 0 || offset >= m_reader.BaseStream.Length)
                return string.Empty;
            m_reader.BaseStream.Position = offset;
            var bytes = new List<byte>();
            while (m_reader.BaseStream.Position < m_reader.BaseStream.Length)
            {
                byte b = m_reader.ReadByte();
                if (0 == b)
                    break;
                bytes.Add (b);
            }
            m_reader.BaseStream.Position = position;
            return Encodings.cp932.GetString (bytes.ToArray());
        }

        protected void OnCodeAddressEncountered (int offset, int address)
        {
            var handler = CodeAddressEncountered;
            if (null != handler)
                handler (offset, address);
            if (address > m_largest_code_address_operand)
                m_largest_code_address_operand = address;
        }

        protected void OnStringAddressEncountered (int offset, int address, BgiStringType type)
        {
            var handler = StringAddressEncountered;
            if (null != handler)
                handler (offset, address, type);
        }

        protected int ReadCount ()
        {
            int count = m_reader.ReadInt32();
            if (count < 0 || count > 0x100000)
                throw new InvalidFormatException();
            return count;
        }
    }

    internal sealed class BgiV0Disassembler : BgiDisassembler
    {
        static readonly Dictionary<ushort, string> OperandTemplates = new Dictionary<ushort, string>
        {
            { 0x0010, "iim" },
            { 0x0011, "" },
            { 0x0012, "zz" },
            { 0x0013, "z" },
            { 0x0014, "z" },
            { 0x0015, "" },
            { 0x0018, "iiiii" },
            { 0x0019, "iiii" },
            { 0x001A, "iii" },
            { 0x001B, "ziii" },
            { 0x001F, "i" },
            { 0x0020, "" },
            { 0x0021, "" },
            { 0x0022, "i" },
            { 0x0024, "iiiii" },
            { 0x0025, "ii" },
            { 0x0028, "zi" },
            { 0x0029, "zzi" },
            { 0x002A, "i" },
            { 0x002B, "zi" },
            { 0x002C, "ziiiiiiii" },
            { 0x002D, "ziiiiiiii" },
            { 0x002E, "iiiii" },
            { 0x0030, "zi" },
            { 0x0031, "zii" },
            { 0x0032, "i" },
            { 0x0033, "i" },
            { 0x0034, "ii" },
            { 0x0035, "i" },
            { 0x0036, "i" },
            { 0x0037, "" },
            { 0x0038, "iziiiii" },
            { 0x0039, "ii" },
            { 0x003A, "iziiiiiiii" },
            { 0x003B, "iiiiii" },
            { 0x003C, "iiiiiiiiii" },
            { 0x003D, "iiiiiiiiiii" },
            { 0x003F, "i" },
            { 0x0040, "iizii" },
            { 0x0041, "iizii" },
            { 0x0042, "iizi" },
            { 0x0043, "iizi" },
            { 0x0044, "iizi" },
            { 0x0045, "iizi" },
            { 0x0046, "izi" },
            { 0x0047, "izi" },
            { 0x0048, "ii" },
            { 0x0049, "ii" },
            { 0x004A, "izi" },
            { 0x004B, "" },
            { 0x004C, "zi" },
            { 0x004D, "zi" },
            { 0x004E, "i" },
            { 0x004F, "i" },
            { 0x0050, "zi" },
            { 0x0051, "zzi" },
            { 0x0052, "i" },
            { 0x0053, "zi" },
            { 0x0054, "zii" },
            { 0x0060, "iiiii" },
            { 0x0061, "ii" },
            { 0x0062, "iiiiii" },
            { 0x0065, "i" },
            { 0x0066, "ii" },
            { 0x0067, "i" },
            { 0x0068, "i" },
            { 0x0069, "i" },
            { 0x006A, "i" },
            { 0x006B, "i" },
            { 0x006C, "i" },
            { 0x006E, "iii" },
            { 0x006F, "i" },
            { 0x0070, "izi" },
            { 0x0071, "i" },
            { 0x0072, "iii" },
            { 0x0073, "iii" },
            { 0x0074, "izi" },
            { 0x0075, "i" },
            { 0x0076, "iii" },
            { 0x0078, "izi" },
            { 0x0079, "i" },
            { 0x007A, "iii" },
            { 0x0080, "izii" },
            { 0x0081, "z" },
            { 0x0082, "i" },
            { 0x0083, "i" },
            { 0x0084, "izi" },
            { 0x0085, "z" },
            { 0x0086, "i" },
            { 0x0087, "i" },
            { 0x0088, "z" },
            { 0x008C, "i" },
            { 0x008D, "i" },
            { 0x008E, "i" },
            { 0x0090, "i" },
            { 0x0091, "i" },
            { 0x0092, "i" },
            { 0x0093, "i" },
            { 0x0094, "i" },
            { 0x0098, "ii" },
            { 0x0099, "ii" },
            { 0x009A, "ii" },
            { 0x009B, "ii" },
            { 0x009C, "ii" },
            { 0x009D, "ii" },
            { 0x00A0, "c" },
            { 0x00A1, "ic" },
            { 0x00A2, "ic" },
            { 0x00A3, "iic" },
            { 0x00A4, "iic" },
            { 0x00A5, "iic" },
            { 0x00A6, "iic" },
            { 0x00A7, "iic" },
            { 0x00A8, "iic" },
            { 0x00AC, "c" },
            { 0x00AD, "" },
            { 0x00AE, "i" },
            { 0x00AF, "" },
            { 0x00B8, "" },
            { 0x00B9, "i" },
            { 0x00BA, "i" },
            { 0x00C0, "z" },
            { 0x00C1, "z" },
            { 0x00C2, "" },
            { 0x00C4, "i" },
            { 0x00C8, "z" },
            { 0x00C9, "" },
            { 0x00CA, "i" },
            { 0x00D0, "" },
            { 0x00D4, "i" },
            { 0x00D8, "i" },
            { 0x00D9, "i" },
            { 0x00DA, "i" },
            { 0x00DB, "i" },
            { 0x00DC, "i" },
            { 0x00F8, "z" },
            { 0x00F9, "zi" },
            { 0x00FE, "h" },
            { 0x0110, "zz" },
            { 0x0111, "i" },
            { 0x0120, "i" },
            { 0x0121, "i" },
            { 0x0128, "zii" },
            { 0x012A, "ii" },
            { 0x0134, "ii" },
            { 0x0135, "i" },
            { 0x0136, "i" },
            { 0x0138, "iziiiiziii" },
            { 0x013B, "iiiiiiii" },
            { 0x0140, "iiziiii" },
            { 0x0141, "iiziiii" },
            { 0x0142, "iiziii" },
            { 0x0143, "iiziii" },
            { 0x0144, "iiziii" },
            { 0x0145, "iiziii" },
            { 0x0146, "iziii" },
            { 0x0147, "iziii" },
            { 0x0148, "ii" },
            { 0x0149, "ii" },
            { 0x014B, "ziiz" },
            { 0x0150, "zii" },
            { 0x0151, "ziii" },
            { 0x0152, "ii" },
            { 0x0153, "iii" },
            { 0x016E, "iiiiii" },
            { 0x016F, "iiiiiii" },
            { 0x0170, "izzii" },
            { 0x01C0, "zz" },
            { 0x01C1, "zz" },
            { 0x0249, "z" },
            { 0x024C, "zziii" },
            { 0x024D, "z" },
            { 0x024E, "zz" },
            { 0x024F, "z" },
        };

        public BgiV0Disassembler (Stream input) : base (input)
        {
        }

        public override int CodeOffset { get { return 0; } }

        public override void Disassemble ()
        {
            bool found_end = false;
            m_reader.BaseStream.Position = CodeOffset;
            while (m_reader.BaseStream.Position + 2 <= m_reader.BaseStream.Length)
            {
                ushort opcode = m_reader.ReadUInt16();
                switch (opcode)
                {
                case 0x00A9:
                    ReadCodeAddressList();
                    break;
                case 0x00B0:
                case 0x00B4:
                    ReadInlineStringList();
                    break;
                case 0x00FD:
                    ReadStringCodePairList();
                    break;
                case 0x0248:
                    throw new InvalidFormatException();
                default:
                    string template;
                    if (!OperandTemplates.TryGetValue (opcode, out template))
                        throw new InvalidFormatException();
                    ReadOperands (template);
                    break;
                }

                if (opcode == 0x00C2 && m_largest_code_address_operand < (int)m_reader.BaseStream.Position - CodeOffset)
                {
                    found_end = true;
                    break;
                }
            }
            if (!found_end)
                throw new InvalidFormatException();
        }

        void ReadCodeAddressList ()
        {
            int count = ReadCount();
            for (int i = 0; i < count; ++i)
                ReadCodeAddress();
        }

        void ReadInlineStringList ()
        {
            int count = ReadCount();
            for (int i = 0; i < count; ++i)
                SkipInlineStringOperand();
        }

        void ReadStringCodePairList ()
        {
            int count = ReadCount();
            for (int i = 0; i < count; ++i)
            {
                SkipInlineStringOperand();
                ReadCodeAddress();
            }
        }
    }

    internal sealed class BgiV1Disassembler : BgiDisassembler
    {
        readonly int m_code_offset;
        readonly Stack<BgiStackItem> m_string_stack = new Stack<BgiStackItem>();

        public BgiV1Disassembler (Stream input) : base (input)
        {
            m_reader.BaseStream.Position = BgiScriptFormat.V1Magic.Length;
            int header_size = m_reader.ReadInt32();
            if (header_size < 4)
                throw new InvalidFormatException();
            m_code_offset = checked (BgiScriptFormat.V1Magic.Length + header_size);
            if (m_code_offset > m_reader.BaseStream.Length)
                throw new InvalidFormatException();
        }

        public override int CodeOffset { get { return m_code_offset; } }

        public override void Disassemble ()
        {
            bool found_end = false;
            m_reader.BaseStream.Position = CodeOffset;
            while (m_reader.BaseStream.Position + 4 <= m_reader.BaseStream.Length)
            {
                int opcode = m_reader.ReadInt32();
                switch (opcode)
                {
                case 0x0003:
                    ReadPushStringAddressOperand();
                    break;
                case 0x001C:
                    HandleUserFunctionCall();
                    break;
                case 0x0140:
                case 0x0143:
                    HandleMessage();
                    break;
                case 0x0160:
                    HandleChoiceScreen();
                    break;
                default:
                    ReadOperands (GetOperandTemplate (opcode));
                    break;
                }

                if ((opcode == 0x001B || opcode == 0x00F4)
                    && m_largest_code_address_operand < (int)m_reader.BaseStream.Position - CodeOffset)
                {
                    found_end = true;
                    break;
                }
                if (opcode == 0x007E || opcode == 0x007F || opcode == 0x00FE)
                    OutputInternalStrings();
            }
            if (!found_end)
                throw new InvalidFormatException();
            OutputInternalStrings();
        }

        static string GetOperandTemplate (int opcode)
        {
            switch (opcode)
            {
            case 0x0000:
            case 0x0002:
            case 0x0008:
            case 0x0009:
            case 0x000A:
            case 0x0017:
            case 0x0019:
            case 0x003F:
            case 0x007E:
                return "i";
            case 0x0001:
                return "c";
            case 0x007B:
                return "iii";
            case 0x007F:
                return "ii";
            default:
                return "";
            }
        }

        void ReadPushStringAddressOperand ()
        {
            int offset = (int)m_reader.BaseStream.Position;
            int address = m_reader.ReadInt32();
            m_string_stack.Push (new BgiStackItem (offset, address));
        }

        void HandleUserFunctionCall ()
        {
            if (m_string_stack.Count == 0)
                return;
            var item = m_string_stack.Pop();
            string function = ReadStringAtAddress (item.Value);
            OnStringAddressEncountered (item.Offset, item.Value, BgiStringType.Internal);
            if ("_SelectEx" == function)
                HandleChoiceScreen();
            else if ("_PlayVoice" == function)
                HandleVoice();
        }

        void HandleVoice ()
        {
            if (m_string_stack.Count == 0)
                return;
            var voice = m_string_stack.Pop();
            var type = !IsEmptyString (voice.Value) ? BgiStringType.Voice : BgiStringType.Internal;
            OnStringAddressEncountered (voice.Offset, voice.Value, type);
        }

        void HandleMessage ()
        {
            if (m_string_stack.Count == 0)
                throw new InvalidFormatException();
            var message = m_string_stack.Pop();
            if (m_string_stack.Count > 0)
            {
                var name = m_string_stack.Pop();
                var type = !IsEmptyString (name.Value) ? BgiStringType.CharacterName : BgiStringType.Internal;
                OnStringAddressEncountered (name.Offset, name.Value, type);
            }
            OnStringAddressEncountered (message.Offset, message.Value,
                                        !IsEmptyString (message.Value) ? BgiStringType.Message : BgiStringType.Internal);
        }

        void HandleChoiceScreen ()
        {
            var choices = new List<BgiStackItem>();
            while (m_string_stack.Count > 0)
                choices.Insert (0, m_string_stack.Pop());
            foreach (var item in choices)
                OnStringAddressEncountered (item.Offset, item.Value, BgiStringType.Message);
        }

        void OutputInternalStrings ()
        {
            while (m_string_stack.Count > 0)
            {
                var item = m_string_stack.Pop();
                OnStringAddressEncountered (item.Offset, item.Value, BgiStringType.Internal);
            }
        }

        struct BgiStackItem
        {
            public readonly int Offset;
            public readonly int Value;

            public BgiStackItem (int offset, int value)
            {
                Offset = offset;
                Value = value;
            }
        }
    }
}
