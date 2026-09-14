using System.Data;
using Microsoft.EntityFrameworkCore;
using Sidequest.Domain.Model;
using Sidequest.Domain.Rules;
using Sidequest.Infrastructure.Persistence;

namespace Sidequest.UnitTests.CoreBoundaries;

/// <summary>Exercises nested exception normalization through public save boundaries before any SQL connection opens.</summary>
public sealed class SaveFailureNormalizationTests
{
    /// <summary>Gets every default, cancellation-token, and acceptance-bool save route.</summary>
    public static TheoryData<string, string> Cases
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var route in new[] { "sync", "syncTrue", "syncFalse", "async", "token", "asyncTrue", "asyncFalse" })
                foreach (var failure in new[] { "concurrency", "existing", "cancelAbove", "cancelBelow", "cancel", "taskCancel",
                    "unrelated", "validation", "outerConcurrency", "aggregate" })
                    data.Add(route, failure);
            return data;
        }
    }

    /// <summary>Checks safe Conflict construction, existing Conflict identity, and unchanged non-conflict/cancellation chains.</summary>
    /// <param name="route">The public save overload to invoke.</param>
    /// <param name="failure">The independent nested exception partition injected by the public SavingChanges event.</param>
    /// <returns>A task completing after exact error, invocation-count, tracker-state, and unopened-connection assertions.</returns>
    [Theory]
    [MemberData(nameof(Cases))]
    public async Task SaveChanges_NestedFailures_PreserveSpecifiedBoundary(string route, string failure)
    {
        var conflict = new DomainException(ErrorCode.Conflict, "existing-conflict", "existing-field");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Exception inner = failure switch
        {
            "concurrency" => new DbUpdateException("middle", new DbUpdateConcurrencyException("private detail")),
            "existing" => new DbUpdateException("middle", conflict),
            "cancelAbove" => new OperationCanceledException("cancel", conflict, cancelled.Token),
            "cancelBelow" => new DbUpdateConcurrencyException("race", new OperationCanceledException(cancelled.Token)),
            "cancel" => new OperationCanceledException(cancelled.Token),
            "taskCancel" => new TaskCanceledException("cancelled task"),
            "unrelated" => new DbUpdateException("middle", new ArgumentException("unrelated detail")),
            "validation" => new DomainException(ErrorCode.Validation, "invalid", "probe"),
            "outerConcurrency" => new DbUpdateConcurrencyException("race", conflict),
            "aggregate" => new AggregateException(new ArgumentException("first"), conflict),
            _ => throw new ArgumentOutOfRangeException(nameof(failure))
        };
        var original = failure is "cancel" or "taskCancel" or "aggregate" ? inner : new InvalidOperationException("CB-PRIVATE-DETAIL", inner);
        await using var db = new SidequestDbContext(new DbContextOptionsBuilder<SidequestDbContext>()
            .UseSqlServer("Server=unused.invalid;Database=NeverOpened;Integrated Security=true").Options);
        var row = new UserAccount { DisplayName = "Pending account" };
        db.Users.Add(row);
        var calls = 0;
        db.SavingChanges += (_, _) => { calls++; throw original; };
        var actual = await Record.ExceptionAsync(() => SaveAsync(db, route));
        if (failure is "concurrency" or "outerConcurrency")
        {
            var error = Assert.IsType<DomainException>(actual);
            Assert.Equal(ErrorCode.Conflict, error.Code);
            Assert.Equal("This item changed. Reload and try again.", error.Message);
            Assert.Null(error.Field);
            Assert.Null(error.InnerException);
            Assert.DoesNotContain("CB-PRIVATE-DETAIL", error.ToString());
        }
        else if (failure == "existing")
        {
            Assert.Same(conflict, actual);
            Assert.Equal("existing-conflict", conflict.Message);
            Assert.Equal("existing-field", conflict.Field);
        }
        else
        {
            Assert.Same(original, actual);
            Assert.Same(original.InnerException, actual!.InnerException);
        }
        Assert.Equal(1, calls);
        Assert.Equal(EntityState.Added, db.Entry(row).State);
        Assert.Empty(row.Version);
        Assert.Equal(ConnectionState.Closed, db.Database.GetDbConnection().State);
    }

    private static Task<int> SaveAsync(SidequestDbContext db, string route) => route switch
    {
        "sync" => Task.FromResult(db.SaveChanges()),
        "syncTrue" => Task.FromResult(db.SaveChanges(true)),
        "syncFalse" => Task.FromResult(db.SaveChanges(false)),
        "async" => db.SaveChangesAsync(),
        "token" => db.SaveChangesAsync(CancellationToken.None),
        "asyncTrue" => db.SaveChangesAsync(true, CancellationToken.None),
        "asyncFalse" => db.SaveChangesAsync(false, CancellationToken.None),
        _ => throw new ArgumentOutOfRangeException(nameof(route))
    };
}
