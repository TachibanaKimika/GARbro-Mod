using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using GameRes;

namespace GARbro.Cli
{
    internal sealed class RuntimeContext : IDisposable
    {
        readonly FormatCatalog m_catalog;
        readonly TextWriterTraceListener m_trace_listener;
        ParameterRequestInfo m_parameter_request;

        public FormatCatalog Catalog { get { return m_catalog; } }
        public IList<IDictionary<string, object>> Warnings { get; private set; }

        public RuntimeContext (bool verbose)
        {
            Warnings = new List<IDictionary<string, object>>();
            if (verbose)
            {
                m_trace_listener = new TextWriterTraceListener (Console.Error);
                Trace.Listeners.Add (m_trace_listener);
                Trace.AutoFlush = true;
            }
            m_catalog = FormatCatalog.Instance;
            m_catalog.ParametersRequest += OnParametersRequest;
            LoadSchemes();
        }

        public ArcFile OpenArchive (string path)
        {
            string full_path = RequireFile (path);
            BeginRecognition();
            ArcFile archive = ArcFile.TryOpen (full_path);
            if (null != archive)
                return archive;
            ThrowRecognitionFailure (full_path);
            return null;
        }

        public string RequireFile (string path)
        {
            if (string.IsNullOrWhiteSpace (path))
                throw CliException.Invalid ("empty_path", "Input path cannot be empty.");
            string full_path;
            try
            {
                full_path = Path.GetFullPath (path);
            }
            catch (Exception exception)
            {
                if (exception is ArgumentException || exception is NotSupportedException
                    || exception is PathTooLongException)
                {
                    throw CliException.Invalid ("invalid_path",
                        "Input path is invalid: " + path);
                }
                throw;
            }
            if (!File.Exists (full_path))
                throw CliException.Invalid ("file_not_found",
                    "Input file does not exist: " + full_path,
                    new Dictionary<string, object> { { "path", full_path } });
            return full_path;
        }

        public string RequireDirectory (string path)
        {
            if (string.IsNullOrWhiteSpace (path))
                throw CliException.Invalid ("empty_path", "Input path cannot be empty.");
            string full_path;
            try
            {
                full_path = Path.GetFullPath (path);
            }
            catch (Exception exception)
            {
                if (exception is ArgumentException || exception is NotSupportedException
                    || exception is PathTooLongException)
                {
                    throw CliException.Invalid ("invalid_path",
                        "Input path is invalid: " + path);
                }
                throw;
            }
            if (!Directory.Exists (full_path))
                throw CliException.Invalid ("directory_not_found",
                    "Input directory does not exist: " + full_path,
                    new Dictionary<string, object> { { "path", full_path } });
            return full_path;
        }

        public void BeginRecognition ()
        {
            m_parameter_request = null;
            m_catalog.LastError = null;
        }

        public void ThrowRecognitionFailure (string path)
        {
            if (null != m_parameter_request
                || m_catalog.LastError is OperationCanceledException)
            {
                ThrowNeedsInput (path);
            }
            throw CliException.Unrecognized (
                "File format was not recognized: " + path,
                new Dictionary<string, object> { { "path", path } });
        }

        public void TranslateParameterCancellation (string source)
        {
            if (CancellationState.IsRequested)
                throw new OperationCanceledException ("Operation canceled.");
            if (null != m_parameter_request)
                ThrowNeedsInput (source);
            throw new OperationCanceledException (
                "The resource operation was canceled by its format handler.");
        }

        void ThrowNeedsInput (string source)
        {
            var details = null != m_parameter_request
                ? m_parameter_request.ToDictionary()
                : new Dictionary<string, object>();
            if (!details.ContainsKey ("sourceFileName")
                && !string.IsNullOrEmpty (source))
            {
                details["sourceFileName"] = source;
            }
            throw new CliException (
                ExitCode.NeedsInput, "needs_input", "resource_parameters_required",
                "The resource requires format-specific parameters that are unavailable in non-interactive mode.",
                details);
        }

        void OnParametersRequest (object sender, ParametersRequestEventArgs args)
        {
            var resource = sender as IResource;
            m_parameter_request = new ParameterRequestInfo {
                Notice = args.Notice,
                ResourceTag = null != resource ? resource.Tag : null,
                ResourceType = null != resource ? resource.Type : null,
                SourceFileName = null != args.Context ? args.Context.SourceFileName : null,
            };
            // InputResult deliberately remains false. The CLI never guesses passwords,
            // schemes, or other format-specific choices.
        }

        void LoadSchemes ()
        {
            LoadScheme (Path.Combine (m_catalog.DataDirectory, "Formats.dat"), "bundled");
            string local_app_data = Environment.GetFolderPath (
                Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrEmpty (local_app_data))
            {
                LoadScheme (Path.Combine (local_app_data, "Onachi", "Onachi-GARbro",
                                         "Formats.dat"), "user");
            }
        }

        void LoadScheme (string path, string source)
        {
            try
            {
                if (!File.Exists (path))
                    return;
                using (var input = File.OpenRead (path))
                    m_catalog.DeserializeScheme (input);
            }
            catch (Exception exception)
            {
                Trace.WriteLine (exception.ToString(), "[GARbro.Cli.Scheme]");
                Warnings.Add (new Dictionary<string, object> {
                    { "code", "scheme_load_failed" },
                    { "message", "A format scheme database could not be loaded." },
                    { "details", new Dictionary<string, object> {
                        { "source", source },
                        { "path", path },
                        { "exceptionType", exception.GetType().FullName },
                    } },
                });
            }
        }

        public void Dispose ()
        {
            m_catalog.ParametersRequest -= OnParametersRequest;
            if (null != m_trace_listener)
            {
                Trace.Listeners.Remove (m_trace_listener);
                m_trace_listener.Flush();
                m_trace_listener.Dispose();
            }
        }
    }

    internal sealed class ParameterRequestInfo
    {
        public string Notice;
        public string ResourceTag;
        public string ResourceType;
        public string SourceFileName;

        public Dictionary<string, object> ToDictionary ()
        {
            var result = new Dictionary<string, object>();
            if (!string.IsNullOrEmpty (Notice))
                result["notice"] = Notice;
            if (!string.IsNullOrEmpty (ResourceTag))
                result["resourceTag"] = ResourceTag;
            if (!string.IsNullOrEmpty (ResourceType))
                result["resourceType"] = ResourceType;
            if (!string.IsNullOrEmpty (SourceFileName))
                result["sourceFileName"] = SourceFileName;
            return result;
        }
    }
}
