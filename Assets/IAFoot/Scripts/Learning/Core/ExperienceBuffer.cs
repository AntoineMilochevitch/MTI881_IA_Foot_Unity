using System.Collections.Generic;

namespace IAFoot.Learning
{
    /// <summary>
    /// Accumule les transitions (observation, action, récompense, observation suivante, fin d'épisode...)
    /// de tous les agents d'un même comportement, puis les emballe dans un message "batch".
    /// Les transitions sont rangées dans l'ordre chronologique, tous agents mélangés : côté Python,
    /// on reconstitue les trajectoires en regroupant par (agent_id, episode_id) puis en triant par step.
    /// </summary>
    public sealed class ExperienceBuffer
    {
        readonly string behavior;
        readonly int observationSize;
        readonly int actionSize;
        readonly bool storeNextObservations;

        readonly List<float> observations = new List<float>();
        readonly List<float> nextObservations = new List<float>();
        readonly List<float> actions = new List<float>();
        readonly List<float> rewards = new List<float>();
        readonly List<float> logProbabilities = new List<float>();
        readonly List<int> terminated = new List<int>();
        readonly List<int> truncated = new List<int>();
        readonly List<int> actionIndices = new List<int>();
        readonly List<int> agentIds = new List<int>();
        readonly List<int> episodeIds = new List<int>();
        readonly List<int> steps = new List<int>();
        readonly List<int> modelVersions = new List<int>();
        readonly List<object> episodes = new List<object>();

        public ExperienceBuffer(string behavior, int observationSize, int actionSize, bool storeNextObservations)
        {
            this.behavior = behavior;
            this.observationSize = observationSize;
            this.actionSize = actionSize;
            this.storeNextObservations = storeNextObservations;
        }

        /// <summary>Nombre de transitions en attente.</summary>
        public int Count => rewards.Count;

        /// <param name="isTerminated">L'épisode s'est réellement terminé (but) : pas de valeur future.</param>
        /// <param name="isTruncated">L'épisode a été coupé (temps limite, remise en jeu) : la valeur future existe encore.</param>
        public void Add(int agentId, int episodeId, int step, float[] observation, float[] action, int actionIndex,
            float logProbability, float reward, float[] nextObservation, bool isTerminated, bool isTruncated, int modelVersion)
        {
            observations.AddRange(observation);
            actions.AddRange(action);
            if (storeNextObservations)
                nextObservations.AddRange(nextObservation);
            rewards.Add(reward);
            logProbabilities.Add(logProbability);
            terminated.Add(isTerminated ? 1 : 0);
            truncated.Add(isTruncated ? 1 : 0);
            actionIndices.Add(actionIndex);
            agentIds.Add(agentId);
            episodeIds.Add(episodeId);
            steps.Add(step);
            modelVersions.Add(modelVersion);
        }

        /// <summary>Résumé d'un épisode terminé, joint au prochain batch.</summary>
        public void AddEpisode(int agentId, int episodeId, float totalReward, int length, string outcome)
        {
            episodes.Add(new Dictionary<string, object>
            {
                ["agent_id"] = agentId,
                ["episode_id"] = episodeId,
                ["return"] = totalReward,
                ["length"] = length,
                ["outcome"] = outcome,
            });
        }

        public Message BuildBatchAndClear(int batchId)
        {
            int count = Count;
            var message = new Message("batch");
            message.Header["behavior"] = behavior;
            message.Header["batch_id"] = batchId;
            message.Header["count"] = count;
            message.Header["obs_size"] = observationSize;
            message.Header["action_size"] = actionSize;
            message.Header["episodes"] = new List<object>(episodes);

            message.Tensors["obs"] = new Tensor(observations.ToArray(), count, observationSize);
            message.Tensors["actions"] = new Tensor(actions.ToArray(), count, actionSize);
            message.Tensors["rewards"] = new Tensor(rewards.ToArray(), count);
            if (storeNextObservations)
                message.Tensors["next_obs"] = new Tensor(nextObservations.ToArray(), count, observationSize);
            message.Tensors["terminated"] = new Tensor(terminated.ToArray(), count);
            message.Tensors["truncated"] = new Tensor(truncated.ToArray(), count);
            message.Tensors["log_probs"] = new Tensor(logProbabilities.ToArray(), count);
            message.Tensors["action_index"] = new Tensor(actionIndices.ToArray(), count);
            message.Tensors["agent_id"] = new Tensor(agentIds.ToArray(), count);
            message.Tensors["episode_id"] = new Tensor(episodeIds.ToArray(), count);
            message.Tensors["step"] = new Tensor(steps.ToArray(), count);
            message.Tensors["model_version"] = new Tensor(modelVersions.ToArray(), count);

            observations.Clear();
            nextObservations.Clear();
            actions.Clear();
            rewards.Clear();
            logProbabilities.Clear();
            terminated.Clear();
            truncated.Clear();
            actionIndices.Clear();
            agentIds.Clear();
            episodeIds.Clear();
            steps.Clear();
            modelVersions.Clear();
            episodes.Clear();
            return message;
        }
    }
}
