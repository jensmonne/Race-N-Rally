using UnityEngine;
using UnityEngine.Events;
using UnityEngine.InputSystem;

[CreateAssetMenu(fileName = "InputReader", menuName = "Architecture/Input Reader")]
public class InputReader : ScriptableObject, InputSystem_Actions.IPlayerActions, InputSystem_Actions.IUIActions
{
    private InputSystem_Actions inputActions;

    private void OnEnable()
    {
        if (inputActions == null)
        {
            inputActions = new InputSystem_Actions();
            inputActions.Player.SetCallbacks(this);
            inputActions.UI.SetCallbacks(this);
        }

        EnablePlayerInput();
    }

    private void OnDisable() => DisableAllInput();

    public void EnablePlayerInput()
    {
        inputActions.UI.Disable();
        inputActions.Player.Enable();
    }

    public void EnableUIInput()
    {
        inputActions.Player.Disable();
        inputActions.UI.Enable();
    }

    public void DisableAllInput()
    {
        inputActions.Player.Disable();
        inputActions.UI.Disable();
    }

        // UI Actions
    public void OnNavigate(InputAction.CallbackContext context) { }

    public void OnSubmit(InputAction.CallbackContext context) { }

    public void OnCancel(InputAction.CallbackContext context) { }

    public void OnPoint(InputAction.CallbackContext context) { }

    public void OnClick(InputAction.CallbackContext context) { }

    public void OnRightClick(InputAction.CallbackContext context) { }

    public void OnMiddleClick(InputAction.CallbackContext context) { }

    public void OnScrollWheel(InputAction.CallbackContext context) { }

    public void OnTrackedDevicePosition(InputAction.CallbackContext context) { }

    public void OnTrackedDeviceOrientation(InputAction.CallbackContext context) { }
}