# OledGuard 0.5.0

OledGuard réduit l’exposition statique d’un écran OLED sous Windows en rendant noires les zones inutilisées et en laissant visibles les zones réellement actives.

## Changements de la version 0.5

- Les changements de contenu proches sont regroupés en rectangles cohérents.
- Les contours utilisent une distance presque carrée, plus proche d’un rectangle doux que d’un halo rond.
- Un petit clignotement isolé utilise un masque minimal et ne révèle plus une grande zone autour de lui.
- Un mouvement continu de souris forme un seul rectangle englobant le trajet.
- Tout le rectangle de souris reçoit la même expiration et retourne donc au noir ensemble.
- Le fondu temporel est proportionnel : le bloc conserve sa forme pendant qu’il s’assombrit au lieu de s’éroder par les bords.
- Le curseur immobile ne maintient qu’un très petit carré visible autour de sa position.

## Réglages conseillés

- délai après activité : **30 s** ;
- grille : **32 px** ;
- dégradé contenu : **72 px** ;
- marge trajet souris : **40 px** ;
- dégradé souris : **72 px** ;
- retour au noir : **5 s**.

## Commandes

- `Ctrl + Alt + O` : activer ou désactiver ;
- `Ctrl + Alt + R` : révéler tout pendant 10 secondes ;
- double-clic sur l’icône : activer ou désactiver ;
- clic droit sur l’icône : paramètres et fermeture.

## Compilation

Chaque push sur `main` lance le workflow **Build Windows executable**. Quand il est vert, télécharger l’artifact `OledGuard-win-x64`, le décompresser puis lancer `OledGuard.exe`.

OledGuard complète les protections de la dalle mais ne garantit pas l’absence totale de marquage.
