using System.Diagnostics;
using IsotopeProbe.Domain;

namespace IsotopeProbe.Nuclei;

public sealed class NucleiRunner(NucleiFindingParser parser)
{
    public async Task<ScanExecution> RunAsync(
        string target, string? templatePath = null,
        CancellationToken cancellationToken = default)
    {
        var findings = new List<Finding>();
        var startInfo = CreateStartInfo(target, templatePath);

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

        return new ScanExecution
        {
            Target = target,
            StartedAt = startedAt,
            CompletedAt = completedAt,
            ExitCode = process.ExitCode,
            StandardError = standardError,
            Findings = findings
        };
    }

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
