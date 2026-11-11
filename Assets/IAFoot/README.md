# IA Foot : gameplay

## Mise en place
- **Option A (décor du pack)** : dupliquer `Lightning Poly/.../Demo Scene`, l'ouvrir, puis menu **IA Foot > Configurer le match dans la scène ouverte**.
- **Option B (scène minimale)** : menu **IA Foot > Créer une nouvelle scène de match** (terrain, buts, murs invisibles, caméra). Elle est sauvegardée dans `Assets/IAFoot/Scenes/`.

Ensuite, vérifier la taille des zones de but (boîtes colorées dans la vue Scene, objet `GoalTrigger`) et lancer Play.

## Positions de départ aléatoires

Dans `Arena > Match Manager`, ouvrir `Random Spawns` sous **Apparitions aléatoires**.
Le tirage est activé par défaut, pour les joueurs actifs et le ballon, quel que soit leur contrôleur
(humain, bot ou modèle). Il a lieu au début du match et à chaque nouvelle manche : but, timeout
d'un agent, sortie du ballon ou appel à `RestartRound`.

| Réglage | Valeur initiale | Effet |
|---|---|---|
| `Enabled` | coché | Décocher pour réutiliser exactement `BallSpawn` et les points des équipes. |
| `Area Center` | `(0, 0)` | Centre X/Z dans le repère local de l'arène. |
| `Area Size` | `(2.6, 1.4)` | Dimensions X/Z de la zone ; rectangle cyan dans la vue Scene. |
| `Minimum Distance` | `0.4` | Distance horizontale minimale entre les centres de tous les objets. |
| `Clearance` | `0.05` | Marge supplémentaire entre leurs empreintes et au bord de la zone. |
| `Seed` | `0` | Nouvelle séquence à chaque lancement. Une graine positive reproduit la séquence des placements. |

Les positions sont tirées dans toute la zone, sans réserver une moitié à chaque équipe.
La zone initiale reste à l'intérieur du terrain de démonstration, à distance des cages.
Pour un autre terrain, ajuster son centre et ses dimensions dans l'Inspector.
Les hauteurs des points fixes sont conservées, les joueurs regardent vers leur but adverse,
et les vitesses et états de jeu sont réinitialisés comme lors d'un coup d'envoi habituel.

Le placement vérifie la présence d'un sol, l'absence de pénétration des colliders dans les obstacles
ou les zones de but, et l'espacement des objets. Il est calculé entièrement avant d'être appliqué,
avant les événements qui démarrent les épisodes. Si la zone est trop petite ou encombrée, les points
fixes sont conservés et un avertissement apparaît dans la Console. Il faut alors corriger les réglages.
Les joueurs inactifs au lancement sont ignorés, notamment dans une scène solo.

Le générateur aléatoire est propre aux apparitions : il ne change pas celui des politiques ou du bot.
Une graine fixe reproduit les placements avec la même scène, les mêmes objets actifs et les mêmes
réglages, mais ne rend pas les actions ni la physique du match entièrement déterministes.
Pour comparer des checkpoints, garder la même configuration d'apparitions et relancer Play entre
les évaluations avec la même graine positive. Les observations, récompenses et messages du pont
restent identiques ; aucun changement Python n'est nécessaire.

Vérifications : hors Play, menu **IA Foot > Tests > Vérifier les apparitions aléatoires**.
Elles couvrent le 2v2, le solo, les obstacles et buts, les seeds, les remises à zéro, l'ordre
des événements, le repère de l'arène et les configurations impossibles.
La vérification en ligne de commande ajoute un vrai passage en Play avec `FootAgent` et
`SimpleBotInput` : inscription des équipes avant `Start`, démarrage des épisodes et déplacement
des deux joueurs après une remise en jeu aléatoire puis fixe. Le pont ne se connecte pas à Python.
Exécuter cette commande dans une copie de test du projet : elle crée une scène temporaire.

