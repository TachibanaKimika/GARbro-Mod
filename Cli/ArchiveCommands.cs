using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using GameRes;

namespace GARbro.Cli
{
    internal static class ArchiveCommands
    {
        const int LargeJsonItemThreshold = 1000;

        enum ResumeDispositionKind
        {
            Write,
            VerifiedExisting,
            Repair,
        }

        sealed class ResumeDisposition
        {
            public ResumeDispositionKind Kind;
            public ExtractionManifestEntryState ManifestEntry;
        }

        public static ExitCode List (RuntimeContext runtime, ParsedCommand command,
                                     MachineOutput output)
        {
            command.RejectUnknownOptions (
                "summary-only", "scheme", "hx-names", "cx-dump-dir");
            command.RequirePositionalCount (1);
            string path = command.RequirePositional (0, "archive path");
            bool summary_only = command.HasFlag ("summary-only");
            ArchiveSchemeResolution scheme_resolution =
                ArchiveSchemeOptions.Resolve (runtime, command, path);
            using (var archive = runtime.OpenArchive (path, scheme_resolution))
            {
                scheme_resolution = ArchiveSchemeOptions.FinalizeAfterOpen (
                    archive, scheme_resolution, path);
                int entry_count = archive.Dir.Count;
                var archive_data = new Dictionary<string, object> {
                    { "path", Path.GetFullPath (path) },
                    { "tag", archive.Tag },
                    { "description", archive.Description },
                    { "entryCount", entry_count },
                    { "entryIndexBasis", "zeroBasedArchiveDirectoryOrder" },
                };
                if (null != scheme_resolution)
                    archive_data["schemeResolution"] = scheme_resolution.ToDictionary();
                AddLargeJsonWarning (output, entry_count, summary_only);

                if (output.IsJsonLines)
                    output.WriteEvent (command.CommandName, "archive", "success", archive_data);
                List<Dictionary<string, object>> entries =
                    output.IsJson && !summary_only
                        ? new List<Dictionary<string, object>> (entry_count) : null;
                int entry_index = 0;
                foreach (Entry entry in archive.Dir)
                {
                    Dictionary<string, object> entry_data = EntryData (
                        entry, entry_index++);
                    if (null != entries)
                        entries.Add (entry_data);
                    if (!summary_only && output.IsJsonLines)
                        output.WriteEvent (
                            command.CommandName, "entry", "success", entry_data);
                    else if (!summary_only && output.IsText)
                        output.WriteText (FormatEntryText (entry_data));
                }

                var result = new Dictionary<string, object> (archive_data) {
                    { "summaryOnly", summary_only },
                };
                if (null != entries)
                    result["entries"] = entries;
                output.Complete (command.CommandName, "success", result);
            }
            return ExitCode.Success;
        }

        public static ExitCode Plan (RuntimeContext runtime, ParsedCommand command,
                                     MachineOutput output)
        {
            command.RejectUnknownOptions (
                "destination", "entry", "entry-index", "duplicate-policy",
                "summary-only", "scheme", "hx-names", "cx-dump-dir");
            command.RequirePositionalCount (1);
            string archive_path = command.RequirePositional (0, "archive path");
            string destination = command.GetSingle ("destination");
            if (string.IsNullOrWhiteSpace (destination))
            {
                throw CliException.Usage (
                    "missing_destination", "archive plan requires --destination DIR.");
            }
            bool summary_only = command.HasFlag ("summary-only");
            DuplicatePolicy duplicate_policy =
                ArchivePlanner.ParseDuplicatePolicy (command);
            ArchiveSchemeResolution scheme_resolution =
                ArchiveSchemeOptions.Resolve (runtime, command, archive_path);
            using (var archive = runtime.OpenArchive (
                archive_path, scheme_resolution))
            {
                scheme_resolution = ArchiveSchemeOptions.FinalizeAfterOpen (
                    archive, scheme_resolution, archive_path);
                ArchivePlan plan = ArchivePlanner.Build (
                    archive, destination, command.GetMany ("entry"),
                    command.GetMany ("entry-index"), duplicate_policy,
                    null != scheme_resolution
                        ? scheme_resolution.Fingerprint : null);
                AddLargeJsonWarning (output, plan.Entries.Count, summary_only);
                var archive_data = new Dictionary<string, object> {
                    { "archivePath", Path.GetFullPath (archive_path) },
                    { "archiveTag", archive.Tag },
                    { "description", archive.Description },
                    { "entryCount", plan.ArchiveEntryCount },
                };
                if (null != scheme_resolution)
                    archive_data["schemeResolution"] = scheme_resolution.ToDictionary();
                if (output.IsJsonLines)
                    output.WriteEvent (
                        command.CommandName, "archive", "success", archive_data);

                List<Dictionary<string, object>> entries =
                    output.IsJson && !summary_only
                        ? new List<Dictionary<string, object>> (plan.Entries.Count) : null;
                foreach (ArchivePlanEntry entry in plan.Entries)
                {
                    Dictionary<string, object> entry_data = PlanEntryData (entry);
                    if (null != entries)
                        entries.Add (entry_data);
                    if (!summary_only && output.IsJsonLines)
                        output.WriteEvent (
                            command.CommandName, "entry", "planned", entry_data);
                    else if (!summary_only && output.IsText)
                    {
                        output.WriteText (string.Format (
                            CultureInfo.InvariantCulture,
                            "PLAN [{0}] {1} => {2}", entry.EntryIndex,
                            entry.Entry.Name, entry.OutputFullPath));
                    }
                }

                var result = new Dictionary<string, object> (archive_data);
                Merge (result, plan.ToSummaryDictionary());
                result["summaryOnly"] = summary_only;
                if (null != entries)
                    result["entries"] = entries;
                output.Complete (command.CommandName, "success", result);
            }
            return ExitCode.Success;
        }

