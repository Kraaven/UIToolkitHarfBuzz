using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class TextAnimator : MonoBehaviour
{
    
    private WaitForSeconds animationDelay = new WaitForSeconds(0.02f);

    public string AnimatedText;
    
    private HbTextRenderer textRenderer;
    
    IEnumerator Start()
    {
        textRenderer = GetComponent<HbTextRenderer>();

        for (int i = 0; i < AnimatedText.Length; i++)
        {
            textRenderer.RenderText = AnimatedText.Substring(0, i);
            yield return animationDelay;
        }
    }
    

    // Update is called once per frame
    void Update()
    {
        
    }
}
