//! \file       ScriptPS3.cs
//! \date       Sun May 17 2026
//! \brief      CMVS engine PS2A/PS3 script text extractor.
//

using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.Text;
using GameRes.Utility;

namespace GameRes.Formats.Purple
{
    [Export(typeof(ScriptFormat))]
    public class Ps3ScriptFormat : GenericScriptFormat, IConfigurableScriptFormat
    {
        public override string         Tag { get { return "PS3/CMVS"; } }
        public override string Description { get { return "CMVS engine script"; } }
        public override uint     Signature { get { return 0x41325350; } } // 'PS2A'

        const int MinHeaderSize = 0x30;
        static readonly string[] s_text_modes = { ScriptTextMode.Filtered, ScriptTextMode.Raw };

        public IEnumerable<string> TextModes { get { return s_text_modes; } }
        public string DefaultTextMode { get { return ScriptTextMode.Filtered; } }

        public Ps3ScriptFormat ()
        {
            Extensions = new[] { "ps3", "ps2" };
        }

        public override bool IsScript (IBinaryStream file)
        {
            if (file.Signature != Signature)
                return false;
            var ext = Path.GetExtension (file.Name);
            if (!ext.Equals (".ps3", StringComparison.OrdinalIgnoreCase)
                && !ext.Equals (".ps2", StringComparison.OrdinalIgnoreCase))
                return false;
            var header = file.ReadHeader (MinHeaderSize);
            return IsSaneHeader (header.ToArray(), file.Length);
        }

        public override Stream ConvertFrom (IBinaryStream file)
        {
            return ConvertFrom (file, ScriptTextMode.Filtered);
        }

        public Stream ConvertFrom (IBinaryStream file, string text_mode)
        {
            var data = ReadScriptData (file);
            bool filter = !string.Equals (text_mode, ScriptTextMode.Raw, StringComparison.OrdinalIgnoreCase);
            var lines = ExtractText (data, filter);
            var output = new MemoryStream();
            using (var writer = new StreamWriter (output, new UTF8Encoding (true), 0x400, true))
            {
                foreach (var line in lines)
                    writer.WriteLine (line.Text);
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
                var data = ReadScriptData (input);
                var script = new ScriptData();
                foreach (var line in ExtractText (data, true))
                    script.TextLines.Add (line);
                return script;
            }
        }

        static byte[] ReadScriptData (IBinaryStream file)
        {
            if (file.Length > int.MaxValue)
                throw new InvalidFormatException();
            file.Position = 0;
            var data = file.ReadBytes ((int)file.Length);
            if (!IsSaneHeader (data, data.Length))
                throw new InvalidFormatException();
            if (IsPacked (data))
                data = CpzOpener.UnpackPs2 (data);
            return data;
        }

        static bool IsPacked (byte[] data)
        {
            int header_size = LittleEndian.ToInt32 (data, 4);
            int packed_size = LittleEndian.ToInt32 (data, 0x24);
            int unpacked_size = LittleEndian.ToInt32 (data, 0x28);
            long packed_length = (long)header_size + packed_size;
            long unpacked_length = (long)header_size + unpacked_size;
            return packed_size > 0 && packed_length == data.Length && unpacked_length != data.Length;
        }

        static bool IsSaneHeader (byte[] data, long file_size)
        {
            if (data.Length < MinHeaderSize)
                return false;
            if (LittleEndian.ToUInt32 (data, 0) != 0x41325350)
                return false;
            int header_size = LittleEndian.ToInt32 (data, 4);
            int text_count = LittleEndian.ToInt32 (data, 0x10);
            int code_size = LittleEndian.ToInt32 (data, 0x14);
            int un_size = LittleEndian.ToInt32 (data, 0x18);
            int text_size = LittleEndian.ToInt32 (data, 0x1C);
            int packed_size = LittleEndian.ToInt32 (data, 0x24);
            int unpacked_size = LittleEndian.ToInt32 (data, 0x28);
            if (header_size < MinHeaderSize || header_size > file_size)
                return false;
            if (text_count < 0 || code_size < 0 || un_size < 0 || text_size < 0
                || packed_size < 0 || unpacked_size < 0)
                return false;
            long code_segment_size = (long)code_size + 4L * text_count + un_size;
            long unpacked_length = (long)header_size + unpacked_size;
            long packed_length = (long)header_size + packed_size;
            if (code_segment_size < 0 || unpacked_length < header_size || packed_length < header_size)
                return false;
            if (file_size == unpacked_length)
            {
                long text_offset = (long)header_size + code_segment_size;
                return text_offset >= header_size && text_offset + text_size <= file_size;
            }
            return packed_size > 0 && packed_length == file_size;
        }

