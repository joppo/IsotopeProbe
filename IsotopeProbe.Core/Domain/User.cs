namespace IsotopeProbe.Domain;

public sealed class User
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string? DisplayName { get; set; }
    public string? Email { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class ExternalLogin
{
    public string Provider { get; set; } = "";
    public string Subject { get; set; } = "";
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
}

public sealed class Group
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string NormalizedName { get; set; } = "";
    public string? Description { get; set; }
}

public sealed class UserGroup
{
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    public int GroupId { get; set; }
    public Group Group { get; set; } = null!;
}
