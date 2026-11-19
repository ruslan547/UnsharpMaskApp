namespace UnsharpMaskApp.Core;

/// <summary>Параметры нерезкого маскирования (вариант 10).</summary>
/// <param name="Amount">Коэффициент усиления резкости k.</param>
/// <param name="Sigma">Среднеквадратичное отклонение гауссова размытия.</param>
/// <param name="Threshold">Порог: разница меньше порога не усиливается (подавление шума).</param>
public sealed record UnsharpParams(double Amount, double Sigma, int Threshold);

/// <summary>
/// Нерезкое маскирование: result = src + k * (src - Gauss(src)).
/// Размытие по Гауссу выполняется сепарабельно (по строкам, затем по столбцам).
/// </summary>
public static class UnsharpMask
{
    public static float[] BuildKernel(double sigma)
    {
        int radius = Math.Max(1, (int)Math.Ceiling(3 * sigma));
        var kernel = new float[2 * radius + 1];
        double sum = 0;
        for (int i = -radius; i <= radius; i++)
        {
            double v = Math.Exp(-(i * i) / (2 * sigma * sigma));
            kernel[i + radius] = (float)v;
            sum += v;
        }
        for (int i = 0; i < kernel.Length; i++) kernel[i] /= (float)sum;
        return kernel;
    }

    /// <summary>
    /// Обрабатывает горизонтальную полосу [y0, y1) и возвращает новый массив пикселей полосы.
    /// Исходное изображение только читается (после загрузки оно неизменяемо), запись идёт в локальный буфер.
    /// </summary>
    public static byte[] ProcessStrip(ImageBuffer src, int y0, int y1, UnsharpParams p, float[] kernel,
        CancellationToken token)
    {
        int w = src.Width, h = src.Height, stride = src.Stride;
        int r = kernel.Length / 2;
        byte[] s = src.Pixels;

        // 1) Горизонтальное размытие строк полосы с "полями" высотой r сверху и снизу.
        int hy0 = Math.Max(0, y0 - r), hy1 = Math.Min(h, y1 + r);
        int hRows = hy1 - hy0;
        var horiz = new float[hRows * w * 3];
        for (int y = hy0; y < hy1; y++)
        {
            token.ThrowIfCancellationRequested();
            int rowOff = y * stride;
            int dst = (y - hy0) * w * 3;
            for (int x = 0; x < w; x++)
            {
                float b = 0, g = 0, rr = 0;
                for (int k = -r; k <= r; k++)
                {
                    int xx = Math.Clamp(x + k, 0, w - 1);
                    float kv = kernel[k + r];
                    int i = rowOff + xx * 4;
                    b += kv * s[i];
                    g += kv * s[i + 1];
                    rr += kv * s[i + 2];
                }
                horiz[dst++] = b;
                horiz[dst++] = g;
                horiz[dst++] = rr;
            }
        }

        // 2) Вертикальное размытие + формула нерезкого маскирования.
        var result = new byte[(y1 - y0) * stride];
        float amount = (float)p.Amount;
        for (int y = y0; y < y1; y++)
        {
            token.ThrowIfCancellationRequested();
            for (int x = 0; x < w; x++)
            {
                int si = y * stride + x * 4;
                int di = (y - y0) * stride + x * 4;
                for (int c = 0; c < 3; c++)
                {
                    float blur = 0;
                    for (int k = -r; k <= r; k++)
                    {
                        int yy = Math.Clamp(y + k, 0, h - 1) - hy0;
                        blur += kernel[k + r] * horiz[(yy * w + x) * 3 + c];
                    }
                    float orig = s[si + c];
                    float diff = orig - blur;
                    float v = Math.Abs(diff) < p.Threshold ? orig : orig + amount * diff;
                    result[di + c] = (byte)Math.Clamp((int)MathF.Round(v), 0, 255);
                }
                result[di + 3] = s[si + 3]; // альфа-канал без изменений
            }
        }
        return result;
    }
}
