using System.Text.Json;

namespace CodexTracker.App.Mcp;

internal static class AssistantSettingsView
{
    internal static FrameworkElement Create(MainWindow owner, PreferencesStore preferences, bool demo)
    {
        var panel = new StackPanel();
        var toggle = new CheckBox { Content = "Autoriser les assistants via MCP", IsChecked = preferences.Current.McpEnabled, IsEnabled = !demo };
        panel.Children.Add(toggle);
        var description = Ui.Text("Comptes, quotas et rappels accessibles à votre assistant. Le tracker démarre en arrière-plan si nécessaire. Aucun serveur à héberger.", 12, "MutedBrush");
        description.Margin = new Thickness(0, 10, 0, 22); panel.Children.Add(description);
        var state = Ui.Text("", 12); panel.Children.Add(state);
        var copy = new Button { Content = "Copier la configuration MCP", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 15, 0, 18) };
        panel.Children.Add(copy);
        var hint = Ui.Text("Ajoutez cette configuration à un client compatible MCP stdio. Les données demandées sont transmises à ce client et peuvent être traitées par son fournisseur d’IA.\n\nLes clés sont enregistrées en écriture seule. Une clé saisie via un assistant peut rester dans son contexte. Les tests externes et les nouveaux envois nécessitent votre confirmation ici.", 12, "MutedBrush"); panel.Children.Add(hint);
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        void Sync() { toggle.IsChecked = preferences.Current.McpEnabled; state.Text = owner.AssistantError is not null ? owner.AssistantError : !preferences.Current.McpEnabled ? "Désactivé · aucun accès aux données" : $"Activé · {owner.Assistant?.Connections ?? 0} connexion(s) locale(s)"; }
        toggle.Click += (_, _) => { try { owner.Commands.SavePreferences(p => p with { McpEnabled = toggle.IsChecked == true }); Sync(); } catch { state.Text = "Enregistrement impossible."; } };
        copy.Click += (_, _) =>
        {
            try { System.Windows.Clipboard.SetText(JsonSerializer.Serialize(new { mcpServers = new { codex_tracker = new { command = Environment.ProcessPath, args = new[] { "--mcp" } } } }, new JsonSerializerOptions { WriteIndented = true })); state.Text = "Configuration copiée."; }
            catch { state.Text = "Presse-papiers indisponible."; }
        };
        timer.Tick += (_, _) => Sync(); panel.Loaded += (_, _) => { Sync(); timer.Start(); }; panel.Unloaded += (_, _) => timer.Stop(); Sync();
        return panel;
    }
}
