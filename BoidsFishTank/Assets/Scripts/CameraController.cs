#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif
using System.Collections.Generic;
using UnityEngine;
using Unity.Cinemachine;
using System.Linq;

[DefaultExecutionOrder(-40)]
public class CameraController : MonoBehaviour
{
    public enum Mode { FreeFly, Static }

    [Header("Target Camera")]
    public Camera targetCamera; // defaults to Camera.main if null

    [Header("Free-Fly Movement")]
    public float moveSpeed = 5f;
    public float fastMultiplier = 3f;
    public float lookSensitivity = 2f;
    public float scrollFovSpeed = 10f;
    public float minFov = 20f;
    public float maxFov = 75f;

    [Header("Static Cameras/Views")]
    public List<Camera> staticCameras = new List<Camera>();
    [Tooltip("Optional view points to use as static positions (used if no staticCameras provided).")]
    public List<Transform> staticViewPoints = new List<Transform>();
    public float snapLerp = 12f; // smoothing when moving to static point

    [Header("Runtime UI")]
    public bool showUI = true; // toggle with F3

    [Header("Cinemachine (optional)")]
    public CinemachineCamera freeLook;
    public CinemachineTargetGroup targetGroup;
    public CinemachineCamera overviewCamera;
    [Tooltip("Optional: Use Cinemachine virtual cameras for static shots (single Main Camera renders with Brain)")]
    public List<CinemachineCamera> staticVCams = new List<CinemachineCamera>();

    public Mode mode = Mode.FreeFly;
    int currentIndex = 0;
    Quaternion lookRot;

    // UI placement helpers
    Rect barAreaRect;
    class PopupSpec { public Rect rect; public string title; public List<Transform> items; }
    readonly List<PopupSpec> popupQueue = new List<PopupSpec>();

    void Awake()
    {
        if (!targetCamera) targetCamera = Camera.main;
        if (targetCamera)
            lookRot = targetCamera.transform.rotation;
    }

    void Start()
    {
        // If we have explicit camera list, ensure only active is enabled
        NormalizeStaticSetup();
        AutoFindCinemachine();
    }

    void NormalizeStaticSetup()
    {
        // Remove nulls
        staticCameras.RemoveAll(c => c == null);
        staticViewPoints.RemoveAll(t => t == null);
        staticVCams.RemoveAll(v => v == null);

        // If using Cinemachine virtual cameras: let Brain handle blending by priority
        if (staticVCams.Count > 0)
        {
            for (int i = 0; i < staticVCams.Count; i++)
                staticVCams[i].Priority = (mode == Mode.Static && i == currentIndex) ? 100 : 10;
            // Ensure live camera exists
            return;
        }

        if (staticCameras.Count > 0)
        {
            // Use real cameras, enable current, disable others
            for (int i = 0; i < staticCameras.Count; i++)
                staticCameras[i].gameObject.SetActive(i == currentIndex && mode == Mode.Static);
        }
    }

    void Update()
    {
        if (KeyDown_F3()) showUI = !showUI;
        if (KeyDown_C()) ToggleMode();

        if (mode == Mode.FreeFly)
        {
            UpdateFreeFly();
            // If we keep a list of static cams, ensure they are disabled while in free fly
            if (staticCameras.Count > 0)
            {
                for (int i = 0; i < staticCameras.Count; i++)
                    if (staticCameras[i]) staticCameras[i].gameObject.SetActive(false);
            }
        }
        else // Static
        {
            UpdateStatic();
        }

        // Numeric quick select 1..9
        int num = DigitPressed1to9();
        if (num >= 1)
        {
            int idx = num - 1;
            if (HasIndex(idx)) SetIndex(idx);
        }
    }

    void ToggleMode()
    {
        mode = (mode == Mode.FreeFly) ? Mode.Static : Mode.FreeFly;
        NormalizeStaticSetup();
    }

    bool HasIndex(int idx)
    {
        if (staticCameras.Count > 0) return idx >= 0 && idx < staticCameras.Count;
        return idx >= 0 && idx < staticViewPoints.Count;
    }

    public void Next()
    {
        int count = (staticCameras.Count > 0) ? staticCameras.Count : staticViewPoints.Count;
        if (count == 0) return;
        SetIndex((currentIndex + 1 + count) % count);
    }
    public void Prev()
    {
        int count = (staticCameras.Count > 0) ? staticCameras.Count : staticViewPoints.Count;
        if (count == 0) return;
        SetIndex((currentIndex - 1 + count) % count);
    }

