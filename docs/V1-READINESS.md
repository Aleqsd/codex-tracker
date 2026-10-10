# Préparer la version 1.0

La **0.9.18** est la version courante. La 1.0 sera **non signée** et distribuée par les Releases GitHub : la signature de code et WinGet ont été abandonnés le 10 octobre 2026. Il n’y a plus de CI distante ; la validation passe par `scripts/release.ps1` et par la mise à jour réelle du poste avec `scripts/update-this-pc.ps1`. Restent les essais physiques ci-dessous, le cycle de l’installateur en profil jetable et la validation du binaire final.

## État au 10 octobre 2026

- [x] Pipeline local vert sur la 0.9.18 (`d79ae19`) : 551 tests métier, suite WPF (322 contrôles), ZIP, Setup et SHA-256, protocole MCP stdio (14 contrôles) et collecteurs Claude Code (21 contrôles) sur l’exécutable publié, délai de démarrage MCP. Release publiée puis installée sur le poste par le moteur de mise à jour : quatre comptes conservés, `DisplayVersion` Windows identique à la version de l’EXE.
- [x] Depuis la 0.9.13 : refonte de l’interface, animations, anglais, alertes d’épuisement, notifications actionnables, date du reset hebdomadaire Claude (relevé du terminal ou remise à zéro observée), Codex Pro sans quota de 5 heures, aperçu de l’icône à deux comptes. Couverts par les tests métier et la suite WPF.
- [x] Aucun avis de vulnérabilité NuGet pour les dépendances directes et transitives (`dotnet list package --vulnerable --include-transitive`, 10 octobre 2026). Cela ne remplace pas un audit du code.
- [ ] Cycle de l’installateur (`test-installer.ps1`) et mise à niveau publique (`test-published-update.ps1`, bouton et démarrage) dans un profil jetable : à relancer dans Windows Sandbox. Les preuves en profil vierge précèdent la 0.9.13 et le passage de la vérification des mises à jour à 15 minutes ; la désinstallation retire désormais l’enregistrement des notifications et n’a jamais été testée.
- [ ] Réception réelle des notifications actionnables (Ouvrir, Rappeler plus tard) et effet de « Ne pas déranger ».
- [ ] Passage manuel complet de l’interface en anglais.

## Historique au 23 septembre 2026

