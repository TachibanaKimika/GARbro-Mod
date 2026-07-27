using System;
using System.Diagnostics;
using System.Text;

namespace GARbro.Cli
{
    internal static class Program
    {
        [STAThread]
        public static int Main (string[] args)
        {
            Console.OutputEncoding = new UTF8Encoding (false);
            Console.CancelKeyPress += (sender, event_args) => {
                CancellationState.Request();
                event_args.Cancel = true;
            };

            try
            {
                var application = new CliApplication();
                return application.Run (args);
            }
            catch (Exception exception)
            {
                Trace.WriteLine (exception);
                Console.Error.WriteLine ("GARbro CLI startup failed: " + exception.Message);
                return (int)ExitCode.InternalError;
            }
        }
    }
}
