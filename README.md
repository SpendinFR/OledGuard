# OledGuardSimple

Projet entièrement neuf. Il ne copie, n'importe et ne modifie aucun fichier de l'ancien OledGuard.

## Comportement unique

- La nouvelle fenêtre active est entièrement visible pendant 3 secondes.
- Capture toutes les 20 ms.
- Les pixels modifiés sont regroupés uniquement lorsqu'ils sont réellement connectés.
- Une petite zone détectée reste visible 3 secondes puis s'assombrit.
- Une zone encore active aux étapes 1 s, 3 s et 5 s est validée et reste visible 30 secondes après sa dernière activité.
- Le rectangle d'une zone s'agrandit immédiatement et se resserre toutes les 500 ms sur l'activité réelle.
- Le curseur n'a aucune traînée.
- Le petit trou du curseur suit sa position actuelle.
- Le composant animé connecté près du curseur est révélé en 20 ms.
- Un élément statique sous le curseur est estimé localement par couleur et contours, sans dessiner une trace derrière le curseur.
- La barre des tâches est exclue de l'overlay et reste au-dessus.

## Raccourcis

- `Ctrl + Alt + O` : activer/désactiver le masque.
- `Ctrl + Alt + Q` : quitter.

## Construire

Depuis PowerShell dans ce dossier :

```powershell
dotnet publish .\OledGuardSimple.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

Le fichier final se trouve dans :

```text
bin\Release
et8.0-windows\win-x64\publish\OledGuardSimple.exe
```