    public void SetIndex(int idx)
    {
        if (!HasIndex(idx)) return;
        currentIndex = idx;
        if (mode != Mode.Static) mode = Mode.Static;

        if (staticVCams.Count > 0)
        {
            for (int i = 0; i < staticVCams.Count; i++)
                staticVCams[i].Priority = (i == currentIndex) ? 100 : 10; // Brain will blend main camera
        }
        else if (staticCameras.Count > 0)
        {
            for (int i = 0; i < staticCameras.Count; i++)
                if (staticCameras[i]) staticCameras[i].gameObject.SetActive(i == currentIndex);
        }
        else if (targetCamera && currentIndex < staticViewPoints.Count)
        {
            // Snap immediately on select
            var t = targetCamera.transform;
            var p = staticViewPoints[currentIndex];
            t.position = p.position;
            t.rotation = p.rotation;
            lookRot = t.rotation;
        }
    }

    void UpdateStatic()
    {
        // If using Cinemachine virtual cameras: Brain handles movement, nothing to do here
        if (staticVCams.Count > 0) return;

        // If using separate cameras: ensure only one enabled
        if (staticCameras.Count > 0)
        {
            for (int i = 0; i < staticCameras.Count; i++)
                if (staticCameras[i]) staticCameras[i].gameObject.SetActive(i == currentIndex);
            return;
        }

        // Otherwise drive the target camera toward the chosen viewpoint
        if (!targetCamera || staticViewPoints.Count == 0) return;
        var t = targetCamera.transform;
        var p = staticViewPoints[Mathf.Clamp(currentIndex, 0, staticViewPoints.Count - 1)];
        t.position = Vector3.Lerp(t.position, p.position, 1f - Mathf.Exp(-snapLerp * Time.deltaTime));
        t.rotation = Quaternion.Slerp(t.rotation, p.rotation, 1f - Mathf.Exp(-snapLerp * Time.deltaTime));
        lookRot = t.rotation;
    }

    void UpdateFreeFly()
    {
        if (!targetCamera) return;
        var t = targetCamera.transform;

        // Mouse look while RMB held
        if (MouseRightHeld())
        {
            Vector2 d = MouseDelta();
            lookRot = Quaternion.AngleAxis(d.x * lookSensitivity, Vector3.up) * lookRot;
            lookRot = Quaternion.AngleAxis(-d.y * lookSensitivity, Vector3.right) * lookRot;
        }
        t.rotation = lookRot;

        // WASD + QE movement
        Vector3 move = Vector3.zero;
        if (KeyHeld_Forward()) move += Vector3.forward;
        if (KeyHeld_Backward()) move += Vector3.back;
        if (KeyHeld_Left()) move += Vector3.left;
        if (KeyHeld_Right()) move += Vector3.right;
        if (KeyHeld_Up()) move += Vector3.up;
        if (KeyHeld_Down()) move += Vector3.down;

        if (move.sqrMagnitude > 1f) move.Normalize();
        float speed = moveSpeed * (FastHeld() ? fastMultiplier : 1f);
        t.position += t.TransformDirection(move) * speed * Time.deltaTime;

        // Scroll wheel FOV zoom
        float scroll = ScrollDelta();
        if (Mathf.Abs(scroll) > 0.001f)
        {
            float fov = targetCamera.fieldOfView - scroll * scrollFovSpeed;
            targetCamera.fieldOfView = Mathf.Clamp(fov, minFov, maxFov);
        }
    }

