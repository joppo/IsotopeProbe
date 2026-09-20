using System.Diagnostics;
using IsotopeProbe.Domain;

namespace IsotopeProbe.Nuclei;

public sealed class NucleiRunner(NucleiFindingParser parser, string executablePath = "nuclei")
{
    public async Task<NucleiRunResult> RunAsync(
        string target, string? templatePath,
        Func<Finding, Task> saveFinding,
        CancellationToken cancellationToken = default, string[]? templateFiles = null)
    {
        using var isolation = templateFiles is null ? null : new ProfileIsolation();
        var startInfo = templateFiles is null ? CreateStartInfo(target, templatePath)
            : CreateProfileStartInfo(target, templateFiles, isolation!.Directory);

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

    internal static void ValidateTemplates(string[] files)
    {
        using var isolation = new ProfileIsolation();
        var start = CreateProfileStartInfo("http://localhost", files, isolation.Directory);
        start.ArgumentList.Add("-validate");
        using var process = new Process { StartInfo = start };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            process.Start();
            var stdout = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, deadline.Token);
            var stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null, deadline.Token);
            process.WaitForExitAsync(deadline.Token).GetAwaiter().GetResult();
            Task.WhenAll(stdout, stderr).GetAwaiter().GetResult();
            if (process.ExitCode != 0) throw new ArgumentException("Nuclei rejected the selected templates. Validate the configured files with nuclei -validate before preparation.");
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or OperationCanceledException)
        { throw new ArgumentException("Nuclei template validation could not complete. Check the installed executable and template syntax.", e); }
        finally
        {
            try { if (!process.HasExited) { process.Kill(true); process.WaitForExit(); } }
            catch (InvalidOperationException) { }
        }
    }

    public async Task<string?> GetVersionAsync(CancellationToken token)
    {
        using var isolation = new ProfileIsolation();
        var start = CreateStartInfo("", null);
        start.FileName = executablePath; start.ArgumentList.Clear(); start.ArgumentList.Add("-version");
        Isolate(start, isolation.Directory);
        using var process = new Process { StartInfo = start };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        process.Start();
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var text = await stdout + await stderr;
            var match = System.Text.RegularExpressions.Regex.Match(text, @"Nuclei Engine Version: (v[0-9]+\.[0-9]+\.[0-9]+(?:[-+][a-zA-Z0-9.-]+)?)");
            return match.Success ? match.Groups[1].Value : null;
        }
        finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(CancellationToken.None); } }
    }

    internal static ProcessStartInfo CreateProfileStartInfo(string target, string[] templates, string directory)
    {
        if (templates.Length == 0) throw new ArgumentException("Empty template selection.");
        var start = CreateStartInfo(target, null);
        foreach (var file in templates) { start.ArgumentList.Add("-t"); start.ArgumentList.Add(file); }
        foreach (var flag in new[] { "-duc", "-ni", "-dr", "-no-color" }) start.ArgumentList.Add(flag);
        start.ArgumentList.Add("-config"); start.ArgumentList.Add(Path.Combine(directory, "config.yaml"));
        Isolate(start, directory);
        start.WorkingDirectory = directory;
        return start;
    }
    private static void Isolate(ProcessStartInfo start, string directory)
    {
        // Do not inherit Nuclei/PDCP/proxy configuration, credentials or template download sources.
        start.Environment.TryGetValue("PATH", out var path);
        start.Environment.Clear();
        start.Environment["PATH"] = path;
        start.Environment["HOME"] = directory;
        start.Environment["XDG_CONFIG_HOME"] = directory;
        start.Environment["XDG_CACHE_HOME"] = directory;
        foreach (var source in new[] { "PUBLIC", "GITHUB", "GITLAB", "AWS", "AZURE" })
            start.Environment["DISABLE_NUCLEI_TEMPLATES_" + source + "_DOWNLOAD"] = "true";
    }
    private sealed class ProfileIsolation : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "isotope-engine-" + Guid.NewGuid().ToString("N"));
        public ProfileIsolation() { System.IO.Directory.CreateDirectory(Directory); File.WriteAllText(Path.Combine(Directory, "config.yaml"), "{}\n"); }
        public void Dispose() { try { System.IO.Directory.Delete(Directory, true); } catch (IOException) { } }
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
