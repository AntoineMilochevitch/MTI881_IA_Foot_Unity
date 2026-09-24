using UnityEngine;

namespace IAFoot
{
    /// <summary>
    /// Cerveau scripté simple : se place derrière le ballon, pousse vers le but adverse, tire quand il est aligné
    /// et tacle l'adversaire qui a le ballon. Sert d'adversaire de test et de baseline pour évaluer les agents ML.
    /// </summary>
    [DefaultExecutionOrder(-10)]
    [RequireComponent(typeof(PlayerController))]
    public class SimpleBotInput : MonoBehaviour
    {
        [SerializeField, Range(0f, 1f)] float speedFactor = 0.85f;
        [Tooltip("Temps entre deux décisions (s).")]
        [SerializeField, Min(0f)] float reactionTime = 0.1f;
        [Tooltip("Distance à laquelle le bot se place derrière le ballon.")]
        [SerializeField, Min(0f)] float approachDistance = 0.2f;
        [Tooltip("Distance au but en dessous de laquelle le bot tente sa chance.")]
        [SerializeField, Min(0f)] float shootingDistance = 2.5f;
        [Tooltip("Alignement minimal (produit scalaire) entre le regard et le but pour tirer.")]
        [SerializeField, Range(0f, 1f)] float aimTolerance = 0.85f;
        [SerializeField, Range(0f, 1f)] float powerNoise = 0.15f;
        [SerializeField, Min(0f)] float tackleRange = 0.25f;
        [Tooltip("Probabilité de tacler par seconde quand l'occasion se présente.")]
        [SerializeField, Min(0f)] float tackleChancePerSecond = 1.5f;

        PlayerController self;
        float decisionTimer;

        void Awake() => self = GetComponent<PlayerController>();

        void OnDisable() => self.SetMoveInput(Vector3.zero);

        void FixedUpdate()
        {
            MatchManager match = self.Match;
            if (!match || !match.Ball || match.State != MatchState.Playing)
            {
                self.SetMoveInput(Vector3.zero);
                return;
            }

            decisionTimer -= Time.fixedDeltaTime;
            if (decisionTimer > 0f)
                return;
            decisionTimer = reactionTime;

            Decide(match, Mathf.Max(reactionTime, Time.fixedDeltaTime));
        }

        void Decide(MatchManager match, float interval)
        {
            Vector3 me = Flat(self.Position);
            Vector3 ball = Flat(match.Ball.Position);
            Vector3 attackDirection = Flat(match.transform.right * self.Team.AttackSign()).normalized;
            GoalZone targetGoal = match.GetGoalDefendedBy(self.Team.Opponent());
            Vector3 goal = targetGoal ? Flat(targetGoal.Center) : ball + attackDirection * 10f;

            Vector3 toGoal = goal - ball;
            toGoal = toGoal.sqrMagnitude > 1e-6f ? toGoal.normalized : attackDirection;
            Vector3 toBall = ball - me;
            Vector3 side = Vector3.Cross(Vector3.up, toGoal);

            // Où aller : contourner le ballon si on est du mauvais côté, sinon se placer derrière puis pousser.
            float ahead = Vector3.Dot(me - ball, toGoal);
            Vector3 target;
            if (ahead > -approachDistance * 0.5f)
            {
                float s = Vector3.Dot(me - ball, side) >= 0f ? 1f : -1f;
                target = ball - toGoal * approachDistance + side * (s * approachDistance);
            }
            else if (Vector3.Dot(toBall.normalized, toGoal) > 0.8f)
            {
                target = ball + toGoal * approachDistance;
            }
            else
            {
                target = ball - toGoal * approachDistance;
            }

            Vector3 move = target - me;
            self.SetMoveInput(move.sqrMagnitude > 0.0004f ? move.normalized * speedFactor : Vector3.zero);

            // Tir
            float distanceToGoal = Vector3.Distance(ball, goal);
            if (distanceToGoal < shootingDistance
                && Vector3.Dot(self.Forward, toGoal) > aimTolerance
                && self.CanKickBall())
            {
                float power = Mathf.Clamp01(distanceToGoal / shootingDistance + 0.3f + Random.Range(-powerNoise, powerNoise));
                self.Shoot(power);
                return;
            }

            // Tacle sur l'adversaire proche du ballon, devant nous.
            foreach (PlayerController other in match.AllPlayers)
            {
                if (!other || other.Team == self.Team || other.IsStunned)
                    continue;

                Vector3 toOther = Flat(other.Position) - me;
                bool opponentHasBall = Vector3.Distance(Flat(other.Position), ball) < tackleRange;
                if (toOther.magnitude < tackleRange && opponentHasBall
                    && Vector3.Dot(self.Forward, toOther.normalized) > 0.7f
                    && Random.value < tackleChancePerSecond * interval)
                {
                    self.Tackle();
                    return;
                }
            }
        }

        static Vector3 Flat(Vector3 v) => new Vector3(v.x, 0f, v.z);
    }
}
