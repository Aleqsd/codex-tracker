# Validation de la version 0.1.0

Contrôles effectués sur Windows 11 x64, le 20 septembre 2026.

| Contrôle | Résultat |
| --- | --- |
| Compilation Release, solution complète | Réussie, aucun avertissement |
| Tests automatisés | 66 réussis |
| Lecture réelle du compte Codex courant | Offre, quota hebdomadaire, date de reset et réserve reçus |
| Intégrité de la session active pendant la collecte | Empreinte SHA-256 du cache inchangée avant/après |
| OAuth réel : démarrage puis annulation | URL HTTPS reçue, compte resté déconnecté, session active inchangée |
| Démonstration WPF et fermeture propre | Réussies |
| Rendus WPF 96, 144 et 192 DPI | Inspectés visuellement ; ce contrôle ne change pas les réglages DPI de Windows |
| Icône numérique 16, 20, 24 et 32 px | Valeurs 0, 8, 10, 20, 21, 72, 99, 100 et inconnue inspectées |

Les tests simulés couvrent notamment le rollback après échec de lancement, la conservation d’une rotation de jeton, le refus de remplacer une session si l’application ne quitte pas, les caches malformés, la relecture de l’identité active et le coffre DPAPI.

## Contrôles interactifs restants

- Terminer une connexion OAuth pour les comptes supplémentaires, puis vérifier leur offre et leurs limites.
- Effectuer une bascule aller-retour réelle entre deux comptes depuis l’application terminée. Vérifier l’adresse affichée dans Codex à chaque étape. La preuve de succès est cette vérification, pas le remplacement de `auth.json`.
- Vérifier plusieurs écrans physiques, un vrai changement d’échelle Windows, une sortie de veille et le rétablissement de l’icône après redémarrage d’Explorer.

Ces opérations interactives n’ont pas été exécutées pendant le développement. Les tests de bascule n’arrêtent aucun processus Codex réel. Les rendus publiés utilisent des identités fictives.
