namespace IsotopeProbe.Domain;

public enum ScanStatus { Running = 0, Succeeded = 1, Failed = 2, Cancelled = 3, Queued = 4 }

public enum ScanSource { Cli = 0, Web = 1 }
