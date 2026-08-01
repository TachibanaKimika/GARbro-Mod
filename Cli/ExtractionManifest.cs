using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GARbro.Cli
{
    internal enum ExtractionChecksumMode
    {
        None,
        Sha256,
    }

    internal enum ExtractionResumeMode
    {
        None,
        VerifySize,
        VerifyHash,
    }

    internal sealed class ExtractionManifestOptions
    {
        public string ManifestPath { get; private set; }
        public ExtractionChecksumMode Checksum { get; private set; }
        public ExtractionResumeMode Resume { get; private set; }
        public bool RepairTrailingPartial { get; set; }
        public string TrailingRecordPrefix { get; set; }

        public bool Enabled { get { return !string.IsNullOrEmpty (ManifestPath); } }
        public bool IsResume { get { return ExtractionResumeMode.None != Resume; } }
        public bool HashOutput { get { return ExtractionChecksumMode.Sha256 == Checksum; } }

        public static ExtractionManifestOptions FromCommand (ParsedCommand command)
        {
            string manifest = command.GetSingle ("manifest");
            string resume_manifest = command.GetSingle ("resume-manifest");
            string resume_value = command.GetSingle ("resume");
            ExtractionResumeMode resume = ExtractionResumeMode.None;
            if (!string.IsNullOrEmpty (resume_value))
            {
                switch (resume_value.ToLowerInvariant())
                {
                case "verify-size":
                    resume = ExtractionResumeMode.VerifySize;
                    break;
                case "verify-hash":
                    resume = ExtractionResumeMode.VerifyHash;
                    break;
                default:
                    throw CliException.Usage (
                        "invalid_resume_mode",
                        "--resume must be one of: verify-size, verify-hash.");
                }
            }
            if (!string.IsNullOrEmpty (resume_manifest)
                && ExtractionResumeMode.None == resume)
            {
                throw CliException.Usage (
                    "resume_mode_required",
                    "--resume-manifest requires --resume verify-size|verify-hash.");
            }

            string manifest_path = ResolveOptionalPath (manifest, "manifest");
            string resume_path = ResolveOptionalPath (
                resume_manifest, "resume manifest");
            if (!string.IsNullOrEmpty (manifest_path)
                && !string.IsNullOrEmpty (resume_path)
                && !string.Equals (manifest_path, resume_path,
                                   StringComparison.OrdinalIgnoreCase))
            {
                throw CliException.Usage (
                    "manifest_path_mismatch",
                    "--manifest and --resume-manifest must identify the same file.");
            }
            if (string.IsNullOrEmpty (manifest_path))
                manifest_path = resume_path;
            if (ExtractionResumeMode.None != resume
                && string.IsNullOrEmpty (manifest_path))
            {
                throw CliException.Usage (
                    "resume_manifest_required",
                    "--resume requires --resume-manifest FILE or --manifest FILE.");
            }

            string checksum_value = command.GetSingle ("checksum");
            ExtractionChecksumMode checksum;
            if (string.IsNullOrEmpty (checksum_value))
            {
                checksum = !string.IsNullOrEmpty (manifest_path)
                    ? ExtractionChecksumMode.Sha256 : ExtractionChecksumMode.None;
            }
            else
            {
                switch (checksum_value.ToLowerInvariant())
                {
                case "none":
                    checksum = ExtractionChecksumMode.None;
                    break;
                case "sha256":
                    checksum = ExtractionChecksumMode.Sha256;
                    break;
                default:
                    throw CliException.Usage (
                        "invalid_checksum",
                        "--checksum must be one of: none, sha256.");
                }
            }
            if (ExtractionResumeMode.VerifyHash == resume)
                checksum = ExtractionChecksumMode.Sha256;

            return new ExtractionManifestOptions {
                ManifestPath = manifest_path,
                Checksum = checksum,
                Resume = resume,
            };
        }

        public string ChecksumName ()
        {
            return ExtractionChecksumMode.Sha256 == Checksum ? "sha256" : "none";
        }

        public string ResumeName ()
        {
            switch (Resume)
            {
            case ExtractionResumeMode.VerifySize: return "verify-size";
            case ExtractionResumeMode.VerifyHash: return "verify-hash";
            default: return "none";
            }
        }

        public void EnsureNoOutputCollision (ArchivePlan plan)
        {
            if (!Enabled)
                return;
            string manifest_prefix = ManifestPath + Path.DirectorySeparatorChar;
            foreach (ArchivePlanEntry entry in plan.Entries)
            {
                string output_prefix = entry.OutputFullPath + Path.DirectorySeparatorChar;
                if (string.Equals (ManifestPath, entry.OutputFullPath,
                                   StringComparison.OrdinalIgnoreCase)
                    || ManifestPath.StartsWith (output_prefix,
                                                StringComparison.OrdinalIgnoreCase)
                    || entry.OutputFullPath.StartsWith (
                        manifest_prefix, StringComparison.OrdinalIgnoreCase))
                {
                    throw CliException.Conflict (
                        "manifest_output_collision",
                        "The extraction manifest conflicts with a planned output path.",
                        new Dictionary<string, object> {
                            { "manifest", ManifestPath },
                            { "entry", entry.Entry.Name },
                            { "entryIndex", entry.EntryIndex },
                            { "path", entry.OutputFullPath },
                        });
                }
            }
        }

        public void ValidateWriteTarget (OverwriteMode overwrite)
        {
            if (!Enabled)
                return;
            RejectReparsePoints();
            if (IsResume)
                return;
            if (Directory.Exists (ManifestPath))
            {
                throw CliException.Invalid (
                    "manifest_path_is_directory",
                    "The extraction manifest path identifies a directory: "
                        + ManifestPath,
                    new Dictionary<string, object> { { "path", ManifestPath } });
            }
            if (File.Exists (ManifestPath) && OverwriteMode.Replace != overwrite)
            {
                throw CliException.Conflict (
                    "manifest_exists",
                    "The extraction manifest already exists: " + ManifestPath,
                    new Dictionary<string, object> { { "path", ManifestPath } });
            }
        }

        void RejectReparsePoints ()
        {
            string current = ManifestPath;
            while (!string.IsNullOrEmpty (current))
            {
                if ((File.Exists (current) || Directory.Exists (current))
                    && 0 != (File.GetAttributes (current)
                             & FileAttributes.ReparsePoint))
                {
                    throw CliException.Invalid (
                        "manifest_reparse_point",
                        "The extraction manifest path must not traverse a reparse point.",
                        new Dictionary<string, object> {
                            { "path", ManifestPath },
                            { "component", current },
                        });
                }
                string parent = Path.GetDirectoryName (current);
                if (string.IsNullOrEmpty (parent)
                    || string.Equals (parent, current,
                                      StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
                current = parent;
            }
        }

        static string ResolveOptionalPath (string value, string label)
        {
            if (string.IsNullOrWhiteSpace (value))
                return null;
            try
            {
                return Path.GetFullPath (value);
            }
            catch (Exception exception)
            {
                if (exception is ArgumentException || exception is NotSupportedException
                    || exception is PathTooLongException)
                {
                    throw CliException.Invalid (
                        "invalid_manifest_path", "The " + label + " path is invalid: " + value);
                }
                throw;
            }
        }
    }

    internal sealed class ArchiveSourceIdentity
    {
        public string Path { get; private set; }
        public long Length { get; private set; }
        public long LastWriteTimeUtcTicks { get; private set; }
        public string LastWriteTimeUtc { get; private set; }
        public string Sha256 { get; private set; }

        ArchiveSourceIdentity ()
        {
        }

        public static ArchiveSourceIdentity Create (string path)
        {
            string full_path = System.IO.Path.GetFullPath (path);
            var before = new FileInfo (full_path);
            long length = before.Length;
            long ticks = before.LastWriteTimeUtc.Ticks;
            string checksum = Sha256Utility.ComputeFile (full_path);
            before.Refresh();
            if (before.Length != length || before.LastWriteTimeUtc.Ticks != ticks)
            {
                throw CliException.Invalid (
                    "archive_changed_during_hash",
                    "The source archive changed while its identity was being calculated.",
                    new Dictionary<string, object> { { "path", full_path } });
            }
            return new ArchiveSourceIdentity {
                Path = full_path,
                Length = length,
                LastWriteTimeUtcTicks = ticks,
                LastWriteTimeUtc = before.LastWriteTimeUtc.ToString (
                    "o", CultureInfo.InvariantCulture),
                Sha256 = checksum,
            };
        }

        public Dictionary<string, object> ToDictionary ()
        {
            return new Dictionary<string, object> {
                { "path", Path },
                { "length", Length },
                { "lastWriteTimeUtc", LastWriteTimeUtc },
                { "lastWriteTimeUtcTicks", LastWriteTimeUtcTicks },
                { "sha256", Sha256 },
            };
        }

        public bool Matches (ArchiveSourceIdentity other)
        {
            return null != other
                && Length == other.Length
                && LastWriteTimeUtcTicks == other.LastWriteTimeUtcTicks
                && string.Equals (Sha256, other.Sha256,
                                  StringComparison.OrdinalIgnoreCase);
        }

        public static ArchiveSourceIdentity FromManifest (JObject source)
        {
            if (null == source)
                throw InvalidManifest ("Manifest header has no sourceArchive object.");
            return new ArchiveSourceIdentity {
                Path = RequiredString (source, "path"),
                Length = RequiredInt64 (source, "length"),
                LastWriteTimeUtc = RequiredString (source, "lastWriteTimeUtc"),
                LastWriteTimeUtcTicks = RequiredInt64 (source, "lastWriteTimeUtcTicks"),
                Sha256 = RequiredString (source, "sha256"),
            };
        }

        internal static string RequiredString (JObject value, string name)
        {
            JToken token = value[name];
            if (null == token || JTokenType.String != token.Type
                || string.IsNullOrEmpty ((string)token))
            {
                throw InvalidManifest ("Manifest field is missing or invalid: " + name);
            }
            return (string)token;
        }

        internal static long RequiredInt64 (JObject value, string name)
        {
            JToken token = value[name];
            long result;
            if (null == token || !long.TryParse (
                    token.ToString(), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out result))
            {
                throw InvalidManifest ("Manifest field is missing or invalid: " + name);
            }
            return result;
        }

        internal static CliException InvalidManifest (string message)
        {
            return CliException.Invalid ("invalid_extraction_manifest", message);
        }
    }

    internal sealed class ExtractionManifestEntryState
    {
        public int EntryIndex { get; set; }
        public string EntryName { get; set; }
        public string OutputRelativePath { get; set; }
        public string Status { get; set; }
        public long? ActualBytes { get; set; }
        public string OutputSha256 { get; set; }

        public bool HasMaterializedOutput
        {
            get
            {
                return ActualBytes.HasValue
                    && ("written" == Status || "repaired" == Status
                        || "verified_existing" == Status);
            }
        }
    }

    internal sealed class ExtractionManifestState
    {
        public const string SchemaVersion = "garbro.extraction-manifest/v1";

        public ArchiveSourceIdentity SourceIdentity { get; private set; }
        public string HandlerTag { get; private set; }
        public JToken HandlerOptionsIdentity { get; private set; }
        public string Destination { get; private set; }
        public string DuplicatePolicy { get; private set; }
        public string PlanFingerprint { get; private set; }
        public int Selected { get; private set; }
        public IDictionary<int, ExtractionManifestEntryState> Entries { get; private set; }
        public bool IgnoredTrailingPartial { get; private set; }
        public string TrailingRecordPrefix { get; private set; }

        ExtractionManifestState ()
        {
            Entries = new Dictionary<int, ExtractionManifestEntryState>();
        }

        public static ExtractionManifestState Load (string path)
        {
            if (!File.Exists (path))
            {
                throw CliException.Invalid (
                    "resume_manifest_not_found",
                    "The resume manifest does not exist: " + path,
                    new Dictionary<string, object> { { "path", path } });
            }
            var state = new ExtractionManifestState();
            bool found_header = false;
            bool has_terminal_line_ending = HasTerminalLineEnding (path);
            int line_number = 0;
            var utf8 = new UTF8Encoding (false, true);
            try
            {
                using (var input = new StreamReader (path, utf8, false))
                {
                    for (;;)
                    {
                        string line = input.ReadLine();
                        if (null == line)
                            break;
                        ++line_number;
                        if (1 == line_number && line.Length > 0
                            && '\uFEFF' == line[0])
                        {
                            line = line.Substring (1);
                        }
                        if (string.IsNullOrWhiteSpace (line))
                            continue;
                        JObject record;
                        int object_end = FindFirstObjectEnd (line);
                        bool final_unterminated_line =
                            !has_terminal_line_ending && input.EndOfStream;
                        if (object_end < 0)
                        {
                            if (final_unterminated_line
                                && StartsLikeJsonObject (line))
                            {
                                state.IgnoredTrailingPartial = true;
                                break;
                            }
                            throw InvalidJsonLine (path, line_number, null);
                        }
                        try
                        {
                            using (var text = new StringReader (
                                line.Substring (0, object_end)))
                            using (var json = new JsonTextReader (text))
                            {
                                // Identity timestamps are protocol strings.  Newtonsoft's
                                // default date coercion would turn them into JTokenType.Date
                                // and make a freshly written manifest fail its own validator.
                                json.DateParseHandling = DateParseHandling.None;
                                record = JObject.Load (json);
                            }
                        }
                        catch (JsonException exception)
                        {
                            throw InvalidJsonLine (path, line_number, exception);
                        }
                        string trailing = line.Substring (object_end);
                        if (!string.IsNullOrWhiteSpace (trailing))
                        {
                            if (!final_unterminated_line
                                || !StartsLikeJsonObject (trailing)
                                || StartsWithCompleteJsonValue (trailing))
                            {
                                throw InvalidJsonLine (path, line_number, null);
                            }
                            state.IgnoredTrailingPartial = true;
                            state.TrailingRecordPrefix = line.Substring (0, object_end);
                        }
                        string schema = (string)record["schemaVersion"];
                        if (!string.Equals (SchemaVersion, schema, StringComparison.Ordinal))
                            throw ArchiveSourceIdentity.InvalidManifest (
                                "Unsupported extraction manifest schema at line "
                                + line_number.ToString (CultureInfo.InvariantCulture) + ".");
                        string kind = (string)record["record"];
                        if ("header" == kind)
                        {
                            if (found_header)
                                throw ArchiveSourceIdentity.InvalidManifest (
                                    "The extraction manifest contains more than one header.");
                            state.ReadHeader (record);
                            found_header = true;
                        }
                        else if ("entry" == kind)
                        {
                            if (!found_header)
                                throw ArchiveSourceIdentity.InvalidManifest (
                                    "An extraction manifest entry appears before its header.");
                            ExtractionManifestEntryState entry = ReadEntry (record);
                            ExtractionManifestEntryState previous;
                            bool preserve_materialized =
                                "not_attempted" == entry.Status
                                && state.Entries.TryGetValue (
                                    entry.EntryIndex, out previous)
                                && previous.HasMaterializedOutput;
                            if (!preserve_materialized)
                                state.Entries[entry.EntryIndex] = entry;
                        }
                        else if ("summary" != kind)
                        {
                            throw ArchiveSourceIdentity.InvalidManifest (
                                "Unknown extraction manifest record at line "
                                + line_number.ToString (CultureInfo.InvariantCulture) + ".");
                        }
                    }
                }
            }
            catch (DecoderFallbackException exception)
            {
                throw new CliException (
                    ExitCode.InvalidInput, "invalid_input",
                    "invalid_extraction_manifest",
                    "The extraction manifest is not valid UTF-8.",
                    new Dictionary<string, object> { { "path", path } },
                    exception);
            }
            if (!found_header)
                throw ArchiveSourceIdentity.InvalidManifest (
                    "The extraction manifest has no header.");
            return state;
        }

        static bool StartsLikeJsonObject (string value)
        {
            if (null == value)
                return false;
            int index = 0;
            while (index < value.Length && char.IsWhiteSpace (value[index]))
                ++index;
            return index < value.Length && '{' == value[index];
        }

        static int FindFirstObjectEnd (string line)
        {
            int start = 0;
            while (start < line.Length && char.IsWhiteSpace (line[start]))
                ++start;
            if (start >= line.Length || '{' != line[start])
                return -1;

            int depth = 0;
            bool in_string = false;
            bool escaped = false;
            for (int index = start; index < line.Length; ++index)
            {
                char value = line[index];
                if (in_string)
                {
                    if (escaped)
                        escaped = false;
                    else if ('\\' == value)
                        escaped = true;
                    else if ('"' == value)
                        in_string = false;
                    continue;
                }
                if ('"' == value)
                {
                    in_string = true;
                    continue;
                }
                if ('{' == value || '[' == value)
                    ++depth;
                else if ('}' == value || ']' == value)
                {
                    --depth;
                    if (0 == depth)
                        return index + 1;
                    if (depth < 0)
                        return -1;
                }
            }
            return -1;
        }

        static bool StartsWithCompleteJsonValue (string value)
        {
            try
            {
                using (var text = new StringReader (value))
                using (var json = new JsonTextReader (text))
                {
                    json.DateParseHandling = DateParseHandling.None;
                    JToken.ReadFrom (json);
                    return true;
                }
            }
            catch (JsonException)
            {
                return false;
            }
        }

        static CliException InvalidJsonLine (
            string path, int line_number, Exception inner)
        {
            return new CliException (
                ExitCode.InvalidInput, "invalid_input",
                "invalid_extraction_manifest",
                "The extraction manifest contains invalid JSONL at line "
                    + line_number.ToString (CultureInfo.InvariantCulture) + ".",
                new Dictionary<string, object> {
                    { "path", path }, { "line", line_number },
                }, inner);
        }

        static bool HasTerminalLineEnding (string path)
        {
            using (var input = new FileStream (
                path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete))
            {
                if (0 == input.Length)
                    return false;
                input.Position = input.Length - 1;
                int last = input.ReadByte();
                return '\n' == last || '\r' == last;
            }
        }

        public void Validate (
            ArchiveSourceIdentity current_identity, string handler_tag,
            IDictionary<string, object> handler_options_identity,
            ArchivePlan plan)
        {
            if (!SourceIdentity.Matches (current_identity))
            {
                throw CliException.Invalid (
                    "manifest_source_mismatch",
                    "The resume manifest belongs to a different source archive.",
                    new Dictionary<string, object> {
                        { "manifestPath", SourceIdentity.Path },
                        { "currentPath", current_identity.Path },
                        { "manifestLength", SourceIdentity.Length },
                        { "currentLength", current_identity.Length },
                        { "manifestSha256", SourceIdentity.Sha256 },
                        { "currentSha256", current_identity.Sha256 },
                    });
            }
            if (!string.Equals (HandlerTag, handler_tag, StringComparison.Ordinal)
                || !JToken.DeepEquals (
                    HandlerOptionsIdentity,
                    JToken.FromObject (handler_options_identity
                        ?? new Dictionary<string, object>())))
            {
                throw CliException.Invalid (
                    "manifest_handler_mismatch",
                    "The resume manifest was created with different archive handler options.");
            }
            if (!string.Equals (Destination, plan.Destination,
                               StringComparison.OrdinalIgnoreCase))
            {
                throw CliException.Invalid (
                    "manifest_destination_mismatch",
                    "The resume manifest belongs to a different extraction destination.",
                    new Dictionary<string, object> {
                        { "manifestDestination", Destination },
                        { "currentDestination", plan.Destination },
                    });
            }
            if (!string.Equals (DuplicatePolicy,
                               ArchivePlanner.DuplicatePolicyName (plan.DuplicatePolicyMode),
                               StringComparison.Ordinal)
                || !string.Equals (PlanFingerprint, plan.PlanFingerprint,
                                   StringComparison.OrdinalIgnoreCase)
                || Selected != plan.Entries.Count)
            {
                throw CliException.Invalid (
                    "manifest_plan_mismatch",
                    "The resume manifest does not match the current extraction plan.",
                    new Dictionary<string, object> {
                        { "manifestPlanFingerprint", PlanFingerprint },
                        { "currentPlanFingerprint", plan.PlanFingerprint },
                    });
            }

            var plans = plan.Entries.ToDictionary (x => x.EntryIndex);
            foreach (ExtractionManifestEntryState state in Entries.Values)
            {
                ArchivePlanEntry planned;
                if (!plans.TryGetValue (state.EntryIndex, out planned)
                    || !string.Equals (state.EntryName, planned.Entry.Name,
                                      StringComparison.Ordinal)
                    || !string.Equals (state.OutputRelativePath,
                                      planned.OutputRelativePath,
                                      StringComparison.OrdinalIgnoreCase))
                {
                    throw CliException.Invalid (
                        "manifest_entry_mismatch",
                        "An extraction manifest entry does not match the current plan.",
                        new Dictionary<string, object> {
                            { "entryIndex", state.EntryIndex },
                        });
                }
            }
        }

        void ReadHeader (JObject record)
        {
            SourceIdentity = ArchiveSourceIdentity.FromManifest (
                record["sourceArchive"] as JObject);
            JObject handler = record["handler"] as JObject;
            if (null == handler)
                throw ArchiveSourceIdentity.InvalidManifest (
                    "Manifest header has no handler object.");
            HandlerTag = ArchiveSourceIdentity.RequiredString (handler, "tag");
            HandlerOptionsIdentity = handler["optionsIdentity"]
                ?? new JObject();
            Destination = ArchiveSourceIdentity.RequiredString (
                record, "destination");
            DuplicatePolicy = ArchiveSourceIdentity.RequiredString (
                record, "duplicatePolicy");
            PlanFingerprint = ArchiveSourceIdentity.RequiredString (
                record, "planFingerprint");
            long selected = ArchiveSourceIdentity.RequiredInt64 (record, "selected");
            if (selected < 0 || selected > int.MaxValue)
                throw ArchiveSourceIdentity.InvalidManifest (
                    "Manifest selected count is outside the supported range.");
            Selected = (int)selected;
        }

        static ExtractionManifestEntryState ReadEntry (JObject record)
        {
            long index = ArchiveSourceIdentity.RequiredInt64 (record, "entryIndex");
            if (index < 0 || index > int.MaxValue)
                throw ArchiveSourceIdentity.InvalidManifest (
                    "Manifest entryIndex is outside the supported range.");
            long? actual = null;
            JToken actual_token = record["actualBytes"];
            if (null != actual_token && JTokenType.Null != actual_token.Type)
            {
                long parsed;
                if (!long.TryParse (actual_token.ToString(), NumberStyles.Integer,
                                    CultureInfo.InvariantCulture, out parsed)
                    || parsed < 0)
                {
                    throw ArchiveSourceIdentity.InvalidManifest (
                        "Manifest actualBytes is invalid.");
                }
                actual = parsed;
            }
            return new ExtractionManifestEntryState {
                EntryIndex = (int)index,
                EntryName = ArchiveSourceIdentity.RequiredString (record, "entryName"),
                OutputRelativePath = ArchiveSourceIdentity.RequiredString (
                    record, "outputRelativePath"),
                Status = ArchiveSourceIdentity.RequiredString (record, "status"),
                ActualBytes = actual,
                OutputSha256 = (string)record["outputSha256"],
            };
        }
    }

    internal sealed class ExtractionManifestWriter : IDisposable
    {
        readonly string m_path;
        readonly StreamWriter m_writer;

        public ExtractionManifestWriter (
            string path, bool append, OverwriteMode overwrite,
            ArchiveSourceIdentity source_identity, string handler_tag,
            IDictionary<string, object> handler_options_identity,
            ArchivePlan plan, ExtractionManifestOptions options)
        {
            m_path = path;
            string directory = Path.GetDirectoryName (path);
            Directory.CreateDirectory (directory);
            FileStream stream = null;
            try
            {
                if (append)
                {
                    DetachExistingFile (path, true);
                    stream = new FileStream (
                        path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
                    if (options.RepairTrailingPartial)
                    {
                        RepairTrailingPartial (
                            stream, options.TrailingRecordPrefix);
                    }
                    EnsureAppendBoundary (stream);
                }
                else if (File.Exists (path))
                {
                    if (OverwriteMode.Replace != overwrite)
                    {
                        throw CliException.Conflict (
                            "manifest_exists",
                            "The extraction manifest already exists: " + path,
                            new Dictionary<string, object> { { "path", path } });
                    }
                    DetachExistingFile (path, false);
                    stream = new FileStream (
                        path, FileMode.Open, FileAccess.Write, FileShare.Read);
                }
                else
                {
                    stream = new FileStream (
                        path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                }
                m_writer = new StreamWriter (
                    stream, new UTF8Encoding (false));
                stream = null;
                m_writer.AutoFlush = true;
            }
            finally
            {
                if (null != stream)
                    stream.Dispose();
            }
            if (!append)
            {
                WriteRecord (new Dictionary<string, object> {
                    { "schemaVersion", ExtractionManifestState.SchemaVersion },
                    { "record", "header" },
                    { "createdUtc", DateTime.UtcNow.ToString (
                        "o", CultureInfo.InvariantCulture) },
                    { "programVersion", MachineOutput.ProgramVersion },
                    { "sourceArchive", source_identity.ToDictionary() },
                    { "handler", new Dictionary<string, object> {
                        { "tag", handler_tag },
                        { "optionsIdentity", handler_options_identity
                            ?? new Dictionary<string, object>() },
                    } },
                    { "destination", plan.Destination },
                    { "archiveEntryCount", plan.ArchiveEntryCount },
                    { "selected", plan.Entries.Count },
                    { "duplicatePolicy", ArchivePlanner.DuplicatePolicyName (
                        plan.DuplicatePolicyMode) },
                    { "planFingerprint", plan.PlanFingerprint },
                    { "outputChecksum", options.ChecksumName() },
                });
            }
        }

        static void DetachExistingFile (string path, bool copy_existing)
        {
            string temporary = path + ".garbro-"
                + Guid.NewGuid().ToString ("N") + ".manifest";
            try
            {
                using (var output = new FileStream (
                    temporary, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None))
                {
                    if (copy_existing)
                    {
                        using (var input = new FileStream (
                            path, FileMode.Open, FileAccess.Read,
                            FileShare.Read))
                        {
                            input.CopyTo (output);
                        }
                    }
                    output.Flush (true);
                }
                File.Replace (temporary, path, null, true);
            }
            finally
            {
                if (File.Exists (temporary))
                {
                    try { File.Delete (temporary); }
                    catch { }
                }
            }
        }

        static void RepairTrailingPartial (FileStream stream, string record_prefix)
        {
            long boundary = 0;
            for (long position = stream.Length - 1; position >= 0; --position)
            {
                stream.Position = position;
                if ('\n' != stream.ReadByte())
                    continue;
                boundary = position + 1;
                break;
            }
            stream.SetLength (boundary);
            stream.Position = boundary;
            if (!string.IsNullOrEmpty (record_prefix))
            {
                byte[] encoded = new UTF8Encoding (false).GetBytes (record_prefix);
                stream.Write (encoded, 0, encoded.Length);
                stream.WriteByte ((byte)'\n');
            }
        }

        static void EnsureAppendBoundary (FileStream stream)
        {
            if (0 == stream.Length)
                return;
            stream.Position = stream.Length - 1;
            int last = stream.ReadByte();
            stream.Position = stream.Length;
            if ('\n' != last)
                stream.WriteByte ((byte)'\n');
        }

        public void WriteEntry (
            ArchivePlanEntry entry, string status, FileWriteResult result)
        {
            WriteEntry (entry, status, result, null, null);
        }

        public void WriteEntry (
            ArchivePlanEntry entry, string status, FileWriteResult result,
            string error_code, string error_message)
        {
            var record = new Dictionary<string, object> {
                { "schemaVersion", ExtractionManifestState.SchemaVersion },
                { "record", "entry" },
                { "recordedUtc", DateTime.UtcNow.ToString (
                    "o", CultureInfo.InvariantCulture) },
                { "entryIndex", entry.EntryIndex },
                { "entryName", entry.Entry.Name },
                { "entryType", entry.Entry.Type },
                { "offset", entry.Entry.Offset },
                { "storedBytes", entry.Entry.Size },
                { "declaredBytes", entry.DeclaredBytes },
                { "declaredBytesSource", entry.DeclaredBytesSource },
                { "outputSizeKnown", null != result },
                { "materializedSizeMayDiffer", true },
                { "occurrence", entry.Occurrence },
                { "groupSize", entry.GroupSize },
                { "outputRelativePath", entry.OutputRelativePath },
                { "status", status },
            };
            if (null != result)
            {
                record["actualBytes"] = result.BytesWritten;
                if (!string.IsNullOrEmpty (result.Sha256))
                    record["outputSha256"] = result.Sha256;
            }
            if (!string.IsNullOrEmpty (error_code))
            {
                record["error"] = new Dictionary<string, object> {
                    { "code", error_code },
                    { "message", error_message ?? string.Empty },
                };
            }
            WriteRecord (record);
        }

        public void WriteSummary (string status, IDictionary<string, object> counts)
        {
            var record = new Dictionary<string, object> {
                { "schemaVersion", ExtractionManifestState.SchemaVersion },
                { "record", "summary" },
                { "recordedUtc", DateTime.UtcNow.ToString (
                    "o", CultureInfo.InvariantCulture) },
                { "status", status },
                { "counts", counts },
            };
            WriteRecord (record);
        }

        void WriteRecord (object record)
        {
            try
            {
                m_writer.WriteLine (JsonConvert.SerializeObject (
                    record, Formatting.None));
            }
            catch (Exception exception)
            {
                if (exception is IOException || exception is UnauthorizedAccessException)
                {
                    throw new IOException (
                        "Could not update extraction manifest: " + m_path, exception);
                }
                throw;
            }
        }

        public void Dispose ()
        {
            m_writer.Dispose();
        }
    }
}
