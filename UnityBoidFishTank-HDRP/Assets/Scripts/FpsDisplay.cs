using UnityEngine;

public class FpsDisplay : MonoBehaviour
{
    [Tooltip("Higher values smooth the readout more. 5 = ~0.2s lag.")]
    public float lerpSpeed = 5f;
    public int fontSize = 14;
    public Color textColor = Color.white;
    public Vector2 margin = new Vector2(12f, 12f);
    public bool show = true;

    float smoothedFps;

    void Update()
    {
        if (!show) return;

        float currentFps = 1f / Mathf.Max(Time.unscaledDeltaTime, 0.0001f);
        smoothedFps = Mathf.Lerp(smoothedFps, currentFps, Time.unscaledDeltaTime * Mathf.Max(lerpSpeed, 0f));
    }

    void OnGUI()
    {
        if (!show) return;

        var style = new GUIStyle(GUI.skin.label)
        {
            fontSize = fontSize,
            alignment = TextAnchor.LowerRight,
            normal = { textColor = textColor }
        };

        float height = style.lineHeight + 4f;
        var rect = new Rect(0f, Screen.height - height - margin.y, Screen.width - margin.x, height);
        GUI.Label(rect, $"{smoothedFps:0.0} FPS", style);
    }
}
