using System;
using System.Collections.Generic;
using UnityEngine;

namespace IAFoot
{
    public enum MatchState
    {
        Idle,
        /// <summary>Joueurs et ballon replacés, joueurs gelés quelques instants.</summary>
        Kickoff,
        Playing,
        /// <summary>Un but vient d'être marqué : célébration puis nouvelle manche.</summary>
        GoalScored,
        MatchOver,
    }

    [Serializable]
    public class TeamSetup
    {
        public TeamId id;
        public string displayName = "Équipe";
        public Color color = Color.white;

        [Tooltip("Joueurs de l'équipe (1 en 1v1, 2 en 2v2...). Chacun peut être piloté par un humain, un bot ou un agent ML.")]
        public List<PlayerController> players = new List<PlayerController>();

        [Tooltip("Points d'apparition, dans l'ordre des joueurs. Les points en trop sont ignorés.")]
        public List<Transform> spawnPoints = new List<Transform>();
    }

    /// <summary>
    /// Orchestre une arène : score, manches, coups d'envoi, règles et événements.
    /// Pas de singleton : on peut dupliquer l'objet "Arena" plusieurs fois dans une scène pour entraîner
    /// des agents ML-Agents en parallèle. Toute la logique temporelle tourne en FixedUpdate pour rester
    /// cohérente avec la physique et avec Time.timeScale.
    /// </summary>
    [DefaultExecutionOrder(-50)]
    public class MatchManager : MonoBehaviour
    {
        [Header("Références")]
        public SoccerBall ball;
        public Transform ballSpawn;
        public List<GoalZone> goals = new List<GoalZone>();
        public List<TeamSetup> teams = new List<TeamSetup>();

        [Header("Déroulement")]
        public bool startOnPlay = true;
        [Tooltip("Durée pendant laquelle les joueurs sont gelés au coup d'envoi.")]
        [Min(0f)] public float kickoffFreezeDuration = 1f;
        [Tooltip("Durée de la célébration après un but avant la nouvelle manche (0 pour l'entraînement).")]
        [Min(0f)] public float goalCelebrationDuration = 2.5f;
        [Min(0f)] public float matchOverDuration = 4f;
        [Tooltip("Relance automatiquement un match après la fin du précédent.")]
        public bool autoRestartMatch = true;

        [Header("Règles")]
        public List<MatchRule> rules = new List<MatchRule>();

        public event Action MatchStarted;
        /// <summary>Joueurs et ballon viennent d'être replacés (idéal pour un EndEpisode / OnEpisodeBegin).</summary>
        public event Action RoundStarted;
        /// <summary>Fin du gel du coup d'envoi.</summary>
        public event Action PlayStarted;
        /// <summary>Équipe qui marque.</summary>
        public event Action<TeamId> GoalScored;
        /// <summary>Équipe gagnante, ou null en cas de match nul.</summary>
        public event Action<TeamId?> MatchEnded;

        public MatchState State { get; private set; } = MatchState.Idle;
        /// <summary>Temps de jeu effectif écoulé depuis le début du match.</summary>
        public float MatchTime { get; private set; }
        /// <summary>Temps de jeu effectif écoulé depuis le début de la manche.</summary>
        public float RoundTime { get; private set; }
        public float StateTimeRemaining => stateTimer;
        public bool IsGoldenGoal { get; set; }
        public TeamId? Winner { get; private set; }
        public TeamId? LastScoringTeam { get; private set; }
        public SoccerBall Ball => ball;
        public IReadOnlyList<PlayerController> AllPlayers => allPlayers;

        readonly int[] scores = new int[TeamIdExtensions.Count];
        readonly List<PlayerController> allPlayers = new List<PlayerController>();
        float stateTimer;

        void Awake()
        {
            RegisterPlayers();
            foreach (GoalZone goal in goals)
            {
                if (goal)
                    goal.BallEntered += OnBallEnteredGoal;
            }
        }

