# MTI881 – IA Foot (Unity)

Petit jeu de football en 3D, conçu pour entraîner des agents par apprentissage par renforcement.
L'entraînement passe par une chaîne maison (Unity + back-end Python / PyTorch) à la place de ML-Agents.

- **Unity** 6000.3.10f1 (URP, Input System)
- **Modèles 3D** : Lightning Poly – *Football Essentials 3D*

## Lancer le jeu
1. Ouvrir le projet avec Unity 6000.3.10f1.
2. Ouvrir la scène `Assets/Lightning Poly/Football Essentials 3D/Demo Scene/Demo Scene.unity`.
3. La scène est enregistrée en **mode entraînement** : les deux joueurs sont des agents IA.
   Pour jouer vous-même les **Bleus** contre un bot : menu **IA Foot > Entraînement > Revenir au mode jeu**, puis Play.

Pour monter une nouvelle scène : menu **IA Foot > Créer une nouvelle scène de match**,
ou **IA Foot > Configurer le match dans la scène ouverte** dans une scène qui contient déjà les prefabs du pack.

## Contrôles
| Action | Clavier | Manette |
|---|---|---|
| Déplacement | ZQSD (WASD) | Stick gauche |
| Tir (maintenir = plus fort) | Espace | A / Gâchette droite |
| Tacle | Maj gauche | B |
| Saut | E | Y |

## Fonctionnalités
- **Joueurs** : déplacement dans toutes les directions avec rotation interpolée, tir chargé
  (intensité de 0 à 1 selon le temps d'appui), tacle qui étourdit l'adversaire, saut, animations procédurales.
- **Ballon** : traînée de l'air, effet Magnus, résistance au roulement, anti-blocage contre les murs.
- **Buts** : validés quand le ballon a entièrement franchi la ligne, puis animation de la cage.
- **Match** : score et chrono affichés à l'écran, coup d'envoi avec le ballon au centre et les joueurs de chaque côté.
  Règles modulaires : score limite, temps limite avec but en or, ballon sorti.
- **Pensé pour l'IA** : chaque joueur est piloté par un « cerveau » interchangeable (humain, bot scripté ou agent ML).
  L'arène est duplicable pour l'entraînement parallèle, gère le 2v2 et fournit un repère d'équipe symétrique pour le self-play.

## Entraînement
Unity enregistre les parties et les envoie par batchs à un back-end Python, qui renvoie des modèles
(réseaux de neurones, modèles linéaires, arbres de décision...) installés à chaud sur les agents.

1. Dans Unity : menu **IA Foot > Entraînement > Préparer la scène**.
2. Dans `python/` : `pip install numpy`, puis `python train.py`.
3. Play.

Pour l'instant, le back-end affiche les données reçues et envoie un modèle à poids aléatoires :
l'entraînement PyTorch reste à écrire.

## Documentation
- Code Unity et mise en place : [`Assets/IAFoot/README.md`](Assets/IAFoot/README.md)
- Back-end Python, protocole et formats de modèles : [`python/README.md`](python/README.md)
