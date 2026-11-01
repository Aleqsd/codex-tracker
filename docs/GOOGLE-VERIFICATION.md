# Validation Google du client OAuth publié

Le client « Codex Tracker » (projet Google Cloud `codex-tracker-513408`) demande des portées sensibles : `calendar.events.owned` (depuis 0.9.36, la plus étroite permettant d’écrire dans l’agenda principal) et, pour les alertes e-mail, `gmail.send`. Tant que Google ne les a pas validées, l’écran « Google n’a pas validé cette application » s’affiche (Paramètres avancés → Accéder à Codex Tracker), et l’application est limitée à 100 utilisateurs.

## Déjà en place

- Domaine `zqsdev.com` validé dans Search Console (enregistrement TXT dans le DNS Cloudflare de zqsdev.com, à ne pas supprimer).
- Page d’accueil https://codex-tracker.zqsdev.com/ et politique de confidentialité https://codex-tracker.zqsdev.com/privacy/ (utilisation limitée des données Google), même domaine autorisé `zqsdev.com`.
- Marque OAuth : nom, adresse d’assistance, accueil, confidentialité, domaine autorisé.
- **Data Access** : `openid`, `userinfo.email`, `calendar.events.owned`, `gmail.send` déclarés, justification enregistrée (texte ci-dessous).

## Reste à faire

État au 01/11/2026.

1. **Logo** 120 × 120 px (icône de l’application), facultatif mais conseillé.
2. **Vidéo de démonstration** non répertoriée sur YouTube (2–4 min, en anglais ou sous-titrée) :
   - montrer la barre d’adresse de la page de consentement avec le `client_id` du client publié ;
   - Réglages → Google Agenda → **Connecter Google Agenda** → consentement (cocher agenda et envoi d’e-mails) ;
   - le reset en réserve ajouté, puis l’événement ouvert dans Google Agenda ;
   - **Envoyer un e-mail de test** et l’e-mail reçu dans Gmail ;
   - **Déconnecter** pour montrer la révocation.
   Utiliser des données fictives quand c’est possible (`--demo` ne contacte pas Google : la vidéo doit montrer une vraie connexion).
3. **Justifications** (déjà saisies, à reprendre si Google demande des précisions) :
   - `calendar.events.owned` : « Codex Tracker is a Windows desktop app that tracks Codex usage limits. When the user connects Google Calendar, it creates one event in the user’s primary calendar per banked Codex reset credit, at its expiry time and with reminders, and deletes that event when the credit is used. It never reads or changes other events. We use calendar.events.owned instead of calendar.events because the app only writes to the user’s own primary calendar; read-only scopes cannot create events. »
   - `gmail.send` : « Optional email alerts: the day before a reset credit expires, the app sends one reminder email from the user’s Gmail address to that same address. It never reads, lists or deletes messages. »
4. Coller le lien YouTube dans **Data Access**, puis **Verification Center → Préparer la validation**, puis répondre aux e-mails de Google (délai annoncé : quelques jours à quelques semaines).

Après validation, l’avertissement disparaît pour tous les utilisateurs, sans nouvelle version de l’application.
