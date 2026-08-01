using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;

namespace GarbroCliTests
{
    public static class Xp3FixtureBuilder
    {
        static readonly byte[] Xp3Header = {
            (byte)'X', (byte)'P', (byte)'3', 0x0d, 0x0a, 0x20,
            0x0a, 0x1a, 0x8b, 0x67, 0x01,
        };

        public static void Create (
            string path, string[] names, byte[][] contents)
        {
            if (null == names || null == contents || names.Length != contents.Length)
                throw new ArgumentException ("XP3 names and contents must have equal lengths.");
            var entries = new List<FixtureEntry> (names.Length);
            for (int i = 0; i < names.Length; ++i)
            {
                entries.Add (new FixtureEntry {
                    Name = names[i],
                    Contents = contents[i] ?? new byte[0],
                });
            }
            WriteArchive (path, entries, null);
        }

        public static void CreateEmpty (string path, int count)
        {
            if (count < 1)
                throw new ArgumentOutOfRangeException ("count");
            var entries = new List<FixtureEntry> (count);
            for (int i = 0; i < count; ++i)
            {
                entries.Add (new FixtureEntry {
                    Name = string.Format (
                        CultureInfo.InvariantCulture,
                        "many/item-{0:D6}.txt", i),
                    Contents = new byte[0],
                });
            }
            WriteArchive (path, entries, null);
        }

        public static string CreateXorResolutionFingerprint (
            string cli_assembly, string arc_formats_assembly, uint key)
        {
            string assembly_directory = Path.GetDirectoryName (
                arc_formats_assembly);
            Assembly.LoadFrom (Path.Combine (
                assembly_directory, "GameRes.dll"));
            Assembly arc_formats = Assembly.LoadFrom (arc_formats_assembly);
            Assembly cli = Assembly.LoadFrom (cli_assembly);
            Type xor_type = arc_formats.GetType (
                "GameRes.Formats.KiriKiri.XorCrypt", true);
            object scheme = Activator.CreateInstance (
                xor_type, new object[] { key });
            Type descriptor_type = cli.GetType (
                "GARbro.Cli.ArchiveSchemeDescriptor", true);
            ConstructorInfo descriptor_constructor =
                descriptor_type.GetConstructor (
                    BindingFlags.Instance | BindingFlags.NonPublic,
                    null,
                    new Type[] {
                        typeof (string), typeof (string),
                        xor_type.BaseType, typeof (string), typeof (string),
                        typeof (bool), typeof (string),
                    },
                    null);
            if (null == descriptor_constructor)
            {
                foreach (ConstructorInfo candidate in
                         descriptor_type.GetConstructors (
                             BindingFlags.Instance | BindingFlags.NonPublic))
                {
                    if (7 == candidate.GetParameters().Length)
                    {
                        descriptor_constructor = candidate;
                        break;
                    }
                }
            }
            if (null == descriptor_constructor)
                throw new MissingMethodException (
                    descriptor_type.FullName, ".ctor");
            object descriptor = descriptor_constructor.Invoke (
                new object[] {
                    "fixture-same-name", "fixture-same-name", scheme,
                    "XorCrypt", "xor", false, "fixture",
                });

            Type resolution_type = cli.GetType (
                "GARbro.Cli.ArchiveSchemeResolution", true);
            ConstructorInfo resolution_constructor = null;
            foreach (ConstructorInfo candidate in
                     resolution_type.GetConstructors (
                         BindingFlags.Instance | BindingFlags.NonPublic))
            {
                if (10 == candidate.GetParameters().Length)
                {
                    resolution_constructor = candidate;
                    break;
                }
            }
            if (null == resolution_constructor)
                throw new MissingMethodException (
                    resolution_type.FullName, ".ctor");
            object resolution = resolution_constructor.Invoke (
                new object[] {
                    "fixture-same-name", descriptor, descriptor,
                    false, null, false, false, null, null, null,
                });
            return (string)resolution_type.GetProperty (
                "Fingerprint", BindingFlags.Instance | BindingFlags.Public)
                .GetValue (resolution, null);
        }

