using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

// 小猫锁图标生成器：绘制「猫 + 锁」图形，输出预览图与多尺寸 .ico
// 用法：
//   MakeIcon.exe preview <输出目录>
//   MakeIcon.exe ico <variant: catkey|catbox|catpad> <RRGGBB> <输出.ico>
static class MakeIcon
{
    // ---------- 图形 ----------

    static GraphicsPath RoundRect(RectangleF r, float rad)
    {
        GraphicsPath p = new GraphicsPath();
        float d = rad * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    // 猫头剪影：圆脸 + 两只三角耳（耳根落在圆周上，和脸连成一体）
    static PointF[][] HeadEars()
    {
        return new PointF[][] {
            new PointF[] { new PointF(58.5f, 106.5f), new PointF(110.9f, 69.8f), new PointF(58f, 18f) },
            new PointF[] { new PointF(197.5f, 106.5f), new PointF(145.1f, 69.8f), new PointF(198f, 18f) }
        };
    }

    // 锁体剪影：圆角方块 + 两只猫耳（耳根压进锁体上沿，中间留 V 形缺口）
    static PointF[][] BodyEars()
    {
        return new PointF[][] {
            new PointF[] { new PointF(52, 126), new PointF(120, 120), new PointF(26, 38) },
            new PointF[] { new PointF(204, 126), new PointF(136, 120), new PointF(230, 38) }
        };
    }

    // 钥匙孔：上圆下扩口
    static GraphicsPath KeyholePath(float cx, float cy, float r)
    {
        GraphicsPath p = new GraphicsPath(FillMode.Winding);
        p.AddEllipse(cx - r, cy - r, r * 2, r * 2);
        float top = cy + r * 0.5f;
        float bottom = cy + r * 2.3f;
        float hwTop = r * 0.45f;
        float hwBottom = r * 0.85f;
        p.AddPolygon(new PointF[] {
            new PointF(cx - hwTop, top), new PointF(cx + hwTop, top),
            new PointF(cx + hwBottom, bottom), new PointF(cx - hwBottom, bottom)
        });
        return p;
    }

    // 经典挂锁的锁梁（画在锁体上方）
    static void DrawShackle(Graphics g, Color c)
    {
        using (GraphicsPath sp = new GraphicsPath())
        {
            sp.AddArc(92, 50, 72, 120, 180, 180);
            using (Pen pen = new Pen(c, 20f))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                g.DrawPath(pen, sp);
            }
        }
    }

    // 在 256x256 设计坐标系里画图形（调用方先缩放）
    static void DrawGlyph(Graphics g, string variant, Color c)
    {
        if (variant == "catkey")
        {
            using (SolidBrush b = new SolidBrush(c))
            {
                g.FillEllipse(b, 46, 68, 164, 164);
                foreach (PointF[] ear in HeadEars()) g.FillPolygon(b, ear);
            }
            using (GraphicsPath kh = KeyholePath(128, 146, 25))
            using (SolidBrush w = new SolidBrush(Color.White))
                g.FillPath(w, kh);
        }
        else if (variant == "catbox")
        {
            using (GraphicsPath body = RoundRect(new RectangleF(50, 102, 156, 128), 30))
            using (SolidBrush b = new SolidBrush(c))
            {
                g.FillPath(b, body);
                foreach (PointF[] ear in BodyEars()) g.FillPolygon(b, ear);
            }
            using (GraphicsPath kh = KeyholePath(128, 152, 25))
            using (SolidBrush w = new SolidBrush(Color.White))
                g.FillPath(w, kh);
        }
        else if (variant == "catpad")
        {
            DrawShackle(g, c);
            using (GraphicsPath body = RoundRect(new RectangleF(50, 112, 156, 118), 28))
            using (SolidBrush b = new SolidBrush(c))
            {
                g.FillPath(b, body);
                foreach (PointF[] ear in BodyEars()) g.FillPolygon(b, ear);
            }
            using (GraphicsPath kh = KeyholePath(128, 158, 24))
            using (SolidBrush w = new SolidBrush(Color.White))
                g.FillPath(w, kh);
        }
        else throw new ArgumentException("unknown variant: " + variant);
    }

