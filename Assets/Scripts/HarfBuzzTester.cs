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
    private const bool PRINT_LOGS = true;           // Set to true to accumulate and print all logs at the end.
    private const bool GENERATE_DEBUG_PNG = true; 
    
    // --- Font & Atlas Constants ---
    private const string TEXT_TO_RENDER = "வணக்கம்";
    private const string FONT_FILENAME = "Tamil.ttf";
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
    private int atlasSize = 4096;
    private int currentX = 0;
    private int currentY = 0;
    private int rowHeight = 0;
    private int padding = 4;
    private const int RASTER_SAFETY_MARGIN = 2; // Extra pixels added to dimensions to prevent clipping
    private const float UnitsPerPixel = 64f; 

    private Texture2D atlasTexture;
    private Dictionary<uint, GlyphAtlasInfo> glyphAtlas = new Dictionary<uint, GlyphAtlasInfo>();
    
    // --- Internal Data Structures ---
    private class GlyphAtlasInfo
    {
        public Rect uvRect;
        public Vector2 size; 
        public Vector2 bearing; // Stores HarfBuzz XBearing/YBearing for spacing
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
        if (PRINT_LOGS) log.AppendLine("--- HarfBuzzDirectRenderer START (Log Buffer Initialized) ---");

        cachePath = Application.persistentDataPath;

        if (PRINT_LOGS) log.AppendLine($"HB asset files goes to : {cachePath}");
        
        // --- 1. Load font ---
        string fontPath = Path.Combine(Application.streamingAssetsPath, FONT_FILENAME);
        if (!File.Exists(fontPath))
        {
            Debug.LogError($"CRITICAL: Font file not found at: {fontPath}.");
            return;
        }
        try
        {
            fontData = File.ReadAllBytes(fontPath);
        }
        catch (Exception e)
        {
            Debug.LogError($"CRITICAL: Failed to load font data: {e.Message}");
            return;
        }
        
        // --- 2. Setup HarfBuzz/SkiaSharp ---
        IntPtr ptr = Marshal.AllocHGlobal(fontData.Length);
        Marshal.Copy(fontData, 0, ptr, fontData.Length);
        
        using (var blob = new Blob(ptr, fontData.Length, MemoryMode.ReadOnly, 
            () => Marshal.FreeHGlobal(ptr)))
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

        // --- 3. Initial Setup and Caching Logic ---
        float renderTextSize = 64f;
        skFont = new SKFont(skTypeface, renderTextSize); 
        
        var hbFont = new HarfBuzzSharp.Font(fontFace);
        hbFont.SetScale((int)(renderTextSize * UnitsPerPixel), (int)(renderTextSize * UnitsPerPixel)); 
        
        HarfBuzzSharp.Buffer buffer = null;
        GlyphInfo[] infos = null;
        GlyphPosition[] positions = null;
        
        if (TryLoadAtlasBinary(log)) 
        {
            if (PRINT_LOGS) log.AppendLine("Atlas loaded from efficient binary cache successfully.");
        }
        else
        {
            if (PRINT_LOGS) log.AppendLine("Cache not found or failed to load. Generating full static atlas for ALL glyphs...");

            // 4. Get ALL available glyph IDs from the font
            HashSet<uint> allGlyphIds = GetAllGlyphIds(skTypeface, log); 

            // 5. Create and rasterize the atlas
            atlasTexture = new Texture2D(atlasSize, atlasSize, TextureFormat.Alpha8, false);
            Color32[] clearColors = new Color32[atlasSize * atlasSize];
            for (int i = 0; i < clearColors.Length; i++)
                clearColors[i] = new Color32(0, 0, 0, 0);
            atlasTexture.SetPixels32(clearColors);
            
            RasterizeRequiredGlyphsToAtlas(hbFont, allGlyphIds, log); 
            
            atlasTexture.Apply();
            
            // 6. Save the newly generated atlas
            SaveAtlasBinary(log); 
        }
        
        // 7. Common Rendering Path (Shaping the actual text)
        buffer = new HarfBuzzSharp.Buffer(); 
        buffer.AddUtf16(TEXT_TO_RENDER);
        buffer.Direction = Direction.LeftToRight;
        buffer.Script = Script.Tamil;
        buffer.Language = new Language("ta");
        
        hbFont.Shape(buffer);
        
        infos = buffer.GlyphInfos;
        positions = buffer.GlyphPositions;
        
        // --- DEBUG BLOCK: Inspecting Pulli Data (retained for diagnostics) ---
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
                    glyphName += " (POSSIBLE PULLI/MARK)";
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

        // 8. Build mesh
        var mesh = BuildMesh(infos, positions, log);
        
        // 9. Setup rendering components
        var material = new Material(Shader.Find("UI/Default"));
        material.mainTexture = atlasTexture;
        
        if (GetComponent<MeshRenderer>() == null) gameObject.AddComponent<MeshRenderer>();
        if (GetComponent<MeshFilter>() == null) gameObject.AddComponent<MeshFilter>();
        
        GetComponent<MeshRenderer>().material = material;
        GetComponent<MeshFilter>().mesh = mesh;
        
        // 10. Cleanup
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

        for (uint id = 1; id < totalGlyphs; id++)
        {
            allGlyphIds.Add(id);
        }

        if (PRINT_LOGS) log.AppendLine($"INFO: Total unique glyphs found in font: {totalGlyphs}. Adding {allGlyphIds.Count} IDs to rasterization queue.");
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
                    GlyphMetadata metadata;
                    byte[] buffer = reader.ReadBytes(sizeOfStruct);
                    GCHandle handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
                    metadata = (GlyphMetadata)Marshal.PtrToStructure(handle.AddrOfPinnedObject(), typeof(GlyphMetadata));
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
            if (PRINT_LOGS) log.AppendLine($"INFO: Loaded {glyphAtlas.Count} glyph entries from cache.");
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

    // --- RASTERIZATION (FIXED: Unconditional Centering for ALL Glyphs) ---
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
            
            using (var path = skFont.GetGlyphPath(ushortGlyphId))
            {
                if (path != null && !path.IsEmpty)
                {
                    using (var surface = SKSurface.Create(new SKImageInfo(glyphWidth, glyphHeight, SKColorType.Alpha8)))
                    {
                        var canvas = surface.Canvas;
                        canvas.Clear(SKColors.Transparent);
                        
                        using (var paint = new SKPaint { IsAntialias = true, Color = SKColors.White, Style = SKPaintStyle.Fill })
                        {
                            canvas.Save();
                            
                            // 1. --- UNCONDITIONAL CENTERING FIX ---
                            // Aligns the center of the glyph's path to the center of the canvas.
                            path.GetBounds(out SKRect pathBounds);
                            
                            float canvasCenterX = glyphWidth / 2f;
                            float canvasCenterY = glyphHeight / 2f;
                            
                            float dx = canvasCenterX - pathBounds.MidX;
                            float dy = canvasCenterY - pathBounds.MidY;

                            canvas.Translate(dx, dy); 
                            
                            if (PRINT_LOGS && isZeroSized) log.AppendLine($"   -> Glyph {glyphId} (Mark): Centered path with dx={dx:F2}, dy={dy:F2}.");
                            // -------------------------------------
                            
                            canvas.DrawPath(path, paint);
                            
                            canvas.Restore();
                        }

                        // ... (Pixel copying logic omitted for brevity)
                        using (var image = surface.Snapshot())
                        using (var pixmap = image.PeekPixels())
                        {
                            IntPtr pixelPtr = pixmap.GetPixels();
                            byte[] pixels = new byte[glyphWidth * glyphHeight];
                            Marshal.Copy(pixelPtr, pixels, 0, pixels.Length);
                            
                            for (int y = 0; y < glyphHeight; y++)
                            {
                                for (int x = 0; x < glyphWidth; x++)
                                {
                                    int srcIdx = y * glyphWidth + x;
                                    byte alpha = pixels[srcIdx];
                                    
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
                    if (PRINT_LOGS) log.AppendLine($"WARNING: SkiaSharp could not retrieve a path outline for glyph ID {glyphId}. Skipping.");
                    continue;
                }
            }
            
            // GLYPH ADDED TO ATLAS 
            glyphAtlas[glyphId] = new GlyphAtlasInfo
            {
                glyphId = glyphId,
                uvRect = new Rect(currentX / (float)atlasSize, currentY / (float)atlasSize, glyphWidth / (float)atlasSize, glyphHeight / (float)atlasSize),
                size = new Vector2(glyphWidth, glyphHeight),
                // Store original HarfBuzz bearing for X-spacing
                bearing = new Vector2(extents.XBearing / UnitsPerPixel, extents.YBearing / UnitsPerPixel) 
            };
            
            currentX += glyphWidth;
            rowHeight = Mathf.Max(rowHeight, glyphHeight);
        }
        
        if (PRINT_LOGS) log.AppendLine($"INFO: Rasterization complete. {glyphAtlas.Count} USEABLE glyphs are in the atlas.");
    }
    
    // --- MESH BUILDING (FINAL FIX: Y-Positioning based on Height, ignoring YBearing) ---
    Mesh BuildMesh(GlyphInfo[] infos, GlyphPosition[] positions, StringBuilder log)
    {
        Mesh mesh = new Mesh();
        List<Vector3> vertices = new List<Vector3>();
        List<Vector2> uvs = new List<Vector2>();
        List<int> triangles = new List<int>();
        
        float penX = 0f;
        float penY = 0f;
        float scale = 0.01f; 
        
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
            
            // 1. Calculate the initial pen position + HarfBuzz offsets 
            float startX = penX + (hbPos.XOffset / UnitsPerPixel * scale);
            float startY = penY + (hbPos.YOffset / UnitsPerPixel * scale); 

            // 2. Adjust for quad geometry, padding, and safety margin
            
            float xBearingScaled = atlasInfo.bearing.x * scale;
            float atlasPaddingOffset = padding * scale;
            float safetyMarginOffset = RASTER_SAFETY_MARGIN / 2f * scale;

            // Quad Dimensions
            float w = atlasInfo.size.x * scale;
            float h = atlasInfo.size.y * scale;

            // Final X Position: Start X - (HarfBuzz XBearing) - Padding - Margin
            float finalX = startX - xBearingScaled - atlasPaddingOffset - safetyMarginOffset;

            // FINAL Y POSITION FIX (Height-Based Alignment):
            // Since the glyph is centered in the atlas quad, we use the quad height (h) 
            // to correctly position the quad's top edge (V0) relative to the baseline (startY).
            // V0 Y = Baseline Y - (Distance from Baseline to Top of Quad)
            // This formulation ignores the unreliable Y-Bearing metric.
            float finalY = startY 
                           - h // Subtract the full height of the quad
                           + (atlasPaddingOffset) // Add back the padding below the baseline
                           + safetyMarginOffset; // Add back safety margin

            
            // Vertices 
            int idx = vertices.Count;

            vertices.Add(new Vector3(finalX, finalY, 0));             // Top-Left (V0)
            vertices.Add(new Vector3(finalX + w, finalY, 0));         // Top-Right (V1)
            vertices.Add(new Vector3(finalX + w, finalY - h, 0));     // Bottom-Right (V2)
            vertices.Add(new Vector3(finalX, finalY - h, 0));         // Bottom-Left (V3)
            
            // UV coordinates (Vertical Flip Fix)
            Rect uv = atlasInfo.uvRect;

            uvs.Add(new Vector2(uv.xMin, uv.yMin)); // V0 (Top-Left) <--> UV (Bottom-Left)
            uvs.Add(new Vector2(uv.xMax, uv.yMin)); // V1 (Top-Right) <--> UV (Bottom-Right)
            uvs.Add(new Vector2(uv.xMax, uv.yMax)); // V2 (Bottom-Right) <--> UV (Top-Right)
            uvs.Add(new Vector2(uv.xMin, uv.yMax)); // V3 (Bottom-Left) <--> UV (Top-Left)
            
            // Triangles
            triangles.Add(idx);
            triangles.Add(idx + 1);
            triangles.Add(idx + 2);
            triangles.Add(idx + 2);
            triangles.Add(idx + 3);
            triangles.Add(idx);
            
            // Advance pen
            penX += hbPos.XAdvance / UnitsPerPixel * scale;
            penY += hbPos.YAdvance / UnitsPerPixel * scale;
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