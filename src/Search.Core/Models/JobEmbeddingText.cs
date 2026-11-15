namespace Search.Core.Models;

/// <summary>
/// Text embedded for a job. The model reads about 128 tokens, so title and skills go first and the text
/// is cut at ai-svc's chunk size (app/nlp/chunks.py MAX_WORDS).
/// </summary>
public static class JobEmbeddingText
{
    public const int MaxWords = 55;

    public static string From(JobDocument job)
    {
        var words = $"{job.Title} {string.Join(' ', job.Skills)} {job.Description}"
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', words.Take(MaxWords));
    }
}
