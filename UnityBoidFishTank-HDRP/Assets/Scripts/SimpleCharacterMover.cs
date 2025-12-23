using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

[RequireComponent(typeof(CharacterController))]
public class SimpleCharacterMover : MonoBehaviour
{
    public float moveSpeed = 5f;
    public float gravity = -9.81f;
    public float jumpForce = 5f;

    CharacterController cc;
    Vector3 velocity;

    void Awake()
    {
        cc = GetComponent<CharacterController>();
    }

    void Update()
    {
        // Basic WASD/arrow movement on XZ (supports both Input System and legacy Input)
        float h = GetAxis("Horizontal");
        float v = GetAxis("Vertical");
        Vector3 move = new Vector3(h, 0f, v);
        if (move.sqrMagnitude > 1f) move.Normalize();

        Vector3 worldMove = transform.TransformDirection(move) * moveSpeed;

        if (cc.isGrounded)
        {
            velocity.y = -1f; // stick to ground
            if (JumpPressed())
                velocity.y = jumpForce;
        }
        else
        {
            velocity.y += gravity * Time.deltaTime;
        }

        cc.Move((worldMove + new Vector3(0f, velocity.y, 0f)) * Time.deltaTime);
    }

    float GetAxis(string axis)
    {
#if ENABLE_INPUT_SYSTEM
        var keyboard = Keyboard.current;
        if (keyboard == null) return 0f;
        float h = 0f, v = 0f;
        if (keyboard.aKey.isPressed || keyboard.leftArrowKey.isPressed) h -= 1f;
        if (keyboard.dKey.isPressed || keyboard.rightArrowKey.isPressed) h += 1f;
        if (keyboard.sKey.isPressed || keyboard.downArrowKey.isPressed) v -= 1f;
        if (keyboard.wKey.isPressed || keyboard.upArrowKey.isPressed) v += 1f;
        return axis == "Horizontal" ? h : v;
#else
        return Input.GetAxisRaw(axis);
#endif
    }

    bool JumpPressed()
    {
#if ENABLE_INPUT_SYSTEM
        var keyboard = Keyboard.current;
        return keyboard != null && (keyboard.spaceKey.wasPressedThisFrame);
#else
        return Input.GetButtonDown("Jump");
#endif
    }
}
