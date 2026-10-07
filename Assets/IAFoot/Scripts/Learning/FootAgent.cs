using System;
using UnityEngine;

namespace IAFoot.Learning
{
    /// <summary>
    /// Cerveau "IA" d'un joueur : à chaque décision il observe le terrain, demande une action à la politique
    /// de son comportement (fournie par Python via le <see cref="TrainingBridge"/>), l'applique au
    /// <see cref="PlayerController"/> et enregistre la transition (observation, action, récompense, suite).
    ///
    /// Un épisode = une manche : il commence au coup d'envoi et se termine sur un but ("terminated"),
    /// ou est coupé par la limite de pas, une remise en jeu ou la fin du match ("truncated").
    /// Tant qu'aucun modèle n'a été reçu, l'agent explore au hasard (modèle version 0).
    /// </summary>
    [DefaultExecutionOrder(-10)]
    [RequireComponent(typeof(PlayerController))]
    public class FootAgent : MonoBehaviour
    {
        [Serializable]
        public class RewardSettings
        {
            public float goalScored = 1f;
            public float goalConceded = -1f;
            [Tooltip("Par seconde, multiplié par la vitesse du ballon vers le but adverse (normalisée).")]
            public float ballTowardGoalPerSecond = 0.1f;
            [Tooltip("À chaque contact ou frappe du ballon par ce joueur.")]
            public float ballTouch = 0.02f;
            [Tooltip("Pénalité par seconde, multipliée par la distance au ballon (normalisée). 0 = désactivé.")]
            public float distanceToBallPerSecond = 0f;
            public float tackleHit = 0f;
            [Tooltip("Ajouté chaque seconde (négatif = pousse à marquer vite).")]
            public float timePerSecond = -0.005f;
        }

        [Header("Comportement")]
        [Tooltip("Les agents qui portent le même nom partagent le même modèle et le même enregistrement.")]
        [SerializeField] string behaviorName = "Foot";
        [Tooltip("Une décision tous les N pas de physique (5 = 10 décisions par seconde à 50 Hz).")]
        [SerializeField, Min(1)] int decisionPeriod = 5;
        [Tooltip("Nombre maximal de décisions par épisode avant remise en jeu (0 = illimité).")]
        [SerializeField, Min(0)] int maxEpisodeSteps = 600;
        [Tooltip("Envoie les transitions de cet agent au back-end. À décocher pour un adversaire qui ne s'entraîne pas.")]
        [SerializeField] bool recordExperience = true;
        [Tooltip("Graine du générateur aléatoire (0 = différente à chaque lancement).")]
        [SerializeField] int seed;

        [Header("Observations")]
        [Tooltip("Nombre d'emplacements de coéquipiers dans le vecteur d'observations.")]
        [SerializeField, Min(0)] int maxTeammates = 1;
        [Tooltip("Nombre d'emplacements d'adversaires dans le vecteur d'observations.")]
        [SerializeField, Min(0)] int maxOpponents = 2;
        [Tooltip("Les positions sont divisées par cette valeur (≈ demi-longueur du terrain).")]
        [SerializeField, Min(0.01f)] float positionScale = 2.5f;
        [Tooltip("Les vitesses sont divisées par cette valeur (≈ vitesse maximale du ballon).")]
        [SerializeField, Min(0.01f)] float velocityScale = 6f;

        [Header("Récompenses")]
        [SerializeField] RewardSettings rewards = new RewardSettings();

        public int AgentId { get; private set; } = -1;
        public string TeamName => player ? player.Team.ToString() : string.Empty;
        public bool EpisodeActive => episodeActive;
        public float EpisodeReturn => episodeReturn;
        public int ModelVersion => channel != null ? channel.Policy.Version : 0;

        PlayerController player;
        MatchManager match;
        TrainingBridge bridge;
        BehaviorChannel channel;
        System.Random random;

        float[] observation;
        float[] lastObservation;
        float[] action;
        float[] lastAction;
        float lastLogProbability;
        int lastActionIndex;
        int lastModelVersion;
        bool hasPrevious;

        bool episodeActive;
        int episodeId;
        int step;
        int physicsSteps;
        float pendingReward;
        float episodeReturn;
        SoccerBall subscribedBall;

        void Awake() => player = GetComponent<PlayerController>();

        void OnEnable()
        {
            // Inscription à la première activation seulement (Awake tourne même sur un composant désactivé).
            if (channel != null)
                return;

            bridge = TrainingBridge.Find();
            if (!bridge)
            {
                Debug.LogError("[IA Foot] FootAgent a besoin d'un TrainingBridge dans la scène " +
                               "(menu IA Foot > Entraînement > Préparer la scène).", this);
                enabled = false;
                return;
            }

            int observationSize = FootObservation.Size(maxTeammates, maxOpponents);
            AgentId = bridge.RegisterAgent(this, behaviorName, observationSize,
                FootObservation.Names(maxTeammates, maxOpponents), FootActions.Spec, decisionPeriod, out channel);
            if (channel == null)
            {
                enabled = false;
                return;
            }

            observation = new float[observationSize];
            lastObservation = new float[observationSize];
            action = new float[FootActions.Spec.Count];
            lastAction = new float[FootActions.Spec.Count];
            random = seed != 0 ? new System.Random(seed + AgentId) : new System.Random();
        }

