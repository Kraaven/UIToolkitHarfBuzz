using System;
using System.Runtime.InteropServices;
using UnityEngine;

[Serializable]
public partial class HbLanguageConfig
{
    //Visible in Inspector
    
    [Header("Language Settings")] [Tooltip("The 4 byte code that represents the Language")]
    public string languageCode = "en";

    [Tooltip("The Script rules that the engine uses")]
    public string languageScriptName;
    
    [Tooltip("The Direction the engine draws the language")]
    public HarfBuzzSharp.Direction languageDirection;

    [Header("Language Assets")] 
    [Tooltip("Language Font File Path within Streaming Assets")]public string languageFontAssetPath = "";

    [Header("Language Render Settings")] 
    public AtlasSize atlasSize = AtlasSize.Q2048;
    public FontRenderSize fontRenderSize = FontRenderSize.S64;
}

public enum AtlasSize
{
    Q1024 = 1024,
    Q2048 = 2048,
    Q4096 = 4096
}

public enum FontRenderSize
{
    S32 = 32,
    S64 = 64,
    S128 = 128
}

public class GlyphAtlasInfo
{
    public Rect uvRect;
    public Vector2 size;
    public Vector2 bearing; // stored in font units => converted to world units when used
    public uint glyphId;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct GlyphMetadata
{
    public uint glyphId;
    public float uv_x, uv_y, uv_w, uv_h;
    public float size_x, size_y;
    public float bearing_x, bearing_y;
}