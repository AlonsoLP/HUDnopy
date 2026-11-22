// -----------------------------------------------------------------------------
// Project:     HUDnopy — Elite Dangerous Telemetry HUD & Copilot
// File:        Services/QrCodeGenerator.cs
// Author:      AlonsoLP
// Copyright:   Copyright (c) 2026 AlonsoLP
// License:     GPL-3.0-only (see LICENSE file in the repository root)
// Description: Zero-dependency QR code encoder for quick mobile pairing with the Web HUD.
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Media;

namespace HUDnopy.Services;

/// <summary>
/// Zero-dependency QR code encoder for quick mobile pairing with the Web HUD.
/// </summary>
internal static class QrCodeGenerator
{
    private readonly record struct VersionSpec(int Version, int Size, int DataCodewords, int EcCodewords, int[] Alignments);

    private static readonly VersionSpec[] Specs =
    {
        new(1, 21, 19, 7, Array.Empty<int>()),
        new(2, 25, 34, 10, new[] { 6, 18 }),
        new(3, 29, 55, 15, new[] { 6, 22 }),
        new(4, 33, 80, 20, new[] { 6, 26 }),
        new(5, 37, 108, 26, new[] { 6, 30 })
    };

    public static DrawingImage? CreateQrImage(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        byte[] payload = Encoding.UTF8.GetBytes(text);
        VersionSpec? chosen = null;
        foreach (var spec in Specs)
        {
            if (payload.Length + 2 <= spec.DataCodewords)
            {
                chosen = spec;
                break;
            }
        }
        if (chosen is null)
            return null;

        bool[,] matrix = EncodeMatrix(payload, chosen.Value);
        int size = chosen.Value.Size;
        int quiet = 4; // ISO/IEC 18004 standard 4-module quiet zone for reliable scanning on dark HUD themes
        int total = size + quiet * 2;

        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(
            System.Windows.Media.Brushes.White,
            null,
            new RectangleGeometry(new Rect(0, 0, total, total))));

