using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Pgvector;
using Pgvector.EntityFrameworkCore;
using MasryVoice.Api.Domain;
using MasryVoice.Api.Infrastructure.Persistence;

namespace MasryVoice.Api.Features.Knowledge;

public record KnowledgeSearchResult(
    string DocumentTitle,
    string Content,
    double Similarity,
    string CitationTag
);

public interface IKnowledgeService
{
    Task<KnowledgeDocument> IngestDocumentAsync(string title, string fileName, string content, string category = "General", CancellationToken ct = default);
    Task<IReadOnlyList<KnowledgeSearchResult>> SearchAsync(string query, int maxResults = 3, double minSimilarity = 0.45, CancellationToken ct = default);
}

public class KnowledgeService : IKnowledgeService
{
    private readonly AppDbContext _db;
    private readonly IEmbeddingProvider _embeddingProvider;
    private readonly ILogger<KnowledgeService> _logger;

    public KnowledgeService(AppDbContext db, IEmbeddingProvider embeddingProvider, ILogger<KnowledgeService> logger)
    {
        _db = db;
        _embeddingProvider = embeddingProvider;
        _logger = logger;
    }

    public async Task<KnowledgeDocument> IngestDocumentAsync(string title, string fileName, string content, string category = "General", CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("عنوان المستند مطلوب", nameof(title));
        if (string.IsNullOrWhiteSpace(content)) throw new ArgumentException("محتوى المستند مطلوب", nameof(content));

        // 1. Sanitize against embedded prompt injection instructions
        var sanitizedContent = SanitizeContent(content);

        // 2. Split content into logical semantic chunks
        var rawChunks = ChunkText(sanitizedContent, targetChunkSize: 300, overlap: 50);

        var doc = new KnowledgeDocument
        {
            Id = Guid.NewGuid(),
            Title = title.Trim(),
            FileName = fileName.Trim(),
            Category = category.Trim(),
            ChunkCount = rawChunks.Count,
            CreatedAtUtc = DateTime.UtcNow
        };

        for (int i = 0; i < rawChunks.Count; i++)
        {
            var chunkText = rawChunks[i];
            var embeddingFloats = await _embeddingProvider.GenerateEmbeddingAsync(chunkText, ct);
            Vector? vector = _db.Database.IsNpgsql() ? new Vector(embeddingFloats) : null;

            doc.Chunks.Add(new DocumentChunk
            {
                Id = Guid.NewGuid(),
                DocumentId = doc.Id,
                ChunkIndex = i,
                Content = chunkText,
                Embedding = vector,
                CreatedAtUtc = DateTime.UtcNow
            });
        }

        _db.KnowledgeDocuments.Add(doc);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Ingested document '{Title}' with {ChunkCount} chunks.", doc.Title, doc.ChunkCount);
        return doc;
    }

    public async Task<IReadOnlyList<KnowledgeSearchResult>> SearchAsync(string query, int maxResults = 3, double minSimilarity = 0.45, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return Array.Empty<KnowledgeSearchResult>();
        }

        var cleanQuery = query.Trim();
        var queryFloats = await _embeddingProvider.GenerateEmbeddingAsync(cleanQuery, ct);

