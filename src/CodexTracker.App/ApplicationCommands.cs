using CodexTracker.Codex;
using CodexTracker.Core;

namespace CodexTracker.App;

// Shared by WPF and MCP. Call on the application's dispatcher; never write settings in the bridge.
internal sealed class ApplicationCommands(PreferencesStore preferences, NotificationSecretStore secrets)
{
    internal string Revision => preferences.Revision;
    internal void CheckRevision(string expected)
    {
        if (expected != Revision) throw new InvalidOperationException("revision_conflict");
    }
    internal void SavePreferences(Func<TrackerPreferences, TrackerPreferences> change, string? expected = null)
    {
        if (expected is not null) CheckRevision(expected);
        var next = change(preferences.Current);
        ValidateRules(next.ReminderRules!); ValidatePhone(next.PhonePolicy);
        if (!Enum.IsDefined(next.ThemeMode) || !Enum.IsDefined(next.SortMode) || next.RefreshMinutes is not (1 or 2 or 5))
            throw new ArgumentException("Réglages invalides.");
        preferences.Update(_ => next);
    }
    internal void SaveSecrets(NotificationSecrets value, string? expected = null)
    {
        if (expected is not null) CheckRevision(expected);
        if (value?.Twilio is not { AccountSid: not null, KeySid: not null, Secret: not null, SmsFrom: not null, CallFrom: not null, To: not null }
            || value.SendGrid is not { ApiKey: not null, From: not null, To: not null }) throw new ArgumentException("Champs de connecteur manquants.");
        if (new[] { value.Twilio.AccountSid, value.Twilio.KeySid, value.Twilio.Secret, value.Twilio.SmsFrom, value.Twilio.CallFrom, value.Twilio.To, value.SendGrid.ApiKey, value.SendGrid.From, value.SendGrid.To }.Any(s => s.Length > 4096))
            throw new ArgumentException("Champ de connecteur trop long.");
        if (value.Twilio.Enabled && !NotificationProviders.Configured(ReminderChannel.Sms, value) && !NotificationProviders.Configured(ReminderChannel.Call, value)
            || value.SendGrid.Enabled && !NotificationProviders.Configured(ReminderChannel.Email, value))
            throw new ArgumentException("Configuration du canal incomplète.");
        secrets.Save(value); preferences.Touch();
    }
    internal static void ValidateRules(ReminderRule[] rules)
    {
        if (rules is null || rules.Length > 64 || rules.Any(r => r is null || !Enum.IsDefined(r.Kind) || r.LeadMinutes is null || r.Channels is null
            || r.LeadMinutes.Any(m => !ReminderPlanner.AllowedMinutes.Contains(m) || r.Kind == ResetKind.Short && m >= 300)
            || r.Channels.Any(c => !Enum.IsDefined(c)))) throw new ArgumentException("Règles invalides.");
    }
    internal static void ValidatePhone(PhonePolicy p)
    {
        if (p is null || p.SmsPerDay is < 0 or > 100 || p.CallsPerDay is < 0 or > 20 || p.QuietStart is < 0 or > 23 || p.QuietEnd is < 0 or > 23)
            throw new ArgumentException("Limites invalides.");
        _ = ReminderPlanner.Zone(p);
    }
    internal static bool ExpandsExternalRules(ReminderRule[] before, ReminderRule[] after)
    {
        // Each enabled external account/type/lead/channel permission must already be covered.
        return after.Where(r => r.Enabled).Any(r => r.Channels.Where(c => c != ReminderChannel.Windows).Any(c =>
            r.LeadMinutes.Any(m => !before.Any(b => b.Enabled && b.Kind == r.Kind && b.Channels.Contains(c) && b.LeadMinutes.Contains(m)
                && (b.AccountIds is null || r.AccountIds is not null && r.AccountIds.All(id => b.AccountIds.Contains(id)))))));
    }
    internal static bool ExpandsPhone(PhonePolicy before, PhonePolicy after) =>
        after.SmsPerDay > before.SmsPerDay || after.CallsPerDay > before.CallsPerDay ||
        before.QuietEnabled && (!after.QuietEnabled || before.QuietStart != after.QuietStart || before.QuietEnd != after.QuietEnd || before.TimeZoneId != after.TimeZoneId);
}
