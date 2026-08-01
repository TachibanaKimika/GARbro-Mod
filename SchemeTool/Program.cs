using System;

namespace SchemeTool
{
    internal static class Program
    {
        static int Main (string[] args)
        {
            try
            {
                return SchemeDatabaseCommands.Run (args);
            }
            catch (CommandLineException X)
            {
                Console.Error.WriteLine (X.Message);
                return 2;
            }
            catch (SchemeDatabaseException X)
            {
                Console.Error.WriteLine (X.Message);
                return 4;
            }
            catch (Exception X)
            {
                Console.Error.WriteLine (X.ToString());
                return 9;
            }
        }
    }
}
