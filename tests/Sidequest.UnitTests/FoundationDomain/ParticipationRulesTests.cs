using Sidequest.Domain.Model;
using Sidequest.Domain.Rules;

namespace Sidequest.UnitTests.FoundationDomain;

/// <summary>Checks exclusive participation transitions, repeat commands, and rejected command partitions.</summary>
public sealed class ParticipationRulesTests
{
    /// <summary>Gets all twelve valid state/command pairs with literal results; null denotes the Joined/Follow conflict.</summary>
    public static TheoryData<ParticipationStatus, ParticipationCommand, ParticipationStatus?> Transitions => new()
    {
        { ParticipationStatus.None, ParticipationCommand.Follow, ParticipationStatus.Following },
        { ParticipationStatus.None, ParticipationCommand.Unfollow, ParticipationStatus.None },
        { ParticipationStatus.None, ParticipationCommand.Join, ParticipationStatus.Joined },
        { ParticipationStatus.None, ParticipationCommand.Leave, ParticipationStatus.None },
        { ParticipationStatus.Following, ParticipationCommand.Follow, ParticipationStatus.Following },
        { ParticipationStatus.Following, ParticipationCommand.Unfollow, ParticipationStatus.None },
        { ParticipationStatus.Following, ParticipationCommand.Join, ParticipationStatus.Joined },
        { ParticipationStatus.Following, ParticipationCommand.Leave, ParticipationStatus.Following },
        { ParticipationStatus.Joined, ParticipationCommand.Follow, null },
        { ParticipationStatus.Joined, ParticipationCommand.Unfollow, ParticipationStatus.Joined },
        { ParticipationStatus.Joined, ParticipationCommand.Join, ParticipationStatus.Joined },
        { ParticipationStatus.Joined, ParticipationCommand.Leave, ParticipationStatus.None }
    };

    /// <summary>Checks each transition and the exact error when following would silently leave a joined Quest.</summary>
    /// <param name="current">The initial exclusive participation state.</param>
    /// <param name="command">The requested participation command.</param>
    /// <param name="expected">The required resulting state, or null for the Joined/Follow conflict.</param>
    [Theory]
    [MemberData(nameof(Transitions))]
    public void Apply_AllStateCommandPairs_ReturnExactStateOrConflict(
        ParticipationStatus current, ParticipationCommand command, ParticipationStatus? expected)
    {
        if (expected is null)
        {
            AssertJoinedFollowConflict(() => ParticipationRules.Apply(current, command));
            return;
        }
        Assert.Equal(expected.Value, ParticipationRules.Apply(current, command));
    }

    /// <summary>Checks that repeating a command preserves its explicitly expected result or conflict.</summary>
    /// <param name="current">The state before the first command.</param>
    /// <param name="command">The command executed twice.</param>
    /// <param name="expected">The independently specified stable state, or null for repeated conflict.</param>
    [Theory]
    [MemberData(nameof(Transitions))]
    public void Apply_RepeatedCommands_HaveExplicitStableOutcome(
        ParticipationStatus current, ParticipationCommand command, ParticipationStatus? expected)
    {
        if (expected is null)
        {
            AssertJoinedFollowConflict(() => ParticipationRules.Apply(current, command));
            AssertJoinedFollowConflict(() => ParticipationRules.Apply(current, command));
            return;
        }
        var first = ParticipationRules.Apply(current, command);
        Assert.Equal(expected.Value, first);
        Assert.Equal(expected.Value, ParticipationRules.Apply(first, command));
    }

    /// <summary>Checks that joining replaces Following and subsequent leaving never restores the old subscription.</summary>
    [Fact]
    public void Apply_FollowJoinLeave_DoesNotRestoreFollowing()
    {
        var state = ParticipationRules.Apply(ParticipationStatus.None, ParticipationCommand.Follow);
        Assert.Equal(ParticipationStatus.Following, state);
        state = ParticipationRules.Apply(state, ParticipationCommand.Join);
        Assert.Equal(ParticipationStatus.Joined, state);
        state = ParticipationRules.Apply(state, ParticipationCommand.Leave);
        Assert.Equal(ParticipationStatus.None, state);
        Assert.Equal(ParticipationStatus.None, ParticipationRules.Apply(state, ParticipationCommand.Leave));
    }

    /// <summary>Checks that Leave does not cancel Following and Unfollow does not cancel Joined participation.</summary>
    [Fact]
    public void Apply_LeaveFollowingAndUnfollowJoined_PreserveDistinctSubscriptions()
    {
        Assert.Equal(ParticipationStatus.Following,
            ParticipationRules.Apply(ParticipationStatus.Following, ParticipationCommand.Leave));
        Assert.Equal(ParticipationStatus.Joined,
            ParticipationRules.Apply(ParticipationStatus.Joined, ParticipationCommand.Unfollow));
    }

    /// <summary>Checks explicit Validation errors for unknown commands without substituting a state transition.</summary>
    /// <param name="current">A valid initial participation state.</param>
    /// <param name="command">An integer outside the defined command enumeration.</param>
    [Theory]
    [InlineData(ParticipationStatus.None, -1)]
    [InlineData(ParticipationStatus.None, 99)]
    [InlineData(ParticipationStatus.Following, -1)]
    [InlineData(ParticipationStatus.Following, 99)]
    [InlineData(ParticipationStatus.Joined, -1)]
    [InlineData(ParticipationStatus.Joined, 99)]
    public void Apply_InvalidCommand_ThrowsValidationForEveryValidState(ParticipationStatus current, int command)
    {
        var error = Assert.Throws<DomainException>(() => ParticipationRules.Apply(current, (ParticipationCommand)command));
        Assert.Equal(ErrorCode.Validation, error.Code);
        Assert.Equal("Unknown participation state or command.", error.Message);
        Assert.Null(error.Field);
    }

    private static void AssertJoinedFollowConflict(Func<ParticipationStatus> action)
    {
        var error = Assert.Throws<DomainException>(() => action());
        Assert.Equal(ErrorCode.Conflict, error.Code);
        Assert.Equal("You already joined this Quest and receive attendee updates.", error.Message);
        Assert.Null(error.Field);
    }

    /// <summary>Checks undefined initial states before every valid command and when the command is also undefined.</summary>
    /// <param name="state">The invalid initial enum value.</param>
    /// <param name="command">A defined command or an independently invalid command value.</param>
    [Theory]
    [InlineData(-1, 0)]
    [InlineData(-1, 1)]
    [InlineData(-1, 2)]
    [InlineData(-1, 3)]
    [InlineData(99, 0)]
    [InlineData(99, 1)]
    [InlineData(99, 2)]
    [InlineData(99, 3)]
    [InlineData(-1, -1)]
    [InlineData(99, 99)]
    public void Apply_InvalidCurrentState_ReportsValidationBeforeTransition(int state, int command)
    {
        var error = Assert.Throws<DomainException>(() =>
            ParticipationRules.Apply((ParticipationStatus)state, (ParticipationCommand)command));
        Assert.Equal(ErrorCode.Validation, error.Code);
        Assert.Equal("Unknown participation state or command.", error.Message);
        Assert.Null(error.Field);
    }
}
