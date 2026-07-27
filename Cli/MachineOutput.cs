using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace GARbro.Cli
{
    internal sealed class MachineOutput
    {
        public const string SchemaVersion = "garbro.cli/v1";

        readonly string m_format;
        readonly Stopwatch m_stopwatch;
        readonly JsonSerializerSettings m_json_settings;
        readonly List<IDictionary<string, object>> m_warnings =
            new List<IDictionary<string, object>>();

        public string OperationId { get; private set; }
        public bool IsJson { get { return "json" == m_format; } }
        public bool IsJsonLines { get { return "jsonl" == m_format; } }
        public bool IsText { get { return "text" == m_format; } }
        public bool Completed { get; private set; }

        public MachineOutput (string format)
        {
            m_format = format;
            OperationId = Guid.NewGuid().ToString ("N");
            m_stopwatch = Stopwatch.StartNew();
            m_json_settings = new JsonSerializerSettings {
                ContractResolver = new CamelCasePropertyNamesContractResolver(),
                NullValueHandling = NullValueHandling.Ignore,
                Formatting = Formatting.None,
            };
        }

        public void AddWarning (string code, string message, object details = null)
        {
            var warning = new Dictionary<string, object> {
                { "code", code },
                { "message", message },
            };
            if (null != details)
                warning["details"] = details;
            m_warnings.Add (warning);
        }

        public void AddWarnings (IEnumerable<IDictionary<string, object>> warnings)
        {
            if (null == warnings)
                return;
            m_warnings.AddRange (warnings);
        }

        public void WriteEvent (string command, string event_type, string status, object data)
        {
            if (Completed)
                throw new InvalidOperationException ("Output already completed.");
            if (IsText)
            {
                if (null != data)
                    Console.WriteLine (FormatText (data));
                return;
            }
            if (!IsJsonLines)
                return;
            WriteJson (CreateEnvelope (command, status, event_type, data, null, false));
        }

        public void WriteText (string text)
        {
            if (!IsText || string.IsNullOrEmpty (text))
                return;
            Console.WriteLine (text);
        }

        public void Complete (string command, string status, object data)
        {
            Complete (command, status, data, null);
        }

        public void Complete (string command, string status, object data,
                              IDictionary<string, object> error)
        {
            if (Completed)
                throw new InvalidOperationException ("Output already completed.");
            Completed = true;
            m_stopwatch.Stop();
            if (IsText)
            {
                if (null != data)
                    Console.WriteLine (FormatText (data));
                if (null != error)
                    Console.Error.WriteLine (Convert.ToString (error["message"], CultureInfo.CurrentCulture));
                return;
            }

            string event_type = null;
            if (IsJsonLines)
                event_type = null != error ? ("needs_input" == status ? "needs_input" : "error") : "summary";
            WriteJson (CreateEnvelope (command, status, event_type, data, error, true));
        }

        IDictionary<string, object> CreateEnvelope (string command, string status,
                                                     string event_type, object data,
                                                     IDictionary<string, object> error,
                                                     bool include_warnings)
        {
            var envelope = new Dictionary<string, object> {
                { "schemaVersion", SchemaVersion },
                { "programVersion", ProgramVersion },
                { "operationId", OperationId },
                { "command", command },
                { "status", status },
            };
            if (!string.IsNullOrEmpty (event_type))
                envelope["event"] = event_type;
            if (null != data)
                envelope["data"] = data;
            if (include_warnings && m_warnings.Count > 0)
                envelope["warnings"] = m_warnings;
            if (null != error)
                envelope["error"] = error;
            if (include_warnings)
                envelope["durationMs"] = m_stopwatch.ElapsedMilliseconds;
            return envelope;
        }

        void WriteJson (object value)
        {
            Console.WriteLine (JsonConvert.SerializeObject (value, m_json_settings));
        }

        static string FormatText (object value)
        {
            if (null == value)
                return string.Empty;
            var text = value as string;
            if (null != text)
                return text;
            var dictionary = value as IDictionary;
            if (null != dictionary)
            {
                var parts = new List<string>();
                foreach (DictionaryEntry item in dictionary)
                    parts.Add (item.Key + "=" + Convert.ToString (item.Value, CultureInfo.CurrentCulture));
                return string.Join (" ", parts);
            }
            return Convert.ToString (value, CultureInfo.CurrentCulture);
        }

        public static string ProgramVersion
        {
            get
            {
                return Assembly.GetExecutingAssembly().GetName().Version.ToString();
            }
        }
    }
}
