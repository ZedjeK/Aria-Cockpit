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

Version de correction du diagnostic de **Mon PC**, après les retours des premiers testeurs :

- **HDR** : un écran à gamme de couleurs étendue (gestion automatique des couleurs des portables, écrans large gamut) n'est plus annoncé comme HDR
- **Fausses alertes graves supprimées** : microcode Intel sur les i5 sans K (puce Alder Lake), écran « branché sur la carte mère » en Bureau à distance, écran virtuel ou dock USB, Resizable BAR conseillé sur des plateformes qui ne le permettent pas
- **Problèmes enfin vus** : un SSD usé au-delà de 100 % n'apparaît plus « en bonne santé »
- **Plus juste** : vitesse de la RAM (DDR4 rapportée en MHz, barrettes sans profil XMP), disque « presque plein » sur les grands disques, PCIe des cartes x8 et de la GT 1030, température des SSD selon le seuil du disque, écran intégré des portables, mémoire soudée, G-SYNC, adaptateurs CPL

Rappel de la 0.19.0 : origine aux valeurs d'usine, contrôle de l'overclock de NVIDIA App, Performance revu avec l'avis d'Aria, retour à Équilibre en un clic, journal des réglages.

## Contenu

- **Mon PC** : diagnostic matériel, télémétrie GPU en direct, analyse de performance en jeu, guide BIOS par marque, fiche PC en HTML
- **Réglages** : Windows, NVIDIA (NVAPI officiel), confidentialité, programmes au démarrage, applications préinstallées
- **Réglage GPU** : limite de puissance, plafond et décalages de fréquence (NVML officiel), assistant guidé Équilibre → Économie ou Performance, validation de 15 minutes, garde-fou anti-plantage
- **Banc d'essai Aria** : scène 3D Direct3D 12 en 1080p, 1440p ou 4K, préréglages Faible à Ultra, FPS mesurés, images de contrôle vérifiées (une erreur de calcul trahit un réglage instable) ; l'assistant s'en sert pour un verdict automatique
- **Mises à jour** : Windows Update, applications (winget), pilote NVIDIA, BIOS, et vérification des nouvelles versions d'Aria Cockpit
- **Nettoyage**, **Restauration** et **Rapports**

Détails dans le [README]({REPO_URL}#readme).
