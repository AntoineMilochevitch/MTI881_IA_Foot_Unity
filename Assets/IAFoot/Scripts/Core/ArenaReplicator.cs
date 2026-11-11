using System.Collections.Generic;
using UnityEngine;

namespace IAFoot
{
    /// <summary>
    /// Duplique le prefab de l'arène N fois au lancement, en grille, pour accélérer l'entraînement.
    ///
    /// Chaque copie est un match indépendant (son MatchManager, son ballon, ses joueurs, ses épisodes).
    /// Tous les FootAgent de la scène s'inscrivent auprès du même TrainingBridge : les transitions des N
    /// arènes remplissent donc le même batch envoyé à Python, et chaque modèle reçu est installé en une fois
    /// sur les agents de toutes les arènes (ils lisent la politique partagée de leur comportement).
    /// </summary>
    [DefaultExecutionOrder(-100)]
    public class ArenaReplicator : MonoBehaviour
    {
        [Header("Source")]
        [Tooltip("Prefab de l'arène (menu IA Foot > Entraînement parallèle > Enregistrer l'arène en prefab).")]
        [SerializeField] GameObject arenaPrefab;
        [Tooltip("Arène déjà présente dans la scène : elle compte comme la n°0 et sert d'origine à la grille. " +
                 "Vide = toutes les arènes sont créées à partir de la position de cet objet.")]
        [SerializeField] MatchManager sceneArena;

        [Header("Duplication")]
        [Tooltip("Nombre total d'arènes qui jouent en parallèle (arène de la scène comprise).")]
        [SerializeField, Min(1)] int arenaCount = 32;
        [Tooltip("Nombre de colonnes de la grille (0 = automatique, grille la plus carrée possible).")]
        [SerializeField, Min(0)] int columns;
        [Tooltip("Calcule l'espacement à partir de l'emprise des colliders et des rendus de l'arène n°0.")]
        [SerializeField] bool autoSpacing = true;
        [Tooltip("Espace libre ajouté entre deux arènes en espacement automatique (unités monde).")]
        [SerializeField, Min(0f)] float margin = 2f;
        [Tooltip("Distance entre deux arènes voisines (X, Z) quand l'espacement automatique est désactivé.")]
        [SerializeField] Vector2 spacing = new Vector2(8f, 6f);

        [Header("Copies")]
        [Tooltip("Affiche les copies. Masquées par défaut : la charge GPU reste celle d'une seule arène, sans changer la physique ni l'entraînement.")]
        [SerializeField] bool renderCopies;
        [Tooltip("Garde le tableau de score des copies. Sinon seul celui de l'arène n°0 s'affiche (ils se superposent).")]
        [SerializeField] bool showCopiesUI;
        [Tooltip("Décale la graine des apparitions aléatoires de chaque copie, pour que les arènes ne jouent pas toutes " +
                 "les mêmes placements quand une graine positive est fixée.")]
        [SerializeField] bool offsetSpawnSeeds = true;

        readonly List<MatchManager> arenas = new List<MatchManager>();

        /// <summary>Toutes les arènes actives, la n°0 en premier.</summary>
        public IReadOnlyList<MatchManager> Arenas => arenas;

        /// <summary>Configuration par code (outils éditeur, tests), avant l'Awake.</summary>
        public void Configure(GameObject prefab, MatchManager existingArena, int count)
        {
            arenaPrefab = prefab;
            sceneArena = existingArena;
            arenaCount = Mathf.Max(1, count);
        }

        public GameObject ArenaPrefab => arenaPrefab;
        public MatchManager SceneArena => sceneArena;

        public static int AutoColumns(int count) => Mathf.Max(1, Mathf.CeilToInt(Mathf.Sqrt(count)));

        /// <summary>Décalage de l'arène `index` dans la grille, dans le repère de l'arène n°0.</summary>
        public static Vector3 GridOffset(int index, int columnCount, Vector2 step) =>
            new Vector3(index % columnCount * step.x, 0f, index / columnCount * step.y);

