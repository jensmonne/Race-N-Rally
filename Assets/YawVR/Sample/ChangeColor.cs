using UnityEngine;
using YawVR;


/* Change color based on device state */
public class ChangeColor : MonoBehaviour
{
    [SerializeField]
    private Color stoppedColor;
    [SerializeField]
    private Color startedColor;


    public void StateChanged(DeviceState state) {
        switch (state) {
            case DeviceState.Stopped:
                this.gameObject.GetComponent<Renderer>().material.color = stoppedColor;
                break;
            case DeviceState.Started:
                this.gameObject.GetComponent<Renderer>().material.color = startedColor;
                break;
        }
    }
}