        void OnDestroy()
        {
            foreach (GoalZone goal in goals)
            {
                if (goal)
                    goal.BallEntered -= OnBallEnteredGoal;
            }
        }

        void Start()
        {
            if (startOnPlay)
                StartMatch();
        }

        void FixedUpdate()
        {
            float dt = Time.fixedDeltaTime;
            switch (State)
            {
                case MatchState.Kickoff:
                    stateTimer -= dt;
                    if (stateTimer <= 0f)
                        BeginPlay();
                    break;

                case MatchState.Playing:
                    MatchTime += dt;
                    RoundTime += dt;
                    foreach (MatchRule rule in rules)
                    {
                        if (rule)
                            rule.OnPlayingTick(this, dt);
                        if (State != MatchState.Playing)
                            break;
                    }
                    break;

                case MatchState.GoalScored:
                    stateTimer -= dt;
                    if (stateTimer <= 0f)
                        StartRound();
                    break;

                case MatchState.MatchOver:
                    if (!autoRestartMatch)
                        break;
                    stateTimer -= dt;
                    if (stateTimer <= 0f)
                        StartMatch();
                    break;
            }
        }

        // ------------------------------------------------------------------ Contrôle du match

        /// <summary>(Re)démarre un match complet : scores à zéro puis coup d'envoi.</summary>
        public void StartMatch()
        {
            Array.Clear(scores, 0, scores.Length);
            MatchTime = 0f;
            IsGoldenGoal = false;
            Winner = null;
            LastScoringTeam = null;

            foreach (MatchRule rule in rules)
            {
                if (rule)
                    rule.OnMatchStart(this);
            }

            MatchStarted?.Invoke();
            StartRound();
        }

        /// <summary>Replace le ballon et les joueurs sans toucher au score (ex : ballon sorti, reset d'épisode).</summary>
        public void RestartRound() => StartRound();

        /// <summary>Termine le match. winner = null pour un match nul.</summary>
        public void EndMatch(TeamId? winner)
        {
            if (State == MatchState.MatchOver)
                return;

            Winner = winner;
            State = MatchState.MatchOver;
            stateTimer = matchOverDuration;
            SetPlayersFrozen(true);
            MatchEnded?.Invoke(winner);
        }

        void StartRound()
        {
            RoundTime = 0f;
            ResetBall();
            PlacePlayers();
            SetPlayersFrozen(true);
            State = MatchState.Kickoff;
            stateTimer = kickoffFreezeDuration;

            foreach (MatchRule rule in rules)
            {
                if (rule)
                    rule.OnRoundStart(this);
            }

            RoundStarted?.Invoke();

            if (kickoffFreezeDuration <= 0f)
                BeginPlay();
        }

        void BeginPlay()
        {
            State = MatchState.Playing;
            SetPlayersFrozen(false);
            PlayStarted?.Invoke();
        }

        void OnBallEnteredGoal(GoalZone goal, SoccerBall enteringBall)
        {
            if (State != MatchState.Playing || enteringBall != ball)
                return;

            TeamId scorer = goal.DefendingTeam.Opponent();
            scores[(int)scorer]++;
            LastScoringTeam = scorer;
            State = MatchState.GoalScored;
            stateTimer = goalCelebrationDuration;

            goal.PlayCelebration();
            GoalScored?.Invoke(scorer);

            foreach (MatchRule rule in rules)
            {
                if (rule)
                    rule.OnGoalScored(this, scorer);
                if (State == MatchState.MatchOver)
                    break;
            }
        }

        // ------------------------------------------------------------------ Placement

        void RegisterPlayers()
        {
            allPlayers.Clear();
            foreach (TeamSetup team in teams)
            {
                foreach (PlayerController player in team.players)
                {
                    if (!player)
                        continue;
                    player.Initialize(this, team.id);
                    allPlayers.Add(player);
                }
            }
        }

