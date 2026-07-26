//! \file       ScriptSPT.cs
//! \date       Mon May 18 2026
//! \brief      System-NNN encrypted SPT script extractor.
//

using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.Text;
using GameRes.Utility;

namespace GameRes.Formats.BlackCyc
{
    [Export(typeof(ArchiveFormat))]
    public class SptOpener : ArchiveFormat
    {
        public override string         Tag { get { return SystemNnnSpt.Tag; } }
        public override string Description { get { return "System-NNN script file"; } }
        public override uint     Signature { get { return 0; } }
        public override bool  IsHierarchic { get { return false; } }
        public override bool      CanWrite { get { return false; } }

        public SptOpener ()
        {
            Extensions = new[] { "spt" };
            ContainedFormats = new[] { "TXT" };
        }

        public override ArcFile TryOpen (ArcView file)
        {
            if (!file.Name.HasExtension (".spt"))
                return null;
            using (var input = file.CreateStream())
            {
                if (!SystemNnnSpt.IsScript (input))
                    return null;
            }
            string basename = Path.GetFileNameWithoutExtension (file.Name);
            var text_entry = Create<SptEntry> (basename+".filtered.txt");
            text_entry.Content = SptEntryContent.FilteredText;
            text_entry.Offset = 0;
            text_entry.Size = (uint)Math.Min (file.MaxOffset, uint.MaxValue);
            var raw_entry = Create<SptEntry> (basename+".raw.txt");
            raw_entry.Content = SptEntryContent.RawText;
            raw_entry.Offset = 0;
            raw_entry.Size = (uint)Math.Min (file.MaxOffset, uint.MaxValue);
            var dump_entry = Create<SptEntry> (basename+".dump.txt");
            dump_entry.Content = SptEntryContent.Dump;
            dump_entry.Offset = 0;
            dump_entry.Size = (uint)Math.Min (file.MaxOffset, uint.MaxValue);
            return new ArcFile (file, this, new Entry[] { text_entry, raw_entry, dump_entry });
        }

        public override Stream OpenEntry (ArcFile arc, Entry entry)
        {
            var spt_entry = entry as SptEntry;
            using (var input = arc.File.CreateStream())
            {
                if (null != spt_entry && spt_entry.Content == SptEntryContent.Dump)
                    return SystemNnnSpt.ConvertFrom (input, ScriptTextMode.Dump);
                if (null != spt_entry && spt_entry.Content == SptEntryContent.RawText)
                    return SystemNnnSpt.ConvertFrom (input, ScriptTextMode.Raw);
                return SystemNnnSpt.ConvertFrom (input, ScriptTextMode.Filtered);
            }
        }
    }

    internal enum SptEntryContent
    {
        FilteredText,
        RawText,
        Dump,
    }

    internal class SptEntry : Entry, IScriptTextOutputEntry
    {
        public SptEntryContent Content;

        public string TextMode
        {
            get
            {
                switch (Content)
                {
                case SptEntryContent.RawText:
                    return ScriptTextMode.Raw;
                case SptEntryContent.Dump:
                    return ScriptTextMode.Dump;
                default:
                    return ScriptTextMode.Filtered;
                }
            }
        }
    }

    [Export(typeof(ScriptFormat))]
    public class SptScriptFormat : GenericScriptFormat, IConfigurableScriptFormat
    {
        public override string         Tag { get { return SystemNnnSpt.Tag; } }
        public override string Description { get { return "System-NNN script"; } }
        public override uint     Signature { get { return 0; } }

        static readonly string[] s_text_modes = { ScriptTextMode.Filtered, ScriptTextMode.Raw, ScriptTextMode.Dump };

        public IEnumerable<string> TextModes { get { return s_text_modes; } }
        public string DefaultTextMode { get { return ScriptTextMode.Filtered; } }

        public SptScriptFormat ()
        {
            Extensions = new[] { "spt" };
        }

        public override bool IsScript (IBinaryStream file)
        {
            return SystemNnnSpt.IsScript (file);
        }

        public override Stream ConvertFrom (IBinaryStream file)
        {
            return ConvertFrom (file, ScriptTextMode.Filtered);
        }

