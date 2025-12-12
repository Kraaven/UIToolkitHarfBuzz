using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using HarfBuzzSharp;
using TMPro;
using UnityEngine;
using File = System.IO.File;


public class HarfBuzzTester : MonoBehaviour
{
    private byte[] fontData; 
    private Face fontFace;
    public TMP_FontAsset fontAsset;

    private string TEXT = "வணக\u0bcdகம\u0bcd";
    

    void Start()
    {
        fontData = File.ReadAllBytes(Path.Combine(Application.streamingAssetsPath, "Tamil.ttf"));
        // fontData = fontAsset.sourceFontFile.
        
        if (fontData != null)
        {
            
            IntPtr ptr = Marshal.AllocHGlobal(fontData.Length);
            Marshal.Copy(fontData, 0, ptr, fontData.Length);
            
            using (var blob = new Blob(
                       ptr,
                       fontData.Length,
                       MemoryMode.ReadOnly,
                       () => Marshal.FreeHGlobal(ptr)
                   ))
            {
                fontFace = new Face(blob, 0);
            }
            
            if(fontFace != null) print("Font loaded");
            
            var hbFont = new HarfBuzzSharp.Font(fontFace);
            hbFont.SetScale(2048, 2048);
            
            var buffer = new HarfBuzzSharp.Buffer();
            buffer.AddUtf16("வணக\u0bcdகம\u0bcd");
            buffer.Direction = Direction.LeftToRight;
            buffer.Script = Script.Tamil;
            buffer.Language = new Language("ta");
            
            hbFont.Shape(buffer);
            
            var infos = buffer.GlyphInfos;
            var positions = buffer.GlyphPositions;
            
            
            if (infos.Length == 0)
            {
                Debug.LogError("HarfBuzz shaping failed: no glyphs produced.");
            }

            bool allNotDef = true;
            for (int i = 0; i < infos.Length; i++)
            {
                if (infos[i].Codepoint != 0)
                {
                    allNotDef = false;
                    break;
                }
            }

            if (allNotDef)
            {
                Debug.LogError("HarfBuzz shaping failed: font does not support these characters.");
                return;
            }
            else
            {
                Debug.Log("HarfBuzz shaping OK!");
            }

            var mesh = BuildMesh(infos, positions, fontAsset);
            
            
            var fontMaterialInstance = new Material(fontAsset.material);
            gameObject.GetComponent<MeshRenderer>().material = fontMaterialInstance;
            fontMaterialInstance.SetTexture("_MainTex", fontAsset.atlasTexture);
            
            fontMaterialInstance.SetFloat("_GradientScale", fontAsset.atlasPopulationMode == AtlasPopulationMode.Dynamic ? fontAsset.atlasPadding : 10);
            // fontMaterialInstance.SetTexture("_FaceTex", fontAsset.atlasTexture); // This makes the Font go invisible. I tried this in the original TMP shader as well
            fontMaterialInstance.SetFloat("_ScaleRatioA", 1f);
            fontMaterialInstance.SetFloat("_ScaleRatioB", 1f);
            fontMaterialInstance.SetFloat("_ScaleRatioC", 1f);
            fontMaterialInstance.SetFloat("_FaceDilate", 0);
            
            mesh.RecalculateBounds();
            
            gameObject.GetComponent<MeshFilter>().mesh = mesh;


        }
        else
        {
            print("Font not found");
        }
    }
    
    void OnDestroy()
    {
        fontFace?.Dispose();
    }

    // Update is called once per frame
    void Update()
    {
        
    }
    
    public Mesh BuildMesh(GlyphInfo[] infos, GlyphPosition[] pos, TMP_FontAsset font)
{
    Mesh mesh = new Mesh();

    List<Vector3> vertices = new();
    List<Vector2> uv = new();
    List<int> triangles = new();

    float penX = 0f;
    float penY = 0f;

    float scale = font.faceInfo.scale;   // IMPORTANT!

    for (int i = 0; i < infos.Length; i++)
    {
        uint glyphId = infos[i].Codepoint;
        var hbPos = pos[i];

        TMP_Character ch = FindCharacterByCluster(font, TEXT, infos[i].Cluster);
        if (ch == null) continue;

        var glyph = ch.glyph;
        var m = glyph.metrics;
        var rect = glyph.glyphRect;

        // -------- POSITIONING (scaled into Unity space) --------
        float bearingX = m.horizontalBearingX * scale;
        float bearingY = m.horizontalBearingY * scale;

        float width  = m.width  * scale;
        float height = m.height * scale;

        float x0 = penX + (bearingX + hbPos.XOffset / 64f * scale);
        float y0 = penY + (bearingY + hbPos.YOffset / 64f * scale);

        float x1 = x0 + width;
        float y1 = y0 - height;   // TMP quad goes downward

        int idx = vertices.Count;

        vertices.Add(new Vector3(x0, y0, 0));
        vertices.Add(new Vector3(x1, y0, 0));
        vertices.Add(new Vector3(x1, y1, 0));
        vertices.Add(new Vector3(x0, y1, 0));

        // -------- UVs (correct orientation) --------
        float u0 = rect.x / (float)font.atlasWidth;
        float u1 = (rect.x + rect.width) / (float)font.atlasWidth;

        // V is inverted for TMP SDF textures
        float v0 = 1f - ((rect.y + rect.height) / (float)font.atlasHeight);
        float v1 = 1f - (rect.y / (float)font.atlasHeight);

        uv.Add(new Vector2(u0, v1)); // top-left
        uv.Add(new Vector2(u1, v1)); // top-right
        uv.Add(new Vector2(u1, v0)); // bottom-right
        uv.Add(new Vector2(u0, v0)); // bottom-left

        triangles.Add(idx);
        triangles.Add(idx + 1);
        triangles.Add(idx + 2);

        triangles.Add(idx + 2);
        triangles.Add(idx + 3);
        triangles.Add(idx);

        // -------- Advance pen --------
        penX += hbPos.XAdvance / 64f * scale;
        penY += hbPos.YAdvance / 64f * scale;
    }

    mesh.SetVertices(vertices);
    mesh.SetUVs(0, uv);
    mesh.SetTriangles(triangles, 0);
    mesh.RecalculateBounds();

    return mesh;
}

    
    public TMP_Character FindCharacterByCluster(
        TMP_FontAsset font, 
        string text, 
        uint clusterIndex
    )
    {
        if (clusterIndex < 0 || clusterIndex >= text.Length)
            return null;
    
        uint unicode = text[(int)clusterIndex];

        // Lookup by Unicode (correct!)
        if (font.characterLookupTable.TryGetValue((uint)unicode, out TMP_Character ch))
            return ch;

        Debug.LogError($"TMP missing Unicode U+{unicode:X4}");
        return null;
    }

}
