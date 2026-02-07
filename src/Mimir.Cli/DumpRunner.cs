namespace Mimir.Cli;

public static class DumpRunner
{
    public static void Run(string[] files)
    {
        foreach (var f in files)
        {
            try
            {
                DiagnosticDump.DumpShnFile(f);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error dumping {f}: {ex.Message}");
            }
        }
    }
}
