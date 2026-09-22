# Signature de distribution

## État vérifié le 22 septembre 2026

L’application et l’installateur 0.9.1 sont **non signés** (`NotSigned`). Le poste de développement ne possède pas de certificat de signature de code utilisateur valide avec clé privée. Les checksums SHA-256 contrôlent l’intégrité des paquets ; ils ne constituent pas une signature d’éditeur.

## Piste recommandée à étudier

[SignPath Foundation](https://signpath.org/) propose une signature gratuite pour des projets open source acceptés. Codex Tracker est public, sous licence MIT et déjà distribué, mais ces caractéristiques ne garantissent pas son admission : la fondation examine aussi la réputation du projet, l’origine vérifiable des binaires et ses autres [conditions](https://signpath.org/terms.html).

Avant de déposer une [candidature](https://signpath.org/apply.html), le propriétaire doit valider les conditions, l’authentification multifacteur, les rôles de revue et d’approbation, ainsi que la politique de signature et de confidentialité. Aucun compte, abonnement ou achat n’a été créé et aucune candidature n’a été envoyée pour cette préparation.

## Intégration après acceptation ou fourniture d’un certificat

1. Construire l’application depuis le commit validé en CI et signer son exécutable.
2. Fabriquer le ZIP et son SHA-256 à partir de cet exécutable signé.
3. Construire l’installateur à partir des mêmes fichiers, puis signer l’installateur.
4. Vérifier les deux signatures Authenticode, l’éditeur et les empreintes **après** signature ; tester ces paquets exacts avant publication.
5. Maintenir les clés privées hors du dépôt et des artefacts. Publier la politique correspondant au service réellement activé.

Les [options officielles Microsoft](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/code-signing-options) permettent de comparer les autres modes de distribution. La signature ne doit pas être revendiquée dans le README ou les Releases avant qu’elle soit effectivement vérifiée.