        public static ExitCode Extract (RuntimeContext runtime, ParsedCommand command,
                                        MachineOutput output)
        {
            command.RejectUnknownOptions (
                "destination", "entry", "entry-index", "overwrite", "dry-run",
                "max-files", "max-total-bytes", "max-entry-bytes", "max-depth",
                "duplicate-policy", "budget", "summary-only", "manifest",
                "checksum", "resume", "resume-manifest", "scheme",
                "hx-names", "cx-dump-dir");
            command.RequirePositionalCount (1);
            string archive_path = command.RequirePositional (0, "archive path");
            string destination = command.GetSingle ("destination");
            if (string.IsNullOrWhiteSpace (destination))
            {
                throw CliException.Usage (
                    "missing_destination",
                    "archive extract requires --destination DIR.");
            }

            bool summary_only = command.HasFlag ("summary-only");
            DuplicatePolicy duplicate_policy =
                ArchivePlanner.ParseDuplicatePolicy (command);
            ExtractionManifestOptions manifest_options =
                ExtractionManifestOptions.FromCommand (command);

            ArchiveSchemeResolution scheme_resolution =
                ArchiveSchemeOptions.Resolve (runtime, command, archive_path);
            using (var archive = runtime.OpenArchive (
                archive_path, scheme_resolution))
            {
                scheme_resolution = ArchiveSchemeOptions.FinalizeAfterOpen (
                    archive, scheme_resolution, archive_path);
                if (manifest_options.Enabled)
                {
                    ArchiveSchemeOptions.RequireExplicitManifestScheme (
                        archive, scheme_resolution);
                }
                ArchivePlan plan = ArchivePlanner.Build (
                    archive, destination, command.GetMany ("entry"),
                    command.GetMany ("entry-index"), duplicate_policy,
                    null != scheme_resolution
                        ? scheme_resolution.Fingerprint : null);
                ExtractionPolicy policy = ExtractionPolicy.FromCommand (
                    command, plan.RecommendedLimits);
                plan.EnsureWithin (policy);
                plan.EnsureDuplicatePolicyCanExtract();
                EnsureInputsAreNotOutputs (
                    archive_path, scheme_resolution, plan, manifest_options);
                manifest_options.EnsureNoOutputCollision (plan);
                EnsureExistingDestinationsAreUsable (plan, policy,
                                                     manifest_options.IsResume);
                manifest_options.ValidateWriteTarget (policy.Overwrite);
                AddLargeJsonWarning (output, plan.Entries.Count, summary_only);

                IDictionary<string, object> handler_options_identity =
                    CreateHandlerOptionsIdentity (scheme_resolution);
                ArchiveSourceIdentity source_identity = null;
                ExtractionManifestState manifest_state = null;
                IDictionary<int, ResumeDisposition> resume_dispositions = null;
                if (manifest_options.Enabled)
                {
                    if (manifest_options.IsResume)
                        manifest_state = ExtractionManifestState.Load (
                            manifest_options.ManifestPath);
                    if (null != manifest_state)
                    {
                        manifest_options.RepairTrailingPartial =
                            manifest_state.IgnoredTrailingPartial;
                        manifest_options.TrailingRecordPrefix =
                            manifest_state.TrailingRecordPrefix;
                    }
                    source_identity = ArchiveSourceIdentity.Create (archive_path);
                    if (manifest_options.IsResume)
                    {
                        manifest_state.Validate (
                            source_identity, archive.Tag,
                            handler_options_identity, plan);
                        resume_dispositions = BuildResumeDispositions (
                            plan, manifest_state, manifest_options, policy);
                    }
                }

                var operation = new Dictionary<string, object> {
                    { "archivePath", Path.GetFullPath (archive_path) },
                    { "archiveTag", archive.Tag },
                    { "destination", plan.Destination },
                    { "selected", plan.Entries.Count },
                    { "duplicatePolicy", ArchivePlanner.DuplicatePolicyName (
                        duplicate_policy) },
                    { "planFingerprint", plan.PlanFingerprint },
                    { "budgetBasis", "declared_metadata_plus_finite_headroom" },
                    { "policy", policy.ToDictionary() },
                    { "summaryOnly", summary_only },
                    { "checksum", manifest_options.ChecksumName() },
                    { "resume", manifest_options.ResumeName() },
                };
                if (manifest_options.Enabled)
                    operation["manifest"] = manifest_options.ManifestPath;
                if (null != scheme_resolution)
                    operation["schemeResolution"] = scheme_resolution.ToDictionary();
                if (output.IsJsonLines)
                    output.WriteEvent (
                        command.CommandName, "start", "running", operation);

                if (policy.DryRun)
                {
                    return CompleteDryRun (
                        command, output, operation, plan, resume_dispositions,
                        manifest_options, summary_only);
                }

                return ExecuteExtraction (
                    runtime, command, output, operation, archive, plan, policy,
                    manifest_options, source_identity, handler_options_identity,
                    resume_dispositions, summary_only);
            }
        }

