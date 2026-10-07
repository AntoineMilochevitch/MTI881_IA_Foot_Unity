using System.Collections.Generic;
using UnityEngine;

namespace IAFoot.Learning
{
    /// <summary>
    /// Espace d'actions d'un joueur. Toutes les directions sont exprimées dans le repère d'équipe
    /// (+X = vers le but adverse) pour qu'un même modèle joue indifféremment des deux côtés.
    /// </summary>
    public static class FootActions
    {
        public const int MoveX = 0;
        public const int MoveZ = 1;
        public const int Shoot = 2;
        public const int ShootPower = 3;
        public const int ShootCurve = 4;
        public const int Tackle = 5;
        public const int Jump = 6;

        public static readonly ActionSpec Spec = new ActionSpec(
            new ActionDim("move_x", false),      // [-1, 1] : vers le but adverse (+) ou le sien (-)
            new ActionDim("move_z", false),      // [-1, 1] : latéral
            new ActionDim("shoot", true),        // bouton : tirer maintenant
            new ActionDim("shoot_power", false), // [-1, 1] ramené à une intensité [0, 1]
            new ActionDim("shoot_curve", false), // [-1, 1] : effet latéral
            new ActionDim("tackle", true),       // bouton
            new ActionDim("jump", true));        // bouton

        public static void Apply(float[] action, PlayerController player, MatchManager match)
        {
            var move = new Vector3(Mathf.Clamp(action[MoveX], -1f, 1f), 0f, Mathf.Clamp(action[MoveZ], -1f, 1f));
            player.SetMoveInput(match.DirectionFromTeamFrame(player.Team, move));

            if (action[Shoot] > 0.5f)
                player.Shoot(Mathf.Clamp01((action[ShootPower] + 1f) * 0.5f), Mathf.Clamp(action[ShootCurve], -1f, 1f));
            if (action[Tackle] > 0.5f)
                player.Tackle();
            if (action[Jump] > 0.5f)
                player.Jump();
        }
    }

    /// <summary>
    /// Vecteur d'observations d'un joueur, dans le repère d'équipe et normalisé (positions / positionScale,
    /// vitesses / velocityScale). La taille est fixe : les emplacements de coéquipiers / adversaires absents
    /// sont remplis de zéros, ce qui permet de passer du 1v1 au 2v2 sans changer de modèle.
    /// </summary>
    public static class FootObservation
    {
        const int SelfSize = 15;
        const int BallSize = 9;
        const int GoalSize = 4;
        const int OtherSize = 6;

        public static int Size(int maxTeammates, int maxOpponents) =>
            SelfSize + BallSize + GoalSize + (maxTeammates + maxOpponents) * OtherSize;

        public static List<string> Names(int maxTeammates, int maxOpponents)
        {
            var names = new List<string>
            {
                "self_pos_x", "self_pos_y", "self_pos_z",
                "self_vel_x", "self_vel_y", "self_vel_z",
                "self_forward_x", "self_forward_z",
                "self_charge", "self_shot_cooldown", "self_tackle_cooldown",
                "self_grounded", "self_stunned", "self_tackling", "self_can_kick",
                "ball_rel_x", "ball_rel_y", "ball_rel_z",
                "ball_pos_x", "ball_pos_y", "ball_pos_z",
                "ball_vel_x", "ball_vel_y", "ball_vel_z",
                "own_goal_rel_x", "own_goal_rel_z",
                "ball_to_opp_goal_x", "ball_to_opp_goal_z",
            };

            for (int i = 0; i < maxTeammates; i++)
                AddOtherNames(names, $"mate{i}");
            for (int i = 0; i < maxOpponents; i++)
                AddOtherNames(names, $"opp{i}");
            return names;
        }

        static void AddOtherNames(List<string> names, string prefix)
        {
            names.Add($"{prefix}_present");
            names.Add($"{prefix}_rel_x");
            names.Add($"{prefix}_rel_z");
            names.Add($"{prefix}_vel_x");
            names.Add($"{prefix}_vel_z");
            names.Add($"{prefix}_stunned");
        }

