namespace IsotopeProbe.Domain;

public sealed class Target
{
    public int Id { get; set; }
    public Guid OwnerUserId { get; set; }
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
