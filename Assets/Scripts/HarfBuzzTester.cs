
// THIS CODE IS ONLY FOR REFERENCE, SINCE I KNOW IT WORKS

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using HarfBuzzSharp;
using SkiaSharp;
using UnityEngine;

public class HarfBuzzDirectRenderer : MonoBehaviour
{
    // --- Configuration Flags ---
    private const bool PRINT_LOGS = true;
    private const bool GENERATE_DEBUG_PNG = true;

    // --- Font & Atlas Constants ---
    public string TEXT_TO_RENDER = "";
    public string FONT_FILENAME = "Tamil.ttf";
    private const string CACHE_TEX_FILENAME = "font_atlas_all.bytes";
    private const string CACHE_META_FILENAME = "font_meta_all.bin";
    private const string DEBUG_PNG_FILENAME = "font_atlas_ALL_GLYPHS.png";

    private string cachePath;

    // --- Font & System Fields ---
    private byte[] fontData;
    private Face fontFace;
    private SKTypeface skTypeface;
    private SKFont skFont;

    // --- Atlas Fields ---
    private int atlasSize = 2048;
    private int currentX = 0;
    private int currentY = 0;
    private int rowHeight = 0;
    private int padding = 4;
    private const int RASTER_SAFETY_MARGIN = 2;
    private const float UnitsPerPixel = 64f; // matches your original scaling convention

    private Texture2D atlasTexture;
    private Dictionary<uint, GlyphAtlasInfo> glyphAtlas = new Dictionary<uint, GlyphAtlasInfo>();

    // --- Internal Data Structures ---
    private class GlyphAtlasInfo
    {
        public Rect uvRect;
        public Vector2 size;
        public Vector2 bearing; // stored in font units => converted to world units when used
        public uint glyphId;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct GlyphMetadata
    {
        public uint glyphId;
        public float uv_x, uv_y, uv_w, uv_h;
        public float size_x, size_y;
        public float bearing_x, bearing_y;
    }

