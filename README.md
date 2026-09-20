# Codex Tracker

Vos quotas Codex, directement dans la barre des tâches Windows.

L’icône affiche le **pourcentage hebdomadaire restant au centre d’un anneau de progression**. Survolez-la pour un aperçu ; cliquez pour retrouver vos comptes dans un panneau compact. Les dates précises, réserves, périodes d’abonnement et graphiques restent accessibles dans les détails.

![Tableau de bord avec comptes fictifs](docs/dashboard.png)

## Installer

1. Téléchargez `CodexTracker-…-Setup.exe` depuis les [Releases](https://github.com/Aleqsd/codex-tracker/releases).
2. Lancez l’installateur : il installe l’application pour votre utilisateur, sans droits administrateur, et crée son raccourci. La désinstallation depuis les paramètres Windows conserve vos données de suivi.
3. Utilisez normalement Codex : le tracker détecte son compte ouvert. Chaque autre compte est ajouté automatiquement lorsque vous l’ouvrez dans Codex.
4. Dans les paramètres Windows de la barre des tâches, rendez l’icône Codex Tracker visible près de l’horloge.

Windows 11 x64 et une installation de Codex avec sa CLI `codex` sont nécessaires. Le runtime .NET est inclus. Le démarrage avec Windows est facultatif, depuis les réglages du tracker.

Une version portable ZIP est également disponible : extrayez-la dans un dossier permanent puis lancez `CodexTracker.exe`. Dans **Réglages → Mises à jour**, vous pouvez rechercher une version, la télécharger puis relancer le tracker. Le téléchargement utilise le dépôt public, vérifie l’empreinte SHA-256 et conserve une copie de secours jusqu’au démarrage réussi de la nouvelle version. Cette opération ne ferme pas Codex.

## Détection automatique

Changez de compte **dans Codex**. Le tracker observe son fichier de session en lecture seule, détecte le nouveau compte, sélectionne son quota dans l’icône et actualise ses données. Les changements de fichier sont surveillés immédiatement, avec un contrôle de secours toutes les deux secondes et une stabilisation de 300 ms. Une écriture transitoire ou une connexion réseau lente peut prolonger l’affichage des quotas.

Le bouton d’actualisation déclenche une vérification immédiate. Aucune connexion OAuth, bascule ni fermeture de Codex n’est effectuée par le tracker.

Seul le compte ouvert dans Codex est actualisé, toutes les deux minutes. Les autres comptes affichent leur **dernier relevé daté** ; leurs quotas peuvent avoir changé depuis. Ouvrez un compte dans Codex pour obtenir de nouvelles données. **Afficher dans l’icône** permet de consulter un ancien relevé ; au prochain changement de compte dans Codex, l’icône suit à nouveau le compte actif.

## Lire le tableau de bord

- La vue principale présente le compte actif puis une ligne par compte. Vous pouvez trier par compte actif, quota restant, prochain reset ou offre. Le bouton **…** ouvre les détails et l’historique.
- **Confidentialité** remplace les adresses par des alias stables dans les écrans, menus et aperçus. Les notifications Windows utilisent toujours ces alias pour éviter de laisser des adresses dans les notifications conservées par le système.
- Les thèmes clair et sombre suivent Windows, avec un choix manuel dans les réglages. Le dessin de l’icône s’adapte également au thème.
- **Hebdomadaire** : quota restant de la fenêtre de 10 080 minutes du bucket `codex`. Une limite de cinq heures n’est jamais présentée comme une limite hebdomadaire.
- **Dans l’icône** : nombre et anneau sobres au-dessus de 20 %, orange entre 10 et 20 %, rouge sous 10 %. La valeur reste lisible sans dépendre uniquement de la couleur.
- **Offre** : badges Free, Plus, Pro et autres offres renvoyées par Codex. Les valeurs `prolite` et `pro` correspondent respectivement à Pro **5×** et Pro **20×**, conformément à l’interface Codex actuelle. Une offre inconnue reste affichée telle quelle.
- **Période d’abonnement** : début et fin de période active lorsqu’ils sont présents dans les métadonnées de session Codex. Ce ne sont pas nécessairement la date de souscription initiale ni une échéance de paiement. Les dates absentes restent indisponibles.
- **Resets en réserve** : compteur `availableCount` fourni par Codex, accompagné des expirations disponibles. Le nombre d’éléments détaillés ne remplace jamais le compteur serveur.
- Les dates précises utilisent le fuseau horaire Windows et son décalage UTC. Le compte à rebours complète l’heure exacte ; il ne confirme pas un reset tant que le serveur n’a pas actualisé la valeur.
- En cas d’erreur, les dernières valeurs et leur ancienneté sont conservées. Une information absente reste « indisponible », jamais zéro.

## Historique et notifications

Chaque compte conserve jusqu’à 90 jours de relevés locaux. Les graphiques proposent les dernières 24 heures ou les 7 derniers jours, pour la semaine ou la fenêtre de 5 heures. Les interruptions de collecte et changements de période coupent la courbe : aucune consommation n’est inventée pendant l’absence du compte. L’historique commence avec cette version et ne reconstitue pas les périodes antérieures.

Une estimation d’épuisement apparaît après au moins 15 minutes de relevés récents et continus, si leur évolution est suffisamment régulière. Elle indique la première fenêtre susceptible de s’épuiser, au rythme observé, avant son prochain reset. Un compte inactif, des données insuffisantes ou un rythme trop irrégulier restent sans estimation.

Les réglages permettent d’activer séparément les alertes **20 %, 10 % et 5 %**, ainsi que la notification de reset. Elles portent sur les fenêtres semaine et 5 heures. Les franchissements simultanés sont regroupés ; démarrage et changement de compte restent silencieux. Un reset est annoncé seulement après un relevé confirmant une nouvelle période et un quota remonté. Le simple compte à rebours ne déclenche rien.

Le bouton **Tester une notification** permet de vérifier leur affichage. Les paramètres de notifications et le mode de concentration de Windows s’appliquent.

## Données locales

Les profils, réglages, historiques et derniers relevés sont stockés sous `%LOCALAPPDATA%\CodexTracker`, avec des permissions limitées à l’utilisateur Windows. Le tracker lit `%USERPROFILE%\.codex\auth.json` sans jamais le modifier. Il utilise uniquement le jeton d’accès courant en mémoire dans un processus Codex isolé avec un stockage de connexion `ephemeral` ; il ne conserve pas de session et ne renouvelle aucun jeton. Codex reste responsable de sa connexion. Si elle a expiré, ouvrez Codex pour la rétablir.

Les anciens coffres et sauvegardes de la version 0.1 ne sont ni utilisés ni modifiés lors de la mise à jour. Les métadonnées et derniers relevés sont conservés. Les nouveaux profils de travail temporaires sont nettoyés après collecte.

L’application ne possède pas de serveur de synchronisation et n’envoie pas vos comptes à ce dépôt. Les requêtes de quota passent par Codex et les services OpenAI ; les vérifications de mise à jour interrogent GitHub sans authentification. Aucun mot de passe n’est demandé au tracker. Les journaux applicatifs n’enregistrent pas de tokens. Les exemples et captures de ce dépôt utilisent uniquement des comptes fictifs.

Pour préconfigurer localement une première installation, créez `%LOCALAPPDATA%\CodexTracker\initial-accounts.json` avec un tableau d’adresses, par exemple `["demo@example.test"]`. Ce fichier est facultatif, lu uniquement si les réglages n’existent pas encore, et doit rester hors du dépôt. Ces comptes restent « À détecter » jusqu’à leur ouverture dans Codex.

## Développer

Installez le SDK .NET 10 puis, sous Windows :

```powershell
dotnet restore CodexTracker.slnx
dotnet build CodexTracker.slnx
dotnet test tests/CodexTracker.Tests/CodexTracker.Tests.csproj
dotnet run --project src/CodexTracker.App -- --demo
./scripts/publish.ps1
./scripts/build-installer.ps1 -InstallCompiler
```

Le dernier script peut installer le compilateur Inno Setup officiel pour l’utilisateur courant, après vérification de sa signature. La CI Windows compile la solution, lance les tests et produit le ZIP autonome ainsi que l’installateur.

La solution sépare `Core` (modèle et quotas), `Codex` (observation et protocole en lecture seule) et `App` (WPF et zone de notification). Les tests utilisent des sessions fictives et ne modifient jamais votre connexion Codex. Ils couvrent les réponses de quotas, valeurs absentes, dates et changements d’heure, changements de fichiers, isolation des comptes et réponses réseau tardives. Les contrôles réels et leurs limites sont détaillés dans [VALIDATION.md](docs/VALIDATION.md).

Pour quitter une instance existante avant une mise à jour : `CodexTracker.exe --exit`. Le mode démonstration se ferme séparément avec `CodexTracker.exe --demo --exit`.

Les captures automatisées sont réservées aux comptes fictifs : `CodexTracker.exe --demo --theme dark --screenshot dashboard.png --dpi 144 --smoke-test`. Les options `--theme light`, `--privacy`, `--peek-screenshot`, `--details-screenshot` et `--settings-screenshot` permettent de contrôler les autres états sans exporter les comptes réels.

## Références

[App Server Codex](https://learn.chatgpt.com/docs/app-server) · [Authentification Codex](https://learn.chatgpt.com/docs/auth) · [Offres Codex](https://learn.chatgpt.com/docs/pricing) · [Zone de notification Windows](https://learn.microsoft.com/en-us/windows/win32/shell/notification-area)

Projet indépendant, sans affiliation avec OpenAI. Licence MIT.
