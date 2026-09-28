using UnityEngine;
using TMPro;
using UnityEngine.UI;

public class BrakePoints : MonoBehaviour
{
    [SerializeField] private TextMeshProUGUI brakePointText;
    [SerializeField] private Image brakePointImage;

    private bool isPlayerInBrakePoint = false;
    
    private void Start()
    {
        brakePointText.text = "";
        brakePointImage.color = Color.clear;
    }

    private void Update()
    {
        if (isPlayerInBrakePoint)
        {
            brakePointText.text = "Brake!";
            brakePointImage.color = Color.red;
        }
    }

    private void OnTriggerEnter(Collider other)
    {
        if (other.CompareTag("Player"))
        {
            isPlayerInBrakePoint = true;
        }
    }

    private void OnTriggerExit(Collider other)
    {
        if (other.CompareTag("Player"))
        {
            isPlayerInBrakePoint = false;
            brakePointText.text = "";
            brakePointImage.color = Color.clear;
        }
    }
}
