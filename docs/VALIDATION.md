# Validation de la version 0.2.0

Contrôles effectués sur Windows 11 x64, le 20 septembre 2026.

| Contrôle | Résultat |
| --- | --- |
| Compilation Release, solution complète | Réussie, aucun avertissement |
| Tests automatisés | 76 réussis : quotas, dates, abonnement, surveillance, isolation et arrêt |
| Lecture réelle du compte Codex courant | Offre, multiplicateur, période active, quota hebdomadaire, date de reset et réserve reçus |
| Intégrité de la session active pendant la collecte | Empreinte SHA-256 du cache inchangée avant/après |
| Persistance des credentials | Aucun fichier de session créé dans les données du tracker ; runtime temporaire nettoyé après collecte réelle |
| Démonstration WPF et fermeture par `--exit` | Testées sur un véritable processus de l’application |
| Rendus WPF 96, 144 et 192 DPI | Inspectés visuellement ; ce contrôle ne change pas les réglages DPI de Windows |

Les tests automatisés utilisent des comptes fictifs. Ils couvrent notamment les changements A → B → A, la détection pendant une requête lente, le rejet d’une ancienne réponse après changement de génération, le refus d’un relevé d’un autre compte, la disparition et le remplacement atomique de la session, la recréation du dossier, la déduplication des notifications et la conservation des derniers relevés. Un test garde le fichier de session ouvert en refusant toute écriture.

Les dates d’abonnement proviennent exclusivement des champs de période active de l’ID-token. Les dates d’émission et d’expiration du jeton ne sont jamais utilisées pour représenter l’abonnement. Les valeurs absentes ou malformées restent inconnues. Le mapping Pro 5×/20× a été comparé à l’interface de la version Codex installée et à sa documentation d’offres.

## Contrôles interactifs restants

- Effectuer un changement réel entre deux comptes dans Codex et vérifier la détection dans le tracker. La lecture du compte courant est réelle ; les transitions entre identités ont été testées avec des fichiers fictifs, sans modifier la connexion Codex de l’utilisateur.
- Vérifier plusieurs écrans physiques, un vrai changement d’échelle Windows, une sortie de veille et le rétablissement de l’icône après redémarrage d’Explorer.

Le tracker ne propose plus de connexion OAuth ni de bascule de compte. Les anciens essais de bascule de la version 0.1 ne s’appliquent pas à cette version. Les rendus publiés utilisent uniquement des identités fictives.
