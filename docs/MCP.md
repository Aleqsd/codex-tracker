# Piloter le tracker avec un assistant

1. Installer la dernière version de Codex Tracker.
2. Ouvrir **Réglages → Assistants**, puis activer l’accès MCP.
3. Copier la configuration et l’ajouter au client MCP de votre assistant.

Le bouton copie le chemin réel de l’exécutable, avec l’argument `--mcp` ; dans l’édition Microsoft Store, ce chemin est l’alias stable `%LOCALAPPDATA%\Microsoft\WindowsApps\codex-tracker.exe`. Exemple fictif :

```json
{
  "mcpServers": {
    "codex_tracker": {
      "command": "C:\\Users\\Exemple\\AppData\\Local\\Programs\\CodexTracker\\CodexTracker.exe",
      "args": ["--mcp"]
    }
  }
}
```

Aucun runtime .NET séparé, compte supplémentaire ou serveur réseau n’est nécessaire avec l’exécutable autonome. Le tracker démarre en arrière-plan si nécessaire, avec une attente maximale de 20 secondes. Désactiver l’accès dans les réglages bloque immédiatement les outils et annule les confirmations en attente.

## Exemples de demandes

- « Quels comptes ont encore du quota hebdomadaire ? Précise l’ancienneté des données. »
- « Quels resets arrivent dans les trois prochains jours ? »
- « Active les rappels Windows une heure avant les resets hebdomadaires. »
- « Configure SendGrid sans activer les envois. »
- « Demande un SMS de test et vérifie son statut après ma confirmation dans le tracker. »
- « Connecte Google Agenda avec les e-mails, puis ajoute mes resets en réserve. »
- « Envoie-moi un e-mail de test depuis Gmail. »

## Ligne de commande

Les mêmes outils existent sans client MCP, pour un agent, un script ou un terminal, avec l’accès Assistants activé :

```powershell
CodexTracker.exe --cli list_resets
CodexTracker.exe --cli test_notification channel=Email
CodexTracker.exe --cli update_preferences '@reglages.json'
CodexTracker.exe --cli help
```

Mêmes noms et paramètres que les outils MCP, en `clé=valeur` (valeurs `true`, nombres et JSON reconnus), objet JSON ou `@fichier.json`. Sortie JSON sur stdout ; code 0 en cas de succès, 1 pour une erreur ou un refus, 2 pour une commande ou des arguments invalides. Pour une modification, `expectedRevision` est lu automatiquement s’il est omis et `requestId` est généré : fournir les deux pour répéter une demande sans doublon. Le tracker est démarré en arrière-plan si nécessaire. Depuis cmd.exe, rediriger la sortie (`| more`) pour attendre la réponse.

## Contrat des outils

Lecture : `get_status`, `list_accounts`, `list_resets`, `get_notification_history`, `get_preferences`, `get_reminder_rules`, `get_channels`, `get_action_status`, `get_google_calendar`, `get_diagnostic`, `get_usage_history`, `get_global_reset_feed`, `get_update_status`, `get_claude_code_setup`, `get_whats_new`.

Actions : `refresh_active_account`, `open_page`, `update_preferences`, `update_phone_policy`, `update_reminder_rules`, `configure_channel`, `delete_credentials`, `test_notification`, `connect_google_calendar`, `disconnect_google_calendar`, `import_google_client`, `sync_google_calendar`, `add_reserve_to_calendar`, `set_tray_quota`, `set_start_with_windows`, `declare_codex_reset`, `set_reset_alert`, `rename_account`, `remove_account`, `set_account_avatar`, `detect_current_account`, `complete_onboarding`, `dismiss_notice`, `hide_pill`, `export_calendar_ics`, `prepare_google_calendar_import`, `check_global_reset_announcements`, `forget_google_client`, `check_for_update`, `install_update`, `quit_tracker`, `restart_tracker`, `acknowledge_recovery`, `dismiss_whats_new`, `export_profile`, `import_profile`.

Interface et comptes : `update_preferences` couvre aussi la langue, l’icône par outil, la surveillance des annonces publiques, la pastille et les réglages de mise à jour (champs facultatifs, omis = inchangés) ; `get_preferences` renvoie en plus `startWithWindows`, `trayQuota`, `accountNames`, `avatars`, `resetAlerts`, `pillHidden` et `languagePendingRestart` (`sortMode` est réservé, sans effet). `get_status` ajoute l’accueil (`onboardingComplete`), les encarts du tableau de bord (`notices`, masquables par `dismiss_notice`), les alertes de santé, `recoveryPending` et l’état des notifications Windows ; `list_accounts` ajoute l’estimation d’épuisement (`forecast`) et le rythme (`pace`) des comptes actifs ; `list_resets` donne les liens d’événement Google pré-remplis (`calendarEvents`). `declare_codex_reset` prend une date passée (ou `null` pour annuler) ; `set_reset_alert` reprend `accountId`, `kind` et `at` exacts lus dans `list_resets` ; `remove_account` refuse le compte actif et demande toujours une confirmation dans le tracker. Avec confirmation dans le tracker : `remove_account`, `forget_google_client`, `install_update`, `quit_tracker`, `restart_tracker`, `acknowledge_recovery`, `export_profile` et `import_profile` (le mot de passe du profil passe alors par le contexte de l’assistant). Seuls restent hors agent l’activation de l’accès Assistants, le consentement Google et ces confirmations locales.