        static ExitCode CompleteDryRun (
            ParsedCommand command, MachineOutput output,
            IDictionary<string, object> operation, ArchivePlan plan,
            IDictionary<int, ResumeDisposition> resume_dispositions,
            ExtractionManifestOptions manifest_options, bool summary_only)
        {
            int planned = 0;
            int verified = 0;
            long bytes_verified = 0;
            List<Dictionary<string, object>> files =
                output.IsJson && !summary_only
                    ? new List<Dictionary<string, object>> (plan.Entries.Count) : null;
            foreach (ArchivePlanEntry entry in plan.Entries)
            {
                Dictionary<string, object> file_data = FileData (entry);
                ResumeDisposition disposition = GetDisposition (
                    resume_dispositions, entry.EntryIndex);
                if (null != disposition
                    && ResumeDispositionKind.VerifiedExisting == disposition.Kind)
                {
                    ++verified;
                    file_data["status"] = "verified_existing";
                    file_data["outputSizeKnown"] = true;
                    if (null != disposition.ManifestEntry
                        && disposition.ManifestEntry.ActualBytes.HasValue)
                    {
                        long actual = disposition.ManifestEntry.ActualBytes.Value;
                        file_data["actualBytes"] = actual;
                        bytes_verified += actual;
                        AddChecksum (file_data,
                                     disposition.ManifestEntry.OutputSha256);
                    }
                }
                else
                {
                    ++planned;
                    file_data["status"] = "planned";
                    if (null != disposition
                        && ResumeDispositionKind.Repair == disposition.Kind)
                    {
                        file_data["reason"] = "resume_verification_failed";
                    }
                }
                AddFileResult (
                    command, output, files, file_data, summary_only,
                    Convert.ToString (file_data["status"],
                                      CultureInfo.InvariantCulture));
            }

            var result = new Dictionary<string, object> (operation) {
                { "planned", planned },
                { "written", 0 },
                { "repaired", 0 },
                { "verifiedExisting", verified },
                { "bytesVerified", bytes_verified },
                { "skipped", 0 },
                { "failed", 0 },
                { "bytesWritten", 0 },
                { "observedBytes", 0 },
                { "dryRun", true },
                { "manifestWritten", false },
            };
            if (null != files)
                result["files"] = files;
            output.Complete (command.CommandName, "success", result);
            return ExitCode.Success;
        }

