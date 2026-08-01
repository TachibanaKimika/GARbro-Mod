using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using GameRes;

namespace GARbro.Cli
{
    internal enum DuplicatePolicy
    {
        Error,
        SuffixIndex,
    }

    internal sealed class ExtractionLimits
    {
        public long MaxFiles { get; set; }
        public long MaxTotalBytes { get; set; }
        public long MaxEntryBytes { get; set; }
        public int MaxDepth { get; set; }

        public Dictionary<string, object> ToDictionary ()
        {
            return new Dictionary<string, object> {
                { "maxFiles", MaxFiles },
                { "maxTotalBytes", MaxTotalBytes },
                { "maxEntryBytes", MaxEntryBytes },
                { "maxDepth", MaxDepth },
            };
        }
    }

    internal sealed class IndexedArchiveEntry
    {
        public int EntryIndex { get; private set; }
        public Entry Entry { get; private set; }

        public IndexedArchiveEntry (int entry_index, Entry entry)
        {
            EntryIndex = entry_index;
            Entry = entry;
        }

        public static List<IndexedArchiveEntry> Create (ArcFile archive)
        {
            var result = new List<IndexedArchiveEntry> (archive.Dir.Count);
            int index = 0;
            foreach (Entry entry in archive.Dir)
                result.Add (new IndexedArchiveEntry (index++, entry));
            return result;
        }
    }

    internal sealed class ArchivePlanEntry
    {
        public IndexedArchiveEntry Source { get; set; }
        public ResolvedOutputPath BaseOutput { get; set; }
        public ResolvedOutputPath Output { get; set; }
        public int Occurrence { get; set; }
        public int GroupSize { get; set; }
        public long DeclaredBytes { get; set; }
        public string DeclaredBytesSource { get; set; }
        public bool DestinationExists { get; set; }

        public int EntryIndex { get { return Source.EntryIndex; } }
        public Entry Entry { get { return Source.Entry; } }
        public string OutputRelativePath { get { return Output.RelativePath; } }
        public string OutputFullPath { get { return Output.FullPath; } }
    }

    internal sealed class ArchivePlan
    {
        public string Destination { get; set; }
        public DuplicatePolicy DuplicatePolicyMode { get; set; }
        public IList<ArchivePlanEntry> Entries { get; set; }
        public int ArchiveEntryCount { get; set; }
        public int UniqueNormalizedPathCount { get; set; }
        public int DuplicateGroupCount { get; set; }
        public int DuplicateEntryCount { get; set; }
        public int ExtraOccurrenceCount { get; set; }
        public int DestinationCollisionGroupCount { get; set; }
        public int ExistingConflictCount { get; set; }
        public long DeclaredTotalBytes { get; set; }
        public long MaximumDeclaredEntryBytes { get; set; }
        public int MaximumDepth { get; set; }
        public ExtractionLimits RecommendedLimits { get; set; }
        public bool FitsDefaultLimits { get; set; }
        public string PlanFingerprint { get; set; }

        public bool HasDestinationCollisions
        {
            get { return DestinationCollisionGroupCount > 0; }
        }

        public bool Ready
        {
            get
            {
                return ExistingConflictCount == 0
                    && (DuplicatePolicy.SuffixIndex == DuplicatePolicyMode
                        || !HasDestinationCollisions);
            }
        }

