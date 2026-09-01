using System.Diagnostics;
using IsotopeProbe.Domain;

namespace IsotopeProbe.Nuclei;

public sealed class NucleiRunner(NucleiFindingParser parser)
{
    public async Task<ScanResult> RunAsync(
        string target,
        CancellationToken cancellationToken = default)
    {
        var findings = new List<Finding>();
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
        startInfo.ArgumentList.Add("-jsonl");
        startInfo.ArgumentList.Add("-silent");

        using var process = new Process { StartInfo = startInfo };
        var startedAt = DateTimeOffset.UtcNow;

        try
        {
            process.Start();
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new InvalidOperationException(
                "Unable to start Nuclei. Ensure the 'nuclei' executable is installed and available on PATH.",
                exception);
        }

        var standardErrorTask = process.StandardError.ReadToEndAsync(cancellationToken);

        while (await process.StandardOutput.ReadLineAsync(cancellationToken) is { } line)
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                findings.Add(parser.Parse(line));
            }
        }

        await process.WaitForExitAsync(cancellationToken);
        var standardError = await standardErrorTask;
        var completedAt = DateTimeOffset.UtcNow;

        var execution = new ScanExecutionInfo(
            target,
            startedAt,
            completedAt,
            process.ExitCode,
            standardError);

        return new ScanResult(findings.AsReadOnly(), execution);
    }
}
