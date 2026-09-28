using UnityEngine;
using UnityEngine.XR;

public class SpectatorSetup : MonoBehaviour
{
    private void Start()
    {
        XRSettings.gameViewRenderMode = GameViewRenderMode.None;
    }
}