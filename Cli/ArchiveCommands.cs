using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using GameRes;

namespace GARbro.Cli
{
    internal static class ArchiveCommands
    {
        public static ExitCode List (RuntimeContext runtime, ParsedCommand command,
                                     MachineOutput output)
        {
            command.RejectUnknownOptions();
            command.RequirePositionalCount (1);
            string path = command.RequirePositional (0, "archive path");
            using (var archive = runtime.OpenArchive (path))
            {
                var entries = archive.Dir.Select (EntryData).ToList();
                var archive_data = new Dictionary<string, object> {
                    { "path", Path.GetFullPath (path) },
                    { "tag", archive.Tag },
                    { "description", archive.Description },
                    { "entryCount", entries.Count },
                };

                if (output.IsJsonLines)
                    output.WriteEvent (command.CommandName, "archive", "success", archive_data);
                foreach (var entry in entries)
                {
                    if (output.IsJsonLines)
                        output.WriteEvent (command.CommandName, "entry", "success", entry);
                    else if (output.IsText)
                        output.WriteText (FormatEntryText (entry));
                }

                var result = new Dictionary<string, object> (archive_data);
                if (output.IsJson)
                    result["entries"] = entries;
                output.Complete (command.CommandName, "success", result);
            }
            return ExitCode.Success;
        }

        public static ExitCode Extract (RuntimeContext runtime, ParsedCommand command,
                                        MachineOutput output)
        {
            command.RejectUnknownOptions (
                "destination", "entry", "overwrite", "dry-run",
                "max-files", "max-total-bytes", "max-entry-bytes", "max-depth");
            command.RequirePositionalCount (1);
            string archive_path = command.RequirePositional (0, "archive path");
            string destination = command.GetSingle ("destination");
            if (string.IsNullOrWhiteSpace (destination))
                throw CliException.Usage ("missing_destination",
                    "archive extract requires --destination DIR.");

            ExtractionPolicy policy = ExtractionPolicy.FromCommand (command);
            var resolver = new OutputPathResolver (destination, policy.MaxDepth);
            IList<string> patterns = command.GetMany ("entry");

            using (var archive = runtime.OpenArchive (archive_path))
            {
                var selected = archive.Dir
                    .Where (x => GlobMatcher.IsAnyMatch (x.Name, patterns))
                    .ToList();
                if (selected.Count > policy.MaxFiles)
                {
                    throw CliException.Invalid (
                        "file_count_limit_exceeded",
                        "Selected entry count exceeds --max-files.",
                        new Dictionary<string, object> {
                            { "selected", selected.Count },
                            { "limit", policy.MaxFiles },
                        });
                }
                if (0 == selected.Count)
                {
                    throw CliException.Invalid (
                        "no_entries_selected",
                        "No archive entries matched the requested pattern(s).",
                        new Dictionary<string, object> {
                            { "patterns", patterns },
                        });
                }

                var plans = Preflight (selected, resolver, policy);
                if (OverwriteMode.Never == policy.Overwrite)
                {
                    var conflict = plans.FirstOrDefault (x => File.Exists (x.Destination));
                    if (null != conflict)
                    {
                        throw CliException.Conflict (
                            "destination_exists",
                            "Destination file already exists: " + conflict.Destination,
                            new Dictionary<string, object> {
                                { "entry", conflict.Entry.Name },
                                { "path", conflict.Destination },
                            });
                    }
                }

                var operation = new Dictionary<string, object> {
                    { "archivePath", Path.GetFullPath (archive_path) },
                    { "archiveTag", archive.Tag },
                    { "destination", resolver.Root },
                    { "selected", plans.Count },
                    { "policy", policy.ToDictionary() },
                };
                if (output.IsJsonLines)
                    output.WriteEvent (command.CommandName, "start", "running", operation);

                int written = 0;
                int skipped = 0;
                int failed = 0;
                long committed_bytes = 0;
                var failures = new List<Dictionary<string, object>>();
                var files = new List<Dictionary<string, object>>();
                var budget = new ExtractionBudget (policy);

                foreach (var plan in plans)
                {
                    CancellationState.ThrowIfRequested();
                    var file_data = new Dictionary<string, object> {
                        { "entry", plan.Entry.Name },
                        { "path", plan.Destination },
                        { "declaredBytes", plan.DeclaredSize },
                    };
                    if (policy.DryRun)
                    {
                        file_data["status"] = "planned";
                        files.Add (file_data);
                        if (output.IsJsonLines)
                            output.WriteEvent (command.CommandName, "file", "planned", file_data);
                        else if (output.IsText)
                            output.WriteText ("PLAN " + plan.Entry.Name + " => " + plan.Destination);
                        continue;
                    }

                    if (OverwriteMode.Skip == policy.Overwrite
                        && File.Exists (plan.Destination))
                    {
                        ++skipped;
                        file_data["status"] = "skipped";
                        file_data["reason"] = "destination_exists";
                        files.Add (file_data);
                        if (output.IsJsonLines)
                            output.WriteEvent (command.CommandName, "file", "skipped", file_data);
                        else if (output.IsText)
                            output.WriteText ("SKIP " + plan.Entry.Name);
                        continue;
                    }

                    try
                    {
                        runtime.BeginRecognition();
                        long bytes;
                        using (var input = archive.OpenEntry (plan.Entry))
                        {
                            bytes = SafeFileWriter.CopyToFile (
                                input, plan.Destination, policy.Overwrite, budget);
                        }
                        ++written;
                        committed_bytes += bytes;
                        file_data["status"] = "written";
                        file_data["actualBytes"] = bytes;
                        files.Add (file_data);
                        if (output.IsJsonLines)
                            output.WriteEvent (command.CommandName, "file", "success", file_data);
                        else if (output.IsText)
                            output.WriteText (string.Format (
                                CultureInfo.InvariantCulture, "WRITE {0} ({1} bytes)",
                                plan.Entry.Name, bytes));
                    }
                    catch (OperationCanceledException)
                    {
                        runtime.TranslateParameterCancellation (plan.Entry.Name);
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
                        if (null != cli_exception && 0 == written && 0 == skipped)
                            throw;
                        ++failed;
                        var failure = new Dictionary<string, object> {
                            { "entry", plan.Entry.Name },
                            { "path", plan.Destination },
                            { "code", ErrorCode (exception) },
                            { "message", exception.Message },
                        };
                        failures.Add (failure);
                        file_data["status"] = "failed";
                        file_data["error"] = failure;
                        files.Add (file_data);
                        if (output.IsJsonLines)
                            output.WriteEvent (command.CommandName, "file", "failed", file_data);
                        else if (output.IsText)
                            Console.Error.WriteLine ("FAIL {0}: {1}",
                                                     plan.Entry.Name, exception.Message);
                        if (null != cli_exception)
                            break;
                    }
                }

                bool partial = failed > 0 || skipped > 0;
                string status = partial ? "partial_success" : "success";
                ExitCode exit_code = partial ? ExitCode.PartialSuccess : ExitCode.Success;
                var result = new Dictionary<string, object> (operation) {
                    { "planned", policy.DryRun ? plans.Count : 0 },
                    { "written", written },
                    { "skipped", skipped },
                    { "failed", failed },
                    { "bytesWritten", committed_bytes },
                    { "observedBytes", budget.TotalBytes },
                };
                if (output.IsJson)
                {
                    result["files"] = files;
                    if (failures.Count > 0)
                        result["failures"] = failures;
                }
                output.Complete (command.CommandName, status, result);
                return exit_code;
            }
        }

