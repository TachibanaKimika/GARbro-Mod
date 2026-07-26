using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using GameRes;
using GameRes.Compression;
using GameRes.Formats.Strings;

namespace GameRes.Formats.KiriKiri
{
    internal class KrkrDumpImportResult
    {
        public bool Success { get; set; }
        public string SchemeName { get; set; }
        public string Message { get; set; }
    }

    internal static class KrkrDumpResultImporter
    {
        static readonly byte[] Xp3Header = {
            (byte)'X', (byte)'P', (byte)'3', 0x0d, 0x0a, 0x20, 0x0a, 0x1a, 0x8b, 0x67, 0x01
        };

        static readonly Regex HexValueRe = new Regex (@"\b(?<name>Index Key|Index Nonce):\s*(?<value>[0-9A-Fa-f]+)",
            RegexOptions.Compiled);
        static readonly Regex HexNumberRe = new Regex (@"\b(?<name>Filter Key|Split Pos Mask|Split Pos):\s*0x(?<value>[0-9A-Fa-f]+)",
            RegexOptions.Compiled);
        static readonly Regex DecimalRe = new Regex (@"\bRandom Type:\s*(?<value>\d+)", RegexOptions.Compiled);
        static readonly Regex ArchiveRe = new Regex (@"Parsing archive:\s*(?<name>.+)$", RegexOptions.Compiled);
        static readonly Regex OrderRe = new Regex (@"Cxdec Order \((?<size>[368])\):\s*(?<values>[0-9,\s-]+)",
            RegexOptions.Compiled);
        static readonly Regex NameHashRe = new Regex (
            @"\b(?<kind>PathHash|NameHash):\s+""(?<name>.*?)""\s+""(?<salt>.*?)""\s+""(?<hash>[0-9A-Fa-f]+)""",
            RegexOptions.Compiled);

        public static KrkrDumpImportResult ImportNamesFile (ResourceParameterCommandResult result, string source_file,
                                                             string base_scheme_name, bool include_same_directory)
        {
            if (null == result || !result.Success)
                return new KrkrDumpImportResult { Success = false, Message = result != null ? result.Message : Text ("HxNamesNoResult") };

            string names_file = GetMetadata (result, "NamesFile");
            if (string.IsNullOrEmpty (names_file) || !File.Exists (names_file))
                return new KrkrDumpImportResult { Success = false, Message = Text ("HxNamesFileNotFound") };

            ICrypt base_algorithm;
            if (string.IsNullOrEmpty (base_scheme_name)
                || !Xp3Opener.KnownSchemes.TryGetValue (base_scheme_name, out base_algorithm))
            {
                return new KrkrDumpImportResult { Success = false, Message = Text ("HxNamesNeedHxScheme") };
            }
            var base_crypt = base_algorithm as HxCrypt;
            if (null == base_crypt)
                return new KrkrDumpImportResult { Success = false, Message = Text ("HxNamesNeedHxScheme") };

            Dictionary<string, string> names;
            string validation_error;
            if (!TryReadNamesFile (names_file, out names, out validation_error))
                return new KrkrDumpImportResult { Success = false, Message = validation_error };

            HxIndexHashSet index_hashes;
            try
            {
                var index = ReadHxIndex (source_file);
                index_hashes = null != index
                    ? base_crypt.ReadIndexHashes (Path.GetFileName (source_file), index)
                    : null;
            }
            catch (Exception X)
            {
                Trace.WriteLine (string.Format ("Failed to inspect HxNames coverage for '{0}': {1}",
                                                source_file, X.Message), "[HxNames]");
                index_hashes = null;
            }
            if (null == index_hashes)
                return new KrkrDumpImportResult { Success = false, Message = Text ("HxNamesIndexReadFailed") };

            int path_matches = names.Keys.Count (x => x.Length == 16 && index_hashes.PathHashes.Contains (x));
            int name_matches = names.Keys.Count (x => x.Length == 64 && index_hashes.NameHashes.Contains (x));
            if (0 == path_matches + name_matches)
                return new KrkrDumpImportResult { Success = false, Message = Text ("HxNamesNoMatches") };

            names_file = Path.GetFullPath (names_file);
            var crypt = base_crypt.CloneWithAdditionalNamesFile (names_file);
            var scheme_name = string.Format ("{0}{1} / {2}", Xp3Opener.HxNamesSchemePrefix,
                                             Xp3Opener.GetSchemeDisplayName (base_scheme_name),
                                             Path.GetFileNameWithoutExtension (source_file));
            Xp3Opener.KnownSchemes[scheme_name] = crypt;
            Xp3Opener.SetTransientScheme (scheme_name, source_file, include_same_directory);
            Trace.WriteLine (string.Format (
                "Imported HxNames table. file='{0}', entries={1}, archive='{2}', pathMatches={3}/{4}, nameMatches={5}/{6}, sameDirectory={7}",
                names_file, names.Count, source_file, path_matches, index_hashes.PathHashes.Count,
                name_matches, index_hashes.NameHashes.Count, include_same_directory), "[HxNames]");

            var message_name = include_same_directory ? "HxNamesImportedSameDirectory" : "HxNamesImported";
            return new KrkrDumpImportResult
            {
                Success = true,
                SchemeName = scheme_name,
                Message = string.Format (Text (message_name), names.Count, path_matches,
                                         index_hashes.PathHashes.Count, name_matches, index_hashes.NameHashes.Count),
            };
        }

