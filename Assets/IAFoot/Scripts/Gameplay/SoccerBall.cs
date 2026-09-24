using System;
using UnityEngine;

namespace IAFoot
{
    /// <summary>
    /// Ballon avec une physique plus réaliste que le Rigidbody de base :
    /// traînée aérodynamique quadratique, effet Magnus (balles brossées / liftées / coupées),
    /// résistance au roulement sur l'herbe et rotation cohérente avec le roulement.
    /// Les rebonds sont gérés par le Physic Material du collider.
    /// </summary>
    [RequireComponent(typeof(Rigidbody), typeof(SphereCollider))]
    public class SoccerBall : MonoBehaviour
    {
        [Header("Masse")]
        [SerializeField, Min(0.01f)] float mass = 0.45f;

        [Header("Air")]
        [Tooltip("Traînée quadratique : a = -k·|v|·v.")]
        [SerializeField, Min(0f)] float airDrag = 0.02f;
        [Tooltip("Effet Magnus : a = k·(ω × v). Donne leur courbe aux tirs avec effet.")]
        [SerializeField, Min(0f)] float magnusCoefficient = 0.006f;
        [Tooltip("Amortissement de la rotation dans l'air.")]
        [SerializeField, Min(0f)] float airSpinDamping = 0.1f;

        [Header("Sol")]
        [Tooltip("Décélération due au roulement sur l'herbe (unités/s²).")]
        [SerializeField, Min(0f)] float rollingResistance = 0.8f;
        [Tooltip("Vitesse à laquelle la rotation s'aligne sur un roulement sans glissement.")]
        [SerializeField, Min(0f)] float rollingGrip = 8f;
        [SerializeField] LayerMask groundMask = ~0;
        [SerializeField, Min(0f)] float groundCheckTolerance = 0.01f;

        [Header("Limites")]
        [SerializeField, Min(0.1f)] float maxSpeed = 12f;
        [SerializeField, Min(1f)] float maxAngularSpeed = 150f;
        [Tooltip("En dessous de cette vitesse au sol, le ballon est arrêté net.")]
        [SerializeField, Min(0f)] float stopSpeed = 0.02f;

        [Header("Anti-blocage")]
        [Tooltip("Relance le ballon vers son point de départ s'il reste quasi immobile contre un mur (coins, derrière les cages...).")]
        [SerializeField] bool unstickFromWalls = true;
        [SerializeField, Min(0.1f)] float unstickDelay = 1.5f;
        [Tooltip("En dessous de cette vitesse, le ballon est considéré comme bloqué.")]
        [SerializeField, Min(0f)] float stuckSpeed = 0.25f;
        [SerializeField, Min(0f)] float unstickSpeed = 1.5f;

        /// <summary>(ballon, joueur) à chaque contact ou frappe d'un joueur.</summary>
        public event Action<SoccerBall, PlayerController> Touched;

        public Rigidbody Body => rb ? rb : rb = GetComponent<Rigidbody>();
        public Vector3 Position => Body.position;
        public Vector3 Velocity => Body.linearVelocity;
        public Vector3 AngularVelocity => Body.angularVelocity;
        public bool IsGrounded { get; private set; }
        public PlayerController LastTouchedBy { get; private set; }
        public float LastTouchTime { get; private set; } = float.NegativeInfinity;

        public float Radius
        {
            get
            {
                if (!sphere)
                    sphere = GetComponent<SphereCollider>();
                Vector3 s = transform.lossyScale;
                return sphere.radius * Mathf.Max(Mathf.Abs(s.x), Mathf.Abs(s.y), Mathf.Abs(s.z));
            }
        }

        Rigidbody rb;
        SphereCollider sphere;
        RaycastHit groundHit;
        Vector3 homePosition;
        float stuckTimer;
        readonly Collider[] wallBuffer = new Collider[8];

        void Awake()
        {
            rb = GetComponent<Rigidbody>();
            sphere = GetComponent<SphereCollider>();
            rb.mass = mass;
            rb.linearDamping = 0f;
            rb.angularDamping = airSpinDamping;
            rb.maxAngularVelocity = maxAngularSpeed;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            homePosition = rb.position;
        }