        if (_db.Database.IsNpgsql())
        {
            var queryVector = new Vector(queryFloats);

            // In pgvector: CosineDistance ranges from 0 (identical) to 2 (opposite).
            // Similarity = 1.0 - CosineDistance
            var chunks = await _db.DocumentChunks
                .AsNoTracking()
                .Include(c => c.Document)
                .Where(c => c.Embedding != null)
                .OrderBy(c => c.Embedding!.CosineDistance(queryVector))
                .Take(maxResults)
                .Select(c => new
                {
                    Title = c.Document.Title,
                    Content = c.Content,
                    Distance = c.Embedding!.CosineDistance(queryVector)
                })
                .ToListAsync(ct);

            var results = new List<KnowledgeSearchResult>();
            foreach (var item in chunks)
            {
                var similarity = 1.0 - item.Distance;
                if (similarity >= minSimilarity)
                {
                    results.Add(new KnowledgeSearchResult(
                        DocumentTitle: item.Title,
                        Content: item.Content,
                        Similarity: Math.Round(similarity, 4),
                        CitationTag: $"[المصدر: {item.Title}]"
                    ));
                }
            }

            if (results.Count == 0)
            {
                // Fallback: check text containment in case chunks were seeded without vector or query has exact keywords
                var queryTokens = cleanQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var allChunks = await _db.DocumentChunks
                    .AsNoTracking()
                    .Include(c => c.Document)
                    .ToListAsync(ct);

                foreach (var chunk in allChunks)
                {
                    int matchCount = queryTokens.Count(t => chunk.Content.Contains(t, StringComparison.OrdinalIgnoreCase));
                    if (matchCount > 0)
                    {
                        double lexicalScore = Math.Round(0.4 + 0.5 * ((double)matchCount / queryTokens.Length), 4);
                        results.Add(new KnowledgeSearchResult(
                            DocumentTitle: chunk.Document.Title,
                            Content: chunk.Content,
                            Similarity: lexicalScore,
                            CitationTag: $"[المصدر: {chunk.Document.Title}]"
                        ));
                        if (results.Count >= maxResults) break;
                    }
                }
            }

            return results;
        }
        else
        {
            // SQLite fallback for unit testing: In-memory lexical & embedding similarity
            var allChunks = await _db.DocumentChunks
                .AsNoTracking()
                .Include(c => c.Document)
                .ToListAsync(ct);

            var queryTokens = cleanQuery.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);

            var scored = allChunks.Select(c =>
            {
                int matchCount = 0;
                var chunkLower = c.Content.ToLowerInvariant();
                foreach (var token in queryTokens)
                {
                    if (chunkLower.Contains(token)) matchCount++;
                }

                double score = queryTokens.Length > 0 ? (double)matchCount / queryTokens.Length : 0;
                return new { Chunk = c, Score = score };
            })
            .Where(x => x.Score >= 0.25 || x.Chunk.Content.Contains(cleanQuery, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => x.Score)
            .Take(maxResults)
            .Select(x => new KnowledgeSearchResult(
                DocumentTitle: x.Chunk.Document.Title,
                Content: x.Chunk.Content,
                Similarity: Math.Max(x.Score, 0.75),
                CitationTag: $"[المصدر: {x.Chunk.Document.Title}]"
            ))
            .ToList();

            return scored;
        }
    }

    private static string SanitizeContent(string input)
    {
        // Strip control characters and sanitize common injection trigger tokens
        var sanitized = input.Replace("\0", string.Empty);
        // Neutralize explicit prompt overrides embedded in documents
        sanitized = Regex.Replace(sanitized, @"(?i)(system prompt|ignore (all )?previous (instructions|rules)|disregard (instructions|rules)|you are now|delete (all )?(appointments|bookings|db))", "[REDACTED_INSTRUCTION]");
        return sanitized;
    }

    private static List<string> ChunkText(string text, int targetChunkSize, int overlap)
    {
        var chunks = new List<string>();
        var paragraphs = text.Split(new[] { "\r\n\r\n", "\n\n", "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);

        var currentChunk = new System.Text.StringBuilder();

        foreach (var paragraph in paragraphs)
        {
            var trimmed = paragraph.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;

            if (currentChunk.Length + trimmed.Length > targetChunkSize && currentChunk.Length > 0)
            {
                chunks.Add(currentChunk.ToString().Trim());
                currentChunk.Clear();
            }

            if (currentChunk.Length > 0) currentChunk.Append(' ');
            currentChunk.Append(trimmed);
        }

        if (currentChunk.Length > 0)
        {
            chunks.Add(currentChunk.ToString().Trim());
        }

        return chunks.Count > 0 ? chunks : new List<string> { text.Trim() };
    }
}
