using IsotopeProbe.Domain;
using IsotopeProbe.Queries;

namespace IsotopeProbe.Cli;

internal static class QueryConsole
{
    public static async Task<int> RunAsync(QueryOptions options, TrustedScanQueryService queries,
        CancellationToken cancellationToken = default)
    {
        switch (options.Command)
        {
            case QueryCommand.ScansList:
                var scans = await queries.ListScansAsync(options.Skip, options.Take, cancellationToken);
                Console.WriteLine("ID\tSTARTED (UTC)\tSTATUS\tFINDINGS\tTARGET");
                foreach (var scan in scans.Items)
                    Console.WriteLine($"{scan.Id}\t{scan.StartedAt?.UtcDateTime:O}\t{Status(scan)}\t{scan.FindingCount}\t{Clean(scan.Target)}");
                PrintPage(scans, "scans");
                return 0;

            case QueryCommand.ScansShow:
                var details = await queries.GetScanAsync(options.ScanId!.Value, cancellationToken);
                if (details is null)
                    return NotFound(options.ScanId.Value);
                var execution = details.Execution;
                Console.WriteLine($"Scan {execution.Id}: {Clean(execution.Target)}");
                Console.WriteLine($"Started (UTC): {execution.StartedAt?.UtcDateTime:O}");
                Console.WriteLine($"Completed (UTC): {(execution.CompletedAt is { } completed ? completed.UtcDateTime.ToString("O") : "not completed")}");
                Console.WriteLine($"Status: {Status(execution)} (exit code {execution.ExitCode?.ToString() ?? "unavailable"})");
                if (execution.Status == ScanStatus.Running)
                    Console.WriteLine("Running is the last persisted state; it does not prove the process is still alive.");
                if (details.FailureReason is not null)
                    Console.WriteLine($"Failure details: {Clean(details.FailureReason)}");
                Console.WriteLine($"Findings: {execution.FindingCount}");
                foreach (var severity in details.Severities)
                    Console.WriteLine($"  {Clean(severity.Severity)}: {severity.Count}");
                if (!string.IsNullOrWhiteSpace(details.StandardError))
                    Console.WriteLine($"Standard error: {Clean(details.StandardError)}");
                return 0;

            case QueryCommand.FindingsList:
                var findings = await queries.ListFindingsAsync(options.ScanId!.Value,
                    options.Skip, options.Take, cancellationToken);
                if (findings is null)
                    return NotFound(options.ScanId.Value);
                Console.WriteLine($"Findings for scan {options.ScanId}:");
                Console.WriteLine("ID\tTEMPLATE\tSEVERITY\tMATCHED LOCATION");
                foreach (var finding in findings.Items)
                    Console.WriteLine($"{finding.Id}\t{Clean(finding.TemplateId)}\t{Clean(finding.Severity)}\t{Clean(finding.MatchedAt)}");
                PrintPage(findings, "findings");
                return 0;

            default:
                throw new ArgumentOutOfRangeException(nameof(options));
        }
    }

    private static int NotFound(int id)
    {
        Console.Error.WriteLine($"Scan {id} was not found.");
        return 1;
    }

    private static void PrintPage<T>(Page<T> page, string label)
    {
        if (page.TotalCount == 0)
            Console.WriteLine($"No {label} found.");
        else if (page.Items.Count == 0)
            Console.WriteLine($"No {label} on this page ({page.TotalCount} total; skip {page.Skip}).");
        else
            Console.WriteLine($"Showing {page.Items.Count} of {page.TotalCount} {label} (skip {page.Skip}, take {page.Take}).");
    }

    private static string Status(ScanSummary scan) => scan.Status.ToString();

    // Stored values may originate from a remote target. Keep each value on one
    // console line and avoid interpreting terminal control characters.
    private static string Clean(string value) => new(value.Select(c => char.IsControl(c) ? ' ' : c).ToArray());
}
