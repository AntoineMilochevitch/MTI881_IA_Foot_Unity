using UnityEngine;

namespace IAFoot
{
    [DefaultExecutionOrder(-10)]
    [RequireComponent(typeof(PlayerController))]
    public class DefensiveBotInput : MonoBehaviour
    {
        [Header("Positioning")]
        [SerializeField, Range(0f, 1f)] float speedFactor = 0.8f;
        [Tooltip("Temps entre deux décisions (s).")]
        [SerializeField, Min(0f)] float reactionTime = 0.1f;
        [Tooltip("Distance à laquelle le bot se place devant le but.")]
        [SerializeField, Min(0f)] float distanceFromGoal = 0.85f;
        [Tooltip("Espace minimal à garder devant le but.")]
        [SerializeField, Min(0f)] float minimumGoalClearance = 0.35f;
        [Tooltip("Distance à garder entre le bot et le ballon quand il se replace.")]
        [SerializeField, Min(0f)] float ballClearance = 0.45f;
        [Tooltip("Marge de sécurité devant et sur les côtés du but.")]
        [SerializeField, Min(0f)] float goalMargin = 0.2f;
        [SerializeField, Min(0f)] float arrivalDistance = 0.08f;

        [Header("Dégagement")]
        [Tooltip("Le bot cesse de reculer quand le ballon est à cette distance.")]
        [SerializeField, Min(0f)] float clearanceTriggerDistance = 0.9f;
        [SerializeField, Range(0f, 1f)] float clearancePower = 1f;
        [Tooltip("Alignement minimal vers le terrain avant de dégager.")]
        [SerializeField, Range(0f, 1f)] float clearanceAimAlignment = 0.9f;
        [Tooltip("Petit déplacement utilisé pour tourner vers le terrain.")]
        [SerializeField, Range(0f, 1f)] float turningMoveFactor = 0.15f;

        PlayerController self;
        float decisionTimer;
        bool reportedMissingGoal;

        void Awake() => self = GetComponent<PlayerController>();

        void OnDisable()
        {
            if (self)
                self.SetMoveInput(Vector3.zero);
        }

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

            decisionTimer = Mathf.Max(reactionTime, Time.fixedDeltaTime);
            Defend(match);
        }

        void Defend(MatchManager match)
        {
            GoalZone ownGoal = match.GetGoalDefendedBy(self.Team);
            BoxCollider goalCollider = ownGoal ? ownGoal.GetComponent<BoxCollider>() : null;

            if (!ownGoal || !goalCollider)
            {
                self.SetMoveInput(Vector3.zero);
                if (!reportedMissingGoal)
                {
                    Debug.LogError(
                        "[IA Foot] DefensiveBotInput a besoin du but de son équipe et de son BoxCollider.",
                        this);
                    reportedMissingGoal = true;
                }
                return;
            }

            reportedMissingGoal = false;

            Vector3 goalPosition = Flat(ownGoal.Center);
            Vector3 ballPosition = Flat(match.Ball.Position);
            Vector3 attackDirection =
                Flat(match.transform.right * self.Team.AttackSign()).normalized;
            Vector3 lateralDirection =
                Vector3.Cross(Vector3.up, attackDirection).normalized;

            float mouthOffset =
                GetMouthOffset(goalCollider, ownGoal, goalPosition, attackDirection);
            float safeGoalClearance =
                Mathf.Max(minimumGoalClearance, mouthOffset + goalMargin);
            float safeHalfWidth =
                Mathf.Max(0f, GetHalfWidth(goalCollider, lateralDirection) - goalMargin);

            Vector3 relativeBall = ballPosition - goalPosition;
            Vector3 relativeSelf = Flat(self.Position) - goalPosition;

            float ballForward = Vector3.Dot(relativeBall, attackDirection);
            float ballLateral = Vector3.Dot(relativeBall, lateralDirection);
            float selfForward = Vector3.Dot(relativeSelf, attackDirection);
            float selfLateral = Vector3.Dot(relativeSelf, lateralDirection);

            bool underPressure =
                Vector3.Distance(Flat(self.Position), ballPosition) <= clearanceTriggerDistance;

            float maximumGoalDistance = Mathf.Max(distanceFromGoal, safeGoalClearance);
            float targetForward = underPressure
                ? Mathf.Clamp(selfForward, safeGoalClearance, maximumGoalDistance)
                : Mathf.Max(
                    safeGoalClearance,
                    Mathf.Min(distanceFromGoal, ballForward - ballClearance));

            float targetLateral =
                Mathf.Clamp(ballLateral, -safeHalfWidth, safeHalfWidth);

            Vector3 target = goalPosition
                + attackDirection * targetForward
                + lateralDirection * targetLateral;
            Vector3 move = target - Flat(self.Position);

            if (underPressure && self.CanKickBall())
            {
                if (Vector3.Dot(self.Forward, attackDirection) >= clearanceAimAlignment)
                {
                    self.SetMoveInput(Vector3.zero);
                    if (self.ShotCooldown01 <= 0f)
                        self.Shoot(clearancePower);
                }
                else
                {
                    self.SetMoveInput(attackDirection * turningMoveFactor);
                }
                return;
            }

            if (underPressure
                && move.sqrMagnitude <= arrivalDistance * arrivalDistance
                && Vector3.Dot(self.Forward, attackDirection) < clearanceAimAlignment)
            {
                move = attackDirection * turningMoveFactor;
            }

            self.SetMoveInput(
                move.sqrMagnitude > arrivalDistance * arrivalDistance
                    ? move.normalized * speedFactor
                    : Vector3.zero);
        }

        static float GetMouthOffset(
            BoxCollider goalCollider,
            GoalZone goal,
            Vector3 goalPosition,
            Vector3 attackDirection)
        {
            int face = goal.MouthFace();
            int axis = face / 2;
            float sign = face % 2 == 1 ? 1f : -1f;

            Vector3 faceOffset = Vector3.zero;
            faceOffset[axis] = sign * goalCollider.size[axis] * 0.5f;

            Vector3 faceCenter = goalCollider.transform.TransformPoint(
                goalCollider.center + faceOffset);

            return Mathf.Max(
                0f,
                Vector3.Dot(Flat(faceCenter) - goalPosition, attackDirection));
        }

        static float GetHalfWidth(BoxCollider goalCollider, Vector3 lateralDirection)
        {
            Vector3 halfSize = goalCollider.size * 0.5f;
            Transform goalTransform = goalCollider.transform;

            return Mathf.Abs(Vector3.Dot(
                       goalTransform.TransformVector(new Vector3(halfSize.x, 0f, 0f)),
                       lateralDirection))
                   + Mathf.Abs(Vector3.Dot(
                       goalTransform.TransformVector(new Vector3(0f, halfSize.y, 0f)),
                       lateralDirection))
                   + Mathf.Abs(Vector3.Dot(
                       goalTransform.TransformVector(new Vector3(0f, 0f, halfSize.z)),
                       lateralDirection));
        }

        static Vector3 Flat(Vector3 value) =>
            new Vector3(value.x, 0f, value.z);
    }
}