        void Awake()
        {
            GameObject source = arenaPrefab ? arenaPrefab : sceneArena ? sceneArena.gameObject : null;
            if (!source)
            {
                Debug.LogError("[IA Foot] ArenaReplicator : aucun prefab d'arène ni arène de scène à dupliquer.", this);
                return;
            }
            if (!source.GetComponent<MatchManager>())
            {
                Debug.LogError($"[IA Foot] ArenaReplicator : '{source.name}' n'a pas de MatchManager à sa racine.", this);
                return;
            }
            if (!arenaPrefab)
                Debug.LogWarning("[IA Foot] ArenaReplicator : pas de prefab, l'arène de la scène est clonée telle quelle.", this);

            Transform origin = sceneArena ? sceneArena.transform : transform;
            int columnCount = columns > 0 ? columns : AutoColumns(arenaCount);

            if (sceneArena)
            {
                sceneArena.name = ArenaName(0);
                arenas.Add(sceneArena);
            }
            else
                arenas.Add(CreateArena(source, 0, origin.position, origin.rotation));

            // L'arène n°0 existe : on mesure son emprise réelle pour espacer les suivantes.
            Vector2 step = autoSpacing ? MeasureStep(arenas[0].transform) : spacing;
            for (int i = 1; i < arenaCount; i++)
            {
                Vector3 position = origin.position + origin.rotation * GridOffset(i, columnCount, step);
                MatchManager match = CreateArena(source, i, position, origin.rotation);
                ConfigureCopy(match.gameObject);
                arenas.Add(match);
            }

            if (arenaCount > 1)
                Debug.Log($"[IA Foot] {arenas.Count} arènes en parallèle (grille de {columnCount} colonne(s), " +
                          $"pas {step.x:0.##} x {step.y:0.##}).", this);
        }

        static string ArenaName(int index) => $"Arena_{index:00}";

        MatchManager CreateArena(GameObject source, int index, Vector3 position, Quaternion rotation)
        {
            GameObject copy = Instantiate(source, position, rotation, transform);
            copy.name = ArenaName(index);
            var match = copy.GetComponent<MatchManager>();
            // Graine lue au premier placement (Start du MatchManager) : la modifier ici suffit.
            if (offsetSpawnSeeds && index > 0 && match.randomSpawns.seed > 0)
                match.randomSpawns.seed += index * 7919;
            return match;
        }

        void ConfigureCopy(GameObject copy)
        {
            if (!renderCopies)
            {
                foreach (Renderer r in copy.GetComponentsInChildren<Renderer>(true))
                    r.enabled = false;
            }
            if (!showCopiesUI)
            {
                foreach (Canvas canvas in copy.GetComponentsInChildren<Canvas>(true))
                    canvas.enabled = false;
            }
        }

        /// <summary>Emprise X/Z de l'arène + marge.</summary>
        Vector2 MeasureStep(Transform arena)
        {
            Bounds bounds = MeasureLocalBounds(arena);
            var size = new Vector2(bounds.size.x, bounds.size.z);
            if (size.x < 0.01f || size.y < 0.01f)
                return spacing;
            return size + Vector2.one * margin;
        }

        /// <summary>Boîte englobante des colliders et rendus actifs, dans le repère local de la racine.</summary>
        public static Bounds MeasureLocalBounds(Transform root)
        {
            bool any = false;
            var bounds = new Bounds();
            void Add(Bounds world)
            {
                // Les 8 coins de la boîte monde, ramenés dans le repère de l'arène.
                Vector3 c = world.center, e = world.extents;
                for (int i = 0; i < 8; i++)
                {
                    var sign = new Vector3(i % 2 * 2 - 1, i / 2 % 2 * 2 - 1, i / 4 * 2 - 1);
                    Vector3 local = root.InverseTransformPoint(c + Vector3.Scale(e, sign));
                    if (!any)
                    {
                        bounds = new Bounds(local, Vector3.zero);
                        any = true;
                    }
                    else
                        bounds.Encapsulate(local);
                }
            }

            foreach (Renderer r in root.GetComponentsInChildren<Renderer>())
            {
                if (r is MeshRenderer || r is SkinnedMeshRenderer)
                    Add(r.bounds);
            }
            foreach (Collider c in root.GetComponentsInChildren<Collider>())
            {
                if (c.enabled && !c.isTrigger)
                    Add(c.bounds);
            }
            return bounds;
        }
    }
}
