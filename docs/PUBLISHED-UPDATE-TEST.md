# Essai entre deux Releases publiques

Cette recette valide **0.8.4 → 0.9.0** avec les exécutables publiés, sans compilation de remplacement ni API simulée. Elle ne constitue pas un essai Windows sur un autre PC physique et ne valide pas à elle seule les nouveaux filtres stable/préversions de 0.9.0.

## Environnement obligatoire

Utiliser une VM Windows x64 jetable, un profil Windows réservé au test ou un runner CI Windows éphémère. Aucun compte Codex, aucune installation ou donnée Codex Tracker ne doit y être présent. Le script refuse ces états ; il ne sauvegarde, déplace, remplace ni supprime un profil existant. Déclarer `-DedicatedTestProfile` signifie que cet environnement est réservé au test.

Le mode démo ne convient pas : il désactive volontairement la mise à jour automatique. Changer seulement `LOCALAPPDATA` ou copier l’EXE dans un autre dossier ne suffit pas à isoler l’application normale.

## Exécuter

PowerShell 7 est requis. Par défaut, la commande affiche seulement son plan, sans écriture, requête réseau ou lancement :

```powershell
./scripts/test-published-update.ps1 -Plan
```

Dans le profil de test uniquement :

```powershell
./scripts/test-published-update.ps1 -Run -DedicatedTestProfile `
    -SourceVersion 0.8.4 -TargetVersion 0.9.0 -InstallMode Startup `
    -ReportPath artifacts/published-update/result.json
```

Pour le chemin du bouton, utiliser `-InstallMode Button` **dans un second environnement vierge**. Le script invoque le bouton WPF avec UI Automation ; il ne contourne pas le moteur de mise à jour. Aucun SMS, appel ou email n’est configuré ou envoyé. Les alertes Windows et rappels sont désactivés dans les préférences fictives.

## Ce qui est vérifié

- Métadonnées publiques obtenues sans jeton. Un refus HTTP ou une limitation GitHub arrête l’essai, sans nouvelle tentative. La date autorisée est enregistrée si elle est disponible.
- ZIP source vérifié contre son checksum public ; l’EXE utilisé provient directement de cette archive. Le script refuse une Release plus récente que la cible prévue, pour ne pas installer une version inattendue.
- Deux comptes fictifs, leurs préférences, un relevé et un historique sont créés. Aucun dossier de session Codex n’est importé.
- L’application normale télécharge elle-même la cible. Attente maximale de cinq minutes ; aucun fichier `prepared-update.json` n’est fabriqué par le script. Le ZIP téléchargé et l’EXE préparé sont vérifiés contre la publication publique.
- UI Automation vérifie visibilité et activation du bouton. Cela reste distinct d’une observation humaine, laissée à `null` dans le rapport. Si UI Automation est indisponible, le mode Startup peut vérifier l’installation mais son résultat global reste `partial` ; le mode Button s’arrête sans invoquer une action de remplacement.
- Le chemin Startup quitte proprement puis relance le tracker. Le chemin Button invoque le bouton visible. Le résultat persistant doit annoncer un succès, le processus cible répondre, le binaire correspondre à la publication et les quatre fichiers fictifs garder leurs empreintes.

Le rapport JSON ne contient que les étapes, résultats, versions, dates et empreintes des exécutables publics. Il exclut chemins, utilisateurs Windows, comptes, données d’utilisation et messages d’erreur libres. `passed` indique la validation de ce parcours précis, pas une validation générale de la version 1.0.

Le script ferme uniquement son instance de test, sans arrêt forcé du tracker. Les fichiers de l’essai restent dans le profil jetable ; détruire ensuite cette VM ou ce runner. Il refuse de réutiliser ce profil, même si une première tentative a échoué après création des données fictives.
