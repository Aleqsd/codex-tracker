# Signature de distribution

## État vérifié le 23 septembre 2026

L’application et l’installateur 0.9.2 sont **non signés** (`NotSigned`). Aucun certificat de distribution n’est configuré pour le projet. Les checksums SHA-256 contrôlent l’intégrité des paquets ; ils ne constituent pas une signature d’éditeur.

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

## Intégration préparée

La [politique publique](CODE-SIGNING-POLICY.md) indique les rôles prévus et les limites. Le workflow **Prepare signed Windows release** est manuel, limité à `main` et ne publie aucune Release. Son mode répétition ne contacte pas SignPath et ne revendique aucune signature.

### Configuration après acceptation

1. Activer la MFA du propriétaire sur GitHub et SignPath. Configurer le connecteur demandé par SignPath en limitant ses droits à ce dépôt.
2. Lier le projet au Trusted Build System GitHub.com. Restreindre la politique de release à `Aleqsd/codex-tracker`, branche `main`, workflow `.github/workflows/signed-release.yml` et runners GitHub. Désactiver les autres origines et uploads manuels pour cette politique.
3. Importer `installer/signpath/application.xml` et `installer/signpath/installer.xml`. Les faire valider dans l’éditeur SignPath avec un artefact de CI. Elles ciblent uniquement les deux EXE du projet, pas les composants tiers. Le désinstallateur Inno n’est pas signé séparément par cette intégration.
4. Configurer le certificat de production, l’horodatage et l’approbation manuelle. Créer un jeton limité à la soumission, sans droit d’approuver ni d’administrer.
5. Ajouter le secret Actions `SIGNPATH_API_TOKEN`, sans le transmettre dans le dépôt, une issue ou le contexte d’un assistant. Ajouter ces variables dans les réglages Actions du dépôt :

| Variable | Valeur |
|---|---|
| `SIGNPATH_ORGANIZATION_ID` | Identifiant fourni par SignPath |
| `SIGNPATH_PROJECT_SLUG` | Projet configuré |
| `SIGNPATH_SIGNING_POLICY_SLUG` | Politique avec approbation manuelle |
| `SIGNPATH_APP_CONFIGURATION` | Configuration de l’application |
| `SIGNPATH_INSTALLER_CONFIGURATION` | Configuration de l’installateur |
| `SIGNPATH_CERTIFICATE_SUBJECT` | Sujet exact du certificat de production, confirmé depuis SignPath |
| `SIGNPATH_ENABLED` | `true` uniquement après ces vérifications |

Le sujet attendu se copie depuis le certificat public de SignPath ; ne pas le déduire du fichier à vérifier. Les métadonnées envoyées viennent des binaires compilés après contrôle du produit et de la version ; les espaces de remplissage des ressources Inno sont conservés dans les paramètres. Les actions du workflow sont verrouillées par SHA.

### Produire un candidat

Dans GitHub Actions, ouvrir **Prepare signed Windows release**, choisir `main`, puis lancer :

- **sign décoché** : répétition sans secret ni appel SignPath. L’artefact s’appelle `release-candidate-UNSIGNED-REHEARSAL` et son rapport indique `signed: false`.
- **sign coché** : refuse de démarrer sans configuration complète ; aucun repli vers une livraison non signée. Approuver les deux demandes successives dans SignPath, chacune dans les 30 minutes. Une expiration ne relance pas automatiquement le workflow.

Le parcours compile et teste, signe l’application, construit puis signe l’installateur, vérifie Authenticode/éditeur/horodatage, recrée le ZIP et ses empreintes, puis teste le MCP et le cycle d’installation sur un profil CI vierge. Il produit trois fichiers : **Setup.exe**, ZIP portable et checksum ZIP. Le rapport séparé contient aussi l’empreinte du Setup pour WinGet. Aucun fichier de Release existant n’est remplacé automatiquement.

Après une réussite réelle, publier les trois fichiers du candidat validé sans les reconstruire. Inclure un lien **Code signing policy** dans les notes courtes, vérifier les empreintes publiques et tester la mise à niveau depuis la version distribuée. Le binaire signé exact doit passer ces contrôles avant toute annonce.

### Vérifications locales

```powershell
./scripts/test-release-signing.ps1
```

Les contrôles couvrent fichier non signé, mauvais éditeur, signature non fiable, horodatage absent, produit/version incorrects, conservation de l’exécutable dans le ZIP et refus d’écraser un paquet existant. L’API Windows est éprouvée sur un fichier non signé et, si disponible, le `dotnet.exe` signé du SDK. Les réponses attendues de SignPath sont simulées : elles ne prouvent ni une admission ni une signature réelle par le service. Aucun certificat n’est créé ou ajouté aux magasins de confiance.

Références : [intégration GitHub officielle](https://docs.signpath.io/trusted-build-systems/github), [configurations des artefacts](https://docs.signpath.io/artifact-configuration/reference).