        static ExitCode ExecuteExtraction (
            RuntimeContext runtime, ParsedCommand command, MachineOutput output,
            IDictionary<string, object> operation, ArcFile archive,
            ArchivePlan plan, ExtractionPolicy policy,
            ExtractionManifestOptions manifest_options,
            ArchiveSourceIdentity source_identity,
            IDictionary<string, object> handler_options_identity,
            IDictionary<int, ResumeDisposition> resume_dispositions,
            bool summary_only)
        {
            int written = 0;
            int repaired = 0;
            int verified = 0;
            int skipped = 0;
            int failed = 0;
            int not_attempted = 0;
            long committed_bytes = 0;
            long bytes_verified = 0;
            var budget = new ExtractionBudget (policy);
            List<Dictionary<string, object>> files =
                output.IsJson && !summary_only
                    ? new List<Dictionary<string, object>> (plan.Entries.Count) : null;
            List<Dictionary<string, object>> failures =
                output.IsJson && !summary_only
                    ? new List<Dictionary<string, object>>() : null;

            ExtractionManifestWriter manifest_writer = null;
            try
            {
                if (manifest_options.Enabled)
                {
                    manifest_writer = new ExtractionManifestWriter (
                        manifest_options.ManifestPath, manifest_options.IsResume,
                        policy.Overwrite, source_identity, archive.Tag,
                        handler_options_identity, plan, manifest_options);
                }

                for (int plan_index = 0;
                     plan_index < plan.Entries.Count; ++plan_index)
                {
                    ArchivePlanEntry plan_entry = plan.Entries[plan_index];
                    CancellationState.ThrowIfRequested();
                    Dictionary<string, object> file_data = FileData (plan_entry);
                    ResumeDisposition disposition = GetDisposition (
                        resume_dispositions, plan_entry.EntryIndex);
                    if (null != disposition
                        && ResumeDispositionKind.VerifiedExisting == disposition.Kind)
                    {
                        ++verified;
                        file_data["status"] = "verified_existing";
                        file_data["outputSizeKnown"] = true;
                        if (null != disposition.ManifestEntry
                            && disposition.ManifestEntry.ActualBytes.HasValue)
                        {
                            long actual = disposition.ManifestEntry.ActualBytes.Value;
                            file_data["actualBytes"] = actual;
                            bytes_verified += actual;
                            AddChecksum (file_data,
                                         disposition.ManifestEntry.OutputSha256);
                        }
                        AddFileResult (
                            command, output, files, file_data, summary_only,
                            "verified_existing");
                        continue;
                    }

                    if (null == disposition
                        && OverwriteMode.Skip == policy.Overwrite
                        && File.Exists (plan_entry.OutputFullPath))
                    {
                        ++skipped;
                        file_data["status"] = "skipped";
                        file_data["reason"] = "destination_exists";
                        if (null != manifest_writer)
                            manifest_writer.WriteEntry (
                                plan_entry, "skipped", null);
                        AddFileResult (
                            command, output, files, file_data, summary_only,
                            "skipped");
                        continue;
                    }

                    try
                    {
                        runtime.BeginRecognition();
                        FileWriteResult write_result;
                        using (var input = archive.OpenEntry (plan_entry.Entry))
                        {
                            OverwriteMode write_mode = null != disposition
                                && ResumeDispositionKind.Repair == disposition.Kind
                                    ? OverwriteMode.Replace : policy.Overwrite;
                            write_result = SafeFileWriter.CopyToFile (
                                input, plan_entry.OutputFullPath, write_mode, budget,
                                manifest_options.HashOutput);
                        }
                        string file_status = null != disposition
                            && ResumeDispositionKind.Repair == disposition.Kind
                                ? "repaired" : "written";
                        if (null != manifest_writer)
                            manifest_writer.WriteEntry (
                                plan_entry, file_status, write_result);
                        ++written;
                        if ("repaired" == file_status)
                            ++repaired;
                        committed_bytes += write_result.BytesWritten;
                        file_data["status"] = file_status;
                        file_data["actualBytes"] = write_result.BytesWritten;
                        file_data["outputSizeKnown"] = true;
                        AddChecksum (file_data, write_result.Sha256);
                        AddFileResult (
                            command, output, files, file_data, summary_only,
                            "success");
                    }
                    catch (OperationCanceledException)
                    {
                        runtime.TranslateParameterCancellation (plan_entry.Entry.Name);
                        throw;
                    }
                    catch (Exception exception)
                    {
                        var cli_exception = exception as CliException;
                        if (null != cli_exception
                            && cli_exception.ExitCode == ExitCode.NeedsInput)
                        {
                            throw;
                        }
                        string error_code = ErrorCode (exception);
                        if (null != manifest_writer)
                        {
                            try
                            {
                                manifest_writer.WriteEntry (
                                    plan_entry, "failed", null,
                                    error_code, exception.Message);
                            }
                            catch
                            {
                                // A manifest I/O failure must not replace the
                                // extraction exception that caused this record.
                                ExceptionDispatchInfo.Capture (exception).Throw();
                                throw;
                            }
                        }
                        if (null != cli_exception
                            && 0 == written && 0 == skipped && 0 == verified
                            && !HasVerifiedRemaining (
                                resume_dispositions, plan, plan_index))
                        {
                            throw;
                        }
                        ++failed;
                        var failure = new Dictionary<string, object> {
                            { "entry", plan_entry.Entry.Name },
                            { "entryIndex", plan_entry.EntryIndex },
                            { "path", plan_entry.OutputFullPath },
                            { "outputRelativePath", plan_entry.OutputRelativePath },
                            { "code", error_code },
                            { "message", exception.Message },
                        };
                        if (null != failures)
                            failures.Add (failure);
                        file_data["status"] = "failed";
                        file_data["error"] = failure;
                        AddFileResult (
                            command, output, files, file_data, summary_only,
                            "failed");
                        if (!summary_only && output.IsText)
                        {
                            Console.Error.WriteLine (
                                "FAIL [{0}] {1}: {2}", plan_entry.EntryIndex,
                                plan_entry.Entry.Name, exception.Message);
                        }
                        if (null != cli_exception)
                        {
                            for (int remaining_index = plan_index + 1;
                                 remaining_index < plan.Entries.Count;
                                 ++remaining_index)
                            {
                                ArchivePlanEntry remaining =
                                    plan.Entries[remaining_index];
                                ResumeDisposition remaining_disposition =
                                    GetDisposition (
                                        resume_dispositions,
                                        remaining.EntryIndex);
                                if (null != remaining_disposition
                                    && ResumeDispositionKind.VerifiedExisting
                                        == remaining_disposition.Kind)
                                {
                                    ++verified;
                                    Dictionary<string, object> verified_data =
                                        FileData (remaining);
                                    verified_data["status"] =
                                        "verified_existing";
                                    verified_data["outputSizeKnown"] = true;
                                    if (null != remaining_disposition.ManifestEntry
                                        && remaining_disposition.ManifestEntry
                                            .ActualBytes.HasValue)
                                    {
                                        long actual = remaining_disposition
                                            .ManifestEntry.ActualBytes.Value;
                                        verified_data["actualBytes"] = actual;
                                        bytes_verified += actual;
                                        AddChecksum (
                                            verified_data,
                                            remaining_disposition.ManifestEntry
                                                .OutputSha256);
                                    }
                                    AddFileResult (
                                        command, output, files, verified_data,
                                        summary_only, "verified_existing");
                                    continue;
                                }
                                ++not_attempted;
                                Dictionary<string, object> remaining_data =
                                    FileData (remaining);
                                remaining_data["status"] = "not_attempted";
                                remaining_data["reason"] =
                                    "aborted_after_error";
                                if (null != manifest_writer)
                                {
                                    manifest_writer.WriteEntry (
                                        remaining, "not_attempted", null,
                                        "aborted_after_error",
                                        "The entry was not attempted because a prior entry caused the operation to abort.");
                                }
                                AddFileResult (
                                    command, output, files, remaining_data,
                                    summary_only, "not_attempted");
                            }
                            break;
                        }
                    }
                }

                bool partial = failed > 0 || skipped > 0
                    || not_attempted > 0;
                string status = partial ? "partial_success" : "success";
                ExitCode exit_code = partial
                    ? ExitCode.PartialSuccess : ExitCode.Success;
                var result = new Dictionary<string, object> (operation) {
                    { "planned", 0 },
                    { "written", written },
                    { "repaired", repaired },
                    { "verifiedExisting", verified },
                    { "bytesVerified", bytes_verified },
                    { "skipped", skipped },
                    { "failed", failed },
                    { "notAttempted", not_attempted },
                    { "bytesWritten", committed_bytes },
                    { "observedBytes", budget.ObservedBytes },
                    { "dryRun", false },
                    { "manifestWritten", null != manifest_writer },
                };
                if (null != files)
                {
                    result["files"] = files;
                    if (null != failures && failures.Count > 0)
                        result["failures"] = failures;
                }
                if (null != manifest_writer)
                    manifest_writer.WriteSummary (status, ManifestCounts (result));
                output.Complete (command.CommandName, status, result);
                return exit_code;
            }
            finally
            {
                if (null != manifest_writer)
                    manifest_writer.Dispose();
            }
        }

