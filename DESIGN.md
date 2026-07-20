# Conception du moteur de stabilité

## Trois temporalités

Chaque cellule est comparée à la capture précédente, à une référence moyenne et à une référence longue. Un mouvement court réinitialise immédiatement son âge. Une cellule ne devient candidate à l'assombrissement que lorsque les trois comparaisons sont stables.

## Nettoyage symétrique

La carte binaire passe par un filtre de majorité 3 × 3. Avec le réglage 6/9 :

- au moins six voisins statiques rendent la cellule statique ;
- au plus trois voisins statiques rendent la cellule active ;
- les cas intermédiaires conservent leur état.

Ensuite, les petits composants statiques sont retirés et les petits trous actifs entièrement entourés sont comblés. Le traitement fonctionne donc dans les deux sens.

## Régions uniformes

Une région statique nettoyée reçoit une seule opacité cible. Tous ses blocs utilisent le même fondu temporel. La luminosité moyenne de la région est contrôlée avant assombrissement afin de ne pas recouvrir inutilement une zone déjà noire.

## Empreinte

La résolution de travail dépend du nombre de cellules, pas de la résolution native. À 4K et 64 px par cellule, la grille contient environ 2 040 cellules et chaque référence d'analyse environ 128 Kio.


## Persistance spatiale 3.1

La taille de bloc et le nombre de sous-zones sont découplés. Chaque sous-zone garde son propre compteur temporel. Le masque nettoyé est ensuite regroupé en composantes connexes dont l’opacité est unique et persistante par recouvrement spatial entre deux analyses.
