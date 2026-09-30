using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace CopyPilot.Core.Services;

/// <summary>Runs azcopy as a child process and streams its output line by line.</summary>
public sealed partial class AzCopyRunner
{
    private Process? _current;
    private bool _stdinIsPipe;
    private bool _suspended;

    [LibraryImport("ntdll.dll")]
    private static partial int NtSuspendProcess(IntPtr processHandle);

    [LibraryImport("ntdll.dll")]
    private static partial int NtResumeProcess(IntPtr processHandle);

    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static partial int SysKill(int pid, int signal);

    private const int SigStop = 19; // Linux
    private const int SigCont = 18;

    /// <summary>True while the running azcopy process is paused by <see cref="Suspend"/>.</summary>
    public bool IsSuspended => _suspended;

    /// <summary>Pauses the running azcopy process (used to keep the staging folder small). Returns false if nothing was paused.</summary>
    public bool Suspend()
    {
        var p = _current;
        if (p == null || _suspended || p.HasExited) return false;
        _suspended = OperatingSystem.IsWindows() ? NtSuspendProcess(p.Handle) == 0 : SysKill(p.Id, SigStop) == 0;
        return _suspended;
    }

    /// <summary>Resumes a process paused by <see cref="Suspend"/>.</summary>
    public void Resume()
    {
        var p = _current;
        if (p == null || !_suspended) return;
        try
        {
            if (!p.HasExited)
            {
                if (OperatingSystem.IsWindows()) NtResumeProcess(p.Handle);
                else SysKill(p.Id, SigCont);
            }
        }
        finally
        {
            _suspended = false;
        }
    }

    /// <summary>
    /// Sends a line to azcopy's stdin, e.g. an answer to an interactive prompt
    /// (--overwrite=prompt, --delete-destination=prompt). Ignored when stdin is used for a pipe.
    /// </summary>
    public async Task SendInputAsync(string line)
    {
        var p = _current;
        if (p == null || _stdinIsPipe || p.HasExited) return;
        await p.StandardInput.WriteLineAsync(line).ConfigureAwait(false);
        await p.StandardInput.FlushAsync().ConfigureAwait(false);
    }

    /// <summary>Runs azcopy and returns its exit code. Cancelling kills the process tree.</summary>
    /// <param name="onLine">Called for every output line; the bool is true for stderr.</param>
    public async Task<int> RunAsync(
        string exe,
        CommandPlan plan,
        IReadOnlyDictionary<string, string> environment,
        Action<string, bool> onLine,
        CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            RedirectStandardInput = true,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (plan.StdoutFile == null) psi.StandardOutputEncoding = Encoding.UTF8;
        foreach (var arg in plan.Arguments) psi.ArgumentList.Add(arg);
        foreach (var (key, value) in environment)
        {
            if (!string.IsNullOrWhiteSpace(value)) psi.Environment[key] = value;
        }

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null) onLine(e.Data, true);
        };

        if (!process.Start())
            throw new InvalidOperationException("azcopy could not be started.");

        process.BeginErrorReadLine();

        Task stdoutTask;
        if (plan.StdoutFile != null)
        {
            stdoutTask = CopyToFileAsync(process.StandardOutput.BaseStream, plan.StdoutFile, cancellationToken);
        }
        else
        {
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data != null) onLine(e.Data, false);
            };
            process.BeginOutputReadLine();
            stdoutTask = Task.CompletedTask;
        }

        _stdinIsPipe = plan.StdinFile != null;
        _current = process;
        var stdinTask = plan.StdinFile != null
            ? FeedStdinAsync(process, plan.StdinFile, cancellationToken)
            : Task.CompletedTask;

        await using var reg = cancellationToken.Register(() =>
        {
            try
            {
                Resume();
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // already exited
            }
        });

        try
        {
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _current = null;
        }

        try
        {
            await Task.WhenAll(stdinTask, stdoutTask).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        {
            if (!cancellationToken.IsCancellationRequested) onLine("Pipe error: " + ex.Message, true);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return process.ExitCode;
    }

    private static async Task FeedStdinAsync(Process process, string file, CancellationToken ct)
    {
        try
        {
            await using var input = File.OpenRead(file);
            await input.CopyToAsync(process.StandardInput.BaseStream, ct).ConfigureAwait(false);
        }
        finally
        {
            process.StandardInput.Close();
        }
    }

    private static async Task CopyToFileAsync(Stream stdout, string file, CancellationToken ct)
    {
        await using var output = File.Create(file);
        await stdout.CopyToAsync(output, ct).ConfigureAwait(false);
    }

    /// <summary>Runs azcopy with the given arguments and returns all of its output (used for --version).</summary>
    public static async Task<string> CaptureAsync(string exe, IEnumerable<string> args, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("azcopy could not be started.");
        using var cts = new CancellationTokenSource(timeout);
        var stdout = p.StandardOutput.ReadToEndAsync(cts.Token);
        var stderr = p.StandardError.ReadToEndAsync(cts.Token);
        try
        {
            await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch (InvalidOperationException) { }
            throw new TimeoutException("azcopy did not respond in time.");
        }

        return ((await stdout.ConfigureAwait(false)) + (await stderr.ConfigureAwait(false))).Trim();
    }
}