        public void EnsureWithin (ExtractionPolicy policy)
        {
            if (Entries.Count > policy.MaxFiles)
            {
                throw CliException.Invalid (
                    "file_count_limit_exceeded",
                    "Selected entry count exceeds --max-files.",
                    new Dictionary<string, object> {
                        { "selected", Entries.Count },
                        { "limit", policy.MaxFiles },
                    });
            }
            if (MaximumDeclaredEntryBytes > policy.MaxEntryBytes)
            {
                ArchivePlanEntry entry = Entries.First (
                    x => x.DeclaredBytes > policy.MaxEntryBytes);
                throw CliException.Invalid (
                    "entry_size_limit_exceeded",
                    "An entry exceeds --max-entry-bytes.",
                    new Dictionary<string, object> {
                        { "entry", entry.Entry.Name },
                        { "entryIndex", entry.EntryIndex },
                        { "size", entry.DeclaredBytes },
                        { "limit", policy.MaxEntryBytes },
                    });
            }
            if (DeclaredTotalBytes > policy.MaxTotalBytes)
            {
                throw CliException.Invalid (
                    "total_size_limit_exceeded",
                    "Selected entries exceed --max-total-bytes.",
                    new Dictionary<string, object> {
                        { "observed", DeclaredTotalBytes },
                        { "limit", policy.MaxTotalBytes },
                    });
            }
            if (MaximumDepth > policy.MaxDepth)
            {
                ArchivePlanEntry entry = Entries.First (
                    x => x.Output.Depth > policy.MaxDepth);
                throw CliException.Invalid (
                    "unsafe_output_path",
                    "Archive entry cannot be mapped safely below the destination directory.",
                    new Dictionary<string, object> {
                        { "entry", entry.Entry.Name },
                        { "entryIndex", entry.EntryIndex },
                        { "reason", "depth_limit_exceeded" },
                        { "depth", entry.Output.Depth },
                        { "limit", policy.MaxDepth },
                    });
            }
        }

        public void EnsureDuplicatePolicyCanExtract ()
        {
            if (DuplicatePolicy.Error != DuplicatePolicyMode || !HasDestinationCollisions)
                return;
            var groups = Entries.GroupBy (
                x => x.BaseOutput.FullPath, StringComparer.OrdinalIgnoreCase);
            var collision = groups.First (x => x.Count() > 1)
                                  .OrderBy (x => x.EntryIndex).ToList();
            ArchivePlanEntry first = collision[0];
            ArchivePlanEntry duplicate = collision[1];
            throw CliException.Invalid (
                "unsafe_output_path",
                "Archive entry cannot be mapped safely below the destination directory.",
                new Dictionary<string, object> {
                    { "entry", duplicate.Entry.Name },
                    { "entryIndex", duplicate.EntryIndex },
                    { "conflictingEntryIndex", first.EntryIndex },
                    { "reason", "duplicate_destination" },
                    { "path", duplicate.OutputFullPath },
                });
        }

        public Dictionary<string, object> ToSummaryDictionary ()
        {
            return new Dictionary<string, object> {
                { "destination", Destination },
                { "archiveEntryCount", ArchiveEntryCount },
                { "selected", Entries.Count },
                { "uniqueNormalizedPathCount", UniqueNormalizedPathCount },
                { "duplicateGroupCount", DuplicateGroupCount },
                { "duplicateEntryCount", DuplicateEntryCount },
                { "extraOccurrenceCount", ExtraOccurrenceCount },
                { "destinationCollisionGroupCount", DestinationCollisionGroupCount },
                { "existingConflictCount", ExistingConflictCount },
                { "maximumDepth", MaximumDepth },
                { "declaredTotalBytes", DeclaredTotalBytes },
                { "maximumDeclaredEntryBytes", MaximumDeclaredEntryBytes },
                { "budgetBasis", "declared_metadata_plus_finite_headroom" },
                { "recommendedLimits", RecommendedLimits.ToDictionary() },
                { "fitsDefaultLimits", FitsDefaultLimits },
                { "duplicatePolicy", ArchivePlanner.DuplicatePolicyName (DuplicatePolicyMode) },
                { "ready", Ready },
                { "planFingerprint", PlanFingerprint },
            };
        }
    }

    internal static class ArchivePlanner
    {
        const int PlanningMaximumDepth = 1024;
        const long MinimumHeadroom = 1024L * 1024;

        sealed class PathRecord
        {
            public IndexedArchiveEntry Source;
            public ResolvedOutputPath BaseOutput;
            public ResolvedOutputPath Output;
            public int Occurrence;
            public int GroupSize;
        }

        public static DuplicatePolicy ParseDuplicatePolicy (ParsedCommand command)
        {
            string value = command.GetSingle ("duplicate-policy", "error")
                                  .ToLowerInvariant();
            switch (value)
            {
            case "error":
                return DuplicatePolicy.Error;
            case "suffix-index":
                return DuplicatePolicy.SuffixIndex;
            default:
                throw CliException.Usage (
                    "invalid_duplicate_policy",
                    "--duplicate-policy must be one of: error, suffix-index.");
            }
        }

