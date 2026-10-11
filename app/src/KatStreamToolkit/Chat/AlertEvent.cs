namespace KatStreamToolkit.Chat;

public enum AlertType
{
    Follow,
    Sub,
    Raid,
}

// One platform-agnostic alert (follow / sub / raid). Built by the per-platform
// adapters (Twitch EventSub today) and consumed by the overlays (as a text
// line), the Alerts tab (recent list) and the OBS banner file.
public sealed class AlertEvent
{
    public required string Platform { get; init; }
    public required AlertType Type { get; init; }
    public required string User { get; init; }

    // Resub message text, when the platform provides one.
    public string? Message { get; init; }

    // Sub tier as the platform spells it ("1000"/"2000"/"3000"/"Prime").
    public string? Tier { get; init; }

    public int Months { get; init; }

    // Raid party size.
    public int Viewers { get; init; }

    // Sub that was a community gift (User is the gifter).
    public bool IsGift { get; init; }

    // True for a locally injected test alert (labelled as such everywhere).
    public bool IsTest { get; init; }

    public DateTime Received { get; init; } = DateTime.Now;

    private static string TierText(string? tier) => tier switch
    {
        "1000" => "Tier 1",
        "2000" => "Tier 2",
        "3000" => "Tier 3",
        "Prime" => "Prime",
        _ => "",
    };

    // Plain text, no markup - this exact string goes to the overlay line and
    // the OBS banner file.
    public string Display => Type switch
    {
        AlertType.Follow => $"new follow: {User}",
        AlertType.Sub => SubText(),
        AlertType.Raid => $"{User} is raiding with {Viewers} viewer{(Viewers == 1 ? "" : "s")}",
        _ => $"{User} ({Type})",
    };

    private string SubText()
    {
        if (IsGift)
            return $"{User} gifted a community sub";

        var parts = new List<string>();
        string tier = TierText(Tier);
        if (tier.Length > 0) parts.Add(tier);
        if (Months > 1) parts.Add($"month {Months}");
        string head = parts.Count > 0 ? $"{User} subscribed ({string.Join(", ", parts)})" : $"{User} subscribed";
        return string.IsNullOrWhiteSpace(Message) ? head : $"{head} - {Message}";
    }

    public override string ToString() => Display;
}
