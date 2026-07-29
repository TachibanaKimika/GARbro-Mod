using System;
using System.IO;
using GameRes;
using GARbro.GUI.Strings;

namespace GARbro.GUI
{
    internal class KrkrDumpRunRequest
    {
        public string SourceArchive { get; set; }
        public string GameExecutable { get; set; }
        public string OutputDirectory { get; set; }
    }

    internal class KrkrDumpRuntimeMissingException : FileNotFoundException
    {
        public string Architecture { get; private set; }

        public KrkrDumpRuntimeMissingException (string message, string architecture)
            : base (message)
        {
            Architecture = architecture;
        }
    }

    internal class KrkrDumpRunner
    {
        public const string CommandName = "KiriKiri.KrkrDump";
        public const string SourceRepositoryUrl =
            HxV4KrkrDumpRunner.SourceRepositoryUrl;

        public ResourceParameterCommandResult Run (KrkrDumpRunRequest request, Action<string> report_status)
        {
            try
            {
                var runner = new HxV4KrkrDumpRunner();
                var shared_request = new HxV4KrkrDumpRunRequest {
                    SourceArchive = request.SourceArchive,
                    GameExecutable = request.GameExecutable,
                    OutputDirectory = request.OutputDirectory,
                    Elevate = true,
                };
                var result = runner.Run (shared_request, status => {
                    switch (status)
                    {
                    case "preparing_runtime":
                        Report (report_status, Text ("KrkrDumpPreparingRuntime"));
                        break;
                    case "launching":
                        Report (report_status, Text ("KrkrDumpLaunching"));
                        break;
                    case "waiting_for_game_exit":
                        Report (report_status, Text ("KrkrDumpWaitingExit"));
                        break;
                    case "collecting_output":
                        Report (report_status, Text ("KrkrDumpCollectingOutput"));
                        break;
                    }
                });
                if (!result.Success)
                    throw new InvalidDataException (result.Message);
                result.Message = Text ("KrkrDumpFinished");
                return result;
            }
            catch (HxV4KrkrDumpRuntimeMissingException X)
            {
                var message = X.Message.IndexOf ("incomplete", StringComparison.OrdinalIgnoreCase) >= 0
                    ? Text ("KrkrDumpRuntimeIncomplete")
                    : string.Format (Text ("KrkrDumpRuntimeNotFound"), X.Architecture);
                throw new KrkrDumpRuntimeMissingException (message, X.Architecture);
            }
        }

        static void Report (Action<string> report_status, string status)
        {
            if (null != report_status)
                report_status (status);
        }

        static string Text (string name)
        {
            return guiStrings.ResourceManager.GetString (name) ?? name;
        }
    }
}