        public static string DuplicatePolicyName (DuplicatePolicy policy)
        {
            return DuplicatePolicy.SuffixIndex == policy ? "suffix-index" : "error";
        }

        public static ArchivePlan Build (
            ArcFile archive, string destination, IList<string> patterns,
            IList<string> requested_indexes, DuplicatePolicy duplicate_policy)
        {
            return Build (archive, destination, patterns, requested_indexes,
                          duplicate_policy, null);
        }

        public static ArchivePlan Build (
            ArcFile archive, string destination, IList<string> patterns,
            IList<string> requested_indexes, DuplicatePolicy duplicate_policy,
            string archive_options_fingerprint)
        {
            var all_entries = IndexedArchiveEntry.Create (archive);
            HashSet<int> index_filter = ParseEntryIndexes (
                requested_indexes, all_entries.Count);
            var selected = all_entries.Where (x =>
                (0 == patterns.Count || GlobMatcher.IsAnyMatch (x.Entry.Name, patterns))
                && (null == index_filter || index_filter.Contains (x.EntryIndex)))
                .ToList();
            if (0 == selected.Count)
            {
                throw CliException.Invalid (
                    "no_entries_selected",
                    "No archive entries matched the requested pattern(s) and indexes.",
                    new Dictionary<string, object> {
                        { "patterns", patterns },
                        { "entryIndexes", null == index_filter
                            ? new int[0] : index_filter.OrderBy (x => x).ToArray() },
                    });
            }

            var selected_indexes = new HashSet<int> (
                selected.Select (x => x.EntryIndex));
            var resolver = new OutputPathResolver (
                destination, PlanningMaximumDepth);
            var path_records = new List<PathRecord> (all_entries.Count);
            foreach (IndexedArchiveEntry indexed in all_entries)
            {
                try
                {
                    path_records.Add (new PathRecord {
                        Source = indexed,
                        BaseOutput = resolver.NormalizeAndValidate (indexed.Entry.Name),
                    });
                }
                catch (CliException)
                {
                    if (selected_indexes.Contains (indexed.EntryIndex))
                        throw;
                }
            }

            var groups = new Dictionary<string, List<PathRecord>> (
                StringComparer.OrdinalIgnoreCase);
            foreach (PathRecord record in path_records)
            {
                List<PathRecord> group;
                if (!groups.TryGetValue (record.BaseOutput.FullPath, out group))
                {
                    group = new List<PathRecord>();
                    groups.Add (record.BaseOutput.FullPath, group);
                }
                group.Add (record);
            }

            var reserved = new HashSet<string> (StringComparer.OrdinalIgnoreCase);
            foreach (List<PathRecord> group in groups.Values)
            {
                group.Sort ((x, y) => x.Source.EntryIndex.CompareTo (y.Source.EntryIndex));
                reserved.Add (group[0].BaseOutput.FullPath);
                for (int i = 0; i < group.Count; ++i)
                {
                    group[i].Occurrence = i + 1;
                    group[i].GroupSize = group.Count;
                    group[i].Output = group[i].BaseOutput;
                }
            }

            if (DuplicatePolicy.SuffixIndex == duplicate_policy)
            {
                foreach (List<PathRecord> group in groups.Values)
                {
                    for (int i = 1; i < group.Count; ++i)
                    {
                        PathRecord record = group[i];
                        int disambiguator = 0;
                        for (;;)
                        {
                            string candidate_name = CreateSuffixedName (
                                record.BaseOutput.RelativePath,
                                record.Source.EntryIndex, disambiguator);
                            try
                            {
                                ResolvedOutputPath candidate =
                                    resolver.NormalizeAndValidate (candidate_name);
                                if (reserved.Add (candidate.FullPath))
                                {
                                    record.Output = candidate;
                                    break;
                                }
                            }
                            catch (CliException)
                            {
                                if (selected_indexes.Contains (record.Source.EntryIndex))
                                    throw;
                                break;
                            }
                            ++disambiguator;
                        }
                    }
                }
            }

            var record_by_index = path_records.ToDictionary (
                x => x.Source.EntryIndex);
            var plan_entries = new List<ArchivePlanEntry> (selected.Count);
            long declared_total = 0;
            long maximum_declared = 0;
            int maximum_depth = 0;
            int existing_conflicts = 0;
            foreach (IndexedArchiveEntry indexed in selected)
            {
                PathRecord path = record_by_index[indexed.EntryIndex];
                long declared;
                string declared_source;
                GetDeclaredSize (indexed.Entry, out declared, out declared_source);
                if (declared_total > long.MaxValue - declared)
                {
                    throw CliException.Invalid (
                        "total_size_overflow",
                        "Selected entry declarations exceed the supported total size.");
                }
                declared_total += declared;
                maximum_declared = Math.Max (maximum_declared, declared);
                maximum_depth = Math.Max (maximum_depth, path.Output.Depth);
                bool destination_exists = File.Exists (path.Output.FullPath)
                    || Directory.Exists (path.Output.FullPath);
                if (destination_exists)
                    ++existing_conflicts;
                plan_entries.Add (new ArchivePlanEntry {
                    Source = indexed,
                    BaseOutput = path.BaseOutput,
                    Output = path.Output,
                    Occurrence = path.Occurrence,
                    GroupSize = path.GroupSize,
                    DeclaredBytes = declared,
                    DeclaredBytesSource = declared_source,
                    DestinationExists = destination_exists,
                });
            }

            foreach (var output_group in plan_entries.GroupBy (
                x => x.OutputFullPath, StringComparer.OrdinalIgnoreCase)
                .OrderBy (x => x.Min (entry => entry.EntryIndex)))
            {
                ArchivePlanEntry representative = output_group.OrderBy (
                    x => x.EntryIndex).First();
                resolver.Reserve (
                    representative.Output, representative.Entry.Name);
            }

            var selected_groups = plan_entries.GroupBy (
                x => x.BaseOutput.FullPath, StringComparer.OrdinalIgnoreCase).ToList();
            int destination_collision_groups = selected_groups.Count (x => x.Count() > 1);
            int duplicate_groups = selected_groups.Count (x => x.First().GroupSize > 1);
            int duplicate_entries = plan_entries.Count (x => x.GroupSize > 1);
            int unique_paths = selected_groups.Count;
            var recommended = CreateRecommendedLimits (
                plan_entries.Count, declared_total, maximum_declared, maximum_depth);
            var plan = new ArchivePlan {
                Destination = resolver.Root,
                DuplicatePolicyMode = duplicate_policy,
                Entries = plan_entries,
                ArchiveEntryCount = all_entries.Count,
                UniqueNormalizedPathCount = unique_paths,
                DuplicateGroupCount = duplicate_groups,
                DuplicateEntryCount = duplicate_entries,
                ExtraOccurrenceCount = plan_entries.Count - unique_paths,
                DestinationCollisionGroupCount = destination_collision_groups,
                ExistingConflictCount = existing_conflicts,
                DeclaredTotalBytes = declared_total,
                MaximumDeclaredEntryBytes = maximum_declared,
                MaximumDepth = maximum_depth,
                RecommendedLimits = recommended,
                FitsDefaultLimits = plan_entries.Count <= ExtractionPolicy.DefaultMaxFiles
                    && declared_total <= ExtractionPolicy.DefaultMaxTotalBytes
                    && maximum_declared <= ExtractionPolicy.DefaultMaxEntryBytes
                    && maximum_depth <= ExtractionPolicy.DefaultMaxDepth,
            };
            plan.PlanFingerprint = ComputeFingerprint (
                archive.Tag, archive_options_fingerprint, plan);
            return plan;
        }

