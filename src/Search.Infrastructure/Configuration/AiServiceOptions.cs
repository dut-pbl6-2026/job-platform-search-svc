namespace Search.Infrastructure.Configuration;

public class AiServiceOptions
{
    public const string SectionName = "AiService";

    /// <summary>Base URL of ai-svc, e.g. http://ai:6000. Env: AI_SERVICE_URL.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Bearer for ai-svc /api/internal/*. Env: INTERNAL_SERVICE_TOKEN. Empty refuses every call.</summary>
    public string InternalToken { get; set; } = string.Empty;
}
