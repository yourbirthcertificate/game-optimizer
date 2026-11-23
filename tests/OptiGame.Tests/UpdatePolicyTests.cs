using OptiGame.Core.Updates;

namespace OptiGame.Tests;

public sealed class UpdatePolicyTests
{
    static UpdateInfo Info(string version) =>
        new(Version.Parse(version), "v" + version, "OptiGame+ " + version, "notes", "https://github.com/x/y", null, null, 0, null);

    [Fact]
    public void An_update_is_offered_unless_skipped_or_postponed()
    {
        Assert.False(UpdatePolicy.ShouldOffer(null, null, false));
        Assert.True(UpdatePolicy.ShouldOffer(Info("1.1.0"), null, false));
        Assert.False(UpdatePolicy.ShouldOffer(Info("1.1.0"), "1.1.0", false));      // skipped exactly this version
        Assert.True(UpdatePolicy.ShouldOffer(Info("1.2.0"), "1.1.0", false));       // a newer one is offered again
        Assert.False(UpdatePolicy.ShouldOffer(Info("1.1.0"), null, true));          // remind me later: quiet for this session
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]    // a game is running
    [InlineData(false, true, false)]    // a boost is active
    [InlineData(true, true, false)]
    public void Restart_waits_while_a_game_runs_or_a_boost_is_active(bool game, bool boost, bool expected) =>
        Assert.Equal(expected, UpdatePolicy.CanRestartNow(game, boost));
}
