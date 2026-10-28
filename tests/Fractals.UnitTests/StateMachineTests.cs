using FluentAssertions;
using Fractals.Core.State;
using TaskStatus = Fractals.Core.Models.TaskStatus;
using Xunit;

namespace Fractals.UnitTests;

public class StateMachineTests
{
    [Theory]
    [InlineData(TaskStatus.Queued, TaskStatus.Running, true)]
    [InlineData(TaskStatus.Queued, TaskStatus.Cancelled, true)]
    [InlineData(TaskStatus.Running, TaskStatus.Assembling, true)]
    [InlineData(TaskStatus.Running, TaskStatus.Cancelled, true)]
    [InlineData(TaskStatus.Running, TaskStatus.Failed, true)]
    [InlineData(TaskStatus.Running, TaskStatus.Queued, true)] // Watchdog retry
    [InlineData(TaskStatus.Assembling, TaskStatus.Success, true)]
    [InlineData(TaskStatus.Assembling, TaskStatus.Failed, true)]
    [InlineData(TaskStatus.Assembling, TaskStatus.Cancelled, true)]
    [InlineData(TaskStatus.Success, TaskStatus.Running, false)]
    [InlineData(TaskStatus.Cancelled, TaskStatus.Running, false)]
    [InlineData(TaskStatus.Failed, TaskStatus.Running, false)]
    [InlineData(TaskStatus.Queued, TaskStatus.Success, false)]
    public void CanTransition_ReturnsExpectedValidity(TaskStatus current, TaskStatus next, bool expected)
    {
        bool allowed = TaskStateMachine.CanTransition(current, next);
        allowed.Should().Be(expected);
    }

    [Fact]
    public void ValidateTransition_InvalidTransition_ThrowsInvalidOperationException()
    {
        var action = () => TaskStateMachine.ValidateTransition(TaskStatus.Success, TaskStatus.Running);

        action.Should().Throw<InvalidOperationException>()
            .WithMessage("*Неприпустимий перехід стану*");
    }

    [Theory]
    [InlineData(TaskStatus.Success, true)]
    [InlineData(TaskStatus.Failed, true)]
    [InlineData(TaskStatus.Cancelled, true)]
    [InlineData(TaskStatus.Queued, false)]
    [InlineData(TaskStatus.Running, false)]
    [InlineData(TaskStatus.Assembling, false)]
    public void IsTerminal_IdentifiesTerminalStatesCorrectly(TaskStatus status, bool expected)
    {
        TaskStateMachine.IsTerminal(status).Should().Be(expected);
    }
}