    void Start()
    {
        StringBuilder log = new StringBuilder();
        if (PRINT_LOGS) log.AppendLine("--- HarfBuzzDirectRenderer START ---");

        cachePath = Application.persistentDataPath;
        print(cachePath);

        string fontPath = Path.Combine(Application.streamingAssetsPath, FONT_FILENAME);
        if (!File.Exists(fontPath))
        {
            Debug.LogError($"CRITICAL: Font file not found at: {fontPath}");
            return;
        }

        try { fontData = File.ReadAllBytes(fontPath); }
        catch (Exception e) { Debug.LogError($"CRITICAL: Failed to load font data: {e.Message}"); return; }

        // Create HarfBuzz Face from blob (free memory after blob)
        IntPtr ptr = Marshal.AllocHGlobal(fontData.Length);
        Marshal.Copy(fontData, 0, ptr, fontData.Length);
        using (var blob = new Blob(ptr, fontData.Length, MemoryMode.ReadOnly, () => Marshal.FreeHGlobal(ptr)))
        {
            fontFace = new Face(blob, 0);
        }

        skTypeface = SKTypeface.FromData(SKData.CreateCopy(fontData));
        if (fontFace == null || skTypeface == null)
        {
            Debug.LogError("CRITICAL: HarfBuzz or SkiaSharp setup failed.");
            return;
        }

        if (PRINT_LOGS) log.AppendLine("Font loaded and HarfBuzz/SkiaSharp setup complete.");

        float renderTextSize = 64f;
        skFont = new SKFont(skTypeface, renderTextSize);
        var hbFont = new HarfBuzzSharp.Font(fontFace);
        hbFont.SetScale((int)(renderTextSize * UnitsPerPixel), (int)(renderTextSize * UnitsPerPixel));

        // Build or load atlas
        if (!TryLoadAtlasBinary(log))
        {
            if (PRINT_LOGS) log.AppendLine("Cache not found or failed to load. Generating full static atlas for ALL glyphs...");

            HashSet<uint> allGlyphIds = GetAllGlyphIds(skTypeface, log);
            atlasTexture = new Texture2D(atlasSize, atlasSize, TextureFormat.Alpha8, false);
            Color32[] clearColors = new Color32[atlasSize * atlasSize];
            for (int i = 0; i < clearColors.Length; i++) clearColors[i] = new Color32(0, 0, 0, 0);
            atlasTexture.SetPixels32(clearColors);

            RasterizeRequiredGlyphsToAtlas(hbFont, allGlyphIds, log);

            atlasTexture.Apply();
            SaveAtlasBinary(log);
        }
        else
        {
            if (PRINT_LOGS) log.AppendLine("Atlas loaded from binary cache successfully.");
        }

        // Shape text
        var buffer = new HarfBuzzSharp.Buffer();
        buffer.AddUtf16(TEXT_TO_RENDER);
        buffer.Direction = Direction.LeftToRight;
        buffer.Script = Script.Tamil;
        buffer.Language = new Language("ta");
        hbFont.Shape(buffer);

        var infos = buffer.GlyphInfos;
        var positions = buffer.GlyphPositions;

        if (PRINT_LOGS)
        {
            log.AppendLine("--- Shaped Text Analysis (TEST TEXT: " + TEXT_TO_RENDER + ") ---");
            for (int i = 0; i < infos.Length; i++)
            {
                uint glyphId = infos[i].Codepoint;
                float xAdv = positions[i].XAdvance / UnitsPerPixel * 0.01f;
                float yAdv = positions[i].YAdvance / UnitsPerPixel * 0.01f;
                float xOff = positions[i].XOffset / UnitsPerPixel * 0.01f;
                float yOff = positions[i].YOffset / UnitsPerPixel * 0.01f;
                string glyphName = "Glyph ID " + glyphId;
                if (Mathf.Abs(positions[i].XAdvance) < 1.0f && Mathf.Abs(positions[i].YAdvance) < 1.0f && (positions[i].XOffset != 0 || positions[i].YOffset != 0))
                {
                    glyphName += " (POSSIBLE MARK)";
                }
                log.AppendLine($"[{i}] {glyphName}: Adv ({xAdv:F4}, {yAdv:F4}), Off ({xOff:F4}, {yOff:F4})");
                if (glyphAtlas.ContainsKey(glyphId))
                {
                    var atlasInfo = glyphAtlas[glyphId];
                    log.AppendLine($"   -> Atlas Info: Size ({atlasInfo.size.x}, {atlasInfo.size.y}), Bearing ({atlasInfo.bearing.x:F4}, {atlasInfo.bearing.y:F4}), UV ({atlasInfo.uvRect.xMin:F4}, {atlasInfo.uvRect.yMin:F4})");
                }
                else
                {
                    log.AppendLine($"   -> Atlas Missing: Glyph ID {glyphId} not found in atlas cache. CRITICAL FAILURE.");
                }
            }
            log.AppendLine("-------------------------------------------");
        }

        var mesh = BuildMesh(infos, positions, log);

        var material = new Material(Shader.Find("UI/Default"));
        material.mainTexture = atlasTexture;

        if (GetComponent<MeshRenderer>() == null) gameObject.AddComponent<MeshRenderer>();
        if (GetComponent<MeshFilter>() == null) gameObject.AddComponent<MeshFilter>();

        GetComponent<MeshRenderer>().material = material;
        GetComponent<MeshFilter>().mesh = mesh;

        buffer?.Dispose();
        hbFont?.Dispose();

        if (PRINT_LOGS)
        {
            log.AppendLine($"Rendering setup complete.");
            log.AppendLine("--- HarfBuzzDirectRenderer END ---");
            Debug.Log(log.ToString());
        }
    }

    // --- UTILITY: Get all unique glyph IDs from the font ---
    private HashSet<uint> GetAllGlyphIds(SKTypeface typeface, StringBuilder log)
    {
        HashSet<uint> allGlyphIds = new HashSet<uint>();
        int totalGlyphs = typeface.GlyphCount;
        // start from 0 (glyph 0 may be .notdef) but include all to be safe
        for (uint id = 0; id < totalGlyphs; id++) { allGlyphIds.Add(id); }
        if (PRINT_LOGS) log.AppendLine($"INFO: Total glyphs in font: {totalGlyphs}. Raster queue contains {allGlyphIds.Count} glyphs.");
        return allGlyphIds;
    }