        static List<ExtractionPlan> Preflight (
            IList<Entry> selected, OutputPathResolver resolver, ExtractionPolicy policy)
        {
            var result = new List<ExtractionPlan> (selected.Count);
            long declared_total = 0;
            foreach (var entry in selected)
            {
                long size = GetDeclaredSize (entry);
                if (size > policy.MaxEntryBytes)
                {
                    throw CliException.Invalid (
                        "entry_size_limit_exceeded",
                        "An entry exceeds --max-entry-bytes.",
                        new Dictionary<string, object> {
                            { "entry", entry.Name },
                            { "size", size },
                            { "limit", policy.MaxEntryBytes },
                        });
                }
                if (declared_total > policy.MaxTotalBytes - size)
                {
                    throw CliException.Invalid (
                        "total_size_limit_exceeded",
                        "Selected entries exceed --max-total-bytes.",
                        new Dictionary<string, object> {
                            { "observed", declared_total + size },
                            { "limit", policy.MaxTotalBytes },
                        });
                }
                declared_total += size;
                result.Add (new ExtractionPlan {
                    Entry = entry,
                    Destination = resolver.Resolve (entry.Name),
                    DeclaredSize = size,
                });
            }
            return result;
        }

        static long GetDeclaredSize (Entry entry)
        {
            var packed = entry as PackedEntry;
            if (null != packed && packed.IsPacked && 0 != packed.UnpackedSize)
                return packed.UnpackedSize;
            return entry.Size;
        }

        static Dictionary<string, object> EntryData (Entry entry)
        {
            var result = new Dictionary<string, object> {
                { "name", entry.Name },
                { "type", entry.Type },
                { "offset", entry.Offset },
                { "size", entry.Size },
            };
            var packed = entry as PackedEntry;
            if (null != packed)
            {
                result["isPacked"] = packed.IsPacked;
                result["unpackedSize"] = packed.UnpackedSize;
            }
            return result;
        }

        static string FormatEntryText (IDictionary<string, object> entry)
        {
            return string.Format (
                CultureInfo.InvariantCulture, "{0,12} {1,-8} {2}",
                entry["size"], entry["type"], entry["name"]);
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

        sealed class ExtractionPlan
        {
            public Entry Entry;
            public string Destination;
            public long DeclaredSize;
        }
    }
}
