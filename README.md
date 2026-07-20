# OledGuard 1.2 — moteur dégradé restauré

Cette version revient au moteur visuel qui fonctionnait le mieux pendant les premiers essais.

## Comportement

- L’écran entier est analysé par petites cellules de **16 × 16 pixels**.
- Une cellule immobile pendant **30 secondes** s’assombrit progressivement jusqu’au noir.
- Les grandes zones réellement statiques, comme le fond d’écran autour d’une fenêtre ou une barre immobile, s’éteignent naturellement et de façon cohérente.
- Un changement réel réaffiche immédiatement la zone concernée.
- La souris ouvre un chemin rond et fluide composé de petits pixels en dégradé, conservé visible pendant **30 secondes**.
- Un changement limité à un seul échantillon est ignoré afin qu’un caret, un petit point clignotant ou du bruit de rendu ne crée plus de tache lumineuse.
- Il n’y a plus de logique de fenêtre active, de rectangles grossiers, de balayage artificiel ni de gros halos.

## Optimisation

Pour un écran 4K, la carte de masque fait environ 240 × 135 pixels et la capture d’analyse environ 720 × 405 pixels. Aucune image 4K n’est conservée dans l’historique.

## Commandes

- `Ctrl + Alt + O` : activer ou désactiver.
- `Ctrl + Alt + R` : révéler tout pendant 10 secondes.
- Double-clic sur l’icône : activer ou désactiver.
- Clic droit sur l’icône : délai, paramètres et fermeture.

Les réglages sont sauvegardés dans `%LOCALAPPDATA%\OledGuard\settings.json`. Le schéma 12 remet automatiquement les valeurs adaptées à ce moteur.
