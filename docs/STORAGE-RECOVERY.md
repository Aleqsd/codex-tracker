# Comptes absents après un redémarrage

La version 0.9.2 corrige deux stockages distincts lorsque le tracker était lancé depuis Codex pour Windows : une copie normale et une copie redirigée par MSIX. Les variables d’environnement affichaient pourtant le même chemin. Même l’absence d’identité de package ne permettait pas de détecter cette redirection.

Au démarrage, le tracker vérifie le chemin physique d’un fichier temporaire. En cas de redirection, il redémarre avec le contexte du bureau Windows puis vérifie de nouveau le stockage. Si la redirection persiste, il s’arrête avec une explication au lieu de créer un nouveau profil. Le MCP conserve ses flux standard ; seul son processus d’interface ouvre le stockage.

Lors de la première ouverture corrigée, les anciens comptes du cache local du package Codex sont réunis avec ceux de `%LOCALAPPDATA%\CodexTracker` :

- rapprochement par adresse et conservation des identifiants déjà utilisés ;
- dernier relevé choisi selon sa date réelle, valeurs inconnues conservées ;
- historique d’utilisation réattribué au bon compte ;
- sauvegarde des fichiers concernés sous `CodexTracker\recovery` avant toute fusion ;
- ancienne copie conservée ; aucun accès aux sessions ni changement de compte Codex ;
- aucune importation de clés, de destinataires ou d’autorisations d’envoi ;
- journal de migration pour qu’un compte supprimé ensuite ne réapparaisse pas.

Si une ancienne instance utilise encore la copie à récupérer, ou si la liste des comptes est illisible sans sauvegarde valide, le démarrage s’arrête en préservant les fichiers.

Les personnalisations et paramètres de notifications restent ceux du stockage Windows normal. Les préférences de l’ancienne copie sont conservées dans la sauvegarde de récupération.

Références : [redirection des applications empaquetées](https://learn.microsoft.com/en-us/windows/msix/desktop/desktop-to-uwp-behind-the-scenes), [contexte du processus parent](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-updateprocthreadattribute).
