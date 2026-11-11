using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace IAFoot
{
    /// <summary>
    /// Zone de but (BoxCollider en trigger placé derrière la ligne). Le but est validé quand le ballon est
    /// entièrement dans la zone, comme dans les vraies règles (ballon entièrement derrière la ligne).
    /// Le ballon doit aussi être entré par l'ouverture de la cage (la ligne de but) : un ballon qui passe par
    /// le côté, l'arrière ou le dessus du filet ne compte pas, même s'il finit dans la zone.
    /// C'est le MatchManager qui décide si le but compte ; il déclenche ensuite <see cref="PlayCelebration"/>.
    /// </summary>
    [RequireComponent(typeof(BoxCollider))]
    public class GoalZone : MonoBehaviour
    {
        [Tooltip("Équipe qui défend ce but (celle qui encaisse si le ballon y entre).")]
        [SerializeField] TeamId defendingTeam = TeamId.Blue;
        [SerializeField] bool requireBallFullyInside = true;
        [Tooltip("Le but ne compte que si le ballon est entré par l'ouverture de la cage (pas par le côté, l'arrière ou le dessus).")]
        [SerializeField] bool requireEntryThroughMouth = true;
        [Tooltip("Direction de l'ouverture (vers le terrain), dans le repère de cet objet. (0, 0, 0) = automatique : " +
                 "la face horizontale de la zone la plus tournée vers le centre de l'arène (face verte dans la vue Scene).")]
        [SerializeField] Vector3 mouthDirection;

        [Header("Célébration")]
        [Tooltip("Objet animé lors d'un but (par défaut le parent : le modèle de la cage).")]
        [SerializeField] Transform animatedTarget;
        [SerializeField, Min(0.01f)] float celebrationDuration = 0.8f;
        [SerializeField] float punchScale = 0.1f;
        [SerializeField] float shakeAngle = 5f;
        [SerializeField] ParticleSystem goalEffect;
        [SerializeField] AudioSource goalSound;

        /// <summary>Déclenché à chaque pas de physique tant que le ballon est dans le but.</summary>
        public event Action<GoalZone, SoccerBall> BallEntered;

        public TeamId DefendingTeam => defendingTeam;
        public Vector3 Center => transform.TransformPoint(Box.center);

        BoxCollider Box => box ? box : box = GetComponent<BoxCollider>();

        BoxCollider box;
        Vector3 baseScale;
        Quaternion baseRotation;
        Coroutine celebration;
        // Pour chaque ballon présent dans la zone : est-il entré par l'ouverture ?
        readonly Dictionary<SoccerBall, bool> entries = new Dictionary<SoccerBall, bool>();

        void Awake()
        {
            Box.isTrigger = true;
            if (!animatedTarget)
                animatedTarget = transform.parent ? transform.parent : transform;
            baseScale = animatedTarget.localScale;
            baseRotation = animatedTarget.localRotation;
        }

        void OnDisable()
        {
            entries.Clear();
            if (celebration != null)
            {
                StopCoroutine(celebration);
                celebration = null;
                RestoreTarget();
            }
        }

        /// <summary>Configuration depuis l'outil éditeur.</summary>
        public void Configure(TeamId defending, Transform target)
        {
            defendingTeam = defending;
            animatedTarget = target;
        }

        /// <summary>Impose la direction de l'ouverture (repère local), au lieu de la déduire du centre de l'arène.</summary>
        public void SetMouthDirection(Vector3 localDirection) => mouthDirection = localDirection;

        void OnTriggerStay(Collider other)
        {
            Rigidbody body = other.attachedRigidbody;
            if (!body || !body.TryGetComponent(out SoccerBall ball))
                return;

            if (!entries.TryGetValue(ball, out bool throughMouth))
            {
                // Premier pas de physique où le ballon touche la zone : on regarde par quelle face il arrive.
                Vector3 local = transform.InverseTransformPoint(ball.Position) - Box.center;
                Vector3 velocity = transform.InverseTransformDirection(ball.Velocity);
                throughMouth = EntryFace(local, velocity, Box.size * 0.5f) == MouthFace();
                entries[ball] = throughMouth;
            }

            if (requireEntryThroughMouth && !throughMouth)
                return;
            if (!requireBallFullyInside || ContainsSphere(ball.Position, ball.Radius))
                BallEntered?.Invoke(this, ball);
        }

        void OnTriggerExit(Collider other)
        {
            Rigidbody body = other.attachedRigidbody;
            if (body && body.TryGetComponent(out SoccerBall ball))
                entries.Remove(ball);
        }

        /// <summary>Code de face : axe * 2 + (1 si face positive, 0 si négative).</summary>
        public static int FaceCode(int axis, bool positive) => axis * 2 + (positive ? 1 : 0);

        /// <summary>
        /// Face de la boîte (centrée en 0, demi-tailles `half`) par laquelle un objet en `local`, de vitesse `velocity`,
        /// est entré. Centre encore hors de la boîte : la face qu'il dépasse le plus. Centre à l'intérieur : la face
        /// par laquelle sort le rayon remonté à contre-sens de la vitesse. Immobile à l'intérieur : la face la plus proche.
        /// </summary>
        public static int EntryFace(Vector3 local, Vector3 velocity, Vector3 half)
        {
            int face = -1;
            float best = 0f;
            for (int axis = 0; axis < 3; axis++)
            {
                float excess = (Mathf.Abs(local[axis]) - half[axis]) / Mathf.Max(half[axis], 1e-5f);
                if (excess > best)
                {
                    best = excess;
                    face = FaceCode(axis, local[axis] > 0f);
                }
            }
            if (face >= 0)
                return face;

            best = float.PositiveInfinity;
            for (int axis = 0; axis < 3; axis++)
            {
                float back = -velocity[axis];
                if (Mathf.Abs(back) < 1e-4f)
                    continue;
                float t = ((back > 0f ? half[axis] : -half[axis]) - local[axis]) / back;
                if (t < best)
                {
                    best = t;
                    face = FaceCode(axis, back > 0f);
                }
            }
            if (face >= 0)
                return face;

            best = float.PositiveInfinity;
            for (int axis = 0; axis < 3; axis++)
            {
                for (int sign = -1; sign <= 1; sign += 2)
                {
                    float distance = half[axis] - local[axis] * sign;
                    if (distance < best)
                    {
                        best = distance;
                        face = FaceCode(axis, sign > 0);
                    }
                }
            }
            return face;
        }

        /// <summary>Face de l'ouverture de la cage : axe horizontal local le plus aligné avec la direction du terrain.</summary>
        public int MouthFace()
        {
            Vector3 local = mouthDirection;
            if (local.sqrMagnitude < 1e-8f)
            {
                MatchManager match = GetComponentInParent<MatchManager>();
                Vector3 field = match ? (match.ballSpawn ? match.ballSpawn.position : match.transform.position) : Vector3.zero;
                local = transform.InverseTransformDirection(field - Center);
            }

            // Une cage s'ouvre sur le côté : on ignore l'axe local le plus vertical.
            Vector3 up = transform.InverseTransformDirection(Vector3.up);
            int vertical = Mathf.Abs(up.x) >= Mathf.Abs(up.y) && Mathf.Abs(up.x) >= Mathf.Abs(up.z) ? 0
                         : Mathf.Abs(up.y) >= Mathf.Abs(up.z) ? 1 : 2;
            int axis = -1;
            for (int a = 0; a < 3; a++)
            {
                if (a != vertical && (axis < 0 || Mathf.Abs(local[a]) > Mathf.Abs(local[axis])))
                    axis = a;
            }
            return FaceCode(axis, local[axis] > 0f);
        }

        bool ContainsSphere(Vector3 worldCenter, float worldRadius)
        {
            Vector3 local = transform.InverseTransformPoint(worldCenter) - Box.center;
            Vector3 half = Box.size * 0.5f;
            Vector3 scale = transform.lossyScale;
            for (int axis = 0; axis < 3; axis++)
            {
                float radiusLocal = worldRadius / Mathf.Max(Mathf.Abs(scale[axis]), 1e-5f);
                if (Mathf.Abs(local[axis]) > half[axis] - radiusLocal)
                    return false;
            }
            return true;
        }

        public void PlayCelebration()
        {
            if (goalEffect)
                goalEffect.Play();
            if (goalSound)
                goalSound.Play();

            if (celebration != null)
                StopCoroutine(celebration);
            celebration = StartCoroutine(CelebrationRoutine());
        }

        IEnumerator CelebrationRoutine()
        {
            for (float t = 0f; t < 1f; t += Time.deltaTime / celebrationDuration)
            {
                // Oscillation amortie : la cage "encaisse" le ballon.
                float damping = 1f - t;
                float wave = Mathf.Sin(t * Mathf.PI * 6f) * damping;
                animatedTarget.localScale = baseScale * (1f + punchScale * Mathf.Abs(wave));
                animatedTarget.localRotation = baseRotation * Quaternion.Euler(0f, 0f, shakeAngle * wave);
                yield return null;
            }

            RestoreTarget();
            celebration = null;
        }

        void RestoreTarget()
        {
            animatedTarget.localScale = baseScale;
            animatedTarget.localRotation = baseRotation;
        }

        void OnDrawGizmos()
        {
            BoxCollider b = GetComponent<BoxCollider>();
            if (!b)
                return;

            Gizmos.matrix = transform.localToWorldMatrix;
            Color color = defendingTeam.DefaultColor();
            color.a = 0.2f;
            Gizmos.color = color;
            Gizmos.DrawCube(b.center, b.size);
            color.a = 1f;
            Gizmos.color = color;
            Gizmos.DrawWireCube(b.center, b.size);

            if (!requireEntryThroughMouth)
                return;
            // Ouverture de la cage : seule face par laquelle un but peut entrer.
            int face = MouthFace();
            int axis = face / 2;
            Vector3 offset = Vector3.zero, size = b.size;
            offset[axis] = (face % 2 == 1 ? 0.5f : -0.5f) * b.size[axis];
            size[axis] = 0f;
            Gizmos.color = new Color(0.2f, 1f, 0.3f, 0.35f);
            Gizmos.DrawCube(b.center + offset, size);
        }
    }
}