        public static void GetDeclaredSize (
            Entry entry, out long declared_size, out string source)
        {
            var packed = entry as PackedEntry;
            if (null != packed && packed.IsPacked && 0 != packed.UnpackedSize)
            {
                declared_size = packed.UnpackedSize;
                source = "unpackedSize";
            }
            else
            {
                declared_size = entry.Size;
                source = "storedSize";
            }
        }

        static HashSet<int> ParseEntryIndexes (
            IList<string> values, int archive_entry_count)
        {
            if (null == values || 0 == values.Count)
                return null;
            var result = new HashSet<int>();
            foreach (string value in values)
            {
                int index;
                if (!int.TryParse (value, NumberStyles.Integer,
                                   CultureInfo.InvariantCulture, out index)
                    || index < 0 || index >= archive_entry_count)
                {
                    throw CliException.Invalid (
                        "entry_index_out_of_range",
                        "--entry-index must identify an existing zero-based archive entry.",
                        new Dictionary<string, object> {
                            { "value", value },
                            { "minimum", 0 },
                            { "maximum", Math.Max (0, archive_entry_count - 1) },
                            { "entryCount", archive_entry_count },
                        });
                }
                result.Add (index);
            }
            return result;
        }

        static string CreateSuffixedName (
            string relative_path, int entry_index, int disambiguator)
        {
            string directory = Path.GetDirectoryName (relative_path);
            string filename = Path.GetFileName (relative_path);
            string extension = Path.GetExtension (filename);
            string stem = filename.Substring (0, filename.Length - extension.Length);
            string suffix = ".__entry-" + entry_index.ToString (
                "D6", CultureInfo.InvariantCulture);
            if (disambiguator > 0)
            {
                suffix += "-" + disambiguator.ToString (
                    "D2", CultureInfo.InvariantCulture);
            }
            string candidate = stem + suffix + extension;
            return string.IsNullOrEmpty (directory)
                ? candidate : Path.Combine (directory, candidate);
        }

