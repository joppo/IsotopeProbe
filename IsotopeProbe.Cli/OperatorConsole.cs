using IsotopeProbe.Identity;
using IsotopeProbe.Persistence;

namespace IsotopeProbe.Cli;

public static class OperatorConsole
{
    public static bool IsOperation(string[] args) => args.Length > 0 &&
        (args[0] == "groups" || (args[0] == "scans" && args.Length > 1 && args[1] is "assign" or "recover-web"));

    public static void Validate(string[] args)
    {
        if (args.Length == 4 && args[0] == "scans" && args[1] == "recover-web" && args[3] == "--confirmed-stopped")
        { PositiveId(args[2]); return; }
        if (args.Length >= 6 && args[0] == "scans" && args[1] == "assign" && args[2] == "--user" && args[4] == "--executions")
        {
            UserId(args[3]);
            foreach (var id in args.Skip(5)) PositiveId(id);
            return;
        }
        if (args.Length >= 2 && args[0] == "groups")
        {
            if (args[1] == "create" && args.Length is 3 or 4 && !string.IsNullOrWhiteSpace(args[2])) return;
            if (args[1] == "memberships" && args.Length == 4 && args[2] == "--user") { UserId(args[3]); return; }
            if (args[1] is "add" or "remove" && args.Length == 6 && args[2] == "--user" && args[4] == "--group")
            { UserId(args[3]); PositiveId(args[5]); return; }
        }
        throw new ArgumentException("Usage: scans assign --user <uuid> --executions <id> [<id> ...]; groups create <name> [description]; groups memberships --user <uuid>; groups add|remove --user <uuid> --group <id>.");
    }

    public static async Task<int> RunAsync(string[] args, IsotopeProbeDbContext db, CancellationToken token)
    {
        Validate(args);
        if (args[0] == "scans" && args[1] == "recover-web")
        {
            await new IsotopeProbe.Queue.WebScanRecovery(db).MarkInterruptedFailedAsync(PositiveId(args[2]), token);
            Console.WriteLine("Marked selected interrupted Web execution Failed; saved findings retained.");
            return 0;
        }
        var groups = new GroupService(db);
        if (args[0] == "scans")
        {
            await new UserService(db).AssignUnownedAsync(UserId(args[3]), args.Skip(5).Select(PositiveId).ToArray(), token);
            Console.WriteLine("Assigned all selected unowned executions.");
        }
        else if (args[1] == "create")
        {
            var group = await groups.CreateAsync(args[2], args.Length == 4 ? args[3] : null, token);
            Console.WriteLine($"Created group {group.Id}: {group.Name}");
        }
        else if (args[1] == "memberships")
        {
            await new UserService(db).RequireUserAsync(UserId(args[3]), token);
            foreach (var group in await groups.MembershipsAsync(UserId(args[3]), token))
                Console.WriteLine($"{group.Id}: {group.Name}");
        }
        else if (args[1] == "add") await groups.AddAsync(UserId(args[3]), PositiveId(args[5]), token);
        else await groups.RemoveAsync(UserId(args[3]), PositiveId(args[5]), token);
        return 0;
    }

    private static Guid UserId(string value) => Guid.TryParse(value, out var id) && id != Guid.Empty
        ? id : throw new ArgumentException("User ID must be a nonempty internal UUID.");
    private static int PositiveId(string value) => int.TryParse(value, out var id) && id > 0
        ? id : throw new ArgumentException("Execution and group IDs must be positive integers.");
}
