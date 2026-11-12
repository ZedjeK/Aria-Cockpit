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

- **« Appliquer » sur chaque profil et chaque courbe** (page Réglage GPU) : le bouton « Charger » ne posait rien, il remplissait les curseurs ou les points, et il fallait ensuite cliquer sur « Appliquer » ailleurs. Chaque profil GPU et chaque courbe de ventilation enregistrés ont maintenant leur bouton « Appliquer », qui pose directement après la confirmation habituelle ; « Modifier » les affiche sans rien poser, pour les retoucher
- **Repère « EN PLACE »** sur le profil que la carte a en ce moment et sur la courbe qui tourne ; le nom du profil s'affiche dans le bloc « En place », et le résultat de chaque clic sur la ligne cliquée
- **Incidents du pilote commentés** : une carte liste les incidents des 30 derniers jours, avec leurs lignes brutes du journal de Windows, et un champ « Ce que je faisais » pour chacun (jeu, IA locale, repos…). Le journal ne le dit pas, et c'est ce qui permet de relier un incident à un réglage
- **Rapport à coller dans une IA** : « Copier le rapport » met dans le presse-papiers un rapport en Markdown, avec la carte, le pilote, le réglage en place, les valeurs d'usine, le résultat d'Équilibre, les profils, l'historique des réglages et chaque incident accompagné du réglage en place à ce moment-là et de ton commentaire. « Enregistrer le rapport » l'écrit dans le dossier Rapports. Il ne contient ni le nom du PC, ni le tien, ni le numéro de la carte
- **Page Paramètres** (roue crantée, à droite de « Rapports ») : ce qui règle Aria elle-même. Démarrage avec Windows, ce que fait la croix de la fenêtre (ranger seulement si quelque chose est actif, toujours ranger, ou toujours arrêter), notification au rangement
- **Dossier des données** (page Paramètres) : son chemin, sa taille, le nombre de rapports et de sauvegardes, un bouton pour l'ouvrir, et des **durées de conservation réglables** (6 mois pour les rapports, 12 mois pour les sauvegardes, 10 sauvegardes toujours gardées, par défaut). Enregistrer ne supprime rien : Aria dit combien de fichiers les nouvelles durées feraient disparaître, et le ménage quotidien s'en charge ensuite. L'emplacement du dossier ne se change pas : seuls les administrateurs peuvent y écrire
- **L'onglet « Réglages » s'appelle « Optimisation »** : il règle ton PC (Windows, NVIDIA, confidentialité, démarrage, applications, batterie), pas l'application. Les conseils du diagnostic disent désormais « Optimisation → Démarrage » et « Optimisation → Batterie »
- Dans les incidents, les octets que le pilote joint à une erreur s'affichent en hexadécimal, et non plus « System.Byte[] »

Rappel de la 0.25.0 : icône dans la zone de notification (état du profil GPU relu sur la carte, pastilles), fermeture qui range Aria tant qu'elle a quelque chose d'actif, courbes de ventilation et profils GPU depuis le menu de l'icône, démarrage avec Windows avec reprise de la courbe, mise à jour sans fermer l'ancienne version (à partir de la 0.25 : **ferme la 0.24 ou plus ancienne avant de lancer une version plus récente**), polices embarquées, licences jointes aux zips.

Rappel de la 0.24.0 : les tâches de démarrage lancent une copie d'Aria rangée dans `C:\Program Files\Aria Cockpit`, où seuls les administrateurs écrivent, et non plus l'exe dézippé (correctif de sécurité) ; détail de chaque mise à jour Windows (description, failles corrigées, lien KB), notes de version des applications, historique de Windows Update, rapport de mises à jour plus complet.

Rappel de la 0.23.0 : fonctions reprises de CTI-Cockpit (menu des catégories dans Réglages et Restauration, pare-feu et dernier correctif dans le diagnostic, vrai nom des programmes au démarrage, carte Batterie, une seule fenêtre à la fois, ménage automatique du dossier des données) et limite de charge de la batterie des portables (Dell et HP ; ASUS et Lenovo grand public en expérimental).

Rappel de la 0.22.0 : courbe de ventilation dans Réglage GPU (préréglages, courbe personnalisée, « Mes courbes », sécurités à 85 °C, retour au pilote à la fermeture) ; toutes les données dans ton profil, `C:\Users\<toi>\.Aria-Cockpit`, modifiables par les seuls administrateurs.

Rappel de la 0.21.0 : le préréglage Ultra RT et le contrôle en ray tracing de la validation ont été retirés (ils n'aidaient pas à trouver un meilleur réglage) ; le banc Ultra donne exactement la même image qu'avant.

Rappel de la 0.20.0 : le réglage GPU guidé ne garde un réglage que s'il tient aussi sous le calcul pur et la mémoire vidéo (contrôles de stabilité à chaque palier, après 17 130 résultats faux en une minute sur la tour de l'auteur avec un réglage pourtant stable au banc 3D). Équilibre fait tout le parcours (cœur, mémoire, validation de 15 minutes) ; mesures protégées (premier plan, mémoire vidéo occupée) ; banc d'essai seul, sans mesure en jeu ; Économie baisse aussi les watts ; profils GPU ; rapport de chaque passage.

Rappel de la 0.19.x : origine aux valeurs d'usine, contrôle de l'overclock de NVIDIA App, Performance avec l'avis d'Aria, retour à Équilibre en un clic, journal des réglages, diagnostic de Mon PC corrigé après les retours des premiers testeurs, conseil RAM pour 4 barrettes DDR5, tests automatiques.

## Contenu

- **Mon PC** : diagnostic matériel, télémétrie GPU en direct, analyse de performance en jeu, guide BIOS par marque, fiche PC en HTML
- **Optimisation** : Windows, NVIDIA (NVAPI officiel), confidentialité, programmes au démarrage, applications préinstallées
- **Réglage GPU** : limite de puissance, plafond et décalages de fréquence (NVML officiel), assistant guidé Équilibre → Économie ou Performance, contrôles de stabilité, courbe de ventilation, validation de 15 minutes, profils, rapport de fin de mode, garde-fou anti-plantage, incidents du pilote commentés et rapport à envoyer
- **Banc d'essai Aria** : scène 3D Direct3D 12 en 1080p, 1440p ou 4K, préréglages Faible à Ultra, FPS mesurés, images de contrôle vérifiées (une erreur de calcul trahit un réglage instable) ; l'assistant s'en sert pour un verdict automatique
- **Mises à jour** : Windows Update, applications (winget), pilote NVIDIA, BIOS, et vérification des nouvelles versions d'Aria Cockpit
- **Zone de notification** : icône d'état, courbes de ventilation et profils GPU depuis son menu, démarrage avec Windows
- **Paramètres** : démarrage avec Windows, comportement de la croix, dossier des données et durées de conservation
- **Nettoyage**, **Restauration** et **Rapports**

Détails dans le [README]({REPO_URL}#readme).
