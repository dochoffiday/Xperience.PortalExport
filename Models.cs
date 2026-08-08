namespace ExportXperiencePortal;

public record ExportResult(
    DateTimeOffset ExportedAt,
    List<Dictionary<string, string>> Outages,
    List<Dictionary<string, string>> Alerts,
    List<Dictionary<string, string>> Exceptions,
    List<Dictionary<string, string>> EventLog
);
