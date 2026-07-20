# OledGuard 3.2 — moteur de stabilité

Chaque sous-zone possède son propre âge de stabilité. Les comparaisons immédiate, courte, moyenne et longue servent uniquement à autoriser l'entrée dans l'état assombri.

Une fois assombrie, la sous-zone est verrouillée. Elle ne redevient visible qu'après plusieurs captures consécutives montrant un mouvement local suffisamment soutenu. Une rotation de référence ou une analyse momentanément incohérente ne peut donc plus supprimer tout le masque.

Le nettoyage spatial est symétrique : les petits îlots sombres sont retirés et les petits trous clairs entièrement entourés sont comblés. Les composantes connexes partagent une opacité commune et conservent l'opacité maximale de leurs anciennes composantes lors d'une fusion ou d'une séparation.

Le rendu utilise un unique `WriteableBitmap` basse résolution avec interpolation linéaire. Les anciennes lignes provenaient du dessin de milliers de rectangles adjacents.

La souris n'altère aucun compteur. Elle applique uniquement une réduction temporaire de l'alpha autour de sa position actuelle au moment du rendu. Dès qu'elle se déplace, l'ancienne position retrouve immédiatement l'alpha réel de la zone.
