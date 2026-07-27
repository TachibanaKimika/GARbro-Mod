using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace GARbro.Cli
{
    internal sealed class ParsedCommand
    {
        readonly Dictionary<string, List<string>> m_options =
            new Dictionary<string, List<string>> (StringComparer.OrdinalIgnoreCase);

        public string CommandName { get; private set; }
        public string Group { get; private set; }
        public string Action { get; private set; }
        public List<string> Positionals { get; private set; }

        public string OutputFormat
        {
            get
            {
                var format = GetSingle ("output", "json").ToLowerInvariant();
                if ("json" != format && "jsonl" != format && "text" != format)
                    throw CliException.Usage ("invalid_output_format",
                        "--output must be one of: json, jsonl, text.");
                return format;
            }
        }

        public bool Verbose { get { return HasFlag ("verbose"); } }
        public bool Help { get { return HasFlag ("help"); } }

        ParsedCommand ()
        {
            Positionals = new List<string>();
        }

        public static ParsedCommand Parse (string[] args)
        {
            if (null == args || 0 == args.Length)
                throw CliException.Usage ("missing_command", "A command is required.");

            var command = new ParsedCommand();
            int index;
            string first = args[0];
            if ("capabilities".Equals (first, StringComparison.OrdinalIgnoreCase)
                || "probe".Equals (first, StringComparison.OrdinalIgnoreCase))
            {
                command.Group = first.ToLowerInvariant();
                command.CommandName = command.Group;
                index = 1;
            }
            else if ("formats".Equals (first, StringComparison.OrdinalIgnoreCase)
                     || "archive".Equals (first, StringComparison.OrdinalIgnoreCase)
                     || "script".Equals (first, StringComparison.OrdinalIgnoreCase)
                     || "image".Equals (first, StringComparison.OrdinalIgnoreCase))
            {
                if (args.Length < 2 || args[1].StartsWith ("--", StringComparison.Ordinal))
                    throw CliException.Usage ("missing_action",
                        string.Format (CultureInfo.InvariantCulture,
                            "Command '{0}' requires an action.", first));
                command.Group = first.ToLowerInvariant();
                command.Action = args[1].ToLowerInvariant();
                command.CommandName = command.Group + "." + command.Action;
                index = 2;
            }
            else if ("help".Equals (first, StringComparison.OrdinalIgnoreCase)
                     || "--help".Equals (first, StringComparison.OrdinalIgnoreCase)
                     || "/?".Equals (first, StringComparison.OrdinalIgnoreCase))
            {
                command.Group = "help";
                command.CommandName = "help";
                command.AddOption ("help", "true");
                index = 1;
            }
            else
            {
                throw CliException.Usage ("unknown_command", "Unknown command: " + first);
            }

            while (index < args.Length)
            {
                string token = args[index++];
                if (!token.StartsWith ("--", StringComparison.Ordinal))
                {
                    command.Positionals.Add (token);
                    continue;
                }

                string name = token.Substring (2);
                if (0 == name.Length)
                    throw CliException.Usage ("invalid_option", "Invalid option '--'.");

                string value = "true";
                int equals = name.IndexOf ('=');
                if (equals >= 0)
                {
                    value = name.Substring (equals+1);
                    name = name.Substring (0, equals);
                }
                else if (!IsFlagOption (name)
                         && index < args.Length
                         && !args[index].StartsWith ("--", StringComparison.Ordinal))
                {
                    value = args[index++];
                }
                command.AddOption (name, value);
            }
            return command;
        }

        void AddOption (string name, string value)
        {
            List<string> values;
            if (!m_options.TryGetValue (name, out values))
            {
                values = new List<string>();
                m_options.Add (name, values);
            }
            values.Add (value);
        }

        public bool HasOption (string name)
        {
            return m_options.ContainsKey (name);
        }

        public bool HasFlag (string name)
        {
            List<string> values;
            if (!m_options.TryGetValue (name, out values))
                return false;
            string value = values.LastOrDefault();
            return string.IsNullOrEmpty (value)
                || "true".Equals (value, StringComparison.OrdinalIgnoreCase)
                || "1" == value
                || "yes".Equals (value, StringComparison.OrdinalIgnoreCase);
        }

        public string GetSingle (string name, string default_value = null)
        {
            List<string> values;
            if (!m_options.TryGetValue (name, out values) || 0 == values.Count)
                return default_value;
            if (values.Count > 1)
                throw CliException.Usage ("duplicate_option",
                    string.Format (CultureInfo.InvariantCulture,
                        "Option '--{0}' can be specified only once.", name));
            return values[0];
        }

        public IList<string> GetMany (string name)
        {
            List<string> values;
            if (!m_options.TryGetValue (name, out values))
                return new string[0];
            return values.AsReadOnly();
        }

        public long GetInt64 (string name, long default_value, long minimum, long maximum)
        {
            string value = GetSingle (name);
            if (null == value)
                return default_value;
            long number;
            if (!long.TryParse (value, NumberStyles.Integer, CultureInfo.InvariantCulture, out number)
                || number < minimum || number > maximum)
            {
                throw CliException.Usage ("invalid_numeric_option",
                    string.Format (CultureInfo.InvariantCulture,
                        "Option '--{0}' must be an integer from {1} through {2}.",
                        name, minimum, maximum));
            }
            return number;
        }

        public string RequirePositional (int index, string label)
        {
            if (index < 0 || index >= Positionals.Count)
                throw CliException.Usage ("missing_argument", "Missing " + label + ".");
            return Positionals[index];
        }

        public void RequirePositionalCount (int count)
        {
            if (Positionals.Count != count)
                throw CliException.Usage ("invalid_argument_count",
                    string.Format (CultureInfo.InvariantCulture,
                        "Command '{0}' expects {1} positional argument(s), but received {2}.",
                        CommandName, count, Positionals.Count));
        }

        public void RejectUnknownOptions (params string[] allowed)
        {
            var names = new HashSet<string> (allowed, StringComparer.OrdinalIgnoreCase) {
                "output", "verbose", "non-interactive", "help"
            };
            var unknown = m_options.Keys.Where (x => !names.Contains (x)).OrderBy (x => x).ToArray();
            if (unknown.Length > 0)
                throw CliException.Usage ("unknown_option",
                    "Unknown option(s): " + string.Join (", ", unknown.Select (x => "--" + x)));
        }

        public static string InferOutputFormat (string[] args)
        {
            if (null == args)
                return "json";
            for (int i = 0; i < args.Length; ++i)
            {
                if (args[i].StartsWith ("--output=", StringComparison.OrdinalIgnoreCase))
                {
                    string value = args[i].Substring ("--output=".Length).ToLowerInvariant();
                    return IsOutputFormat (value) ? value : "json";
                }
                if ("--output".Equals (args[i], StringComparison.OrdinalIgnoreCase)
                    && i+1 < args.Length)
                {
                    string value = args[i+1].ToLowerInvariant();
                    return IsOutputFormat (value) ? value : "json";
                }
            }
            return "json";
        }

        static bool IsOutputFormat (string value)
        {
            return "json" == value || "jsonl" == value || "text" == value;
        }

        static bool IsFlagOption (string name)
        {
            return "verbose".Equals (name, StringComparison.OrdinalIgnoreCase)
                || "non-interactive".Equals (name, StringComparison.OrdinalIgnoreCase)
                || "help".Equals (name, StringComparison.OrdinalIgnoreCase)
                || "dry-run".Equals (name, StringComparison.OrdinalIgnoreCase);
        }
    }
}