```powershell
& 'C:\Program Files\Unity\Hub\Editor\6000.3.10f1\Editor\Unity.exe' -batchmode -nographics -projectPath . -executeMethod IAFoot.Editor.RandomSpawnChecks.RunPlayModeBatch -logFile random-spawn-checks.log
```

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
│   └── GoalTrigger (GoalZone)  but validé quand le ballon est entièrement dedans ET entré par l'ouverture
│                               (face verte dans la vue Scene ; côté, arrière et dessus refusés), puis animation
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
- **Entraînement parallèle** : voir ci-dessous (N arènes à partir d'un prefab, un seul `TrainingBridge`).

## Entraînement parallèle (N arènes)
L'arène est enregistrée en prefab, puis dupliquée N fois au lancement. Chaque copie est un match
indépendant, mais tous les agents parlent au même `TrainingBridge` :
- les transitions des N arènes remplissent **le même batch** (un batch de 1024 se remplit N fois plus vite) ;
- chaque modèle reçu de Python est installé **en une fois sur les agents de toutes les arènes**.

Rien ne change côté Python : les trajectoires restent séparées par `agent_id` / `episode_id`, uniques
dans toute la scène. Le message `hello` indique l'arène de chaque agent (`arena`) et `train.py` affiche
le nombre d'arènes connectées.

Mise en route (après **Préparer la scène**) :
1. **IA Foot > Entraînement parallèle > 1. Enregistrer l'arène en prefab** → `Assets/IAFoot/Prefabs/Arena.prefab`.
   L'arène de la scène devient une instance de ce prefab.
2. **IA Foot > Entraînement parallèle > 2. Dupliquer l'arène** → crée l'objet `ArenaReplicator`.
3. Régler `Arena Count` sur l'`ArenaReplicator`, puis Play.

| Réglage (`ArenaReplicator`) | Défaut | Effet |
|---|---|---|
| `Arena Prefab` / `Scene Arena` | remplis par le menu | prefab dupliqué ; arène de la scène (n°0, origine de la grille) |
| `Arena Count` | 32 | nombre total d'arènes, celle de la scène comprise (32 arènes = 64 agents) |
| `Columns` | 0 | colonnes de la grille (0 = la plus carrée possible) |
| `Auto Spacing` / `Margin` | coché / 2 | espacement = emprise de l'arène n°0 (colliders + rendus) + marge |
| `Spacing` | (8, 6) | pas X/Z utilisé si l'espacement automatique est décoché |
| `Render Copies` | décoché | copies invisibles : le GPU ne dessine que l'arène n°0 (physique et entraînement inchangés) |
| `Show Copies UI` | décoché | seul le tableau de score de l'arène n°0 est affiché |
| `Offset Spawn Seeds` | coché | avec une graine d'apparition positive, chaque copie reçoit une graine différente |

À savoir :
- **Les copies viennent du prefab.** *Préparer la scène* et *Revenir au mode jeu* appliquent automatiquement
  leurs changements au prefab. Après toute autre modification de l'arène dans la scène, utiliser
  **Entraînement parallèle > Appliquer les modifications de l'arène au prefab** (ou *Overrides > Apply All*) ;
  sinon un avertissement apparaît au lancement de Play.
- Les copies sont créées dans l'`Awake` de l'`ArenaReplicator` et nommées `Arena_00`, `Arena_01`...
- `python train.py` accélère la simulation x10 par défaut (`--time-scale 1` pour regarder en temps réel).
  Le coût CPU augmente avec N : surveiller les FPS, et masquer les copies si le rendu devient limitant.

Vérification (Play réel en batchmode, dans une copie de test du projet ; pour tester aussi les vrais batchs
et modèles, définir `IAFOOT_CHECK_PYTHON=1` et lancer avant `python train.py --resend-every 2`) :
```powershell
& 'C:\Program Files\Unity\Hub\Editor\6000.3.10f1\Editor\Unity.exe' -batchmode -nographics -projectPath . -executeMethod IAFoot.Editor.ArenaReplicatorChecks.RunPlayModeBatch -logFile arena-checks.log
```
