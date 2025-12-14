using System;
using System.Collections.Generic;
using HarfBuzzSharp;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Profiling;
using Buffer = HarfBuzzSharp.Buffer;

[RequireComponent(typeof(MeshRenderer))]
[RequireComponent(typeof(MeshFilter))]
public class HbTextRenderer : MonoBehaviour
{
    private MeshRenderer meshRenderer;
    private MeshFilter meshFilter;
    private Buffer textBuffer = new();
    private HbLanguageConfig langConfig => HbLanguageConfigurator.mainLangConfig;

    [SerializeField] private string rendererText;

    public string RenderText {
        set
        {
            rendererText = value;
            UpdateRuntimeText();
        }
        get => rendererText; }

    private void Awake()
    {
        meshRenderer = gameObject.GetComponent<MeshRenderer>();
        meshFilter = gameObject.GetComponent<MeshFilter>();
    }

    private void Start()
    {
        HbLanguageConfigurator.Instance.RegisterTextRenderer(this);
        UpdateRuntimeText();
    }

    private void SetBufferSettings()
    {
        textBuffer.ClearContents();
        textBuffer.Direction = langConfig.languageDirection;
        textBuffer.Script = langConfig.languageScript;
        textBuffer.Language = langConfig.hbLanguage;
        meshRenderer.material = langConfig.hbLangMaterial;
    }

    public void UpdateRuntimeText()
    {
        SetBufferSettings();
        ShapeAndRenderText();
    }

    public void ShapeAndRenderText()
    {
        textBuffer.AddUtf16(rendererText);
        langConfig.hbFont.Shape(textBuffer);
        
        var infos = textBuffer.GlyphInfos;
        var positions = textBuffer.GlyphPositions;

        meshFilter.mesh = BuildMesh(infos, positions);
    }

    private readonly List<Vector3> vertices = new();
    private readonly List<Vector2> uvs = new();
    private readonly List<int> triangles = new();

    private Mesh BuildMesh(GlyphInfo[] infos, GlyphPosition[] positions)
    {
        var mesh = new Mesh();

        vertices.Clear();
        uvs.Clear();
        triangles.Clear();

        var penX = 0f;
        var penY = 0f;
        var scale = 0.01f;

        for (var i = 0; i < infos.Length; i++)
        {
            var glyphId = infos[i].Codepoint;

            if (!langConfig.glyphAtlas.ContainsKey(glyphId))
            {
                Debug.LogError($"FAILURE: Missing glyph ID {glyphId} in atlas. Mesh generation skipped for index: {i}");
                continue;
            }

            var atlasInfo = langConfig.glyphAtlas[glyphId];
            var hbPos = positions[i];

            // HarfBuzz anchor point in world units (scale converts font->world)
            var startX = penX + hbPos.XOffset / langConfig.unitsPerPixel * scale;
            var startY = penY + hbPos.YOffset / langConfig.unitsPerPixel * scale;

            // Convert stored bearing (which was in font units / UnitsPerPixel) into world units
            var bearingX_world = atlasInfo.bearing.x * scale; // extents.XBearing / UnitsPerPixel * scale
            var bearingY_world = atlasInfo.bearing.y * scale; // extents.YBearing / UnitsPerPixel * scale

            // Quad Dimensions (in world units)
            var w = atlasInfo.size.x * scale;
            var h = atlasInfo.size.y * scale;

            // FINAL POSITION of the quad (Top-left)
            // We want the HarfBuzz origin (the anchor point used by HarfBuzz offsets) to map to the
            // correct spot inside the glyph quad. Since during rasterization we placed the HarfBuzz
            // origin at pixel position:
            //   pxOriginX = padding + (-extents.XBearing / UnitsPerPixel)
            //   pxOriginY = padding + (extents.YBearing / UnitsPerPixel)
            //
            // The top-left of the quad in world units is then:
            //   topLeftX = startX + bearingX_world
            //   topLeftY = startY + bearingY_world
            //
            // (No subtraction of padding/safety here. Padding is only for atlas packing.)
            var finalX = startX + bearingX_world;
            var finalY = startY + bearingY_world;

            // Add vertices (V0 Top-Left, V1 Top-Right, V2 Bottom-Right, V3 Bottom-Left)
            var idx = vertices.Count;
            vertices.Add(new Vector3(finalX, finalY, 0)); // V0
            vertices.Add(new Vector3(finalX + w, finalY, 0)); // V1
            vertices.Add(new Vector3(finalX + w, finalY - h, 0)); // V2 (y decreases for bottom)
            vertices.Add(new Vector3(finalX, finalY - h, 0)); // V3

            // UV (Note: atlas stored as (x,y,width,height) with (0,0) bottom-left)
            var uv = atlasInfo.uvRect;
            uvs.Add(new Vector2(uv.xMin, uv.yMin)); // top-left -> uv bottom-left because Unity origin differences
            uvs.Add(new Vector2(uv.xMax, uv.yMin));
            uvs.Add(new Vector2(uv.xMax, uv.yMax));
            uvs.Add(new Vector2(uv.xMin, uv.yMax));

            // Triangles
            triangles.Add(idx);
            triangles.Add(idx + 1);
            triangles.Add(idx + 2);
            triangles.Add(idx + 2);
            triangles.Add(idx + 3);
            triangles.Add(idx);

            // Advance pen
            penX += positions[i].XAdvance / langConfig.unitsPerPixel * scale;
            penY += positions[i].YAdvance / langConfig.unitsPerPixel * scale;
        }

        mesh.SetVertices(vertices);
        mesh.SetUVs(0, uvs);
        mesh.SetTriangles(triangles, 0);
        mesh.RecalculateBounds();
        return mesh;
    }

    public void OnDestroy()
    {
        DestroyResources();
        HbLanguageConfigurator.Instance.UnregisterTextRenderer(this);
    }

    public void DestroyResources()
    {
        textBuffer?.Dispose();
    }
}