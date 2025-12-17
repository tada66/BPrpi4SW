using System;
using System.IO;
using System.Runtime.CompilerServices;

internal enum LogLevel
{
    Trace = 0,
    Debug = 1,
    Info  = 2,
    Notice = 3,
    Warn  = 4,
    Error = 5,
    Fatal = 6
}

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

    public static LogLevel MinimumLevel { get; set; } = LogLevel.Info;

    private static void Log(string message,
        LogLevel level = LogLevel.Info,
        [CallerMemberName] string caller = "",
        [CallerFilePath] string callerFile = "",
        [CallerLineNumber] int callerLine = 0)
    {

        string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        string shortFile = Path.GetFileName(callerFile);
        string levelTag = level.ToString().ToUpper();
        string fullMessage = $"[{levelTag}] {timestamp} [{shortFile}:{callerLine} {caller}] - {message}";

        lock (_lock)
        {
            if (level >= MinimumLevel)
            {
                var prevColor = Console.ForegroundColor;
                Console.ForegroundColor = GetColor(level);
                Console.WriteLine(fullMessage);
                Console.ForegroundColor = prevColor;
            }

            File.AppendAllText(_LogFilePath.Value, fullMessage + Environment.NewLine);
        }
    }

    private static ConsoleColor GetColor(LogLevel level) => level switch
    {
        LogLevel.Trace => ConsoleColor.DarkGray,
        LogLevel.Debug => ConsoleColor.Gray,
        LogLevel.Info => ConsoleColor.White,
        LogLevel.Notice => ConsoleColor.Cyan,
        LogLevel.Warn => ConsoleColor.Yellow,
        LogLevel.Error => ConsoleColor.Red,
        LogLevel.Fatal => ConsoleColor.Magenta,
        _ => Console.ForegroundColor
    };

    // Convenience wrappers to avoid repeating caller info at call sites
    public static void Trace(string message,
        [CallerMemberName] string caller = "",
        [CallerFilePath] string callerFile = "",
        [CallerLineNumber] int callerLine = 0) =>
        Log(message, LogLevel.Trace, caller, callerFile, callerLine);

    public static void Debug(string message,
        [CallerMemberName] string caller = "",
        [CallerFilePath] string callerFile = "",
        [CallerLineNumber] int callerLine = 0) =>
        Log(message, LogLevel.Debug, caller, callerFile, callerLine);

    public static void Info(string message,
        [CallerMemberName] string caller = "",
        [CallerFilePath] string callerFile = "",
        [CallerLineNumber] int callerLine = 0) =>
        Log(message, LogLevel.Info, caller, callerFile, callerLine);

    public static void Notice(string message,
        [CallerMemberName] string caller = "",
        [CallerFilePath] string callerFile = "",
        [CallerLineNumber] int callerLine = 0) =>
        Log(message, LogLevel.Notice, caller, callerFile, callerLine);

    public static void Warn(string message,
        [CallerMemberName] string caller = "",
        [CallerFilePath] string callerFile = "",
        [CallerLineNumber] int callerLine = 0) =>
        Log(message, LogLevel.Warn, caller, callerFile, callerLine);

    public static void Error(string message,
        [CallerMemberName] string caller = "",
        [CallerFilePath] string callerFile = "",
        [CallerLineNumber] int callerLine = 0) =>
        Log(message, LogLevel.Error, caller, callerFile, callerLine);

    public static void Fatal(string message,
        [CallerMemberName] string caller = "",
        [CallerFilePath] string callerFile = "",
        [CallerLineNumber] int callerLine = 0) =>
        Log(message, LogLevel.Fatal, caller, callerFile, callerLine);
}