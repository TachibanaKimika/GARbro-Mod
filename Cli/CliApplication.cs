using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace GARbro.Cli
{
    internal enum ExitCode
    {
        Success = 0,
        UsageError = 2,
        InvalidInput = 3,
        Unrecognized = 4,
        NeedsInput = 5,
        Conflict = 6,
        PartialSuccess = 7,
        IoError = 8,
        InternalError = 9,
    }

    internal sealed class CliException : Exception
    {
        public ExitCode ExitCode { get; private set; }
        public string Status { get; private set; }
        public string Code { get; private set; }
        public object Details { get; private set; }

        public CliException (ExitCode exit_code, string status, string code,
                             string message, object details = null, Exception inner = null)
            : base (message, inner)
        {
            ExitCode = exit_code;
            Status = status;
            Code = code;
            Details = details;
        }

        public static CliException Usage (string code, string message)
        {
            return new CliException (ExitCode.UsageError, "usage_error", code, message);
        }

        public static CliException Invalid (string code, string message, object details = null)
        {
            return new CliException (ExitCode.InvalidInput, "invalid_input", code, message, details);
        }

        public static CliException Unrecognized (string message, object details = null)
        {
            return new CliException (ExitCode.Unrecognized, "unrecognized",
                                     "format_not_recognized", message, details);
        }

        public static CliException Conflict (string code, string message, object details = null)
        {
            return new CliException (ExitCode.Conflict, "conflict", code, message, details);
        }
    }

    internal static class CancellationState
    {
        static volatile bool s_requested;

        public static bool IsRequested { get { return s_requested; } }

        public static void Request ()
        {
            s_requested = true;
        }

        public static void ThrowIfRequested ()
        {
            if (s_requested)
                throw new OperationCanceledException ("Operation canceled.");
        }
    }

    internal sealed class CliApplication
    {
        public int Run (string[] args)
        {
            MachineOutput output = new MachineOutput (ParsedCommand.InferOutputFormat (args));
            string command_name = InferCommandName (args);
            try
            {
                ParsedCommand command = ParsedCommand.Parse (args);
                command_name = command.CommandName;
                if (!string.Equals (command.OutputFormat,
                                    ParsedCommand.InferOutputFormat (args),
                                    StringComparison.Ordinal))
                {
                    throw new InvalidOperationException (
                        "Output format inference did not match parsed options.");
                }
                if (command.Help || "help" == command.Group)
                {
                    CompleteHelp (output);
                    return (int)ExitCode.Success;
                }

                using (var runtime = new RuntimeContext (command.Verbose))
                {
                    output.AddWarnings (runtime.Warnings);
                    ExitCode result = Dispatch (runtime, command, output);
                    return (int)result;
                }
            }
            catch (CliException exception)
            {
                CompleteError (output, command_name, exception);
                return (int)exception.ExitCode;
            }
            catch (OperationCanceledException exception)
            {
                var error = new CliException (
                    ExitCode.InvalidInput, "canceled", "operation_canceled", exception.Message);
                CompleteError (output, command_name, error);
                return (int)error.ExitCode;
            }
            catch (UnauthorizedAccessException exception)
            {
                var error = new CliException (
                    ExitCode.IoError, "io_error", "access_denied", exception.Message, null, exception);
                CompleteError (output, command_name, error);
                return (int)error.ExitCode;
            }
            catch (IOException exception)
            {
                var error = new CliException (
                    ExitCode.IoError, "io_error", "io_failure", exception.Message, null, exception);
                CompleteError (output, command_name, error);
                return (int)error.ExitCode;
            }
            catch (Exception exception)
            {
                Trace.WriteLine (exception);
                var details = new Dictionary<string, object> {
                    { "exceptionType", exception.GetType().FullName },
                };
                var error = new CliException (
                    ExitCode.InternalError, "internal_error", "unhandled_exception",
                    exception.Message, details, exception);
                CompleteError (output, command_name, error);
                return (int)error.ExitCode;
            }
        }

        static ExitCode Dispatch (RuntimeContext runtime, ParsedCommand command, MachineOutput output)
        {
            switch (command.CommandName)
            {
            case "capabilities":
                return ResourceCommands.Capabilities (runtime, command, output);
            case "formats.list":
                return ResourceCommands.FormatsList (runtime, command, output);
            case "probe":
                return ResourceCommands.Probe (runtime, command, output);
            case "archive.list":
                return ArchiveCommands.List (runtime, command, output);
            case "archive.extract":
                return ArchiveCommands.Extract (runtime, command, output);
            case "script.extract":
                return ResourceCommands.ScriptExtract (runtime, command, output);
            case "image.info":
                return ResourceCommands.ImageInfo (runtime, command, output);
            case "image.convert":
                return ResourceCommands.ImageConvert (runtime, command, output);
            case "hxv4.schemes":
                return HxV4Commands.Schemes (runtime, command, output);
            case "hxv4.hash":
                return HxV4Commands.Hash (runtime, command, output);
            case "hxv4.generate":
                return HxV4Commands.Generate (runtime, command, output);
            case "hxv4.generate-archive":
                return HxV4Commands.GenerateArchive (runtime, command, output);
            case "hxv4.clean":
                return HxV4Commands.Clean (runtime, command, output);
            case "hxv4.find-missing-voices":
                return HxV4Commands.FindMissingVoices (runtime, command, output);
            case "hxv4.restore-structure":
                return HxV4Commands.RestoreStructure (runtime, command, output);
            case "hxv4.rename":
                return HxV4Commands.Rename (runtime, command, output);
            case "hxv4.krkrdump":
                return HxV4Commands.KrkrDump (runtime, command, output);
            case "hxv4.krkrdump-import":
                return HxV4Commands.KrkrDumpImport (runtime, command, output);
            default:
                throw CliException.Usage ("unknown_action",
                    "Unknown command or action: " + command.CommandName);
            }
        }

        static void CompleteError (MachineOutput output, string command, CliException exception)
        {
            if (output.Completed)
                return;
            var error = new Dictionary<string, object> {
                { "code", exception.Code },
                { "message", exception.Message },
            };
            if (null != exception.Details)
                error["details"] = exception.Details;
            output.Complete (command, exception.Status, null, error);
        }

        static string InferCommandName (string[] args)
        {
            if (null == args || 0 == args.Length)
                return "unknown";
            if (args.Length > 1 && !args[1].StartsWith ("--", StringComparison.Ordinal))
                return args[0].ToLowerInvariant() + "." + args[1].ToLowerInvariant();
            return args[0].TrimStart ('-', '/').ToLowerInvariant();
        }

        static void CompleteHelp (MachineOutput output)
        {
            const string usage =
                "Onachi-GARbro.Cli.exe COMMAND [ACTION] [ARGUMENTS] [OPTIONS]\n"
              + "Commands:\n"
              + "  capabilities\n"
              + "  formats list [--kind all|archive|image|audio|script]\n"
              + "  probe PATH\n"
              + "  archive list ARCHIVE\n"
              + "  archive extract ARCHIVE --destination DIR [--entry GLOB]\n"
              + "  script extract PATH --destination DIR [--entry ARCHIVE_ENTRY] --mode MODE\n"
              + "  image info IMAGE\n"
              + "  image convert IMAGE --format TAG_OR_EXTENSION --destination DIR\n"
              + "  hxv4 schemes\n"
              + "  hxv4 hash NAME --kind file|path\n"
              + "  hxv4 generate --destination FILE [--source-dir DIR] [--source-file FILE] [--krkrdump-dir DIR] [--seed FILE]\n"
              + "  hxv4 generate-archive ARCHIVE --scheme NAME --destination FILE [--seed FILE]\n"
              + "  hxv4 clean HXNAMES --deobfuscated-dir DIR --destination FILE\n"
              + "  hxv4 find-missing-voices --voice-dir DIR [--voice-dir DIR]\n"
              + "  hxv4 restore-structure DIR [--recursive] [--dry-run]\n"
              + "  hxv4 rename DIR --names HXNAMES [--dry-run]\n"
              + "  hxv4 krkrdump ARCHIVE --game-executable EXE --destination DIR [--run-only] [--same-directory]\n"
              + "  hxv4 krkrdump-import ARCHIVE --result-dir DIR [--game-executable EXE] [--same-directory]\n"
              + "Global options: --output json|jsonl|text --verbose --non-interactive";
            if (output.IsText)
            {
                output.WriteText (usage);
                output.Complete ("help", "success", null);
            }
            else
            {
                output.Complete ("help", "success", new Dictionary<string, object> {
                    { "usage", usage },
                });
            }
        }
    }
}
