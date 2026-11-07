**Aria Cockpit** {VERSION} : le tableau de bord de ton PC de jeu. Analyse du matériel, télémétrie GPU en direct, réglages Windows, NVIDIA et confidentialité, réglage GPU guidé (Équilibre, Économie, Performance) avec son banc d'essai 3D, mises à jour, nettoyage, restauration et rapports.

**Rien n'est modifié sans ton accord** : chaque changement est proposé, sauvegardé et réversible depuis la page Restauration.

## Quel fichier choisir ?

| Fichier | Pour qui |
|---|---|
| `Aria-Cockpit-{VERSION}-autonome.zip` | Tout le monde : rien à installer |
| `Aria-Cockpit-{VERSION}-leger.zip` | PC qui ont déjà le [runtime .NET 10 Desktop](https://dotnet.microsoft.com/download/dotnet/10.0) |

Décompresser (garder `Aria-Bench.exe`, le banc d'essai, à côté), puis lancer `Aria-Cockpit.exe` (Windows 11, droits administrateur demandés).

## Premier lancement

L'exécutable n'est pas signé : Windows SmartScreen peut afficher « Windows a protégé votre ordinateur ». Clique sur « Informations complémentaires » puis « Exécuter quand même ». Le code source complet est dans ce dépôt.

## Nouveautés de la {VERSION}

- **Menu des catégories** dans Réglages et Restauration : Windows, NVIDIA, Confidentialité, Démarrage et Applications sont listés à gauche, toujours dans le même ordre, avec le nombre de lignes cochées ; chaque catégorie s'affiche seule, avec une phrase qui dit ce qu'elle contient
- **Sécurité dans le diagnostic** : pare-feu Windows sur les profils domaine, privé et public (un pare-feu d'un autre éditeur est reconnu), et date du dernier correctif Windows, avec une alerte au-delà de 45 jours
- **Programmes au démarrage** : un programme lancé par l'intermédiaire de Windows (rundll32) affiche son vrai nom et son éditeur, par exemple « Logitech Download Assistant » au lieu de « Processus hôte Windows (Rundll32) ». Les composants des pilotes (audio Realtek et Waves, magasin de pilotes de Windows) ne sont plus proposés à la désactivation. Sur Mon PC, le point « programmes au démarrage » distingue ceux que tu peux désactiver de ceux qui sont toujours gardés, et ne conseille de faire le tri qu'à partir de 5 programmes désactivables
- **Limite de charge de la batterie** (portables), nouvelle catégorie Batterie dans Réglages : une batterie qui reste à 100 % s'use bien plus vite. Aria la règle lui-même sur les **Dell** (arrêt à 80 %, reprise sous 50 %) et les **HP** (« Maximize My Battery Health ») par les interfaces officielles de leur BIOS, et, en **expérimental** (décoché), sur les **ASUS** (arrêt à 80 %, réappliqué à chaque ouverture de session) et les **Lenovo** Legion, LOQ, IdeaPad et Yoga (mode conservation). Sauvegardée et réversible comme les autres réglages. Ailleurs (ThinkPad, Acer, MSI, Surface…), le diagnostic dit où la régler
- **Carte Batterie** sur Mon PC (portables) : niveau de charge, secteur ou batterie, santé par rapport à la capacité d'origine, nombre de cycles, limite de charge. Sur batterie, elle rappelle que la carte graphique est bridée
- **Une seule fenêtre à la fois** : relancer Aria Cockpit ramène au premier plan la fenêtre déjà ouverte, au lieu d'en ouvrir une seconde qui se disputerait la courbe de ventilation et le réglage GPU avec la première
- **Ménage automatique du dossier des données**, une fois par jour au plus : rapports et fiches PC supprimés au-delà de 6 mois, sauvegardes au-delà de 12 mois (les 10 plus récentes sont toujours gardées), journaux archivés au-delà de 1 Mo. Seuls les fichiers créés par Aria sont concernés ; les réglages en place ne sont jamais touchés

La plupart de ces nouveautés sont reprises de CTI-Cockpit, la version professionnelle d'Aria Cockpit.

Rappel de la 0.22.0 : courbe de ventilation dans Réglage GPU (préréglages, courbe personnalisée, « Mes courbes », sécurités à 85 °C, retour au pilote à la fermeture) ; toutes les données dans ton profil, `C:\Users\<toi>\.Aria-Cockpit`, modifiables par les seuls administrateurs.

Rappel de la 0.21.0 : le préréglage Ultra RT et le contrôle en ray tracing de la validation ont été retirés (ils n'aidaient pas à trouver un meilleur réglage) ; le banc Ultra donne exactement la même image qu'avant.

Rappel de la 0.20.0 : le réglage GPU guidé ne garde un réglage que s'il tient aussi sous le calcul pur et la mémoire vidéo (contrôles de stabilité à chaque palier, après 17 130 résultats faux en une minute sur la tour de l'auteur avec un réglage pourtant stable au banc 3D). Équilibre fait tout le parcours (cœur, mémoire, validation de 15 minutes) ; mesures protégées (premier plan, mémoire vidéo occupée) ; banc d'essai seul, sans mesure en jeu ; Économie baisse aussi les watts ; profils GPU ; rapport de chaque passage.

Rappel de la 0.19.x : origine aux valeurs d'usine, contrôle de l'overclock de NVIDIA App, Performance avec l'avis d'Aria, retour à Équilibre en un clic, journal des réglages, diagnostic de Mon PC corrigé après les retours des premiers testeurs, conseil RAM pour 4 barrettes DDR5, tests automatiques.

## Contenu

- **Mon PC** : diagnostic matériel, télémétrie GPU en direct, analyse de performance en jeu, guide BIOS par marque, fiche PC en HTML
- **Réglages** : Windows, NVIDIA (NVAPI officiel), confidentialité, programmes au démarrage, applications préinstallées
- **Réglage GPU** : limite de puissance, plafond et décalages de fréquence (NVML officiel), assistant guidé Équilibre → Économie ou Performance, contrôles de stabilité, courbe de ventilation, validation de 15 minutes, profils, rapport de fin de mode, garde-fou anti-plantage
- **Banc d'essai Aria** : scène 3D Direct3D 12 en 1080p, 1440p ou 4K, préréglages Faible à Ultra, FPS mesurés, images de contrôle vérifiées (une erreur de calcul trahit un réglage instable) ; l'assistant s'en sert pour un verdict automatique
- **Mises à jour** : Windows Update, applications (winget), pilote NVIDIA, BIOS, et vérification des nouvelles versions d'Aria Cockpit
- **Nettoyage**, **Restauration** et **Rapports**

Détails dans le [README]({REPO_URL}#readme).
