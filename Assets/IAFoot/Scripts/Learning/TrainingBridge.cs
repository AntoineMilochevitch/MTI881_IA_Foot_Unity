using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace IAFoot.Learning
{
    /// <summary>
    /// Groupe d'agents qui partagent le même modèle et le même enregistrement (équivalent d'un "behavior" ML-Agents).
    /// </summary>
    public sealed class BehaviorChannel
    {
        public readonly string Name;
        public readonly int ObservationSize;
        public readonly List<string> ObservationNames;
        public readonly ActionSpec Actions;
        public readonly int DecisionPeriod;

        internal readonly List<FootAgent> Agents = new List<FootAgent>();
        internal ExperienceBuffer Buffer;
        internal int NextBatchId;
        internal int BatchesSent;
        internal int EpisodesFinished;
        internal readonly Queue<float> RecentReturns = new Queue<float>();

        internal BehaviorChannel(string name, int observationSize, List<string> observationNames, ActionSpec actions, int decisionPeriod)
        {
            Name = name;
            ObservationSize = observationSize;
            ObservationNames = observationNames;
            Actions = actions;
            DecisionPeriod = decisionPeriod;
            Policy = Policy.CreateRandom(actions);
        }

        /// <summary>Politique courante. Remplacée à chaud quand Python envoie un nouveau modèle.</summary>
        public Policy Policy { get; internal set; }

        /// <summary>Nombre de transitions en attente dans le batch en cours de remplissage.</summary>
        public int PendingTransitions => Buffer.Count;

        /// <summary>Récompense totale moyenne des derniers épisodes terminés (0 s'il n'y en a aucun).</summary>
        public float AverageRecentReturn
        {
            get
            {
                if (RecentReturns.Count == 0)
                    return 0f;
                float sum = 0f;
                foreach (float value in RecentReturns)
                    sum += value;
                return sum / RecentReturns.Count;
            }
        }
    }

    /// <summary>
    /// Pont entre Unity et le back-end Python (un seul par scène, partagé par toutes les arènes) :
    /// - regroupe les transitions des agents et envoie un message "batch" dès que le seuil est atteint ;
    /// - reçoit les modèles ("set_model") et les installe immédiatement sur les agents concernés ;
    /// - reçoit les réglages ("set_config") : vitesse de simulation, taille de batch.
    /// </summary>
    [DefaultExecutionOrder(-40)]
    public class TrainingBridge : MonoBehaviour
    {
        const int ProtocolVersion = 1;

        [Header("Connexion au back-end Python")]
        [SerializeField] string host = "127.0.0.1";
        [SerializeField] int port = 5005;
        [SerializeField] bool connectOnStart = true;
        [SerializeField, Min(0.1f)] float retrySeconds = 2f;

        [Header("Enregistrement")]
        [Tooltip("Nombre de transitions accumulées (tous agents confondus) qui déclenche l'envoi d'un batch.")]
        [SerializeField, Min(1)] int batchSize = 1024;
        [Tooltip("Joint l'observation suivante à chaque transition (double la taille des batchs, mais chaque transition se suffit à elle-même).")]
        [SerializeField] bool includeNextObservations = true;
        [Tooltip("Envoie le batch incomplet restant quand on quitte le mode Play.")]
        [SerializeField] bool flushOnExit = true;

        [Header("Simulation")]
        [Tooltip("Time.timeScale appliqué pendant l'entraînement (ex : 10 pour accélérer).")]
        [SerializeField, Range(0.1f, 50f)] float timeScale = 1f;

        [Header("Modèles")]
        [Tooltip("Modèles chargés au lancement (fichiers .bytes sauvegardés), pour jouer sans le back-end Python.")]
        [SerializeField] TextAsset[] initialModels;
        [Tooltip("Sauvegarde chaque modèle reçu dans Application.persistentDataPath/IAFootModels.")]
        [SerializeField] bool saveReceivedModels;

        [Header("Debug")]
        [Tooltip("Messages dans la console : batchs envoyés, modèles reçus, état périodique.")]
        [SerializeField] bool verboseLogs = true;
        [Tooltip("Intervalle (secondes réelles) entre deux messages d'état : remplissage du batch, récompense moyenne. 0 = désactivé.")]
        [SerializeField, Min(0f)] float statusLogInterval = 5f;
        [Tooltip("Intervalle minimal (secondes réelles) entre deux messages d'envoi de batch ou de réception de modèle. " +
                 "Les événements survenus entre-temps sont comptés et résumés dans le message suivant.")]
        [SerializeField, Min(0f)] float minEventLogGap = 2f;
        [Tooltip("Nombre d'épisodes récents utilisés pour la récompense moyenne.")]
        [SerializeField, Min(1)] int rewardWindow = 20;
        [Tooltip("Affiche l'état de l'entraînement en surimpression dans la vue Game (n'écrit rien dans la console).")]
        [SerializeField] bool showOverlay = true;

        readonly Dictionary<string, BehaviorChannel> channels = new Dictionary<string, BehaviorChannel>();
        readonly string sessionId = Guid.NewGuid().ToString("N").Substring(0, 8);
        TrainingClient client;
        int helloConnection = -1;
        bool helloDirty;
        int nextAgentId;
        int nextEpisodeId;
        int droppedBatches;
        float previousTimeScale = 1f;

        // Anti-flood : horodatages en temps réel (insensibles à Time.timeScale) et compteurs d'événements passés sous silence.
        float nextStatusTime;
        int lastStatusFingerprint = -1;
        float lastBatchLogTime = float.NegativeInfinity;
        int silentBatches;
        int silentTransitions;
        float lastModelLogTime = float.NegativeInfinity;
        int silentModels;
        GUIStyle overlayStyle;

        public bool IsConnected => client != null && client.IsConnected;
        public int BatchSize => batchSize;

        public static TrainingBridge Find() => FindFirstObjectByType<TrainingBridge>();

        void OnEnable()
        {
            previousTimeScale = Time.timeScale;
            Time.timeScale = timeScale;
        }

        void Start()
        {
            if (initialModels != null)
            {
                foreach (TextAsset asset in initialModels)
                {
                    if (!asset)
                        continue;
                    try
                    {
                        ApplyModel(Message.FromBytes(asset.bytes), false);
                    }
                    catch (Exception e)
                    {
                        Debug.LogError($"[IA Foot] Modèle initial '{asset.name}' illisible : {e.Message}", this);
                    }
                }
            }

            if (connectOnStart)
                Connect();
        }

        void OnDisable()
        {
            if (client != null)
            {
                if (flushOnExit && client.IsConnected)
                {
                    foreach (BehaviorChannel channel in channels.Values)
                        Flush(channel);
                }
                client.Dispose();
                client = null;
            }
            Time.timeScale = previousTimeScale;
        }

        void Update()
        {
            if (client == null)
                return;

            while (client.Logs.TryDequeue(out string log))
                Debug.Log($"[IA Foot] {log}", this);

            EnsureHello();
            while (client.Incoming.TryDequeue(out Message message))
                Handle(message);

            LogStatus();
        }

        // ------------------------------------------------------------------ Debug

        /// <summary>Une ligne d'état par comportement, au plus toutes les `statusLogInterval` secondes, et seulement si quelque chose a changé.</summary>
        void LogStatus()
        {
            if (!verboseLogs || statusLogInterval <= 0f || Time.unscaledTime < nextStatusTime)
                return;
            nextStatusTime = Time.unscaledTime + statusLogInterval;

            int fingerprint = IsConnected ? 1 : 0;
            foreach (BehaviorChannel channel in channels.Values)
                fingerprint = unchecked(fingerprint * 31 + channel.PendingTransitions + channel.EpisodesFinished * 7919 + channel.Policy.Version * 104729);
            if (fingerprint == lastStatusFingerprint)
                return;
            lastStatusFingerprint = fingerprint;

            foreach (BehaviorChannel channel in channels.Values)
                Debug.Log($"[IA Foot] État — {DescribeStatus(channel)}", this);
        }

        string DescribeStatus(BehaviorChannel channel)
        {
            int pending = channel.PendingTransitions;
            string reward = channel.RecentReturns.Count > 0
                ? $"récompense moyenne sur les {channel.RecentReturns.Count} derniers épisodes : {channel.AverageRecentReturn:+0.000;-0.000}"
                : "aucun épisode terminé";
            string model = channel.Policy.Version == 0 ? "aucun modèle (jeu aléatoire)" : $"modèle v{channel.Policy.Version}";
            return $"'{channel.Name}' : batch en cours {pending}/{batchSize} ({100f * pending / batchSize:0} %) | " +
                   $"{channel.BatchesSent} batch(s) envoyé(s) | {model} | {reward} | " +
                   (IsConnected ? "Python connecté" : "Python NON connecté");
        }

        void OnGUI()
        {
            if (!showOverlay || channels.Count == 0)
                return;

            overlayStyle ??= new GUIStyle(GUI.skin.box) { alignment = TextAnchor.UpperLeft, fontSize = 13, richText = false };
            var text = new System.Text.StringBuilder();
            foreach (BehaviorChannel channel in channels.Values)
            {
                if (text.Length > 0)
                    text.Append('\n');
                text.Append(DescribeStatus(channel).Replace(" | ", "\n   "));
            }

            var content = new GUIContent(text.ToString());
            Vector2 size = overlayStyle.CalcSize(content);
            GUI.Box(new Rect(10f, Screen.height - size.y - 10f, size.x + 12f, size.y + 4f), content, overlayStyle);
        }

        /// <summary>Message d'envoi de batch, limité à un par `minEventLogGap` : les envois intermédiaires sont résumés.</summary>
        void LogBatchSent(BehaviorChannel channel, int batchId, int count, int frameBytes)
        {
            if (!verboseLogs)
                return;

            if (Time.unscaledTime - lastBatchLogTime < minEventLogGap)
            {
                silentBatches++;
                silentTransitions += count;
                return;
            }

            string skipped = silentBatches > 0
                ? $" (+ {silentBatches} autre(s) batch(s), {silentTransitions} transitions, envoyé(s) depuis le dernier message)"
                : string.Empty;
            string reward = channel.RecentReturns.Count > 0
                ? $" Récompense moyenne sur les {channel.RecentReturns.Count} derniers épisodes : {channel.AverageRecentReturn:+0.000;-0.000}."
                : string.Empty;
            Debug.Log($"[IA Foot] Batch plein ({count}/{batchSize}) → batch n°{batchId} envoyé à Python : {count} transitions, " +
                      $"{FormatSize(frameBytes)} ('{channel.Name}').{skipped}{reward}", this);

            lastBatchLogTime = Time.unscaledTime;
            silentBatches = 0;
            silentTransitions = 0;
        }

        /// <summary>Message de réception de modèle, avec la même limitation que les envois de batch.</summary>
        void LogModelInstalled(BehaviorChannel channel, Policy policy, int parameterCount)
        {
            if (!verboseLogs)
                return;

            if (Time.unscaledTime - lastModelLogTime < minEventLogGap)
            {
                silentModels++;
                return;
            }

            string skipped = silentModels > 0 ? $" (+ {silentModels} autre(s) modèle(s) reçu(s) depuis le dernier message)" : string.Empty;
            Debug.Log($"[IA Foot] Nouveau modèle reçu de Python → {channel.Agents.Count} agent(s) de '{channel.Name}' mis à jour. " +
                      $"{policy.Describe()}, {parameterCount} paramètres.{skipped}", this);

            lastModelLogTime = Time.unscaledTime;
            silentModels = 0;
        }

        static string FormatSize(int bytes) =>
            bytes < 1024 ? $"{bytes} o" : bytes < 1024 * 1024 ? $"{bytes / 1024f:0.0} Ko" : $"{bytes / (1024f * 1024f):0.00} Mo";

        public void Connect()
        {
            if (client != null)
                return;
            client = new TrainingClient(host, port, retrySeconds);
            client.Start();
        }

        // ------------------------------------------------------------------ API pour les agents

        /// <summary>Inscrit un agent dans son groupe. Retourne son identifiant, ou -1 si ses specs ne collent pas au groupe.</summary>
        public int RegisterAgent(FootAgent agent, string behaviorName, int observationSize, List<string> observationNames,
            ActionSpec actions, int decisionPeriod, out BehaviorChannel channel)
        {
            if (!channels.TryGetValue(behaviorName, out channel))
            {
                channel = new BehaviorChannel(behaviorName, observationSize, observationNames, actions, decisionPeriod)
                {
                    Buffer = new ExperienceBuffer(behaviorName, observationSize, actions.Count, includeNextObservations),
                };
                channels[behaviorName] = channel;
            }
            else if (channel.ObservationSize != observationSize || channel.Actions.Count != actions.Count)
            {
                Debug.LogError($"[IA Foot] L'agent '{agent.name}' n'a pas les mêmes tailles d'observations / d'actions que les " +
                               $"autres agents du comportement '{behaviorName}'. Donnez-lui un autre nom de comportement.", agent);
                channel = null;
                return -1;
            }

            channel.Agents.Add(agent);
            helloDirty = true;
            return nextAgentId++;
        }

        public void UnregisterAgent(FootAgent agent, BehaviorChannel channel)
        {
            if (channel != null && channel.Agents.Remove(agent))
                helloDirty = true;
        }

        public int NextEpisodeId() => nextEpisodeId++;

        public void Record(BehaviorChannel channel, int agentId, int episodeId, int step, float[] observation, float[] action,
            int actionIndex, float logProbability, float reward, float[] nextObservation, bool terminated, bool truncated, int modelVersion)
        {
            channel.Buffer.Add(agentId, episodeId, step, observation, action, actionIndex, logProbability, reward,
                nextObservation, terminated, truncated, modelVersion);
            if (channel.Buffer.Count >= batchSize)
                Flush(channel);
        }

        public void ReportEpisode(BehaviorChannel channel, int agentId, int episodeId, float totalReward, int length, string outcome)
        {
            channel.Buffer.AddEpisode(agentId, episodeId, totalReward, length, outcome);
            channel.EpisodesFinished++;
            channel.RecentReturns.Enqueue(totalReward);
            while (channel.RecentReturns.Count > rewardWindow)
                channel.RecentReturns.Dequeue();
        }

        // ------------------------------------------------------------------ Unity -> Python

        void Flush(BehaviorChannel channel)
        {
            if (channel.Buffer.Count == 0)
                return;

            Message batch = channel.Buffer.BuildBatchAndClear(channel.NextBatchId++);
            int count = batch.Header.GetInt("count");
            EnsureHello();
            if (client != null && client.Send(batch, out int frameBytes))
            {
                channel.BatchesSent++;
                LogBatchSent(channel, batch.Header.GetInt("batch_id"), count, frameBytes);
                return;
            }

            // Pas de back-end : on jette le batch pour ne pas accumuler indéfiniment en mémoire.
            droppedBatches++;
            if (droppedBatches == 1 || droppedBatches % 20 == 0)
                Debug.LogWarning($"[IA Foot] Back-end Python non connecté : {droppedBatches} batch(s) ignoré(s).", this);
        }

        void EnsureHello()
        {
            if (client == null || !client.IsConnected)
                return;

            int connection = client.ConnectionCount;
            if (connection == helloConnection && !helloDirty)
                return;

            helloConnection = connection;
            helloDirty = false;
            client.Send(BuildHello());
        }

        Message BuildHello()
        {
            var behaviors = new List<object>();
            foreach (BehaviorChannel channel in channels.Values)
            {
                var actions = new List<object>();
                foreach (ActionDim dim in channel.Actions.Dims)
                {
                    actions.Add(new Dictionary<string, object>
                    {
                        ["name"] = dim.Name,
                        ["type"] = dim.IsBinary ? "binary" : "continuous",
                    });
                }

                var agents = new List<object>();
                foreach (FootAgent agent in channel.Agents)
                {
                    agents.Add(new Dictionary<string, object>
                    {
                        ["id"] = agent.AgentId,
                        ["name"] = agent.name,
                        ["team"] = agent.TeamName,
                    });
                }

                behaviors.Add(new Dictionary<string, object>
                {
                    ["name"] = channel.Name,
                    ["obs_size"] = channel.ObservationSize,
                    ["obs_names"] = channel.ObservationNames,
                    ["action_size"] = channel.Actions.Count,
                    ["actions"] = actions,
                    ["decision_period"] = channel.DecisionPeriod,
                    ["model_version"] = channel.Policy.Version,
                    ["agents"] = agents,
                });
            }

            var hello = new Message("hello");
            hello.Header["protocol"] = ProtocolVersion;
            hello.Header["session"] = sessionId;
            hello.Header["unity_version"] = Application.unityVersion;
            hello.Header["fixed_delta_time"] = Time.fixedDeltaTime;
            hello.Header["time_scale"] = Time.timeScale;
            hello.Header["batch_size"] = batchSize;
            hello.Header["behaviors"] = behaviors;
            return hello;
        }

        // ------------------------------------------------------------------ Python -> Unity

        void Handle(Message message)
        {
            switch (message.Type)
            {
                case "set_model":
                    ApplyModel(message, true);
                    break;
                case "set_config":
                    ApplyConfig(message);
                    break;
                default:
                    Debug.LogWarning($"[IA Foot] Message de type inconnu reçu : '{message.Type}'.", this);
                    break;
            }
        }

        /// <summary>Construit le modèle décrit par le message et l'installe sur les agents du comportement visé ("*" = tous).</summary>
        void ApplyModel(Message message, bool reply)
        {
            string target = message.Header.GetString("behavior", "*");
            int version = message.Header.GetInt("version", 1);
            var ack = new Message("model_ack");
            ack.Header["behavior"] = target;
            ack.Header["version"] = version;

            try
            {
                var policies = new List<KeyValuePair<BehaviorChannel, Policy>>();
                foreach (BehaviorChannel channel in channels.Values)
                {
                    if (target != "*" && target != channel.Name)
                        continue;

                    // Tout est construit et vérifié avant d'être installé : un modèle invalide ne casse pas l'ancien.
                    Policy policy = Policy.FromMessage(message, channel.Actions, channel.ObservationSize);
                    policy.Verify(message, channel.ObservationSize);
                    policies.Add(new KeyValuePair<BehaviorChannel, Policy>(channel, policy));
                }

                if (policies.Count == 0)
                    throw new InvalidOperationException($"aucun comportement nommé '{target}' dans la scène");

                int parameterCount = 0;
                foreach (Tensor tensor in message.Tensors.Values)
                    parameterCount += tensor.Count;

                // Les agents lisent la politique de leur comportement à chaque décision : le changement est immédiat.
                foreach (KeyValuePair<BehaviorChannel, Policy> pair in policies)
                {
                    pair.Key.Policy = pair.Value;
                    LogModelInstalled(pair.Key, pair.Value, parameterCount);
                }

                ack.Header["ok"] = true;
                ack.Header["description"] = policies[0].Value.Describe();
                if (saveReceivedModels)
                    SaveModel(message, target, version);
            }
            catch (Exception e)
            {
                Debug.LogError($"[IA Foot] Modèle v{version} refusé : {e.Message}", this);
                ack.Header["ok"] = false;
                ack.Header["error"] = e.Message;
            }

            if (reply && client != null)
                client.Send(ack);
        }

        void ApplyConfig(Message message)
        {
            Dictionary<string, object> header = message.Header;
            if (header.Has("time_scale"))
            {
                timeScale = Mathf.Clamp(header.GetFloat("time_scale", 1f), 0.1f, 50f);
                Time.timeScale = timeScale;
            }
            if (header.Has("batch_size"))
                batchSize = Mathf.Max(1, header.GetInt("batch_size", batchSize));
            if (header.GetBool("flush"))
            {
                foreach (BehaviorChannel channel in channels.Values)
                    Flush(channel);
            }

            if (verboseLogs)
                Debug.Log($"[IA Foot] Réglages reçus : timeScale = {timeScale}, batchSize = {batchSize}.", this);
        }

        void SaveModel(Message message, string behavior, int version)
        {
            try
            {
                string folder = Path.Combine(Application.persistentDataPath, "IAFootModels");
                Directory.CreateDirectory(folder);
                string name = behavior == "*" ? "all" : behavior;
                string path = Path.Combine(folder, $"{name}_v{version}.bytes");
                File.WriteAllBytes(path, message.Encode());
                if (verboseLogs)
                    Debug.Log($"[IA Foot] Modèle sauvegardé : {path}", this);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[IA Foot] Sauvegarde du modèle impossible : {e.Message}", this);
            }
        }
    }
}
