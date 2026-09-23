# Piloter le tracker avec un assistant

1. Installer Codex Tracker 0.7.0 ou ultérieur.
2. Ouvrir **Réglages → Assistants**, puis activer l’accès MCP.
3. Copier la configuration et l’ajouter au client MCP de votre assistant.

Le bouton copie le chemin réel de l’exécutable, avec l’argument `--mcp`. Exemple fictif :

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

## Contrat des outils

Lecture : `get_status`, `list_accounts`, `list_resets`, `get_notification_history`, `get_preferences`, `get_reminder_rules`, `get_channels`, `get_action_status`.

Actions : `refresh_active_account`, `open_page`, `update_preferences`, `update_phone_policy`, `update_reminder_rules`, `configure_channel`, `delete_credentials`, `test_notification`.

Les mutations utilisent `expectedRevision`, obtenu par une lecture récente, et `requestId`, UUID conservé pour toute répétition de la même demande. Sur `revision_conflict`, relire et reconstruire l’action ; ne pas écraser aveuglément. Réutiliser un UUID avec des paramètres différents est refusé. Les réponses ont un objet `result` ou un code `error`.

Les réglages généraux, règles et connecteurs sont remplacés explicitement ; récupérer l’état avant modification. Pour `configure_channel`, fournir seulement `twilio` ou `sendGrid`, avec `provider` correspondant. Omettre `enabled` le laisse à `false`. Les clés existantes ne sont jamais renvoyées : pour remplacer une configuration complète, fournir les identifiants nécessaires ou utiliser le formulaire local.

## Confidentialité et confirmations

Les clés sont chiffrées avec DPAPI pour l’utilisateur Windows courant. Le MCP permet leur saisie en écriture seule ; une clé fournie à un assistant peut toutefois rester dans sa conversation ou ses journaux. La saisie directe dans l’application reste disponible.

Chaque test externe, activation ou élargissement des envois demande une confirmation dans le tracker. Les demandes expirent après cinq minutes ; fermeture, refus, changement des réglages ou redémarrage les annulent. Après validation des règles, les rappels partent automatiquement tant que le tracker est ouvert et le PC éveillé.

Une action `unknown` n’est pas répétée automatiquement : vérifier le journal et le prestataire. Un résultat `completed` signifie que la commande a été traitée ; lire son détail pour distinguer un test bloqué, un refus, une acceptation et une livraison. Le SDK et le transport n’ajoutent aucune facturation ; les canaux externes restent facturés par les prestataires de l’utilisateur.

Les données retournées sont transmises au client assistant et peuvent être traitées par son fournisseur d’IA. Le canal local n’isole pas l’application d’un autre programme malveillant exécuté sous le même utilisateur Windows.

Après une mise à jour ou la fermeture du tracker, reconnecter le serveur depuis le client assistant. Un nouveau processus MCP redémarrera le tracker si nécessaire.

Le code courant distingue un échec de démarrage d’une fermeture normale : si le tracker ne termine pas sa connexion en 20 secondes, le pont sort avec le code 1 et explique sur la sortie d’erreur qu’il faut ouvrir le tracker puis reconnecter le client. Sa sortie standard reste réservée au protocole. Ce correctif est postérieur à la 0.9.2 publiée. `scripts/test-mcp-startup.ps1 -ExecutablePath <exe-publié>` le vérifie avec un canal de démonstration volontairement bloqué, sans lire de comptes ni modifier les réglages réels.
