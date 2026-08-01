using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using GameRes;
using GameRes.Compression;
using GameRes.Formats.Strings;

namespace GameRes.Formats.KiriKiri
{
    public class KrkrDumpImportResult
    {
        ICrypt m_scheme;
        readonly Dictionary<string, string> m_artifact_sha256 =
            new Dictionary<string, string> (StringComparer.OrdinalIgnoreCase);

        public bool Success { get; set; }
        public string SchemeName { get; set; }
        public string Message { get; set; }
        public IList<string> LogFiles { get; private set; }
        public string TableFile { get; set; }
        public string OrderFile { get; set; }
        public string NamesFile { get; set; }
        public bool StrictDirectory { get; set; }

        public KrkrDumpImportResult ()
        {
            LogFiles = new List<string>();
        }

        /// <summary>
        /// Returns the imported scheme without exposing it as a serializable
        /// property.  Scheme implementations contain secret key material and
        /// must never be included in machine-readable command output.
        /// </summary>
        public ICrypt GetScheme ()
        {
            return m_scheme;
        }

        internal void SetScheme (ICrypt scheme)
        {
            m_scheme = scheme;
        }

        public string GetArtifactSha256 (string path)
        {
            if (string.IsNullOrWhiteSpace (path))
                return null;
            string value;
            return m_artifact_sha256.TryGetValue (
                Path.GetFullPath (path), out value) ? value : null;
        }

        internal void SetArtifactSha256 (string path, string sha256)
        {
            if (string.IsNullOrWhiteSpace (path)
                || string.IsNullOrWhiteSpace (sha256))
            {
                return;
            }
            m_artifact_sha256[Path.GetFullPath (path)] = sha256;
        }
    }

    public static class KrkrDumpResultImporter
    {
        internal const string LimelightLemonadeJamPresetId = "lllj";
        const string LimelightLemonadeJamExecutablePrefix = "limelight_lj";
        const string LimelightLemonadeJamNamesFile = "HxNames-LLLJ.lst";
        const int MaxLogFiles = 128;
        const long MaxLogFileBytes = 16L * 1024 * 1024;
        const long MaxTotalLogBytes = 64L * 1024 * 1024;
        const long MaxNamesFileBytes = 64L * 1024 * 1024;
        const long MaxOrderFileBytes = 64L * 1024;

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
            string names_sha256;
            if (!TryReadNamesFile (
                names_file, out names, out validation_error,
                out names_sha256))
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
            var crypt = base_crypt.CloneWithInlineNames (names);
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
            var import = new KrkrDumpImportResult
            {
                Success = true,
                SchemeName = scheme_name,
                NamesFile = names_file,
                Message = string.Format (Text (message_name), names.Count, path_matches,
                                          index_hashes.PathHashes.Count, name_matches, index_hashes.NameHashes.Count),
            };
            import.SetScheme (crypt);
            import.SetArtifactSha256 (names_file, names_sha256);
            return import;
        }

        public static KrkrDumpImportResult Import (ResourceParameterCommandResult result, string source_file,
                                                    bool include_same_directory = false,
                                                    Action<ResourceProgressInfo> progress_reporter = null)
        {
            return Import (result, source_file, include_same_directory, false, progress_reporter);
        }

        /// <summary>
        /// Import a KrkrDump result.  When <paramref name="strict_directory"/>
        /// is true, every consumed artifact is resolved only from the explicit
        /// output directory; game, runtime, and log-file sibling directories
        /// are not searched.
        /// </summary>
        public static KrkrDumpImportResult Import (ResourceParameterCommandResult result, string source_file,
                                                    bool include_same_directory, bool strict_directory,
                                                    Action<ResourceProgressInfo> progress_reporter = null)
        {
            return Import (result, source_file, include_same_directory,
                           strict_directory, true, progress_reporter);
        }

