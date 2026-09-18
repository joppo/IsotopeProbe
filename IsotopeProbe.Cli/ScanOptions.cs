namespace IsotopeProbe.Cli;

public sealed record ScanOptions(string Target, string? TemplatePath, Guid? OwnerUserId = null)
{
    public static ScanOptions Parse(string[] args)
    {
        Guid? owner = null;
        string? target = null;
        string? templatePath = null;

        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--owner")
            {
                if (owner is not null || ++i >= args.Length || !Guid.TryParse(args[i], out var id) || id == Guid.Empty)
                    throw new ArgumentException("--owner requires one nonempty internal user UUID.");
                owner = id;
            }
            else if (args[i] == "-templatepath")
            {
                if (templatePath is not null || ++i >= args.Length ||
                    string.IsNullOrWhiteSpace(args[i]) || args[i].StartsWith('-'))
                    throw new ArgumentException("-templatepath requires a single path.");

                templatePath = args[i];
            }
            else if (target is null && !string.IsNullOrWhiteSpace(args[i]) && !args[i].StartsWith('-'))
            {
                target = args[i];
            }
            else
            {
                throw new ArgumentException("Unexpected argument.");
            }
        }

        if (target is null)
            throw new ArgumentException("A target is required.");

        return new ScanOptions(target, templatePath, owner);
    }
}