        public Stream ConvertFrom (IBinaryStream file, string text_mode)
        {
            return SystemNnnSpt.ConvertFrom (file, text_mode);
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
                var data = SystemNnnSpt.ReadScriptData (input);
                foreach (var line in SystemNnnSpt.ExtractText (data, true))
                    script.TextLines.Add (line);
                return script;
            }
        }
    }

    internal static class SystemNnnSpt
    {
        public const string Tag = "SPT/SystemNNN";

        const int MinHeaderSize = 0x20;
        const int DataIdentify = 0x66660001;
        const int SystemCommandIdentify = 0x66660006;
        const int SystemCommandPrint = 0x22220001;
        const int SystemCommandLPrint = 0x22220002;
        const int SystemCommandAppend = 0x22220003;
        const int SystemCommandSelect = 0x22220006;
        const int DataHeader = 0x55550001;
        const int DataTable = 0x55550002;

        enum HeaderField
        {
            MessageCount = 4,
            MessageTable = 5,
            StringCount  = 6,
            StringTable  = 7,
        }

        public static bool IsScript (IBinaryStream file)
        {
            if (!file.Name.HasExtension (".spt"))
                return false;
            long position = file.Position;
            try
            {
                if (!IsSaneLength (file.Length))
                    return false;
                file.Position = 0;
                var header = file.ReadBytes (MinHeaderSize);
                return IsSaneEncryptedHeader (header, file.Length);
            }
            finally
            {
                if (file.CanSeek)
                    file.Position = position;
            }
        }

        public static Stream ConvertFrom (IBinaryStream file)
        {
            return ConvertFrom (file, ScriptTextMode.Filtered);
        }

        public static Stream ConvertFrom (IBinaryStream file, string text_mode)
        {
            var data = ReadScriptData (file);
            var output = new MemoryStream();
            using (var writer = new StreamWriter (output, new UTF8Encoding (true), 0x400, true))
            {
                if (string.Equals (text_mode, ScriptTextMode.Dump, StringComparison.OrdinalIgnoreCase))
                {
                    WriteInstructionDump (data, writer);
                }
                else
                {
                    bool filter = !string.Equals (text_mode, ScriptTextMode.Raw, StringComparison.OrdinalIgnoreCase);
                    foreach (var line in ExtractText (data, filter))
                        writer.WriteLine (line.Text);
                }
            }
            output.Position = 0;
            return output;
        }

        public static byte[] ReadScriptData (IBinaryStream file)
        {
            if (!file.Name.HasExtension (".spt") || !IsSaneLength (file.Length))
                throw new InvalidFormatException();
            file.Position = 0;
            var data = file.ReadBytes ((int)file.Length);
            if (data.Length != file.Length)
                throw new EndOfStreamException();
            Xor (data);
            if (!IsSaneDecryptedHeader (data, data.Length))
                throw new InvalidFormatException();
            return data;
        }

        public static IEnumerable<ScriptLine> ExtractText (byte[] data)
        {
            return ExtractText (data, true);
        }

        public static IEnumerable<ScriptLine> ExtractText (byte[] data, bool filter)
        {
            int message_count = ReadInt32 (data, (int)HeaderField.MessageCount);
            int message_table = ReadInt32 (data, (int)HeaderField.MessageTable);
            int string_count  = ReadInt32 (data, (int)HeaderField.StringCount);
            int string_table  = ReadInt32 (data, (int)HeaderField.StringTable);
            uint id = 0;
            foreach (var item in EnumerateItems (data))
            {
                if (IsPrintItem (item))
                {
                    string text = ReadPrintText (data, item, message_table, message_count);
                    if (null == text)
                        continue;
                    if (filter)
                    {
                        foreach (var line in ParseFileText (text))
                            yield return new ScriptLine { Id = id++, Text = line };
                    }
                    else if (!string.IsNullOrEmpty (text))
                    {
                        yield return new ScriptLine { Id = id++, Text = text };
                    }
                }
                else if (IsSelectItem (item))
                {
                    foreach (var offset in GetSelectStringOffsets (data, item, string_table, string_count))
                    {
                        string text = ReadString (data, offset);
                        if (!string.IsNullOrEmpty (text))
                            yield return new ScriptLine { Id = id++, Text = text };
                    }
                }
            }
        }

        static string ReadPrintText (byte[] data, SptItem item, int message_table, int message_count)
        {
            if (item.Length <= 3)
                return null;
            int message_id = ReadInt32 (data, item.Index + 3);
            if (message_id < 0 || message_id >= message_count)
                return null;
            int offset = ReadTableOffset (data, message_table, message_id);
            return ReadString (data, offset);
        }

        static IEnumerable<int> GetSelectStringOffsets (byte[] data, SptItem item, int string_table, int string_count)
        {
            if (item.Length <= 4)
                yield break;
            int count = ReadInt32 (data, item.Index + 3);
            if (count < 0)
                yield break;
            if (item.Length != count + 5)
            {
                int extra_count = ReadInt32 (data, item.Index + item.Length - 1);
                if (extra_count < 0 || count > int.MaxValue - extra_count)
                    yield break;
                count += extra_count;
            }
            int max_count = item.Length - 4;
            if (count > max_count)
                yield break;
            for (int i = 0; i < count; ++i)
            {
                int string_id = ReadInt32 (data, item.Index + 4 + i);
                if (string_id < 0 || string_id >= string_count)
                    continue;
                yield return ReadTableOffset (data, string_table, string_id);
            }
        }

        static int ReadTableOffset (byte[] data, int table_index, int item_id)
        {
            long index = (long)table_index + item_id;
            if (index < 0 || index >= data.Length / 4)
                return -1;
            long offset = 4L * ReadInt32 (data, (int)index);
            if (offset < 0 || offset >= data.Length)
                return -1;
            return (int)offset;
        }

        static void WriteInstructionDump (byte[] data, TextWriter writer)
        {
            int word_count = data.Length / 4;
            int header_length = ReadInt32 (data, 0);
            int message_count = ReadInt32 (data, (int)HeaderField.MessageCount);
            int message_table = ReadInt32 (data, (int)HeaderField.MessageTable);
            int string_count  = ReadInt32 (data, (int)HeaderField.StringCount);
            int string_table  = ReadInt32 (data, (int)HeaderField.StringTable);

            writer.WriteLine ("# System-NNN SPT decoded dump");
            writer.WriteLine ("# This is not reconstructed source; it is a direct dump of decoded tables and items.");
            writer.WriteLine ();
            writer.WriteLine ("[Header]");
            writer.WriteLine ("WordCount={0}", word_count);
            writer.WriteLine ("HeaderLength={0}", header_length);
            writer.WriteLine ("MessageCount={0}", message_count);
            writer.WriteLine ("MessageTableWord={0}", message_table);
            writer.WriteLine ("StringCount={0}", string_count);
            writer.WriteLine ("StringTableWord={0}", string_table);
            writer.WriteLine ();

            WriteTableDump (writer, "Messages", data, message_table, message_count);
            WriteTableDump (writer, "Strings", data, string_table, string_count);
            WriteItemDump (writer, data);
        }

        static void WriteTableDump (TextWriter writer, string title, byte[] data, int table_index, int count)
        {
            writer.WriteLine ("[{0}]", title);
            if (0 == count)
            {
                writer.WriteLine ("<empty>");
                writer.WriteLine ();
                return;
            }
            for (int i = 0; i < count; ++i)
            {
                int offset = ReadTableOffset (data, table_index, i);
                writer.WriteLine ("#{0:D4} @0x{1:X8}", i, offset);
                string text = ReadString (data, offset);
                if (null == text)
                    writer.WriteLine ("<invalid or empty>");
                else
                    writer.WriteLine (text);
                writer.WriteLine ();
            }
        }

        static void WriteItemDump (TextWriter writer, byte[] data)
        {
            writer.WriteLine ("[Items]");
            int index = 0;
            foreach (var item in EnumerateItems (data))
            {
                bool has_code = (item.Identify == SystemCommandIdentify || item.Identify == DataIdentify) && item.Length > 2;
                writer.Write ("#{0:D5} word={1} byte=0x{2:X8} length={3} identify=0x{4:X8}",
                              index++, item.Index, 4 * item.Index, item.Length, unchecked ((uint)item.Identify));
                if (has_code)
                    writer.Write (" code=0x{0:X8}", unchecked ((uint)item.Code));
                if (item.Code == DataTable)
                    writer.Write (" tableType=0x{0:X8}", unchecked ((uint)item.TableType));
                if (IsPrintItem (item) && item.Length > 3)
                    writer.Write (" messageId={0}", ReadInt32 (data, item.Index + 3));
                else if (IsSelectItem (item) && item.Length > 3)
                    writer.Write (" choiceCount={0}", ReadInt32 (data, item.Index + 3));
                writer.WriteLine ();
                WriteItemWords (writer, data, item);
            }
        }

        static void WriteItemWords (TextWriter writer, byte[] data, SptItem item)
        {
            int word_limit = Math.Min (item.Length, 16);
            writer.Write ("  words:");
            for (int i = 0; i < word_limit; ++i)
                writer.Write (" {0:X8}", unchecked ((uint)ReadInt32 (data, item.Index + i)));
            if (item.Length > word_limit)
                writer.Write (" ...");
            writer.WriteLine ();
        }

        static IEnumerable<string> ParseFileText (string text)
        {
            text = StripCommentLines (text).TrimEnd ('\r', '\n');
            int split = text.IndexOf ("\r\n", StringComparison.Ordinal);
            if (split > 0)
            {
                string name = text.Substring (0, split);
                string message = text.Substring (split + 2).Trim();
                if (IsCharacterName (name) && IsQuotedMessage (message))
                {
                    yield return name;
                    yield return message;
                    yield break;
                }
            }
            if (!string.IsNullOrEmpty (text))
                yield return text;
        }

        static string StripCommentLines (string text)
        {
            if (string.IsNullOrEmpty (text) || text.IndexOf ("//", StringComparison.Ordinal) < 0)
                return text;
            var builder = new StringBuilder (text.Length);
            using (var reader = new StringReader (text))
            {
                string line;
                bool first = true;
                while (null != (line = reader.ReadLine()))
                {
                    if (line.StartsWith ("//", StringComparison.Ordinal))
                        continue;
                    if (!first)
                        builder.Append ("\r\n");
                    builder.Append (line);
                    first = false;
                }
            }
            return builder.ToString();
        }

        static bool IsCharacterName (string text)
        {
            if (string.IsNullOrWhiteSpace (text))
                return false;
            char[] invalid = { '\u300c', '\u300e', '\uff08', '\uff09', '\u300f', '\u300d', '\r', '\n' };
            return text.IndexOfAny (invalid) < 0;
        }

        static bool IsQuotedMessage (string text)
        {
            if (string.IsNullOrEmpty (text))
                return false;
            char first = text[0];
            char last = text[text.Length - 1];
            return (first == '\u300c' || first == '\u300e' || first == '\uff08')
                && (last == '\uff09' || last == '\u300f' || last == '\u300d');
        }

        static string ReadString (byte[] data, int offset)
        {
            if (offset < 0 || offset >= data.Length)
                return null;
            int end = Array.IndexOf<byte> (data, 0, offset);
            if (end <= offset)
                return null;
            return Encodings.cp932.GetString (data, offset, end - offset);
        }

        static IEnumerable<SptItem> EnumerateItems (byte[] data)
        {
            int word_count = data.Length / 4;
            int index = 0;
            while (index + 1 < word_count)
            {
                int length = ReadInt32 (data, index);
                if (length <= 0 || length > word_count - index)
                    yield break;
                var item = new SptItem {
                    Index = index,
                    Length = length,
                    Identify = ReadInt32 (data, index + 1),
                };
                if ((item.Identify == SystemCommandIdentify || item.Identify == DataIdentify) && length > 2)
                {
                    item.Code = ReadInt32 (data, index + 2);
                    if (item.Code == DataTable && length > 3)
                        item.TableType = ReadInt32 (data, index + 3);
                }
                yield return item;
                index += length;
            }
        }

        static bool IsPrintItem (SptItem item)
        {
            if (item.Identify != SystemCommandIdentify)
                return false;
            return item.Code == SystemCommandPrint
                || item.Code == SystemCommandLPrint
                || item.Code == SystemCommandAppend;
        }

        static bool IsSelectItem (SptItem item)
        {
            return item.Identify == SystemCommandIdentify
                && item.Code == SystemCommandSelect;
        }

        static bool IsSaneLength (long length)
        {
            return length >= MinHeaderSize && length <= int.MaxValue && 0 == (length & 3);
        }

        static bool IsSaneEncryptedHeader (byte[] header, long length)
        {
            if (header.Length < MinHeaderSize || !IsSaneLength (length))
                return false;
            var decrypted = new byte[header.Length];
            Buffer.BlockCopy (header, 0, decrypted, 0, header.Length);
            Xor (decrypted);
            return IsSaneDecryptedHeader (decrypted, (int)length);
        }

        static bool IsSaneDecryptedHeader (byte[] data, long length)
        {
            int word_count = (int)(length / 4);
            int header_length = ReadInt32 (data, 0);
            if (header_length < 8 || header_length > word_count)
                return false;
            if (ReadInt32 (data, 1) != DataIdentify || ReadInt32 (data, 2) != DataHeader)
                return false;
            int message_count = ReadInt32 (data, (int)HeaderField.MessageCount);
            int message_table = ReadInt32 (data, (int)HeaderField.MessageTable);
            int string_count  = ReadInt32 (data, (int)HeaderField.StringCount);
            int string_table  = ReadInt32 (data, (int)HeaderField.StringTable);
            return IsSaneTable (message_table, message_count, word_count)
                && IsSaneTable (string_table, string_count, word_count);
        }

        static bool IsSaneTable (int index, int count, int word_count)
        {
            if (index < 0 || count < 0 || index > word_count)
                return false;
            return count <= word_count - index;
        }

        static int ReadInt32 (byte[] data, int index)
        {
            return LittleEndian.ToInt32 (data, 4 * index);
        }

        static void Xor (byte[] data)
        {
            for (int i = 0; i < data.Length; ++i)
                data[i] ^= 0xFF;
        }

        struct SptItem
        {
            public int Index;
            public int Length;
            public int Identify;
            public int Code;
            public int TableType;
        }
    }
}
