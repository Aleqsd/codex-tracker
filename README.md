# Codex Tracker

Vos quotas Codex, directement dans la barre des tâches Windows.

L’icône affiche le **pourcentage hebdomadaire restant**. Un clic ouvre un tableau de bord sombre avec vos comptes, offres, périodes d’abonnement, quotas, dates de reset et resets en réserve.

![Tableau de bord avec comptes fictifs](docs/dashboard.png)

## Installer

1. Téléchargez le ZIP `CodexTracker-…-win-x64.zip` depuis les [Releases](https://github.com/Aleqsd/codex-tracker/releases).
2. Extrayez-le dans un dossier permanent et lancez `CodexTracker.exe`.
3. Utilisez normalement Codex : le tracker détecte son compte ouvert. Chaque autre compte est ajouté automatiquement lorsque vous l’ouvrez dans Codex.
4. Dans les paramètres Windows de la barre des tâches, rendez l’icône Codex Tracker visible près de l’horloge.

Windows 11 x64 et une installation de Codex avec sa CLI `codex` sont nécessaires. Le runtime .NET est inclus. Le démarrage avec Windows est facultatif, depuis les réglages du tracker.

## Détection automatique

Changez de compte **dans Codex**. Le tracker observe son fichier de session en lecture seule, détecte le nouveau compte, sélectionne son quota dans l’icône et actualise ses données. Les changements de fichier sont surveillés immédiatement, avec un contrôle de secours toutes les deux secondes et une stabilisation de 300 ms. Une écriture transitoire ou une connexion réseau lente peut prolonger l’affichage des quotas.

Le bouton **Détecter le compte Codex** déclenche une vérification immédiate. Aucune connexion OAuth, bascule ni fermeture de Codex n’est effectuée par le tracker.

Seul le compte ouvert dans Codex est actualisé, toutes les deux minutes. Les autres comptes affichent leur **dernier relevé daté** ; leurs quotas peuvent avoir changé depuis. Ouvrez un compte dans Codex pour obtenir de nouvelles données. **Afficher dans l’icône** permet de consulter un ancien relevé ; au prochain changement de compte dans Codex, l’icône suit à nouveau le compte actif.

## Lire le tableau de bord

- **Hebdomadaire** : quota restant de la fenêtre de 10 080 minutes du bucket `codex`. Une limite de cinq heures n’est jamais présentée comme une limite hebdomadaire.
- **Vert** au-dessus de 20 %, **orange** entre 10 et 20 %, **rouge** sous 10 %. La valeur reste lisible sans dépendre uniquement de la couleur.
- **Offre** : badges Free, Plus, Pro et autres offres renvoyées par Codex. Les valeurs `prolite` et `pro` correspondent respectivement à Pro **5×** et Pro **20×**, conformément à l’interface Codex actuelle. Une offre inconnue reste affichée telle quelle.
- **Période d’abonnement** : début et fin de période active lorsqu’ils sont présents dans les métadonnées de session Codex. Ce ne sont pas nécessairement la date de souscription initiale ni une échéance de paiement. Les dates absentes restent indisponibles.
- **Resets en réserve** : compteur `availableCount` fourni par Codex, accompagné des expirations disponibles. Le nombre d’éléments détaillés ne remplace jamais le compteur serveur.
- Les dates précises utilisent le fuseau horaire Windows et son décalage UTC. Le compte à rebours complète l’heure exacte ; il ne confirme pas un reset tant que le serveur n’a pas actualisé la valeur.
- En cas d’erreur, les dernières valeurs et leur ancienneté sont conservées. Une information absente reste « indisponible », jamais zéro.

## Données locales

Les profils, réglages et derniers relevés sont stockés sous `%LOCALAPPDATA%\CodexTracker`, avec des permissions limitées à l’utilisateur Windows. Le tracker lit `%USERPROFILE%\.codex\auth.json` sans jamais le modifier. Il utilise uniquement le jeton d’accès courant en mémoire dans un processus Codex isolé avec un stockage de connexion `ephemeral` ; il ne conserve pas de session et ne renouvelle aucun jeton. Codex reste responsable de sa connexion. Si elle a expiré, ouvrez Codex pour la rétablir.

Les anciens coffres et sauvegardes de la version 0.1 ne sont ni utilisés ni modifiés lors de la mise à jour. Les métadonnées et derniers relevés sont conservés. Les nouveaux profils de travail temporaires sont nettoyés après collecte.

L’application ne possède pas de serveur de synchronisation et n’envoie pas vos comptes à ce dépôt. Les requêtes de quota passent par Codex et les services OpenAI. Aucun mot de passe n’est demandé au tracker. Les journaux applicatifs n’enregistrent pas de tokens. Les exemples et captures de ce dépôt utilisent uniquement des comptes fictifs.

Pour préconfigurer localement une première installation, créez `%LOCALAPPDATA%\CodexTracker\initial-accounts.json` avec un tableau d’adresses, par exemple `["demo@example.test"]`. Ce fichier est facultatif, lu uniquement si les réglages n’existent pas encore, et doit rester hors du dépôt. Ces comptes restent « À détecter » jusqu’à leur ouverture dans Codex.

## Développer

Installez le SDK .NET 10 puis, sous Windows :

```powershell
dotnet restore CodexTracker.slnx
dotnet build CodexTracker.slnx
dotnet test tests/CodexTracker.Tests/CodexTracker.Tests.csproj
dotnet run --project src/CodexTracker.App -- --demo
./scripts/publish.ps1
```

La solution sépare `Core` (modèle et quotas), `Codex` (observation et protocole en lecture seule) et `App` (WPF et zone de notification). Les tests utilisent des sessions fictives et ne modifient jamais votre connexion Codex. Ils couvrent les réponses de quotas, valeurs absentes, dates et changements d’heure, changements de fichiers, isolation des comptes et réponses réseau tardives. Les contrôles réels et leurs limites sont détaillés dans [VALIDATION.md](docs/VALIDATION.md).

Pour quitter une instance existante avant une mise à jour : `CodexTracker.exe --exit`. Le mode démonstration se ferme séparément avec `CodexTracker.exe --demo --exit`.

## Références

[App Server Codex](https://learn.chatgpt.com/docs/app-server) · [Authentification Codex](https://learn.chatgpt.com/docs/auth) · [Offres Codex](https://learn.chatgpt.com/docs/pricing) · [Zone de notification Windows](https://learn.microsoft.com/en-us/windows/win32/shell/notification-area)

Projet indépendant, sans affiliation avec OpenAI. Licence MIT.
