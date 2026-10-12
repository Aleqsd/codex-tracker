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
        if (next.ResetAlerts is null || next.ResetAlerts.Length > ResetAlertPlanner.MaximumAlerts || next.ResetAlerts.Any(a => !ResetAlertPlanner.Valid(a)))
            throw new ArgumentException(Loc.T("Notifications de reset invalides."));
        if (!Enum.IsDefined(next.ThemeMode) || !Enum.IsDefined(next.SortMode) || next.RefreshMinutes is not (1 or 2 or 5) ||
            !Enum.IsDefined(next.TrayProvider) || next.TrayWindow is not (ResetKind.Weekly or ResetKind.Short) ||
            next.TrayWindows is null || next.TrayWindows.Any(entry => !Enum.IsDefined(entry.Key) || entry.Value is not (ResetKind.Weekly or ResetKind.Short)))
            throw new ArgumentException(Loc.T("Réglages invalides."));
        preferences.Update(_ => next);
    }
    /// <summary>The quota drawn in the notification icon, remembered for its tool; choosing the current one again writes nothing.</summary>
    internal void SetTrayQuota(AccountProvider provider, ResetKind window)
    {
        var current = preferences.Current;
        if (current.TrayProvider == provider && current.TrayWindow == window && current.TrayWindows.TryGetValue(provider, out var kept) && kept == window) return;
        SavePreferences(p => p with { TrayProvider = provider, TrayWindow = window, TrayWindows = new(p.TrayWindows) { [provider] = window } });
    }
    internal void SaveSecrets(NotificationSecrets value, string? expected = null)
    {
        if (expected is not null) CheckRevision(expected);
        if (value?.Twilio is not { AccountSid: not null, KeySid: not null, Secret: not null, SmsFrom: not null, CallFrom: not null, To: not null }
            || value.SendGrid is not { ApiKey: not null, From: not null, To: not null }) throw new ArgumentException(Loc.T("Champs de connecteur manquants."));
        if (new[] { value.Twilio.AccountSid, value.Twilio.KeySid, value.Twilio.Secret, value.Twilio.SmsFrom, value.Twilio.CallFrom, value.Twilio.To, value.SendGrid.ApiKey, value.SendGrid.From, value.SendGrid.To }.Any(s => s.Length > 4096))
            throw new ArgumentException(Loc.T("Champ de connecteur trop long."));
        if (value.Twilio.Enabled && !NotificationProviders.Configured(ReminderChannel.Sms, value) && !NotificationProviders.Configured(ReminderChannel.Call, value)
            || value.SendGrid.Enabled && !NotificationProviders.Configured(ReminderChannel.Email, value))
            throw new ArgumentException(Loc.T("Configuration du canal incomplète."));
        secrets.Save(value); preferences.Touch();
    }
    internal void DeclareCodexReset(DateTimeOffset? at, DateTimeOffset now, string expected)
    {
        CheckRevision(expected);
        if (at > now) throw new ArgumentException(Loc.T("Le reset doit avoir eu lieu : choisissez une date et une heure passées."));
        if (preferences.Current.ManualCodexReset?.At == at) return;
        SavePreferences(p => p with { ManualCodexReset = at is { } time ? new(time, now) : null }, expected);
    }
    internal void SetResetAlert(TrackerState state, Guid accountId, ResetKind kind, DateTimeOffset at, bool enabled,
        DateTimeOffset now, string expected)
    {
        CheckRevision(expected);
        var matching = preferences.Current.ResetAlerts.Where(a => ResetAlertPlanner.Matches(a, accountId, kind, at)).ToArray();
        if (enabled && matching.Any(a => a.Enabled) || !enabled && matching.Length == 0) return;
        var remaining = preferences.Current.ResetAlerts.Where(a => !ResetAlertPlanner.Matches(a, accountId, kind, at) && a.At >= now.AddDays(-1)).ToArray();
        if (!enabled) { SavePreferences(p => p with { ResetAlerts = remaining }, expected); return; }
        if (preferences.RecoveryRequired) throw new InvalidOperationException(Loc.T("Vérifiez d’abord les réglages récupérés avant d’activer une notification."));
        var account = state.Accounts.FirstOrDefault(a => a.Profile.Id == accountId);
        if (account?.Snapshot is not { } snapshot || kind is not (ResetKind.Short or ResetKind.Weekly) || at <= now ||
            kind == ResetKind.Short && !account.HasShortWindow ||
            QuotaPresentation.ResetsAt(state, account, kind, now) != at)
            throw new ArgumentException(Loc.T("Cette échéance n’est plus disponible. Actualisez les resets."));
        var estimated = kind == ResetKind.Weekly && snapshot.Weekly?.IsResetEstimated == true;
        var alert = new ResetAlert(accountId, kind, at, now, snapshot.FetchedAt, estimated);
        if (!ResetAlertPlanner.Valid(alert) || remaining.Length >= ResetAlertPlanner.MaximumAlerts)
            throw new ArgumentException(Loc.T("Notifications de reset invalides."));
        SavePreferences(p => p with { ResetAlerts = [.. remaining, alert] }, expected);
    }
    internal static void ValidateRules(ReminderRule[] rules)
    {
        if (rules is null || rules.Length > 64 || rules.Any(r => r is null || !Enum.IsDefined(r.Kind) || r.LeadMinutes is null || r.Channels is null
            || r.LeadMinutes.Any(m => !ReminderPlanner.AllowedMinutes.Contains(m) || r.Kind == ResetKind.Short && m >= 300)
            || r.Channels.Any(c => !Enum.IsDefined(c)))) throw new ArgumentException(Loc.T("Règles invalides."));
    }
    internal static void ValidatePhone(PhonePolicy p)
    {
        if (p is null || p.SmsPerDay is < 0 or > 100 || p.CallsPerDay is < 0 or > 20 || p.QuietStart is < 0 or > 23 || p.QuietEnd is < 0 or > 23)
            throw new ArgumentException(Loc.T("Limites invalides."));
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
