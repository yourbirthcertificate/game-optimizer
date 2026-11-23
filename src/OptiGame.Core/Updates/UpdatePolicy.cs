namespace OptiGame.Core.Updates;

/// <summary>When an update is offered and when it may restart the app. Pure decisions, so they are tested without a window.</summary>
public static class UpdatePolicy
{
    /// <summary>The banner is shown unless the user skipped exactly this version or asked to be reminded later this session.</summary>
    public static bool ShouldOffer(UpdateInfo? update, string? skippedVersion, bool remindedThisSession) =>
        update is not null && !remindedThisSession
        && !string.Equals(skippedVersion, update.Version.ToString(), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Restarting closes OptiGame+, which restores a boost. While a game is running (or a boost is active for one) the
    /// install waits, so the update never interrupts a session.
    /// </summary>
    public static bool CanRestartNow(bool gameRunning, bool boostActive) => !gameRunning && !boostActive;

    public static string WaitMessage => "A game is running. Close it, then restart to finish the update.";
}
