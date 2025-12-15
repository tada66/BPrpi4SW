using System.IO;
using System.Runtime.CompilerServices;

internal static class Logger
{
    private static readonly object _lock = new();

    private static readonly Lazy<string> _LogFilePath = new(() => 
    {
        string logDir = Path.Combine(AppContext.BaseDirectory, "logs");
        Directory.CreateDirectory(logDir);
        string logFile = Path.Combine(logDir, $"log_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
        return logFile;
    });

    public static void Info(string message,
        [CallerMemberName] string caller = "",
        [CallerFilePath] string callerFile = "",
        [CallerLineNumber] int callerLine = 0)
    {
        string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        string shortFile = Path.GetFileName(callerFile);
        string fullMessage = $"[INFO] {timestamp} [{shortFile}:{callerLine} {caller}] - {message}";

        Console.WriteLine(fullMessage);

        lock (_lock)
        {
            File.AppendAllText(_LogFilePath.Value, fullMessage + Environment.NewLine);
        }
    }

    public static void Notice(string message,
        [CallerMemberName] string caller = "",
        [CallerFilePath] string callerFile = "",
        [CallerLineNumber] int callerLine = 0)
    {
        string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        string shortFile = Path.GetFileName(callerFile);
        string fullMessage = $"[NOTICE] {timestamp} [{shortFile}:{callerLine} {caller}] - {message}";

        Console.WriteLine(fullMessage);

        lock (_lock)
        {
            File.AppendAllText(_LogFilePath.Value, fullMessage + Environment.NewLine);
        }
    }

    public static void Warn(string message,
        [CallerMemberName] string caller = "",
        [CallerFilePath] string callerFile = "",
        [CallerLineNumber] int callerLine = 0)
    {
        string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        string shortFile = Path.GetFileName(callerFile);
        string fullMessage = $"[WARN] {timestamp} [{shortFile}:{callerLine} {caller}] - {message}";

        Console.WriteLine(fullMessage);

        lock (_lock)
        {
            File.AppendAllText(_LogFilePath.Value, fullMessage + Environment.NewLine);
        }
    }

    public static void Error(string message,
        [CallerMemberName] string caller = "",
        [CallerFilePath] string callerFile = "",
        [CallerLineNumber] int callerLine = 0)
    {
        string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        string shortFile = Path.GetFileName(callerFile);
        string fullMessage = $"[ERROR] {timestamp} [{shortFile}:{callerLine} {caller}] - {message}";

        Console.WriteLine(fullMessage);

        lock (_lock)
        {
            File.AppendAllText(_LogFilePath.Value, fullMessage + Environment.NewLine);
        }
    }
}