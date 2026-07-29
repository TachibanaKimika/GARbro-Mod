//! \file       HxV4Tools.cs
//! \date       2026 Jul 30
//! \brief      Reusable Hx v4 name-table and extracted-tree tools.
//
// Copyright (C) 2026 by GARbro-Mod-Onachi contributors
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to
// deal in the Software without restriction, including without limitation the
// rights to use, copy, modify, merge, publish, distribute, sublicense, and/or
// sell copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
// FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS
// IN THE SOFTWARE.
//

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace GameRes.Formats.KiriKiri
{
    public sealed class HxV4SourceOptions
    {
        public IList<string> SourceDirectories { get; private set; }
        public IList<string> SourceFiles { get; private set; }
        public IList<string> KrkrDumpDirectories { get; private set; }
        public IList<string> SeedNamesFiles { get; private set; }
        public int MaxFiles { get; set; }
        public bool IncludeGarbroCommonCandidates { get; set; }
        public Func<bool> CancellationRequested { get; set; }

        public HxV4SourceOptions ()
        {
            SourceDirectories = new List<string>();
            SourceFiles = new List<string>();
            KrkrDumpDirectories = new List<string>();
            SeedNamesFiles = new List<string>();
            MaxFiles = 100000;
        }
    }

    public sealed class HxV4SourceGenerationResult
    {
        public bool Success;
        public string NamesFile;
        public string Error;
        public int ScannedFiles;
        public int ParsedResources;
        public int ScenarioCount;
        public int CandidateCount;
        public int PathCount;
        public int NameCount;
        public int SeedCount;
        public int KrkrDumpNameCount;
    }

    public sealed class HxV4FileOperationItem
    {
        public string Kind;
        public string Source;
        public string Destination;
        public string Status;
        public string Message;
    }

    public sealed class HxV4FileOperationResult
    {
        public bool Success;
        public bool DryRun;
        public string Root;
        public int Planned;
        public int Changed;
        public int Skipped;
        public int Failed;
        public int Ignored;
        public IList<HxV4FileOperationItem> Items { get; private set; }

        public HxV4FileOperationResult ()
        {
            Success = true;
            Items = new List<HxV4FileOperationItem>();
        }
    }

    public sealed class HxV4CleanResult
    {
        public bool Success;
        public string SourceNamesFile;
        public string DeobfuscatedDirectory;
        public string OutputFile;
        public int SourceEntries;
        public int WrittenEntries;
        public int IgnoredEntries;
    }

    public sealed class HxV4MissingVoiceResult
    {
        public int ScannedFiles;
        public int PrefixCount;
        public int CandidateCount;
        public int MissingCount;
        public IList<string> MissingVoiceStems { get; private set; }

        public HxV4MissingVoiceResult ()
        {
            MissingVoiceStems = new List<string>();
        }
    }

    /// <summary>
    /// Native equivalents for the plaintext-dictionary, hashing, clean-table,
    /// directory-restoration, and rename operations used by Hx v4 tooling.
    /// </summary>
    public static class HxV4Tools
    {
        const int MaxKrkrDumpLogFiles = 1024;
        const long MaxKrkrDumpLogBytes = 64L * 1024 * 1024;
        const int MaxMissingVoiceFiles = 100000;
        const int MaxMissingVoiceCandidates = 1000000;

        internal static readonly string[] InitialNames = {
            "base.stage", "cglist.csv", "soundlist.csv", "charvoice.csv",
            "imagediffmap.csv", "savelist.csv", "scenelist.csv", "replay.ks",
            "emotion.csv", "facethumbpos.csv", "facezoom.csv",
            "_chthum_index.pbd",
        };

        static readonly Regex KrkrDumpNameRe = new Regex (
            @"\b(?<kind>PathHash|NameHash):\s+""(?<name>.*?)""\s+""(?<salt>.*?)""\s+""(?<hash>[0-9A-Fa-f]+)""",
            RegexOptions.Compiled);
        static readonly Regex QuotedValueRe = new Regex (@"""(?<name>[^""]*)""",
            RegexOptions.Compiled);

        public static string GetFileNameHash (string name)
        {
            return HxV4Hash.GetFileNameHash (name);
        }

        public static string GetPathHash (string path)
        {
            return HxV4Hash.GetPathHash (NormalizePathCandidate (path));
        }

        public static HxV4SourceGenerationResult GenerateFromSources (
            HxV4SourceOptions options, string output_file)
        {
            var result = new HxV4SourceGenerationResult { NamesFile = output_file };
            try
            {
                if (null == options)
                    throw new ArgumentNullException ("options");
                if (string.IsNullOrWhiteSpace (output_file))
                    throw new ArgumentNullException ("output_file");
                if (options.MaxFiles <= 0)
                    throw new ArgumentOutOfRangeException ("options.MaxFiles");

                var candidates = new HxNameGenerator.CandidateCollector (null);
                candidates.AddPath ("/");
                foreach (var name in InitialNames)
                    candidates.AddName (name);

                foreach (var seed_file in options.SeedNamesFiles)
                {
                    ThrowIfCancellationRequested (options.CancellationRequested);
                    Dictionary<string, string> seed;
                    string error;
                    if (!TryReadNamesFile (seed_file, out seed, out error))
                        throw new InvalidDataException (
                            string.Format ("Could not read HxNames seed '{0}': {1}",
                                           seed_file, error));
                    foreach (var pair in seed)
                        candidates.AddKnownMapping (pair.Key, pair.Value);
                    result.SeedCount += seed.Count;
                }

                var scanner = new HxResourceNameScanner (candidates);
                var visited_files = new HashSet<string> (StringComparer.OrdinalIgnoreCase);
                foreach (var source_directory in options.SourceDirectories)
                {
                    ThrowIfCancellationRequested (options.CancellationRequested);
                    var directory = RequireDirectory (source_directory);
                    ScanDirectory (directory, options.MaxFiles, scanner, candidates,
                                   visited_files, result,
                                   options.CancellationRequested);
                }
                foreach (var source_file in options.SourceFiles)
                {
                    ThrowIfCancellationRequested (options.CancellationRequested);
                    var file = RequireFile (source_file);
                    if (visited_files.Add (file))
                        ScanSourceFile (file, scanner, candidates, result);
                }
                foreach (var log_directory in options.KrkrDumpDirectories)
                {
                    ThrowIfCancellationRequested (options.CancellationRequested);
                    var directory = RequireDirectory (log_directory);
                    result.KrkrDumpNameCount += ScanKrkrDumpLogs (
                        directory, candidates, options.CancellationRequested);
                }

                ThrowIfCancellationRequested (options.CancellationRequested);
                scanner.Complete();
                candidates.ExpandVoiceSequences();
                candidates.ExpandSystemVoices();
                if (options.IncludeGarbroCommonCandidates)
                    candidates.AddCommonPaths();

                HxNameGenerator.WriteNamesFile (output_file, candidates.Matches);
                result.CandidateCount = candidates.CandidateCount;
                result.PathCount = candidates.Paths.Count();
                result.NameCount = candidates.Names.Count();
                result.Success = true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception X)
            {
                result.Success = false;
                result.Error = X.Message;
                Trace.WriteLine (X.ToString(), "[HxV4Tools]");
            }
            return result;
        }

        static void ScanDirectory (string root, int max_files,
                                   HxResourceNameScanner scanner,
                                   HxNameGenerator.CandidateCollector candidates,
                                   ISet<string> visited_files,
                                   HxV4SourceGenerationResult result,
                                   Func<bool> cancellation_requested)
        {
            var pending = new Stack<string>();
            pending.Push (root);
            while (pending.Count > 0)
            {
                ThrowIfCancellationRequested (cancellation_requested);
                var directory = pending.Pop();
                var relative = GetRelativePath (root, directory);
                AddPlainDirectoryCandidates (relative, candidates);

                foreach (var child in Directory.EnumerateDirectories (directory))
                {
                    if (IsReparsePoint (child))
                        continue;
                    pending.Push (child);
                }
                foreach (var file in Directory.EnumerateFiles (directory))
                {
                    ThrowIfCancellationRequested (cancellation_requested);
                    if (visited_files.Count >= max_files)
                        throw new InvalidDataException (
                            "Hx v4 source scan exceeded the configured file limit.");
                    var full_file = Path.GetFullPath (file);
                    if (!visited_files.Add (full_file))
                        continue;

                    var relative_file = GetRelativePath (root, full_file);
                    var file_name = Path.GetFileName (full_file);
                    if (!IsUpstreamHash (Path.GetFileNameWithoutExtension (file_name), 64))
                        candidates.AddName (relative_file);
                    ++result.ScannedFiles;
                    if (HxNameGenerator.ShouldInspectLooseFile (full_file))
                        ScanSourceFile (full_file, scanner, candidates, result);
                }
            }
        }

        static void AddPlainDirectoryCandidates (
            string relative, HxNameGenerator.CandidateCollector candidates)
        {
            if (string.IsNullOrWhiteSpace (relative))
                return;
            var levels = relative.Replace ('\\', '/').Split (
                new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            var full = new StringBuilder();
            foreach (var level in levels)
            {
                full.Append (level).Append ('/');
                if (IsUpstreamHash (level, 16))
                    break;
                candidates.AddPath (full.ToString());
            }
            for (int start = 0; start < levels.Length; ++start)
            {
                bool plain = true;
                for (int i = start; i < levels.Length; ++i)
                {
                    if (IsUpstreamHash (levels[i], 16))
                    {
                        plain = false;
                        break;
                    }
                }
                if (plain)
                    candidates.AddPath (string.Join ("/", levels.Skip (start)) + "/");
            }
        }

        static void ScanSourceFile (string file, HxResourceNameScanner scanner,
                                    HxNameGenerator.CandidateCollector candidates,
                                    HxV4SourceGenerationResult result)
        {
            candidates.AddName (Path.GetFileName (file));
            using (var input = BinaryStream.FromFile (file))
            {
                var scan = scanner.Scan (input, Path.GetFileName (file));
                if (scan.Parsed)
                    ++result.ParsedResources;
                if (scan.Scenario)
                    ++result.ScenarioCount;
            }
        }

        static int ScanKrkrDumpLogs (
            string directory, HxNameGenerator.CandidateCollector candidates,
            Func<bool> cancellation_requested)
        {
            int count = 0;
            int log_count = 0;
            long total_bytes = 0;
            foreach (var log in Directory.EnumerateFiles (
                directory, "KrkrDump-*.log", SearchOption.TopDirectoryOnly))
            {
                if (++log_count > MaxKrkrDumpLogFiles)
                    throw new InvalidDataException (
                        "Hx v4 KrkrDump log scan exceeded the file limit.");
                var length = new FileInfo (log).Length;
                if (length < 0 || total_bytes > MaxKrkrDumpLogBytes-length)
                    throw new InvalidDataException (
                        "Hx v4 KrkrDump log scan exceeded the byte limit.");
                total_bytes += length;
                foreach (var line in File.ReadLines (log, Encoding.UTF8))
                {
                    ThrowIfCancellationRequested (cancellation_requested);
                    var match = KrkrDumpNameRe.Match (line);
                    if (match.Success)
                    {
                        var hash = match.Groups["hash"].Value;
                        var name = match.Groups["name"].Value;
                        candidates.AddKnownMapping (hash, name);
                        if ("PathHash".Equals (match.Groups["kind"].Value,
                                               StringComparison.Ordinal))
                            candidates.AddPath (name);
                        else
                            candidates.AddName (name);
                        ++count;
                        continue;
                    }
                    if (line.IndexOf ("NameHash: ", StringComparison.Ordinal) < 0
                        && line.IndexOf ("PathHash: ", StringComparison.Ordinal) < 0)
                        continue;
                    var quoted = QuotedValueRe.Match (line);
                    if (!quoted.Success)
                        continue;
                    if (line.IndexOf ("PathHash: ", StringComparison.Ordinal) >= 0)
                        candidates.AddPath (quoted.Groups["name"].Value);
                    else
                        candidates.AddName (quoted.Groups["name"].Value);
                    ++count;
                }
            }
            return count;
        }

        public static bool TryReadNamesFile (
            string names_file, out Dictionary<string, string> names, out string error)
        {
            names = new Dictionary<string, string> (StringComparer.OrdinalIgnoreCase);
            error = null;
            try
            {
                if (string.IsNullOrWhiteSpace (names_file) || !File.Exists (names_file))
                {
                    error = "HxNames file was not found.";
                    return false;
                }
                int line_number = 0;
                foreach (var line in File.ReadLines (names_file, Encoding.UTF8))
                {
                    ++line_number;
                    if (string.IsNullOrWhiteSpace (line)
                        || line.StartsWith ("#", StringComparison.Ordinal)
                        || line.StartsWith (";", StringComparison.Ordinal))
                        continue;
                    int separator = line.IndexOf (':');
                    if (separator <= 0)
                    {
                        error = "Invalid HxNames line " + line_number;
                        return false;
                    }
                    var hash = line.Substring (0, separator).Trim();
                    var name = line.Substring (separator+1);
                    if ((16 != hash.Length && 64 != hash.Length) || !IsHex (hash)
                        || (64 == hash.Length && 0 == name.Length))
                    {
                        error = "Invalid HxNames line " + line_number;
                        return false;
                    }
                    names[hash.ToUpperInvariant()] = name;
                }
                if (0 == names.Count)
                {
                    error = "HxNames file is empty.";
                    return false;
                }
                return true;
            }
            catch (Exception X)
            {
                error = X.Message;
                return false;
            }
        }

        public static HxV4CleanResult GenerateCleanNamesFile (
            string source_names_file, string deobfuscated_directory,
            string output_file)
        {
            Dictionary<string, string> table;
            string error;
            if (!TryReadNamesFile (source_names_file, out table, out error))
                throw new InvalidDataException (error);
            var root = RequireDirectory (deobfuscated_directory);
            if (string.IsNullOrWhiteSpace (output_file))
                throw new ArgumentNullException ("output_file");

            var file_lookup = table.Where (x => 64 == x.Key.Length)
                .GroupBy (x => x.Value, StringComparer.Ordinal)
                .ToDictionary (x => x.Key, x => x.First().Key, StringComparer.Ordinal);
            var path_lookup = table.Where (x => 16 == x.Key.Length)
                .GroupBy (x => NormalizePathCandidate (x.Value), StringComparer.Ordinal)
                .ToDictionary (x => x.Key, x => x.First().Key, StringComparer.Ordinal);
            var output = new Dictionary<string, string> (StringComparer.OrdinalIgnoreCase);
            int ignored = 0;

            foreach (var xp3_directory in Directory.EnumerateDirectories (root))
            {
                foreach (var file in EnumerateFilesSafe (xp3_directory))
                {
                    var name = Path.GetFileName (file);
                    string hash;
                    if (IsUpstreamHash (Path.GetFileNameWithoutExtension (name), 64)
                        || !file_lookup.TryGetValue (name, out hash))
                    {
                        ++ignored;
                        continue;
                    }
                    output[hash] = name;
                }
                foreach (var directory in EnumerateDirectoriesSafe (xp3_directory))
                {
                    var relative = NormalizePathCandidate (
                        GetRelativePath (xp3_directory, directory));
                    if (relative.Split ('/').Any (x => IsUpstreamHash (x, 16)))
                    {
                        ++ignored;
                        continue;
                    }
                    string hash;
                    if (!path_lookup.TryGetValue (relative, out hash))
                    {
                        ++ignored;
                        continue;
                    }
                    output[hash] = relative;
                }
            }
            HxNameGenerator.WriteNamesFile (output_file, output);
            return new HxV4CleanResult {
                Success = true,
                SourceNamesFile = Path.GetFullPath (source_names_file),
                DeobfuscatedDirectory = root,
                OutputFile = Path.GetFullPath (output_file),
                SourceEntries = table.Count,
                WrittenEntries = output.Count,
                IgnoredEntries = ignored,
            };
        }

        public static HxV4MissingVoiceResult FindMissingVoices (
            IEnumerable<string> voice_directories,
            Func<bool> cancellation_requested = null)
        {
            if (null == voice_directories)
                throw new ArgumentNullException ("voice_directories");
            var roots = voice_directories
                .Select (RequireDirectory)
                .Distinct (StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (0 == roots.Length)
                throw new ArgumentException (
                    "At least one voice directory is required.",
                    "voice_directories");

            var sequences = new Dictionary<string, VoiceSequenceInfo> (
                StringComparer.Ordinal);
            var existing_ogg = new HashSet<string> (
                StringComparer.OrdinalIgnoreCase);
            var result = new HxV4MissingVoiceResult();
            foreach (var root in roots)
            {
                foreach (var file in Directory.EnumerateFiles (
                    root, "*", SearchOption.TopDirectoryOnly))
                {
                    ThrowIfCancellationRequested (cancellation_requested);
                    if (++result.ScannedFiles > MaxMissingVoiceFiles)
                        throw new InvalidDataException (
                            "Hx v4 missing-voice scan exceeded the file limit.");
                    var extension = Path.GetExtension (file);
                    if (".ogg".Equals (extension, StringComparison.OrdinalIgnoreCase))
                    {
                        var stem = Path.GetFileNameWithoutExtension (file);
                        existing_ogg.Add (stem);
                        RememberVoiceSequence (stem, sequences);
                    }
                    else if (".sli".Equals (
                        extension, StringComparison.OrdinalIgnoreCase))
                    {
                        RememberVoiceSequence (
                            Path.GetFileNameWithoutExtension (file), sequences);
                    }
                    else if (".csv".Equals (
                                 extension, StringComparison.OrdinalIgnoreCase)
                             && Path.GetFileNameWithoutExtension (file).StartsWith (
                                 "bgv", StringComparison.Ordinal))
                    {
                        ScanBgvVoiceSequences (file, sequences);
                    }
                }
            }

            result.PrefixCount = sequences.Count;
            var missing = new HashSet<string> (StringComparer.OrdinalIgnoreCase);
            foreach (var pair in sequences.OrderBy (
                x => x.Key, StringComparer.Ordinal))
            {
                int limit = Math.Min (pair.Value.Maximum + 5, 99999);
                for (int number = 1; number <= limit; ++number)
                {
                    ThrowIfCancellationRequested (cancellation_requested);
                    var base_stem = pair.Key + "_"
                        + number.ToString (
                            "D" + pair.Value.Width, CultureInfo.InvariantCulture);
                    foreach (var suffix in new[] { "", "a", "b", "c" })
                    {
                        var stem = base_stem + suffix;
                        result.CandidateCount += 2;
                        if (result.CandidateCount > MaxMissingVoiceCandidates)
                            throw new InvalidDataException (
                                "Hx v4 missing-voice scan exceeded the candidate limit.");
                        if (!existing_ogg.Contains (stem))
                            missing.Add (stem);
                    }
                }
            }
            foreach (var stem in missing.OrderBy (
                x => x, StringComparer.Ordinal))
                result.MissingVoiceStems.Add (stem);
            result.MissingCount = result.MissingVoiceStems.Count;
            return result;
        }

        public static HxV4FileOperationResult RestoreDirectoryStructure (
            string root_directory, bool recursive, bool dry_run)
        {
            var root = RequireDirectory (root_directory);
            var result = new HxV4FileOperationResult {
                Root = root,
                DryRun = dry_run,
            };
            var reserved_destinations = new HashSet<string> (
                StringComparer.OrdinalIgnoreCase);
            var directories = recursive
                ? new[] { root }.Concat (EnumerateDirectoriesSafe (root)).ToArray()
                : new[] { root };
            foreach (var directory in directories)
            {
                foreach (var file in Directory.EnumerateFiles (
                    directory, "*", SearchOption.TopDirectoryOnly).ToArray())
                {
                    var stem = Path.GetFileNameWithoutExtension (file);
                    if (stem.IndexOf ('_') < 0)
                    {
                        ++result.Ignored;
                        continue;
                    }
                    var parts = stem.Split ('_');
                    if (parts.Length < 2 || parts.Any (string.IsNullOrEmpty))
                    {
                        ++result.Ignored;
                        continue;
                    }
                    var destination_directory = Path.Combine (
                        new[] { directory }.Concat (parts.Take (parts.Length-1)).ToArray());
                    var destination = Path.Combine (
                        destination_directory, parts[parts.Length-1] + Path.GetExtension (file));
                    destination = EnsureContainedPath (root, destination);
                    destination = GetUniquePath (
                        destination, reserved_destinations);
                    ApplyMove (result, "file", file, destination, dry_run);
                }
            }
            result.Success = 0 == result.Failed;
            return result;
        }

        public static HxV4FileOperationResult RenameExtractedTree (
            string root_directory, string names_file, bool dry_run)
        {
            Dictionary<string, string> names;
            string error;
            if (!TryReadNamesFile (names_file, out names, out error))
                throw new InvalidDataException (error);
            var root = RequireDirectory (root_directory);
            var result = new HxV4FileOperationResult {
                Root = root,
                DryRun = dry_run,
            };
            var reserved_destinations = new HashSet<string> (
                StringComparer.OrdinalIgnoreCase);

            foreach (var file in EnumerateFilesSafe (root).ToArray())
            {
                string plain_name;
                if (!names.TryGetValue (Path.GetFileName (file), out plain_name)
                    || string.IsNullOrWhiteSpace (plain_name))
                {
                    ++result.Ignored;
                    continue;
                }
                if (plain_name.IndexOfAny (new[] {
                        Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar
                    }) >= 0
                    || Path.IsPathRooted (plain_name))
                {
                    AddFailure (result, "file", file, null,
                                "HxNames file-name mapping contains a path.");
                    continue;
                }
                var destination = EnsureContainedPath (
                    root, Path.Combine (Path.GetDirectoryName (file), plain_name));
                destination = GetUniquePath (
                    destination, reserved_destinations);
                ApplyMove (result, "file", file, destination, dry_run);
            }

            var directories = EnumerateDirectoriesSafe (root)
                .OrderByDescending (x => x.Length).ToArray();
            foreach (var directory in directories)
            {
                if (!Directory.Exists (directory))
                    continue;
                string plain_path;
                if (!names.TryGetValue (Path.GetFileName (directory), out plain_path)
                    || string.IsNullOrWhiteSpace (plain_path))
                {
                    ++result.Ignored;
                    continue;
                }
                plain_path = plain_path.Replace ('/', Path.DirectorySeparatorChar)
                    .TrimEnd (Path.DirectorySeparatorChar);
                if (!IsSafeRelativePath (plain_path))
                {
                    AddFailure (result, "directory", directory, null,
                                "HxNames path mapping is unsafe.");
                    continue;
                }
                var destination = EnsureContainedPath (
                    root, Path.Combine (Path.GetDirectoryName (directory), plain_path));
                if (destination.StartsWith (
                    directory + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
                {
                    AddFailure (result, "directory", directory, destination,
                                "A directory cannot be moved into itself.");
                    continue;
                }
                ++result.Planned;
                if (dry_run)
                {
                    result.Items.Add (new HxV4FileOperationItem {
                        Kind = "directory",
                        Source = directory,
                        Destination = destination,
                        Status = "planned",
                    });
                    continue;
                }
                try
                {
                    Directory.CreateDirectory (Path.GetDirectoryName (destination));
                    if (Directory.Exists (destination))
                        MergeDirectory (directory, destination);
                    else
                        Directory.Move (directory, destination);
                    ++result.Changed;
                    result.Items.Add (new HxV4FileOperationItem {
                        Kind = "directory",
                        Source = directory,
                        Destination = destination,
                        Status = "renamed",
                    });
                }
                catch (Exception X)
                {
                    AddFailure (result, "directory", directory, destination, X.Message, false);
                }
            }
            result.Success = 0 == result.Failed;
            return result;
        }

        static void ApplyMove (HxV4FileOperationResult result, string kind,
                               string source, string destination, bool dry_run)
        {
            ++result.Planned;
            if (dry_run)
            {
                result.Items.Add (new HxV4FileOperationItem {
                    Kind = kind,
                    Source = source,
                    Destination = destination,
                    Status = "planned",
                });
                return;
            }
            try
            {
                Directory.CreateDirectory (Path.GetDirectoryName (destination));
                File.Move (source, destination);
                ++result.Changed;
                result.Items.Add (new HxV4FileOperationItem {
                    Kind = kind,
                    Source = source,
                    Destination = destination,
                    Status = "renamed",
                });
            }
            catch (Exception X)
            {
                AddFailure (result, kind, source, destination, X.Message, false);
            }
        }

        static void AddFailure (HxV4FileOperationResult result, string kind,
                                string source, string destination, string message,
                                bool count_planned = true)
        {
            if (count_planned)
                ++result.Planned;
            ++result.Failed;
            result.Items.Add (new HxV4FileOperationItem {
                Kind = kind,
                Source = source,
                Destination = destination,
                Status = "failed",
                Message = message,
            });
        }

        static void MergeDirectory (string source, string destination)
        {
            foreach (var child_directory in Directory.EnumerateDirectories (
                source, "*", SearchOption.TopDirectoryOnly).ToArray())
            {
                var target = Path.Combine (destination, Path.GetFileName (child_directory));
                if (Directory.Exists (target))
                    MergeDirectory (child_directory, target);
                else if (File.Exists (target))
                    Directory.Move (child_directory, GetUniquePath (target));
                else
                    Directory.Move (child_directory, target);
            }
            foreach (var child_file in Directory.EnumerateFiles (
                source, "*", SearchOption.TopDirectoryOnly).ToArray())
            {
                var target = Path.Combine (destination, Path.GetFileName (child_file));
                if (!File.Exists (target) && !Directory.Exists (target))
                {
                    File.Move (child_file, target);
                }
                else if (File.Exists (target) && FilesIdentical (child_file, target))
                {
                    File.Delete (child_file);
                }
                else
                {
                    File.Move (child_file, GetUniquePath (target));
                }
            }
            if (!Directory.EnumerateFileSystemEntries (source).Any())
                Directory.Delete (source);
        }

        static bool FilesIdentical (string first, string second)
        {
            var first_info = new FileInfo (first);
            var second_info = new FileInfo (second);
            if (first_info.Length != second_info.Length)
                return false;
            const int buffer_size = 8192;
            var first_buffer = new byte[buffer_size];
            var second_buffer = new byte[buffer_size];
            using (var first_stream = File.OpenRead (first))
            using (var second_stream = File.OpenRead (second))
            {
                for (;;)
                {
                    int first_read = first_stream.Read (
                        first_buffer, 0, first_buffer.Length);
                    int second_read = second_stream.Read (
                        second_buffer, 0, second_buffer.Length);
                    if (first_read != second_read)
                        return false;
                    if (0 == first_read)
                        return true;
                    for (int i = 0; i < first_read; ++i)
                    {
                        if (first_buffer[i] != second_buffer[i])
                            return false;
                    }
                }
            }
        }

        static string GetUniquePath (string path)
        {
            return GetUniquePath (path, null);
        }

        static string GetUniquePath (string path, ISet<string> reserved)
        {
            if (!File.Exists (path) && !Directory.Exists (path)
                && (null == reserved || reserved.Add (path)))
                return path;
            var directory = Path.GetDirectoryName (path);
            var extension = Path.GetExtension (path);
            var stem = Path.GetFileNameWithoutExtension (path);
            for (int i = 1; ; ++i)
            {
                var candidate = Path.Combine (
                    directory, stem + "_" + i.ToString (CultureInfo.InvariantCulture) + extension);
                if (!File.Exists (candidate) && !Directory.Exists (candidate)
                    && (null == reserved || reserved.Add (candidate)))
                    return candidate;
            }
        }

        static IEnumerable<string> EnumerateDirectoriesSafe (string root)
        {
            var pending = new Stack<string>();
            pending.Push (root);
            while (pending.Count > 0)
            {
                var current = pending.Pop();
                foreach (var directory in Directory.EnumerateDirectories (
                    current, "*", SearchOption.TopDirectoryOnly))
                {
                    if (IsReparsePoint (directory))
                        continue;
                    pending.Push (directory);
                    yield return directory;
                }
            }
        }

        static IEnumerable<string> EnumerateFilesSafe (string root)
        {
            foreach (var file in Directory.EnumerateFiles (
                root, "*", SearchOption.TopDirectoryOnly))
                yield return file;
            foreach (var directory in EnumerateDirectoriesSafe (root))
            {
                foreach (var file in Directory.EnumerateFiles (
                    directory, "*", SearchOption.TopDirectoryOnly))
                    yield return file;
            }
        }

        static bool IsReparsePoint (string path)
        {
            try
            {
                return 0 != (File.GetAttributes (path) & FileAttributes.ReparsePoint);
            }
            catch
            {
                return true;
            }
        }

        static string RequireDirectory (string path)
        {
            if (string.IsNullOrWhiteSpace (path))
                throw new ArgumentNullException ("path");
            path = Path.GetFullPath (path);
            if (!Directory.Exists (path))
                throw new DirectoryNotFoundException (path);
            return path.TrimEnd (Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        static string RequireFile (string path)
        {
            if (string.IsNullOrWhiteSpace (path))
                throw new ArgumentNullException ("path");
            path = Path.GetFullPath (path);
            if (!File.Exists (path))
                throw new FileNotFoundException ("Hx v4 source file was not found.", path);
            return path;
        }

        static string GetRelativePath (string root, string path)
        {
            var root_uri = new Uri (AppendDirectorySeparator (Path.GetFullPath (root)));
            var path_uri = new Uri (Path.GetFullPath (path));
            return Uri.UnescapeDataString (
                root_uri.MakeRelativeUri (path_uri).ToString()).Replace ('/', Path.DirectorySeparatorChar);
        }

        static string AppendDirectorySeparator (string path)
        {
            return path.EndsWith (Path.DirectorySeparatorChar.ToString(),
                                  StringComparison.Ordinal)
                ? path : path + Path.DirectorySeparatorChar;
        }

        static string NormalizePathCandidate (string path)
        {
            path = (path ?? string.Empty).Trim().Replace ('\\', '/');
            if (string.IsNullOrEmpty (path) || "/" == path)
                return "/";
            return path.Trim ('/') + "/";
        }

        static bool IsUpstreamHash (string value, int length)
        {
            return null != value && value.Length == length
                && value.All (c => char.IsDigit (c) || char.IsUpper (c));
        }

        static bool IsHex (string value)
        {
            return !string.IsNullOrEmpty (value) && value.All (
                c => (c >= '0' && c <= '9') || (c >= 'A' && c <= 'F')
                  || (c >= 'a' && c <= 'f'));
        }

        static bool IsSafeRelativePath (string path)
        {
            if (string.IsNullOrWhiteSpace (path) || Path.IsPathRooted (path))
                return false;
            return path.Split (new[] {
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar
                }, StringSplitOptions.RemoveEmptyEntries)
                .All (x => "." != x && ".." != x);
        }

        static string EnsureContainedPath (string root, string path)
        {
            var full_root = AppendDirectorySeparator (Path.GetFullPath (root));
            var full_path = Path.GetFullPath (path);
            if (!full_path.StartsWith (full_root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException ("Hx v4 operation escaped its root directory.");
            return full_path;
        }

        static void ThrowIfCancellationRequested (
            Func<bool> cancellation_requested)
        {
            if (null != cancellation_requested && cancellation_requested())
                throw new OperationCanceledException ("Operation canceled.");
        }

        static void ScanBgvVoiceSequences (
            string file, IDictionary<string, VoiceSequenceInfo> sequences)
        {
            string text;
            using (var reader = new StreamReader (
                file, Encoding.Unicode, true))
                text = reader.ReadToEnd();
            foreach (var row in HxResourceNameScanner.ParseCsv (text))
            {
                if (row.Count < 3
                    || HxResourceNameScanner.CleanCell (row[0]).StartsWith (
                        "#", StringComparison.Ordinal))
                    continue;
                RememberVoiceSequence (
                    HxResourceNameScanner.CleanCell (row[2]), sequences);
            }
        }

        static void RememberVoiceSequence (
            string voice_stem, IDictionary<string, VoiceSequenceInfo> sequences)
        {
            if (string.IsNullOrEmpty (voice_stem))
                return;
            int separator = voice_stem.LastIndexOf ('_');
            if (separator <= 0 || separator+1 >= voice_stem.Length)
                return;
            var suffix = voice_stem.Substring (separator+1);
            var match = Regex.Match (suffix, @"^\d+");
            if (!match.Success)
                return;
            int number;
            if (!int.TryParse (
                match.Value, NumberStyles.None, CultureInfo.InvariantCulture,
                out number))
                return;
            var prefix = voice_stem.Substring (0, separator);
            VoiceSequenceInfo sequence;
            if (!sequences.TryGetValue (prefix, out sequence))
            {
                sequence = new VoiceSequenceInfo {
                    Width = match.Value.Length,
                    Maximum = number,
                };
                sequences[prefix] = sequence;
            }
            else if (number > sequence.Maximum)
            {
                sequence.Maximum = number;
            }
        }

        sealed class VoiceSequenceInfo
        {
            public int Width;
            public int Maximum;
        }
    }
}
