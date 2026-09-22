# Préparer la version 1.0

La 0.9 est une version de stabilisation. Une compilation verte ne valide pas à elle seule tous les comportements de Windows ou les services facultatifs.

## Critères de sortie

- [ ] Mise à niveau téléchargée depuis une Release publique : téléchargement automatique, bouton visible, installation au démarrage et conservation des données.
- [x] Canal stable par défaut ; les préversions demandent un choix explicite. Changer de canal écarte le paquet et le cache de l’autre canal. Vérifié par tests HTTP, persistance et contrôles WPF.
- [x] Fichiers endommagés : sauvegarde valide récupérée, avertissement visible, aucune réactivation silencieuse des rappels ou du MCP. Un journal d’envoi illisible suspend les rappels sans effacer les reçus. Vérifié avec fichiers fictifs et vraies vues WPF.
- [ ] Installation et désinstallation sur un autre PC Windows 11 x64, avec un utilisateur standard. Premier lancement sans Codex, sans session puis avec une session.
- [ ] Deux écrans physiques à DPI différents, sortie de veille, redémarrage d’Explorer et démarrage Windows. Les rendus WPF ne remplacent pas ces essais.
- [ ] Utilisation quotidienne pendant une semaine par plusieurs personnes : consommation mémoire/CPU, collecte, réseau indisponible et notifications sans doublon.
- [ ] Choix explicite pour la signature de distribution ; contrôler le résultat Authenticode sur l’EXE et l’installateur. Aucune signature n’est revendiquée tant que ces fichiers ne sont pas réellement signés.
- [x] CI Windows verte sur le commit 0.9.0 livré ; EXE, ZIP et checksum publiés ; test MCP sur le binaire autonome et notes de version courtes. À répéter pour le futur binaire 1.0.

Les preuves terminées sont consignées dans [VALIDATION.md](VALIDATION.md). Les cases correspondent à une validation complète, pas seulement à une implémentation.

Le [test entre Releases publiques](PUBLISHED-UPDATE-TEST.md) dispose d’un workflow Windows dédié, lancé à la demande. Les chemins du bouton et du prochain démarrage utilisent chacun un profil éphémère distinct ; aucun compte personnel n’est nécessaire.

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

Les sauvegardes `.bak` et les copies `.corrupt-*` restent uniquement dans le dossier privé du tracker. Ne pas les joindre à un ticket public : elles peuvent contenir des adresses et des données d’utilisation. Après récupération des préférences, les rappels et le MCP restent désactivés jusqu’à votre choix. Réactiver uniquement ce qui est souhaité.

Si aucune copie valide des profils n’existe, le tracker explique l’erreur et conserve les fichiers ; il ne recrée pas silencieusement une liste vide. Un journal d’envoi corrompu ne se restaure pas à partir d’une ancienne copie : elle pourrait oublier des envois et provoquer des doublons. Conserver les fichiers et demander de l’aide.
