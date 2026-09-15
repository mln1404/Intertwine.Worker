using Intertwine.Domain.Entities;
using Intertwine.Repositories.Data;
using Intertwine.Repositories.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Intertwine.Worker.Tests;

public class DailyQuestionRepositoryTests
{
    [Fact]
    public async Task SelectionFiltersInactiveQuestionsAndCountsAllLinkedCategoriesAndDates()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        connection.CreateFunction("GETUTCDATE", () => "2026-09-15 12:00:00");
        await using var db = new IntertwineDbContext(
            new DbContextOptionsBuilder<IntertwineDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var first = new Category { CategoryId = 1, CategoryName = "Travel" };
        var second = new Category { CategoryId = 2, CategoryName = "Food" };
        db.Questions.AddRange(
            new Question
            {
                QuestionId = 1, IsActive = true,
                QuestionCategories = [new() { Category = first }, new() { Category = second }],
                DailyQuestions = [new() { Date = new(2026, 9, 10) }, new() { Date = new(2026, 9, 22) }]
            },
            new Question { QuestionId = 2, IsActive = true },
            new Question
            {
                QuestionId = 3, IsActive = false,
                QuestionCategories = [new() { Category = first }],
                DailyQuestions = [new() { Date = new(2026, 9, 11) }]
            });
        await db.SaveChangesAsync();

        var repository = new DailyQuestionRepository(db);
        var selection = await repository.GetSelectionAsync();

        Assert.Equal(new[] { 1, 2 }, selection.Candidates.Select(x => x.QuestionId).Order());
        Assert.Equal(new DateOnly(2026, 9, 22), selection.Candidates.Single(x => x.QuestionId == 1).LastSelectedDate);
        Assert.Null(selection.Candidates.Single(x => x.QuestionId == 2).LastSelectedDate);
        Assert.Equal(3, selection.CategoryUsage[1]);
        Assert.Equal(2, selection.CategoryUsage[2]);
        Assert.True(await repository.ExistsAsync(new(2026, 9, 11)));
    }

    [Fact]
    public async Task DatabaseRejectsDuplicateDatesButAllowsQuestionReuseOnDifferentDates()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        connection.CreateFunction("GETUTCDATE", () => "2026-09-15 12:00:00");
        await using var db = new IntertwineDbContext(
            new DbContextOptionsBuilder<IntertwineDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        db.Questions.Add(new Question { QuestionId = 1 });
        await db.SaveChangesAsync();
        var repository = new DailyQuestionRepository(db);
        Assert.True(await repository.TryInsertAsync(new DailyQuestion { Date = new(2026, 9, 15), QuestionId = 1 }));
        Assert.True(await repository.TryInsertAsync(new DailyQuestion { Date = new(2026, 9, 16), QuestionId = 1 }));
        db.DailyQuestions.Add(new DailyQuestion { Date = new(2026, 9, 15), QuestionId = 1 });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }
}