- [x] Stockage unifié, récupération des anciens comptes et vérification réelle après lancement normal puis depuis Codex. Les données d’origine restent sauvegardées. Voir [STORAGE-RECOVERY.md](STORAGE-RECOVERY.md).
- [x] **396 tests métier**, suite WPF, protocole MCP publié et cycle de l’installateur réussis en [CI Windows](https://github.com/Aleqsd/codex-tracker/actions/runs/35825263224).
- [x] Mise à niveau publique **0.9.1 → 0.9.2**, canal stable : téléchargement par l’application, bouton visible, installation par bouton et au prochain démarrage, puis conservation des données fictives. [Deux parcours réussis](https://github.com/Aleqsd/codex-tracker/actions/runs/35827491176), [rapports](validation/public-update-0.9.1-to-0.9.2.json).
- [x] Notifications Windows confirmées visibles par l’utilisateur après installation de la 0.8.4. Le réglage « Ne pas déranger » a été résolu par l’utilisateur. Cela ne valide pas tous les autres postes.
- [x] Intégration SignPath préparée avec vérification Authenticode, éditeur et horodatage, contrôle de l’EXE réellement installé et séparation des répétitions non signées. Les 28 contrôles passent. La [CI complète](https://github.com/Aleqsd/codex-tracker/actions/runs/35827933069) et la [répétition du parcours de release](https://github.com/Aleqsd/codex-tracker/actions/runs/35827933693) réussissent sur `9811e76` ; aucune signature du projet n’est revendiquée.
- [x] Candidature SignPath envoyée le 23 septembre 2026 ; réception confirmée par le formulaire, sans inscription aux communications commerciales.
- [x] Première soumission WinGet mise à niveau vers la 0.9.2 corrigée : Setup public retéléchargé, empreinte identique et manifeste validé localement. Les contrôles Microsoft, dont installation et analyse du Setup, ont réussi. La [PR Microsoft](https://github.com/microsoft/winget-pkgs/pull/438574) attend toujours la revue manuelle ; le paquet n’est pas annoncé disponible dans le catalogue.
- [x] Préparation WinGet couverte par 14 contrôles isolés et [CI Windows complète](https://github.com/Aleqsd/codex-tracker/actions/runs/35832082507) verte sur `109410f`. Aucun avis de vulnérabilité NuGet remonté pour les dépendances directes et transitives lors du contrôle du 23 septembre ; cela ne remplace pas un audit du code.
- Signature : abandonnée le 10 octobre 2026. La candidature Foundation n’avait pas obtenu d’accès ; l’intégration SignPath et ses scripts ont été retirés.
- WinGet : abandonné le 10 octobre 2026 ; la PR Microsoft ci-dessus n’est plus suivie.
- [ ] Essais matériels et bêta ci-dessous, puis fabrication et validation de l’exécutable final 1.0.

## Fonctions implémentées et couvertes

- [x] Canal stable par défaut ; les préversions demandent un choix explicite. Changer de canal écarte le paquet et le cache de l’autre canal. Vérifié par tests HTTP, persistance et contrôles WPF.
- [x] Fichiers endommagés : sauvegarde valide récupérée, avertissement visible, aucune réactivation silencieuse des rappels ou du MCP. Un journal d’envoi illisible suspend les rappels sans effacer les reçus. Vérifié avec fichiers fictifs et vraies vues WPF.
- [x] Réglages intégrés au troisième onglet : navigation entre sept sections, conservation des saisies, avertissements de récupération et diagnostic sans comptes ni secrets. Couvert par les contrôles WPF ; les essais physiques restent distincts.
- [x] Échec de démarrage MCP après 20 secondes : code de sortie non nul et explication sur stderr, sans texte parasite sur stdout. Régression reproduite sur la 0.9.2, corrigée et validée sur le candidat autonome et en [CI Windows](https://github.com/Aleqsd/codex-tracker/actions/runs/35840768129). Livré depuis la 0.9.3 et contrôlé par `scripts/release.ps1` à chaque version.

## Vérifications déjà obtenues

- [x] Mise à niveau téléchargée depuis une Release publique : téléchargement automatique, bouton visible, installation au démarrage et conservation des données. Les deux parcours 0.8.4 → 0.9.0 ont réussi dans des profils Windows de CI vierges.
- [x] Même recette 0.9.0 → 0.9.1 réussie par bouton et au lancement suivant avec le canal préversion explicitement activé, paquets publics et données fictives conservées. Rapports distincts dans VALIDATION.md.
- [x] CI Windows verte sur le commit livré `444ef42` (0.9.1) : 381 tests métier, suite WPF, construction de l’installateur et test MCP sur le binaire autonome. Cela ne valide pas un futur binaire 1.0.
- [x] Validation renforcée sur `18a3ae8` : 383 tests métier, suite WPF et protocole MCP sur l’exécutable publié. Cycle réel de l’installateur en profil CI vierge : premier lancement sans Codex, fenêtre visible et réactive, arrêt propre, réinstallation et désinstallation conservant les données fictives. Ce Windows Server de CI ne remplace pas le poste Windows 11 standard ci-dessous.
- [x] Installation locale de la 0.9.1 : exécutable attendu, processus réactif et conservation des profils, préférences, secrets chiffrés et historiques vérifiés.
- [x] Affichage local des cinq comptes confirmé dans l’interface réelle de la 0.9.1 le 22 septembre 2026. Ce constat historique ne prouvait pas la correction : le défaut de redirection a été identifié le lendemain et traité dans la 0.9.2, avec un test des deux contextes de lancement.

Les preuves et limites sont consignées dans [VALIDATION.md](VALIDATION.md). Chaque case cochée vaut uniquement pour le scénario et la version indiqués. La suite WPF passe en CI pour la 0.9.1. Deux passages locaux avaient échoué sur le sélecteur de fuseau horaire avant correction du harnais de test ; la suite complète passe désormais aussi localement avec cette correction, sans modification du contrôle de production.

Le [test entre Releases publiques](PUBLISHED-UPDATE-TEST.md) se lance dans un profil Windows jetable (Windows Sandbox ou VM) ; son ancien workflow GitHub a été retiré avec la CI. Les chemins du bouton et du prochain démarrage utilisent chacun un profil éphémère distinct ; aucun compte personnel n’est nécessaire. La recette accepte désormais les versions numériques publiques et le canal choisi explicitement ; chaque paire nécessite une exécution et son propre rapport.

## Nouveau PC Windows 11 — 28 septembre 2026

L’utilisateur confirme avoir installé le Setup sur ce nouveau PC sans incident. Le poste est sous Windows 11 Professionnel x64, build 26200. Ce retour valide l’installation rapportée ; il ne valide pas une désinstallation, un premier lancement sans Codex ou un changement réel de compte. L’utilisateur indique ne pas encore avoir effectué le parcours changement de compte puis fermeture/réouverture du tracker.

L’exécutable installé est le correctif local **0.9.8-polices.3**, identique au candidat local vérifié par SHA-256, et reste non signé. Il précède le commit d’animations `57464bb`. L’enregistrement Windows affiche encore **0.9.8-polices.1** : cet écart de métadonnées reste à corriger. L’instance tourne avec élévation ; l’accès à son canal local est refusé depuis le processus de contrôle non élevé. Le lancement et l’accès MCP sans élévation restent donc à vérifier.

Contrôles passifs réussis sur l’instance existante : chemin physique du stockage attendu, verrou exclusif présent, fichiers JSON et sauvegardes lisibles, processus réactif sans activation de fenêtre, démarrage avec Windows configuré vers l’installation. Pendant **130,7 secondes**, un nouveau relevé automatique est arrivé, les comptes présents sont conservés et les empreintes des préférences et de la session Codex restent inchangées. Les six mesures de mémoire restent à **176,5 Mio** et le CPU moyen représente **0,155 % d’un cœur logique**. Cette courte observation ne remplace pas une semaine de bêta ni un test de fuite mémoire. Aucun événement de plantage visant `CodexTracker.exe` n’a été trouvé dans le journal Application sur les deux derniers jours.

Le test du délai de démarrage MCP passe sur cet exécutable installé : un canal de démonstration volontairement bloqué provoque l’erreur attendue en **20,1 secondes**, code 1, stdout vide et explication sur stderr. Aucun envoi externe ni changement de réglage. FFXIV étant actif, aucune fenêtre n’a été ouverte ; les interactions, la veille, les écrans physiques et la réception d’une notification restent non testés sur ce poste.

## Essais physiques et bêta à terminer

- [x] Installation du Setup sur un nouveau PC Windows 11 x64 confirmée par l’utilisateur le 28 septembre 2026, complétée par les contrôles passifs ci-dessus.
- [ ] Compléter la recette en utilisateur non élevé : lancement et accès MCP, désinstallation avec conservation des données, premier lancement sans Codex, sans session puis avec une session. Le processus actuellement élevé ne valide pas ce parcours.
- [ ] Changer réellement de compte dans Codex ; vérifier l’identité active, la présence des autres comptes et les dates de leurs derniers relevés, puis fermer et rouvrir le tracker. Les transitions avec fichiers fictifs et le constat local des cinq lignes ne remplacent pas ce parcours.
- [ ] Utiliser deux écrans physiques à DPI différents, sortir de veille, redémarrer Explorer et vérifier le démarrage avec Windows. Les rendus WPF et messages Windows simulés ne remplacent pas ces essais.
- [ ] Confirmer la réception d’une notification Windows sur le poste d’essai, y compris l’effet de « Ne pas déranger ». Les contrôles simulés ne prouvent pas sa réception.
- [ ] Utiliser quotidiennement la 0.9.2 ou un candidat ultérieur pendant une semaine à plusieurs : relever mémoire/CPU, comportement de la collecte, perte et retour du réseau, notifications sans doublon et éventuelle réapparition de comptes absents. En cas de nouveau signalement, vérifier le contexte de lancement et le chemin physique du stockage avant toute restauration.

Pour chaque essai, conserver la version exacte, la date, Windows et sa configuration d’affichage, les étapes, le résultat attendu et le résultat observé. N’inclure ni adresses, ni quotas réels, ni dossiers privés dans les preuves publiques.

## Validation du candidat 1.0

- [ ] Passer à 1.0.0 avec `scripts/bump.ps1`, puis exécuter `scripts/release.ps1 -Publish -UpdateThisPc` sur le commit exact destiné à la 1.0 ; résoudre ou expliquer toute différence avec les essais précédents.
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
