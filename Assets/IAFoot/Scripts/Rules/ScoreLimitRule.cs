using UnityEngine;

namespace IAFoot
{
    /// <summary>La première équipe à atteindre le nombre de buts gagne.</summary>
    [CreateAssetMenu(menuName = "IA Foot/Rules/Score Limit", fileName = "ScoreLimitRule")]
    public class ScoreLimitRule : MatchRule
    {
        [Min(1)] public int goalsToWin = 5;

        public override void OnGoalScored(MatchManager match, TeamId scoringTeam)
        {
            if (match.GetScore(scoringTeam) >= goalsToWin)
                match.EndMatch(scoringTeam);
        }
    }
}
