namespace Search.Api.DTOs;

public record BulkSyncResponseDto(
    int TotalRequested,
    int TotalIndexed,
    string Message
);
