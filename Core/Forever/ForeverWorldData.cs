using Microsoft.Extensions.Logging;

using Newtonsoft.Json;

using SharedLib;
using SharedLib.Quests;

using System;
using System.IO;

namespace Core.Forever;

/// <summary>
/// The client's world data for the tasks that name no point (a visit, a quest's camp): its
/// <see cref="QuestBook"/> - every creature with its roles and sides, every spawn, every quest - read
/// the first time such a task asks, as most routes never do. The files ship in the repository's
/// <c>Json</c> (<c>scripts/extract-questiedb.py</c>); the agent's data sync does not carry them yet.
/// </summary>
public sealed class ForeverWorldData(DataConfig data, ILogger<ForeverWorldData> logger)
{
    private readonly object gate = new();
    private QuestBook? book;
    private string? problem;

    /// <summary>The book, read once; false with why when its files do not read.</summary>
    public bool TryGetBook(out QuestBook read, out string why)
    {
        lock (gate)
        {
            if (book is null && problem is null)
            {
                try
                {
                    book = QuestBook.Load(data, logger);
                    if (logger.IsEnabled(LogLevel.Information))
                        logger.LogInformation("Forever world data: {Creatures} creatures, {Quests} quests", book.Creatures.Count,
                            book.Quests.Count);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                {
                    problem = $"the world data does not read: {ex.Message}";
                }
            }

            read = book ?? QuestBook.Empty;
            why = problem ?? string.Empty;
            return book is not null;
        }
    }
}