Google Agenda et Gmail : `get_google_calendar` donne l’état de connexion, l’adresse, l’envoi d’e-mails, le problème éventuel et chaque reset en réserve avec le statut de son événement (`Confirmed`, `Opened`, `Deleted`, `Used`, `Missing`) et son lien Google ; aucun jeton n’est renvoyé. `connect_google_calendar` ouvre l’autorisation Google dans le navigateur de l’utilisateur (`mail: true` ajoute l’envoi d’e-mails) : rien n’est accordé avant son accord sur la page Google ; relire ensuite `get_google_calendar`. `test_notification` avec `Email` passe par Gmail, vers l’adresse de l’utilisateur et sans confirmation locale, quand l’envoi Gmail est accordé et SendGrid non configuré. Téléphone : `configure_channel` avec `provider: "ntfy"` et `ntfy: { enabled, server, topic, token }` (sujet de 12 à 64 caractères, serveur https) ; l’activation demande une confirmation locale, le test (`test_notification` avec `Push`) non. `get_channels` ne renvoie ni le sujet ni le jeton. `get_status` liste aussi les situations de la pastille (`pill`) ; `update_preferences` accepte `screenPill` pour l’activer ou la couper, et `pushMirror` (copie de chaque notification Windows sur le téléphone). `update_preferences` accepte aussi en option `newCreditNotifications`, `reserveAdviceNotifications`, `reserveMailAlerts`, `googleCalendarAutoAdd`, `calendarSuggestionDismissed` et `forecastNotifications` ; omis, ils gardent leur valeur.

Les mutations utilisent `expectedRevision`, obtenu par une lecture récente, et `requestId`, UUID conservé pour toute répétition de la même demande. Sur `revision_conflict`, relire et reconstruire l’action ; ne pas écraser aveuglément. Réutiliser un UUID avec des paramètres différents est refusé. Les réponses ont un objet `result` ou un code `error`.

Les réglages généraux, règles et connecteurs sont remplacés explicitement ; récupérer l’état avant modification. Pour `configure_channel`, fournir un seul de `twilio`, `sendGrid` ou `ntfy`, avec le `provider` correspondant. Omettre `enabled` le laisse à `false`. Les clés existantes ne sont jamais renvoyées : pour remplacer une configuration complète, fournir les identifiants nécessaires ou utiliser le formulaire local.

## Confidentialité et confirmations

Les clés sont chiffrées avec DPAPI pour l’utilisateur Windows courant. Le MCP permet leur saisie en écriture seule ; une clé fournie à un assistant peut toutefois rester dans sa conversation ou ses journaux. La saisie directe dans l’application reste disponible.

Chaque test externe, activation ou élargissement des envois demande une confirmation dans le tracker. Les demandes expirent après cinq minutes ; fermeture, refus, changement des réglages ou redémarrage les annulent. Après validation des règles, les rappels partent automatiquement tant que le tracker est ouvert et le PC éveillé.

Une action `unknown` n’est pas répétée automatiquement : vérifier le journal et le prestataire. Un résultat `completed` signifie que la commande a été traitée ; lire son détail pour distinguer un test bloqué, un refus, une acceptation et une livraison. Le SDK et le transport n’ajoutent aucune facturation ; les canaux externes restent facturés par les prestataires de l’utilisateur.

Les données retournées sont transmises au client assistant et peuvent être traitées par son fournisseur d’IA. Le canal local n’isole pas l’application d’un autre programme malveillant exécuté sous le même utilisateur Windows.

Après une mise à jour ou la fermeture du tracker, reconnecter le serveur depuis le client assistant. Un nouveau processus MCP redémarrera le tracker si nécessaire.

Depuis la 0.9.3, le pont distingue un échec de démarrage d’une fermeture normale : si le tracker ne termine pas sa connexion en 20 secondes, le pont sort avec le code 1 et explique sur la sortie d’erreur qu’il faut ouvrir le tracker puis reconnecter le client. Sa sortie standard reste réservée au protocole. `scripts/test-mcp-startup.ps1 -ExecutablePath <exe-publié>` le vérifie avec un canal de démonstration volontairement bloqué, sans lire de comptes ni modifier les réglages réels.
