# Préparer la version 1.0

La **0.9.2** est la version de stabilisation courante. Le problème de comptes absents est expliqué et corrigé : la redirection MSIX créait deux stockages selon le mode de lancement. La signature de distribution, les essais physiques restants et la validation du binaire final restent nécessaires avant une 1.0 signée.

## État au 23 septembre 2026

- [x] Stockage unifié, récupération des anciens comptes et vérification réelle après lancement normal puis depuis Codex. Les données d’origine restent sauvegardées. Voir [STORAGE-RECOVERY.md](STORAGE-RECOVERY.md).
- [x] **396 tests métier**, suite WPF, protocole MCP publié et cycle de l’installateur réussis en [CI Windows](https://github.com/Aleqsd/codex-tracker/actions/runs/35825263224).
- [x] Mise à niveau publique **0.9.1 → 0.9.2**, canal stable : téléchargement par l’application, bouton visible, installation par bouton et au prochain démarrage, puis conservation des données fictives. [Deux parcours réussis](https://github.com/Aleqsd/codex-tracker/actions/runs/35827491176), [rapports](validation/public-update-0.9.1-to-0.9.2.json).
- [x] Notifications Windows confirmées visibles par l’utilisateur après installation de la 0.8.4. Le réglage « Ne pas déranger » a été résolu par l’utilisateur. Cela ne valide pas tous les autres postes.
- [x] Intégration SignPath préparée avec vérification Authenticode, éditeur et horodatage, contrôle de l’EXE réellement installé et séparation des répétitions non signées. Les 28 contrôles passent. La [CI complète](https://github.com/Aleqsd/codex-tracker/actions/runs/35827933069) et la [répétition du parcours de release](https://github.com/Aleqsd/codex-tracker/actions/runs/35827933693) réussissent sur `9811e76` ; aucune signature du projet n’est revendiquée.
- [x] Candidature SignPath envoyée le 23 septembre 2026 ; réception confirmée par le formulaire, sans inscription aux communications commerciales.
- [x] Première soumission WinGet mise à niveau vers la 0.9.2 corrigée : Setup public retéléchargé, empreinte identique et manifeste validé localement. Les contrôles Microsoft, dont installation et analyse du Setup, ont réussi. La [PR Microsoft](https://github.com/microsoft/winget-pkgs/pull/438574) attend toujours la revue manuelle ; le paquet n’est pas annoncé disponible dans le catalogue.
- [x] Préparation WinGet couverte par 14 contrôles isolés et [CI Windows complète](https://github.com/Aleqsd/codex-tracker/actions/runs/35832082507) verte sur `109410f`. Aucun avis de vulnérabilité NuGet remonté pour les dépendances directes et transitives lors du contrôle du 23 septembre ; cela ne remplace pas un audit du code.
- [ ] Choisir une voie de signature admissible puis vérifier sa première signature réelle. La candidature Foundation n’a pas obtenu d’accès ; ce point n’est plus simplement en attente de réponse. Voir [les alternatives et leurs contraintes](SIGNATURE.md).
- [ ] Essais matériels et bêta ci-dessous, puis fabrication et validation de l’exécutable final 1.0.

Le parcours de signature est manuel et sa répétition ne contacte pas SignPath. Son admission et ses délais dépendent de la fondation ; une préparation ou un test simulé ne suffit pas à cocher la signature.

## Fonctions implémentées et couvertes

- [x] Canal stable par défaut ; les préversions demandent un choix explicite. Changer de canal écarte le paquet et le cache de l’autre canal. Vérifié par tests HTTP, persistance et contrôles WPF.
- [x] Fichiers endommagés : sauvegarde valide récupérée, avertissement visible, aucune réactivation silencieuse des rappels ou du MCP. Un journal d’envoi illisible suspend les rappels sans effacer les reçus. Vérifié avec fichiers fictifs et vraies vues WPF.
- [x] Réglages intégrés au troisième onglet : navigation entre sept sections, conservation des saisies, avertissements de récupération et diagnostic sans comptes ni secrets. Couvert par les contrôles WPF ; les essais physiques restent distincts.
- [x] Échec de démarrage MCP après 20 secondes : code de sortie non nul et explication sur stderr, sans texte parasite sur stdout. Régression reproduite sur la 0.9.2, corrigée et validée sur le candidat autonome et en [CI Windows](https://github.com/Aleqsd/codex-tracker/actions/runs/35840768129). Prêt pour la prochaine version, pas encore inclus dans une Release.

## Vérifications déjà obtenues

- [x] Mise à niveau téléchargée depuis une Release publique : téléchargement automatique, bouton visible, installation au démarrage et conservation des données. Les deux parcours 0.8.4 → 0.9.0 ont réussi dans des profils Windows de CI vierges.
- [x] Même recette 0.9.0 → 0.9.1 réussie par bouton et au lancement suivant avec le canal préversion explicitement activé, paquets publics et données fictives conservées. Rapports distincts dans VALIDATION.md.
- [x] CI Windows verte sur le commit livré `444ef42` (0.9.1) : 381 tests métier, suite WPF, construction de l’installateur et test MCP sur le binaire autonome. Cela ne valide pas un futur binaire 1.0.
- [x] Validation renforcée sur `18a3ae8` : 383 tests métier, suite WPF et protocole MCP sur l’exécutable publié. Cycle réel de l’installateur en profil CI vierge : premier lancement sans Codex, fenêtre visible et réactive, arrêt propre, réinstallation et désinstallation conservant les données fictives. Ce Windows Server de CI ne remplace pas le poste Windows 11 standard ci-dessous.
- [x] Installation locale de la 0.9.1 : exécutable attendu, processus réactif et conservation des profils, préférences, secrets chiffrés et historiques vérifiés.
- [x] Affichage local des cinq comptes confirmé dans l’interface réelle de la 0.9.1 le 22 septembre 2026. Ce constat historique ne prouvait pas la correction : le défaut de redirection a été identifié le lendemain et traité dans la 0.9.2, avec un test des deux contextes de lancement.

Les preuves et limites sont consignées dans [VALIDATION.md](VALIDATION.md). Chaque case cochée vaut uniquement pour le scénario et la version indiqués. La suite WPF passe en CI pour la 0.9.1. Deux passages locaux avaient échoué sur le sélecteur de fuseau horaire avant correction du harnais de test ; la suite complète passe désormais aussi localement avec cette correction, sans modification du contrôle de production.

Le [test entre Releases publiques](PUBLISHED-UPDATE-TEST.md) dispose d’un workflow Windows dédié, lancé à la demande. Les chemins du bouton et du prochain démarrage utilisent chacun un profil éphémère distinct ; aucun compte personnel n’est nécessaire. La recette accepte désormais les versions numériques publiques et le canal choisi explicitement ; chaque paire nécessite une exécution et son propre rapport.

## Essais physiques et bêta à terminer

- [ ] Installer et désinstaller sur un autre PC Windows 11 x64, avec un utilisateur standard. Vérifier le premier lancement sans Codex, sans session puis avec une session.
- [ ] Changer réellement de compte dans Codex ; vérifier l’identité active, la présence des autres comptes et les dates de leurs derniers relevés, puis fermer et rouvrir le tracker. Les transitions avec fichiers fictifs et le constat local des cinq lignes ne remplacent pas ce parcours.
- [ ] Utiliser deux écrans physiques à DPI différents, sortir de veille, redémarrer Explorer et vérifier le démarrage avec Windows. Les rendus WPF et messages Windows simulés ne remplacent pas ces essais.
- [ ] Confirmer la réception d’une notification Windows sur le poste d’essai, y compris l’effet de « Ne pas déranger ». Les contrôles simulés ne prouvent pas sa réception.
- [ ] Utiliser quotidiennement la 0.9.2 ou un candidat ultérieur pendant une semaine à plusieurs : relever mémoire/CPU, comportement de la collecte, perte et retour du réseau, notifications sans doublon et éventuelle réapparition de comptes absents. En cas de nouveau signalement, vérifier le contexte de lancement et le chemin physique du stockage avant toute restauration.

Pour chaque essai, conserver la version exacte, la date, Windows et sa configuration d’affichage, les étapes, le résultat attendu et le résultat observé. N’inclure ni adresses, ni quotas réels, ni dossiers privés dans les preuves publiques.

## Validation du candidat 1.0

- [ ] Finaliser la voie de signature retenue et contrôler le résultat sur les fichiers du candidat. La [préparation de la signature](SIGNATURE.md) décrit l’intégration existante et les alternatives. Aucune signature n’est revendiquée tant que les fichiers ne sont pas réellement signés.
- [ ] Exécuter la CI Windows et le test MCP sur le binaire autonome exact destiné à la 1.0 ; résoudre ou expliquer toute différence avec les essais locaux.
- [ ] Vérifier le Setup, le ZIP et leurs SHA-256, les notes courtes et la mise à niveau depuis la version publique retenue, en conservant les données existantes. Les preuves des 0.9.0 et 0.9.1 restent un historique, pas la validation du candidat.
- [ ] Consigner les résultats des essais physiques et bêta ci-dessus, ainsi que les limites des intégrations facultatives, avant de décider la sortie stable.

## Essai utilisateur, sans configuration avancée

1. Installer le `Setup.exe`, ouvrir Codex et vérifier le compte détecté. Le tracker ne demande jamais de lui transmettre une session.
2. Changer de compte dans Codex ; vérifier l’identité active et l’ancienneté des autres relevés.
3. Ouvrir Comptes et Resets en clair/sombre, puis masquer et rouvrir le tracker depuis la barre d’état.
4. Tester une notification Windows depuis Réglages → Canaux. Consulter le résultat et, si nécessaire, les paramètres « Ne pas déranger » de Windows.
5. Fermer/rouvrir, mettre le PC en veille puis reprendre. Vérifier que les échéances restent datées et les données anciennes identifiées.
6. Après un souci : Réglages → Application → Préparer un diagnostic. Relire avant copie et joindre les étapes pour reproduire à un signalement. Éviter les captures avec des comptes réels.

## Intégrations facultatives

Le protocole MCP est vérifié par un client de test réel ; il reste à valider l’expérience de configuration dans les assistants tiers. L’import final Google Agenda nécessite un essai volontaire. Twilio et SendGrid sont testés avec des API simulées : un test payant ne doit jamais être lancé automatiquement pour compléter cette liste.

## Si les données sont endommagées

Les sauvegardes `.bak` et les copies `.corrupt-*` restent uniquement dans le dossier privé du tracker. Une sauvegarde conserve une seule génération valide et peut précéder les derniers ajouts de comptes ou changements de réglages. Ne pas joindre ces fichiers à un ticket public : ils peuvent contenir des adresses et des données d’utilisation. Après récupération des préférences, les rappels et le MCP restent désactivés jusqu’à votre choix. Réactiver uniquement ce qui est souhaité.

Si aucune copie valide des profils n’existe, le tracker explique l’erreur et conserve les fichiers ; il ne recrée pas silencieusement une liste vide. Un journal d’envoi corrompu ne se restaure pas à partir d’une ancienne copie : elle pourrait oublier des envois et provoquer des doublons. Conserver les fichiers et demander de l’aide.
