using UnityEngine;
using TMPro;

[RequireComponent(typeof(TextMeshPro))]
public class BillboardTMP : MonoBehaviour
{
    public Camera targetCamera;

    void Start() => targetCamera = Camera.main;

    void LateUpdate()
    {
        if (!targetCamera) targetCamera = Camera.main;
        transform.forward = targetCamera.transform.forward;
    }

}