        static ExtractionLimits CreateRecommendedLimits (
            int count, long total, long maximum_entry, int maximum_depth)
        {
            long total_headroom = Math.Max (MinimumHeadroom, CeilingTwoPercent (total));
            long entry_headroom = Math.Max (
                MinimumHeadroom, CeilingTwoPercent (maximum_entry));
            return new ExtractionLimits {
                MaxFiles = Math.Max (1, count),
                MaxTotalBytes = AddFinite (total, total_headroom),
                MaxEntryBytes = AddFinite (maximum_entry, entry_headroom),
                MaxDepth = Math.Max (1, maximum_depth),
            };
        }

        static long CeilingTwoPercent (long value)
        {
            if (value <= 0)
                return 0;
            return value / 50 + (0 == value % 50 ? 0 : 1);
        }

        static long AddFinite (long value, long addition)
        {
            return value > long.MaxValue - addition
                ? long.MaxValue : value + addition;
        }

        static string ComputeFingerprint (
            string archive_tag, string archive_options_fingerprint,
            ArchivePlan plan)
        {
            byte[] data;
            using (var buffer = new MemoryStream())
            {
                using (var writer = new BinaryWriter (buffer, Encoding.UTF8))
                {
                    writer.Write ("garbro.archive-plan/v1");
                    writer.Write (archive_tag ?? string.Empty);
                    writer.Write (archive_options_fingerprint ?? string.Empty);
                    writer.Write (DuplicatePolicyName (plan.DuplicatePolicyMode));
                    writer.Write (plan.Entries.Count);
                    foreach (ArchivePlanEntry item in plan.Entries.OrderBy (
                        x => x.EntryIndex))
                    {
                        writer.Write (item.EntryIndex);
                        writer.Write (item.Entry.Name ?? string.Empty);
                        writer.Write (item.Entry.Type ?? string.Empty);
                        writer.Write (item.Entry.Offset);
                        writer.Write (item.Entry.Size);
                        var packed = item.Entry as PackedEntry;
                        writer.Write (null != packed);
                        if (null != packed)
                        {
                            writer.Write (packed.IsPacked);
                            writer.Write (packed.UnpackedSize);
                        }
                        writer.Write (item.DeclaredBytes);
                        writer.Write (item.DeclaredBytesSource ?? string.Empty);
                        writer.Write (item.Occurrence);
                        writer.Write (item.GroupSize);
                        writer.Write (item.OutputRelativePath ?? string.Empty);
                    }
                    writer.Flush();
                    data = buffer.ToArray();
                }
            }
            using (var sha256 = SHA256.Create())
                return Sha256Utility.ToHex (sha256.ComputeHash (data));
        }
    }
}
