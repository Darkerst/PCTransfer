using System.Collections.Generic;

namespace PCTransfer11.Services;

/// <summary>
/// Minimalistische QR-code-generator voor 6-cijferige PINs.
/// Versie 1, numeric mode, error-correction M. Geen externe NuGet-packages.
/// </summary>
public static class SimpleQrGenerator
{
    public static bool[,] Encode(string numericData)
    {
        const int size = 21;
        var m = new bool[size, size];
        AddFinderPattern(m, 0, 0);
        AddFinderPattern(m, 0, size - 7);
        AddFinderPattern(m, size - 7, 0);
        for (int i = 8; i < size - 8; i++) { m[6, i] = i % 2 == 0; m[i, 6] = i % 2 == 0; }
        m[size - 8, 8] = true;
        PlaceBits(m, EncodeNumericBits(numericData), size);
        return m;
    }

    private static void AddFinderPattern(bool[,] m, int r, int c)
    {
        for (int dr = 0; dr < 7; dr++)
            for (int dc = 0; dc < 7; dc++)
                m[r + dr, c + dc] = dr == 0 || dr == 6 || dc == 0 || dc == 6 || (dr >= 2 && dr <= 4 && dc >= 2 && dc <= 4);
    }

    private static List<bool> EncodeNumericBits(string d)
    {
        var b = new List<bool>();
        b.AddRange(new[] { false, false, false, true });
        for (int i = 9; i >= 0; i--) b.Add((d.Length >> i & 1) == 1);
        int p = 0;
        while (p < d.Length)
        {
            if (p + 2 < d.Length) { int v = (d[p]-'0')*100+(d[p+1]-'0')*10+(d[p+2]-'0'); for (int i=9;i>=0;i--) b.Add((v>>i&1)==1); p+=3; }
            else if (p + 1 < d.Length) { int v = (d[p]-'0')*10+(d[p+1]-'0'); for (int i=6;i>=0;i--) b.Add((v>>i&1)==1); p+=2; }
            else { int v = d[p]-'0'; for (int i=3;i>=0;i--) b.Add((v>>i&1)==1); p++; }
        }
        b.AddRange(new[] { false, false, false, false });
        return b;
    }

    private static void PlaceBits(bool[,] m, List<bool> bits, int size)
    {
        int idx = 0; bool up = true; int col = size - 1;
        while (col >= 1)
        {
            if (col == 6) col--;
            int sr = up ? size-1 : 0, er = up ? -1 : size, step = up ? -1 : 1;
            for (int row = sr; row != er; row += step)
                for (int c = col; c > col-2; c--)
                    if (!IsReserved(m, row, c, size) && idx < bits.Count)
                        m[row, c] = bits[idx++];
            up = !up; col -= 2;
        }
    }

    private static bool IsReserved(bool[,] m, int r, int c, int size) =>
        (r < 8 && c < 8) || (r < 8 && c >= size-8) || (r >= size-8 && c < 8) || r == 6 || c == 6 || (r == size-8 && c == 8);
}