        static IDictionary<int, ResumeDisposition> BuildResumeDispositions (
            ArchivePlan plan, ExtractionManifestState manifest,
            ExtractionManifestOptions options, ExtractionPolicy policy)
        {
            var result = new Dictionary<int, ResumeDisposition>();
            foreach (ArchivePlanEntry entry in plan.Entries)
            {
                ExtractionManifestEntryState state;
                manifest.Entries.TryGetValue (entry.EntryIndex, out state);
                bool exists = File.Exists (entry.OutputFullPath);
                bool valid = false;
                if (exists && null != state && state.HasMaterializedOutput)
                {
                    long length = new FileInfo (entry.OutputFullPath).Length;
                    valid = state.ActualBytes.HasValue
                        && length == state.ActualBytes.Value;
                    if (valid && ExtractionResumeMode.VerifyHash == options.Resume)
                    {
                        if (string.IsNullOrEmpty (state.OutputSha256))
                        {
                            throw CliException.Invalid (
                                "manifest_checksum_missing",
                                "verify-hash requires an output SHA-256 for every completed entry.",
                                new Dictionary<string, object> {
                                    { "entry", entry.Entry.Name },
                                    { "entryIndex", entry.EntryIndex },
                                });
                        }
                        string actual_checksum = Sha256Utility.ComputeFile (
                            entry.OutputFullPath);
                        valid = string.Equals (
                            state.OutputSha256, actual_checksum,
                            StringComparison.OrdinalIgnoreCase);
                    }
                }

                if (valid)
                {
                    result[entry.EntryIndex] = new ResumeDisposition {
                        Kind = ResumeDispositionKind.VerifiedExisting,
                        ManifestEntry = state,
                    };
                }
                else if (!exists)
                {
                    result[entry.EntryIndex] = new ResumeDisposition {
                        Kind = ResumeDispositionKind.Write,
                        ManifestEntry = state,
                    };
                }
                else if (OverwriteMode.Replace == policy.Overwrite)
                {
                    result[entry.EntryIndex] = new ResumeDisposition {
                        Kind = ResumeDispositionKind.Repair,
                        ManifestEntry = state,
                    };
                }
                else
                {
                    throw CliException.Conflict (
                        "resume_verification_failed",
                        "An existing destination did not pass resume verification.",
                        new Dictionary<string, object> {
                            { "entry", entry.Entry.Name },
                            { "entryIndex", entry.EntryIndex },
                            { "path", entry.OutputFullPath },
                            { "verification", options.ResumeName() },
                            { "tracked", null != state && state.HasMaterializedOutput },
                        });
                }
            }
            return result;
        }

