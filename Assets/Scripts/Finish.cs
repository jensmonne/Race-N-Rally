using UnityEngine;

public class Finish : MonoBehaviour
{
    private static Finish instance;
    private float finishTime;

        private void Awake()
    {
        if (instance == null)
        {
            instance = this;
            DontDestroyOnLoad(gameObject);
        }
        else
        {
            Destroy(gameObject);
        }
    }

    private void OnTriggerEnter(Collider other)
    {
        if (other.CompareTag("Player"))
        {
            finishTime = Timer.currentTime;
            if (finishTime < GameManager.BestTime || GameManager.BestTime == 0f)
            {
                GameManager.instance.UpdateBestTime(finishTime);
            }
            Timer.ResetTimer();
                
        }
    }
}
