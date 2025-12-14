using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Unity.Profiling;
using UnityEngine;

public class HbLanguageConfigurator : MonoBehaviour
{
    #region Instance
    public static HbLanguageConfigurator Instance;
    public static HbLanguageConfig mainLangConfig;
    private void InitInstance()
    {
        if(Instance == null) Instance = this;
        else Destroy(this);
    }
    

    #endregion
    
    public List<HbLanguageConfig> languageConfigs = new();
    public HashSet<HbTextRenderer> textRenderers = new();
    private void Awake()
    {
        Debug.Log(Path.Combine(Application.persistentDataPath, "HbLangConfigs"));
        
       InitInstance();
       Directory.CreateDirectory(Path.Combine(Application.persistentDataPath, "HbLangConfigs"));
       languageConfigs.ForEach((config) => config.InitFont());
       if(languageConfigs.Count > 0) mainLangConfig = languageConfigs[0];
    }

    public void RegisterTextRenderer(HbTextRenderer renderer) => textRenderers.Add(renderer);
    public void UnregisterTextRenderer(HbTextRenderer renderer) => textRenderers.Remove(renderer);

    public void SetLanguage(string lang)
    {
        var langConfig = languageConfigs.FirstOrDefault(config => config.languageCode == lang);
        if(langConfig != null) mainLangConfig = langConfig;
        foreach (var hbTextRenderer in textRenderers)
        {
            hbTextRenderer.UpdateRuntimeText();
        }
    }

    public void OnDestroy()
    {
        
    }
}