        public static KrkrDumpImportResult Import (ResourceParameterCommandResult result, string source_file,
                                                    bool include_same_directory = false,
                                                    Action<ResourceProgressInfo> progress_reporter = null)
        {
            if (null == result || !result.Success)
                return new KrkrDumpImportResult { Success = false, Message = result != null ? result.Message : Text ("KrkrDumpNoResult") };

            var data = ReadDumpData (result);
            if (data.LogLines.Count == 0)
            {
                Trace.WriteLine (string.Format ("No KrkrDump log lines found. output='{0}', log='{1}'",
                                                result.OutputDirectory, result.LogFileName), "[KrkrDump]");
                return new KrkrDumpImportResult { Success = false, Message = Text ("KrkrDumpLogNotFound") };
            }

            ParseLog (data);
            if (null == data.ControlBlock)
                return new KrkrDumpImportResult { Success = false, Message = Text ("KrkrDumpCxTableNotFound") };
            if (null == data.EvenOrder || null == data.OddOrder || null == data.PrologOrder)
                return new KrkrDumpImportResult { Success = false, Message = Text ("KrkrDumpCxOrderNotFound") };
            if (0 == data.IndexKeys.Count && (null == data.IndexKey1 || null == data.IndexKey2))
                return new KrkrDumpImportResult { Success = false, Message = Text ("KrkrDumpIndexKeyNotFound") };

            var scheme = new CxScheme
            {
                Mask = data.SplitPosMask,
                Offset = data.SplitPos,
                PrologOrder = data.PrologOrder,
                OddBranchOrder = data.OddOrder,
                EvenBranchOrder = data.EvenOrder,
                ControlBlock = data.ControlBlock,
            };
            var crypt = new HxCrypt (scheme)
            {
                FilterKey = data.FilterKey,
                RandomType = data.RandomType,
                IndexKey1 = data.IndexKey1,
                IndexKey2 = data.IndexKey2,
                IndexKeyDict = data.IndexKeys.Count > 0 ? data.IndexKeys : null,
            };

            var logged_names = new Dictionary<string, string> (data.Names, StringComparer.OrdinalIgnoreCase);
            FilterNamesToSourceArchive (source_file, data, crypt);
            data.NamesFile = WriteNamesFile (result, data);
            crypt.NamesFile = data.NamesFile;
            TraceDumpData (data);

            var scheme_name = CreateSchemeName (result, source_file);
            Xp3Opener.KnownSchemes[scheme_name] = crypt;
            Xp3Opener.SetTransientScheme (scheme_name, source_file, include_same_directory);
            var import = new KrkrDumpImportResult
            {
                Success = true,
                SchemeName = scheme_name,
                Message = string.Format (Text ("KrkrDumpSchemeImported"), scheme_name),
            };

            // Make the last generated result available immediately while a fresh
            // scenario scan runs in the background. HxCrypt reads this path when
            // opening an index, so the atomic replacement performed by the
            // generator also refreshes subsequent archive opens.
            foreach (var existing_names_file in FindAutomaticNamesFiles (result, source_file))
            {
                var existing_result = new ResourceParameterCommandResult { Success = true };
                existing_result.Metadata["NamesFile"] = existing_names_file;
                var existing_import = ImportNamesFile (
                    existing_result, source_file, scheme_name, include_same_directory);
                if (existing_import.Success)
                {
                    Trace.WriteLine (string.Format (
                        "Applied existing HxNames result before live regeneration. file='{0}', archive='{1}'",
                        existing_names_file, source_file), "[HxNames]");
                    break;
                }
                Trace.WriteLine (string.Format (
                    "Existing HxNames result was not applicable before regeneration. file='{0}', reason='{1}'",
                    existing_names_file, existing_import.Message), "[HxNames]");
            }

            var generated_names_file = GetAutomaticNamesCacheFile (result, source_file);
            HxNameGenerationResult generation = null;
            KrkrDumpImportResult generated_import_failure = null;
            if (!string.IsNullOrEmpty (generated_names_file))
            {
                generation = HxNameGenerator.Generate (
                    source_file, crypt, logged_names, generated_names_file, progress_reporter);
                if (generation.Success)
                {
                    var generated_result = new ResourceParameterCommandResult { Success = true };
                    generated_result.Metadata["NamesFile"] = generated_names_file;
                    var generated_import = ImportNamesFile (
                        generated_result, source_file, scheme_name, include_same_directory);
                    if (generated_import.Success)
                    {
                        generated_import.Message = string.Format (Text ("HxNamesGenerated"),
                            generation.ScenarioCount, generation.CandidateCount,
                            generation.PathMatches, generation.NameMatches,
                            generated_import.Message);
                        return generated_import;
                    }
                    generated_import_failure = generated_import;
                    Trace.WriteLine (string.Format (
                        "Generated HxNames did not match the selected archive. file='{0}', reason='{1}'",
                        generated_names_file, generated_import.Message), "[HxNames]");
                }
            }

            var automatic_names_files = FindAutomaticNamesFiles (result, source_file).ToList();
            if (generation != null && generation.Success)
            {
                automatic_names_files.RemoveAll (x => string.Equals (
                    x, generated_names_file, StringComparison.OrdinalIgnoreCase));
            }
            if (0 == automatic_names_files.Count)
            {
                if (generation != null && !generation.Success)
                {
                    import.Message = string.Format (Text ("HxNamesGenerationFailed"),
                                                    import.Message, generation.Error);
                }
                else if (null != generated_import_failure)
                {
                    import.Message = string.Format (Text ("HxNamesAutoImportFailed"),
                                                    import.Message, generated_import_failure.Message);
                }
                return import;
            }

            KrkrDumpImportResult last_failure = generated_import_failure;
            foreach (var automatic_names_file in automatic_names_files)
            {
                var names_result = new ResourceParameterCommandResult { Success = true };
                names_result.Metadata["NamesFile"] = automatic_names_file;
                var names_import = ImportNamesFile (names_result, source_file, scheme_name, include_same_directory);
                if (names_import.Success)
                {
                    names_import.Message = string.Format (Text ("HxNamesAutoImported"), names_import.Message);
                    Trace.WriteLine (string.Format ("Automatically imported HxNames cache '{0}'.",
                                                    automatic_names_file), "[HxNames]");
                    return names_import;
                }
                last_failure = names_import;
                Trace.WriteLine (string.Format ("Automatic HxNames candidate rejected. file='{0}', reason='{1}'",
                                                automatic_names_file, names_import.Message), "[HxNames]");
            }
            if (null != last_failure)
                import.Message = string.Format (Text ("HxNamesAutoImportFailed"), import.Message, last_failure.Message);
            if (generation != null && !generation.Success)
                import.Message = string.Format (Text ("HxNamesGenerationFailed"), import.Message, generation.Error);
            return import;
        }

