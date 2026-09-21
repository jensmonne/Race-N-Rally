using UnityEngine;
using TMPro;

public class Finish : MonoBehaviour
{
    private float finishTime;
    private float bestTime;

    [SerializeField] private TextMeshProUGUI BestTimeText;

    private void OnTriggerEnter(Collider other)
    {
        if (other.CompareTag("Player"))
        {
            Debug.Log("Finish line crossed!");
            finishTime = Timer.currentTime;
            if (finishTime < bestTime || bestTime == 0f)
            {
                bestTime = finishTime;
                bestTime = Mathf.Round(bestTime * 100f) / 100f;
                BestTimeText.text = $"Best Time: {bestTime:F2}";
            }
            Timer.ResetTimer();
        }
    }
}
