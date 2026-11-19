namespace UnsharpMaskApp.Core;

/// <summary>Изображение в памяти: 4 байта на пиксель в порядке B, G, R, A (без премультипликации).</summary>
public sealed class ImageBuffer
{
    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; }
    public int Stride => Width * 4;

    public ImageBuffer(int width, int height, byte[]? pixels = null)
    {
        Width = width;
        Height = height;
        Pixels = pixels ?? new byte[width * height * 4];
        if (Pixels.Length != width * height * 4)
            throw new ArgumentException("Размер буфера не соответствует размеру изображения");
    }
}
