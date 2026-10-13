using Web.Services;

namespace Test.Web;

/// <summary>
///     Characterizes replay acceptance at the extracted state boundary without inspecting component fields.
/// </summary>
public sealed class ProjectTerminalReplayStateTests
{
    /// <summary>
    ///     Confirmed and duplicate orders are rejected while live-only output remains independently renderable.
    /// </summary>
    [Fact]
    public void Cursor_and_duplicate_rules_preserve_live_only_output()
    {
        var state = new ProjectTerminalReplayState();
        Assert.True(state.Accept(3, false));
        Assert.False(state.Accept(3, true));
        state.Confirm(3);
        state.Confirm(1);
        Assert.Equal(3, state.LastOrderId);
        Assert.False(state.Accept(2, true));
        Assert.True(state.Accept(-1, false));
        Assert.True(state.Accept(-1, false));
        state.Reset();
        Assert.Equal(0, state.LastOrderId);
        Assert.True(state.Accept(3, false));
    }

    /// <summary>
    ///     Saturated live tracking flags omission while confirmed replay can continue past the bound.
    /// </summary>
    [Fact]
    public void Replay_can_drain_a_saturated_live_identity_window()
    {
        var state = new ProjectTerminalReplayState();
        for (var order = 1; order <= 2000; order++) Assert.True(state.Accept(order, false));
        Assert.False(state.Accept(2001, false));
        Assert.True(state.Overflow);
        Assert.True(state.Accept(2001, true));
        state.Confirm(2001);
        state.AcknowledgeOverflow();
        Assert.False(state.Overflow);
        Assert.True(state.Accept(2002, false));
    }
}
