namespace VTTranslate.App.Account;

/// <summary>
/// Phase 25B — customer wording for the free trial. The server sends stable machine-readable codes
/// (<c>trial_expired</c>, <c>trial_usage_exhausted</c>); this maps them to text. No payment or upgrade URL exists yet —
/// the wording only points the customer toward an upgrade and never claims that payment is available.
/// </summary>
public static class TrialMessages
{
    public const string ExpiredCode = "trial_expired";
    public const string ExhaustedCode = "trial_usage_exhausted";

    public const string Ended = "Your trial has ended. Upgrade to Premium to continue using Voice-Translater.";
    public const string AllowanceUsed = "Your trial allowance has been used. Upgrade to Premium to continue using Voice-Translater.";

    /// <summary>Returns the upgrade message for a trial-related backend code, or null for any other code.</summary>
    public static string? ForCode(string? code) => code switch
    {
        ExpiredCode => Ended,
        ExhaustedCode => AllowanceUsed,
        _ => null,
    };
}