        static void EnsureExistingDestinationsAreUsable (
            ArchivePlan plan, ExtractionPolicy policy, bool resume)
        {
            ArchivePlanEntry directory = plan.Entries.FirstOrDefault (
                x => Directory.Exists (x.OutputFullPath));
            if (null != directory)
            {
                throw CliException.Conflict (
                    "destination_is_directory",
                    "A planned output path is an existing directory.",
                    new Dictionary<string, object> {
                        { "entry", directory.Entry.Name },
                        { "entryIndex", directory.EntryIndex },
                        { "path", directory.OutputFullPath },
                    });
            }
            if (resume || OverwriteMode.Never != policy.Overwrite)
                return;
            ArchivePlanEntry conflict = plan.Entries.FirstOrDefault (
                x => File.Exists (x.OutputFullPath));
            if (null != conflict)
            {
                throw CliException.Conflict (
                    "destination_exists",
                    "Destination file already exists: " + conflict.OutputFullPath,
                    new Dictionary<string, object> {
                        { "entry", conflict.Entry.Name },
                        { "entryIndex", conflict.EntryIndex },
                        { "path", conflict.OutputFullPath },
                    });
            }
        }

        static void EnsureInputsAreNotOutputs (
            string archive_path, ArchiveSchemeResolution scheme_resolution,
            ArchivePlan plan, ExtractionManifestOptions manifest_options)
        {
            var inputs = new List<KeyValuePair<string, string>> {
                new KeyValuePair<string, string> (
                    "sourceArchive", Path.GetFullPath (archive_path)),
            };
            if (null != scheme_resolution)
            {
                foreach (string artifact in scheme_resolution.InputArtifactPaths)
                {
                    inputs.Add (new KeyValuePair<string, string> (
                        "schemeArtifact", Path.GetFullPath (artifact)));
                }
            }

            foreach (var input in inputs
                .GroupBy (x => x.Value, StringComparer.OrdinalIgnoreCase)
                .Select (x => x.First()))
            {
                if (manifest_options.Enabled
                    && string.Equals (manifest_options.ManifestPath, input.Value,
                                      StringComparison.OrdinalIgnoreCase))
                {
                    throw CliException.Conflict (
                        "manifest_input_collision",
                        "The extraction manifest path conflicts with an input file.",
                        new Dictionary<string, object> {
                            { "manifest", manifest_options.ManifestPath },
                            { "inputKind", input.Key },
                            { "inputPath", input.Value },
                        });
                }
                ArchivePlanEntry collision = plan.Entries.FirstOrDefault (
                    x => string.Equals (x.OutputFullPath, input.Value,
                                        StringComparison.OrdinalIgnoreCase));
                if (null != collision)
                {
                    throw CliException.Conflict (
                        "output_input_collision",
                        "A planned output path conflicts with an input file.",
                        new Dictionary<string, object> {
                            { "inputKind", input.Key },
                            { "inputPath", input.Value },
                            { "entry", collision.Entry.Name },
                            { "entryIndex", collision.EntryIndex },
                            { "outputPath", collision.OutputFullPath },
                        });
                }
            }
        }

