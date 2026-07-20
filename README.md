# OledGuard 0.3.0

OledGuard réduit l’exposition prolongée des zones statiques sur un écran OLED sous Windows.

## Comportement

- L’écran est découpé en zones fines de **16 × 16 pixels** par défaut.
- Une zone inchangée pendant **30 secondes** devient progressivement noire.
- Une zone qui bouge redevient visible immédiatement et son compteur repart à zéro.
- La souris révèle une zone avec un fondu court ; après son passage, cette zone reste visible **30 secondes** par défaut.
- Le masque est rendu comme une petite carte alpha lissée, sans milliers de rectangles WPF.
- Les variations faibles doivent être confirmées afin d’éviter les petits trous causés par le bruit de rendu.

Le délai, la finesse du masque, le rayon de la souris, le maintien après souris et les fondus sont réglables depuis l’icône près de l’horloge.

## Commandes

- Double-clic sur l’icône : activer ou désactiver.
- `Ctrl + Alt + O` : activer ou désactiver globalement (`o` ou `O`, c’est identique).
- `Ctrl + Alt + R` : révéler tout l’écran pendant 10 secondes.
- Clic droit sur l’icône : délai, paramètres et fermeture.

## Optimisation

Pour un écran 4K avec le réglage par défaut :

- grille d’environ 240 × 135 zones ;
- capture d’analyse réduite à environ 720 × 405 pixels ;
- deux petits tampons réutilisés, sans historique vidéo ;
- analyse normale toutes les 1 000 ms ;
- analyse toutes les 250 ms lorsqu’une zone est masquée pour révéler rapidement un changement ;
- animation uniquement lorsque l’opacité évolue ;
- aucun modèle IA et aucune reconnaissance d’interface.

L’overlay plein écran peut utiliser quelques dizaines de Mio de mémoire GPU selon WPF, le pilote et la résolution. Les tampons d’analyse utilisent surtout quelques Mio de mémoire système.

## Compiler avec GitHub Actions

Chaque push sur `main` lance **Build Windows executable**. Quand le workflow est vert, télécharger l’artifact `OledGuard-win-x64`, le décompresser et lancer `OledGuard.exe`.

## Compiler localement

Installer le SDK .NET 8 x64 puis exécuter `build.cmd`. Le résultat est créé dans :

```text
dist\OledGuard\OledGuard.exe
```

## Réglages sauvegardés

```text
%LOCALAPPDATA%\OledGuard\settings.json
```

La version 0.3 migre automatiquement les anciens réglages afin de remplacer la grille grossière et le maintien souris trop court.

## Limites

OledGuard réduit le risque mais ne garantit pas l’absence de marquage. Conserver le déplacement de pixels, les cycles d’entretien de la dalle et l’extinction automatique de l’écran activés.