        /// <summary>
        /// Import a KrkrDump result, optionally retaining parsed names only in
        /// the transient scheme instead of writing an HxNames cache file.
        /// </summary>
        public static KrkrDumpImportResult Import (ResourceParameterCommandResult result, string source_file,
                                                    bool include_same_directory, bool strict_directory,
                                                    bool write_names_cache,
                                                    Action<ResourceProgressInfo> progress_reporter = null)
        {
            if (null == result || !result.Success)
                return new KrkrDumpImportResult { Success = false, Message = result != null ? result.Message : Text ("KrkrDumpNoResult") };

            KrkrDumpData data;
            try
            {
                data = ReadDumpData (result, strict_directory);
            }
            catch (InvalidDataException exception)
            {
                Trace.WriteLine (string.Format (
                    "KrkrDump artifact limit or shape validation failed. output='{0}', error='{1}'",
                    result.OutputDirectory, exception.Message), "[KrkrDump]");
                return new KrkrDumpImportResult {
                    Success = false,
                    StrictDirectory = strict_directory,
                    Message = exception.Message,
                };
            }
            if (data.LogLines.Count == 0)
            {
                Trace.WriteLine (string.Format ("No KrkrDump log lines found. output='{0}', log='{1}'",
                                                result.OutputDirectory, result.LogFileName), "[KrkrDump]");
                return new KrkrDumpImportResult { Success = false, Message = Text ("KrkrDumpLogNotFound") };
            }

            try
            {
                ParseLog (data);
                ValidateParsedDump (data);
            }
            catch (Exception exception)
            {
                if (!(exception is FormatException)
                    && !(exception is OverflowException)
                    && !(exception is ArgumentException))
                {
                    throw;
                }
                Trace.WriteLine (string.Format (
                    "Invalid KrkrDump log value. output='{0}', error='{1}'",
                    result.OutputDirectory, exception.Message), "[KrkrDump]");
                return new KrkrDumpImportResult {
                    Success = false,
                    StrictDirectory = strict_directory,
                    Message = "The KrkrDump log contains an invalid numeric or order value.",
                };
            }
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

            FilterNamesToSourceArchive (source_file, data, crypt);
            data.NamesFile = write_names_cache ? WriteNamesFile (result, data) : null;
            crypt.NamesFile = data.NamesFile;
            if (!write_names_cache)
                crypt.SetInlineNames (data.Names);
            if (!string.IsNullOrEmpty (data.NamesFile))
                result.Metadata["KrkrDumpNamesFile"] = data.NamesFile;
            TraceDumpData (data);

            var scheme_name = CreateSchemeName (result, source_file);
            Xp3Opener.KnownSchemes[scheme_name] = crypt;
            Xp3Opener.SetTransientScheme (scheme_name, source_file, include_same_directory);
            var import = new KrkrDumpImportResult
            {
                Success = true,
                SchemeName = scheme_name,
                TableFile = data.ControlBlockFile,
                OrderFile = data.OrderFile,
                NamesFile = data.NamesFile,
                StrictDirectory = strict_directory,
                Message = string.Format (Text ("KrkrDumpSchemeImported"), scheme_name),
            };
            import.SetScheme (crypt);
            foreach (var log_file in data.LogFiles)
                import.LogFiles.Add (log_file);
            foreach (var artifact in data.ArtifactSha256)
                import.SetArtifactSha256 (artifact.Key, artifact.Value);

            if (strict_directory)
                return import;
            var automatic_names_files = FindAutomaticNamesFiles (result, source_file).ToList();
            if (0 == automatic_names_files.Count)
                return import;

            KrkrDumpImportResult last_failure = null;
            foreach (var automatic_names_file in automatic_names_files)
            {
                var names_result = new ResourceParameterCommandResult { Success = true };
                names_result.Metadata["NamesFile"] = automatic_names_file;
                var names_import = ImportNamesFile (names_result, source_file, scheme_name, include_same_directory);
                if (names_import.Success)
                {
                    result.Metadata["ImportedNamesFile"] = automatic_names_file;
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
            return import;
        }

        static IEnumerable<string> FindAutomaticNamesFiles (ResourceParameterCommandResult result, string source_file)
        {
            var candidates = new List<string>();
            AddCandidate (candidates, GetMetadata (result, "HxNamesFile"));

            AddCandidate (candidates, GetInstalledNamesFile (result, source_file));

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

        internal static string ResolveInstalledNamesPresetFile (
            string preset_id, ResourceParameterCommandResult result, string source_file)
        {
            if (string.IsNullOrEmpty (preset_id))
                return GetInstalledNamesFile (result, source_file);
            if (string.Equals (preset_id, LimelightLemonadeJamPresetId,
                               StringComparison.OrdinalIgnoreCase))
            {
                return Path.Combine (FormatCatalog.Instance.DataDirectory,
                                     LimelightLemonadeJamNamesFile);
            }
            return null;
        }

        static string GetInstalledNamesFile (ResourceParameterCommandResult result, string source_file)
        {
            if (!IsLimelightLemonadeJam (result, source_file))
                return null;
            return ResolveInstalledNamesPresetFile (
                LimelightLemonadeJamPresetId, result, source_file);
        }

        static bool IsLimelightLemonadeJam (ResourceParameterCommandResult result, string source_file)
        {
            if (IsLimelightLemonadeJamExecutable (GetMetadata (result, "GameExecutable")))
                return true;

            var game_directory = GetMetadata (result, "GameDirectory");
            if (string.IsNullOrEmpty (game_directory) && !string.IsNullOrEmpty (source_file))
                game_directory = Path.GetDirectoryName (source_file);
            if (string.IsNullOrEmpty (game_directory) || !Directory.Exists (game_directory))
                return false;
            try
            {
                return Directory.EnumerateFiles (game_directory, "*.exe")
                    .Any (IsLimelightLemonadeJamExecutable);
            }
            catch
            {
                return false;
            }
        }

        static bool IsLimelightLemonadeJamExecutable (string path)
        {
            if (string.IsNullOrWhiteSpace (path))
                return false;
            var name = Path.GetFileNameWithoutExtension (path);
            return !string.IsNullOrEmpty (name)
                && name.StartsWith (LimelightLemonadeJamExecutablePrefix,
                                    StringComparison.OrdinalIgnoreCase);
        }

        public static string GetAutomaticNamesCacheFile (ResourceParameterCommandResult result, string source_file)
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

        internal static bool TryReadNamesFile (string names_file, out Dictionary<string, string> names, out string error)
        {
            string sha256;
            return TryReadNamesFile (
                names_file, out names, out error, out sha256);
        }

        static bool TryReadNamesFile (
            string names_file, out Dictionary<string, string> names,
            out string error, out string sha256)
        {
            names = new Dictionary<string, string> (StringComparer.OrdinalIgnoreCase);
            error = null;
            sha256 = null;
            try
            {
                byte[] bytes = ReadBoundedFile (
                    names_file, MaxNamesFileBytes, "HxNames file");
                sha256 = HashBytes (bytes);
                using (var memory = new MemoryStream (bytes, false))
                using (var input = new StreamReader (
                    memory, Encoding.UTF8, true))
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

        static KrkrDumpData ReadDumpData (ResourceParameterCommandResult result, bool strict_directory)
        {
            var data = new KrkrDumpData { StrictDirectory = strict_directory };
            data.OutputDirectory = GetFullPathOrNull (result.OutputDirectory);
            data.SourceArchive = GetMetadata (result, "SourceArchive");
            data.GameDirectory = GetMetadata (result, "GameDirectory");

            var log_files = new List<string>();
            if (strict_directory)
            {
                var explicit_log = ResolveStrictArtifact (data.OutputDirectory, result.LogFileName);
                if (!string.IsNullOrEmpty (explicit_log))
                    log_files.Add (explicit_log);
                AddLogs (log_files, data.OutputDirectory);
            }
            else
            {
                if (!string.IsNullOrEmpty (result.LogFileName))
                    log_files.Add (result.LogFileName);
                AddLogs (log_files, result.OutputDirectory);
                AddLogs (log_files, data.GameDirectory);
            }
            long total_log_bytes = 0;
            foreach (var log in log_files.Where (File.Exists)
                                         .Select (GetFullPathOrNull)
                                         .Where (x => !string.IsNullOrEmpty (x))
                                         .Where (x => !strict_directory
                                             || IsStrictArtifactPath (
                                                 data.OutputDirectory, x))
                                         .Distinct (StringComparer.OrdinalIgnoreCase)
                                         .OrderBy (x => x, StringComparer.OrdinalIgnoreCase))
            {
                byte[] bytes = ReadBoundedFile (
                    log, MaxLogFileBytes, "KrkrDump log");
                if (total_log_bytes > MaxTotalLogBytes - bytes.Length)
                {
                    throw new InvalidDataException (
                        "KrkrDump logs exceed the cumulative 64 MiB input limit.");
                }
                total_log_bytes += bytes.Length;
                data.LogFiles.Add (log);
                data.ArtifactSha256[log] = HashBytes (bytes);
                using (var memory = new MemoryStream (bytes, false))
                using (var reader = new StreamReader (
                    memory, Encoding.UTF8, true))
                {
                    string line;
                    while (null != (line = reader.ReadLine()))
                        data.LogLines.Add (line);
                }
            }

            data.ControlBlockFile = FindResultFile (result, "CxdecTable.bin", strict_directory);
            data.ControlBlock = ReadControlBlock (
                data.ControlBlockFile, strict_directory, data);
            data.OrderFile = FindResultFile (result, "CxdecOrder.bin", strict_directory);
            ReadOrderFile (data.OrderFile, data);
            result.Metadata["KrkrDumpStrictDirectory"] = strict_directory ? "true" : "false";
            if (data.LogFiles.Count > 0)
                result.Metadata["KrkrDumpLogFiles"] = string.Join (";", data.LogFiles);
            if (!string.IsNullOrEmpty (data.ControlBlockFile))
                result.Metadata["KrkrDumpTableFile"] = data.ControlBlockFile;
            if (!string.IsNullOrEmpty (data.OrderFile))
                result.Metadata["KrkrDumpOrderFile"] = data.OrderFile;
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
            foreach (string path in Directory.EnumerateFiles (
                directory, "KrkrDump-*.log"))
            {
                if (logs.Contains (path, StringComparer.OrdinalIgnoreCase))
                    continue;
                if (logs.Count >= MaxLogFiles)
                {
                    throw new InvalidDataException (
                        "KrkrDump output contains more than 128 log files.");
                }
                logs.Add (path);
            }
        }

        static string FindResultFile (ResourceParameterCommandResult result, string name,
                                      bool strict_directory)
        {
            if (strict_directory)
            {
                var path = ResolveStrictArtifact (result.OutputDirectory, name);
                return !string.IsNullOrEmpty (path) && File.Exists (path) ? path : null;
            }
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
                    return GetFullPathOrNull (path);
            }
            return null;
        }

        static string ResolveStrictArtifact (string directory, string path)
        {
            directory = GetFullPathOrNull (directory);
            if (string.IsNullOrEmpty (directory) || string.IsNullOrWhiteSpace (path))
                return null;
            try
            {
                var candidate = Path.IsPathRooted (path)
                    ? Path.GetFullPath (path)
                    : Path.GetFullPath (Path.Combine (directory, path));
                var root = directory.TrimEnd (Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                           + Path.DirectorySeparatorChar;
                if (!candidate.StartsWith (root, StringComparison.OrdinalIgnoreCase))
                    return null;
                if (!IsStrictArtifactPath (directory, candidate))
                    return null;
                return candidate;
            }
            catch
            {
                return null;
            }
        }

        static bool IsStrictArtifactPath (string directory, string candidate)
        {
            directory = GetFullPathOrNull (directory);
            candidate = GetFullPathOrNull (candidate);
            if (string.IsNullOrEmpty (directory) || string.IsNullOrEmpty (candidate))
                return false;
            var root = directory.TrimEnd (
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            if (!candidate.StartsWith (root, StringComparison.OrdinalIgnoreCase))
                return false;
            string ancestor = directory;
            while (!string.IsNullOrEmpty (ancestor))
            {
                if ((File.Exists (ancestor) || Directory.Exists (ancestor))
                    && 0 != (File.GetAttributes (ancestor)
                             & FileAttributes.ReparsePoint))
                {
                    return false;
                }
                string parent = Path.GetDirectoryName (ancestor);
                if (string.IsNullOrEmpty (parent)
                    || string.Equals (parent, ancestor,
                                      StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
                ancestor = parent;
            }
            string current = candidate;
            for (;;)
            {
                if ((File.Exists (current) || Directory.Exists (current))
                    && 0 != (File.GetAttributes (current)
                             & FileAttributes.ReparsePoint))
                {
                    return false;
                }
                if (string.Equals (current, directory,
                                   StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
                string parent = Path.GetDirectoryName (current);
                if (string.IsNullOrEmpty (parent)
                    || string.Equals (parent, current,
                                      StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
                current = parent;
            }
        }

        static string GetFullPathOrNull (string path)
        {
            if (string.IsNullOrWhiteSpace (path))
                return null;
            try
            {
                return Path.GetFullPath (path);
            }
            catch
            {
                return null;
            }
        }

        static string GetMetadata (ResourceParameterCommandResult result, string key)
        {
            if (null == result)
                return null;
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
                    case "Split Pos Mask": data.SplitPosMask = checked ((uint)value); break;
                    case "Split Pos": data.SplitPos = checked ((uint)value); break;
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
                    int order_size = int.Parse (
                        order_match.Groups["size"].Value,
                        CultureInfo.InvariantCulture);
                    var order = ParseOrder (
                        order_match.Groups["values"].Value, order_size);
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

        static void ValidateParsedDump (KrkrDumpData data)
        {
            ValidateIndexKeyPair (
                data.IndexKey1, data.IndexKey2, "default index key");
            foreach (var pair in data.IndexKeys)
            {
                if (null == pair.Value)
                    throw new ArgumentException (
                        "The archive index key pair is missing: " + pair.Key);
                ValidateIndexKeyPair (
                    pair.Value.Key1, pair.Value.Key2,
                    "archive index key for " + pair.Key);
            }
        }

        static void ValidateIndexKeyPair (
            byte[] key1, byte[] key2, string label)
        {
            if (null == key1 && null == key2)
                return;
            if (null == key1 || 32 != key1.Length
                || null == key2 || 16 != key2.Length)
            {
                throw new ArgumentException (
                    label + " must contain a 32-byte Index Key and a "
                        + "16-byte Index Nonce.");
            }
        }

        static byte[] ParseOrder (string values, int expected_size)
        {
            byte[] order = values.Split (
                    new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select (x => byte.Parse (x.Trim(), CultureInfo.InvariantCulture)).ToArray();
            if (order.Length != expected_size)
                throw new ArgumentException ("The Cxdec order has an invalid length.");
            var seen = new bool[expected_size];
            foreach (byte value in order)
            {
                if (value >= expected_size || seen[value])
                    throw new ArgumentException (
                        "The Cxdec order must be a permutation of its indexes.");
                seen[value] = true;
            }
            return order;
        }

        static byte[] ParseHexBytes (string hex)
        {
            hex = hex.Trim();
            if (0 != (hex.Length & 1))
                throw new FormatException ("A hexadecimal key has an odd length.");
            var data = new byte[hex.Length / 2];
            for (int i = 0; i < data.Length; ++i)
                data[i] = byte.Parse (hex.Substring (i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return data;
        }

        static uint[] ReadControlBlock (
            string path, bool require_complete_table, KrkrDumpData data)
        {
            if (string.IsNullOrEmpty (path) || !File.Exists (path))
                return null;
            var bytes = ReadBoundedFile (
                path, require_complete_table ? 0x1000 : MaxLogFileBytes,
                "Cxdec control block");
            data.ArtifactSha256[path] = HashBytes (bytes);
            if (bytes.Length < 4 || 0 != (bytes.Length & 3)
                || (require_complete_table && 0x1000 != bytes.Length))
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
            var order = ReadBoundedFile (
                path, MaxOrderFileBytes, "Cxdec order file");
            data.ArtifactSha256[path] = HashBytes (order);
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
            var seen = new bool[count];
            for (int i = 0; i < count; ++i)
            {
                var src = order[offset + i];
                if (src >= count || seen[src])
                    return null;
                seen[src] = true;
                result[src] = mapping[i];
            }
            return result;
        }

        static string HashBytes (byte[] value)
        {
            using (var hash = SHA256.Create())
            {
                byte[] digest = hash.ComputeHash (value);
                var text = new StringBuilder (digest.Length * 2);
                foreach (byte item in digest)
                {
                    text.Append (item.ToString (
                        "x2", CultureInfo.InvariantCulture));
                }
                return text.ToString();
            }
        }

        static byte[] ReadBoundedFile (
            string path, long maximum_bytes, string label)
        {
            using (var input = new FileStream (
                path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                long length = input.Length;
                if (length < 0 || length > maximum_bytes
                    || length > int.MaxValue)
                {
                    throw new InvalidDataException (string.Format (
                        CultureInfo.InvariantCulture,
                        "{0} exceeds its {1}-byte input limit: {2}",
                        label, maximum_bytes, path));
                }
                var value = new byte[(int)length];
                int offset = 0;
                while (offset < value.Length)
                {
                    int read = input.Read (
                        value, offset, value.Length - offset);
                    if (0 == read)
                    {
                        throw new InvalidDataException (
                            label + " changed while it was being read: " + path);
                    }
                    offset += read;
                }
                if (-1 != input.ReadByte())
                {
                    throw new InvalidDataException (
                        label + " changed while it was being read: " + path);
                }
                return value;
            }
        }

        static string WriteNamesFile (ResourceParameterCommandResult result, KrkrDumpData data)
        {
            if (0 == data.Names.Count || string.IsNullOrEmpty (result.OutputDirectory))
            {
                Trace.WriteLine ("No KrkrDump name map was written.", "[KrkrDump]");
                return null;
            }
            Directory.CreateDirectory (result.OutputDirectory);
            var path = Path.GetFullPath (Path.Combine (result.OutputDirectory, "HxNames.lst"));
            using (var writer = new StreamWriter (path, false, Encoding.UTF8))
            {
                foreach (var pair in data.Names.OrderBy (x => x.Key, StringComparer.Ordinal))
                    writer.WriteLine ("{0}:{1}", pair.Key, pair.Value);
            }
            Trace.WriteLine (string.Format ("Wrote KrkrDump name map. entries={0}, path='{1}'",
                                            data.Names.Count, path), "[KrkrDump]");
            result.Metadata["KrkrDumpNamesCacheWritten"] = "true";
            result.Metadata["KrkrDumpNamesFile"] = path;
            return path;
        }

        class KrkrDumpData
        {
            public string OutputDirectory;
            public string SourceArchive;
            public string GameDirectory;
            public bool StrictDirectory;
            public readonly List<string> LogFiles = new List<string>();
            public readonly List<string> LogLines = new List<string>();
            public readonly Dictionary<string, string> Names = new Dictionary<string, string> (StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, HxIndexKey> IndexKeys = new Dictionary<string, HxIndexKey> (StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, string> ArtifactSha256 =
                new Dictionary<string, string> (
                    StringComparer.OrdinalIgnoreCase);
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
            public string ControlBlockFile;
            public string OrderFile;
            public string NamesFile;
        }
    }
}