        static ResumeDisposition GetDisposition (
            IDictionary<int, ResumeDisposition> dispositions, int entry_index)
        {
            if (null == dispositions)
                return null;
            ResumeDisposition result;
            return dispositions.TryGetValue (entry_index, out result)
                ? result : null;
        }

        static bool HasVerifiedRemaining (
            IDictionary<int, ResumeDisposition> dispositions,
            ArchivePlan plan, int current_index)
        {
            if (null == dispositions)
                return false;
            for (int index = current_index + 1;
                 index < plan.Entries.Count; ++index)
            {
                ResumeDisposition disposition = GetDisposition (
                    dispositions, plan.Entries[index].EntryIndex);
                if (null != disposition
                    && ResumeDispositionKind.VerifiedExisting
                        == disposition.Kind)
                {
                    return true;
                }
            }
            return false;
        }

        static Dictionary<string, object> EntryData (Entry entry, int entry_index)
        {
            long declared;
            string declared_source;
            ArchivePlanner.GetDeclaredSize (
                entry, out declared, out declared_source);
            var result = new Dictionary<string, object> {
                { "entryIndex", entry_index },
                { "name", entry.Name },
                { "type", entry.Type },
                { "offset", entry.Offset },
                { "size", entry.Size },
                { "storedBytes", entry.Size },
                { "declaredBytes", declared },
                { "declaredBytesSource", declared_source },
                { "outputSizeKnown", false },
                { "materializedSizeMayDiffer", true },
            };
            var packed = entry as PackedEntry;
            if (null != packed)
            {
                result["isPacked"] = packed.IsPacked;
                result["unpackedSize"] = packed.UnpackedSize;
            }
            return result;
        }

        static Dictionary<string, object> PlanEntryData (ArchivePlanEntry entry)
        {
            Dictionary<string, object> result = EntryData (
                entry.Entry, entry.EntryIndex);
            result["entry"] = entry.Entry.Name;
            result["occurrence"] = entry.Occurrence;
            result["groupSize"] = entry.GroupSize;
            result["outputRelativePath"] = entry.OutputRelativePath;
            result["path"] = entry.OutputFullPath;
            result["depth"] = entry.Output.Depth;
            result["destinationExists"] = entry.DestinationExists;
            return result;
        }