        public static bool VerifyInlineNamesCopy (string arc_formats_assembly)
        {
            string assembly_directory = Path.GetDirectoryName (
                arc_formats_assembly);
            Assembly.LoadFrom (Path.Combine (
                assembly_directory, "GameRes.dll"));
            Assembly arc_formats = Assembly.LoadFrom (arc_formats_assembly);
            Type scheme_type = arc_formats.GetType (
                "GameRes.Formats.KiriKiri.CxScheme", true);
            Type hx_type = arc_formats.GetType (
                "GameRes.Formats.KiriKiri.HxCrypt", true);
            object crypt = Activator.CreateInstance (
                hx_type, new object[] { Activator.CreateInstance (scheme_type) });
            MethodInfo clone_method = hx_type.GetMethod (
                "CloneWithInlineNames",
                BindingFlags.Instance | BindingFlags.NonPublic);
            MethodInfo copy_method = hx_type.GetMethod (
                "CopyInlineNamesTo",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (null == clone_method || null == copy_method)
                return false;

            const string hash = "0011223344556677";
            var inline = new Dictionary<string, string> (
                StringComparer.OrdinalIgnoreCase) {
                { hash, "inline-name" },
            };
            crypt = clone_method.Invoke (
                crypt, new object[] { inline });
            var seeds = new Dictionary<string, string> (
                StringComparer.OrdinalIgnoreCase) {
                { hash, "older-name" },
            };
            copy_method.Invoke (crypt, new object[] { seeds });
            string value;
            return seeds.TryGetValue (hash, out value)
                && "inline-name" == value;
        }

        public static void CreateHx (
            string path, string arc_formats_assembly,
            byte[] index_key, byte[] index_nonce,
            byte[] path_hash, byte[] name_hash,
            uint entry_id, long entry_key, byte[] encrypted_contents)
        {
            if (null == index_key || 32 != index_key.Length)
                throw new ArgumentException ("Hx index key must contain 32 bytes.");
            if (null == index_nonce || 16 != index_nonce.Length)
                throw new ArgumentException ("Hx index nonce must contain 16 bytes.");
            if (null == path_hash || 8 != path_hash.Length)
                throw new ArgumentException ("Hx path hash must contain 8 bytes.");
            if (null == name_hash || 32 != name_hash.Length)
                throw new ArgumentException ("Hx name hash must contain 32 bytes.");

            CreateHxInternal (
                path, arc_formats_assembly, index_key, index_nonce,
                path_hash, name_hash, entry_id, entry_key,
                encrypted_contents, 0);
        }

        public static void CreateHxWithFillers (
            string path, string arc_formats_assembly,
            byte[] index_key, byte[] index_nonce,
            byte[] path_hash, byte[] name_hash,
            uint entry_id, long entry_key, byte[] encrypted_contents,
            int filler_count)
        {
            if (filler_count < 1)
                throw new ArgumentOutOfRangeException ("filler_count");
            CreateHxInternal (
                path, arc_formats_assembly, index_key, index_nonce,
                path_hash, name_hash, entry_id, entry_key,
                encrypted_contents, filler_count);
        }

        static void CreateHxInternal (
            string path, string arc_formats_assembly,
            byte[] index_key, byte[] index_nonce,
            byte[] path_hash, byte[] name_hash,
            uint entry_id, long entry_key, byte[] encrypted_contents,
            int filler_count)
        {
            if (null == index_key || 32 != index_key.Length)
                throw new ArgumentException ("Hx index key must contain 32 bytes.");
            if (null == index_nonce || 16 != index_nonce.Length)
                throw new ArgumentException ("Hx index nonce must contain 16 bytes.");
            if (null == path_hash || 8 != path_hash.Length)
                throw new ArgumentException ("Hx path hash must contain 8 bytes.");
            if (null == name_hash || 32 != name_hash.Length)
                throw new ArgumentException ("Hx name hash must contain 32 bytes.");

            byte[] serialized = SerializeHxIndex (
                path_hash, name_hash, entry_id, entry_key);
            byte[] compressed = CompressZlib (serialized);
            var plain_index = new byte[4 + compressed.Length];
            Buffer.BlockCopy (compressed, 0, plain_index, 4, compressed.Length);
            byte[] encrypted_index = TransformHxIndex (
                arc_formats_assembly, index_key, index_nonce, plain_index);
            var hx_record = new byte[16 + encrypted_index.Length];
            Buffer.BlockCopy (
                encrypted_index, 0, hx_record, 16, encrypted_index.Length);

            var entries = new List<FixtureEntry> (1 + filler_count);
            entries.Add (new FixtureEntry {
                Name = GetUnicodeName (entry_id),
                Contents = encrypted_contents ?? new byte[0],
                IsEncrypted = true,
            });
            for (int i = 0; i < filler_count; ++i)
            {
                entries.Add (new FixtureEntry {
                    Name = string.Format (
                        CultureInfo.InvariantCulture,
                        "filler/item-{0:D4}.txt", i),
                    Contents = Encoding.UTF8.GetBytes (
                        "fixture candidate pixel.png images/"),
                    IsEncrypted = true,
                });
            }
            WriteArchive (path, entries, hx_record);
        }

        public static void WriteCxDump (
            string directory, string archive_name,
            byte[] index_key, byte[] index_nonce,
            ulong filter_key, uint split_mask, uint split_position,
            int random_type, byte[] even_order, byte[] odd_order,
            byte[] prolog_order, uint[] control_block)
        {
            if (null == control_block || 0x400 != control_block.Length)
                throw new ArgumentException ("Cx control block must contain 1024 values.");
            Directory.CreateDirectory (directory);
            string log = string.Format (
                CultureInfo.InvariantCulture,
                "Parsing archive: {0}\r\n"
                + "Index Key: {1}\r\n"
                + "Index Nonce: {2}\r\n"
                + "Filter Key: 0x{3:X16}\r\n"
                + "Split Pos Mask: 0x{4:X8}\r\n"
                + "Split Pos: 0x{5:X8}\r\n"
                + "Random Type: {6}\r\n"
                + "Cxdec Order (8): {7}\r\n"
                + "Cxdec Order (6): {8}\r\n"
                + "Cxdec Order (3): {9}\r\n",
                archive_name,
                ToHex (index_key), ToHex (index_nonce), filter_key,
                split_mask, split_position, random_type,
                JoinOrder (even_order), JoinOrder (odd_order),
                JoinOrder (prolog_order));
            File.WriteAllText (
                Path.Combine (directory, "KrkrDump-fixture.log"),
                log, new UTF8Encoding (false));
            using (var output = new BinaryWriter (File.Create (
                Path.Combine (directory, "CxdecTable.bin"))))
            {
                foreach (uint value in control_block)
                    output.Write (~value);
            }
        }

        static string JoinOrder (byte[] value)
        {
            if (null == value)
                return string.Empty;
            var result = new string[value.Length];
            for (int i = 0; i < value.Length; ++i)
                result[i] = value[i].ToString (CultureInfo.InvariantCulture);
            return string.Join (",", result);
        }

        static string ToHex (byte[] value)
        {
            if (null == value)
                return string.Empty;
            var output = new StringBuilder (value.Length * 2);
            foreach (byte item in value)
                output.Append (item.ToString ("X2", CultureInfo.InvariantCulture));
            return output.ToString();
        }

        static void WriteArchive (
            string path, IEnumerable<FixtureEntry> source_entries,
            byte[] hx_record)
        {
            string directory = Path.GetDirectoryName (Path.GetFullPath (path));
            Directory.CreateDirectory (directory);
            var entries = new List<FixtureEntry> (source_entries);
            using (var file = new FileStream (
                path, FileMode.Create, FileAccess.Write, FileShare.Read))
            using (var writer = new BinaryWriter (file, Encoding.UTF8, true))
            {
                writer.Write (Xp3Header);
                long index_offset_position = writer.BaseStream.Position;
                writer.Write ((long)0);

                foreach (FixtureEntry entry in entries)
                {
                    entry.Offset = writer.BaseStream.Position;
                    writer.Write (entry.Contents);
                }
                long hx_offset = 0;
                if (null != hx_record)
                {
                    hx_offset = writer.BaseStream.Position;
                    writer.Write (hx_record);
                }

                long index_offset = writer.BaseStream.Position;
                writer.BaseStream.Position = index_offset_position;
                writer.Write (index_offset);
                writer.BaseStream.Position = index_offset;

                using (var index_stream = new MemoryStream())
                using (var index = new BinaryWriter (
                    index_stream, Encoding.Unicode, true))
                {
                    if (null != hx_record)
                    {
                        index.Write ((uint)0x34767848); // Hxv4
                        index.Write ((long)14);
                        index.Write (hx_offset);
                        index.Write ((uint)hx_record.Length);
                        index.Write ((ushort)0);
                    }
                    foreach (FixtureEntry entry in entries)
                        WriteFileRecord (index, entry);
                    index.Flush();
                    writer.Write ((byte)0); // uncompressed index
                    writer.Write (index_stream.Length);
                    index_stream.Position = 0;
                    index_stream.CopyTo (writer.BaseStream);
                }
            }
        }

        static void WriteFileRecord (BinaryWriter writer, FixtureEntry entry)
        {
            byte[] name = Encoding.Unicode.GetBytes (entry.Name);
            long info_size = 4 + 8 + 8 + 2 + name.Length;
            const long segment_size = 0x1c;
            const long adler_size = 4;
            long file_size = (12 + info_size)
                           + (12 + segment_size)
                           + (12 + adler_size);

            writer.Write ((uint)0x656c6946); // File
            writer.Write (file_size);

            writer.Write ((uint)0x6f666e69); // info
            writer.Write (info_size);
            writer.Write (entry.IsEncrypted ? 0x80000000u : 0u);
            writer.Write ((long)entry.Contents.Length);
            writer.Write ((long)entry.Contents.Length);
            writer.Write ((short)(name.Length / 2));
            writer.Write (name);

            writer.Write ((uint)0x6d676573); // segm
            writer.Write (segment_size);
            writer.Write (0);
            writer.Write (entry.Offset);
            writer.Write ((long)entry.Contents.Length);
            writer.Write ((long)entry.Contents.Length);

            writer.Write ((uint)0x726c6461); // adlr
            writer.Write (adler_size);
            writer.Write (Adler32 (entry.Contents));
        }

        static byte[] SerializeHxIndex (
            byte[] path_hash, byte[] name_hash,
            uint entry_id, long entry_key)
        {
            using (var output = new MemoryStream())
            using (var writer = new BinaryWriter (output, Encoding.UTF8, true))
            {
                WriteArrayHeader (writer, 2);
                WriteByteArray (writer, path_hash);
                WriteArrayHeader (writer, 2);
                WriteByteArray (writer, name_hash);
                WriteArrayHeader (writer, 2);
                WriteInt64 (writer, entry_id);
                WriteInt64 (writer, entry_key);
                writer.Flush();
                return output.ToArray();
            }
        }

        static void WriteArrayHeader (BinaryWriter writer, int count)
        {
            writer.Write ((byte)0x81);
            WriteBigEndian (writer, count);
        }

        static void WriteByteArray (BinaryWriter writer, byte[] value)
        {
            writer.Write ((byte)0x03);
            WriteBigEndian (writer, value.Length);
            writer.Write (value);
        }

        static void WriteInt64 (BinaryWriter writer, long value)
        {
            writer.Write ((byte)0x04);
            byte[] bytes = BitConverter.GetBytes (value);
            if (BitConverter.IsLittleEndian)
                Array.Reverse (bytes);
            writer.Write (bytes);
        }

        static void WriteBigEndian (BinaryWriter writer, int value)
        {
            byte[] bytes = BitConverter.GetBytes (value);
            if (BitConverter.IsLittleEndian)
                Array.Reverse (bytes);
            writer.Write (bytes);
        }

        static byte[] CompressZlib (byte[] value)
        {
            using (var output = new MemoryStream())
            {
                output.WriteByte (0x78);
                output.WriteByte (0x9c);
                using (var deflate = new DeflateStream (
                    output, CompressionMode.Compress, true))
                {
                    deflate.Write (value, 0, value.Length);
                }
                uint checksum = Adler32 (value);
                output.WriteByte ((byte)(checksum >> 24));
                output.WriteByte ((byte)(checksum >> 16));
                output.WriteByte ((byte)(checksum >> 8));
                output.WriteByte ((byte)checksum);
                return output.ToArray();
            }
        }

        static byte[] TransformHxIndex (
            string assembly_path, byte[] key, byte[] nonce, byte[] input)
        {
            Assembly assembly = Assembly.LoadFrom (assembly_path);
            Type type = assembly.GetType (
                "GameRes.Formats.KiriKiri.HxChachaDecryptor", true);
            object transform = Activator.CreateInstance (
                type,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, new object[] { key, nonce, new uint[] { 1, 0 } },
                CultureInfo.InvariantCulture);
            MethodInfo decrypt = type.GetMethod (
                "Decrypt", BindingFlags.Instance | BindingFlags.Public);
            var output = new byte[input.Length];
            decrypt.Invoke (transform, new object[] {
                input, 0, output, 0, input.Length,
            });
            return output;
        }

        static string GetUnicodeName (uint hash)
        {
            var buffer = new char[4];
            int length = 0;
            do
            {
                buffer[length++] = (char)((hash & 0x3fff) + 0x5000);
                hash >>= 14;
            }
            while (0 != hash);
            return new string (buffer, 0, length);
        }

        static uint Adler32 (byte[] value)
        {
            const uint modulo = 65521;
            uint a = 1;
            uint b = 0;
            foreach (byte item in value)
            {
                a = (a + item) % modulo;
                b = (b + a) % modulo;
            }
            return (b << 16) | a;
        }

        sealed class FixtureEntry
        {
            public string Name;
            public byte[] Contents;
            public bool IsEncrypted;
            public long Offset;
        }
    }
}