        void ResetBall()
        {
            if (!ball)
                return;
            Vector3 position = ballSpawn ? ballSpawn.position : transform.position + transform.up * 0.2f;
            ball.ResetBall(position);
        }

        void PlacePlayers()
        {
            foreach (TeamSetup team in teams)
            {
                Quaternion facing = GetAttackRotation(team.id);
                for (int i = 0; i < team.players.Count; i++)
                {
                    PlayerController player = team.players[i];
                    if (player)
                        player.ResetState(GetSpawnPosition(team, i), facing);
                }
            }
        }

        Vector3 GetSpawnPosition(TeamSetup team, int index)
        {
            if (index < team.spawnPoints.Count && team.spawnPoints[index])
                return team.spawnPoints[index].position;

            // Pas assez de points d'apparition : on aligne les joueurs dans leur propre moitié.
            float side = -team.id.AttackSign();
            return transform.TransformPoint(new Vector3(side * 0.8f, 0.05f, (index - 0.5f) * 0.4f));
        }

        void SetPlayersFrozen(bool frozen)
        {
            foreach (PlayerController player in allPlayers)
            {
                if (player)
                    player.SetFrozen(frozen);
            }
        }

        // ------------------------------------------------------------------ Requêtes

        public int GetScore(TeamId team) => scores[(int)team];

        public TeamSetup GetTeam(TeamId id) => teams.Find(t => t.id == id);

        public string GetTeamName(TeamId id) => GetTeam(id)?.displayName ?? id.ToString();

        public Color GetTeamColor(TeamId id)
        {
            TeamSetup team = GetTeam(id);
            return team != null ? team.color : id.DefaultColor();
        }

        public GoalZone GetGoalDefendedBy(TeamId team) => goals.Find(g => g && g.DefendingTeam == team);

        /// <summary>Textes fournis par les règles (chrono...), une ligne par règle.</summary>
        public string GetStatusText()
        {
            string text = null;
            foreach (MatchRule rule in rules)
            {
                string line = rule ? rule.GetStatusText(this) : null;
                if (!string.IsNullOrEmpty(line))
                    text = text == null ? line : text + "\n" + line;
            }
            return text ?? string.Empty;
        }

        // ------------------------------------------------------------------ Repère d'équipe (utile pour ML-Agents)
        // Dans le repère d'une équipe, +X pointe toujours vers le but adverse. Un même réseau de neurones
        // peut ainsi jouer des deux côtés du terrain (self-play) sans apprendre deux fois la même chose.

        public Quaternion GetAttackRotation(TeamId team) =>
            transform.rotation * Quaternion.LookRotation(Vector3.right * team.AttackSign(), Vector3.up);

        public Vector3 ToTeamFrame(TeamId team, Vector3 worldPosition) =>
            MirrorForTeam(team, transform.InverseTransformPoint(worldPosition));

        public Vector3 DirectionToTeamFrame(TeamId team, Vector3 worldDirection) =>
            MirrorForTeam(team, transform.InverseTransformDirection(worldDirection));

        public Vector3 DirectionFromTeamFrame(TeamId team, Vector3 teamDirection) =>
            transform.TransformDirection(MirrorForTeam(team, teamDirection));

        static Vector3 MirrorForTeam(TeamId team, Vector3 v)
        {
            // Rotation de 180° autour de Y (et non un miroir) pour garder un repère direct.
            return team.AttackSign() > 0f ? v : new Vector3(-v.x, v.y, -v.z);
        }

        void OnDrawGizmos()
        {
            foreach (TeamSetup team in teams)
            {
                Gizmos.color = team.color;
                foreach (Transform spawn in team.spawnPoints)
                {
                    if (spawn)
                        Gizmos.DrawWireSphere(spawn.position, 0.08f);
                }
            }

            if (ballSpawn)
            {
                Gizmos.color = Color.white;
                Gizmos.DrawWireSphere(ballSpawn.position, 0.07f);
            }
        }
    }
}