    // --- EFFICIENT BINARY CACHING METHODS ---
    private bool TryLoadAtlasBinary(StringBuilder log)
    {
        string imagePath = Path.Combine(cachePath, CACHE_TEX_FILENAME);
        string metadataPath = Path.Combine(cachePath, CACHE_META_FILENAME);
        if (!File.Exists(imagePath) || !File.Exists(metadataPath)) return false;

        try
        {
            byte[] textureBytes = File.ReadAllBytes(imagePath);
            atlasTexture = new Texture2D(atlasSize, atlasSize, TextureFormat.Alpha8, false);
            atlasTexture.LoadRawTextureData(textureBytes);
            atlasTexture.Apply();

            using (FileStream fs = new FileStream(metadataPath, FileMode.Open))
            using (BinaryReader reader = new BinaryReader(fs))
            {
                int count = reader.ReadInt32();
                int sizeOfStruct = Marshal.SizeOf<GlyphMetadata>();
                glyphAtlas.Clear();

                for (int i = 0; i < count; i++)
                {
                    byte[] buffer = reader.ReadBytes(sizeOfStruct);
                    GCHandle handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
                    GlyphMetadata metadata = (GlyphMetadata)Marshal.PtrToStructure(handle.AddrOfPinnedObject(), typeof(GlyphMetadata));
                    handle.Free();

                    glyphAtlas[metadata.glyphId] = new GlyphAtlasInfo
                    {
                        glyphId = metadata.glyphId,
                        uvRect = new Rect(metadata.uv_x, metadata.uv_y, metadata.uv_w, metadata.uv_h),
                        size = new Vector2(metadata.size_x, metadata.size_y),
                        bearing = new Vector2(metadata.bearing_x, metadata.bearing_y)
                    };
                }
            }
            return true;
        }
        catch (Exception e)
        {
            Debug.LogError($"WARNING: Failed to load atlas from binary cache: {e.Message}");
            if (File.Exists(imagePath)) File.Delete(imagePath);
            if (File.Exists(metadataPath)) File.Delete(metadataPath);
            return false;
        }
    }

    private void SaveAtlasBinary(StringBuilder log)
    {
        try
        {
            // 1. Save Texture (Raw Data)
            byte[] textureBytes = atlasTexture.GetRawTextureData();
            File.WriteAllBytes(Path.Combine(cachePath, CACHE_TEX_FILENAME), textureBytes);

            // 2. Save Debug PNG
            if (GENERATE_DEBUG_PNG)
            {
                Texture2D debugTex = new Texture2D(atlasSize, atlasSize, TextureFormat.RGBA32, false);
                Color[] alphaPixels = atlasTexture.GetPixels();
                Color[] debugPixels = new Color[alphaPixels.Length];
                for (int i = 0; i < alphaPixels.Length; i++)
                {
                    float a = alphaPixels[i].a;
                    debugPixels[i] = new Color(a, a, a, 1f);
                }
                debugTex.SetPixels(debugPixels);
                debugTex.Apply();

                byte[] pngBytes = debugTex.EncodeToPNG();
                File.WriteAllBytes(Path.Combine(cachePath, DEBUG_PNG_FILENAME), pngBytes);
                Destroy(debugTex);
                if (PRINT_LOGS) log.AppendLine($"INFO: Debug PNG saved to: {Path.Combine(cachePath, DEBUG_PNG_FILENAME)}");
            }

            // 3. Save Metadata (Binary)
            using (FileStream fs = new FileStream(Path.Combine(cachePath, CACHE_META_FILENAME), FileMode.Create))
            using (BinaryWriter writer = new BinaryWriter(fs))
            {
                writer.Write(glyphAtlas.Count);
                int sizeOfStruct = Marshal.SizeOf<GlyphMetadata>();

                foreach (var pair in glyphAtlas)
                {
                    GlyphMetadata metadata = new GlyphMetadata
                    {
                        glyphId = pair.Key,
                        uv_x = pair.Value.uvRect.x, uv_y = pair.Value.uvRect.y,
                        uv_w = pair.Value.uvRect.width, uv_h = pair.Value.uvRect.height,
                        size_x = pair.Value.size.x, size_y = pair.Value.size.y,
                        bearing_x = pair.Value.bearing.x, bearing_y = pair.Value.bearing.y
                    };

                    byte[] buffer = new byte[sizeOfStruct];
                    GCHandle handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
                    Marshal.StructureToPtr(metadata, handle.AddrOfPinnedObject(), false);
                    handle.Free();
                    writer.Write(buffer);
                }
            }
            if (PRINT_LOGS) log.AppendLine($"INFO: Atlas successfully saved in binary format to: {cachePath}");
        }
        catch (Exception e)
        {
            Debug.LogError($"ERROR: Failed to save atlas in binary format: {e.Message}");
        }
    }