        var modules = new GeometryGroup();
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                if (matrix[y, x])
                    modules.Children.Add(new RectangleGeometry(new Rect(x + quiet, y + quiet, 1, 1)));
            }
        }
        modules.Freeze();
        group.Children.Add(new GeometryDrawing(System.Windows.Media.Brushes.Black, null, modules));
        group.Freeze();

        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }

    private static bool[,] EncodeMatrix(byte[] payload, VersionSpec spec)
    {
        var bits = new List<bool>(spec.DataCodewords * 8);
        AppendBits(bits, 0b0100, 4); // Byte mode
        AppendBits(bits, payload.Length, 8);
        foreach (byte b in payload)
            AppendBits(bits, b, 8);

        int maxBits = spec.DataCodewords * 8;
        int terminator = Math.Min(4, maxBits - bits.Count);
        AppendBits(bits, 0, terminator);
        while (bits.Count % 8 != 0)
            bits.Add(false);

        byte[] data = new byte[spec.DataCodewords];
        for (int i = 0; i < bits.Count / 8; i++)
        {
            byte v = 0;
            for (int b = 0; b < 8; b++)
                v = (byte)((v << 1) | (bits[i * 8 + b] ? 1 : 0));
            data[i] = v;
        }
        int padIdx = bits.Count / 8;
        bool alt = false;
        while (padIdx < data.Length)
        {
            data[padIdx++] = alt ? (byte)0x11 : (byte)0xEC;
            alt = !alt;
        }

        byte[] ec = ComputeReedSolomon(data, spec.EcCodewords);
        var allBits = new List<bool>((data.Length + ec.Length) * 8);
        foreach (byte b in data) AppendBits(allBits, b, 8);
        foreach (byte b in ec) AppendBits(allBits, b, 8);

        int n = spec.Size;
        var modules = new bool[n, n];
        var isFunction = new bool[n, n];

        PlaceFinder(modules, isFunction, 0, 0);
        PlaceFinder(modules, isFunction, n - 7, 0);
        PlaceFinder(modules, isFunction, 0, n - 7);

        for (int i = 8; i < n - 8; i++)
        {
            SetFunction(modules, isFunction, i, 6, i % 2 == 0);
            SetFunction(modules, isFunction, 6, i, i % 2 == 0);
        }

        if (spec.Alignments.Length > 0)
        {
            int c = spec.Alignments[^1];
            for (int dy = -2; dy <= 2; dy++)
            {
                for (int dx = -2; dx <= 2; dx++)
                {
                    int dist = Math.Max(Math.Abs(dx), Math.Abs(dy));
                    SetFunction(modules, isFunction, c + dx, c + dy, dist != 1);
                }
            }
        }

        // Reserve format info areas + dark module
        for (int i = 0; i <= 8; i++)
        {
            if (i != 6)
            {
                isFunction[8, i] = true;
                isFunction[i, 8] = true;
            }
        }
        for (int i = n - 8; i < n; i++)
        {
            isFunction[8, i] = true;
            isFunction[i, 8] = true;
        }
        SetFunction(modules, isFunction, 8, n - 8, true);

        // Place data bits in zigzag
        int bitPos = 0;
        for (int right = n - 1; right >= 1; right -= 2)
        {
            if (right == 6) right = 5;
            bool upward = ((right + 1) & 2) == 0;
            for (int vert = 0; vert < n; vert++)
            {
                int y = upward ? n - 1 - vert : vert;
                for (int j = 0; j < 2; j++)
                {
                    int x = right - j;
                    if (isFunction[y, x]) continue;
                    bool dark = bitPos < allBits.Count && allBits[bitPos++];
                    if ((x + y) % 2 == 0) dark = !dark; // Mask 0
                    modules[y, x] = dark;
                }
            }
        }

        // Format bits for Error Correction Level L (01) and Mask 0 (000) -> 01000 -> 0x77C4
        const int formatBits = 0x77C4;
        for (int i = 0; i <= 5; i++) modules[8, i] = ((formatBits >> i) & 1) != 0;
        modules[8, 7] = ((formatBits >> 6) & 1) != 0;
        modules[8, 8] = ((formatBits >> 7) & 1) != 0;
        modules[7, 8] = ((formatBits >> 8) & 1) != 0;
        for (int i = 9; i < 15; i++) modules[14 - i, 8] = ((formatBits >> i) & 1) != 0;
        for (int i = 0; i < 8; i++) modules[8, n - 1 - i] = ((formatBits >> i) & 1) != 0;
        for (int i = 8; i < 15; i++) modules[n - 15 + i, 8] = ((formatBits >> i) & 1) != 0;

        return modules;
    }

    private static void PlaceFinder(bool[,] modules, bool[,] isFunction, int x0, int y0)
    {
        int n = modules.GetLength(0);
        for (int dy = -1; dy <= 7; dy++)
        {
            for (int dx = -1; dx <= 7; dx++)
            {
                int x = x0 + dx, y = y0 + dy;
                if (x < 0 || x >= n || y < 0 || y >= n) continue;
                bool dark = dx is >= 0 and <= 6 && dy is >= 0 and <= 6 &&
                    (dx is 0 or 6 || dy is 0 or 6 || (dx is >= 2 and <= 4 && dy is >= 2 and <= 4));
                SetFunction(modules, isFunction, x, y, dark);
            }
        }
    }

    private static void SetFunction(bool[,] modules, bool[,] isFunction, int x, int y, bool dark)
    {
        modules[y, x] = dark;
        isFunction[y, x] = true;
    }

    private static void AppendBits(List<bool> bits, int value, int length)
    {
        for (int i = length - 1; i >= 0; i--)
            bits.Add(((value >> i) & 1) != 0);
    }

    private static byte[] ComputeReedSolomon(byte[] data, int degree)
    {
        byte[] gen = new byte[degree];
        gen[degree - 1] = 1;
        byte root = 1;
        for (int i = 0; i < degree; i++)
        {
            for (int j = 0; j < degree; j++)
            {
                gen[j] = GfMultiply(gen[j], root);
                if (j + 1 < degree)
                    gen[j] ^= gen[j + 1];
            }
            root = GfMultiply(root, 0x02);
        }

        byte[] result = new byte[degree];
        foreach (byte b in data)
        {
            byte factor = (byte)(b ^ result[0]);
            Array.Copy(result, 1, result, 0, degree - 1);
            result[degree - 1] = 0;
            for (int j = 0; j < degree; j++)
                result[j] ^= GfMultiply(gen[j], factor);
        }
        return result;
    }

    private static byte GfMultiply(byte x, byte y)
    {
        int z = 0;
        for (int i = 7; i >= 0; i--)
        {
            z = (z << 1) ^ ((z >> 7) * 0x11D);
            z ^= ((y >> i) & 1) * x;
        }
        return (byte)z;
    }
}
