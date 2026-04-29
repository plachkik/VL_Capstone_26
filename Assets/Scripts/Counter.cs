using System.Collections.Generic;
using TMPro;
using UnityEngine;

[RequireComponent(typeof(TMP_Text))]
public class Counter : MonoBehaviour
{
    private TMP_Text counterText;
    private readonly HashSet<string> detectedIds = new HashSet<string>();

    private void Awake()
    {
        counterText = GetComponent<TMP_Text>();
        // counterText.enabled = false;
    }

    public void UpdateCount(int count)
    {
        counterText.text = $"Tools detected: {count}";
        // counterText.enabled = true;
    }

    public void UpdateText(string text)
    {
        counterText.text = text;
    }

    public void Clear()
    {
        // counterText.enabled = false;
    }
}