    // --- RASTERIZATION (place glyph path using HarfBuzz extents; do NOT center) ---
    void RasterizeRequiredGlyphsToAtlas(HarfBuzzSharp.Font hbFont, HashSet<uint> requiredGlyphIds, StringBuilder log)
    {
        if (PRINT_LOGS) log.AppendLine($"INFO: Rasterizing {requiredGlyphIds.Count} required glyphs...");

        foreach (uint glyphId in requiredGlyphIds)
        {
            if (!hbFont.TryGetGlyphExtents(glyphId, out var extents))
            {
                if (PRINT_LOGS) log.AppendLine($"WARNING: HarfBuzz failed to get extents for glyph ID {glyphId}. Skipping.");
                continue;
            }

            // --- Dimension Calculation with Safety Margin ---
            int baseWidth = (int)Math.Ceiling((extents.XBearing + extents.Width) / UnitsPerPixel);
            int baseHeight = (int)Math.Ceiling(Math.Abs(extents.Height) / UnitsPerPixel);

            int glyphWidth = baseWidth + (padding * 2) + RASTER_SAFETY_MARGIN;
            int glyphHeight = baseHeight + (padding * 2) + RASTER_SAFETY_MARGIN;

            // DOT FIX CHECK
            int minRenderSize = 2;
            bool isZeroSized = (glyphWidth <= padding * 2 + RASTER_SAFETY_MARGIN) || (glyphHeight <= padding * 2 + RASTER_SAFETY_MARGIN);

            if (isZeroSized)
            {
                glyphWidth = Mathf.Max(glyphWidth, minRenderSize + padding * 2 + RASTER_SAFETY_MARGIN);
                glyphHeight = Mathf.Max(glyphHeight, minRenderSize + padding * 2 + RASTER_SAFETY_MARGIN);
            }

            // Atlas Packing Logic
            if (currentX + glyphWidth > atlasSize)
            {
                currentX = 0;
                currentY += rowHeight;
                rowHeight = 0;
            }
            if (currentY + glyphHeight > atlasSize)
            {
                Debug.LogError($"CRITICAL: Atlas full at glyph ID {glyphId}. Increase atlasSize!");
                break;
            }

            ushort ushortGlyphId = (ushort)glyphId;

            // Get path from Skia for this glyph
            using (var path = skFont.GetGlyphPath(ushortGlyphId))
            {
                if (path != null && !path.IsEmpty)
                {
                    // Create an alpha-only surface for the glyph
                    using (var surface = SKSurface.Create(new SKImageInfo(glyphWidth, glyphHeight, SKColorType.Alpha8)))
                    {
                        var canvas = surface.Canvas;
                        canvas.Clear(SKColors.Transparent);

                        using (var paint = new SKPaint { IsAntialias = true, Color = SKColors.White, Style = SKPaintStyle.Fill })
                        {
                            canvas.Save();

                            // -----------------------
                            // IMPORTANT: draw the path at the HarfBuzz origin
                            // extents.XBearing = distance from origin to left of glyph bounding box (in font units)
                            // extents.YBearing = distance from origin to top of glyph bounding box (in font units)
                            //
                            // We translate so that the HarfBuzz origin (0,0) maps to a consistent pixel position inside the glyph box:
                            // pxOriginX = padding + (-extents.XBearing / UnitsPerPixel)
                            // pxOriginY = padding + (extents.YBearing / UnitsPerPixel)
                            //
                            // NOTE: If the font/harfbuzz signs differ for your environment, you may need to flip the sign of Y here.
                            // -----------------------

                            float pxOriginX = padding + (-extents.XBearing / UnitsPerPixel);
                            float pxOriginY = padding + (extents.YBearing / UnitsPerPixel);

                            canvas.Translate(pxOriginX, pxOriginY);

                            if (PRINT_LOGS && isZeroSized) log.AppendLine($"   -> Glyph {glyphId} (likely mark): placed at pxOrigin ({pxOriginX:F2},{pxOriginY:F2})");

                            canvas.DrawPath(path, paint);

                            canvas.Restore();
                        }

                        // Copy pixels out of the SKSurface into atlasTexture
                        using (var image = surface.Snapshot())
                        using (var pixmap = image.PeekPixels())
                        {
                            IntPtr pixelPtr = pixmap.GetPixels();
                            // Note: pixmap rowBytes may be >= width; but SKPixmap for Alpha8 should be tightly packed; be defensive
                            int rowBytes = pixmap.RowBytes;
                            byte[] rowBuffer = new byte[rowBytes];

                            for (int y = 0; y < glyphHeight; y++)
                            {
                                // copy row by row (accounting for rowBytes)
                                Marshal.Copy(pixelPtr + y * rowBytes, rowBuffer, 0, rowBytes);
                                for (int x = 0; x < glyphWidth; x++)
                                {
                                    byte alpha = rowBuffer[x];
                                    int atlasX = currentX + x;
                                    int atlasY = currentY + y;
                                    atlasTexture.SetPixel(atlasX, atlasY, new Color(1, 1, 1, alpha / 255f));
                                }
                            }
                        }
                    }
                }
                else
                {
                    if (PRINT_LOGS) log.AppendLine($"WARNING: SkiaSharp returned empty path for glyph ID {glyphId}. Skipping.");
                    continue;
                }
            }

            // GLYPH ADDED TO ATLAS (store bearing using HarfBuzz extents)
            glyphAtlas[glyphId] = new GlyphAtlasInfo
            {
                glyphId = glyphId,
                uvRect = new Rect(currentX / (float)atlasSize, currentY / (float)atlasSize, glyphWidth / (float)atlasSize, glyphHeight / (float)atlasSize),
                size = new Vector2(glyphWidth, glyphHeight),
                // store the raw extents bearings (in font units -> convert to pixels/units when used)
                bearing = new Vector2(extents.XBearing / UnitsPerPixel, extents.YBearing / UnitsPerPixel)
            };

            currentX += glyphWidth;
            rowHeight = Mathf.Max(rowHeight, glyphHeight);
        }

        if (PRINT_LOGS) log.AppendLine($"INFO: Rasterization complete. {glyphAtlas.Count} glyphs are in the atlas.");
    }