        void FixedUpdate()
        {
            float dt = Time.fixedDeltaTime;
            float radius = Radius;
            IsGrounded = Physics.Raycast(rb.position, Vector3.down, out groundHit, radius + groundCheckTolerance,
                groundMask, QueryTriggerInteraction.Ignore);

            Vector3 v = rb.linearVelocity;
            Vector3 w = rb.angularVelocity;
            float speed = v.magnitude;

            if (speed > 1e-4f)
            {
                Vector3 acceleration = -airDrag * speed * v;
                if (!IsGrounded)
                    acceleration += magnusCoefficient * Vector3.Cross(w, v);
                rb.AddForce(acceleration, ForceMode.Acceleration);
            }

            if (IsGrounded)
                ApplyRolling(dt, v, w, radius);

            if (rb.linearVelocity.sqrMagnitude > maxSpeed * maxSpeed)
                rb.linearVelocity = rb.linearVelocity.normalized * maxSpeed;

            if (unstickFromWalls)
                UpdateUnstick(dt, radius);
        }

        void UpdateUnstick(float dt, float radius)
        {
            if (rb.linearVelocity.sqrMagnitude > stuckSpeed * stuckSpeed || !IsTouchingWall(radius))
            {
                stuckTimer = 0f;
                return;
            }

            stuckTimer += dt;
            if (stuckTimer < unstickDelay)
                return;

            stuckTimer = 0f;
            Vector3 toHome = homePosition - rb.position;
            toHome.y = 0f;
            Vector3 direction = toHome.sqrMagnitude > 1e-4f ? toHome.normalized : Vector3.zero;
            rb.linearVelocity = (direction + Vector3.up * 0.6f).normalized * unstickSpeed;
            rb.angularVelocity = Vector3.zero;
        }

        /// <summary>Vrai si le ballon touche un collider statique quasi vertical (mur, poteau...).</summary>
        bool IsTouchingWall(float radius)
        {
            Vector3 center = rb.position;
            int count = Physics.OverlapSphereNonAlloc(center, radius + 0.02f, wallBuffer, ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < count; i++)
            {
                Collider other = wallBuffer[i];
                if (other == sphere || other.attachedRigidbody)
                    continue;
                if (other is MeshCollider meshCollider && !meshCollider.convex)
                    continue; // ClosestPoint n'accepte pas les mesh colliders concaves

                Vector3 away = center - other.ClosestPoint(center);
                if (away.sqrMagnitude > 1e-8f && Mathf.Abs(away.normalized.y) < 0.5f)
                    return true;
            }
            return false;
        }

        void ApplyRolling(float dt, Vector3 v, Vector3 w, float radius)
        {
            Vector3 normal = groundHit.normal;
            Vector3 tangential = Vector3.ProjectOnPlane(v, normal);
            float tangentialSpeed = tangential.magnitude;

            // Résistance au roulement : décélération constante le long du sol.
            if (tangentialSpeed > 0f)
            {
                float drop = rollingResistance * dt;
                Vector3 slowed = tangentialSpeed <= drop ? Vector3.zero : tangential * (1f - drop / tangentialSpeed);
                v += slowed - tangential;
                tangential = slowed;
            }

            if (tangential.sqrMagnitude < stopSpeed * stopSpeed && Mathf.Abs(Vector3.Dot(v, normal)) < stopSpeed)
            {
                rb.linearVelocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
                return;
            }

            rb.linearVelocity = v;

            // La rotation converge vers un roulement sans glissement ; la toupie (axe normal) s'amortit.
            Vector3 rollingSpin = Vector3.Cross(normal, tangential) / radius;
            Vector3 topSpin = Vector3.Project(w, normal) * Mathf.Exp(-rollingGrip * 0.25f * dt);
            rb.angularVelocity = Vector3.Lerp(w, rollingSpin + topSpin, 1f - Mathf.Exp(-rollingGrip * dt));
        }

        /// <summary>Frappe le ballon : remplace sa vitesse et sa rotation.</summary>
        public void Kick(Vector3 velocity, Vector3 spin, PlayerController kicker)
        {
            rb.linearVelocity = Vector3.ClampMagnitude(velocity, maxSpeed);
            rb.angularVelocity = spin;
            RegisterTouch(kicker);
        }

        /// <summary>Téléporte le ballon à l'arrêt (coup d'envoi).</summary>
        public void ResetBall(Vector3 position)
        {
            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
            rb.position = position;
            transform.position = position;
            homePosition = position;
            stuckTimer = 0f;
            LastTouchedBy = null;
            LastTouchTime = float.NegativeInfinity;
        }

        void OnCollisionEnter(Collision collision)
        {
            if (collision.rigidbody && collision.rigidbody.TryGetComponent(out PlayerController player))
                RegisterTouch(player);
        }

        void RegisterTouch(PlayerController player)
        {
            if (!player)
                return;
            LastTouchedBy = player;
            LastTouchTime = Time.time;
            Touched?.Invoke(this, player);
        }
    }
}