        void Start()
        {
            match = player.Match;
            if (!match)
            {
                Debug.LogError("[IA Foot] Ce joueur n'est inscrit dans aucune équipe d'un MatchManager.", this);
                enabled = false;
                return;
            }

            match.PlayStarted += OnPlayStarted;
            match.GoalScored += OnGoalScored;
            match.RoundEnding += OnRoundEnding;
            match.MatchEnded += OnMatchEnded;
            player.TackleHit += OnTackleHit;
            if (match.Ball)
            {
                subscribedBall = match.Ball;
                subscribedBall.Touched += OnBallTouched;
            }

            // Le match peut déjà être lancé (le MatchManager démarre avant les agents).
            if (match.State == MatchState.Playing)
                BeginEpisode();
        }

        void OnDisable()
        {
            // À la fermeture de la scène, le match peut déjà être détruit : on ne clôt proprement que s'il existe encore.
            if (episodeActive && match && bridge)
                EndEpisode(false, "disabled");
            episodeActive = false;
        }

        void OnDestroy()
        {
            if (match)
            {
                match.PlayStarted -= OnPlayStarted;
                match.GoalScored -= OnGoalScored;
                match.RoundEnding -= OnRoundEnding;
                match.MatchEnded -= OnMatchEnded;
            }
            if (player)
                player.TackleHit -= OnTackleHit;
            if (subscribedBall)
                subscribedBall.Touched -= OnBallTouched;
            if (bridge)
                bridge.UnregisterAgent(this, channel);
        }

        // ------------------------------------------------------------------ Événements du match

        void OnPlayStarted()
        {
            if (isActiveAndEnabled)
                BeginEpisode();
        }

        void OnGoalScored(TeamId scorer)
        {
            if (!episodeActive)
                return;
            bool mine = scorer == player.Team;
            pendingReward += mine ? rewards.goalScored : rewards.goalConceded;
            EndEpisode(true, mine ? "goal_scored" : "goal_conceded");
        }

        void OnRoundEnding() => EndEpisode(false, "interrupted");

        void OnMatchEnded(TeamId? winner) => EndEpisode(false, "match_over");

        void OnBallTouched(SoccerBall ball, PlayerController toucher)
        {
            if (episodeActive && toucher == player)
                pendingReward += rewards.ballTouch;
        }

        void OnTackleHit(PlayerController tackler, PlayerController victim)
        {
            if (episodeActive)
                pendingReward += rewards.tackleHit;
        }

        // ------------------------------------------------------------------ Boucle de décision

        void FixedUpdate()
        {
            if (!episodeActive)
                return;

            AccumulateShaping(Time.fixedDeltaTime);
            if (physicsSteps++ % decisionPeriod == 0)
                Decide();
        }

        void Decide()
        {
            WriteObservation();

            if (hasPrevious)
            {
                bool timedOut = maxEpisodeSteps > 0 && step + 1 >= maxEpisodeSteps;
                RecordTransition(false, timedOut);
                if (timedOut)
                {
                    FinishEpisode("timeout");
                    match.RestartRound(); // coupe aussi l'épisode des autres agents de l'arène (RoundEnding)
                    return;
                }
            }

            Policy policy = channel.Policy;
            policy.Act(observation, random, action, out lastLogProbability, out lastActionIndex);
            FootActions.Apply(action, player, match);

            Array.Copy(observation, lastObservation, observation.Length);
            Array.Copy(action, lastAction, action.Length);
            lastModelVersion = policy.Version;
            hasPrevious = true;
        }

        void AccumulateShaping(float dt)
        {
            pendingReward += rewards.timePerSecond * dt;

            SoccerBall ball = match.Ball;
            if (!ball)
                return;

            if (rewards.ballTowardGoalPerSecond != 0f)
            {
                float towardGoal = match.DirectionToTeamFrame(player.Team, ball.Velocity).x / velocityScale;
                pendingReward += rewards.ballTowardGoalPerSecond * towardGoal * dt;
            }

            if (rewards.distanceToBallPerSecond != 0f)
            {
                float distance = Vector3.Distance(player.Position, ball.Position) / positionScale;
                pendingReward -= rewards.distanceToBallPerSecond * distance * dt;
            }
        }

        // ------------------------------------------------------------------ Épisodes

        void BeginEpisode()
        {
            episodeActive = true;
            hasPrevious = false;
            episodeId = bridge.NextEpisodeId();
            step = 0;
            physicsSteps = 0;
            pendingReward = 0f;
            episodeReturn = 0f;
        }

        void EndEpisode(bool terminated, string outcome)
        {
            if (!episodeActive)
                return;

            if (hasPrevious)
            {
                WriteObservation();
                RecordTransition(terminated, !terminated);
            }
            FinishEpisode(outcome);
        }

        void FinishEpisode(string outcome)
        {
            if (recordExperience)
                bridge.ReportEpisode(channel, AgentId, episodeId, episodeReturn, step, outcome);
            episodeActive = false;
            hasPrevious = false;
            player.SetMoveInput(Vector3.zero);
        }

        /// <summary>Clôt la transition ouverte à la décision précédente ; `observation` contient l'observation suivante.</summary>
        void RecordTransition(bool terminated, bool truncated)
        {
            if (recordExperience)
            {
                bridge.Record(channel, AgentId, episodeId, step, lastObservation, lastAction, lastActionIndex,
                    lastLogProbability, pendingReward, observation, terminated, truncated, lastModelVersion);
            }

            episodeReturn += pendingReward;
            pendingReward = 0f;
            step++;
        }

        void WriteObservation() =>
            FootObservation.Write(observation, player, match, maxTeammates, maxOpponents, positionScale, velocityScale);
    }
}
