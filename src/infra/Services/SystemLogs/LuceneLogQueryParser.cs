using System;
using System.Collections.Generic;
using System.Linq;
using Farm.Infrastructure.Domain;
using Lucene.Net.Analysis.Core;
using Lucene.Net.QueryParsers.Classic;
using Lucene.Net.Search;
using Lucene.Net.Util;

namespace Farm.Infrastructure.Services.SystemLogs;

/// <summary>
/// Parses Lucene query syntax and applies filters to IQueryable&lt;SystemLog&gt;.
/// Supports fields: level, message, correlationId, source, metadata, timestamp
/// Example: level:Error AND message:timeout correlationId:abc123
/// </summary>
public static class LuceneLogQueryParser
{
    private const string LuceneVersion = "LUCENE_48";

    /// <summary>
    /// Parses a Lucene query string and returns a filter function.
    /// </summary>
    /// <param name="queryString">The Lucene query string to parse (supports fields: level, message, correlationId, source, metadata).</param>
    public static Func<SystemLog, bool> Parse(string? queryString)
    {
        if (string.IsNullOrWhiteSpace(queryString))
        {
            return _ => true; // No filter
        }

        try
        {
            // Parse the Lucene query
            var parser = new QueryParser(LuceneVersion.AsVersionEnum(), "message", new SimpleAnalyzer(LuceneVersion.AsVersionEnum()));
            Query query = parser.Parse(queryString);

            // Convert Lucene query to lambda expression
            return CreateFilter(query);
        }
        catch (ParseException)
        {
            // If parsing fails, try simple field-based parsing as fallback
            return ParseSimpleFields(queryString);
        }
    }

    /// <summary>
    /// Simple field-based parsing for common patterns like: level:Error message:timeout correlationId:abc123
    /// </summary>
    private static Func<SystemLog, bool> ParseSimpleFields(string queryString)
    {
        string[] terms = queryString.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        var filters = new List<Func<SystemLog, bool>>();

        foreach (string term in terms)
        {
            if (term.Contains(':'))
            {
                string[] parts = term.Split(':', 2);
                string field = parts[0].ToLowerInvariant();
                string value = parts[1].Trim('"', '\'');

                Func<SystemLog, bool> filter = field switch
                {
                    "level" => (Func<SystemLog, bool>)(log => log.Level?.Contains(value, StringComparison.OrdinalIgnoreCase) ?? false),
                    "message" => log => log.Message?.Contains(value, StringComparison.OrdinalIgnoreCase) ?? false,
                    "correlationid" => log => log.CorrelationId?.Contains(value, StringComparison.OrdinalIgnoreCase) ?? false,
                    "source" => log => log.Source?.Contains(value, StringComparison.OrdinalIgnoreCase) ?? false,
                    "metadata" => log => log.Metadata?.Contains(value, StringComparison.OrdinalIgnoreCase) ?? false,
                    _ => _ => true
                };

                filters.Add(filter);
            }
            else if (!term.StartsWith("AND", StringComparison.OrdinalIgnoreCase) &&
                     !term.StartsWith("OR", StringComparison.OrdinalIgnoreCase))
            {
                // Default: search in message
                filters.Add(log => log.Message?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false);
            }
        }

        return log => filters.All(f => f(log));
    }

    /// <summary>
    /// Converts a parsed Lucene query to a filter function.
    /// </summary>
    /// <param name="query">The parsed Lucene query object to convert.</param>
    private static Func<SystemLog, bool> CreateFilter(Lucene.Net.Search.Query query)
    {
        // For simple term queries, extract the field and value
        if (query is Lucene.Net.Search.TermQuery termQuery)
        {
            string field = termQuery.Term.Field;
            string value = termQuery.Term.Text;

            return field switch
            {
                "level" => log => log.Level?.Contains(value, StringComparison.OrdinalIgnoreCase) ?? false,
                "message" => log => log.Message?.Contains(value, StringComparison.OrdinalIgnoreCase) ?? false,
                "correlationId" => log => log.CorrelationId?.Contains(value, StringComparison.OrdinalIgnoreCase) ?? false,
                "source" => log => log.Source?.Contains(value, StringComparison.OrdinalIgnoreCase) ?? false,
                "metadata" => log => log.Metadata?.Contains(value, StringComparison.OrdinalIgnoreCase) ?? false,
                _ => _ => true
            };
        }

        // For boolean queries (AND, OR, NOT)
        if (query is Lucene.Net.Search.BooleanQuery boolQuery)
        {
            var filters = new List<Func<SystemLog, bool>>();

            foreach (BooleanClause? clause in boolQuery.Clauses)
            {
                Func<SystemLog, bool> filter = CreateFilter(clause.Query);

                if (clause.Occur == Lucene.Net.Search.Occur.MUST)
                {
                    filters.Add(filter);
                }
                else if (clause.Occur == Lucene.Net.Search.Occur.MUST_NOT)
                {
                    filters.Add(log => !filter(log));
                }

                // OR and SHOULD are more complex; simplified here
            }

            return log => filters.All(f => f(log));
        }

        // For wildcard queries
        if (query is Lucene.Net.Search.WildcardQuery wildcardQuery)
        {
            string field = wildcardQuery.Term.Field;
            string pattern = wildcardQuery.Term.Text
                .Replace("*", string.Empty)
                .Replace("?", string.Empty);

            return field switch
            {
                "level" => log => log.Level?.Contains(pattern, StringComparison.OrdinalIgnoreCase) ?? false,
                "message" => log => log.Message?.Contains(pattern, StringComparison.OrdinalIgnoreCase) ?? false,
                "correlationId" => log => log.CorrelationId?.Contains(pattern, StringComparison.OrdinalIgnoreCase) ?? false,
                "source" => log => log.Source?.Contains(pattern, StringComparison.OrdinalIgnoreCase) ?? false,
                "metadata" => log => log.Metadata?.Contains(pattern, StringComparison.OrdinalIgnoreCase) ?? false,
                _ => _ => true
            };
        }

        // Default: no filter
        return _ => true;
    }
}

/// <summary>
/// Extension method to convert string to LuceneVersion enum.
/// </summary>
internal static class LuceneVersionExtensions
{
    /// <summary>
    /// Converts string version to LuceneVersion enum.
    /// Currently only LUCENE_48 is supported.
    /// </summary>
    /// <param name="version">The version string to convert.</param>
    public static LuceneVersion AsVersionEnum(this string version)
    {
        _ = version;
        return LuceneVersion.LUCENE_48;
    }
}
