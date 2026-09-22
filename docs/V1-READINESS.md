# Préparer la version 1.0

La 0.9.1 est une version de stabilisation. La préparation de la 1.0 distingue les fonctions implémentées, les vérifications déjà réalisées et les essais encore nécessaires. Une compilation verte ne valide pas à elle seule tous les comportements de Windows ou les services facultatifs.

## Fonctions implémentées et couvertes

- [x] Canal stable par défaut ; les préversions demandent un choix explicite. Changer de canal écarte le paquet et le cache de l’autre canal. Vérifié par tests HTTP, persistance et contrôles WPF.
- [x] Fichiers endommagés : sauvegarde valide récupérée, avertissement visible, aucune réactivation silencieuse des rappels ou du MCP. Un journal d’envoi illisible suspend les rappels sans effacer les reçus. Vérifié avec fichiers fictifs et vraies vues WPF.
- [x] Réglages intégrés au troisième onglet : navigation entre sept sections, conservation des saisies, avertissements de récupération et diagnostic sans comptes ni secrets. Couvert par les contrôles WPF ; les essais physiques restent distincts.

## Vérifications déjà obtenues

- [x] Mise à niveau téléchargée depuis une Release publique : téléchargement automatique, bouton visible, installation au démarrage et conservation des données. Les deux parcours 0.8.4 → 0.9.0 ont réussi dans des profils Windows de CI vierges.
- [x] Même recette 0.9.0 → 0.9.1 réussie par bouton et au lancement suivant avec le canal préversion explicitement activé, paquets publics et données fictives conservées. Rapports distincts dans VALIDATION.md.
- [x] CI Windows verte sur le commit livré `444ef42` (0.9.1) : 381 tests métier, suite WPF, construction de l’installateur et test MCP sur le binaire autonome. Cela ne valide pas un futur binaire 1.0.
- [x] Validation renforcée sur `18a3ae8` : 383 tests métier, suite WPF et protocole MCP sur l’exécutable publié. Cycle réel de l’installateur en profil CI vierge : premier lancement sans Codex, fenêtre visible et réactive, arrêt propre, réinstallation et désinstallation conservant les données fictives. Ce Windows Server de CI ne remplace pas le poste Windows 11 standard ci-dessous.
- [x] Installation locale de la 0.9.1 : exécutable attendu, processus réactif et conservation des profils, préférences, secrets chiffrés et historiques vérifiés.
- [x] Affichage local des cinq comptes confirmé dans l’interface réelle de la 0.9.1 le 22 septembre 2026, avec cinq lignes et le compteur correspondant. Aucun fichier de données n’a été restauré ou modifié manuellement pour ce constat. La cause du précédent signalement de comptes absents n’est pas établie : ce constat ne démontre pas un correctif de persistance.

Les preuves et limites sont consignées dans [VALIDATION.md](VALIDATION.md). Chaque case cochée vaut uniquement pour le scénario et la version indiqués. La suite WPF passe en CI pour la 0.9.1. Deux passages locaux avaient échoué sur le sélecteur de fuseau horaire avant correction du harnais de test ; la suite complète passe désormais aussi localement avec cette correction, sans modification du contrôle de production.

Le [test entre Releases publiques](PUBLISHED-UPDATE-TEST.md) dispose d’un workflow Windows dédié, lancé à la demande. Les chemins du bouton et du prochain démarrage utilisent chacun un profil éphémère distinct ; aucun compte personnel n’est nécessaire. La recette accepte désormais les versions numériques publiques et le canal choisi explicitement ; chaque paire nécessite une exécution et son propre rapport.

## Essais physiques et bêta à terminer

- [ ] Installer et désinstaller sur un autre PC Windows 11 x64, avec un utilisateur standard. Vérifier le premier lancement sans Codex, sans session puis avec une session.
- [ ] Changer réellement de compte dans Codex ; vérifier l’identité active, la présence des autres comptes et les dates de leurs derniers relevés, puis fermer et rouvrir le tracker. Les transitions avec fichiers fictifs et le constat local des cinq lignes ne remplacent pas ce parcours.
- [ ] Utiliser deux écrans physiques à DPI différents, sortir de veille, redémarrer Explorer et vérifier le démarrage avec Windows. Les rendus WPF et messages Windows simulés ne remplacent pas ces essais.
- [ ] Confirmer la réception d’une notification Windows sur le poste d’essai, y compris l’effet de « Ne pas déranger ». Les contrôles simulés ne prouvent pas sa réception.
- [ ] Utiliser quotidiennement le tracker pendant une semaine à plusieurs : relever mémoire/CPU, comportement de la collecte, perte et retour du réseau, notifications sans doublon et éventuelle réapparition de comptes absents. Si ce dernier symptôme réapparaît, consigner les étapes et états de l’interface avant toute restauration ; sa cause reste à reproduire.

Pour chaque essai, conserver la version exacte, la date, Windows et sa configuration d’affichage, les étapes, le résultat attendu et le résultat observé. N’inclure ni adresses, ni quotas réels, ni dossiers privés dans les preuves publiques.

## Validation du candidat 1.0

- [ ] Décider de la signature de distribution et contrôler le résultat Authenticode sur l’EXE et l’installateur du candidat. La [préparation de la signature](SIGNATURE.md) décrit l’état non signé et une piste gratuite soumise à acceptation. Aucune signature n’est revendiquée tant que ces fichiers ne sont pas réellement signés.
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
