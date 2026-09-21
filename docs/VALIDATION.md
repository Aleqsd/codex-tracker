# Validation de la version 0.4.1

Contrôles effectués sur Windows 11 x64, les 20 et 21 septembre 2026. Les tests et captures publics utilisent uniquement des comptes fictifs.

| Contrôle | Résultat |
| --- | --- |
| Compilation Release, solution complète | Réussie, aucun avertissement |
| Tests automatisés | 199 réussis : quotas, dates, abonnement, observation, isolation, historique, prévisions, notifications, confidentialité, mises à jour, reprise et conseil de compte |
| Lecture réelle du compte Codex courant | Offre, multiplicateur, période active, quota hebdomadaire, date de reset et réserve reçus |
| Historique après collecte réelle | Premier point enregistré ; aucune prévision inventée à partir de ce seul point |
| Intégrité de la session active | Empreinte SHA-256 du cache inchangée avant/après la collecte |
| Persistance des credentials | Aucun fichier de session créé dans les données du tracker ; runtime temporaire nettoyé après collecte |
| Démonstration WPF | Vrais processus, captures clair/sombre et identités masquées, détails et réglages |
| Interactions WPF en démonstration | 21 contrôles réussis : badges de réserve, infobulle ouverte avec dates et fuseau, tri, thèmes à chaud, confidentialité dans les fenêtres ouvertes et le conseil, menus, graphique, minuterie et arrêt |
| Rendus WPF 96, 144 et 192 DPI | Contrôle des dimensions et lisibilité ; ces rendus ne changent pas les réglages DPI de Windows |
| Icône anneau claire et sombre | Valeurs 0, 8, 10, 20, 21, 72, 99, 100 et inconnue aux tailles 16, 20, 24 et 32 pixels dans le [rendu de contrôle](tray-ring.png) |
| Installateur Windows | Installation, mise à niveau et désinstallation réelles dans un dossier isolé ; arrêt gracieux de l’application de test et conservation du témoin de données privées |
| Mécanisme de mise à jour | Vrais exécutables de test autonomes utilisant le code de production : préparation, arrêt du parent, remplacement, contrôle de démarrage et restauration de l’ancien exécutable si le nouveau échoue |
| Limitation GitHub réelle | Réponse anonyme limitée : échéance serveur conservée ; deuxième essai et recréation du service ne produisent aucune nouvelle requête pendant ce délai |
| Comportements Windows | 17 tests de placement et 13 contrôles sur de vrais HWND, décrits ci-dessous |

## Vérifications Windows de la version 0.4

L’environnement disponible comporte un écran 2880 × 1800 à 200 %, avec une zone utile de 2880 × 1704 pixels physiques. Les fenêtres principale et Réglages, déplacées volontairement hors écran, sont récupérées après des messages `WM_DISPLAYCHANGE` et `SPI_SETWORKAREA` adressés uniquement à la démonstration.

L’icône de cette seule démonstration a été retirée avec `Shell_NotifyIcon(NIM_DELETE)`. Son absence, puis son retour avec la même identité après des messages ciblés `TaskbarCreated`, ont été vérifiés avec `Shell_NotifyIconGetRect`. Explorer n’a pas été redémarré. Le traitement natif de [NotifyIcon .NET 10](https://github.com/dotnet/winforms/blob/v10.0.0/src/System.Windows.Forms/System/Windows/Forms/NotifyIcon.cs) est complété par une réaffirmation différée de l’icône.

Les gestionnaires de veille/reprise de l’application ont été invoqués dans la démonstration ; aucune mise en veille du PC n’a été déclenchée. Les tests du collecteur couvrent l’abandon des réponses commencées avant la suspension, une identité modifiée pendant la pause, le silence des notifications à la reprise et les événements survenant avant l’initialisation. Après 80 cycles de menus et d’aperçu, le nombre de ressources GDI est resté stable (38).

Les origines négatives de moniteur, les DPI 96/144/192, les barres sur les quatre côtés, les petites zones utiles et la disparition d’un écran sont couverts par les tests de géométrie. Ces tests ne remplacent pas une manipulation de plusieurs écrans physiques.

## Couverture des tests

Les tests de collecte couvrent les changements A → B → A, une détection pendant une requête lente, le rejet d’une réponse tardive, le refus des données d’un autre compte, la disparition et le remplacement atomique de la session, la recréation du dossier et la conservation des derniers relevés. Un test garde le fichier de session ouvert en refusant toute écriture.

Les dates d’abonnement proviennent exclusivement des champs de période active de l’ID-token, jamais de la date d’émission ou d’expiration du jeton. Les valeurs absentes restent inconnues. Le mapping Pro 5×/20× a été comparé à l’interface Codex installée et à sa documentation d’offres.

Les tests d’historique vérifient la rétention de 90 jours, le regroupement des relevés, les frontières de reset, la séparation des comptes et les réponses obsolètes. Les prévisions refusent les périodes trop courtes, anciennes ou irrégulières, les interruptions et un épuisement situé après le prochain reset. Les notifications couvrent les trois seuils indépendants, la déduplication persistante, les resets confirmés et le silence au démarrage ou lors d’un changement de compte.

Les tests de mise à jour couvrent les versions, les réponses GitHub, les limites de téléchargement, les redirections, les empreintes, les archives malformées et les chemins interdits, ainsi que l’installation et la restauration. Le cache et les délais couvrent également ETag/304, la pagination, la corruption locale, les réponses trop volumineuses, le regroupement des requêtes et l’annulation d’un seul appelant. Les essais avec de vrais processus réalisés en 0.3 restent applicables au mécanisme d’installation inchangé : ils utilisent un dossier isolé, un téléchargement simulé et des versions de test. Ils ne remplacent pas une mise à jour depuis une future Release publique. La consultation sans authentification peut être temporairement limitée par GitHub ; le tracker affiche la date du cache et la prochaine vérification autorisée selon les [consignes GitHub](https://docs.github.com/en/rest/using-the-rest-api/best-practices-for-using-the-rest-api).

Le conseil de compte possède 28 tests couvrant les seuils, la fraîcheur au tick près, les valeurs absentes, les identités contradictoires, les erreurs, les resets non confirmés, les décalages horaires et les changements d’heure. Aucune capacité absolue n’est déduite d’un pourcentage ou d’une offre.

## Contrôles interactifs restants

- Effectuer un changement réel entre deux comptes dans Codex. La lecture du compte courant est réelle ; les transitions entre identités ont été testées avec des fichiers fictifs, sans modifier la connexion de l’utilisateur.
- Vérifier plusieurs écrans physiques, un vrai changement d’échelle Windows, une sortie de veille, le survol dans les différentes configurations de barre des tâches et le rétablissement de l’icône après redémarrage d’Explorer.
- Confirmer la réception des notifications sur la configuration personnelle de Windows, notamment avec le mode de concentration activé.

Le tracker ne propose ni connexion OAuth ni bascule de compte. Il ne ferme pas Codex. Les anciens essais de bascule de la version 0.1 ne s’appliquent pas à cette version.