    void OnGUI()
    {
        if (!showUI) return;
        const int h = 84;
        const int pad = 8;
        int w = Mathf.Min(1000, Screen.width - 40);
        barAreaRect = new Rect((Screen.width - w) * 0.5f, Screen.height - h - 12, w, h);
        GUILayout.BeginArea(barAreaRect, GUI.skin.box);
        GUILayout.BeginHorizontal();

        GUILayout.Label("Camera:", GUILayout.Width(60));
        string modeLabel = (mode == Mode.FreeFly) ? "FreeFly" : "Static";
        if (GUILayout.Button(modeLabel, GUILayout.Width(90))) ToggleMode();

        if (mode == Mode.Static)
        {
            if (GUILayout.Button("Prev", GUILayout.Width(60))) Prev();

            int count = (staticCameras.Count > 0) ? staticCameras.Count : staticViewPoints.Count;
            for (int i = 0; i < count; i++)
            {
                GUIStyle s = (i == currentIndex) ? new GUIStyle(GUI.skin.button) { fontStyle = FontStyle.Bold } : GUI.skin.button;
                if (GUILayout.Button((i + 1).ToString(), s, GUILayout.Width(32))) SetIndex(i);
            }

            if (GUILayout.Button("Next", GUILayout.Width(60))) Next();
        }

        GUILayout.FlexibleSpace();
        if (mode == Mode.FreeFly && targetCamera)
        {
            GUILayout.Label($"FOV {(int)targetCamera.fieldOfView}", GUILayout.Width(80));
        }

        // Cinemachine quick actions (only in FreeFly mode)
        if (mode == Mode.FreeFly)
        {
            if (overviewCamera != null)
            {
                if (GUILayout.Button("Top-Down (Group)", GUILayout.Width(140))) ActivateOverviewCamera();
            }
            if (freeLook != null)
            {
                RenderFollowControls();
            }
        }

        GUILayout.EndHorizontal();

        // After building bar, render any queued popups so they appear above UI
        RenderQueuedPopups();

        GUILayout.Space(pad);
        GUILayout.BeginHorizontal();
        GUILayout.Label("Hotkeys: F3 UI • C toggle mode • 1..9 select view • RMB look • WASD move • QE up/down • Shift fast", new GUIStyle(GUI.skin.label) { fontSize = 11, wordWrap = true });
        GUILayout.EndHorizontal();
        GUILayout.EndArea();
    }

