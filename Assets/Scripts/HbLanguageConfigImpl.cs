using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using HarfBuzzSharp;
using SkiaSharp;
using UnityEngine;
using File = System.IO.File;
using Font = HarfBuzzSharp.Font;

public partial class HbLanguageConfig
{
    private string langConfigPath;
    private const string CACHE_TEX_FILENAME = "font_atlas.bytes";
    private const string CACHE_META_FILENAME = "font_meta.bin";
    private const string DEBUG_PNG_FILENAME = "font_debugImage.png";

    //Font Data
    private byte[] fontData;
    private Face HbFontFace;
    private SKTypeface skTypeface;
    private SKFont skFont;

    //Atlas Data
    private int atlasDimensions;
    private int currentX;
    private int currentY;
    private int rowHeight;
    private int padding = 4;
    private const int RASTER_SAFETY_MARGIN = 2;
    private float fontSize = 100f;
    private Texture2D atlasTexture;

    //Exposed Lang Data
    [HideInInspector] public Material hbLangMaterial;
    public Script languageScript;
    public Language hbLanguage;
    [HideInInspector] public float unitsPerPixel = 64f;
    public Dictionary<uint, GlyphAtlasInfo> glyphAtlas = new();
    public Font hbFont;


    public void InitFont()
    {
        langConfigPath = Path.Combine(Application.persistentDataPath, "HbLangConfigs", $"lang_{languageCode}");
        Directory.CreateDirectory(langConfigPath);

        atlasDimensions = (int)atlasSize;
        fontSize = (float)fontRenderSize;
        unitsPerPixel = (float)fontRenderSize;
        if (!Script.TryParse(languageScriptName, out languageScript)) Debug.Log($"Script Type {languageScriptName} not found");

        if (!File.Exists(Path.Combine(Application.streamingAssetsPath, languageFontAssetPath)))
        {
            Debug.LogError(
                $"CRITICAL: Font file not found at: {Path.Combine(Application.streamingAssetsPath, languageFontAssetPath)}");
            return;
        }

        try
        {
            fontData = File.ReadAllBytes(Path.Combine(Application.streamingAssetsPath, languageFontAssetPath));
        }
        catch (Exception e)
        {
            Debug.LogError($"CRITICAL: Failed to load {languageScriptName}[{languageCode}] font data: {e.Message}");
        }

        var ptr = Marshal.AllocHGlobal(fontData.Length);
        Marshal.Copy(fontData, 0, ptr, fontData.Length);
        using (var blob = new Blob(ptr,
                   fontData.Length, MemoryMode.ReadOnly,
                   () => Marshal.FreeHGlobal(ptr)))
        {
            HbFontFace = new Face(blob, 0);
        }

        skTypeface = SKTypeface.FromData(SKData.CreateCopy(fontData));
        if (HbFontFace == null || skTypeface == null)
            Debug.LogError($"CRITICAL: Either HB or SkiaSharp {languageCode} font failed to load");

        skFont = new SKFont(skTypeface, fontSize);
        hbFont = new Font(HbFontFace);
        hbFont.SetScale((int)(fontSize * unitsPerPixel), (int)(fontSize * unitsPerPixel));

        LoadAtlas(this);

        hbLangMaterial = new Material(Shader.Find("UI/Default"));
        hbLangMaterial.mainTexture = atlasTexture;

        hbLanguage = new Language(languageCode);
    }

