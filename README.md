# OledGuard 3.2

OledGuard assombrit les grandes zones claires qui restent réellement statiques sur un écran OLED.

## Correctifs 3.2

- Une zone déjà assombrie reste verrouillée jusqu'à ce qu'un mouvement réel soit confirmé sur plusieurs captures. Les mises à jour des références temporelles ne peuvent donc plus rallumer tout l'écran d'un coup.
- Les références courte, moyenne et longue sont automatiquement plafonnées par le délai choisi. Un test réglé à 5 secondes commence réellement près de 5 secondes au lieu d'attendre une référence longue de 60 secondes.
- Le masque est rendu comme une seule petite image alpha mise à l'échelle. Il n'y a plus de rectangles WPF adjacents ni de lignes entre cellules.
- La souris révèle uniquement sa position actuelle avec un petit dégradé. Aucune ancienne position n'est mémorisée et aucun chemin ne persiste.
- Le réglage 128 px / 8 sous-zones reste conservé et donne environ 16 px de précision réelle sur un écran 4K.

## Commandes

- `Ctrl + Alt + O` : activer ou désactiver.
- `Ctrl + Alt + R` : révéler temporairement tout l'écran.
- Clic droit sur l'icône OledGuard : paramètres et fermeture.

## Compilation

Chaque push sur `main` lance le workflow GitHub Actions **Build Windows executable**. Télécharger ensuite l'artifact `OledGuard-win-x64`.