    // ---------- Input helpers (old + new systems) ----------
    bool KeyDown_F3()
    {
#if ENABLE_INPUT_SYSTEM
        return Keyboard.current != null && Keyboard.current.f3Key.wasPressedThisFrame;
#else
        return Input.GetKeyDown(KeyCode.F3);
#endif
    }
    bool KeyDown_C()
    {
#if ENABLE_INPUT_SYSTEM
        return Keyboard.current != null && Keyboard.current.cKey.wasPressedThisFrame;
#else
        return Input.GetKeyDown(KeyCode.C);
#endif
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
        var k = Keyboard.current; if (k == null) return false; return k.leftShiftKey.isPressed || k.rightShiftKey.isPressed;
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

    int DigitPressed1to9()
    {
#if ENABLE_INPUT_SYSTEM
        var k = Keyboard.current; if (k == null) return 0;
        if (k.digit1Key.wasPressedThisFrame) return 1;
        if (k.digit2Key.wasPressedThisFrame) return 2;
        if (k.digit3Key.wasPressedThisFrame) return 3;
        if (k.digit4Key.wasPressedThisFrame) return 4;
        if (k.digit5Key.wasPressedThisFrame) return 5;
        if (k.digit6Key.wasPressedThisFrame) return 6;
        if (k.digit7Key.wasPressedThisFrame) return 7;
        if (k.digit8Key.wasPressedThisFrame) return 8;
        if (k.digit9Key.wasPressedThisFrame) return 9;
        return 0;
#else
        if (Input.GetKeyDown(KeyCode.Alpha1)) return 1;
        if (Input.GetKeyDown(KeyCode.Alpha2)) return 2;
        if (Input.GetKeyDown(KeyCode.Alpha3)) return 3;
        if (Input.GetKeyDown(KeyCode.Alpha4)) return 4;
        if (Input.GetKeyDown(KeyCode.Alpha5)) return 5;
        if (Input.GetKeyDown(KeyCode.Alpha6)) return 6;
        if (Input.GetKeyDown(KeyCode.Alpha7)) return 7;
        if (Input.GetKeyDown(KeyCode.Alpha8)) return 8;
        if (Input.GetKeyDown(KeyCode.Alpha9)) return 9;
        return 0;
#endif
    }

    // ---------- Cinemachine controls ported from OrcaController ----------
    void AutoFindCinemachine()
    {
        if (freeLook == null)
            freeLook = FindFirstObjectByType<CinemachineCamera>();
        if (targetGroup == null)
            targetGroup = FindFirstObjectByType<CinemachineTargetGroup>();
        if (overviewCamera == null)
            overviewCamera = FindFirstObjectByType<CinemachineCamera>();
    }

    public void ActivateOverviewCamera()
    {
        if (overviewCamera == null) return;
        overviewCamera.Priority = 25;
        if (freeLook != null) freeLook.Priority = 10;
    }

    public void FollowTransform(Transform t)
    {
        if (freeLook == null || t == null) return;
        freeLook.Follow = t;
        freeLook.LookAt = t;
        freeLook.Priority = 20;
        if (overviewCamera != null) overviewCamera.Priority = 10;
    }

    // Helper to follow an Orca by role name prefix, leveraging naming used in OrcaController ("Leader 1", etc.)
    public void FollowRoleName(string rolePrefix)
    {
        var match = GameObject.Find(rolePrefix);
        if (match != null) { FollowTransform(match.transform); return; }
        // fallback: scan for begins-with (first)
        var t = FindObjectsOfType<Transform>().FirstOrDefault(x => x.name.StartsWith(rolePrefix, System.StringComparison.OrdinalIgnoreCase));
        if (t != null) FollowTransform(t);
    }

    // --- Dynamic follow controls per role (dropdown-like) ---
    Dictionary<string, bool> roleFoldout = new Dictionary<string, bool>{{"Leader",false},{"Flanker",false},{"Striker",false},{"Support",false}};
    void RenderFollowControls()
    {
        string[] roles = { "Leader", "Flanker", "Striker", "Support" };
        foreach (var role in roles)
        {
            var list = FindRoleTransforms(role);
            if (list.Count == 0) continue; // hide when none exist

            // Dropdown header button (toggle)
            string header = roleFoldout.TryGetValue(role, out bool open) && open ? $"Follow {role} ▾" : $"Follow {role} ▸";
            if (GUILayout.Button(header, GUILayout.Width(140)))
            {
                bool cur = roleFoldout.ContainsKey(role) && roleFoldout[role];
                roleFoldout[role] = !cur;
            }
            // Queue popup render for after the bar (prevent being clipped by BeginArea)
            if (roleFoldout[role])
            {
                var lastRect = GUILayoutUtility.GetLastRect();
                float headerCenterX = barAreaRect.x + lastRect.x + lastRect.width * 0.5f;
                int cols = Mathf.Clamp(list.Count, 1, 10);
                int btnSize = 28;
                int padding = 6;
                int panelW = cols * (btnSize + padding) + padding;
                int rows = Mathf.CeilToInt(list.Count / (float)cols);
                int panelH = rows * (btnSize + padding) + padding + 20; // add title bar space
                float panelX = Mathf.Clamp(headerCenterX - panelW * 0.5f, 8, Screen.width - panelW - 8);
                float panelY = Mathf.Max(barAreaRect.y - panelH - 6, 8); // drop-up
                popupQueue.Add(new PopupSpec{ rect = new Rect(panelX, panelY, panelW, panelH), title = $"Follow {role}", items = list });
            }
        }
    }

    List<Transform> FindRoleTransforms(string rolePrefix)
    {
        // Per frame discovery to reflect dynamic changes in pod size
        return FindObjectsOfType<Transform>().Where(t => t.name.StartsWith(rolePrefix, System.StringComparison.OrdinalIgnoreCase)).ToList();
    }

    void RenderQueuedPopups()
    {
        if (popupQueue.Count == 0) return;
        // Draw on top of everything after the bar area
        foreach (var pop in popupQueue)
        {
            GUI.Window(0, pop.rect, (id)=>
            {
                GUILayout.Label(pop.title, new GUIStyle(GUI.skin.label){fontStyle=FontStyle.Bold});
                int btnSize = 28;
                int padding = 6;
                int cols = Mathf.Clamp(pop.items.Count, 1, 10);
                int rows = Mathf.CeilToInt(pop.items.Count / (float)cols);
                int idx = 0;
                for (int r = 0; r < rows; r++)
                {
                    GUILayout.BeginHorizontal();
                    for (int c = 0; c < cols; c++)
                    {
                        if (idx >= pop.items.Count) break;
                        if (GUILayout.Button((idx + 1).ToString(), GUILayout.Width(btnSize), GUILayout.Height(btnSize)))
                            FollowTransform(pop.items[idx]);
                        idx++;
                    }
                    GUILayout.EndHorizontal();
                }
            }, pop.title);
        }
        popupQueue.Clear();
    }
}
