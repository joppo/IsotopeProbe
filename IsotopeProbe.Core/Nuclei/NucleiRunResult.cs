namespace IsotopeProbe.Nuclei;

public sealed record NucleiRunResult(int? ExitCode, string StandardError, string? FailureReason, bool Cancelled);