    private void LoadAtlas(HbLanguageConfig config)
    {
        var imagePath = Path.Combine(langConfigPath, CACHE_TEX_FILENAME);
        var metadataPath = Path.Combine(langConfigPath, CACHE_META_FILENAME);
        var atlasDebugImagePath = Path.Combine(langConfigPath, DEBUG_PNG_FILENAME);

        if (File.Exists(imagePath) &&
            File.Exists(metadataPath) &&
            Math.Abs((File.GetCreationTime(imagePath) -
                      File.GetCreationTime(metadataPath)).TotalSeconds) < 2)
        {
            try
            {
                var textureBytes = File.ReadAllBytes(imagePath);
                atlasTexture = new Texture2D(atlasDimensions, atlasDimensions, TextureFormat.Alpha8, false);
                atlasTexture.LoadRawTextureData(textureBytes);
                atlasTexture.Apply();

                using (var fs = new FileStream(metadataPath, FileMode.Open))
                using (var reader = new BinaryReader(fs))
                {
                    var count = reader.ReadInt32();
                    var sizeOfStruct = Marshal.SizeOf<GlyphMetadata>();
                    glyphAtlas.Clear();

                    for (var i = 0; i < count; i++)
                    {
                        var buffer = reader.ReadBytes(sizeOfStruct);
                        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
                        var metadata =
                            (GlyphMetadata)Marshal.PtrToStructure(handle.AddrOfPinnedObject(), typeof(GlyphMetadata));
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

                Debug.Log($"Atlas Loaded for language {languageCode}");
            }
            catch (Exception e)
            {
                Debug.LogError(
                    $"WARNING: Failed to load atlas from binary cache: {e.Message}, Deleting Corrupted Data Files for {languageCode}");
                if (File.Exists(imagePath)) File.Delete(imagePath);
                if (File.Exists(metadataPath)) File.Delete(metadataPath);
            }
        }
        else
        {
            Debug.Log($"No Atlas Data found for {languageCode}, Creating atlas for future use");

            var allGlyphIds = GetAllGlyphIds(skTypeface);
            atlasTexture = new Texture2D(atlasDimensions, atlasDimensions, TextureFormat.Alpha8, false);
            var clearColors = new Color32[atlasDimensions * atlasDimensions];
            for (var i = 0; i < clearColors.Length; i++) clearColors[i] = new Color32(0, 0, 0, 0);
            atlasTexture.SetPixels32(clearColors);

            RasterizeRequiredGlyphsToAtlas(hbFont, allGlyphIds, atlasTexture, this);

            atlasTexture.Apply();

            try
            {
                // 1. Save Texture (Raw Data)
                var textureBytes = atlasTexture.GetRawTextureData();
                File.WriteAllBytes(imagePath, textureBytes);

                var debugTex = new Texture2D(atlasDimensions, atlasDimensions, TextureFormat.RGBA32, false);
                var alphaPixels = atlasTexture.GetPixels();
                var debugPixels = new Color[alphaPixels.Length];
                for (var i = 0; i < alphaPixels.Length; i++)
                {
                    var a = alphaPixels[i].a;
                    debugPixels[i] = new Color(a, a, a, 1f);
                }

                debugTex.SetPixels(debugPixels);
                debugTex.Apply();

                var pngBytes = debugTex.EncodeToPNG();
                File.WriteAllBytes(atlasDebugImagePath, pngBytes);
                // 3. Save Metadata (Binary)
                using (var fs = new FileStream(metadataPath, FileMode.Create))
                using (var writer = new BinaryWriter(fs))
                {
                    writer.Write(glyphAtlas.Count);
                    var sizeOfStruct = Marshal.SizeOf<GlyphMetadata>();

                    foreach (var pair in glyphAtlas)
                    {
                        var metadata = new GlyphMetadata
                        {
                            glyphId = pair.Key,
                            uv_x = pair.Value.uvRect.x, uv_y = pair.Value.uvRect.y,
                            uv_w = pair.Value.uvRect.width, uv_h = pair.Value.uvRect.height,
                            size_x = pair.Value.size.x, size_y = pair.Value.size.y,
                            bearing_x = pair.Value.bearing.x, bearing_y = pair.Value.bearing.y
                        };

                        var buffer = new byte[sizeOfStruct];
                        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
                        Marshal.StructureToPtr(metadata, handle.AddrOfPinnedObject(), false);
                        handle.Free();
                        writer.Write(buffer);
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"ERROR: Failed to save atlas in binary format: {e.Message}");
            }
        }
    }

    private HashSet<uint> GetAllGlyphIds(SKTypeface typeface)
    {
        var allGlyphIds = new HashSet<uint>();
        var totalGlyphs = typeface.GlyphCount;
        for (uint id = 0; id < totalGlyphs; id++) allGlyphIds.Add(id);
        return allGlyphIds;
    }

    private void RasterizeRequiredGlyphsToAtlas(Font langHbFont, HashSet<uint> requiredGlyphIds, Texture2D atlasTexture,
    HbLanguageConfig config)
{
    
    int successCount = 0;
    int emptyPathCount = 0;
    int noExtentsCount = 0;

    foreach (var glyphId in requiredGlyphIds)
    {
        if (!langHbFont.TryGetGlyphExtents(glyphId, out var extents))
        {
            noExtentsCount++;
            continue;
        }

        // --- Dimension Calculation with Safety Margin ---
        var baseWidth = (int)Math.Ceiling((extents.XBearing + extents.Width) / unitsPerPixel);
        var baseHeight = (int)Math.Ceiling(Math.Abs(extents.Height) / unitsPerPixel);
        var glyphWidth = baseWidth + padding * 2 + RASTER_SAFETY_MARGIN;
        var glyphHeight = baseHeight + padding * 2 + RASTER_SAFETY_MARGIN;

        // DOT FIX CHECK
        var minRenderSize = 2;
        var isZeroSized = glyphWidth <= padding * 2 + RASTER_SAFETY_MARGIN ||
                          glyphHeight <= padding * 2 + RASTER_SAFETY_MARGIN;

        if (isZeroSized)
        {
            glyphWidth = Mathf.Max(glyphWidth, minRenderSize + padding * 2 + RASTER_SAFETY_MARGIN);
            glyphHeight = Mathf.Max(glyphHeight, minRenderSize + padding * 2 + RASTER_SAFETY_MARGIN);
        }

        // Atlas Packing Logic
        if (currentX + glyphWidth > atlasDimensions)
        {
            currentX = 0;
            currentY += rowHeight;
            rowHeight = 0;
        }

        if (currentY + glyphHeight > atlasDimensions)
        {
            Debug.LogError($"CRITICAL: Atlas full at glyph ID {glyphId}. Increase atlasSize!");
            break;
        }

        var ushortGlyphId = (ushort)glyphId;

        // Get path from Skia for this glyph
        using (var path = skFont.GetGlyphPath(ushortGlyphId))
        {
            if (path != null && !path.IsEmpty)
            {

                // Create an alpha-only surface for the glyph
                using (var surface = SKSurface.Create(new SKImageInfo(glyphWidth, glyphHeight, SKColorType.Alpha8)))
                {
                    if (surface == null)
                    {
                        Debug.LogError($"CRITICAL: Failed to create SKSurface for glyph {glyphId}!");
                        continue;
                    }

                    var canvas = surface.Canvas;
                    canvas.Clear(SKColors.Transparent);

                    using (var paint = new SKPaint
                    {
                        IsAntialias = true,
                        Color = SKColors.White,
                        Style = SKPaintStyle.Fill
                    })
                    {
                        canvas.Save();

                        var pxOriginX = padding + (-extents.XBearing / unitsPerPixel);
                        var pxOriginY = padding + (extents.YBearing / unitsPerPixel);

                        canvas.Translate(pxOriginX, pxOriginY);
                        canvas.DrawPath(path, paint);
                        canvas.Restore();
                    }

                    // Copy pixels out of the SKSurface into atlasTexture
                    using (var image = surface.Snapshot())
                    using (var pixmap = image.PeekPixels())
                    {
                        if (pixmap == null)
                        {
                            Debug.LogError($"CRITICAL: Failed to get pixmap for glyph {glyphId}!");
                            continue;
                        }

                        var pixelPtr = pixmap.GetPixels();
                        var rowBytes = pixmap.RowBytes;
                        var rowBuffer = new byte[rowBytes];

                        // Check if any pixel is non-zero
                        int nonZeroPixels = 0;
                        
                        for (var y = 0; y < glyphHeight; y++)
                        {
                            Marshal.Copy(pixelPtr + y * rowBytes, rowBuffer, 0, rowBytes);
                            for (var x = 0; x < glyphWidth; x++)
                            {
                                var alpha = rowBuffer[x];
                                if (alpha > 0) nonZeroPixels++;
                                
                                var atlasX = currentX + x;
                                var atlasY = currentY + y;
                                atlasTexture.SetPixel(atlasX, atlasY, new Color(1, 1, 1, alpha / 255f));
                            }
                        }
                    }
                }
                
                successCount++;
            }
            else
            {
                emptyPathCount++;
                if (emptyPathCount <= 5)
                {
                    Debug.LogWarning($"WARNING: SkiaSharp returned empty path for glyph ID {glyphId}. Will add empty entry to atlas.");
                }
            }
        }

        // GLYPH ADDED TO ATLAS
        glyphAtlas[glyphId] = new GlyphAtlasInfo
        {
            glyphId = glyphId,
            uvRect = new Rect(currentX / (float)atlasDimensions, currentY / (float)atlasDimensions,
                glyphWidth / (float)atlasDimensions, glyphHeight / (float)atlasDimensions),
            size = new Vector2(glyphWidth, glyphHeight),
            bearing = new Vector2(extents.XBearing / unitsPerPixel, extents.YBearing / unitsPerPixel)
        };

        currentX += glyphWidth;
        rowHeight = Mathf.Max(rowHeight, glyphHeight);
    }

    Debug.Log($"=== Rasterization Complete ===");
    Debug.Log($"Success: {successCount}, Empty paths: {emptyPathCount}, No extents: {noExtentsCount}");
    Debug.Log($"Total in atlas: {glyphAtlas.Count}");
}

    public void DestroyResources()
    {
        HbFontFace?.Dispose();
        skTypeface?.Dispose();
        skFont?.Dispose();
        hbFont?.Dispose();
        hbLanguage?.Dispose();
    }
}