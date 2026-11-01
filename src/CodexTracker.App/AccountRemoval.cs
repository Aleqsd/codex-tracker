namespace CodexTracker.App;

/// <summary>Removing an account from the tracker, shared by the details window and the assistants: history, name and photo go together.</summary>
internal static class AccountRemoval
{
    internal static async Task RemoveAsync(ITrackerService service, PreferencesStore preferences, Guid accountId, CancellationToken token = default)
    {
        await service.RemoveAccountAsync(accountId, token);
        var avatar = preferences.Current.Appearances.GetValueOrDefault(accountId)?.AvatarFile;
        preferences.Update(p =>
        {
            var appearances = new Dictionary<Guid, AccountAppearance>(p.Appearances); appearances.Remove(accountId);
            return p with { Appearances = appearances };
        });
        AvatarStore.Remove(preferences.DataDirectory, avatar);
    }
}