    // --- MESH BUILDING (use bearing stored from extents; do not apply padding offsets to world placement) ---
    Mesh BuildMesh(GlyphInfo[] infos, GlyphPosition[] positions, StringBuilder log)
    {
        Mesh mesh = new Mesh();
        List<Vector3> vertices = new List<Vector3>();
        List<Vector2> uvs = new List<Vector2>();
        List<int> triangles = new List<int>();

        float penX = 0f;
        float penY = 0f;
        float scale = 0.01f; // matches your prior scaling from render units to Unity world units

        for (int i = 0; i < infos.Length; i++)
        {
            uint glyphId = infos[i].Codepoint;

            if (!glyphAtlas.ContainsKey(glyphId))
            {
                Debug.LogError($"FAILURE: Missing glyph ID {glyphId} in atlas. Mesh generation skipped for index: {i}");
                continue;
            }

            var atlasInfo = glyphAtlas[glyphId];
            var hbPos = positions[i];

            // HarfBuzz anchor point in world units (scale converts font->world)
            float startX = penX + (hbPos.XOffset / UnitsPerPixel * scale);
            float startY = penY + (hbPos.YOffset / UnitsPerPixel * scale);

            // Convert stored bearing (which was in font units / UnitsPerPixel) into world units
            float bearingX_world = atlasInfo.bearing.x * scale; // extents.XBearing / UnitsPerPixel * scale
            float bearingY_world = atlasInfo.bearing.y * scale; // extents.YBearing / UnitsPerPixel * scale

            // Quad Dimensions (in world units)
            float w = atlasInfo.size.x * scale;
            float h = atlasInfo.size.y * scale;

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
            float finalX = startX + bearingX_world;
            float finalY = startY + bearingY_world;

            // Add vertices (V0 Top-Left, V1 Top-Right, V2 Bottom-Right, V3 Bottom-Left)
            int idx = vertices.Count;
            vertices.Add(new Vector3(finalX, finalY, 0));            // V0
            vertices.Add(new Vector3(finalX + w, finalY, 0));        // V1
            vertices.Add(new Vector3(finalX + w, finalY - h, 0));    // V2 (y decreases for bottom)
            vertices.Add(new Vector3(finalX, finalY - h, 0));        // V3

            // UV (Note: atlas stored as (x,y,width,height) with (0,0) bottom-left)
            Rect uv = atlasInfo.uvRect;
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
            penX += positions[i].XAdvance / UnitsPerPixel * scale;
            penY += positions[i].YAdvance / UnitsPerPixel * scale;
        }

        mesh.SetVertices(vertices);
        mesh.SetUVs(0, uvs);
        mesh.SetTriangles(triangles, 0);
        mesh.RecalculateBounds();

        if (PRINT_LOGS) log.AppendLine($"INFO: Mesh built successfully with {vertices.Count} vertices and {triangles.Count / 3} quads.");

        return mesh;
    }

    void OnDestroy()
    {
        fontFace?.Dispose();
        skTypeface?.Dispose();
        skFont?.Dispose();
        if (atlasTexture != null)
        {
            Destroy(atlasTexture);
        }
    }
}
