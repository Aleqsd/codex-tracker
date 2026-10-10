# Données et confidentialité

Codex Tracker est une application indépendante, sans compte de service Tracker ni serveur hébergé par le projet.

## Données locales

Le tracker observe le compte connecté dans Codex et conserve les identités, offres, quotas, échéances, préférences, avatars et historiques dans `%LOCALAPPDATA%\CodexTracker`. Les relevés des comptes inactifs sont des copies datées. Le journal des notifications couvre 30 jours. Les clés des connecteurs sont chiffrées par Windows DPAPI pour l’utilisateur courant ; les autres données locales ne sont pas toutes chiffrées.

Il ne se connecte pas à un autre compte et ne modifie pas la session de Codex. La collecte utilise la CLI Codex installée, qui communique avec les services OpenAI pour le compte actif.

Des copies `.bak` des préférences, profils et relevés permettent leur récupération. Les fichiers corrompus sont conservés localement avant remplacement et peuvent aussi contenir des données privées : ne les partagez pas. Le diagnostic proposé dans l’onglet Réglages utilise une liste limitée de versions, états techniques et dates, sans compte, quota, chemin personnel ni secret ; son contenu est montré avant copie et n’est jamais envoyé automatiquement.

## Échanges réseau

- Les annonces de resets généraux sont recherchées par défaut au démarrage puis toutes les 15 minutes : index public sur shixilin.com, vérification du texte original sur publish.x.com. Aucun compte, quota, cookie ou identifiant n’est transmis ; les services voient les métadonnées habituelles, dont l’adresse IP. Désactivation dans **Réglages → Rappels**. Voir les [sources et limites](GLOBAL-RESETS.md).

- Par défaut, le tracker recherche et télécharge les mises à jour sur GitHub au démarrage puis toutes les 15 minutes. GitHub reçoit les informations ordinaires d’une requête réseau, dont l’adresse IP ; aucun compte ni quota ne lui est envoyé. Désactivez le téléchargement automatique dans **Réglages → Application** pour ne faire que des recherches manuelles.
- Les notifications Windows restent sur le PC. Les connecteurs SMS/appels Twilio et email SendGrid, désactivés par défaut, transmettent les destinations et messages au prestataire lorsque l’utilisateur les configure et les active. Leurs conditions et coûts s’appliquent.
- L’import Google Agenda prépare un fichier local à importer par l’utilisateur. L’ajout d’une échéance via un lien Google transmet son contenu dans un brouillon d’événement.
- Le MCP est désactivé par défaut. Une fois activé, les outils transmettent les données demandées au client assistant connecté. Le traitement effectué par ce client relève de sa propre configuration. Les secrets enregistrés via MCP ne sont pas renvoyés, mais leur saisie peut apparaître dans le contexte de l’assistant.

## Contrôle et suppression

Les canaux et le MCP peuvent être désactivés depuis les réglages. Les fiches de connecteurs permettent d’effacer les identifiants. Retirer un compte supprime son suivi local ; le compte sera redétecté si vous l’ouvrez ensuite dans Codex.

Pour afficher des notifications avec boutons, le tracker s’enregistre auprès de Windows pour l’utilisateur courant : clés sous `HKCU\Software\Classes` et icône sous `%LOCALAPPDATA%\ToastNotificationManagerCompat`. La désinstallation retire cet enregistrement.

La désinstallation conserve les données pour permettre une réinstallation. Pour tout supprimer, quittez le tracker puis supprimez son dossier `%LOCALAPPDATA%\CodexTracker`. Les événements déjà importés dans Google Agenda, les messages transmis et les données du client assistant doivent être supprimés dans les services correspondants.

Après une récupération d’un ancien stockage Windows redirigé, une copie peut aussi subsister dans `%LOCALAPPDATA%\Packages\OpenAI.Codex_*\LocalCache\Local\CodexTracker`. Elle est conservée pour éviter une perte de données. Pour effacer cette copie, supprimer uniquement ce sous-dossier `CodexTracker`, jamais le dossier du package Codex entier. Les sauvegardes privées créées manuellement hors du dossier principal doivent également être gérées séparément.

Questions ou signalements : [dépôt du projet](https://github.com/Aleqsd/codex-tracker). Ne publiez jamais de clés, sessions ou captures de comptes personnels dans une issue.

## Claude Code et déclarations manuelles

La détection Claude Code lit les fichiers locaux natifs et ne conserve que l’identité et les métadonnées d’abonnement. Ses jetons ne sont ni copiés dans le profil du tracker, ni transmis à un serveur, ni utilisés pour un appel HTTP. Le branchement facultatif reçoit les données documentées de la barre de statut : seuls les quotas et leurs dates sont conservés, sans projet, chemin de transcription, contenu de conversation ou clé.

L’application Claude est prise en charge par lecture seule de son cache `plan-usage-history.json`. Seul le dernier relevé daté de l’organisation sélectionnée est importé. L’UUID du compte et de l’organisation, son nom et l’offre distinguent les comptes personnels et d’entreprise sur une même adresse. La configuration Desktop, les cookies, les jetons chiffrés et les conversations ne sont pas lus par cette collecte.

Les sessions natives ont une association locale au compte pour ignorer les relevés retardés d’un ancien compte. Les associations de plus de 30 jours sont retirées au démarrage d’une nouvelle session. Les relevés restent dans le stockage privé Windows du tracker.

Une déclaration de reset Codex conserve uniquement l’heure du reset et l’heure de sa saisie dans les préférences. Elle change la présentation locale et se retire devant de nouvelles observations, sans effacer l’historique ni agir sur votre compte.
