using System.Globalization;
using IsotopeProbe.Queries;

namespace IsotopeProbe.Cli;

public enum QueryCommand { ScansList, ScansShow, FindingsList }

public sealed record QueryOptions(QueryCommand Command, int? ScanId, int Skip = 0,
    int Take = TrustedScanQueryService.DefaultTake)
{
    public static bool IsQuery(string[] args) => args.Length > 0 && args[0] is "scans" or "findings";

    public static QueryOptions Parse(string[] args)
    {
        if (args.Length < 2)
            throw new ArgumentException("Specify scans list, scans show <id>, or findings list --scan <id>.");

        if (args[0] == "scans" && args[1] == "show")
        {
            if (args.Length != 3)
                throw new ArgumentException("Usage: scans show <id>.");
            var id = Integer(args[2], "Scan ID");
            TrustedScanQueryService.ValidateScanId(id);
            return new QueryOptions(QueryCommand.ScansShow, id);
        }

        var command = (args[0], args[1]) switch
        {
            ("scans", "list") => QueryCommand.ScansList,
            ("findings", "list") => QueryCommand.FindingsList,
            _ => throw new ArgumentException("Unknown query command.")
        };
        int? scanId = null;
        var skip = 0;
        var take = TrustedScanQueryService.DefaultTake;
        var seen = new HashSet<string>();
        for (var i = 2; i < args.Length; i += 2)
        {
            var option = args[i];
            if (option is not ("--skip" or "--take") &&
                !(command == QueryCommand.FindingsList && option == "--scan"))
                throw new ArgumentException($"Unknown option: {option}.");
            if (!seen.Add(option))
                throw new ArgumentException($"Duplicate option: {option}.");
            if (i + 1 >= args.Length)
                throw new ArgumentException($"{option} requires an integer value.");
            var value = Integer(args[i + 1], option);
            switch (option)
            {
                case "--scan": scanId = value; break;
                case "--skip": skip = value; break;
                case "--take": take = value; break;
            }
        }

        TrustedScanQueryService.ValidatePagination(skip, take);
        if (command == QueryCommand.FindingsList)
        {
            if (scanId is null)
                throw new ArgumentException("findings list requires --scan <id>.");
            TrustedScanQueryService.ValidateScanId(scanId.Value);
        }
        return new QueryOptions(command, scanId, skip, take);
    }

    private static int Integer(string value, string label) =>
        int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number)
            ? number : throw new ArgumentException($"{label} must be an integer.");
}
