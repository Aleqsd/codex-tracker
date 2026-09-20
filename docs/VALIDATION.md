# Validation de la version 0.3.0

Contrôles effectués sur Windows 11 x64, le 20 septembre 2026. Les tests et captures publics utilisent uniquement des comptes fictifs.

| Contrôle | Résultat |
| --- | --- |
| Compilation Release, solution complète | Réussie, aucun avertissement |
| Tests automatisés | 126 réussis : quotas, dates, abonnement, observation, isolation, historique, prévisions, notifications, confidentialité et mises à jour |
| Lecture réelle du compte Codex courant | Offre, multiplicateur, période active, quota hebdomadaire, date de reset et réserve reçus |
| Historique après collecte réelle | Premier point enregistré ; aucune prévision inventée à partir de ce seul point |
| Intégrité de la session active | Empreinte SHA-256 du cache inchangée avant/après la collecte |
| Persistance des credentials | Aucun fichier de session créé dans les données du tracker ; runtime temporaire nettoyé après collecte |
| Démonstration WPF | Vrais processus, captures clair/sombre et identités masquées, détails et réglages |
| Interactions WPF en démonstration | 12 contrôles réussis : tri, thèmes à chaud, confidentialité dans les fenêtres ouvertes, menus, graphique, minuterie et arrêt |
| Rendus WPF 96, 144 et 192 DPI | Contrôle des dimensions et lisibilité ; ces rendus ne changent pas les réglages DPI de Windows |
| Icône anneau claire et sombre | Valeurs 0, 8, 10, 20, 21, 72, 99, 100 et inconnue aux tailles 16, 20, 24 et 32 pixels dans le [rendu de contrôle](tray-ring.png) |
| Installateur Windows | Installation, mise à niveau et désinstallation réelles dans un dossier isolé ; arrêt gracieux de l’application de test et conservation du témoin de données privées |
| Mécanisme de mise à jour | Vrais exécutables de test autonomes utilisant le code de production : préparation, arrêt du parent, remplacement, contrôle de démarrage et restauration de l’ancien exécutable si le nouveau échoue |

## Couverture des tests

Les tests de collecte couvrent les changements A → B → A, une détection pendant une requête lente, le rejet d’une réponse tardive, le refus des données d’un autre compte, la disparition et le remplacement atomique de la session, la recréation du dossier et la conservation des derniers relevés. Un test garde le fichier de session ouvert en refusant toute écriture.

Les dates d’abonnement proviennent exclusivement des champs de période active de l’ID-token, jamais de la date d’émission ou d’expiration du jeton. Les valeurs absentes restent inconnues. Le mapping Pro 5×/20× a été comparé à l’interface Codex installée et à sa documentation d’offres.

Les tests d’historique vérifient la rétention de 90 jours, le regroupement des relevés, les frontières de reset, la séparation des comptes et les réponses obsolètes. Les prévisions refusent les périodes trop courtes, anciennes ou irrégulières, les interruptions et un épuisement situé après le prochain reset. Les notifications couvrent les trois seuils indépendants, la déduplication persistante, les resets confirmés et le silence au démarrage ou lors d’un changement de compte.

Les tests de mise à jour couvrent les versions, les réponses GitHub, les limites de téléchargement, les redirections, les empreintes, les archives malformées et les chemins interdits, ainsi que l’installation et la restauration. Les essais avec de vrais processus sont effectués dans un dossier isolé, avec téléchargement simulé et versions de test ; ils ne remplacent pas une mise à jour depuis une future Release publique. La consultation GitHub sans authentification peut être temporairement limitée par GitHub : l’interface affiche alors une erreur explicite et permet de réessayer.

## Contrôles interactifs restants

- Effectuer un changement réel entre deux comptes dans Codex. La lecture du compte courant est réelle ; les transitions entre identités ont été testées avec des fichiers fictifs, sans modifier la connexion de l’utilisateur.
- Vérifier plusieurs écrans physiques, un vrai changement d’échelle Windows, une sortie de veille, le survol dans les différentes configurations de barre des tâches et le rétablissement de l’icône après redémarrage d’Explorer.
- Confirmer la réception des notifications sur la configuration personnelle de Windows, notamment avec le mode de concentration activé.

Le tracker ne propose ni connexion OAuth ni bascule de compte. Il ne ferme pas Codex. Les anciens essais de bascule de la version 0.1 ne s’appliquent pas à cette version.