        static IEnumerable<ScriptLine> ExtractText (byte[] data, bool filter)
        {
            var header = new Ps3Header (data);
            var lines = new List<ScriptLine>();
            int code_start = header.HeaderSize;
            int code_end = checked (code_start + header.CodeSegmentSize);
            int text_start = code_end;
            int text_end = checked (text_start + header.TextBlockSize);

            for (int pos = code_start; pos + 12 <= code_end; ++pos)
            {
                if (data[pos] != 0x01 || data[pos+1] != 0x02 || data[pos+2] != 0x20 || data[pos+3] != 0x01)
                    continue;
                if (data[pos+8] != 0x0F || data[pos+9] != 0x02 || data[pos+10] != 0x30 || data[pos+11] != 0x04)
                    continue;
                uint rva = LittleEndian.ToUInt32 (data, pos+4);
                if (rva >= header.TextBlockSize)
                    continue;
                int text_pos = text_start + (int)rva;
                int length = text_end - text_pos;
                int eos = Array.IndexOf<byte> (data, 0, text_pos, length);
                if (eos <= text_pos)
                    continue;
                var text = Encodings.cp932.GetString (data, text_pos, eos - text_pos);
                if (filter && IsFilteredString (text))
                    continue;
                lines.Add (new ScriptLine { Id = (uint)lines.Count, Text = text });
            }
            return lines;
        }

        static bool IsFilteredString (string text)
        {
            if (string.IsNullOrEmpty (text))
                return true;
            string[] resource_ext = { ".ogg", ".wav", ".mv2", ".pb3", ".pb2", ".ps3", ".ps2", ".cur", ".cmv", ".mgv" };
            foreach (var ext in resource_ext)
            {
                if (text.IndexOf (ext, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            if (IsEngineToken (text))
                return true;
            if (IsPunctuationOnly (text))
                return true;
            return false;
        }

        static bool IsEngineToken (string text)
        {
            bool has_letter = false;
            foreach (var c in text)
            {
                if (c < 0x20 || c > 0x7E || char.IsWhiteSpace (c))
                    return false;
                has_letter = has_letter || char.IsLetter (c);
            }
            if (!has_letter)
                return false;
            return text.IndexOfAny (new[] { '_', '/', '\\', ':', '.', '@', '$', '%' }) >= 0;
        }

        static bool IsPunctuationOnly (string text)
        {
            foreach (var c in text)
            {
                if (char.IsLetterOrDigit (c) && c != 'ー')
                    return false;
            }
            return true;
        }

        struct Ps3Header
        {
            public readonly int HeaderSize;
            public readonly int CodeSegmentSize;
            public readonly int TextBlockSize;

            public Ps3Header (byte[] data)
            {
                HeaderSize = LittleEndian.ToInt32 (data, 4);
                int text_count = LittleEndian.ToInt32 (data, 0x10);
                int code_size = LittleEndian.ToInt32 (data, 0x14);
                int un_size = LittleEndian.ToInt32 (data, 0x18);
                CodeSegmentSize = checked (code_size + 4 * text_count + un_size);
                TextBlockSize = LittleEndian.ToInt32 (data, 0x1C);
                long text_end = (long)HeaderSize + CodeSegmentSize + TextBlockSize;
                if (HeaderSize < MinHeaderSize || CodeSegmentSize < 0 || TextBlockSize < 0 || text_end > data.Length)
                    throw new InvalidFormatException();
            }
        }
    }
}
