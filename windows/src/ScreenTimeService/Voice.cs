namespace ScreenTimeService;

/// <summary>Spoken phrases, word for word the same as the Mac daemon's.</summary>
public static class Voice
{
    public const string RapidReloginWarnOne =
        "Warning. The next login attempt will shut down this computer. Make sure your parents have given you access.";

    public const string RapidReloginWarnMany =
        "Warning. {0} more login attempts will shut down this computer. Make sure your parents have given you access.";

    public const string BudgetInitial = "Your daily screen time limit has been set to {0}.";

    /// <summary>
    /// Spoken when a kid arrives at the console (sign-in or user switch),
    /// after a delay so their desktop and audio are up before the helper runs.
    /// </summary>
    public const string Arrival = "You have {0} of screen time left today.";

    public static readonly TimeSpan ArrivalDelay = TimeSpan.FromSeconds(10);

    public const string BudgetIncreased = "Your parent has added {0} to your daily limit. Your daily limit is now {1}.";
    public const string BudgetDecreased = "Your parent has decreased your daily limit by {0}. Your daily limit is now {1}.";
    public const string BonusGranted = "You've received a bonus of {0}! Your total for today is now {1}.";
    public const string BonusReduced = "Your bonus time has been reduced by {0}. Your total for today is now {1}.";

    /// <summary>When several are crossed at once, only the lowest is spoken.</summary>
    public static readonly int[] WarningThresholds = [15, 10, 5, 1];

    public static string Warning(int threshold) => threshold == 1
        ? "1 minute of screen time remaining."
        : $"{threshold} minutes of screen time remaining.";

    /// <summary>"75" becomes "1 hour and 15 minutes", which is easier to follow by ear.</summary>
    public static string MinutesText(int n)
    {
        if (n < 60)
            return n == 1 ? "1 minute" : $"{n} minutes";
        var hours = n / 60;
        var minutes = n % 60;
        var hourText = hours == 1 ? "1 hour" : $"{hours} hours";
        if (minutes == 0)
            return hourText;
        var minuteText = minutes == 1 ? "1 minute" : $"{minutes} minutes";
        return $"{hourText} and {minuteText}";
    }
}
