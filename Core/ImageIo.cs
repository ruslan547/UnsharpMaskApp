using System.Runtime.InteropServices;
using SkiaSharp;

namespace UnsharpMaskApp.Core;

/// <summary>Асинхронное чтение и запись файлов изображений.</summary>
public static class ImageIo
{
    private const int BufferSize = 1 << 16;

    /// <summary>Асинхронно читает файл (FileStream с FileOptions.Asynchronous + ReadAsync) и декодирует его.</summary>
    public static async Task<ImageBuffer> ReadAsync(string path, CancellationToken token)
    {
        byte[] encoded;
        await using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                         BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            encoded = new byte[fs.Length];
            int offset = 0;
            while (offset < encoded.Length)
            {
                int read = await fs.ReadAsync(encoded.AsMemory(offset), token).ConfigureAwait(false);
                if (read == 0) throw new EndOfStreamException("Файл прочитан не полностью");
                offset += read;
            }
        }

        // Декодирование - вычислительная операция, выполняется вне UI-потока.
        return await Task.Run(() => Decode(encoded), token).ConfigureAwait(false);
    }

    private static ImageBuffer Decode(byte[] encoded)
    {
        using var decoded = SKBitmap.Decode(encoded)
                            ?? throw new InvalidDataException("Неподдерживаемый формат изображения");
        var info = new SKImageInfo(decoded.Width, decoded.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        using var bgra = new SKBitmap(info);
        if (!decoded.CopyTo(bgra, SKColorType.Bgra8888))
            using (var canvas = new SKCanvas(bgra)) canvas.DrawBitmap(decoded, 0, 0);

        var image = new ImageBuffer(info.Width, info.Height);
        Marshal.Copy(bgra.GetPixels(), image.Pixels, 0, image.Pixels.Length);
        return image;
    }

    /// <summary>
    /// Кодирует изображение и асинхронно записывает его во временный файл (WriteAsync),
    /// который переименовывается в целевой только после успешной записи.
    /// При отмене временный файл удаляется - частичный результат не сохраняется.
    /// </summary>
    public static async Task WriteAsync(ImageBuffer image, string path, CancellationToken token)
    {
        byte[] encoded = await Task.Run(() => Encode(image, path), token).ConfigureAwait(false);
        string tmp = path + ".part";
        try
        {
            await using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None,
                             BufferSize, FileOptions.Asynchronous))
            {
                const int chunk = 1 << 18;
                for (int offset = 0; offset < encoded.Length; offset += chunk)
                {
                    token.ThrowIfCancellationRequested();
                    await fs.WriteAsync(encoded.AsMemory(offset, Math.Min(chunk, encoded.Length - offset)), token)
                        .ConfigureAwait(false);
                }
                await fs.FlushAsync(token).ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            if (File.Exists(tmp)) File.Delete(tmp);
            throw;
        }
    }

    private static byte[] Encode(ImageBuffer image, string path)
    {
        var info = new SKImageInfo(image.Width, image.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        var handle = GCHandle.Alloc(image.Pixels, GCHandleType.Pinned);
        try
        {
            using var skImage = SKImage.FromPixels(info, handle.AddrOfPinnedObject(), image.Stride);
            string ext = Path.GetExtension(path).ToLowerInvariant();
            var format = ext is ".jpg" or ".jpeg" ? SKEncodedImageFormat.Jpeg : SKEncodedImageFormat.Png;
            using var data = skImage.Encode(format, 95);
            return data.ToArray();
        }
        finally
        {
            handle.Free();
        }
    }
}
