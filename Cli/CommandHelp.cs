using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace GARbro.Cli
{
    internal static class CommandHelp
    {
        const string ProgramName = "Onachi-GARbro.Cli.exe";

        static readonly string[] s_group_order = {
            "formats", "archive", "script", "image", "hxv4",
        };

        static readonly HelpOption[] s_global_options = {
            Option ("output", "FORMAT", "Select stdout representation.",
                false, false, "json", new[] { "json", "jsonl", "text" }, true),
            Flag ("verbose", "Write diagnostics to stderr.", true),
            Flag ("non-interactive", "State that no interactive input is allowed.", true),
            Flag ("help", "Show help for the selected command or group.", true),
        };

        static readonly IDictionary<string, HelpCommand> s_commands = CreateCommands();

        public static void Complete (ParsedCommand command, MachineOutput output)
        {
            string topic = ResolveTopic (command);
            IDictionary<string, object> data;
            string text;
            if (string.IsNullOrEmpty (topic))
            {
                data = RootData();
                text = RootText();
            }
            else
            {
                HelpCommand command_spec;
                if (s_commands.TryGetValue (topic, out command_spec))
                {
                    data = CommandData (command_spec);
                    text = CommandText (command_spec);
                }
                else if (s_group_order.Contains (
                    topic, StringComparer.OrdinalIgnoreCase))
                {
                    data = GroupData (topic);
                    text = GroupText (topic);
                }
                else
                {
                    throw CliException.Usage (
                        "unknown_help_topic", "Unknown help topic: " + topic);
                }
            }

            if (output.IsText)
            {
                output.WriteText (text);
                output.Complete ("help", "success", null);
            }
            else
            {
                output.Complete ("help", "success", data);
            }
        }

        static string ResolveTopic (ParsedCommand command)
        {
            if (!string.Equals (command.Group, "help",
                                StringComparison.OrdinalIgnoreCase))
            {
                return command.CommandName;
            }
            if (command.Positionals.Count > 2)
            {
                throw CliException.Usage (
                    "invalid_help_topic",
                    "help accepts at most a command group and action.");
            }
            if (0 == command.Positionals.Count)
                return null;
            if (1 == command.Positionals.Count)
                return NormalizeTopic (command.Positionals[0]);
            return NormalizeTopic (command.Positionals[0]) + "."
                + NormalizeTopic (command.Positionals[1]);
        }

        static string NormalizeTopic (string value)
        {
            return (value ?? string.Empty).Trim().ToLowerInvariant();
        }

        static IDictionary<string, object> RootData ()
        {
            return new Dictionary<string, object> {
                { "topic", "root" },
                { "kind", "root" },
                { "usage", RootText() },
                { "summary", "Versioned non-interactive GARbro command interface." },
                { "commands", OrderedCommands().Select (CommandSummary).ToArray() },
                { "groups", s_group_order },
                { "options", s_global_options.Select (OptionData).ToArray() },
            };
        }

        static IDictionary<string, object> GroupData (string group)
        {
            var commands = GroupCommands (group).ToArray();
            return new Dictionary<string, object> {
                { "topic", group },
                { "kind", "group" },
                { "usage", ProgramName + " " + group + " ACTION [ARGUMENTS] [OPTIONS]" },
                { "summary", GroupSummary (group) },
                { "commands", commands.Select (CommandSummary).ToArray() },
                { "options", s_global_options.Select (OptionData).ToArray() },
            };
        }

        static IDictionary<string, object> CommandData (HelpCommand command)
        {
            return new Dictionary<string, object> {
                { "topic", command.Name },
                { "kind", "command" },
                { "usage", ProgramName + " " + command.Usage },
                { "summary", command.Summary },
                { "arguments", command.Arguments.Select (ArgumentData).ToArray() },
                { "options", command.Options.Concat (s_global_options)
                    .Select (OptionData).ToArray() },
                { "examples", command.Examples.Select (
                    x => ProgramName + " " + x).ToArray() },
            };
        }

        static IDictionary<string, object> CommandSummary (HelpCommand command)
        {
            return new Dictionary<string, object> {
                { "name", command.Name },
                { "usage", ProgramName + " " + command.Usage },
                { "summary", command.Summary },
            };
        }

        static IDictionary<string, object> ArgumentData (HelpArgument argument)
        {
            return new Dictionary<string, object> {
                { "name", argument.Name },
                { "valueName", argument.ValueName },
                { "required", argument.Required },
                { "repeatable", argument.Repeatable },
                { "description", argument.Description },
            };
        }

        static IDictionary<string, object> OptionData (HelpOption option)
        {
            var result = new Dictionary<string, object> {
                { "name", option.Name },
                { "flag", option.IsFlag },
                { "required", option.Required },
                { "repeatable", option.Repeatable },
                { "global", option.IsGlobal },
                { "description", option.Description },
            };
            if (!string.IsNullOrEmpty (option.ValueName))
                result["valueName"] = option.ValueName;
            if (null != option.DefaultValue)
                result["default"] = option.DefaultValue;
            if (null != option.Choices && option.Choices.Length > 0)
                result["choices"] = option.Choices;
            return result;
        }

        static string RootText ()
        {
            var text = new StringBuilder();
            text.AppendLine ("Usage: " + ProgramName
                + " COMMAND [ACTION] [ARGUMENTS] [OPTIONS]");
            text.AppendLine();
            text.AppendLine ("Commands:");
            foreach (var command in OrderedCommands())
                AppendCommandSummary (text, command);
            AppendOptions (text, s_global_options);
            return text.ToString().TrimEnd();
        }

        static string GroupText (string group)
        {
            var text = new StringBuilder();
            text.AppendLine ("Usage: " + ProgramName + " " + group
                + " ACTION [ARGUMENTS] [OPTIONS]");
            text.AppendLine();
            text.AppendLine (GroupSummary (group));
            text.AppendLine();
            text.AppendLine ("Commands:");
            foreach (var command in GroupCommands (group))
                AppendCommandSummary (text, command);
            AppendOptions (text, s_global_options);
            return text.ToString().TrimEnd();
        }

        static string CommandText (HelpCommand command)
        {
            var text = new StringBuilder();
            text.AppendLine ("Usage: " + ProgramName + " " + command.Usage);
            text.AppendLine();
            text.AppendLine (command.Summary);
            if (command.Arguments.Length > 0)
            {
                text.AppendLine();
                text.AppendLine ("Arguments:");
                foreach (var argument in command.Arguments)
                {
                    text.Append ("  ").Append (argument.ValueName).Append ("  ")
                        .Append (argument.Description);
                    if (argument.Required)
                        text.Append (" (required)");
                    if (argument.Repeatable)
                        text.Append (" (repeatable)");
                    text.AppendLine();
                }
            }
            AppendOptions (text, command.Options.Concat (s_global_options));
            if (command.Examples.Length > 0)
            {
                text.AppendLine();
                text.AppendLine ("Examples:");
                foreach (var example in command.Examples)
                    text.AppendLine ("  " + ProgramName + " " + example);
            }
            return text.ToString().TrimEnd();
        }

        static void AppendCommandSummary (StringBuilder text, HelpCommand command)
        {
            text.Append ("  ").Append (command.Usage).AppendLine();
            text.Append ("      ").AppendLine (command.Summary);
        }

        static void AppendOptions (StringBuilder text, IEnumerable<HelpOption> options)
        {
            var items = options.ToArray();
            if (0 == items.Length)
                return;
            text.AppendLine();
            text.AppendLine ("Options:");
            foreach (var option in items)
            {
                text.Append ("  --").Append (option.Name);
                if (!option.IsFlag)
                    text.Append (' ').Append (option.ValueName);
                text.Append ("  ").Append (option.Description);
                if (option.Required)
                    text.Append (" (required)");
                if (option.Repeatable)
                    text.Append (" (repeatable)");
                if (null != option.DefaultValue)
                {
                    text.Append (" (default: ").Append (
                        Convert.ToString (option.DefaultValue,
                                          CultureInfo.InvariantCulture)).Append (')');
                }
                text.AppendLine();
            }
        }

        static IEnumerable<HelpCommand> OrderedCommands ()
        {
            return s_commands.Values.OrderBy (x => CommandOrder (x.Name))
                .ThenBy (x => x.Name, StringComparer.OrdinalIgnoreCase);
        }

        static IEnumerable<HelpCommand> GroupCommands (string group)
        {
            string prefix = group + ".";
            return s_commands.Values.Where (x => x.Name.StartsWith (
                    prefix, StringComparison.OrdinalIgnoreCase))
                .OrderBy (x => x.Name, StringComparer.OrdinalIgnoreCase);
        }

        static int CommandOrder (string name)
        {
            if ("capabilities" == name)
                return 0;
            if ("probe" == name)
                return 1;
            for (int i = 0; i < s_group_order.Length; ++i)
            {
                if (name.StartsWith (s_group_order[i] + ".",
                                     StringComparison.OrdinalIgnoreCase))
                    return i + 2;
            }
            return int.MaxValue;
        }

        static string GroupSummary (string group)
        {
            switch (group)
            {
            case "formats": return "Discover installed resource handlers.";
            case "archive": return "Inspect or safely extract archives.";
            case "script": return "Export supported game scripts.";
            case "image": return "Inspect or convert images.";
            case "hxv4": return "Manage Hx v4 names and KrkrDump workflows.";
            default: return "GARbro command group.";
            }
        }

        static IDictionary<string, HelpCommand> CreateCommands ()
        {
            var commands = new Dictionary<string, HelpCommand> (
                StringComparer.OrdinalIgnoreCase);
            Add (commands, Command ("capabilities", "capabilities",
                "Report protocol, format, component, and safety capabilities."));
            Add (commands, Command ("formats.list", "formats list [--kind KIND]",
                "List installed resource handlers.",
                Options (Option ("kind", "KIND", "Limit handlers by resource kind.",
                    false, false, "all",
                    new[] { "all", "archive", "image", "audio", "script" })),
                null, "formats list --kind image --output jsonl"));
            Add (commands, Command ("probe", "probe PATH [XP3_SCHEME_OPTIONS]",
                "Recognize one input file, optionally forcing an explicit XP3 scheme.",
                SchemeOptions(),
                Arguments (Argument ("path", "PATH", "Input file to recognize.")),
                "probe data.xp3 --scheme TITLE --output json"));
            Add (commands, Command ("archive.list",
                "archive list ARCHIVE [XP3_SCHEME_OPTIONS] [--summary-only]",
                "List archive metadata and stable zero-based entry indexes.",
                SchemeOptions (
                    Flag ("summary-only", "Suppress per-entry output and return only archive totals.")),
                Arguments (Argument ("archive", "ARCHIVE", "Archive file to inspect.")),
                "archive list data.xp3 --output jsonl"));
            Add (commands, Command ("archive.plan",
                "archive plan ARCHIVE --destination DIR [SELECTION_OPTIONS]",
                "Plan deterministic output paths, duplicate handling, and finite extraction limits.",
                SchemeOptions (
                    Option ("destination", "DIR", "Prospective output directory.", true),
                    Option ("entry", "GLOB", "Entry selection glob; intersects --entry-index.", false, true),
                    Option ("entry-index", "N", "Zero-based archive entry index; intersects --entry.", false, true),
                    Option ("duplicate-policy", "POLICY", "Duplicate destination policy.",
                        false, false, "error", new[] { "error", "suffix-index" }),
                    Flag ("summary-only", "Suppress per-entry plan records.")),
                Arguments (Argument ("archive", "ARCHIVE", "Archive file to plan.")),
                "archive plan voice.xp3 --destination output --duplicate-policy suffix-index --output jsonl"));
            Add (commands, Command ("archive.extract",
                "archive extract ARCHIVE --destination DIR [SELECTION_OPTIONS] [RESUME_OPTIONS]",
                "Safely extract selected logical entries with deterministic collision and resume semantics.",
                SchemeOptions (WriteOptions (
                    Option ("destination", "DIR", "Output directory.", true),
                    Option ("entry", "GLOB", "Entry selection glob; intersects --entry-index.", false, true),
                    Option ("entry-index", "N", "Zero-based archive entry index; intersects --entry.", false, true),
                    Option ("duplicate-policy", "POLICY", "Duplicate destination policy.",
                        false, false, "error", new[] { "error", "suffix-index" }),
                    Option ("budget", "MODE", "Use finite limits recommended by archive plan.",
                        false, false, null, new[] { "auto" }),
                    Option ("manifest", "FILE", "Write a garbro.extraction-manifest/v1 JSONL manifest."),
                    Option ("checksum", "MODE", "Checksum final materialized outputs.",
                        false, false, null, new[] { "none", "sha256" }),
                    Option ("resume", "MODE", "Verify existing outputs against a prior manifest.",
                        false, false, null, new[] { "verify-size", "verify-hash" }),
                    Option ("resume-manifest", "FILE", "Existing manifest; requires --resume."),
                    Flag ("summary-only", "Suppress per-entry output records.")
                )),
                Arguments (Argument ("archive", "ARCHIVE", "Archive file to extract.")),
                "archive extract data.xp3 --destination output --duplicate-policy suffix-index --budget auto --dry-run --output jsonl",
                "archive extract data.xp3 --destination output --manifest extraction.jsonl --checksum sha256 --output jsonl",
                "archive extract data.xp3 --destination output --resume verify-hash --resume-manifest extraction.jsonl --output jsonl"));
            Add (commands, Command ("archive.schemes",
                "archive schemes [--tag XP3] [--filter TEXT]",
                "List safe XP3 scheme descriptors and game-map mappings without key material.",
                Options (
                    Option ("tag", "TAG", "Archive format tag.", false, false,
                        "XP3", new[] { "XP3" }),
                    Option ("filter", "TEXT", "Filter schemes and game-map fields.")),
                null, "archive schemes --filter ExE --output jsonl"));
            Add (commands, Command ("archive.scheme-info",
                "archive scheme-info NAME",
                "Describe one exact, case-insensitive XP3 scheme name and matching game-map rows.",
                null, Arguments (Argument ("name", "NAME", "Scheme name or builtin alias.")),
                "archive scheme-info __NOCRYPT__ --output json"));
            Add (commands, Command ("archive.scheme-check",
                "archive scheme-check ARCHIVE <--scheme NAME | --cx-dump-dir DIR | both> [--hx-names FILE]",
                "Force an XP3 scheme, open the index, and sample recognizable entry headers.",
                SchemeOptions(),
                Arguments (Argument ("archive", "ARCHIVE", "XP3 archive to validate.")),
                "archive scheme-check data.xp3 --scheme TITLE --hx-names HxNames.lst --output json"));
            Add (commands, Command ("script.extract",
                "script extract PATH --mode MODE --destination DIR [--entry NAME]",
                "Export one physical script or exact archive script entry.", WriteOptions (
                    Option ("destination", "DIR", "Output directory.", true),
                    Option ("entry", "NAME", "Exact archive entry name."),
                    Option ("mode", "MODE", "Script text mode.", true, false, null,
                        new[] { "filtered", "raw", "dump", "jsonl" })),
                Arguments (Argument ("path", "PATH", "Script or archive file.")),
                "script extract scenario.ks --mode filtered --destination output"));
            Add (commands, Command ("image.info", "image info IMAGE",
                "Report image handler and metadata.", null,
                Arguments (Argument ("image", "IMAGE", "Image file to inspect.")),
                "image info event.png --output json"));
            Add (commands, Command ("image.convert",
                "image convert IMAGE --format FORMAT --destination DIR",
                "Convert one image with a writable GARbro handler.", WriteOptions (
                    Option ("format", "FORMAT", "Writable handler tag or extension.", true),
                    Option ("destination", "DIR", "Output directory.", true)),
                Arguments (Argument ("image", "IMAGE", "Image file to convert.")),
                "image convert event.png --format WEBP/80 --destination output"));
            Add (commands, Command ("image.convert-batch",
                "image convert-batch --source-root DIR --destination DIR --format FORMAT [OPTIONS]",
                "Convert a directory or manifest of images in one initialized process.",
                WriteOptions (
                    Option ("source-root", "DIR", "Root containing every selected source.", true),
                    Option ("destination", "DIR", "Output directory outside the source root.", true),
                    Option ("format", "FORMAT", "Writable handler tag or extension.", true),
                    Option ("manifest", "FILE", "UTF-8 text or JSONL source manifest."),
                    Flag ("recursive", "Scan source subdirectories."),
                    Flag ("detect-by-signature", "Probe files whose extensions are not known image extensions."),
                    Option ("include", "GLOB", "Relative source-path include glob.", false, true),
                    Option ("resume", "MODE", "Verify existing encoded outputs.",
                        false, false, null, new[] { "verify-header", "verify-decode" }),
                    Option ("budget", "MODE", "Use finite limits derived from the image plan.",
                        false, false, null, new[] { "auto" }),
                    Flag ("summary-only", "Suppress per-image JSONL/text records.")),
                null,
                "image convert-batch --source-root images --destination webp --format WEBP/80 --recursive --detect-by-signature --budget auto --output jsonl"));
            Add (commands, Command ("hxv4.schemes", "hxv4 schemes",
                "List installed Hx v4 schemes."));
            Add (commands, Command ("hxv4.hash", "hxv4 hash VALUE [--kind KIND]",
                "Calculate an Hx v4 file-name or path hash.",
                Options (Option ("kind", "KIND", "Hash kind.", false, false,
                    "file", new[] { "file", "path" })),
                Arguments (Argument ("value", "VALUE", "Name or path to hash.")),
                "hxv4 hash startup.tjs --kind file"));
            Add (commands, Command ("hxv4.generate",
                "hxv4 generate --destination FILE [SOURCE_OPTIONS]",
                "Generate an unfiltered HxNames table from explicit sources.",
                Options (
                    Option ("destination", "FILE", "Output HxNames file.", true),
                    Option ("source-dir", "DIR", "Source directory.", false, true),
                    Option ("source-file", "FILE", "Explicit source file.", false, true),
                    Option ("krkrdump-dir", "DIR", "KrkrDump result directory.", false, true),
                    Option ("seed", "FILE", "Existing HxNames seed file.", false, true),
                    Option ("max-files", "N", "Maximum scanned source files.",
                        false, false, 100000),
                    Flag ("include-garbro-common", "Include common GARbro candidates.")),
                null, "hxv4 generate --source-dir resources --destination HxNames.lst"));
            Add (commands, Command ("hxv4.generate-archive",
                "hxv4 generate-archive ARCHIVE --scheme NAME --destination FILE [--seed FILE]...",
                "Generate mappings retained by real same-directory Hx indexes.",
                Options (
                    Option ("scheme", "NAME", "Installed Hx v4 scheme name.", true),
                    Option ("destination", "FILE", "Output HxNames file.", true),
                    Option ("seed", "FILE", "Existing HxNames seed file.", false, true)),
                Arguments (Argument ("archive", "ARCHIVE", "Hx v4 archive in the game directory.")),
                "hxv4 generate-archive data.xp3 --scheme TITLE --destination HxNames.lst --output jsonl"));
            Add (commands, Command ("hxv4.clean",
                "hxv4 clean HXNAMES --deobfuscated-dir DIR --destination FILE",
                "Keep mappings observed in a deobfuscated tree.",
                Options (
                    Option ("deobfuscated-dir", "DIR", "Extracted resource tree.", true),
                    Option ("destination", "FILE", "Output HxNames file.", true)),
                Arguments (Argument ("hxnames", "HXNAMES", "Source HxNames file."))));
            Add (commands, Command ("hxv4.find-missing-voices",
                "hxv4 find-missing-voices --voice-dir DIR [--voice-dir DIR]...",
                "Report gaps in numeric voice sequences.",
                Options (Option ("voice-dir", "DIR", "Voice directory.", true, true))));
            Add (commands, Command ("hxv4.restore-structure",
                "hxv4 restore-structure DIR [--recursive] [--dry-run]",
                "Restore flattened underscore-separated directory components.",
                Options (
                    Flag ("recursive", "Process subdirectories recursively."),
                    Flag ("dry-run", "Plan changes without writing.")),
                Arguments (Argument ("directory", "DIR", "Extracted resource tree."))));
            Add (commands, Command ("hxv4.rename",
                "hxv4 rename DIR --names HXNAMES [--dry-run]",
                "Rename hashed files and directories from an HxNames table.",
                Options (
                    Option ("names", "HXNAMES", "HxNames table.", true),
                    Flag ("dry-run", "Plan changes without writing.")),
                Arguments (Argument ("directory", "DIR", "Extracted resource tree."))));
            Add (commands, Command ("hxv4.krkrdump",
                "hxv4 krkrdump ARCHIVE --game-executable EXE --destination DIR [OPTIONS]",
                "Run KrkrDump and optionally import its result.",
                Options (
                    Option ("game-executable", "EXE", "Game executable.", true),
                    Option ("destination", "DIR", "Fresh output directory.", true),
                    Option ("tool-directory", "DIR", "Explicit KrkrDump runtime directory."),
                    Flag ("no-elevate", "Do not request Windows elevation."),
                    Flag ("same-directory", "Apply the imported scheme to sibling XP3 files."),
                    Flag ("run-only", "Collect output without importing it.")),
                Arguments (Argument ("archive", "ARCHIVE", "Hx v4 archive."))));
            Add (commands, Command ("hxv4.krkrdump-import",
                "hxv4 krkrdump-import ARCHIVE --result-dir DIR [OPTIONS]",
                "Import an existing KrkrDump result without launching a game.",
                Options (
                    Option ("result-dir", "DIR", "Existing KrkrDump result directory.", true),
                    Option ("game-executable", "EXE", "Optional game executable identity."),
                    Flag ("same-directory", "Apply the imported scheme to sibling XP3 files.")),
                Arguments (Argument ("archive", "ARCHIVE", "Hx v4 archive."))));
            return commands;
        }

        static HelpOption[] WriteOptions (params HelpOption[] command_options)
        {
            return command_options.Concat (new[] {
                Option ("overwrite", "MODE", "Existing destination policy.",
                    false, false, "never", new[] { "never", "skip", "replace" }),
                Flag ("dry-run", "Validate and plan without writing."),
                Option ("max-files", "N", "Maximum output file count.",
                    false, false, ExtractionPolicy.DefaultMaxFiles),
                Option ("max-total-bytes", "N", "Maximum total output bytes.",
                    false, false, ExtractionPolicy.DefaultMaxTotalBytes),
                Option ("max-entry-bytes", "N", "Maximum bytes for one output.",
                    false, false, ExtractionPolicy.DefaultMaxEntryBytes),
                Option ("max-depth", "N", "Maximum relative output path depth.",
                    false, false, ExtractionPolicy.DefaultMaxDepth),
            }).ToArray();
        }

        static HelpOption[] SchemeOptions (params HelpOption[] command_options)
        {
            return command_options.Concat (new[] {
                Option ("scheme", "NAME",
                    "Exact XP3 scheme name or builtin alias (__NOCRYPT__, __YUZUCRYPT__, __XOR-XX__)."),
                Option ("hx-names", "FILE",
                    "Explicit HxNames table applied after the selected Hx v4/Cx scheme."),
                Option ("cx-dump-dir", "DIR",
                    "Explicit KrkrDump/Cx result directory; supersedes --scheme for content decryption."),
            }).ToArray();
        }

        static HelpCommand Command (string name, string usage, string summary,
                                    HelpOption[] options = null,
                                    HelpArgument[] arguments = null,
                                    params string[] examples)
        {
            return new HelpCommand {
                Name = name,
                Usage = usage,
                Summary = summary,
                Options = options ?? new HelpOption[0],
                Arguments = arguments ?? new HelpArgument[0],
                Examples = examples ?? new string[0],
            };
        }

        static void Add (IDictionary<string, HelpCommand> commands,
                         HelpCommand command)
        {
            commands.Add (command.Name, command);
        }

        static HelpArgument[] Arguments (params HelpArgument[] arguments)
        {
            return arguments;
        }

        static HelpArgument Argument (string name, string value_name,
                                      string description, bool required = true,
                                      bool repeatable = false)
        {
            return new HelpArgument {
                Name = name,
                ValueName = value_name,
                Description = description,
                Required = required,
                Repeatable = repeatable,
            };
        }

        static HelpOption[] Options (params HelpOption[] options)
        {
            return options;
        }

        static HelpOption Option (string name, string value_name,
                                  string description, bool required = false,
                                  bool repeatable = false,
                                  object default_value = null,
                                  string[] choices = null,
                                  bool global = false)
        {
            return new HelpOption {
                Name = name,
                ValueName = value_name,
                Description = description,
                Required = required,
                Repeatable = repeatable,
                DefaultValue = default_value,
                Choices = choices,
                IsGlobal = global,
            };
        }

        static HelpOption Flag (string name, string description,
                                bool global = false)
        {
            var option = Option (name, null, description, false, false,
                                 null, null, global);
            option.IsFlag = true;
            return option;
        }

        sealed class HelpCommand
        {
            public string Name;
            public string Usage;
            public string Summary;
            public HelpArgument[] Arguments;
            public HelpOption[] Options;
            public string[] Examples;
        }

        sealed class HelpArgument
        {
            public string Name;
            public string ValueName;
            public string Description;
            public bool Required;
            public bool Repeatable;
        }

        sealed class HelpOption
        {
            public string Name;
            public string ValueName;
            public string Description;
            public bool IsFlag;
            public bool Required;
            public bool Repeatable;
            public bool IsGlobal;
            public object DefaultValue;
            public string[] Choices;
        }
    }
}
