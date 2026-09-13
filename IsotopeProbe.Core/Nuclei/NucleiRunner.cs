using System.Diagnostics;
using IsotopeProbe.Domain;

namespace IsotopeProbe.Nuclei;

public sealed class NucleiRunner(NucleiFindingParser parser, string executablePath = "nuclei")
{
    public async Task<NucleiRunResult> RunAsync(
        string target, string? templatePath,
        Func<Finding, Task> saveFinding,
        CancellationToken cancellationToken = default)
    {
        var startInfo = CreateStartInfo(target, templatePath);
        startInfo.FileName = executablePath;
        using var process = new Process { StartInfo = startInfo };
        using var io = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var started = false;
        var cancelled = false;
        int? exitCode = null;
        string? failure = null;
        var stage = "starting Nuclei";
        long stderrCharacters = 0;
        Task stderrTask = Task.CompletedTask;

        async Task DrainStandardErrorAsync()
        {
            // Never accumulate arbitrary scanner diagnostics: they may include
            // authentication headers, URLs with credentials, or request payloads.
            var buffer = new char[2048];
            try
            {
                int count;
                while ((count = await process.StandardError.ReadAsync(buffer, io.Token)) > 0)
                    Interlocked.Add(ref stderrCharacters, count);
            }
            catch
            {
                io.Cancel(); // Also release stdout if the stderr reader fails.
                throw;
            }
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            process.Start();
            started = true;
            stderrTask = DrainStandardErrorAsync();
            stage = "reading Nuclei output";
            while (await process.StandardOutput.ReadLineAsync(io.Token) is { } line)
            {
                io.Token.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                stage = "parsing a Nuclei finding";
                var finding = parser.Parse(line);
                stage = "saving a finding";
                // The service gives each collected finding a bounded write even
                // when Ctrl+C arrives, before cancellation is observed again.
                await saveFinding(finding);
                stage = "reading Nuclei output";
            }
            stage = "waiting for Nuclei to exit";
            await process.WaitForExitAsync(io.Token);
            await stderrTask;
            exitCode = process.ExitCode;
            if (exitCode != 0)
                failure = $"Nuclei exited with code {exitCode}.";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            cancelled = true;
            failure = "Scan cancellation requested.";
        }
        catch (Exception exception)
        {
            // Exception messages can contain targets, raw JSON or DB values.
            failure = $"Execution failed while {stage} ({exception.GetType().Name}).";
            if (!started && exception is System.ComponentModel.Win32Exception native)
                failure += $" OS error code: {native.NativeErrorCode}. Check the Nuclei executable and PATH.";
        }
        finally
        {
            if (started)
            {
                using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try
                {
                    if (!process.HasExited)
                    {
                        try { process.Kill(entireProcessTree: true); }
                        catch (PlatformNotSupportedException) { process.Kill(); }
                        catch (InvalidOperationException) when (process.HasExited) { }
                    }
                    await process.WaitForExitAsync(shutdown.Token);
                    exitCode = process.ExitCode;
                }
                catch (Exception exception)
                {
                    failure = Append(failure, $"Could not confirm scanner shutdown ({exception.GetType().Name}).");
                }
                finally
                {
                    io.Cancel();
                    try { await stderrTask.WaitAsync(shutdown.Token); }
                    catch (OperationCanceledException) { }
                    catch (Exception exception)
                    {
                        failure = Append(failure, $"Could not finish reading stderr ({exception.GetType().Name}).");
                    }
                }
            }
        }

        var characters = Interlocked.Read(ref stderrCharacters);
        var stderr = characters == 0 ? "" :
            $"Nuclei wrote {characters} stderr characters. Content omitted to avoid storing credentials.";
        return new NucleiRunResult(exitCode, stderr, failure, cancelled);
    }

    private static string Append(string? current, string message) =>
        current is null ? message : $"{current} {message}";

    internal static ProcessStartInfo CreateStartInfo(string target, string? templatePath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "nuclei",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("-u");
        startInfo.ArgumentList.Add(target);
        if (templatePath is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(templatePath);
            if (templatePath == "~" || templatePath.StartsWith("~/", StringComparison.Ordinal))
            {
                templatePath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    templatePath.Length == 1 ? "" : templatePath[2..]);
            }

            startInfo.ArgumentList.Add("-t");
            startInfo.ArgumentList.Add(templatePath);
        }
        startInfo.ArgumentList.Add("-jsonl");
        startInfo.ArgumentList.Add("-silent");

        return startInfo;
    }
}
