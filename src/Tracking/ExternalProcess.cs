using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

internal class ExternalProcess
{
    // ── Process execution ──

    /// <summary>
    /// Run an external process and capture stdout/stderr.
    /// Accepts a pre-split argument list — avoids shell quoting issues on Linux.
    /// </summary>
    internal static Task<(int exitCode, string stdout, string stderr)> RunProcessAsync(
        string command, string[] argumentList, int timeoutSeconds, CancellationToken ct, string? stdoutFile = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = command,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var arg in argumentList)
            psi.ArgumentList.Add(arg);
        return RunProcessCoreAsync(psi, timeoutSeconds, ct, stdoutFile);
    }

    /// <summary>
    /// Run an external process and capture stdout/stderr.
    /// </summary>
    internal static Task<(int exitCode, string stdout, string stderr)> RunProcessAsync(
        string command, string arguments, int timeoutSeconds, CancellationToken ct, string? stdoutFile = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = command,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        return RunProcessCoreAsync(psi, timeoutSeconds, ct, stdoutFile);
    }

    internal static async Task<(int exitCode, string stdout, string stderr)> RunProcessCoreAsync(
        ProcessStartInfo psi, int timeoutSeconds, CancellationToken ct, string? stdoutFile = null)
    {
        using var process = new Process { StartInfo = psi };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return (-1, "", $"Failed to start {psi.FileName}: {ex.Message}");
        }

        // If redirecting stdout to a file (for RAW conversion)
        if (stdoutFile != null)
        {
            using var fs = File.Create(stdoutFile);
            var copyTask = process.StandardOutput.BaseStream.CopyToAsync(fs, ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            try
            {
                await process.WaitForExitAsync(cts.Token);
                await copyTask;
                string stderr = await stderrTask;
                return (process.ExitCode, "(redirected to file)", stderr);
            }
            catch (OperationCanceledException)
            {
                TryKillProcess(process);
                return (-1, "", "Process timed out or was cancelled");
            }
        }
        else
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            try
            {
                await process.WaitForExitAsync(cts.Token);
                string stdout = await stdoutTask;
                string stderr = await stderrTask;
                return (process.ExitCode, stdout, stderr);
            }
            catch (OperationCanceledException)
            {
                TryKillProcess(process);
                return (-1, "", "Process timed out or was cancelled");
            }
        }
    }

    internal static void TryKillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch { }
    }
}