        static IEnumerable<string> FindAutomaticNamesFiles (ResourceParameterCommandResult result, string source_file)
        {
            var candidates = new List<string>();
            AddCandidate (candidates, GetMetadata (result, "HxNamesFile"));

            AddCandidate (candidates, GetAutomaticNamesCacheFile (result, source_file));

            if (!string.IsNullOrEmpty (source_file))
            {
                var source_directory = Path.GetDirectoryName (source_file);
                if (!string.IsNullOrEmpty (source_directory))
                    AddCandidate (candidates, Path.Combine (source_directory, "HxNames.lst"));
            }
            var game_directory_from_result = GetMetadata (result, "GameDirectory");
            if (!string.IsNullOrEmpty (game_directory_from_result))
                AddCandidate (candidates, Path.Combine (game_directory_from_result, "HxNames.lst"));

            return candidates.Where (File.Exists);
        }

        static string GetAutomaticNamesCacheFile (ResourceParameterCommandResult result, string source_file)
        {
            var game_executable = GetMetadata (result, "GameExecutable");
            var game_id = Path.GetFileNameWithoutExtension (game_executable);
            if (string.IsNullOrWhiteSpace (game_id) && !string.IsNullOrEmpty (source_file))
            {
                var game_directory = Path.GetDirectoryName (source_file);
                game_id = !string.IsNullOrEmpty (game_directory)
                    ? Path.GetFileName (game_directory)
                    : null;
            }
            if (string.IsNullOrWhiteSpace (game_id))
                return null;
            foreach (char c in Path.GetInvalidFileNameChars())
                game_id = game_id.Replace (c, '_');
            var local_app_data = Environment.GetFolderPath (Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine (local_app_data, "Onachi", "Onachi-GARbro",
                                 "HxNames", game_id, "HxNames.lst");
        }

        static void AddCandidate (ICollection<string> candidates, string path)
        {
            if (string.IsNullOrWhiteSpace (path))
                return;
            try
            {
                path = Path.GetFullPath (path);
            }
            catch
            {
                return;
            }
            if (!candidates.Contains (path, StringComparer.OrdinalIgnoreCase))
                candidates.Add (path);
        }

        static bool TryReadNamesFile (string names_file, out Dictionary<string, string> names, out string error)
        {
            names = new Dictionary<string, string> (StringComparer.OrdinalIgnoreCase);
            error = null;
            try
            {
                using (var input = new StreamReader (names_file, Encoding.UTF8))
                {
                    string line;
                    int line_number = 0;
                    while ((line = input.ReadLine()) != null)
                    {
                        ++line_number;
                        if (string.IsNullOrWhiteSpace (line) || line.StartsWith ("#") || line.StartsWith (";"))
                            continue;
                        int separator = line.IndexOf (':');
                        var hash = separator > 0 ? line.Substring (0, separator).Trim() : string.Empty;
                        var name = separator >= 0 ? line.Substring (separator+1) : string.Empty;
                        if ((hash.Length != 16 && hash.Length != 64) || !IsHexString (hash)
                            || (hash.Length == 64 && 0 == name.Length))
                        {
                            error = string.Format (Text ("HxNamesInvalidLine"), line_number);
                            return false;
                        }
                        names[hash.ToUpperInvariant()] = name;
                    }
                }
            }
            catch (Exception X)
            {
                error = X.Message;
                return false;
            }
            if (0 == names.Count)
            {
                error = Text ("HxNamesEmpty");
                return false;
            }
            return true;
        }

        static bool IsHexString (string value)
        {
            foreach (char c in value)
            {
                if (!((c >= '0' && c <= '9') || (c >= 'A' && c <= 'F') || (c >= 'a' && c <= 'f')))
                    return false;
            }
            return value.Length > 0;
        }

        static string CreateSchemeName (ResourceParameterCommandResult result, string source_file)
        {
            string exe_name = GetMetadata (result, "GameExecutable");
            exe_name = string.IsNullOrEmpty (exe_name) ? "Game" : Path.GetFileNameWithoutExtension (exe_name);
            string arc_name = string.IsNullOrEmpty (source_file) ? "XP3" : Path.GetFileNameWithoutExtension (source_file);
            return string.Format ("{0}{1} / {2}", Xp3Opener.KrkrDumpSchemePrefix, exe_name, arc_name);
        }

        static KrkrDumpData ReadDumpData (ResourceParameterCommandResult result)
        {
            var data = new KrkrDumpData();
            data.OutputDirectory = result.OutputDirectory;
            data.SourceArchive = GetMetadata (result, "SourceArchive");
            data.GameDirectory = GetMetadata (result, "GameDirectory");

            var log_files = new List<string>();
            if (!string.IsNullOrEmpty (result.LogFileName))
                log_files.Add (result.LogFileName);
            AddLogs (log_files, result.OutputDirectory);
            AddLogs (log_files, data.GameDirectory);
            foreach (var log in log_files.Where (File.Exists).Distinct (StringComparer.OrdinalIgnoreCase))
            {
                data.LogFiles.Add (log);
                data.LogLines.AddRange (File.ReadAllLines (log, Encoding.UTF8));
            }

            data.ControlBlock = ReadControlBlock (FindResultFile (result, "CxdecTable.bin"));
            ReadOrderFile (FindResultFile (result, "CxdecOrder.bin"), data);
            return data;
        }

        static void TraceDumpData (KrkrDumpData data)
        {
            Trace.WriteLine (string.Format (
                "Parsed KrkrDump output. logs={0}, lines={1}, indexKeys={2}, pathHashes={3}, nameHashes={4}, namesFile='{5}', controlBlock={6}, orders={7}/{8}/{9}",
                data.LogFiles.Count,
                data.LogLines.Count,
                data.IndexKeys.Count,
                data.PathHashCount,
                data.NameHashCount,
                data.NamesFile ?? "",
                null != data.ControlBlock ? data.ControlBlock.Length.ToString (CultureInfo.InvariantCulture) : "missing",
                null != data.EvenOrder ? data.EvenOrder.Length.ToString (CultureInfo.InvariantCulture) : "missing",
                null != data.OddOrder ? data.OddOrder.Length.ToString (CultureInfo.InvariantCulture) : "missing",
                null != data.PrologOrder ? data.PrologOrder.Length.ToString (CultureInfo.InvariantCulture) : "missing"),
                "[KrkrDump]");
            foreach (var log in data.LogFiles)
                Trace.WriteLine (string.Format ("Input log: {0}", log), "[KrkrDump]");
        }

        static void AddLogs (List<string> logs, string directory)
        {
            if (string.IsNullOrEmpty (directory) || !Directory.Exists (directory))
                return;
            logs.AddRange (Directory.GetFiles (directory, "KrkrDump-*.log"));
        }

        static string FindResultFile (ResourceParameterCommandResult result, string name)
        {
            var candidates = new[]
            {
                result.OutputDirectory,
                GetMetadata (result, "GameDirectory"),
                GetMetadata (result, "RuntimeDirectory"),
                !string.IsNullOrEmpty (result.LogFileName) ? Path.GetDirectoryName (result.LogFileName) : null,
            };
            foreach (var dir in candidates)
            {
                if (string.IsNullOrEmpty (dir))
                    continue;
                var path = Path.Combine (dir, name);
                if (File.Exists (path))
                    return path;
            }
            return null;
        }

        static string GetMetadata (ResourceParameterCommandResult result, string key)
        {
            string value;
            if (result.Metadata.TryGetValue (key, out value))
                return value;
            return null;
        }

        static string Text (string name)
        {
            return arcStrings.ResourceManager.GetString (name) ?? name;
        }

        static void ParseLog (KrkrDumpData data)
        {
            string current_archive = Path.GetFileName (data.SourceArchive);
            byte[] pending_key = null;
            foreach (var line in data.LogLines)
            {
                var archive_match = ArchiveRe.Match (line);
                if (archive_match.Success)
                {
                    current_archive = archive_match.Groups["name"].Value.Trim();
                    continue;
                }

                var hex_match = HexValueRe.Match (line);
                if (hex_match.Success)
                {
                    var bytes = ParseHexBytes (hex_match.Groups["value"].Value);
                    if ("Index Key" == hex_match.Groups["name"].Value)
                    {
                        pending_key = bytes;
                        data.IndexKey1 = bytes;
                    }
                    else
                    {
                        data.IndexKey2 = bytes;
                        if (pending_key != null && !string.IsNullOrEmpty (current_archive))
                        {
                            data.IndexKeys[current_archive] = new HxIndexKey { Key1 = pending_key, Key2 = bytes };
                            pending_key = null;
                        }
                    }
                    continue;
                }

                var hex_number = HexNumberRe.Match (line);
                if (hex_number.Success)
                {
                    var value = ulong.Parse (hex_number.Groups["value"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    switch (hex_number.Groups["name"].Value)
                    {
                    case "Filter Key": data.FilterKey = value; break;
                    case "Split Pos Mask": data.SplitPosMask = (uint)value; break;
                    case "Split Pos": data.SplitPos = (uint)value; break;
                    }
                    continue;
                }

                var decimal_match = DecimalRe.Match (line);
                if (decimal_match.Success)
                {
                    data.RandomType = int.Parse (decimal_match.Groups["value"].Value, CultureInfo.InvariantCulture);
                    continue;
                }

                var order_match = OrderRe.Match (line);
                if (order_match.Success)
                {
                    var order = ParseOrder (order_match.Groups["values"].Value);
                    switch (order_match.Groups["size"].Value)
                    {
                    case "8": data.EvenOrder = order; break;
                    case "6": data.OddOrder = order; break;
                    case "3": data.PrologOrder = order; break;
                    }
                    continue;
                }

                var name_match = NameHashRe.Match (line);
                if (name_match.Success)
                {
                    if ("PathHash" == name_match.Groups["kind"].Value)
                        ++data.PathHashCount;
                    else
                        ++data.NameHashCount;
                    data.Names[name_match.Groups["hash"].Value.ToUpperInvariant()] = name_match.Groups["name"].Value;
                }
            }
        }

        static void FilterNamesToSourceArchive (string source_file, KrkrDumpData data, HxCrypt crypt)
        {
            if (string.IsNullOrEmpty (source_file) || !File.Exists (source_file))
                return;
            try
            {
                var hx = ReadHxIndex (source_file);
                if (null == hx)
                    return;
                var hashes = crypt.ReadIndexHashes (Path.GetFileName (source_file), hx);
                if (null == hashes)
                    return;
                var before = data.Names.Count;
                var removed = data.Names.Keys
                    .Where (x => !hashes.PathHashes.Contains (x) && !hashes.NameHashes.Contains (x))
                    .ToList();
                foreach (var key in removed)
                    data.Names.Remove (key);
                data.PathHashCount = data.Names.Keys.Count (x => x.Length == 16);
                data.NameHashCount = data.Names.Keys.Count (x => x.Length == 64);
                Trace.WriteLine (string.Format (
                    "Filtered KrkrDump name map to target archive. archive='{0}', before={1}, after={2}, indexPathHashes={3}, indexNameHashes={4}",
                    source_file, before, data.Names.Count, hashes.PathHashes.Count, hashes.NameHashes.Count),
                    "[KrkrDump]");
            }
            catch (Exception X)
            {
                Trace.WriteLine (string.Format ("Failed to filter KrkrDump names for '{0}': {1}",
                                                source_file, X.Message), "[KrkrDump]");
            }
        }

        internal static byte[] ReadHxIndex (string source_file)
        {
            using (var file = new ArcView (source_file))
            {
                if (!BytesEqual (file, 0, Xp3Header))
                    return null;
                long base_offset = 0;
                long dir_offset = base_offset + file.View.ReadInt64 (base_offset+0x0b);
                if (dir_offset < 0x13 || dir_offset >= file.MaxOffset)
                    return null;
                if (0x80 == file.View.ReadUInt32 (dir_offset))
                {
                    dir_offset = base_offset + file.View.ReadInt64 (dir_offset+9);
                    if (dir_offset < 0x13 || dir_offset >= file.MaxOffset)
                        return null;
                }
                int header_type = file.View.ReadByte (dir_offset);
                Stream header_stream;
                if (0 == header_type)
                {
                    long header_size = file.View.ReadInt64 (dir_offset+1);
                    if (header_size > uint.MaxValue)
                        return null;
                    header_stream = file.CreateStream (dir_offset+9, (uint)header_size);
                }
                else if (1 == header_type)
                {
                    long packed_size = file.View.ReadInt64 (dir_offset+1);
                    if (packed_size > uint.MaxValue)
                        return null;
                    using (var input = file.CreateStream (dir_offset+17, (uint)packed_size))
                        header_stream = ZLibCompressor.DeCompress (input);
                }
                else
                    return null;

                using (header_stream)
                using (var header = new BinaryReader (header_stream, Encoding.Unicode))
                {
                    while (-1 != header.PeekChar())
                    {
                        uint entry_signature = header.ReadUInt32();
                        long entry_size = header.ReadInt64();
                        if (entry_size < 0)
                            return null;
                        long next_entry = header.BaseStream.Position + entry_size;
                        if (0x34767848 == entry_signature) // "Hxv4"
                        {
                            var offset = header.ReadInt64 () + base_offset;
                            var size = header.ReadUInt32 ();
                            if (offset < 0 || size > file.MaxOffset || offset + size > file.MaxOffset)
                                return null;
                            return file.View.ReadBytes (offset, size);
                        }
                        header.BaseStream.Position = next_entry;
                    }
                }
            }
            return null;
        }

        static bool BytesEqual (ArcView file, long offset, byte[] signature)
        {
            if (offset < 0 || offset + signature.Length > file.MaxOffset)
                return false;
            for (int i = 0; i < signature.Length; ++i)
            {
                if (file.View.ReadByte (offset+i) != signature[i])
                    return false;
            }
            return true;
        }

        static byte[] ParseOrder (string values)
        {
            return values.Split (new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select (x => byte.Parse (x.Trim(), CultureInfo.InvariantCulture)).ToArray();
        }

        static byte[] ParseHexBytes (string hex)
        {
            hex = hex.Trim();
            var data = new byte[hex.Length / 2];
            for (int i = 0; i < data.Length; ++i)
                data[i] = byte.Parse (hex.Substring (i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return data;
        }

        static uint[] ReadControlBlock (string path)
        {
            if (string.IsNullOrEmpty (path) || !File.Exists (path))
                return null;
            var bytes = File.ReadAllBytes (path);
            if (bytes.Length < 4 || 0 != (bytes.Length & 3))
                return null;
            var result = new uint[bytes.Length / 4];
            for (int i = 0; i < result.Length; ++i)
                result[i] = ~BitConverter.ToUInt32 (bytes, i * 4);
            return result;
        }

        static void ReadOrderFile (string path, KrkrDumpData data)
        {
            if (string.IsNullOrEmpty (path) || !File.Exists (path))
                return;
            var order = File.ReadAllBytes (path);
            if (order.Length < 0x11)
                return;
            if (null == data.EvenOrder)
                data.EvenOrder = ConvertOrder (order, 0, 8, new byte[] { 0, 2, 3, 1, 5, 6, 7, 4 });
            if (null == data.OddOrder)
                data.OddOrder = ConvertOrder (order, 8, 6, new byte[] { 2, 5, 3, 4, 1, 0 });
            if (null == data.PrologOrder)
                data.PrologOrder = ConvertOrder (order, 0xE, 3, new byte[] { 0, 1, 2 });
        }

        static byte[] ConvertOrder (byte[] order, int offset, int count, byte[] mapping)
        {
            var result = new byte[count];
            for (int i = 0; i < count; ++i)
            {
                var src = order[offset + i];
                if (src >= count)
                    return null;
                result[src] = mapping[i];
            }
            return result;
        }

        static string WriteNamesFile (ResourceParameterCommandResult result, KrkrDumpData data)
        {
            if (0 == data.Names.Count || string.IsNullOrEmpty (result.OutputDirectory))
            {
                Trace.WriteLine ("No KrkrDump name map was written.", "[KrkrDump]");
                return null;
            }
            Directory.CreateDirectory (result.OutputDirectory);
            var path = Path.Combine (result.OutputDirectory, "HxNames.lst");
            using (var writer = new StreamWriter (path, false, Encoding.UTF8))
            {
                foreach (var pair in data.Names.OrderBy (x => x.Key, StringComparer.Ordinal))
                    writer.WriteLine ("{0}:{1}", pair.Key, pair.Value);
            }
            Trace.WriteLine (string.Format ("Wrote KrkrDump name map. entries={0}, path='{1}'",
                                            data.Names.Count, path), "[KrkrDump]");
            return path;
        }

        class KrkrDumpData
        {
            public string OutputDirectory;
            public string SourceArchive;
            public string GameDirectory;
            public readonly List<string> LogFiles = new List<string>();
            public readonly List<string> LogLines = new List<string>();
            public readonly Dictionary<string, string> Names = new Dictionary<string, string> (StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, HxIndexKey> IndexKeys = new Dictionary<string, HxIndexKey> (StringComparer.OrdinalIgnoreCase);
            public int PathHashCount;
            public int NameHashCount;
            public byte[] IndexKey1;
            public byte[] IndexKey2;
            public ulong FilterKey;
            public uint SplitPosMask;
            public uint SplitPos;
            public int RandomType;
            public uint[] ControlBlock;
            public byte[] EvenOrder;
            public byte[] OddOrder;
            public byte[] PrologOrder;
            public string NamesFile;
        }
    }
}
