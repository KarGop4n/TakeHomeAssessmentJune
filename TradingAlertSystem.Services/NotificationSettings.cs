
public class NotificationSettings
{
    public double EmailFrequencyHours { get; set; } = 4;
    public string SlackMinimumSeverity { get; set; } = "Medium";
    public string EmailMinimumSeverity { get; set; } = "Low";
    public bool ImmediateEmailForCritical { get; set; } = true;
}