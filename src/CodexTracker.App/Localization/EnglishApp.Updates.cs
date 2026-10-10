namespace CodexTracker.App;

internal static partial class EnglishApp
{
    internal static readonly (string French, string English)[] Updates =
    [
        // Release checks
        ("Versions vérifiées auprès de GitHub.", "Releases checked on GitHub."),
        ("Dernier résultat conservé en cache.", "Last result kept in cache."),
        ("GitHub limite temporairement les vérifications.", "GitHub is temporarily limiting checks."),
        ("GitHub limite temporairement les vérifications. Réessayez plus tard.", "GitHub is temporarily limiting checks. Try again later."),
        ("GitHub n’a pas pu être vérifié. Le dernier résultat reste disponible.", "GitHub could not be checked. The last result is still available."),
        ("GitHub a renvoyé un cache qui n’est pas disponible.", "GitHub returned a cached response that is not available."),
        ("GitHub a renvoyé une redirection de téléchargement non autorisée.", "GitHub returned a download redirect that is not allowed."),
        ("La vérification sur GitHub est momentanément indisponible.", "Checking on GitHub is temporarily unavailable."),
        ("La réponse de GitHub est invalide.", "The GitHub response is invalid."),
        ("La liste des versions est trop longue pour être vérifiée entièrement.", "The release list is too long to be fully checked."),
        ("La page suivante des versions GitHub n’est pas autorisée.", "The next page of GitHub releases is not allowed."),
        ("La mise à jour est indisponible sur GitHub. Réessayez plus tard.", "The update is unavailable on GitHub. Try again later."),

        // Download and preparation
        ("Téléchargement de la version {0}…", "Downloading version {0}…"),
        ("Version {0} téléchargée et vérifiée.", "Version {0} downloaded and verified."),
        ("Version {0} prête. L’installation automatique a déjà été tentée ; vous pouvez réessayer.", "Version {0} ready. Automatic installation was already attempted; you can try again."),
        ("Téléchargement interrompu. Le suivi continue ; une nouvelle tentative sera effectuée plus tard.", "Download interrupted. Tracking continues; another attempt will be made later."),
        ("La mise à jour préparée est absente ou invalide. Relancez sa recherche pour la télécharger à nouveau.", "The prepared update is missing or invalid. Check for updates again to download it."),
        ("La source de mise à jour n’est pas autorisée.", "The update source is not allowed."),
        ("Le canal de mise à jour a changé.", "The update channel changed."),
        ("La taille de la mise à jour ne correspond pas à la publication.", "The update size does not match the release."),
        ("Le téléchargement de la mise à jour est incomplet.", "The update download is incomplete."),
        ("Le téléchargement est incomplet.", "The download is incomplete."),
        ("Le téléchargement comporte trop de redirections.", "The download has too many redirects."),
        ("La réponse de mise à jour est trop volumineuse.", "The update response is too large."),
        ("La mise à jour dépasse la taille autorisée.", "The update exceeds the allowed size."),
        ("Le fichier de vérification de la mise à jour est invalide.", "The update checksum file is invalid."),
        ("La vérification SHA-256 a échoué. La mise à jour n’a pas été installée.", "SHA-256 verification failed. The update was not installed."),
        ("L’archive contient trop de fichiers.", "The archive contains too many files."),
        ("L’archive de mise à jour contient un chemin non autorisé.", "The update archive contains a path that is not allowed."),
        ("Un fichier de mise à jour sortirait du dossier temporaire.", "An update file would extract outside the temporary folder."),
        ("L’archive de mise à jour est trop volumineuse.", "The update archive is too large."),
        ("L’archive ne contient pas l’application Windows attendue.", "The archive does not contain the expected Windows application."),
        ("Le fichier téléchargé n’est pas une application Windows.", "The downloaded file is not a Windows application."),
        ("Un lien de dossier empêche la mise à jour automatique. Utilisez l’installateur.", "A folder link prevents automatic updates. Use the installer."),

        // Installation
        ("La mise à jour n’est plus prête. Recherchez-la à nouveau.", "The update is no longer ready. Check for it again."),
        ("L’application en cours est introuvable.", "The running application cannot be found."),
        ("Utilisez la version installée ou portable pour effectuer la mise à jour.", "Use the installed or portable version to update."),
        ("La mise à jour automatique nécessite la version autonome installée ou portable.", "Automatic updates require the installed or portable self-contained version."),
        ("Le fichier téléchargé a changé depuis la préparation.", "The downloaded file changed since it was prepared."),
        ("Le programme de mise à jour n’a pas démarré.", "The updater did not start."),
        ("Le programme de mise à jour n’a pas pu préparer l’installation.", "The updater could not prepare the installation."),
        ("Mise à jour incomplète.", "Incomplete update."),
        ("Le manifeste de mise à jour est invalide.", "The update manifest is invalid."),
        ("Fichier de mise à jour invalide.", "Invalid update file."),
        ("Le processus à mettre à jour n’a pas pu être vérifié.", "The process to update could not be verified."),
        ("Le dossier de préparation de la mise à jour est invalide.", "The update staging folder is invalid."),
        ("Chemin d’installation invalide.", "Invalid installation path."),
        ("L’application a changé depuis la préparation. Réessayez la mise à jour.", "The app changed since the update was prepared. Try the update again."),
        ("L’application reste verrouillée, éventuellement par un client MCP. Reconnectez ce client puis réessayez ; aucun fichier n’a été remplacé.", "The app is still locked, possibly by an MCP client. Reconnect that client, then try again; no file was replaced."),
        ("La nouvelle version n’a pas démarré correctement.", "The new version did not start correctly."),
        ("La nouvelle application reste ouverte. Quittez Codex Tracker avant de restaurer la copie de secours.", "The new app is still open. Quit Codex Tracker before restoring the backup copy."),

        // Outcome
        ("La mise à jour a été installée.", "The update was installed."),
        ("La mise à jour a été annulée car Codex Tracker n’a pas quitté à temps.", "The update was cancelled because Codex Tracker did not exit in time."),
        ("La mise à jour a échoué ; la version précédente a été restaurée et relancée.", "The update failed; the previous version was restored and restarted."),
        ("La version précédente a été restaurée. Ouvrez-la manuellement ici : {0}", "The previous version was restored. Open it manually here: {0}"),
        ("La mise à jour n’a pas pu être installée ; la version précédente a été relancée.", "The update could not be installed; the previous version was restarted."),
        ("La mise à jour n’a pas pu être installée. Ouvrez Codex Tracker manuellement ici : {0}", "The update could not be installed. Open Codex Tracker manually here: {0}"),
        ("La restauration a échoué. La copie de secours est conservée ici : {0}", "Restoring failed. The backup copy is kept here: {0}"),
        ("La restauration automatique a échoué. Vérifiez l’application ici : {0}", "Automatic restore failed. Check the app here: {0}"),
    ];
}
