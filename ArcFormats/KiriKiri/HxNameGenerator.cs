//! \file       HxNameGenerator.cs
//! \date       2026 Jul 26
//! \brief      Hx v4 name candidate generation.
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
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using GameRes.Formats.Emote;
using GameRes.Formats.Strings;

namespace GameRes.Formats.KiriKiri
{
    public sealed class HxNameGenerationResult
    {
        public bool Success;
        public string NamesFile;
        public string Error;
        public string FailureReason;
        public int IndexArchivesTried;
        public int ArchiveCount;
        public int ResourceCount;
        public int LooseFileCount;
        public int ScannedEntryCount;
        public int ScenarioCount;
        public int CandidateCount;
        public int PathMatches;
        public int NameMatches;
    }

    /// <summary>
    /// Builds an HxNames table by hashing names found in decrypted game resources
    /// and retaining only candidates present in an Hx v4 archive index.
    /// </summary>
    public static class HxNameGenerator
    {
        internal const uint PsbSignature = 0x00425350; // 'PSB'

        static readonly Regex FileNameRe = new Regex (
            @"(?<name>[^""'<>\|\r\n\t]+?\.(?:ogg|opus|wav|mp3|sli|mchx|ini|png|tlg|jpg|jpeg|webp|bmp|psd|pimg|scn|ks|tjs|csv|psb|pbd|stand|sinfo|stage|mtn|asd|mp4|m2v|wmv|avi))(?=$|[\s""'<>,;)\]])",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        static readonly Regex VoiceNameRe = new Regex (
            @"(?<![A-Za-z0-9])(?<name>[A-Za-z][A-Za-z0-9]*_[0-9]{3}_[0-9]{3,5})(?![A-Za-z0-9])",
            RegexOptions.Compiled);
        static readonly Regex IdentifierRe = new Regex (
            @"^[A-Za-z0-9_@+\-./\\]+$", RegexOptions.Compiled);
        static readonly Regex NumericVoiceRe = new Regex (
            @"^(?<prefix>.+_)(?<number>[0-9]+)",
            RegexOptions.Compiled);

        static readonly HashSet<string> AudioContexts = new HashSet<string> (
            new[] {
                "voice", "playvoice", "loopvoice", "loopvoicelist", "sound",
                "se", "se2", "lse", "lse2", "sysse", "bgm", "bgv", "bg_voice",
                "live", "liveout",
            },
            StringComparer.OrdinalIgnoreCase);
        static readonly HashSet<string> ImageContexts = new HashSet<string> (
            new[] {
                "image", "event", "ev", "stand", "face", "icon", "stamp",
                "stage", "background", "bg", "clip", "character", "msgwin",
            },
            StringComparer.OrdinalIgnoreCase);
        static readonly HashSet<string> ScenarioContexts = new HashSet<string> (
            new[] { "scenario", "scene", "script", "storage" },
            StringComparer.OrdinalIgnoreCase);
        static readonly HashSet<string> DirectiveContexts = new HashSet<string> (
            AudioContexts.Concat (ImageContexts).Concat (ScenarioContexts),
            StringComparer.OrdinalIgnoreCase);

        static readonly string[] SystemVoiceSuffixes = {
            "after", "attention0", "attention1", "attention2", "attention3",
            "backlog", "chart", "config", "config_easy", "custom", "dialog",
            "end", "extra", "extra_bu", "extra_cg", "extra_scene", "game",
            "game2", "goodbye", "jump", "load", "mouse", "pad", "rec",
            "reset", "save", "shortcut", "sound", "text", "tittle",
            "tittleback", "title", "titleback", "voice", "volume", "window",
            "yuzu",
        };

        public static HxNameGenerationResult Generate (string source_file, HxCrypt crypt,
                                                        IDictionary<string, string> logged_names,
                                                        string output_file,
                                                        Action<ResourceProgressInfo> progress_reporter = null)
        {
            var result = new HxNameGenerationResult { NamesFile = output_file };
            try
            {
                ReportProgress (progress_reporter, 2, Text ("HxNamesProgressPreparing"),
                    "prepare", new Dictionary<string, object>());
                if (string.IsNullOrEmpty (source_file) || !File.Exists (source_file))
                    throw new FileNotFoundException ("Source XP3 was not found.", source_file);
                if (null == crypt)
                    throw new ArgumentNullException ("crypt");
                if (string.IsNullOrEmpty (output_file))
                    throw new ArgumentNullException ("output_file");

                var game_directory = Path.GetDirectoryName (Path.GetFullPath (source_file));
                var archive_files = Directory.GetFiles (game_directory, "*.xp3")
                    .OrderBy (x => x, StringComparer.OrdinalIgnoreCase).ToArray();
                result.IndexArchivesTried = archive_files.Length;
                var target_hashes = ReadTargetHashes (archive_files, crypt, result, progress_reporter);
                if (0 == target_hashes.PathHashes.Count && 0 == target_hashes.NameHashes.Count)
                {
                    result.FailureReason = "no_readable_index";
                    result.Error = Text ("HxNamesIndexReadFailed");
                    return result;
                }

                var candidates = new CandidateCollector (target_hashes);
                candidates.AddPath ("/");
                foreach (var name in HxV4Tools.InitialNames)
                    candidates.AddName (name);
                foreach (var archive in archive_files)
                    candidates.AddPath (Path.GetFileNameWithoutExtension (archive) + "/");
                SeedNamesFile (output_file, candidates);

                if (null != logged_names)
                {
                    foreach (var pair in logged_names)
                        candidates.AddKnownMapping (pair.Key, pair.Value);
                }

                var scanner = new HxResourceNameScanner (candidates);
                ScanLooseResources (game_directory, scanner, candidates, result, progress_reporter);
                ScanResourceArchives (archive_files, crypt, scanner, candidates, result, progress_reporter);
                scanner.Complete();
                ReportCandidateProgress (
                    progress_reporter, 78, "voice_sequences", candidates, result);
                candidates.ExpandVoiceSequences();
                ReportCandidateProgress (
                    progress_reporter, 86, "system_voices", candidates, result);
                candidates.ExpandSystemVoices();
                ReportCandidateProgress (
                    progress_reporter, 93, "common_paths", candidates, result);
                candidates.AddCommonPaths();

                ReportProgress (progress_reporter, 97, string.Format (
                    Text ("HxNamesProgressWriting"), candidates.CandidateCount, candidates.Matches.Count),
                    "write", new Dictionary<string, object> {
                        { "candidateCount", candidates.CandidateCount },
                        { "matchCount", candidates.Matches.Count },
                        { "scannedEntryCount", result.ScannedEntryCount },
                        { "parsedResourceCount", result.ResourceCount },
                    });
                result.CandidateCount = candidates.CandidateCount;
                result.PathMatches = candidates.Matches.Keys.Count (x => x.Length == 16);
                result.NameMatches = candidates.Matches.Keys.Count (x => x.Length == 64);
                result.Success = result.PathMatches + result.NameMatches > 0;
                if (!result.Success)
                {
                    result.FailureReason = "no_name_matches";
                    result.Error = "No generated candidates matched an Hx v4 index.";
                    return result;
                }
                WriteNamesFile (output_file, candidates.Matches);

                Trace.WriteLine (string.Format (CultureInfo.InvariantCulture,
                    "Generated HxNames. archives={0}, resources={1}, looseFiles={2}, entries={3}, scenarios={4}, candidates={5}, pathMatches={6}, nameMatches={7}, file='{8}'",
                    result.ArchiveCount, result.ResourceCount, result.LooseFileCount,
                    result.ScannedEntryCount, result.ScenarioCount, result.CandidateCount,
                    result.PathMatches, result.NameMatches, output_file), "[HxNames]");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception X)
            {
                result.Success = false;
                if (string.IsNullOrEmpty (result.FailureReason))
                    result.FailureReason = "generation_failed";
                result.Error = X.Message;
                Trace.WriteLine (string.Format ("HxNames generation failed for '{0}': {1}",
                                                source_file, X), "[HxNames]");
            }
            return result;
        }

        static void SeedNamesFile (string names_file, CandidateCollector candidates)
        {
            if (string.IsNullOrEmpty (names_file) || !File.Exists (names_file))
                return;
            try
            {
                foreach (var line in File.ReadLines (names_file))
                {
                    var value = line.Trim();
                    if (0 == value.Length || value.StartsWith ("#", StringComparison.Ordinal)
                        || value.StartsWith (";", StringComparison.Ordinal))
                        continue;
                    int separator = value.IndexOf (':');
                    if (separator <= 0)
                        continue;
                    candidates.AddKnownMapping (
                        value.Substring (0, separator), value.Substring (separator+1));
                }
            }
            catch (Exception X)
            {
                Trace.WriteLine (string.Format (
                    "Could not seed Hx name generation from previous cache '{0}': {1}",
                    names_file, X.Message), "[HxNames]");
            }
        }

        static HxIndexHashSet ReadTargetHashes (string[] archive_files, HxCrypt crypt,
                                                 HxNameGenerationResult result,
                                                 Action<ResourceProgressInfo> progress_reporter)
        {
            var target = new HxIndexHashSet();
            for (int i = 0; i < archive_files.Length; ++i)
            {
                var archive = archive_files[i];
                try
                {
                    var index = KrkrDumpResultImporter.ReadHxIndex (archive);
                    if (null != index)
                    {
                        var hashes = crypt.ReadIndexHashes (
                            Path.GetFileName (archive), index);
                        if (null != hashes)
                        {
                            target.PathHashes.UnionWith (hashes.PathHashes);
                            target.NameHashes.UnionWith (hashes.NameHashes);
                            ++result.ArchiveCount;
                        }
                    }
                }
                catch (Exception X)
                {
                    Trace.WriteLine (string.Format ("Could not read Hx v4 index '{0}': {1}",
                                                    archive, X.Message), "[HxNames]");
                }
                ReportProgress (progress_reporter,
                    2 + (archive_files.Length > 0 ? (i+1) * 10 / archive_files.Length : 10),
                    string.Format (Text ("HxNamesProgressIndexes"), i+1, archive_files.Length,
                                   result.ArchiveCount),
                    "read_indexes", new Dictionary<string, object> {
                        { "archiveIndex", i+1 },
                        { "archiveCount", archive_files.Length },
                        { "readableIndexCount", result.ArchiveCount },
                    });
            }
            return target;
        }

        static void ScanLooseResources (string game_directory, HxResourceNameScanner scanner,
                                        CandidateCollector candidates, HxNameGenerationResult result,
                                        Action<ResourceProgressInfo> progress_reporter)
        {
            const int max_files = 100000;
            var pending = new Stack<string>();
            pending.Push (game_directory);
            int visited = 0;
            while (pending.Count > 0 && visited < max_files)
            {
                var directory = pending.Pop();
                try
                {
                    foreach (var child in Directory.EnumerateDirectories (directory))
                    {
                        var name = Path.GetFileName (child);
                        if (ShouldSkipLooseDirectory (name))
                            continue;
                        try
                        {
                            if (0 != (File.GetAttributes (child) & FileAttributes.ReparsePoint))
                                continue;
                        }
                        catch
                        {
                            continue;
                        }
                        var relative_directory = child.Substring (game_directory.Length)
                            .TrimStart (Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                        if (HxResourceNameScanner.IsUsefulPlainPath (relative_directory))
                            candidates.AddPath (relative_directory);
                        pending.Push (child);
                    }
                    foreach (var file in Directory.EnumerateFiles (directory))
                    {
                        if (visited++ >= max_files)
                            break;
                        if (".xp3".Equals (Path.GetExtension (file), StringComparison.OrdinalIgnoreCase))
                            continue;
                        var relative = file.Substring (game_directory.Length)
                            .TrimStart (Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                        if (HxResourceNameScanner.IsUsefulPlainName (relative))
                        {
                            candidates.AddName (relative);
                            ++result.LooseFileCount;
                        }
                        if (!ShouldInspectLooseFile (file))
                            continue;
                        try
                        {
                            using (var input = BinaryStream.FromFile (file))
                            {
                                var scan = scanner.Scan (input, relative);
                                if (scan.Parsed)
                                    ++result.ResourceCount;
                                if (scan.Scenario)
                                    ++result.ScenarioCount;
                            }
                        }
                        catch (Exception X)
                        {
                            Trace.WriteLine (string.Format (
                                "Could not inspect loose Hx name resource '{0}': {1}",
                                file, X.Message), "[HxNames]");
                        }
                        if (0 == (visited & 0xFF))
                        {
                            ReportProgress (progress_reporter, Math.Min (19, 12 + visited / 15000),
                                string.Format (Text ("HxNamesProgressLoose"), visited,
                                               result.ResourceCount),
                                "scan_loose", new Dictionary<string, object> {
                                    { "scannedFileCount", visited },
                                    { "parsedResourceCount", result.ResourceCount },
                                    { "candidateCount", candidates.CandidateCount },
                                });
                        }
                    }
                }
                catch (Exception X)
                {
                    Trace.WriteLine (string.Format (
                        "Could not enumerate loose Hx name resources in '{0}': {1}",
                        directory, X.Message), "[HxNames]");
                }
            }
            ReportProgress (progress_reporter, 20, string.Format (
                Text ("HxNamesProgressLoose"), visited, result.ResourceCount),
                "scan_loose", new Dictionary<string, object> {
                    { "scannedFileCount", visited },
                    { "parsedResourceCount", result.ResourceCount },
                    { "candidateCount", candidates.CandidateCount },
                });
        }

        static bool ShouldSkipLooseDirectory (string name)
        {
            return string.IsNullOrEmpty (name)
                || ".git".Equals (name, StringComparison.OrdinalIgnoreCase)
                || ".svn".Equals (name, StringComparison.OrdinalIgnoreCase)
                || "node_modules".Equals (name, StringComparison.OrdinalIgnoreCase);
        }

        internal static bool ShouldInspectLooseFile (string file)
        {
            switch (Path.GetExtension (file).ToLowerInvariant())
            {
            case ".psb":
            case ".scn":
            case ".mdf":
            case ".pbd":
            case ".pimg":
            case ".mtn":
            case ".stand":
            case ".csv":
            case ".stage":
            case ".ks":
                return true;
            default:
                return false;
            }
        }

        static void ScanResourceArchives (IEnumerable<string> archive_files, HxCrypt crypt,
                                          HxResourceNameScanner scanner,
                                          CandidateCollector candidates,
                                          HxNameGenerationResult result,
                                          Action<ResourceProgressInfo> progress_reporter)
        {
            var inputs = archive_files.OrderBy (ArchiveScanPriority)
                .ThenBy (x => x, StringComparer.OrdinalIgnoreCase).ToArray();
            if (0 == inputs.Length)
            {
                ReportProgress (progress_reporter, 75, Text ("HxNamesProgressNoResources"),
                    "scan_entries", new Dictionary<string, object> {
                        { "archiveIndex", 0 },
                        { "archiveCount", 0 },
                        { "scannedEntryCount", result.ScannedEntryCount },
                        { "parsedResourceCount", result.ResourceCount },
                        { "candidateCount", candidates.CandidateCount },
                    });
                return;
            }

            using (Xp3Opener.PushThreadTransientScheme (crypt, inputs[0], true))
            {
                for (int i = 0; i < inputs.Length; ++i)
                {
                    int archive_number = i + 1;
                    var archive = inputs[i];
                    ArcView view = null;
                    ArcFile arc = null;
                    try
                    {
                        view = new ArcView (archive);
                        var opener = new Xp3Opener { ForceEncryptionQuery = true };
                        arc = opener.TryOpen (view);
                        if (null == arc)
                            continue;
                        view = null;
                        int processed = 0;
                        int total = arc.Dir.Count;
                        foreach (var entry in arc.Dir)
                        {
                            try
                            {
                                if (HxResourceNameScanner.IsUsefulPlainName (entry.Name))
                                    candidates.AddName (entry.Name);
                                var xp3_entry = entry as Xp3Entry;
                                var hx_entry = null != xp3_entry
                                    ? xp3_entry.Extra as HxEntry
                                    : null;
                                if (!ShouldInspectArchiveEntry (entry.Name, hx_entry))
                                    continue;
                                using (var input = arc.OpenBinaryEntry (entry))
                                {
                                    var scan = scanner.Scan (input, entry.Name,
                                        null != hx_entry ? hx_entry.NameHash : null);
                                    if (scan.Parsed)
                                        ++result.ResourceCount;
                                    if (scan.Scenario)
                                        ++result.ScenarioCount;
                                }
                            }
                            catch (Exception X)
                            {
                                Trace.WriteLine (string.Format (
                                    "Could not inspect Hx name resource '{0}' in '{1}': {2}",
                                    entry.Name, archive, X.Message), "[HxNames]");
                            }
                            finally
                            {
                                ++processed;
                                ++result.ScannedEntryCount;
                                double archive_progress = total > 0
                                    ? (double)processed / total : 1;
                                int percentage = 20 + (int)(55 * (i + archive_progress) / inputs.Length);
                                ReportProgress (progress_reporter, percentage, string.Format (
                                    Text ("HxNamesProgressResources"), Path.GetFileName (archive),
                                    archive_number, inputs.Length, processed, total,
                                    result.ResourceCount),
                                    "scan_entries", new Dictionary<string, object> {
                                        { "archiveName", Path.GetFileName (archive) },
                                        { "archiveIndex", archive_number },
                                        { "archiveCount", inputs.Length },
                                        { "entryIndex", processed },
                                        { "entryCount", total },
                                        { "scannedEntryCount", result.ScannedEntryCount },
                                        { "parsedResourceCount", result.ResourceCount },
                                        { "candidateCount", candidates.CandidateCount },
                                        { "matchCount", candidates.Matches.Count },
                                    });
                            }
                        }
                    }
                    catch (Exception X)
                    {
                        Trace.WriteLine (string.Format (
                            "Could not scan Hx name resource archive '{0}': {1}",
                            archive, X.Message), "[HxNames]");
                    }
                    finally
                    {
                        if (null != arc)
                            arc.Dispose();
                        else if (null != view)
                            view.Dispose();
                    }
                }
            }
        }

        static int ArchiveScanPriority (string archive)
        {
            var name = Path.GetFileNameWithoutExtension (archive);
            if (name.IndexOf ("scn", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf ("scenario", StringComparison.OrdinalIgnoreCase) >= 0)
                return 0;
            if (name.IndexOf ("config", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf ("system", StringComparison.OrdinalIgnoreCase) >= 0)
                return 1;
            return 2;
        }

        static bool ShouldInspectArchiveEntry (string name, HxEntry hx_entry)
        {
            if (null != hx_entry && string.IsNullOrEmpty (hx_entry.Name))
                return true;
            switch (Path.GetExtension (name ?? string.Empty).ToLowerInvariant())
            {
            case ".psb":
            case ".scn":
            case ".mdf":
            case ".pbd":
            case ".pimg":
            case ".mtn":
            case ".stand":
            case ".csv":
            case ".stage":
            case ".ks":
            case ".tjs":
                return true;
            default:
                return false;
            }
        }

        static void ReportCandidateProgress (Action<ResourceProgressInfo> progress_reporter,
                                              int percentage, string stage,
                                              CandidateCollector candidates,
                                              HxNameGenerationResult result)
        {
            ReportProgress (progress_reporter, percentage, string.Format (
                Text ("HxNamesProgressCandidates"), candidates.CandidateCount, candidates.Matches.Count),
                "expand_candidates", new Dictionary<string, object> {
                    { "stage", stage },
                    { "candidateCount", candidates.CandidateCount },
                    { "matchCount", candidates.Matches.Count },
                    { "scannedEntryCount", result.ScannedEntryCount },
                    { "parsedResourceCount", result.ResourceCount },
                });
        }

        static void ReportProgress (Action<ResourceProgressInfo> progress_reporter,
                                    int percentage, string message, string phase,
                                    IDictionary<string, object> details)
        {
            if (null == progress_reporter)
                return;
            try
            {
                progress_reporter (new ResourceProgressInfo {
                    Percentage = percentage,
                    Message = message,
                    Phase = phase,
                    Details = details,
                });
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception X)
            {
                Trace.WriteLine ("HxNames progress reporter failed: " + X.Message, "[HxNames]");
            }
        }

        static string Text (string name)
        {
            return arcStrings.ResourceManager.GetString (name) ?? name;
        }

        internal static void WriteNamesFile (string output_file, IDictionary<string, string> names)
        {
            var directory = Path.GetDirectoryName (Path.GetFullPath (output_file));
            Directory.CreateDirectory (directory);
            var temporary_file = output_file + "." + Guid.NewGuid().ToString ("N") + ".tmp";
            try
            {
                using (var writer = new StreamWriter (temporary_file, false, new UTF8Encoding (false)))
                {
                    foreach (var pair in names.OrderBy (x => x.Key, StringComparer.Ordinal))
                        writer.WriteLine ("{0}:{1}", pair.Key, pair.Value);
                }
                if (File.Exists (output_file))
                    File.Replace (temporary_file, output_file, null);
                else
                    File.Move (temporary_file, output_file);
            }
            finally
            {
                if (File.Exists (temporary_file))
                    File.Delete (temporary_file);
            }
        }

        internal sealed class CandidateCollector
        {
            readonly HxIndexHashSet m_targets;
            readonly HashSet<string> m_names = new HashSet<string> (StringComparer.Ordinal);
            readonly HashSet<string> m_paths = new HashSet<string> (StringComparer.Ordinal);
            readonly HashSet<string> m_voice_names = new HashSet<string> (StringComparer.OrdinalIgnoreCase);
            readonly HashSet<string> m_system_prefixes = new HashSet<string> (StringComparer.OrdinalIgnoreCase);
            readonly Dictionary<string, VoiceSequence> m_voice_sequences =
                new Dictionary<string, VoiceSequence> (StringComparer.OrdinalIgnoreCase);
            readonly HxV4Hash.FileNameHasher m_file_hasher = new HxV4Hash.FileNameHasher();

            public readonly Dictionary<string, string> Matches =
                new Dictionary<string, string> (StringComparer.OrdinalIgnoreCase);

            public int CandidateCount { get { return m_names.Count + m_paths.Count; } }
            public IEnumerable<string> Names { get { return m_names; } }
            public IEnumerable<string> Paths { get { return m_paths; } }

            public CandidateCollector (HxIndexHashSet targets)
            {
                m_targets = targets;
            }

            public bool TryResolveName (string hash, out string name)
            {
                return Matches.TryGetValue (hash ?? string.Empty, out name);
            }

            public void AddSystemVoicePrefix (string prefix)
            {
                prefix = CleanCandidate (prefix);
                if (!string.IsNullOrEmpty (prefix) && IdentifierRe.IsMatch (prefix)
                    && prefix.Length <= 24)
                    m_system_prefixes.Add (prefix);
            }

            public void AddKnownMapping (string hash, string value)
            {
                if (string.IsNullOrWhiteSpace (hash))
                    return;
                hash = hash.Trim().ToUpperInvariant();
                if (hash.Length == 16
                    && (null == m_targets || m_targets.PathHashes.Contains (hash)))
                {
                    var path = value ?? string.Empty;
                    if (string.IsNullOrEmpty (path)
                        && hash.Equals (HxV4Hash.GetPathHash ("/"),
                                        StringComparison.OrdinalIgnoreCase))
                        path = "/";
                    Matches[hash] = path;
                    if (!string.IsNullOrEmpty (path))
                        AddPath (path);
                }
                else if (hash.Length == 64
                         && (null == m_targets || m_targets.NameHashes.Contains (hash))
                         && !string.IsNullOrEmpty (value))
                {
                    Matches[hash] = value;
                    AddName (value);
                }
            }

            public void AddName (string name)
            {
                name = CleanCandidate (name);
                if (string.IsNullOrEmpty (name) || name.Length > 260)
                    return;
                int separator = Math.Max (name.LastIndexOf ('/'), name.LastIndexOf ('\\'));
                if (separator >= 0)
                {
                    AddPath (name.Substring (0, separator+1));
                    name = name.Substring (separator+1);
                }
                if (string.IsNullOrEmpty (name))
                    return;
                AddNameCore (name);
                var lower = name.ToLowerInvariant();
                if (lower != name)
                    AddNameCore (lower);
                RememberVoiceName (name);
            }

            void AddNameCore (string name)
            {
                if (!m_names.Add (name))
                    return;
                var hash = m_file_hasher.GetHash (name);
                if ((null == m_targets || m_targets.NameHashes.Contains (hash))
                    && !Matches.ContainsKey (hash))
                    Matches[hash] = name;
            }

            public void AddPath (string path)
            {
                path = CleanCandidate (path);
                if (string.IsNullOrEmpty (path))
                    path = "/";
                path = path.Replace ('\\', '/');
                if (path != "/" && !path.EndsWith ("/", StringComparison.Ordinal))
                    path += "/";
                if (path.Length > 260)
                    return;
                AddPathCore (path);
                var lower = path.ToLowerInvariant();
                if (lower != path)
                    AddPathCore (lower);
            }

            void AddPathCore (string path)
            {
                if (!m_paths.Add (path))
                    return;
                var hash = HxV4Hash.GetPathHash (path);
                if ((null == m_targets || m_targets.PathHashes.Contains (hash))
                    && !Matches.ContainsKey (hash))
                    Matches[hash] = path;
            }

            public void Walk (object value, string context)
            {
                var text = value as string;
                if (null != text)
                {
                    ProcessString (text, context);
                    return;
                }
                var dict = value as IDictionary;
                if (null != dict)
                {
                    string dict_context = context;
                    if (dict.Contains ("class"))
                    {
                        var class_name = dict["class"] as string;
                        if (!string.IsNullOrEmpty (class_name)
                            && DirectiveContexts.Contains (class_name))
                            dict_context = class_name;
                    }
                    foreach (DictionaryEntry item in dict)
                    {
                        var key = item.Key as string;
                        var item_text = item.Value as string;
                        if (null != item_text)
                        {
                            if ("icon".Equals (key, StringComparison.OrdinalIgnoreCase))
                                AddName ("chaticon_" + item_text + ".png");
                            else if ("stamp".Equals (key, StringComparison.OrdinalIgnoreCase)
                                     && !string.IsNullOrWhiteSpace (item_text)
                                     && !"null".Equals (item_text, StringComparison.OrdinalIgnoreCase))
                                AddName (item_text + ".png");
                            else if ("stage".Equals (key, StringComparison.OrdinalIgnoreCase))
                            {
                                AddImageVariants (item_text);
                                var stage = RemoveExtension (CleanCandidate (item_text),
                                    new[] { ".png", ".tlg", ".jpg", ".jpeg", ".webp", ".bmp", ".psd", ".pimg" });
                                if (!string.IsNullOrEmpty (stage))
                                    AddName ("bgthum_" + stage + ".jpg");
                            }
                            else if ("file".Equals (key, StringComparison.OrdinalIgnoreCase)
                                     || "filename".Equals (key, StringComparison.OrdinalIgnoreCase))
                                ProcessString (item_text, dict_context);
                            else if ("storage".Equals (key, StringComparison.OrdinalIgnoreCase)
                                     && AudioContexts.Contains (dict_context))
                                AddAudioVariants (item_text, false);
                        }
                        Walk (item.Value, key ?? dict_context);
                    }
                    return;
                }
                var list = value as IList;
                if (null == list)
                    return;
                string next_context = context;
                for (int i = 0; i < list.Count; ++i)
                {
                    var directive = list[i] as string;
                    if (null != directive && DirectiveContexts.Contains (directive))
                    {
                        next_context = directive;
                        continue;
                    }
                    Walk (list[i], next_context);
                    next_context = context;
                }
            }

            public void ProcessString (string text, string context)
            {
                if (string.IsNullOrWhiteSpace (text))
                    return;
                text = text.Trim();

                foreach (Match match in FileNameRe.Matches (text))
                    AddName (match.Groups["name"].Value);
                foreach (Match match in VoiceNameRe.Matches (text))
                    AddAudioVariants (match.Groups["name"].Value, false);

                if (!IdentifierRe.IsMatch (text) || text.Length > 180)
                    return;
                if (AudioContexts.Contains (context))
                {
                    foreach (var item in text.Split (new[] { '|', ',', ';' },
                                                     StringSplitOptions.RemoveEmptyEntries))
                    {
                        bool bgm = "bgm".Equals (context, StringComparison.OrdinalIgnoreCase)
                            || "live".Equals (context, StringComparison.OrdinalIgnoreCase)
                            || "liveout".Equals (context, StringComparison.OrdinalIgnoreCase);
                        AddAudioVariants (item, bgm);
                    }
                }
                else if (ImageContexts.Contains (context))
                {
                    AddImageVariants (text);
                    if ("stand".Equals (context, StringComparison.OrdinalIgnoreCase)
                        || "character".Equals (context, StringComparison.OrdinalIgnoreCase)
                        || "msgwin".Equals (context, StringComparison.OrdinalIgnoreCase))
                    {
                        var stand = RemoveExtension (CleanCandidate (text),
                            new[] { ".stand", ".png", ".tlg", ".jpg", ".jpeg", ".psb", ".pimg" });
                        if (!string.IsNullOrEmpty (stand))
                            AddName (stand + ".stand");
                    }
                    if ("stage".Equals (context, StringComparison.OrdinalIgnoreCase))
                    {
                        var stage = RemoveExtension (CleanCandidate (text),
                            new[] { ".png", ".tlg", ".jpg", ".jpeg", ".psb", ".pimg" });
                        if (!string.IsNullOrEmpty (stage))
                            AddName ("bgthum_" + stage + ".jpg");
                    }
                }
                else if (ScenarioContexts.Contains (context))
                {
                    AddScenarioVariants (text);
                }
            }

            public void AddAudioVariants (string value, bool bgm)
            {
                var explicit_name = CleanCandidate (value);
                value = RemoveExtension (explicit_name,
                    new[] { ".ogg.sli", ".opus.sli", ".mchx.sli", ".ogg", ".opus", ".wav", ".mp3", ".sli", ".ini", ".mchx" });
                if (string.IsNullOrEmpty (value))
                    return;
                AddName (value + ".ogg");
                AddName (value + ".ogg.sli");
                AddName (value + ".opus");
                AddName (value + ".opus.sli");
                AddName (value + ".ini");
                if (bgm)
                {
                    AddName (value + ".mchx");
                    AddName (value + ".mchx.sli");
                }
                if (!string.IsNullOrEmpty (explicit_name)
                    && Path.GetFileName (explicit_name).IndexOf ('.') >= 0)
                    AddName (explicit_name);
            }

            void AddImageVariants (string value)
            {
                value = RemoveExtension (CleanCandidate (value),
                    new[] { ".png", ".tlg", ".jpg", ".jpeg", ".webp", ".bmp", ".psd", ".pimg", ".psb" });
                if (string.IsNullOrEmpty (value))
                    return;
                AddName (value + ".png");
                AddName (value + ".tlg");
                AddName (value + ".jpg");
                AddName (value + ".psd");
                AddName (value + ".pimg");
                AddName (value + ".psb");
            }

            void AddScenarioVariants (string value)
            {
                value = RemoveExtension (CleanCandidate (value), new[] { ".scn", ".ks", ".tjs" });
                if (string.IsNullOrEmpty (value))
                    return;
                AddName (value + ".scn");
                AddName (value + ".ks");
                AddName (value + ".tjs");
            }

            void RememberVoiceName (string name)
            {
                var audio_extensions = new[] {
                    ".ogg.sli", ".opus.sli", ".mchx.sli", ".ogg", ".opus",
                    ".mchx", ".wav", ".mp3", ".sli", ".ini",
                };
                if (!audio_extensions.Any (
                    x => name.EndsWith (x, StringComparison.OrdinalIgnoreCase)))
                    return;
                var value = RemoveExtension (name, audio_extensions);
                if (string.IsNullOrEmpty (value))
                    return;
                var numeric = NumericVoiceRe.Match (value);
                if (numeric.Success)
                {
                    bool first_observation = m_voice_names.Add (value);
                    var prefix = numeric.Groups["prefix"].Value;
                    var digits = numeric.Groups["number"].Value;
                    VoiceSequence sequence;
                    if (!m_voice_sequences.TryGetValue (prefix, out sequence))
                    {
                        sequence = new VoiceSequence { Width = digits.Length };
                        m_voice_sequences[prefix] = sequence;
                    }
                    int number;
                    if (int.TryParse (digits, NumberStyles.None, CultureInfo.InvariantCulture, out number))
                        sequence.Maximum = Math.Max (sequence.Maximum, number);
                    if (first_observation)
                        ++sequence.Observed;
                    var first_separator = prefix.IndexOf ('_');
                    if (first_separator > 0)
                        m_system_prefixes.Add (prefix.Substring (0, first_separator));
                    return;
                }

                var separator = value.IndexOf ('_');
                if (separator > 0 && separator <= 12
                    && IdentifierRe.IsMatch (value.Substring (0, separator)))
                    m_system_prefixes.Add (value.Substring (0, separator));
            }

            public void ExpandVoiceSequences ()
            {
                foreach (var pair in m_voice_sequences.ToArray())
                {
                    var limit = Math.Min (pair.Value.Maximum + 5, 99999);
                    for (int i = 1; i <= limit; ++i)
                    {
                        var stem = pair.Key + i.ToString ("D" + pair.Value.Width, CultureInfo.InvariantCulture);
                        AddNumericVoiceVariants (stem);
                        AddNumericVoiceVariants (stem + "a");
                        AddNumericVoiceVariants (stem + "b");
                        AddNumericVoiceVariants (stem + "c");
                    }
                }
            }

            void AddNumericVoiceVariants (string stem)
            {
                AddName (stem + ".ogg");
                AddName (stem + ".ogg.sli");
            }

            public void ExpandSystemVoices ()
            {
                foreach (var prefix in m_system_prefixes.ToArray())
                {
                    foreach (var suffix in SystemVoiceSuffixes)
                        AddAudioVariants (prefix + "_" + suffix, false);
                    for (int i = 0; i <= 999; ++i)
                        AddNumericVoiceVariants (prefix + "_loop_" + i.ToString ("D3", CultureInfo.InvariantCulture));
                }
                for (int i = 0; i <= 199; ++i)
                    AddAudioVariants ("bgm" + i.ToString ("D2", CultureInfo.InvariantCulture), true);
                for (int i = 0; i <= 20; ++i)
                    AddAudioVariants ("songed" + i.ToString (CultureInfo.InvariantCulture), true);
                AddAudioVariants ("songop", true);
            }

            public void AddCommonPaths ()
            {
                var paths = new[] {
                    "scenario/", "scenario/scripts/", "scenario/transitions/",
                    "scn/", "sysscn/", "voice/", "sound/", "sysse/", "bgm/",
                    "image/", "bgimage/", "fgimage/", "evimage/", "evimage2/",
                    "face/", "motion/", "sdmotion/", "thum/", "uipsd/", "video/",
                    "system/", "init/", "main/", "func/", "ini/", "rule/",
                    "locale/", "locale/jp/", "locale/en/", "locale/cn/", "locale/tw/",
                };
                foreach (var path in paths)
                    AddPath (path);
            }

            static string CleanCandidate (string value)
            {
                if (string.IsNullOrWhiteSpace (value))
                    return null;
                value = value.Trim().Trim ('"', '\'', '[', ']', '(', ')');
                int parameter = value.IndexOf ('?');
                if (parameter >= 0)
                    value = value.Substring (0, parameter);
                return value.Trim();
            }

            static string RemoveExtension (string value, IEnumerable<string> extensions)
            {
                if (string.IsNullOrEmpty (value))
                    return value;
                foreach (var extension in extensions)
                {
                    if (value.EndsWith (extension, StringComparison.OrdinalIgnoreCase))
                        return value.Substring (0, value.Length - extension.Length);
                }
                return value;
            }

            sealed class VoiceSequence
            {
                public int Width;
                public int Maximum;
                public int Observed;
            }
        }
    }

    /// <summary>
    /// Hx v4 salted file-name and path hashes.
    /// </summary>
    public static class HxV4Hash
    {
        const string Salt = "xp3hnp";

        static readonly uint[] BlakeIv = {
            0x6A09E667, 0xBB67AE85, 0x3C6EF372, 0xA54FF53A,
            0x510E527F, 0x9B05688C, 0x1F83D9AB, 0x5BE0CD19,
        };

        static readonly byte[,] BlakeSigma = {
            { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9,10,11,12,13,14,15 },
            {14,10, 4, 8, 9,15,13, 6, 1,12, 0, 2,11, 7, 5, 3 },
            {11, 8,12, 0, 5, 2,15,13,10,14, 3, 6, 7, 1, 9, 4 },
            { 7, 9, 3, 1,13,12,11,14, 2, 6, 5,10, 4, 0,15, 8 },
            { 9, 0, 5, 7, 2, 4,10,15,14, 1,11,12, 6, 8, 3,13 },
            { 2,12, 6,10, 0,11, 8, 3, 4,13, 7, 5,15,14, 1, 9 },
            {12, 5, 1,15,14,13, 4,10, 0, 7, 6, 3, 9, 2, 8,11 },
            {13,11, 7,14,12, 1, 3, 9, 5, 0,15, 4, 8, 6, 2,10 },
            { 6,15,14, 9,11, 3, 0, 8,12, 2,13, 7, 1, 4,10, 5 },
            {10, 2, 8, 4, 7, 6, 1, 5,15,11, 9,14, 3,12,13, 0 },
        };

        public static string GetFileNameHash (string name)
        {
            return new FileNameHasher().GetHash (name);
        }

        public static string GetPathHash (string path)
        {
            var value = "/" == path ? Salt : (path ?? string.Empty) + Salt;
            var input = Encoding.Unicode.GetBytes (value);
            var hash = SipHash24 (input);
            var bytes = BitConverter.GetBytes (hash);
            if (!BitConverter.IsLittleEndian)
                Array.Reverse (bytes);
            return ToHex (bytes);
        }

        internal sealed class FileNameHasher
        {
            readonly uint[] m_hash = new uint[8];
            readonly uint[] m_message = new uint[16];
            readonly uint[] m_work = new uint[16];
            readonly byte[] m_block = new byte[64];
            readonly char[] m_hex = new char[64];

            public string GetHash (string name)
            {
                name = name ?? string.Empty;
                Array.Copy (BlakeIv, m_hash, m_hash.Length);
                m_hash[0] ^= 0x01010020; // fanout=1, depth=1, digest length=32
                int input_length = checked ((name.Length + Salt.Length) * 2);
                ulong counter = 0;
                int offset = 0;
                while (offset < input_length)
                {
                    Array.Clear (m_block, 0, m_block.Length);
                    int count = Math.Min (m_block.Length, input_length - offset);
                    for (int i = 0; i < count; ++i)
                    {
                        int input_offset = offset + i;
                        int char_offset = input_offset >> 1;
                        char c = char_offset < name.Length
                            ? name[char_offset]
                            : Salt[char_offset-name.Length];
                        m_block[i] = 0 == (input_offset & 1) ? (byte)c : (byte)(c >> 8);
                    }
                    counter += (uint)count;
                    bool last = offset + count == input_length;
                    BlakeCompress (m_hash, m_block, (uint)counter, (uint)(counter >> 32),
                                   last, m_message, m_work);
                    offset += count;
                }

                const string digits = "0123456789ABCDEF";
                int output = 0;
                for (int i = 0; i < m_hash.Length; ++i)
                {
                    uint word = m_hash[i];
                    for (int j = 0; j < 4; ++j)
                    {
                        byte value = (byte)(word >> (j*8));
                        m_hex[output++] = digits[value >> 4];
                        m_hex[output++] = digits[value & 0xF];
                    }
                }
                return new string (m_hex);
            }
        }

        static void BlakeCompress (uint[] h, byte[] block, uint counter_low,
                                    uint counter_high, bool last, uint[] message, uint[] v)
        {
            for (int i = 0; i < message.Length; ++i)
            {
                int p = i * 4;
                message[i] = (uint)(block[p] | block[p+1] << 8
                    | block[p+2] << 16 | block[p+3] << 24);
            }
            Array.Copy (h, 0, v, 0, 8);
            Array.Copy (BlakeIv, 0, v, 8, 8);
            v[12] ^= counter_low;
            v[13] ^= counter_high;
            if (last)
                v[14] = ~v[14];

            for (int round = 0; round < 10; ++round)
            {
                BlakeMix (v, 0, 4, 8, 12, message[BlakeSigma[round,0]], message[BlakeSigma[round,1]]);
                BlakeMix (v, 1, 5, 9, 13, message[BlakeSigma[round,2]], message[BlakeSigma[round,3]]);
                BlakeMix (v, 2, 6,10, 14, message[BlakeSigma[round,4]], message[BlakeSigma[round,5]]);
                BlakeMix (v, 3, 7,11, 15, message[BlakeSigma[round,6]], message[BlakeSigma[round,7]]);
                BlakeMix (v, 0, 5,10, 15, message[BlakeSigma[round,8]], message[BlakeSigma[round,9]]);
                BlakeMix (v, 1, 6,11, 12, message[BlakeSigma[round,10]], message[BlakeSigma[round,11]]);
                BlakeMix (v, 2, 7, 8, 13, message[BlakeSigma[round,12]], message[BlakeSigma[round,13]]);
                BlakeMix (v, 3, 4, 9, 14, message[BlakeSigma[round,14]], message[BlakeSigma[round,15]]);
            }
            for (int i = 0; i < 8; ++i)
                h[i] ^= v[i] ^ v[i+8];
        }

        static void BlakeMix (uint[] v, int a, int b, int c, int d, uint x, uint y)
        {
            unchecked
            {
                v[a] += v[b] + x;
                v[d] = RotateRight (v[d] ^ v[a], 16);
                v[c] += v[d];
                v[b] = RotateRight (v[b] ^ v[c], 12);
                v[a] += v[b] + y;
                v[d] = RotateRight (v[d] ^ v[a], 8);
                v[c] += v[d];
                v[b] = RotateRight (v[b] ^ v[c], 7);
            }
        }

        static uint RotateRight (uint value, int count)
        {
            return value >> count | value << (32-count);
        }

        static ulong SipHash24 (byte[] input)
        {
            ulong v0 = 0x736F6D6570736575;
            ulong v1 = 0x646F72616E646F6D;
            ulong v2 = 0x6C7967656E657261;
            ulong v3 = 0x7465646279746573;
            int offset = 0;
            while (offset + 8 <= input.Length)
            {
                ulong value = ReadUInt64 (input, offset);
                v3 ^= value;
                SipRound (ref v0, ref v1, ref v2, ref v3);
                SipRound (ref v0, ref v1, ref v2, ref v3);
                v0 ^= value;
                offset += 8;
            }

            ulong tail = (ulong)(input.Length & 0xFF) << 56;
            for (int i = 0; offset + i < input.Length; ++i)
                tail |= (ulong)input[offset+i] << (8*i);
            v3 ^= tail;
            SipRound (ref v0, ref v1, ref v2, ref v3);
            SipRound (ref v0, ref v1, ref v2, ref v3);
            v0 ^= tail;
            v2 ^= 0xFF;
            for (int i = 0; i < 4; ++i)
                SipRound (ref v0, ref v1, ref v2, ref v3);
            return v0 ^ v1 ^ v2 ^ v3;
        }

        static void SipRound (ref ulong v0, ref ulong v1, ref ulong v2, ref ulong v3)
        {
            unchecked
            {
                v0 += v1;
                v1 = RotateLeft (v1, 13) ^ v0;
                v0 = RotateLeft (v0, 32);
                v2 += v3;
                v3 = RotateLeft (v3, 16) ^ v2;
                v0 += v3;
                v3 = RotateLeft (v3, 21) ^ v0;
                v2 += v1;
                v1 = RotateLeft (v1, 17) ^ v2;
                v2 = RotateLeft (v2, 32);
            }
        }

        static ulong RotateLeft (ulong value, int count)
        {
            return value << count | value >> (64-count);
        }

        static ulong ReadUInt64 (byte[] input, int offset)
        {
            return (ulong)input[offset]
                | (ulong)input[offset+1] << 8
                | (ulong)input[offset+2] << 16
                | (ulong)input[offset+3] << 24
                | (ulong)input[offset+4] << 32
                | (ulong)input[offset+5] << 40
                | (ulong)input[offset+6] << 48
                | (ulong)input[offset+7] << 56;
        }

        static string ToHex (byte[] input)
        {
            var chars = new char[input.Length * 2];
            const string digits = "0123456789ABCDEF";
            for (int i = 0; i < input.Length; ++i)
            {
                chars[i*2] = digits[input[i] >> 4];
                chars[i*2+1] = digits[input[i] & 0xF];
            }
            return new string (chars);
        }
    }
}