        static Dictionary<string, object> FileData (ArchivePlanEntry entry)
        {
            var result = new Dictionary<string, object> {
                { "entry", entry.Entry.Name },
                { "entryIndex", entry.EntryIndex },
                { "occurrence", entry.Occurrence },
                { "groupSize", entry.GroupSize },
                { "offset", entry.Entry.Offset },
                { "path", entry.OutputFullPath },
                { "outputRelativePath", entry.OutputRelativePath },
                { "storedBytes", entry.Entry.Size },
                { "declaredBytes", entry.DeclaredBytes },
                { "declaredBytesSource", entry.DeclaredBytesSource },
                { "outputSizeKnown", false },
                { "materializedSizeMayDiffer", true },
            };
            return result;
        }

        static void AddChecksum (
            IDictionary<string, object> data, string checksum)
        {
            if (string.IsNullOrEmpty (checksum))
                return;
            data["outputSha256"] = checksum;
            data["checksum"] = new Dictionary<string, object> {
                { "algorithm", "sha256" },
                { "value", checksum },
            };
        }

        static void AddFileResult (
            ParsedCommand command, MachineOutput output,
            IList<Dictionary<string, object>> files,
            Dictionary<string, object> file_data, bool summary_only,
            string event_status)
        {
            if (null != files)
                files.Add (file_data);
            if (!summary_only && output.IsJsonLines)
                output.WriteEvent (
                    command.CommandName, "file", event_status, file_data);
            else if (!summary_only && output.IsText
                     && "failed" != event_status)
            {
                output.WriteText (string.Format (
                    CultureInfo.InvariantCulture, "{0} [{1}] {2} => {3}",
                    Convert.ToString (file_data["status"],
                                      CultureInfo.InvariantCulture).ToUpperInvariant(),
                    file_data["entryIndex"], file_data["entry"],
                    file_data["path"]));
            }
        }

        static Dictionary<string, object> ManifestCounts (
            IDictionary<string, object> extraction_result)
        {
            return new Dictionary<string, object> {
                { "selected", extraction_result["selected"] },
                { "written", extraction_result["written"] },
                { "repaired", extraction_result["repaired"] },
                { "verifiedExisting", extraction_result["verifiedExisting"] },
                { "skipped", extraction_result["skipped"] },
                { "failed", extraction_result["failed"] },
                { "notAttempted", extraction_result["notAttempted"] },
                { "bytesWritten", extraction_result["bytesWritten"] },
            };
        }

        static IDictionary<string, object> CreateHandlerOptionsIdentity (
            ArchiveSchemeResolution scheme_resolution)
        {
            return null != scheme_resolution
                ? scheme_resolution.ToManifestIdentity()
                : new Dictionary<string, object>();
        }

        static void AddLargeJsonWarning (
            MachineOutput output, int item_count, bool summary_only)
        {
            if (!output.IsJson || summary_only
                || item_count <= LargeJsonItemThreshold)
            {
                return;
            }
            output.AddWarning (
                "large_json_response",
                "This command is returning a large JSON collection; prefer --output jsonl or --summary-only.",
                new Dictionary<string, object> {
                    { "itemCount", item_count },
                    { "threshold", LargeJsonItemThreshold },
                    { "recommendedOutput", "jsonl" },
                });
        }

        static string FormatEntryText (IDictionary<string, object> entry)
        {
            return string.Format (
                CultureInfo.InvariantCulture, "{0,8} {1,12} {2,-8} {3}",
                entry["entryIndex"], entry["size"], entry["type"], entry["name"]);
        }

        static string ErrorCode (Exception exception)
        {
            var cli = exception as CliException;
            if (null != cli)
                return cli.Code;
            if (exception is UnauthorizedAccessException)
                return "access_denied";
            if (exception is IOException)
                return "io_failure";
            return "entry_extraction_failed";
        }

        static void Merge (
            IDictionary<string, object> target,
            IDictionary<string, object> source)
        {
            foreach (KeyValuePair<string, object> item in source)
                target[item.Key] = item.Value;
        }
    }
}
