# Codex Tracker

Vos quotas Codex, directement dans la barre des tâches Windows.

Une icône affiche le **pourcentage hebdomadaire restant** du compte suivi. Un clic ouvre un tableau de bord sombre avec tous vos comptes, abonnements, quotas, dates de reset et resets en réserve.

![Tableau de bord avec comptes fictifs](docs/dashboard.png)

## Installer

1. Téléchargez le ZIP `CodexTracker-…-win-x64.zip` depuis les [Releases](https://github.com/Aleqsd/codex-tracker/releases).
2. Extrayez-le dans un dossier permanent et lancez `CodexTracker.exe`.
3. Le compte Codex actuellement connecté est détecté. Ajoutez vos autres adresses, puis cliquez sur **Connecter** pour chacune.
4. Dans les paramètres Windows de la barre des tâches, rendez l’icône Codex Tracker visible près de l’horloge.

Windows 11 x64 et une installation de Codex avec sa CLI `codex` sont nécessaires. Le runtime .NET est inclus dans le ZIP. Le lancement avec Windows est facultatif, depuis les réglages du tracker.

## Lire le tableau de bord

- **Hebdomadaire** : quota restant de la fenêtre de 10 080 minutes du bucket `codex`. Une limite de cinq heures n’est jamais présentée comme une limite hebdomadaire.
- **Vert** au-dessus de 20 %, **orange** entre 10 et 20 %, **rouge** sous 10 %. La valeur reste lisible sans dépendre uniquement de la couleur.
- **Compte suivi** : celui dont le quota est affiché dans l’icône. Le sélectionner ne change pas le compte utilisé dans Codex.
- **Compte actif** : session détectée dans le cache de Codex. Pendant une bascule, son activation dans l’application reste à vérifier.
- **Resets en réserve** : nombre fourni par Codex, accompagné des expirations disponibles. Les détails peuvent être partiels ; leur longueur ne remplace jamais le compteur serveur.
- Les dates utilisent le fuseau horaire Windows avec décalage UTC. Le compte à rebours complète l’heure exacte ; il ne remplace pas une confirmation de reset par le serveur.
- Actualisation toutes les deux minutes. En cas d’erreur, les dernières valeurs et leur ancienneté sont conservées. Une information absente reste « indisponible », jamais zéro.

## Changer de compte

**Utiliser dans Codex** est distinct de **Afficher dans l’icône**. Une confirmation précède la fermeture et la relance, car du travail peut être interrompu. Le tracker ne force pas l’arrêt d’un processus qui refuse de quitter.

L’adaptateur de bascule cible la version Windows `26.915.4065.0` de Codex/ChatGPT, avec une session ChatGPT gérée dans `auth.json`. Il est désactivé pour les autres versions et modes de connexion. Il n’existe pas d’API publique de bascule à laquelle cet adaptateur puisse se connecter sur cette version.

Le tracker sauvegarde la session précédente sous forme chiffrée, attend la fermeture de l’application et de ses processus enfants, remplace atomiquement le cache de connexion puis relance Codex. **Une écriture du cache ne suffit pas à prouver une connexion.** Vérifiez l’adresse dans le menu du compte de Codex et confirmez dans le tracker, ou choisissez **Restaurer la session précédente**. La sauvegarde reste disponible tant que la vérification n’est pas terminée.

Les projets, conversations et réglages Codex ne sont pas déplacés. Un seul programme renouvelle les tokens d’une session : Codex pour le compte actif, le tracker pour les comptes inactifs. Si le token actif a expiré et que Codex ne l’a pas encore renouvelé, le tracker conserve les dernières données et signale leur état.

## Données locales

Les profils, réglages, résultats en cache et sessions sont stockés sous `%LOCALAPPDATA%\CodexTracker`. Les sessions conservées et la sauvegarde de bascule sont protégées par Windows DPAPI pour l’utilisateur courant. Les profils de travail temporaires nécessaires à la CLI sont limités par les permissions Windows puis nettoyés.

L’application ne possède pas de serveur de synchronisation et n’envoie pas vos comptes à ce dépôt. Les connexions passent par Codex et les services OpenAI. Aucun mot de passe n’est demandé au tracker. Les journaux applicatifs n’enregistrent pas de tokens. Les exemples et captures de ce dépôt utilisent uniquement des comptes fictifs.

Pour préconfigurer localement une première installation, créez `%LOCALAPPDATA%\CodexTracker\initial-accounts.json` avec un tableau d’adresses, par exemple `["demo@example.test"]`. Ce fichier n’est lu que si les réglages n’existent pas encore. Ne le placez pas dans le dépôt.

## Développer

Installez le SDK .NET 10 puis, sous Windows :

```powershell
dotnet restore CodexTracker.slnx
dotnet build CodexTracker.slnx
dotnet test tests/CodexTracker.Tests/CodexTracker.Tests.csproj
dotnet run --project src/CodexTracker.App -- --demo
./scripts/publish.ps1
```

La solution sépare `Core` (modèle et interprétation des quotas), `Codex` (protocole, sessions et bascule) et `App` (interface WPF et zone de notification). Les tests emploient des données fictives et un faux environnement desktop : ils ne ferment jamais votre application Codex.

Les contrôles automatisés couvrent les réponses de quotas, les valeurs manquantes, les dates, l’isolation des comptes et les transactions de bascule. Un essai OAuth et un aller-retour réel entre deux comptes nécessitent leur connexion interactive et une vérification du compte affiché. Ils ne doivent pas être confondus avec les tests simulés.

## Références

[App Server Codex](https://learn.chatgpt.com/docs/app-server) · [Authentification Codex](https://learn.chatgpt.com/docs/auth) · [Zone de notification Windows](https://learn.microsoft.com/en-us/windows/win32/shell/notification-area)

Projet indépendant, sans affiliation avec OpenAI. Licence MIT.