    // 位图（透明背景）
    static Bitmap Render(string variant, int size, Color c)
    {
        Bitmap bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);
            g.ScaleTransform(size / 256f, size / 256f);
            DrawGlyph(g, variant, c);
            g.ResetTransform();
        }
        return bmp;
    }

    // ---------- 预览图 ----------

    static Bitmap ContactSheet(string variant, Color c)
    {
        int W = 720, H = 680;
        Bitmap sheet = new Bitmap(W, H, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(sheet))
        {
            g.Clear(Color.White);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using (Font title = new Font("Microsoft YaHei", 15f, FontStyle.Bold))
            using (Font lab = new Font("Microsoft YaHei", 9f))
            using (Brush ink = new SolidBrush(Color.FromArgb(40, 40, 40)))
            using (Brush lightInk = new SolidBrush(Color.FromArgb(230, 230, 230)))
            {
                g.DrawString(variant + "   #" + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2"),
                    title, ink, 20, 12);

                using (Bitmap big = Render(variant, 256, c))
                    g.DrawImageUnscaled(big, 20, 46);

                g.DrawString("256 x 256", lab, ink, 24, 306);

                // 浅色任务栏
                using (Brush bg = new SolidBrush(Color.FromArgb(243, 243, 243)))
                    g.FillRectangle(bg, 20, 340, 680, 130);
                g.DrawString("浅色背景", lab, ink, 28, 346);
                DrawRow(g, variant, c, 80, 372, 3, ink);

                // 深色任务栏
                using (Brush bg = new SolidBrush(Color.FromArgb(32, 32, 32)))
                    g.FillRectangle(bg, 20, 486, 680, 130);
                g.DrawString("深色背景", lab, lightInk, 28, 492);
                DrawRow(g, variant, c, 80, 518, 3, lightInk);
            }
        }
        return sheet;
    }

    static void DrawRow(Graphics g, string variant, Color c, int x, int y, int zoom, Brush ink)
    {
        using (Font lab = new Font("Microsoft YaHei", 8f))
        {
            int[] sizes = new int[] { 16, 24, 32, 48 };
            foreach (int s in sizes)
            {
                using (Bitmap b = Render(variant, s, c))
                {
                    int z = zoom;
                    int draw = s * z;
                    g.InterpolationMode = InterpolationMode.NearestNeighbor;
                    g.PixelOffsetMode = PixelOffsetMode.Half;
                    g.DrawImage(b, new Rectangle(x, y, draw, draw));
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.DrawString(s + "px", lab, ink, x, y + draw + 4);
                }
                x += s * zoom + 34;
            }
        }
    }

    static void Preview(string outDir)
    {
        Directory.CreateDirectory(outDir);
        string[] variants = new string[] { "catkey", "catbox", "catpad" };
        Color[] colors = new Color[] {
            Color.FromArgb(242, 160, 61),  // 橙
            Color.FromArgb(235, 90, 90)    // 与提示卡强调色一致的红
        };
        foreach (string v in variants)
        {
            foreach (Color c in colors)
            {
                string tag = v + "_" + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2");
                using (Bitmap sheet = ContactSheet(v, c))
                    sheet.Save(Path.Combine(outDir, tag + ".png"), ImageFormat.Png);
                using (Bitmap px = Render(v, 256, c))
                    px.Save(Path.Combine(outDir, tag + "_256.png"), ImageFormat.Png);
            }
        }
    }

    // ---------- .ico 写入 ----------

    static byte[] PngBytes(Bitmap b)
    {
        using (MemoryStream ms = new MemoryStream())
        {
            b.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }
    }

    static byte[] BmpIconBytes(Bitmap b)
    {
        int w = b.Width, h = b.Height;
        int xorSize = w * h * 4;
        int maskRow = ((w + 31) / 32) * 4;
        int maskSize = maskRow * h;
        using (MemoryStream ms = new MemoryStream())
        using (BinaryWriter bw = new BinaryWriter(ms))
        {
            bw.Write(40);                 // biSize
            bw.Write(w);                  // biWidth
            bw.Write(h * 2);              // biHeight（XOR + AND）
            bw.Write((ushort)1);          // biPlanes
            bw.Write((ushort)32);         // biBitCount
            bw.Write(0);                  // biCompression = BI_RGB
            bw.Write(xorSize + maskSize); // biSizeImage
            bw.Write(0); bw.Write(0);     // 分辨率
            bw.Write(0); bw.Write(0);     // 调色板

            BitmapData d = b.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                byte[] row = new byte[w * 4];
                for (int y = h - 1; y >= 0; y--)
                {
                    IntPtr p = new IntPtr(d.Scan0.ToInt64() + (long)y * d.Stride);
                    Marshal.Copy(p, row, 0, row.Length);
                    // BGRA 直存，alpha 原样保留
                    bw.Write(row);
                }
            }
            finally { b.UnlockBits(d); }

            bw.Write(new byte[maskSize]); // AND 掩码全 0，靠 alpha 透明
            bw.Flush();
            return ms.ToArray();
        }
    }

    static void WriteIco(string variant, Color c, string outPath)
    {
        int[] sizes = new int[] { 16, 20, 24, 32, 40, 48, 64, 128, 256 };
        List<byte[]> images = new List<byte[]>();
        foreach (int s in sizes)
        {
            using (Bitmap b = Render(variant, s, c))
                images.Add(s >= 128 ? PngBytes(b) : BmpIconBytes(b));
        }

        using (FileStream fs = File.Create(outPath))
        using (BinaryWriter w = new BinaryWriter(fs))
        {
            w.Write((ushort)0);                 // reserved
            w.Write((ushort)1);                 // type = icon
            w.Write((ushort)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                int s = sizes[i];
                w.Write((byte)(s >= 256 ? 0 : s));
                w.Write((byte)(s >= 256 ? 0 : s));
                w.Write((byte)0); w.Write((byte)0);
                w.Write((ushort)1);             // planes
                w.Write((ushort)32);            // bit count
                w.Write((uint)images[i].Length);
                w.Write((uint)offset);
                offset += images[i].Length;
            }
            foreach (byte[] img in images) w.Write(img);
        }
    }

    static Color ParseColor(string hex)
    {
        int v = Convert.ToInt32(hex.TrimStart('#'), 16);
        return Color.FromArgb((v >> 16) & 255, (v >> 8) & 255, v & 255);
    }

    static int Main(string[] args)
    {
        try
        {
            if (args.Length >= 2 && args[0] == "preview")
            {
                Preview(args[1]);
                return 0;
            }
            if (args.Length >= 4 && args[0] == "ico")
            {
                WriteIco(args[1], ParseColor(args[2]), args[3]);
                return 0;
            }
            Console.WriteLine("usage: MakeIcon preview <outDir> | ico <variant> <RRGGBB> <out.ico>");
            return 2;
        }
        catch (Exception ex)
        {
            Console.WriteLine("ERROR: " + ex);
            return 1;
        }
    }
}
