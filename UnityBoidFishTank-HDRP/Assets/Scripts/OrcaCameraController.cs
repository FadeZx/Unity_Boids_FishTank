#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

using System.Collections.Generic;
using UnityEngine;
using Unity.Cinemachine;

public class OrcaCameraController : MonoBehaviour
{
    [Header("Cinemachine")]
    [Tooltip("Assign your FreeLook-style Cinemachine camera used to follow specific orcas.")]
    public CinemachineCamera freeLook;
    [Tooltip("Assign your target group to feed the overview camera.")]
    public CinemachineTargetGroup targetGroup;
    [Tooltip("Assign the overview virtual camera that frames the target group.")]
    public CinemachineCamera overviewCamera;
    [Tooltip("Optional dedicated Cinemachine camera used only for WASD free-roam controls.")]
    public CinemachineCamera freeroamCamera;

    [Header("Freeroam Controls")]
    [Tooltip("Automatically enter freeroam mode when play starts.")]
    public bool startInFreeroam = true;
    public float freeroamMoveSpeed = 6f;
    public float freeroamFastMultiplier = 3f;
    public float freeroamLookSensitivity = 2f;
    public float freeroamScrollFovSpeed = 10f;
    public float freeroamMinFov = 25f;
    public float freeroamMaxFov = 80f;

    OrcaController owner;
    bool freeroamActive;
    Quaternion freeroamLookRot;
    float freeroamYaw;
    float freeroamPitch;

    void Awake()
    {
        if (!owner) owner = GetComponent<OrcaController>();
    }

    public void Initialize(OrcaController controller)
    {
        owner = controller;
        AutoFindCinemachine();
        if (startInFreeroam)
            ActivateFreeroamCamera();
    }

    void Update()
    {
        if (freeroamActive)
            UpdateFreeroamCamera();
    }

    public void SyncTargetGroup(List<OrcaAgent> pod)
    {
        if (targetGroup == null || pod == null) return;
        var targets = new List<CinemachineTargetGroup.Target>(pod.Count);
        for (int i = 0; i < pod.Count; i++)
        {
            if (pod[i] == null) continue;
            targets.Add(new CinemachineTargetGroup.Target
            {
                Object = pod[i].transform,
                Weight = 1f,
                Radius = 1.5f
            });
        }
        targetGroup.Targets = targets;
    }

    public void DrawCameraUI(List<OrcaAgent> pod)
    {
        GUILayout.Space(6);
        GUILayout.Label("<b>Camera</b>", new GUIStyle(GUI.skin.label) { richText = true });
        GUILayout.BeginVertical(GUI.skin.box);
        GUI.enabled = freeroamCamera != null || freeLook != null;
        if (GUILayout.Button(freeroamCamera != null ? "Freeroam Camera" : "Freeroam (FreeLook)"))
            ActivateFreeroamCamera();
        GUI.enabled = overviewCamera != null;
        if (GUILayout.Button("Top-Down: Target Group"))
            ActivateOverviewCamera();
        GUI.enabled = true;
        GUILayout.EndVertical();

        if (pod == null) return;
        foreach (var o in pod)
        {
            if (o == null) continue;
            GUI.enabled = freeLook != null;
            if (GUILayout.Button($"FreeLook Follow: {o.name}"))
                Follow(o);
            GUI.enabled = true;
        }
    }

    public void Follow(OrcaAgent target)
    {
        freeroamActive = false;
        if (freeLook == null || target == null) return;
        freeLook.Follow = target.transform;
        freeLook.LookAt = target.transform;
        freeLook.Priority = 20;
        if (overviewCamera != null) overviewCamera.Priority = 10;
        if (freeroamCamera != null) freeroamCamera.Priority = 5;
    }

    public void ActivateFreeroamCamera()
    {
        if (freeroamCamera != null)
        {
            freeroamActive = true;
            var e = freeroamCamera.transform.rotation.eulerAngles;
            freeroamYaw = e.y;
            freeroamPitch = e.x > 180f ? e.x - 360f : e.x;
            freeroamLookRot = Quaternion.Euler(freeroamPitch, freeroamYaw, 0f);
            freeroamCamera.Priority = 30;
            if (overviewCamera != null) overviewCamera.Priority = 10;
            if (freeLook != null) freeLook.Priority = 10;
            return;
        }

        freeroamActive = false;
        if (freeLook == null) return;
        freeLook.Follow = null;
        freeLook.LookAt = null;
        freeLook.Priority = 25;
        if (overviewCamera != null) overviewCamera.Priority = 10;
    }

    public void ActivateOverviewCamera()
    {
        freeroamActive = false;
        if (overviewCamera == null) return;
        overviewCamera.Priority = 25;
        if (freeLook != null) freeLook.Priority = 10;
        if (freeroamCamera != null) freeroamCamera.Priority = 5;
    }

