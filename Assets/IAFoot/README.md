# IA Foot : gameplay

## Mise en place
- **Option A (décor du pack)** : dupliquer `Lightning Poly/.../Demo Scene`, l'ouvrir, puis menu **IA Foot > Configurer le match dans la scène ouverte**.
- **Option B (scène minimale)** : menu **IA Foot > Créer une nouvelle scène de match** (terrain, buts, murs invisibles, caméra). Elle est sauvegardée dans `Assets/IAFoot/Scenes/`.

Ensuite, vérifier la taille des zones de but (boîtes colorées dans la vue Scene, objet `GoalTrigger`) et lancer Play.

## Contrôles
| Action | Joueur 1 | Joueur 2 | Manette |
|---|---|---|---|
| Déplacement | ZQSD (WASD) | Flèches | Stick gauche |
| Tir (maintenir = plus fort) | Espace | Ctrl droit / Pavé 0 | A / Gâchette droite |
| Tacle | Maj gauche | Maj droit / Pavé 1 | B |
| Saut | E | Pavé 2 / Entrée du pavé | Y |

## Architecture
```
Arena (MatchManager)          score, manches, règles, événements ; pas de singleton, donc duplicable
├── Ball (SoccerBall)         traînée de l'air, effet Magnus, roulement
├── Goal_Blue / Goal_Red
│   └── GoalTrigger (GoalZone)  but validé quand le ballon est entièrement dedans, puis animation
├── Blue_Player_0 (PlayerController + PlayerVisuals + HumanPlayerInput)
├── Red_Player_0  (PlayerController + PlayerVisuals + SimpleBotInput)
├── SpawnPoints / BallSpawn
└── MatchUI (ScoreboardUI)
```

- **PlayerController** ne lit aucune entrée. Un "cerveau" le pilote via :
  `SetMoveInput(Vector3)`, `StartCharge()` / `ReleaseCharge()`, `Shoot(power01, curve)`, `Tackle()`, `Jump()`.
- **Cerveaux** : `HumanPlayerInput`, `SimpleBotInput` ou `FootAgent` (IA, voir plus bas). Un seul cerveau actif par joueur.
- **2v2** : ajouter des personnages dans `MatchManager > teams > players` ; les points `*_1` sont déjà créés.
- **Règles** : assets `MatchRule` dans `Assets/IAFoot/Settings` (score limite, temps limite + but en or,
  ballon sorti). Pour une nouvelle règle : hériter de `MatchRule`, créer l'asset, l'ajouter à `rules`.

## Entraînement (remplace ML-Agents)
Chaîne maison : Unity enregistre les parties et les envoie par batchs à un back-end Python, qui renvoie des
modèles installés à chaud sur les agents. Détails du protocole et des formats : [`python/README.md`](../../python/README.md).

Mise en route :
1. Menu **IA Foot > Entraînement > Préparer la scène** : chaque joueur reçoit un `FootAgent`, un objet
   `TrainingBridge` est créé, les temps morts entre manches sont mis à zéro.
2. Dans `python/` : `python train.py`.
3. Play. Pour rejouer soi-même : **IA Foot > Entraînement > Revenir au mode jeu**.

| Script | Rôle |
|---|---|
| `Learning/FootAgent` | cerveau IA d'un joueur : observe, demande une action à la politique, l'applique, calcule les récompenses, enregistre la transition |
| `Learning/TrainingBridge` | un par scène : regroupe les transitions, envoie un batch au seuil `Batch Size`, reçoit et installe les modèles |
| `Learning/FootSpaces` | définition des observations (45 valeurs, repère d'équipe) et des actions (7 valeurs) |
| `Learning/Core/` | C# pur sans dépendance Unity : protocole, modèles, décodeurs d'actions, buffer, client TCP |

À savoir :
- **Épisode** = une manche. Fin sur un but (`terminated`) ou coupure après `Max Episode Steps` décisions (`truncated`).
- **Sans modèle reçu**, les agents explorent au hasard (modèle version 0) et l'enregistrement fonctionne déjà.
- **Récompenses** réglables sur chaque `FootAgent` : but marqué / encaissé, ballon qui va vers le but adverse,
  contact avec le ballon, temps qui passe.
- **Comportement** (`Behavior Name`) : les agents de même nom partagent modèle et enregistrement. Donner
  deux noms différents aux deux équipes permet de les entraîner séparément.
- **Adversaire fixe** : décocher `Record Experience` sur un agent, ou lui laisser le `SimpleBotInput`.
- **Jouer sans Python** : cocher `Save Received Models` sur le `TrainingBridge`, puis glisser le fichier
  `.bytes` obtenu dans `Initial Models`.
- **Entraînement parallèle** : dupliquer l'objet `Arena` en l'éloignant des autres ; un seul `TrainingBridge` suffit.
