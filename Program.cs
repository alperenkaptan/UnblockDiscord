namespace UnblockDiscord;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        using var instance = new Mutex(true, "Local\\UnblockDiscord", out var created);
        if (!created) return;
        ApplicationConfiguration.Initialize();
        Application.Run(new Form1());
    }
}
