# Données et confidentialité

Codex Tracker est une application indépendante, sans compte de service Tracker ni serveur hébergé par le projet.

## Données locales

Le tracker observe le compte connecté dans Codex et conserve les identités, offres, quotas, échéances, préférences, avatars et historiques dans `%LOCALAPPDATA%\CodexTracker`. Les relevés des comptes inactifs sont des copies datées. Le journal des notifications couvre 30 jours. Les clés des connecteurs sont chiffrées par Windows DPAPI pour l’utilisateur courant ; les autres données locales ne sont pas toutes chiffrées.

Il ne se connecte pas à un autre compte et ne modifie pas la session de Codex. La collecte utilise la CLI Codex installée, qui communique avec les services OpenAI pour le compte actif.

## Échanges facultatifs

- Une recherche de mises à jour contacte GitHub ; GitHub reçoit les informations ordinaires d’une requête réseau, dont l’adresse IP. Le tracker ne lui envoie pas les comptes ni les quotas.
- Les notifications Windows restent sur le PC. Les connecteurs SMS/appels Twilio et email SendGrid, désactivés par défaut, transmettent les destinations et messages au prestataire lorsque l’utilisateur les configure et les active. Leurs conditions et coûts s’appliquent.
- L’import Google Agenda prépare un fichier local à importer par l’utilisateur. L’ajout d’une échéance via un lien Google transmet son contenu dans un brouillon d’événement.
- Le MCP est désactivé par défaut. Une fois activé, les outils transmettent les données demandées au client assistant connecté. Le traitement effectué par ce client relève de sa propre configuration. Les secrets enregistrés via MCP ne sont pas renvoyés, mais leur saisie peut apparaître dans le contexte de l’assistant.

## Contrôle et suppression

Les canaux et le MCP peuvent être désactivés depuis les réglages. Les fiches de connecteurs permettent d’effacer les identifiants. Retirer un compte supprime son suivi local ; le compte sera redétecté si vous l’ouvrez ensuite dans Codex.

La désinstallation conserve les données pour permettre une réinstallation. Pour tout supprimer, quittez le tracker puis supprimez son dossier `%LOCALAPPDATA%\CodexTracker`. Les événements déjà importés dans Google Agenda, les messages transmis et les données du client assistant doivent être supprimés dans les services correspondants.

Questions ou signalements : [dépôt du projet](https://github.com/Aleqsd/codex-tracker). Ne publiez jamais de clés, sessions ou captures de comptes personnels dans une issue.
