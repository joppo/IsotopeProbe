namespace IsotopeProbe;

// A safe message for callers: do not report a successful save after a DB failure.
public sealed class ScanPersistenceException(string message) : Exception(message);
