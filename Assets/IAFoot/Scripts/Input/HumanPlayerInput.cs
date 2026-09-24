using UnityEngine;
using UnityEngine.InputSystem;

namespace IAFoot
{
    /// <summary>
    /// Cerveau "humain" : lit le clavier / la manette (Input System) et pilote un <see cref="PlayerController"/>.
    /// Joueur 1 : ZQSD/WASD + Espace (tir, maintenir pour charger) + Maj gauche (tacle) + E (saut), ou manette (A / B / Y).
    /// Joueur 2 : flèches + Ctrl droit ou Pavé 0 (tir) + Maj droit ou Pavé 1 (tacle) + Pavé 2 ou Entrée du pavé (saut).
    /// </summary>
    [RequireComponent(typeof(PlayerController))]
    public class HumanPlayerInput : MonoBehaviour
    {
        public enum ControlScheme
        {
            Player1,
            Player2,
        }

        [SerializeField] ControlScheme scheme = ControlScheme.Player1;
        [SerializeField] bool allowGamepad = true;
        [Tooltip("Les directions sont relatives à la caméra (sinon : axes du monde).")]
        [SerializeField] bool cameraRelative = true;
        [Tooltip("Caméra de référence (Camera.main si vide).")]
        [SerializeField] Transform cameraOverride;

        PlayerController player;
        InputAction move;
        InputAction shoot;
        InputAction tackle;
        InputAction jump;

        void Awake() => player = GetComponent<PlayerController>();

        void OnEnable()
        {
            CreateActions();
            move.Enable();
            shoot.Enable();
            tackle.Enable();
            jump.Enable();
        }

        void OnDisable()
        {
            move?.Dispose();
            shoot?.Dispose();
            tackle?.Dispose();
            jump?.Dispose();
            player.SetMoveInput(Vector3.zero);
            player.CancelCharge();
        }

        void Update()
        {
            player.SetMoveInput(ToWorld(move.ReadValue<Vector2>()));

            if (shoot.WasPressedThisFrame())
                player.StartCharge();
            if (shoot.WasReleasedThisFrame())
                player.ReleaseCharge();
            if (tackle.WasPressedThisFrame())
                player.Tackle();
            if (jump.WasPressedThisFrame())
                player.Jump();
        }

        Vector3 ToWorld(Vector2 input)
        {
            Vector3 forward = Vector3.forward;
            Vector3 right = Vector3.right;

            Transform cam = cameraOverride ? cameraOverride : Camera.main ? Camera.main.transform : null;
            if (cameraRelative && cam)
            {
                forward = Vector3.ProjectOnPlane(cam.forward, Vector3.up);
                if (forward.sqrMagnitude < 1e-4f) // caméra à la verticale
                    forward = Vector3.ProjectOnPlane(cam.up, Vector3.up);
                forward.Normalize();
                right = Vector3.Cross(Vector3.up, forward);
            }

            return Vector3.ClampMagnitude(right * input.x + forward * input.y, 1f);
        }

        void CreateActions()
        {
            move = new InputAction("Move", InputActionType.Value, expectedControlType: "Vector2");
            shoot = new InputAction("Shoot", InputActionType.Button);
            tackle = new InputAction("Tackle", InputActionType.Button);
            jump = new InputAction("Jump", InputActionType.Button);

            if (scheme == ControlScheme.Player1)
            {
                // Les chemins <Keyboard>/w etc. désignent des positions physiques : ZQSD sur AZERTY.
                move.AddCompositeBinding("2DVector")
                    .With("Up", "<Keyboard>/w")
                    .With("Down", "<Keyboard>/s")
                    .With("Left", "<Keyboard>/a")
                    .With("Right", "<Keyboard>/d");
                shoot.AddBinding("<Keyboard>/space");
                tackle.AddBinding("<Keyboard>/leftShift");
                jump.AddBinding("<Keyboard>/e");

                if (allowGamepad)
                {
                    move.AddBinding("<Gamepad>/leftStick");
                    shoot.AddBinding("<Gamepad>/buttonSouth");
                    shoot.AddBinding("<Gamepad>/rightTrigger");
                    tackle.AddBinding("<Gamepad>/buttonEast");
                    jump.AddBinding("<Gamepad>/buttonNorth");
                }
            }
            else
            {
                move.AddCompositeBinding("2DVector")
                    .With("Up", "<Keyboard>/upArrow")
                    .With("Down", "<Keyboard>/downArrow")
                    .With("Left", "<Keyboard>/leftArrow")
                    .With("Right", "<Keyboard>/rightArrow");
                shoot.AddBinding("<Keyboard>/rightCtrl");
                shoot.AddBinding("<Keyboard>/numpad0");
                tackle.AddBinding("<Keyboard>/rightShift");
                tackle.AddBinding("<Keyboard>/numpad1");
                jump.AddBinding("<Keyboard>/numpad2");
                jump.AddBinding("<Keyboard>/numpadEnter");
            }
        }
    }
}