        public static void Write(float[] obs, PlayerController self, MatchManager match,
            int maxTeammates, int maxOpponents, float positionScale, float velocityScale)
        {
            TeamId team = self.Team;
            int k = 0;

            Vector3 position = match.ToTeamFrame(team, self.Position) / positionScale;
            Vector3 velocity = match.DirectionToTeamFrame(team, self.Velocity) / velocityScale;
            Vector3 forward = match.DirectionToTeamFrame(team, self.Forward);

            obs[k++] = position.x;
            obs[k++] = position.y;
            obs[k++] = position.z;
            obs[k++] = velocity.x;
            obs[k++] = velocity.y;
            obs[k++] = velocity.z;
            obs[k++] = forward.x;
            obs[k++] = forward.z;
            obs[k++] = self.ChargeAmount;
            obs[k++] = self.ShotCooldown01;
            obs[k++] = self.TackleCooldown01;
            obs[k++] = self.IsGrounded ? 1f : 0f;
            obs[k++] = self.IsStunned ? 1f : 0f;
            obs[k++] = self.IsTackling ? 1f : 0f;
            obs[k++] = self.CanKickBall() ? 1f : 0f;

            SoccerBall ball = match.Ball;
            Vector3 ballPosition = ball ? match.ToTeamFrame(team, ball.Position) / positionScale : Vector3.zero;
            Vector3 ballVelocity = ball ? match.DirectionToTeamFrame(team, ball.Velocity) / velocityScale : Vector3.zero;
            Vector3 ballRelative = ballPosition - position;

            obs[k++] = ballRelative.x;
            obs[k++] = ballRelative.y;
            obs[k++] = ballRelative.z;
            obs[k++] = ballPosition.x;
            obs[k++] = ballPosition.y;
            obs[k++] = ballPosition.z;
            obs[k++] = ballVelocity.x;
            obs[k++] = ballVelocity.y;
            obs[k++] = ballVelocity.z;

            GoalZone ownGoal = match.GetGoalDefendedBy(team);
            GoalZone opponentGoal = match.GetGoalDefendedBy(team.Opponent());
            Vector3 toOwnGoal = ownGoal ? match.ToTeamFrame(team, ownGoal.Center) / positionScale - position : Vector3.zero;
            Vector3 ballToGoal = opponentGoal ? match.ToTeamFrame(team, opponentGoal.Center) / positionScale - ballPosition : Vector3.zero;

            obs[k++] = toOwnGoal.x;
            obs[k++] = toOwnGoal.z;
            obs[k++] = ballToGoal.x;
            obs[k++] = ballToGoal.z;

            k = WriteOthers(obs, k, self, match, true, maxTeammates, position, positionScale, velocityScale);
            WriteOthers(obs, k, self, match, false, maxOpponents, position, positionScale, velocityScale);
        }

        static int WriteOthers(float[] obs, int k, PlayerController self, MatchManager match, bool teammates,
            int slots, Vector3 selfPosition, float positionScale, float velocityScale)
        {
            int end = k + slots * OtherSize;
            int written = 0;
            foreach (PlayerController other in match.AllPlayers)
            {
                if (written >= slots)
                    break;
                if (!other || other == self || (other.Team == self.Team) != teammates)
                    continue;

                Vector3 relative = match.ToTeamFrame(self.Team, other.Position) / positionScale - selfPosition;
                Vector3 velocity = match.DirectionToTeamFrame(self.Team, other.Velocity) / velocityScale;
                obs[k++] = 1f;
                obs[k++] = relative.x;
                obs[k++] = relative.z;
                obs[k++] = velocity.x;
                obs[k++] = velocity.z;
                obs[k++] = other.IsStunned ? 1f : 0f;
                written++;
            }

            while (k < end)
                obs[k++] = 0f;
            return end;
        }
    }
}
