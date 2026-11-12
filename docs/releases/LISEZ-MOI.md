# Notes de version

Un fichier par version : `vX.Y.Z.md`, du nom exact du tag. Le workflow de release s'en sert comme texte de la
release, et refuse un tag dont le fichier manque.

## Écrire les notes d'une version

1. Copier `MODELE.md` en `vX.Y.Z.md`.
2. Remplacer chaque `{VERSION}` par le numéro, et chaque `{REPO_URL}` par l'adresse du dépôt. Le workflow refuse un
   fichier où il en reste un.
3. Garder le titre `## Nouveautés de la X.Y.Z`, avec une puce par nouveauté. La fenêtre de mise à jour de l'appli
   affiche les cinq premières puces de cette section, sans mise en forme, coupées à 160 caractères : la première
   phrase de chaque puce doit se suffire.
4. Ne pas ajouter les empreintes à la main : GitHub affiche celle de chaque fichier, et chaque zip est publié avec
   son fichier `.sha256`.

## Publier

Dans l'ordre :

1. Une PR qui passe la version dans `Directory.Build.props`, ajoute `docs/releases/vX.Y.Z.md` et met à jour
   `CHANGELOG.md`. Elle est fusionnée dans `main`.
2. Le tag, poussé depuis `main` à jour :

   ```powershell
   git switch main; git pull origin main
   git tag -a vX.Y.Z -m "Version X.Y.Z"; git push origin vX.Y.Z
   ```

3. Le workflow teste, fabrique les deux zips, atteste leur provenance et crée la release **en brouillon**.
4. Relire le brouillon, puis le publier. C'est la publication qui prévient les utilisateurs : l'appli lit la dernière
   release publiée, jamais un brouillon.

Deux verrous à connaître avant de pousser un tag ou de publier :

- Les tags `v*` ne se suppriment pas et ne se déplacent pas. Un tag poussé par erreur, ou dont le workflow a échoué,
  est perdu : on corrige par une PR, puis on sort le numéro suivant.
- Les releases sont immuables. Une fois publiée, une release ne change plus de fichiers ; seuls son titre et ses notes
  restent modifiables. Tout se relit donc sur le brouillon.

Vérifier la provenance d'un zip téléchargé :

```powershell
gh attestation verify Aria-Cockpit-X.Y.Z-autonome.zip --repo ZedjeK/Aria-Cockpit
```

## Essai à blanc

`Actions → Release → Run workflow` fabrique les zips sans tag ni release. Sur son PC, `.\tools\empaqueter.ps1` fait
la même chose dans `.work\release\`.