    void AutoFindCinemachine()
    {
        if (freeLook == null)
            freeLook = FindFirstObjectByType<CinemachineCamera>();
        if (targetGroup == null)
            targetGroup = FindFirstObjectByType<CinemachineTargetGroup>();
        if (overviewCamera == null)
            overviewCamera = FindFirstObjectByType<CinemachineCamera>();
    }

    void UpdateFreeroamCamera()
    {
        if (freeroamCamera == null)
        {
            freeroamActive = false;
            return;
        }

        var t = freeroamCamera.transform;
        if (MouseRightHeld())
        {
            Vector2 d = MouseDelta();
            freeroamYaw += d.x * freeroamLookSensitivity;
            freeroamPitch = Mathf.Clamp(freeroamPitch - d.y * freeroamLookSensitivity, -89f, 89f);
        }
        freeroamLookRot = Quaternion.Euler(freeroamPitch, freeroamYaw, 0f);
        t.rotation = freeroamLookRot;

        Vector3 move = Vector3.zero;
        if (KeyHeld_Forward()) move += Vector3.forward;
        if (KeyHeld_Backward()) move += Vector3.back;
        if (KeyHeld_Left()) move += Vector3.left;
        if (KeyHeld_Right()) move += Vector3.right;
        if (KeyHeld_Up()) move += Vector3.up;
        if (KeyHeld_Down()) move += Vector3.down;

        if (move.sqrMagnitude > 1f) move.Normalize();
        float speed = freeroamMoveSpeed * (FastHeld() ? freeroamFastMultiplier : 1f);
        t.position += t.TransformDirection(move) * speed * Time.deltaTime;

        float scroll = ScrollDelta();
        if (Mathf.Abs(scroll) > 0.001f)
        {
            var lens = freeroamCamera.Lens;
            lens.FieldOfView = Mathf.Clamp(lens.FieldOfView - scroll * freeroamScrollFovSpeed, freeroamMinFov, freeroamMaxFov);
            freeroamCamera.Lens = lens;
        }
    }

    bool KeyHeld_Forward()
    {
#if ENABLE_INPUT_SYSTEM
        var k = Keyboard.current; if (k == null) return false; return k.wKey.isPressed || k.upArrowKey.isPressed;
#else
        return Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow);
#endif
    }
    bool KeyHeld_Backward()
    {
#if ENABLE_INPUT_SYSTEM
        var k = Keyboard.current; if (k == null) return false; return k.sKey.isPressed || k.downArrowKey.isPressed;
#else
        return Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow);
#endif
    }
    bool KeyHeld_Left()
    {
#if ENABLE_INPUT_SYSTEM
        var k = Keyboard.current; if (k == null) return false; return k.aKey.isPressed || k.leftArrowKey.isPressed;
#else
        return Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow);
#endif
    }
    bool KeyHeld_Right()
    {
#if ENABLE_INPUT_SYSTEM
        var k = Keyboard.current; if (k == null) return false; return k.dKey.isPressed || k.rightArrowKey.isPressed;
#else
        return Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow);
#endif
    }
    bool KeyHeld_Up()
    {
#if ENABLE_INPUT_SYSTEM
        var k = Keyboard.current; if (k == null) return false; return k.eKey.isPressed || k.pageUpKey.isPressed;
#else
        return Input.GetKey(KeyCode.E) || Input.GetKey(KeyCode.PageUp);
#endif
    }
    bool KeyHeld_Down()
    {
#if ENABLE_INPUT_SYSTEM
        var k = Keyboard.current; if (k == null) return false; return k.qKey.isPressed || k.pageDownKey.isPressed;
#else
        return Input.GetKey(KeyCode.Q) || Input.GetKey(KeyCode.PageDown);
#endif
    }
    bool FastHeld()
    {
#if ENABLE_INPUT_SYSTEM
        var k = Keyboard.current; return k != null && (k.leftShiftKey.isPressed || k.rightShiftKey.isPressed);
#else
        return Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
#endif
    }
    bool MouseRightHeld()
    {
#if ENABLE_INPUT_SYSTEM
        return Mouse.current != null && Mouse.current.rightButton.isPressed;
#else
        return Input.GetMouseButton(1);
#endif
    }
    Vector2 MouseDelta()
    {
#if ENABLE_INPUT_SYSTEM
        return Mouse.current != null ? Mouse.current.delta.ReadValue() * 0.02f : Vector2.zero;
#else
        return new Vector2(Input.GetAxis("Mouse X"), Input.GetAxis("Mouse Y"));
#endif
    }
    float ScrollDelta()
    {
#if ENABLE_INPUT_SYSTEM
        return Mouse.current != null ? Mouse.current.scroll.ReadValue().y * 0.01f : 0f;
#else
        return Input.GetAxis("Mouse ScrollWheel");
#endif
    }
}
