namespace Search.Core.Models;

/// <summary>
/// Sync paths (Kafka job events, job-svc HTTP fallback) replace the whole ES document.
/// Fields the incoming payload does not carry must be copied from the indexed version,
/// otherwise every update resets them (review #15).
/// </summary>
public static class JobDocumentMerge
{
    /// <param name="incoming">Document built from the payload; updated in place and returned.</param>
    /// <param name="existing">Currently indexed document, or null for a new job.</param>
    /// <param name="createdAtKnown">The payload carried the job's original creation time.</param>
    /// <param name="skillsKnown">The payload carried a skills list (job events never do).</param>
    public static JobDocument KeepStableFields(JobDocument incoming, JobDocument? existing, bool createdAtKnown, bool skillsKnown)
    {
        if (existing is null)
            return incoming;

        if (!createdAtKnown && existing.CreatedAt != default)
            incoming.CreatedAt = existing.CreatedAt;

        if (!skillsKnown && existing.Skills is { Count: > 0 })
            incoming.Skills = existing.Skills;

        return incoming;
    }
}
